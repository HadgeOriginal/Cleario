using Cleario.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class HistoryService
    {
        private const string FileName = "history.json";
        private const string SeriesAggregateVideoId = "__series__";
        private static readonly SemaphoreSlim _sync = new(1, 1);
        private static List<HistoryEntry>? _cache;

        public static event EventHandler? HistoryChanged;

        public sealed class HistoryEntry
        {
            public string Id { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string VideoId { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
            public string EpisodeTitle { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string PosterUrl { get; set; } = string.Empty;
            public string FallbackPosterUrl { get; set; } = string.Empty;
            public string Year { get; set; } = string.Empty;
            public string ImdbRating { get; set; } = string.Empty;
            public string SourceBaseUrl { get; set; } = string.Empty;
            public string StreamKey { get; set; } = string.Empty;
            public string StreamDisplayName { get; set; } = string.Empty;
            public string AddonName { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string DirectUrl { get; set; } = string.Empty;
            public string EmbeddedPageUrl { get; set; } = string.Empty;
            public string MagnetUrl { get; set; } = string.Empty;
            public string ContentLogoUrl { get; set; } = string.Empty;
            public string StreamPosterUrl { get; set; } = string.Empty;
            public string StreamFallbackPosterUrl { get; set; } = string.Empty;
            public long PositionMs { get; set; }
            public long DurationMs { get; set; }
            public bool IsWatched { get; set; }
            public bool IsDismissed { get; set; }
            public int WatchCount { get; set; }
            public DateTime LastPlayedUtc { get; set; } = DateTime.UtcNow;
            public DateTime? CompletedUtc { get; set; }
        }

        public sealed class ContinueWatchingItem
        {
            public MetaItem Item { get; set; } = new();
            public string VideoId { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
            public string EpisodeTitle { get; set; } = string.Empty;
            public long PositionMs { get; set; }
            public long DurationMs { get; set; }
            public DateTime LastPlayedUtc { get; set; }
            public CatalogService.StreamOption Stream { get; set; } = new();

            public double ProgressRatio => DurationMs > 0 ? Math.Clamp((double)PositionMs / DurationMs, 0, 1) : 0;
            public string ResumeLabel => DurationMs > 0 ? $"{FormatTime(PositionMs)} / {FormatTime(DurationMs)}" : FormatTime(PositionMs);
            public string DismissKey => $"{Item.Type}::{Item.Id}";
        }

        public sealed class ItemWatchSummary
        {
            public bool IsWatched { get; set; }
            public bool HasProgress { get; set; }
            public long PositionMs { get; set; }
            public long DurationMs { get; set; }
            public DateTime? LastPlayedUtc { get; set; }
            public int WatchCount { get; set; }
            public string StreamKey { get; set; } = string.Empty;
        }

        public static async Task<IReadOnlyList<HistoryEntry>> GetEntriesAsync(bool forceReload = false)
        {
            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(forceReload);
                return items.OrderByDescending(x => x.LastPlayedUtc).Select(Clone).ToList();
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task<List<ContinueWatchingItem>> GetContinueWatchingAsync(bool forceReload = false)
        {
            await _sync.WaitAsync();
            try
            {
                var entries = await LoadEntriesCoreAsync(forceReload);
                var latestByContent = entries
                    .Where(x => !x.IsDismissed && !x.IsWatched && x.PositionMs > 0)
                    .GroupBy(x => BuildContentKey(x.Type, x.Id))
                    .Select(g => g.OrderByDescending(x => x.LastPlayedUtc).First())
                    .OrderByDescending(x => x.LastPlayedUtc)
                    .ToList();

                return latestByContent.Select(ToContinueWatchingItem).ToList();
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task<ItemWatchSummary> GetItemSummaryAsync(string? type, string? id)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                return new ItemWatchSummary();

            await _sync.WaitAsync();
            try
            {
                var entries = await LoadEntriesCoreAsync(false);
                var relevant = entries.Where(x => IsSameContent(x, type, id)).ToList();
                if (relevant.Count == 0)
                    return new ItemWatchSummary();

                var last = relevant.OrderByDescending(x => x.LastPlayedUtc).First();
                var isSeries = string.Equals(type, "series", StringComparison.OrdinalIgnoreCase);
                return new ItemWatchSummary
                {
                    IsWatched = isSeries
                        ? relevant.Any(x => IsWatchedRecord(x) && IsSeriesAggregate(x))
                        : relevant.Any(IsWatchedRecord),
                    HasProgress = relevant.Any(x => !x.IsWatched && x.PositionMs > 0),
                    PositionMs = last.PositionMs,
                    DurationMs = last.DurationMs,
                    LastPlayedUtc = last.LastPlayedUtc,
                    WatchCount = relevant.Sum(x => x.WatchCount),
                    StreamKey = last.StreamKey
                };
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task<HistoryEntry?> GetLatestEntryForItemAsync(string? type, string? id)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                return null;

            await _sync.WaitAsync();
            try
            {
                var entries = await LoadEntriesCoreAsync(false);
                return entries.Where(x => IsSameContent(x, type, id))
                    .OrderByDescending(x => x.LastPlayedUtc)
                    .Select(Clone)
                    .FirstOrDefault();
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task<HistoryEntry?> GetEntryForVideoAsync(string? type, string? id, string? videoId)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(videoId))
                return null;

            await _sync.WaitAsync();
            try
            {
                var entries = await LoadEntriesCoreAsync(false);
                return entries.FirstOrDefault(x => IsSameContent(x, type, id) && string.Equals(NormalizeVideoId(x.VideoId, type, id), NormalizeVideoId(videoId, type, id), StringComparison.OrdinalIgnoreCase)) is HistoryEntry match
                    ? Clone(match)
                    : null;
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task<HashSet<string>> GetWatchedVideoIdsAsync(string? type, string? id)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await _sync.WaitAsync();
            try
            {
                var entries = await LoadEntriesCoreAsync(false);
                var watchedAll = entries.Any(x => IsSameContent(x, type, id) && IsWatchedRecord(x) && IsSeriesAggregate(x));
                if (watchedAll)
                    return new HashSet<string>(new[] { SeriesAggregateVideoId }, StringComparer.OrdinalIgnoreCase);

                return entries.Where(x => IsSameContent(x, type, id) && IsWatchedRecord(x) && !IsSeriesAggregate(x))
                    .Select(x => NormalizeVideoId(x.VideoId, type, id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task<bool> IsItemWatchedAsync(MetaItem? item)
        {
            if (item == null)
                return false;

            var summary = await GetItemSummaryAsync(item.Type, item.Id);
            return summary.IsWatched;
        }

        public static async Task SaveProgressAsync(MetaItem? item, CatalogService.StreamOption? stream, long positionMs, long durationMs)
        {
            if (item == null || stream == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Type))
                return;

            var normalizedVideoId = NormalizeVideoId(stream.VideoId, item.Type, item.Id);
            if (string.IsNullOrWhiteSpace(normalizedVideoId))
                normalizedVideoId = NormalizeVideoId(item.Id, item.Type, item.Id);

            var markWatched = ShouldMarkWatched(positionMs, durationMs);
            var hasMeaningfulProgress = positionMs >= 5_000;
            bool changed = false;

            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(false);
                var entry = items.FirstOrDefault(x => IsSameContent(x, item.Type, item.Id) && string.Equals(NormalizeVideoId(x.VideoId, item.Type, item.Id), normalizedVideoId, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new HistoryEntry
                    {
                        Id = item.Id,
                        Type = item.Type,
                        VideoId = normalizedVideoId,
                        Name = item.Name,
                        PosterUrl = PreferPoster(item),
                        FallbackPosterUrl = PreferFallbackPoster(item),
                        Year = item.Year,
                        ImdbRating = item.ImdbRating,
                        SourceBaseUrl = item.SourceBaseUrl
                    };
                    items.Add(entry);
                }

                entry.Name = !string.IsNullOrWhiteSpace(stream.ContentName) ? stream.ContentName : (!string.IsNullOrWhiteSpace(item.Name) ? item.Name : entry.Name);
                entry.PosterUrl = PreferPoster(item, !string.IsNullOrWhiteSpace(stream.PosterUrl) ? stream.PosterUrl : entry.PosterUrl);
                entry.FallbackPosterUrl = PreferFallbackPoster(item, !string.IsNullOrWhiteSpace(stream.FallbackPosterUrl) ? stream.FallbackPosterUrl : entry.FallbackPosterUrl);
                entry.Year = !string.IsNullOrWhiteSpace(stream.Year) ? stream.Year : (!string.IsNullOrWhiteSpace(item.Year) ? item.Year : entry.Year);
                entry.ImdbRating = !string.IsNullOrWhiteSpace(stream.ImdbRating) ? stream.ImdbRating : (!string.IsNullOrWhiteSpace(item.ImdbRating) ? item.ImdbRating : entry.ImdbRating);
                entry.SourceBaseUrl = !string.IsNullOrWhiteSpace(item.SourceBaseUrl) ? item.SourceBaseUrl : entry.SourceBaseUrl;
                entry.SeasonNumber = stream.SeasonNumber;
                entry.EpisodeNumber = stream.EpisodeNumber;
                entry.EpisodeTitle = stream.EpisodeTitle ?? string.Empty;
                entry.StreamKey = CatalogService.BuildStreamIdentity(stream);
                entry.StreamDisplayName = stream.DisplayName ?? string.Empty;
                entry.AddonName = stream.AddonName ?? string.Empty;
                entry.Description = stream.Description ?? string.Empty;
                entry.DirectUrl = stream.DirectUrl ?? string.Empty;
                entry.EmbeddedPageUrl = stream.EmbeddedPageUrl ?? string.Empty;
                entry.MagnetUrl = stream.MagnetUrl ?? string.Empty;
                entry.ContentLogoUrl = stream.ContentLogoUrl ?? string.Empty;
                entry.StreamPosterUrl = stream.PosterUrl ?? string.Empty;
                entry.StreamFallbackPosterUrl = stream.FallbackPosterUrl ?? string.Empty;
                entry.DurationMs = durationMs > 0 ? durationMs : entry.DurationMs;
                entry.LastPlayedUtc = DateTime.UtcNow;
                entry.IsDismissed = false;

                if (markWatched)
                {
                    if (!entry.IsWatched)
                        entry.WatchCount = Math.Max(1, entry.WatchCount + 1);

                    entry.IsWatched = true;
                    entry.CompletedUtc = DateTime.UtcNow;
                    entry.PositionMs = 0;
                }
                else if (hasMeaningfulProgress)
                {
                    entry.PositionMs = positionMs;
                    entry.IsWatched = false;
                }

                changed = true;
                await SaveEntriesCoreAsync(items);
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                HistoryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task MarkItemWatchedAsync(MetaItem item, bool watched)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Type))
                return;

            bool changed = false;
            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(false);
                if (string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase))
                {
                    if (watched)
                        changed = UpdateSeriesAggregate(items, item, true);
                    else
                        changed = items.RemoveAll(x => IsSameContent(x, item.Type, item.Id)) > 0;
                }
                else
                {
                    var videoId = NormalizeVideoId(item.Id, item.Type, item.Id);
                    var entry = items.FirstOrDefault(x => IsSameContent(x, item.Type, item.Id) && string.Equals(NormalizeVideoId(x.VideoId, item.Type, item.Id), videoId, StringComparison.OrdinalIgnoreCase));
                    if (entry == null && watched)
                    {
                        entry = CreateBasicEntry(item, videoId);
                        items.Add(entry);
                    }

                    if (entry != null)
                    {
                        entry.IsWatched = watched;
                        entry.PositionMs = 0;
                        entry.DurationMs = Math.Max(entry.DurationMs, 0);
                        entry.LastPlayedUtc = DateTime.UtcNow;
                        entry.IsDismissed = false;
                        if (watched)
                        {
                            entry.CompletedUtc = DateTime.UtcNow;
                            entry.WatchCount = Math.Max(1, entry.WatchCount + 1);
                        }
                        else
                        {
                            entry.CompletedUtc = null;
                        }
                        changed = true;
                    }
                }

                if (changed)
                    await SaveEntriesCoreAsync(items);
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                HistoryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static Task MarkSeriesEpisodesWatchedAsync(MetaItem item, IEnumerable<CatalogService.SeriesEpisodeOption> episodes, bool watched)
            => SetSeriesEpisodesWatchedAsync(item, episodes, watched, markWholeSeries: true);

        public static async Task SetSeriesEpisodesWatchedAsync(MetaItem item, IEnumerable<CatalogService.SeriesEpisodeOption> episodes, bool watched, bool markWholeSeries = false)
        {
            if (item == null)
                return;

            if (!string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase))
            {
                await MarkItemWatchedAsync(item, watched);
                return;
            }

            bool changed = false;
            var targetEpisodes = (episodes ?? Enumerable.Empty<CatalogService.SeriesEpisodeOption>())
                .Where(x => !string.IsNullOrWhiteSpace(x.VideoId))
                .ToList();

            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(false);
                items.RemoveAll(x => IsSameContent(x, item.Type, item.Id) && IsSeriesAggregate(x));

                if (markWholeSeries && !watched)
                {
                    changed = items.RemoveAll(x => IsSameContent(x, item.Type, item.Id)) > 0;
                }
                else if (!watched)
                {
                    var targetIds = targetEpisodes.Select(x => NormalizeVideoId(x.VideoId, item.Type, item.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    changed = items.RemoveAll(x => IsSameContent(x, item.Type, item.Id) && targetIds.Contains(NormalizeVideoId(x.VideoId, item.Type, item.Id))) > 0;
                }
                else
                {
                    foreach (var episode in targetEpisodes)
                    {
                        var videoId = NormalizeVideoId(episode.VideoId, item.Type, item.Id);
                        var entry = items.FirstOrDefault(x => IsSameContent(x, item.Type, item.Id) && string.Equals(NormalizeVideoId(x.VideoId, item.Type, item.Id), videoId, StringComparison.OrdinalIgnoreCase));
                        if (entry == null)
                        {
                            entry = CreateBasicEntry(item, videoId);
                            items.Add(entry);
                        }

                        entry.SeasonNumber = episode.Season;
                        entry.EpisodeNumber = episode.Episode;
                        entry.EpisodeTitle = episode.Title ?? string.Empty;
                        entry.IsWatched = true;
                        entry.PositionMs = 0;
                        entry.LastPlayedUtc = DateTime.UtcNow;
                        entry.CompletedUtc = DateTime.UtcNow;
                        entry.IsDismissed = false;
                        entry.WatchCount = Math.Max(1, entry.WatchCount + 1);
                        changed = true;
                    }

                    if (markWholeSeries)
                        changed |= UpdateSeriesAggregate(items, item, true);
                }

                if (changed)
                    await SaveEntriesCoreAsync(items);
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                HistoryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task DismissContinueWatchingAsync(string? type, string? id)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                return;

            bool changed = false;
            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(false);
                foreach (var entry in items.Where(x => IsSameContent(x, type, id) && !x.IsWatched))
                {
                    entry.IsDismissed = true;
                    ClearResumeStreamData(entry);
                    changed = true;
                }

                if (changed)
                    await SaveEntriesCoreAsync(items);
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                HistoryChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void ClearResumeStreamData(HistoryEntry entry)
        {
            entry.StreamKey = string.Empty;
            entry.StreamDisplayName = string.Empty;
            entry.AddonName = string.Empty;
            entry.Description = string.Empty;
            entry.DirectUrl = string.Empty;
            entry.EmbeddedPageUrl = string.Empty;
            entry.MagnetUrl = string.Empty;
            entry.ContentLogoUrl = string.Empty;
            entry.StreamPosterUrl = string.Empty;
            entry.StreamFallbackPosterUrl = string.Empty;
            entry.PositionMs = 0;
            entry.DurationMs = 0;
        }

        public static async Task<int> ImportContinueWatchingEntriesAsync(IEnumerable<HistoryEntry> entries)
        {
            var incoming = (entries ?? Enumerable.Empty<HistoryEntry>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Type) && x.PositionMs > 0)
                .ToList();

            if (incoming.Count == 0)
                return 0;

            var imported = 0;
            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(false);
                foreach (var source in incoming)
                {
                    source.VideoId = NormalizeVideoId(source.VideoId, source.Type, source.Id);
                    if (string.IsNullOrWhiteSpace(source.VideoId))
                        source.VideoId = NormalizeVideoId(source.Id, source.Type, source.Id);

                    var entry = items.FirstOrDefault(x => IsSameContent(x, source.Type, source.Id) && string.Equals(NormalizeVideoId(x.VideoId, x.Type, x.Id), source.VideoId, StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        entry = Clone(source);
                        entry.IsDismissed = false;
                        entry.IsWatched = false;
                        items.Add(entry);
                        imported++;
                        continue;
                    }

                    if (entry.IsWatched || source.LastPlayedUtc >= entry.LastPlayedUtc || entry.PositionMs <= 0)
                    {
                        entry.VideoId = source.VideoId;
                        entry.SeasonNumber = source.SeasonNumber;
                        entry.EpisodeNumber = source.EpisodeNumber;
                        entry.EpisodeTitle = source.EpisodeTitle;
                        entry.Name = string.IsNullOrWhiteSpace(source.Name) ? entry.Name : source.Name;
                        entry.PosterUrl = string.IsNullOrWhiteSpace(source.PosterUrl) ? entry.PosterUrl : source.PosterUrl;
                        entry.FallbackPosterUrl = string.IsNullOrWhiteSpace(source.FallbackPosterUrl) ? entry.FallbackPosterUrl : source.FallbackPosterUrl;
                        entry.Year = string.IsNullOrWhiteSpace(source.Year) ? entry.Year : source.Year;
                        entry.SourceBaseUrl = string.IsNullOrWhiteSpace(source.SourceBaseUrl) ? entry.SourceBaseUrl : source.SourceBaseUrl;
                        entry.PositionMs = source.PositionMs;
                        entry.DurationMs = source.DurationMs > 0 ? source.DurationMs : entry.DurationMs;
                        entry.LastPlayedUtc = source.LastPlayedUtc == default ? DateTime.UtcNow : source.LastPlayedUtc;
                        entry.IsDismissed = false;
                        entry.IsWatched = false;
                        entry.CompletedUtc = null;
                        imported++;
                    }
                }

                if (imported > 0)
                    await SaveEntriesCoreAsync(items);
            }
            finally
            {
                _sync.Release();
            }

            if (imported > 0)
                HistoryChanged?.Invoke(null, EventArgs.Empty);

            return imported;
        }

        public static async Task<string> ExportJsonAsync()
        {
            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(false);
                return System.Text.Json.JsonSerializer.Serialize(items.Select(Clone).ToList(), new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task ImportJsonAsync(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            bool changed = false;
            await _sync.WaitAsync();
            try
            {
                var items = System.Text.Json.JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
                _cache = items
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Type))
                    .GroupBy(x => BuildVideoKey(x.Type, x.Id, NormalizeVideoId(x.VideoId, x.Type, x.Id)))
                    .Select(g => g.OrderByDescending(x => x.LastPlayedUtc).First())
                    .ToList();
                await SaveEntriesCoreAsync(_cache);
                changed = true;
            }
            catch
            {
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                HistoryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task ResetAsync()
        {
            await _sync.WaitAsync();
            try
            {
                _cache = new List<HistoryEntry>();
                await StorageService.SaveAsync(FileName, _cache);
            }
            finally
            {
                _sync.Release();
            }

            HistoryChanged?.Invoke(null, EventArgs.Empty);
        }

        private static ContinueWatchingItem ToContinueWatchingItem(HistoryEntry entry)
        {
            var posterUrl = !string.IsNullOrWhiteSpace(entry.PosterUrl) ? entry.PosterUrl : entry.StreamPosterUrl;
            var fallbackPosterUrl = !string.IsNullOrWhiteSpace(entry.FallbackPosterUrl) ? entry.FallbackPosterUrl : entry.StreamFallbackPosterUrl;
            var item = new MetaItem
            {
                Id = entry.Id,
                Type = entry.Type,
                Name = entry.Name,
                PosterUrl = posterUrl,
                FallbackPosterUrl = fallbackPosterUrl,
                Poster = !string.IsNullOrWhiteSpace(posterUrl) ? posterUrl : (!string.IsNullOrWhiteSpace(fallbackPosterUrl) ? fallbackPosterUrl : CatalogService.PlaceholderPosterUri),
                Year = entry.Year,
                ImdbRating = entry.ImdbRating,
                SourceBaseUrl = entry.SourceBaseUrl,
                IsPosterLoading = true
            };

            return new ContinueWatchingItem
            {
                Item = item,
                VideoId = entry.VideoId,
                SeasonNumber = entry.SeasonNumber,
                EpisodeNumber = entry.EpisodeNumber,
                EpisodeTitle = entry.EpisodeTitle,
                PositionMs = entry.PositionMs,
                DurationMs = entry.DurationMs,
                LastPlayedUtc = entry.LastPlayedUtc,
                Stream = new CatalogService.StreamOption
                {
                    AddonName = entry.AddonName,
                    DisplayName = entry.StreamDisplayName,
                    Description = entry.Description,
                    DirectUrl = entry.DirectUrl,
                    EmbeddedPageUrl = entry.EmbeddedPageUrl,
                    MagnetUrl = entry.MagnetUrl,
                    ContentName = entry.Name,
                    ContentType = entry.Type,
                    ContentLogoUrl = entry.ContentLogoUrl,
                    PosterUrl = entry.StreamPosterUrl,
                    FallbackPosterUrl = entry.StreamFallbackPosterUrl,
                    Year = entry.Year,
                    ImdbRating = entry.ImdbRating,
                    ContentId = entry.Id,
                    SourceBaseUrl = entry.SourceBaseUrl,
                    VideoId = entry.VideoId,
                    SeasonNumber = entry.SeasonNumber,
                    EpisodeNumber = entry.EpisodeNumber,
                    EpisodeTitle = entry.EpisodeTitle,
                    StreamKey = entry.StreamKey,
                    StartPositionMs = entry.PositionMs
                }
            };
        }

        private static async Task<List<HistoryEntry>> LoadEntriesCoreAsync(bool forceReload)
        {
            if (_cache != null && !forceReload)
                return _cache;

            _cache = await StorageService.LoadAsync<List<HistoryEntry>>(FileName) ?? new List<HistoryEntry>();
            _cache = _cache
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Type))
                .GroupBy(x => BuildVideoKey(x.Type, x.Id, NormalizeVideoId(x.VideoId, x.Type, x.Id)))
                .Select(g => g.OrderByDescending(x => x.LastPlayedUtc).First())
                .ToList();

            return _cache;
        }

        private static async Task SaveEntriesCoreAsync(List<HistoryEntry> items)
        {
            _cache = items.OrderByDescending(x => x.LastPlayedUtc).ToList();
            await StorageService.SaveAsync(FileName, _cache);
        }

        private static HistoryEntry Clone(HistoryEntry entry)
        {
            return new HistoryEntry
            {
                Id = entry.Id,
                Type = entry.Type,
                VideoId = entry.VideoId,
                SeasonNumber = entry.SeasonNumber,
                EpisodeNumber = entry.EpisodeNumber,
                EpisodeTitle = entry.EpisodeTitle,
                Name = entry.Name,
                PosterUrl = entry.PosterUrl,
                FallbackPosterUrl = entry.FallbackPosterUrl,
                Year = entry.Year,
                ImdbRating = entry.ImdbRating,
                SourceBaseUrl = entry.SourceBaseUrl,
                StreamKey = entry.StreamKey,
                StreamDisplayName = entry.StreamDisplayName,
                AddonName = entry.AddonName,
                Description = entry.Description,
                DirectUrl = entry.DirectUrl,
                EmbeddedPageUrl = entry.EmbeddedPageUrl,
                MagnetUrl = entry.MagnetUrl,
                ContentLogoUrl = entry.ContentLogoUrl,
                StreamPosterUrl = entry.StreamPosterUrl,
                StreamFallbackPosterUrl = entry.StreamFallbackPosterUrl,
                PositionMs = entry.PositionMs,
                DurationMs = entry.DurationMs,
                IsWatched = entry.IsWatched,
                IsDismissed = entry.IsDismissed,
                WatchCount = entry.WatchCount,
                LastPlayedUtc = entry.LastPlayedUtc,
                CompletedUtc = entry.CompletedUtc
            };
        }

        private static bool UpdateSeriesAggregate(List<HistoryEntry> items, MetaItem item, bool watched)
        {
            var key = NormalizeVideoId(SeriesAggregateVideoId, item.Type, item.Id);
            var entry = items.FirstOrDefault(x => IsSameContent(x, item.Type, item.Id) && string.Equals(NormalizeVideoId(x.VideoId, item.Type, item.Id), key, StringComparison.OrdinalIgnoreCase));
            if (entry == null && watched)
            {
                entry = CreateBasicEntry(item, key);
                items.Add(entry);
            }

            if (entry == null)
                return false;

            entry.IsWatched = watched;
            entry.PositionMs = 0;
            entry.LastPlayedUtc = DateTime.UtcNow;
            entry.CompletedUtc = watched ? DateTime.UtcNow : null;
            entry.IsDismissed = false;
            if (watched)
                entry.WatchCount = Math.Max(1, entry.WatchCount + 1);
            return true;
        }

        private static HistoryEntry CreateBasicEntry(MetaItem item, string videoId)
        {
            return new HistoryEntry
            {
                Id = item.Id,
                Type = item.Type,
                VideoId = videoId,
                Name = item.Name,
                PosterUrl = PreferPoster(item),
                FallbackPosterUrl = PreferFallbackPoster(item),
                Year = item.Year,
                ImdbRating = item.ImdbRating,
                SourceBaseUrl = item.SourceBaseUrl,
                LastPlayedUtc = DateTime.UtcNow
            };
        }

        private static bool ShouldMarkWatched(long positionMs, long durationMs)
        {
            if (durationMs <= 0)
                return false;

            if (durationMs - positionMs <= 5 * 60 * 1000L)
                return true;

            return durationMs > 0 && (double)positionMs / durationMs >= 0.95;
        }

        private static bool IsSameContent(HistoryEntry entry, string? type, string? id)
            => string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase) && string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase);

        private static string BuildContentKey(string type, string id)
            => $"{type.Trim().ToLowerInvariant()}::{id.Trim().ToLowerInvariant()}";

        private static string BuildVideoKey(string type, string id, string videoId)
            => $"{type.Trim().ToLowerInvariant()}::{id.Trim().ToLowerInvariant()}::{videoId.Trim().ToLowerInvariant()}";

        private static bool IsSeriesAggregate(HistoryEntry entry)
            => string.Equals(entry.VideoId, SeriesAggregateVideoId, StringComparison.OrdinalIgnoreCase);

        private static bool IsWatchedRecord(HistoryEntry entry)
            => entry.IsWatched && !entry.IsDismissed;

        private static string NormalizeVideoId(string? videoId, string? type, string? id)
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) ? (id ?? string.Empty) : string.Empty;

            return videoId.Trim();
        }

        private static string PreferPoster(MetaItem item, string fallback = "")
        {
            if (!string.IsNullOrWhiteSpace(item.PosterUrl))
                return item.PosterUrl;

            if (!string.IsNullOrWhiteSpace(item.Poster) && !string.Equals(item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase))
                return item.Poster;

            return fallback;
        }

        private static string PreferFallbackPoster(MetaItem item, string fallback = "")
        {
            if (!string.IsNullOrWhiteSpace(item.FallbackPosterUrl))
                return item.FallbackPosterUrl;

            if (!string.IsNullOrWhiteSpace(item.Poster) && !string.Equals(item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase))
                return item.Poster;

            return fallback;
        }

        private static string FormatTime(long milliseconds)
        {
            if (milliseconds <= 0)
                return "00:00";

            var time = TimeSpan.FromMilliseconds(milliseconds);
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes:00}:{time.Seconds:00}";
        }
    }
}
