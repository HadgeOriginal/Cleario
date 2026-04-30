using Cleario.Models;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class CatalogService
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static readonly SemaphoreSlim _posterDownloadSemaphore = new(4, 4);
        private static readonly object _metaCacheLock = new();
        private static readonly Dictionary<string, MetaDetails> _metaCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _posterCacheQueueLock = new();
        private static readonly HashSet<string> _posterCacheQueue = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _manifestCacheLock = new();
        private static readonly Dictionary<string, ManifestCacheEntry> _manifestCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Task<string?>> _manifestRequests = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _metadataBaseUrlsCacheLock = new();
        private static string _metadataBaseUrlsSignature = string.Empty;
        private static List<string> _metadataBaseUrlsCache = new();

        private sealed class ManifestCacheEntry
        {
            public string Json { get; init; } = string.Empty;
            public DateTimeOffset ExpiresAtUtc { get; init; }
        }

        public const string CinemetaBaseUrl = "https://v3-cinemeta.strem.io";
        public const string PlaceholderPosterUri = "ms-appx:///Assets/StoreLogo.png";

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 16,
                UseCookies = false
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        static CatalogService()
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Cleario/1.0");
        }

        public sealed class DiscoverCatalogDefinition
        {
            public string Type { get; set; } = string.Empty;
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public bool SupportsSearch { get; set; }
            public bool SupportsSkip { get; set; }
            public bool SupportsGenre { get; set; }
            public bool RequiresGenre { get; set; }
            public List<string> GenreOptions { get; set; } = new();
        }

        public sealed class MetaDetails
        {
            public string Name { get; set; } = string.Empty;
            public string PosterUrl { get; set; } = string.Empty;
            public string BackgroundUrl { get; set; } = string.Empty;
            public string LogoUrl { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string ReleaseInfo { get; set; } = string.Empty;
            public string Year { get; set; } = string.Empty;
            public string ImdbRating { get; set; } = string.Empty;
            public string Runtime { get; set; } = string.Empty;
            public string Genres { get; set; } = string.Empty;
            public string Cast { get; set; } = string.Empty;
            public string Directors { get; set; } = string.Empty;
        }

        public sealed class SeriesEpisodeOption : INotifyPropertyChanged
        {
            private string _videoId = string.Empty;
            private int _season;
            private int _episode;
            private string _title = string.Empty;
            private string _releaseDate = string.Empty;
            private string _thumbnailUrl = string.Empty;
            private string _fallbackThumbnailUrl = string.Empty;
            private bool _isWatched;
            private bool _hideSpoilers;

            public event PropertyChangedEventHandler? PropertyChanged;

            public string VideoId
            {
                get => _videoId;
                set => SetProperty(ref _videoId, value ?? string.Empty);
            }

            public int Season
            {
                get => _season;
                set
                {
                    if (SetProperty(ref _season, value))
                    {
                        OnPropertyChanged(nameof(EpisodeCode));
                        OnPropertyChanged(nameof(EpisodeLabel));
                    }
                }
            }

            public int Episode
            {
                get => _episode;
                set
                {
                    if (SetProperty(ref _episode, value))
                    {
                        OnPropertyChanged(nameof(DisplayTitle));
                        OnPropertyChanged(nameof(EpisodeNumberText));
                        OnPropertyChanged(nameof(EpisodeCode));
                        OnPropertyChanged(nameof(EpisodeLabel));
                    }
                }
            }

            public string Title
            {
                get => _title;
                set
                {
                    if (SetProperty(ref _title, value ?? string.Empty))
                        OnEpisodeDisplayPropertiesChanged();
                }
            }

            public string ReleaseDate
            {
                get => _releaseDate;
                set
                {
                    if (SetProperty(ref _releaseDate, value ?? string.Empty))
                    {
                        OnPropertyChanged(nameof(IsReleased));
                        OnPropertyChanged(nameof(IsUpcoming));
                        OnPropertyChanged(nameof(ReleaseDateLabel));
                        OnPropertyChanged(nameof(ThumbnailDisplayUrl));
                        OnPropertyChanged(nameof(EpisodeOverlayText));
                        OnPropertyChanged(nameof(EpisodeOverlayVisibility));
                        OnPropertyChanged(nameof(EpisodeImageOpacity));
                    }
                }
            }

            public string ThumbnailUrl
            {
                get => _thumbnailUrl;
                set
                {
                    if (SetProperty(ref _thumbnailUrl, value ?? string.Empty))
                        OnPropertyChanged(nameof(ThumbnailDisplayUrl));
                }
            }

            public string FallbackThumbnailUrl
            {
                get => _fallbackThumbnailUrl;
                set
                {
                    if (SetProperty(ref _fallbackThumbnailUrl, value ?? string.Empty))
                        OnPropertyChanged(nameof(ThumbnailDisplayUrl));
                }
            }

            public bool IsWatched
            {
                get => _isWatched;
                set
                {
                    if (SetProperty(ref _isWatched, value))
                        OnEpisodeDisplayPropertiesChanged();
                }
            }

            public bool HideSpoilers
            {
                get => _hideSpoilers;
                set
                {
                    if (SetProperty(ref _hideSpoilers, value))
                        OnEpisodeDisplayPropertiesChanged();
                }
            }

            public bool IsReleased
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(ReleaseDate))
                        return true;

                    return !DateTimeOffset.TryParse(ReleaseDate, out var releaseDate)
                        || releaseDate.Date <= DateTimeOffset.Now.Date;
                }
            }

            public bool IsUpcoming => !IsReleased;
            public bool ShouldHideSpoilers => HideSpoilers && !IsWatched;

            public string DisplayTitle => ShouldHideSpoilers || string.IsNullOrWhiteSpace(Title)
                ? $"Episode {Episode}"
                : Title;

            public string EpisodeNumberText => $"{Episode}.";

            public string EpisodeCode => Season <= 0
                ? $"Special {Episode:00}"
                : $"S{Season:00}E{Episode:00}";

            public string EpisodeLabel
            {
                get
                {
                    var label = ShouldHideSpoilers
                        ? $"E{Episode:00}"
                        : (string.IsNullOrWhiteSpace(Title)
                            ? $"Episode {Episode}"
                            : $"E{Episode:00} {Title}");

                    return IsWatched ? $"{label} ✓" : label;
                }
            }

            public string ReleaseDateLabel => string.IsNullOrWhiteSpace(ReleaseDate) ? string.Empty : ReleaseDate;
            public string ThumbnailDisplayUrl
            {
                get
                {
                    if (IsUpcoming && !string.IsNullOrWhiteSpace(FallbackThumbnailUrl))
                        return FallbackThumbnailUrl;

                    if (!string.IsNullOrWhiteSpace(ThumbnailUrl))
                        return ThumbnailUrl;

                    if (!string.IsNullOrWhiteSpace(FallbackThumbnailUrl))
                        return FallbackThumbnailUrl;

                    return PlaceholderPosterUri;
                }
            }

            public Visibility WatchedBadgeVisibility => IsWatched ? Visibility.Visible : Visibility.Collapsed;
            public string EpisodeOverlayText => IsUpcoming ? "Coming soon" : (ShouldHideSpoilers ? "Spoiler hidden" : string.Empty);
            public Visibility EpisodeOverlayVisibility => string.IsNullOrWhiteSpace(EpisodeOverlayText) ? Visibility.Collapsed : Visibility.Visible;
            public double EpisodeImageOpacity => IsUpcoming || ShouldHideSpoilers ? 0.25 : 1.0;

            private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(storage, value))
                    return false;

                storage = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

            private void OnEpisodeDisplayPropertiesChanged()
            {
                OnPropertyChanged(nameof(IsWatched));
                OnPropertyChanged(nameof(HideSpoilers));
                OnPropertyChanged(nameof(ShouldHideSpoilers));
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(EpisodeLabel));
                OnPropertyChanged(nameof(ThumbnailDisplayUrl));
                OnPropertyChanged(nameof(WatchedBadgeVisibility));
                OnPropertyChanged(nameof(EpisodeOverlayText));
                OnPropertyChanged(nameof(EpisodeOverlayVisibility));
                OnPropertyChanged(nameof(EpisodeImageOpacity));
            }
        }

        public sealed class CalendarReleaseEntry
        {
            public string Type { get; set; } = string.Empty;
            public string MetaId { get; set; } = string.Empty;
            public string VideoId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string EpisodeTitle { get; set; } = string.Empty;
            public int? Season { get; set; }
            public int? Episode { get; set; }
            public DateTimeOffset ReleaseDate { get; set; }
            public string PosterUrl { get; set; } = string.Empty;
            public string FallbackPosterUrl { get; set; } = string.Empty;
            public string SourceBaseUrl { get; set; } = string.Empty;

            public bool IsSeries => string.Equals(Type, "series", StringComparison.OrdinalIgnoreCase);

            public string SidebarCode
            {
                get
                {
                    if (!IsSeries)
                        return string.Empty;

                    if ((Season ?? 0) <= 0)
                        return Episode.HasValue ? $"Special {Episode.Value}" : "Special";

                    if (Season.HasValue && Episode.HasValue)
                        return $"S{Season.Value}E{Episode.Value}";

                    return string.Empty;
                }
            }
        }

        public sealed class StreamOption
        {
            public string AddonName { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string DirectUrl { get; set; } = string.Empty;
            public string EmbeddedPageUrl { get; set; } = string.Empty;
            public string MagnetUrl { get; set; } = string.Empty;
            public string ContentName { get; set; } = string.Empty;
            public string ContentType { get; set; } = string.Empty;
            public string ContentLogoUrl { get; set; } = string.Empty;
            public string PosterUrl { get; set; } = string.Empty;
            public string FallbackPosterUrl { get; set; } = string.Empty;
            public string Year { get; set; } = string.Empty;
            public string ImdbRating { get; set; } = string.Empty;
            public string ContentId { get; set; } = string.Empty;
            public string SourceBaseUrl { get; set; } = string.Empty;
            public string VideoId { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
            public string EpisodeTitle { get; set; } = string.Empty;
            public string StreamKey { get; set; } = string.Empty;
            public long StartPositionMs { get; set; }
            public long ResumePositionMs { get; set; }
            public long ResumeDurationMs { get; set; }
            public bool IsResumeCandidate { get; set; }
            public double ResumeBarHeight { get; set; }
            public double ResumeTrackWidth => IsResumeCandidate ? 240 : 0;
            public string ResumeBorderBrush => IsResumeCandidate ? "#665AB0FF" : "#26FFFFFF";
            public string ResumeTagText => IsResumeCandidate ? "Last used" : string.Empty;
            public Visibility ResumeTagVisibility => IsResumeCandidate ? Visibility.Visible : Visibility.Collapsed;
            public double ResumeFillWidth
            {
                get
                {
                    if (!IsResumeCandidate || ResumeDurationMs <= 0)
                        return 0;

                    var ratio = Math.Clamp((double)ResumePositionMs / Math.Max(1, ResumeDurationMs), 0, 1);
                    return Math.Max(12, ResumeTrackWidth * ratio);
                }
            }

            public bool IsSupportedInApp =>
                !string.IsNullOrWhiteSpace(DirectUrl) || !string.IsNullOrWhiteSpace(EmbeddedPageUrl);

            public string ModeLabel =>
                !string.IsNullOrWhiteSpace(EmbeddedPageUrl) ? "Web player" :
                !string.IsNullOrWhiteSpace(DirectUrl) ? string.Empty :
                !string.IsNullOrWhiteSpace(MagnetUrl) ? "Torrent / external" :
                string.Empty;
        }


        public static string BuildStreamIdentity(StreamOption? stream)
        {
            if (stream == null)
                return string.Empty;

            return string.Join("|", new[]
            {
                stream.AddonName ?? string.Empty,
                stream.DisplayName ?? string.Empty,
                stream.DirectUrl ?? string.Empty,
                stream.EmbeddedPageUrl ?? string.Empty,
                stream.MagnetUrl ?? string.Empty,
                stream.Description ?? string.Empty
            });
        }

        public static async Task<string> GetMetadataCatalogBaseUrlAsync()
        {
            var candidates = await GetMetadataBaseCandidatesAsync();
            return candidates.FirstOrDefault() ?? string.Empty;
        }

        public static async Task<List<DiscoverCatalogDefinition>> GetDiscoverCatalogsAsync()
        {
            var baseUrl = await GetMetadataCatalogBaseUrlAsync();
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new List<DiscoverCatalogDefinition>();

            var manifest = await TryGetManifestDocumentAsync(baseUrl);

            await SettingsManager.InitializeAsync();
            if (manifest == null &&
                SettingsManager.MetadataProvider == MetadataProviderMode.Cinemeta &&
                !string.Equals(baseUrl, CinemetaBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                manifest = await TryGetManifestDocumentAsync(CinemetaBaseUrl);
            }

            if (manifest == null)
                return new List<DiscoverCatalogDefinition>();

            using (manifest)
            {
                return ParseDiscoverCatalogs(manifest.RootElement);
            }
        }

        public static async Task<List<MetaItem>> GetCatalogItemsAsync(
            string addonBaseUrl,
            string type,
            string catalogId,
            int skip = 0,
            string? searchQuery = null,
            string? genre = null)
        {
            return await TryGetCatalogItemsInternalAsync(addonBaseUrl, type, catalogId, skip, searchQuery, genre);
        }

        private static async Task<List<MetaItem>> TryGetCatalogItemsInternalAsync(
            string addonBaseUrl,
            string type,
            string catalogId,
            int skip = 0,
            string? searchQuery = null,
            string? genre = null)
        {
            var results = new List<MetaItem>();

            try
            {
                var baseUrl = NormalizeAddonBaseUrl(addonBaseUrl);
                var extras = new List<string>();

                if (!string.IsNullOrWhiteSpace(searchQuery))
                    extras.Add($"search={Uri.EscapeDataString(searchQuery.Trim())}");

                if (!string.IsNullOrWhiteSpace(genre))
                    extras.Add($"genre={Uri.EscapeDataString(genre.Trim())}");

                if (skip > 0)
                    extras.Add($"skip={skip}");

                var extraPath = extras.Count > 0
                    ? "/" + string.Join("/", extras)
                    : string.Empty;

                var url = $"{baseUrl}/catalog/{type}/{catalogId}{extraPath}.json";

                var json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array)
                    return results;

                foreach (var meta in metas.EnumerateArray())
                {
                    if (!meta.TryGetProperty("id", out var idProp))
                        continue;

                    var id = idProp.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    var name = meta.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString() ?? id
                        : id;

                    var itemType = meta.TryGetProperty("type", out var typeProp)
                        ? typeProp.GetString() ?? type
                        : type;

                    var poster = meta.TryGetProperty("poster", out var posterProp)
                        ? posterProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(poster) &&
                        meta.TryGetProperty("background", out var backgroundProp))
                    {
                        poster = backgroundProp.GetString() ?? string.Empty;
                    }

                    results.Add(new MetaItem
                    {
                        Id = id,
                        Name = name,
                        Type = itemType,
                        Poster = PlaceholderPosterUri,
                        PosterUrl = NormalizeUrl(poster, baseUrl),
                        FallbackPosterUrl = BuildMetaHubPosterUrl(id, "large"),
                        SourceBaseUrl = baseUrl
                    });
                }

                return results;
            }
            catch
            {
                return new List<MetaItem>();
            }
        }

        public static async Task<MetaDetails> GetMetaDetailsAsync(string type, string id, string? preferredBaseUrl = null)
        {
            var normalizedPreferredBaseUrl = NormalizeAddonBaseUrl(preferredBaseUrl);
            var cacheKey = string.IsNullOrWhiteSpace(normalizedPreferredBaseUrl)
                ? $"{type}:{id}"
                : $"{normalizedPreferredBaseUrl}|{type}:{id}";

            lock (_metaCacheLock)
            {
                if (_metaCache.TryGetValue(cacheKey, out var cached))
                    return CloneMetaDetails(cached);
            }

            try
            {
                foreach (var metadataBaseUrl in await GetMetadataBaseUrlsAsync(normalizedPreferredBaseUrl))
                {
                    var details = await TryGetMetaDetailsFromBaseUrlAsync(type, id, metadataBaseUrl);
                    if (!IsMetaDetailsEmpty(details))
                    {
                        lock (_metaCacheLock)
                            _metaCache[cacheKey] = details;

                        return CloneMetaDetails(details);
                    }
                }

                return new MetaDetails();
            }
            catch
            {
                return new MetaDetails();
            }
        }

        public static async Task<List<SeriesEpisodeOption>> GetSeriesEpisodesAsync(string seriesId, string? preferredBaseUrl = null)
        {
            try
            {
                foreach (var metadataBaseUrl in await GetMetadataBaseUrlsAsync(preferredBaseUrl))
                {
                    var results = await TryGetSeriesEpisodesFromBaseUrlAsync(seriesId, metadataBaseUrl);
                    if (results.Count > 0)
                        return results;
                }

                return new List<SeriesEpisodeOption>();
            }
            catch
            {
                return new List<SeriesEpisodeOption>();
            }
        }

        public static async Task<List<CalendarReleaseEntry>> GetCalendarReleaseEntriesAsync(MetaItem item)
        {
            var results = new List<CalendarReleaseEntry>();

            if (item == null || string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Id))
                return results;

            try
            {
                foreach (var metadataBaseUrl in await GetMetadataBaseUrlsAsync(item.SourceBaseUrl))
                {
                    var entries = await TryGetCalendarReleaseEntriesFromBaseUrlAsync(item, metadataBaseUrl);
                    if (entries.Count > 0)
                        return entries;
                }
            }
            catch
            {
            }

            return results;
        }

        public static async Task<string> ResolveStreamRequestIdAsync(string type, string id, string? preferredBaseUrl = null)
        {
            if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase) && !id.Contains(':'))
                return await ResolveSeriesVideoIdAsync(type, id, preferredBaseUrl);

            return id;
        }

        private static async Task<string> ResolveSeriesVideoIdAsync(string type, string id, string? preferredBaseUrl = null)
        {
            try
            {
                foreach (var metadataBaseUrl in await GetMetadataBaseUrlsAsync(preferredBaseUrl))
                {
                    var videoId = await TryResolveSeriesVideoIdFromBaseUrlAsync(type, id, metadataBaseUrl);
                    if (!string.IsNullOrWhiteSpace(videoId) &&
                        !string.Equals(videoId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        return videoId;
                    }
                }
            }
            catch
            {
            }

            return id;
        }

        public static async Task<List<StreamOption>> GetStreamsAsync(string type, string id)
        {
            if (AddonManager.Addons.Count == 0)
                await AddonManager.InitializeAsync();

            var requestId = await ResolveStreamRequestIdAsync(type, id);
            var enabledAddons = AddonManager.GetAddonsSnapshot(enabledOnly: true);

            var tasks = enabledAddons
                .Select(addon => GetStreamsForAddonAsync(addon, type, requestId))
                .ToArray();

            var results = await Task.WhenAll(tasks);
            var allStreams = results.SelectMany(x => x).ToList();

            return allStreams
                .GroupBy(s => $"{s.DisplayName}|{s.Description}|{s.DirectUrl}|{s.EmbeddedPageUrl}|{s.MagnetUrl}")
                .Select(g => g.First())
                .ToList();
        }


        public static async Task<List<StreamOption>> GetStreamsForAddonAsync(Addon addon, string type, string requestId)
        {
            var results = new List<StreamOption>();

            if (addon == null || !addon.IsEnabled)
                return results;

            try
            {
                var baseUrl = NormalizeAddonBaseUrl(addon.ManifestUrl);
                var url = BuildStreamUrl(baseUrl, type, requestId);
                var json = await _httpClient.GetStringAsync(url);

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                    return results;

                foreach (var stream in streams.EnumerateArray())
                {
                    var name = stream.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString() ?? "Stream"
                        : stream.TryGetProperty("title", out var titleProp)
                            ? titleProp.GetString() ?? "Stream"
                            : "Stream";

                    var description = stream.TryGetProperty("description", out var descriptionProp)
                        ? descriptionProp.GetString() ?? string.Empty
                        : stream.TryGetProperty("title", out var titleTextProp)
                            ? titleTextProp.GetString() ?? string.Empty
                            : string.Empty;

                    results.Add(new StreamOption
                    {
                        AddonName = addon.Name,
                        DisplayName = $"[{addon.Name}] {name}",
                        Description = description,
                        DirectUrl = ExtractDirectUrlCandidate(stream),
                        EmbeddedPageUrl = ExtractEmbeddedPageUrl(stream),
                        MagnetUrl = ExtractMagnetUrl(stream)
                    });
                }

                return results
                    .GroupBy(s => $"{s.DisplayName}|{s.Description}|{s.DirectUrl}|{s.EmbeddedPageUrl}|{s.MagnetUrl}")
                    .Select(g => g.First())
                    .ToList();
            }
            catch
            {
                return new List<StreamOption>();
            }
        }

        public static async Task<List<string>> GetAllPosterCandidatesAsync(
            string type,
            string id,
            string? originalUrl,
            string? fallbackPosterUrl)
        {
            var results = GetBasicPosterCandidates(id, originalUrl, fallbackPosterUrl);

            try
            {
                var details = await GetMetaDetailsAsync(type, id);
                AddCandidate(results, details.PosterUrl);
                AddCandidate(results, details.BackgroundUrl);
            }
            catch
            {
            }

            await SettingsManager.InitializeAsync();

            if (!SettingsManager.CacheImages)
                return results;

            var cachedResults = new List<string>();

            foreach (var candidate in results)
            {
                var cached = TryGetCachedPosterUri(id, candidate);
                AddCandidate(cachedResults, cached);
                AddCandidate(cachedResults, candidate);

                if (string.IsNullOrWhiteSpace(cached))
                    QueuePosterCacheDownload(id, candidate);
            }

            return cachedResults;
        }

        public static async Task<string> GetBestPosterUriAsync(
            string type,
            string id,
            string? originalUrl,
            string? fallbackPosterUrl)
        {
            var candidates = await GetAllPosterCandidatesAsync(type, id, originalUrl, fallbackPosterUrl);
            return candidates.FirstOrDefault() ?? PlaceholderPosterUri;
        }

        public static async Task<List<string>> GetBrowsePosterCandidatesAsync(
            string id,
            string? originalUrl,
            string? fallbackPosterUrl)
        {
            var results = GetBasicPosterCandidates(id, originalUrl, fallbackPosterUrl);

            await SettingsManager.InitializeAsync();
            if (!SettingsManager.CacheImages)
                return results;

            var browseResults = new List<string>();

            foreach (var candidate in results)
            {
                var cached = TryGetCachedPosterUri(id, candidate);
                AddCandidate(browseResults, cached);
                AddCandidate(browseResults, candidate);
            }

            return browseResults;
        }

        public static async Task QueuePosterCacheIfEnabledAsync(string id, string? url)
        {
            await SettingsManager.InitializeAsync();

            if (!SettingsManager.CacheImages || string.IsNullOrWhiteSpace(url))
                return;

            QueuePosterCacheDownload(id, url);
        }

        public static async Task EnforcePosterCacheLimitIfEnabledAsync()
        {
            await SettingsManager.InitializeAsync();

            if (!SettingsManager.CacheImages)
                return;

            await EnsurePosterCacheCapacityAsync(0);
        }

        public static List<string> GetBasicPosterCandidates(string id, string? originalUrl, string? fallbackPosterUrl)
        {
            var results = new List<string>();

            AddCandidate(results, NormalizeUrl(originalUrl));
            AddCandidate(results, NormalizeUrl(fallbackPosterUrl));
            AddCandidate(results, BuildMetaHubPosterUrl(id, "large"));
            AddCandidate(results, BuildMetaHubPosterUrl(id, "big"));
            AddCandidate(results, BuildMetaHubPosterUrl(id, "medium"));
            AddCandidate(results, BuildMetaHubPosterUrl(id, "small"));

            return results;
        }

        public static string BuildMetaHubPosterUrl(string id, string size)
        {
            if (string.IsNullOrWhiteSpace(id))
                return string.Empty;

            return $"https://images.metahub.space/poster/{size}/{Uri.EscapeDataString(id)}/img";
        }

        public static string BuildMetaHubLogoUrl(string id, string size)
        {
            if (string.IsNullOrWhiteSpace(id))
                return string.Empty;

            return $"https://images.metahub.space/logo/{size}/{Uri.EscapeDataString(id)}/img";
        }

        public static bool LooksLikeDirectMediaUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            var value = url.ToLowerInvariant();

            return value.Contains(".m3u8") ||
                   value.Contains(".mp4") ||
                   value.Contains(".mkv") ||
                   value.Contains(".webm") ||
                   value.Contains(".avi") ||
                   value.Contains(".mov") ||
                   value.Contains(".ts") ||
                   value.Contains(".mpd");
        }

        public static bool CouldBeHtmlPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            var value = url.ToLowerInvariant();

            if (LooksLikeDirectMediaUrl(value))
                return false;

            if (value.StartsWith("magnet:"))
                return false;

            return value.StartsWith("http://") || value.StartsWith("https://");
        }

        public static async Task<JsonDocument?> GetManifestDocumentAsync(string addonBaseUrl)
        {
            try
            {
                var json = await GetManifestJsonAsync(addonBaseUrl);
                return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<JsonDocument?> TryGetManifestDocumentAsync(string addonBaseUrl)
        {
            return await GetManifestDocumentAsync(addonBaseUrl);
        }

        private static async Task<string?> GetManifestJsonAsync(string addonBaseUrl)
        {
            var baseUrl = NormalizeAddonBaseUrl(addonBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            Task<string?>? pendingRequest = null;

            lock (_manifestCacheLock)
            {
                if (_manifestCache.TryGetValue(baseUrl, out var cached) &&
                    cached.ExpiresAtUtc > DateTimeOffset.UtcNow &&
                    !string.IsNullOrWhiteSpace(cached.Json))
                {
                    return cached.Json;
                }

                if (!_manifestRequests.TryGetValue(baseUrl, out pendingRequest))
                {
                    pendingRequest = FetchManifestJsonCoreAsync(baseUrl);
                    _manifestRequests[baseUrl] = pendingRequest;
                }
            }

            try
            {
                var json = await pendingRequest;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    lock (_manifestCacheLock)
                    {
                        _manifestCache[baseUrl] = new ManifestCacheEntry
                        {
                            Json = json,
                            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15)
                        };
                    }
                }

                return json;
            }
            finally
            {
                lock (_manifestCacheLock)
                {
                    if (_manifestRequests.TryGetValue(baseUrl, out var existing) && ReferenceEquals(existing, pendingRequest))
                        _manifestRequests.Remove(baseUrl);
                }
            }
        }

        private static async Task<string?> FetchManifestJsonCoreAsync(string baseUrl)
        {
            try
            {
                var manifestUrl = $"{NormalizeAddonBaseUrl(baseUrl)}/manifest.json";
                return await _httpClient.GetStringAsync(manifestUrl);
            }
            catch
            {
                return null;
            }
        }

        private static bool HasSupportedDiscoverCatalogs(JsonElement manifestRoot)
        {
            return ParseDiscoverCatalogs(manifestRoot).Count > 0;
        }

        private static bool SupportsMetadataOrDiscover(JsonElement manifestRoot)
        {
            if (HasSupportedDiscoverCatalogs(manifestRoot))
                return true;

            if (!manifestRoot.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var resource in resources.EnumerateArray())
            {
                if (resource.ValueKind == JsonValueKind.String)
                {
                    var resourceName = resource.GetString() ?? string.Empty;
                    if (string.Equals(resourceName, "meta", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(resourceName, "catalog", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    continue;
                }

                if (resource.ValueKind != JsonValueKind.Object)
                    continue;

                var name = resource.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? string.Empty
                    : string.Empty;

                if (!string.Equals(name, "meta", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "catalog", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!resource.TryGetProperty("types", out var typesProp) || typesProp.ValueKind != JsonValueKind.Array)
                    return true;

                foreach (var type in typesProp.EnumerateArray())
                {
                    var value = type.GetString() ?? string.Empty;
                    if (string.Equals(value, "movie", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(value, "series", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static List<DiscoverCatalogDefinition> ParseDiscoverCatalogs(JsonElement manifestRoot)
        {
            var results = new List<DiscoverCatalogDefinition>();

            if (!manifestRoot.TryGetProperty("catalogs", out var catalogs) || catalogs.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var catalog in catalogs.EnumerateArray())
            {
                var type = catalog.TryGetProperty("type", out var typeProp)
                    ? typeProp.GetString() ?? string.Empty
                    : string.Empty;

                if (!string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, "series", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var id = catalog.TryGetProperty("id", out var idProp)
                    ? idProp.GetString() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = catalog.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? id
                    : id;

                var definition = new DiscoverCatalogDefinition
                {
                    Type = type,
                    Id = id,
                    Name = name
                };

                var extraNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var extraRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (catalog.TryGetProperty("extraSupported", out var extraSupportedProp) &&
                    extraSupportedProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in extraSupportedProp.EnumerateArray())
                    {
                        var value = item.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(value))
                            extraNames.Add(value);
                    }
                }

                if (catalog.TryGetProperty("extraRequired", out var extraRequiredProp) &&
                    extraRequiredProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in extraRequiredProp.EnumerateArray())
                    {
                        var value = item.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(value))
                            extraRequired.Add(value);
                    }
                }

                if (catalog.TryGetProperty("extra", out var extraProp) &&
                    extraProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var extra in extraProp.EnumerateArray())
                    {
                        if (extra.ValueKind != JsonValueKind.Object)
                            continue;

                        var extraName = extra.TryGetProperty("name", out var extraNameProp)
                            ? extraNameProp.GetString() ?? string.Empty
                            : string.Empty;

                        if (string.IsNullOrWhiteSpace(extraName))
                            continue;

                        extraNames.Add(extraName);

                        if (extra.TryGetProperty("isRequired", out var isRequiredProp) &&
                            isRequiredProp.ValueKind == JsonValueKind.True)
                        {
                            extraRequired.Add(extraName);
                        }

                        if (string.Equals(extraName, "genre", StringComparison.OrdinalIgnoreCase) &&
                            extra.TryGetProperty("options", out var optionsProp) &&
                            optionsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var option in optionsProp.EnumerateArray())
                            {
                                var value = option.GetString() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(value) &&
                                    !definition.GenreOptions.Contains(value, StringComparer.OrdinalIgnoreCase))
                                {
                                    definition.GenreOptions.Add(value);
                                }
                            }
                        }
                    }
                }

                if (definition.GenreOptions.Count == 0 &&
                    catalog.TryGetProperty("genres", out var genresProp) &&
                    genresProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var genre in genresProp.EnumerateArray())
                    {
                        var value = genre.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(value) &&
                            !definition.GenreOptions.Contains(value, StringComparer.OrdinalIgnoreCase))
                        {
                            definition.GenreOptions.Add(value);
                        }
                    }
                }

                var unsupportedRequiredExtras = extraRequired
                    .Where(x =>
                        !string.Equals(x, "genre", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(x, "search", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(x, "skip", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (unsupportedRequiredExtras.Count > 0)
                    continue;

                definition.SupportsSearch = extraNames.Contains("search");
                definition.SupportsSkip = extraNames.Contains("skip");
                definition.SupportsGenre = definition.GenreOptions.Count > 0 || extraNames.Contains("genre");
                definition.RequiresGenre = extraRequired.Contains("genre");

                results.Add(definition);
            }

            return results;
        }

        private static string ExtractDirectUrlCandidate(JsonElement stream)
        {
            if (stream.TryGetProperty("url", out var urlProp))
                return urlProp.GetString() ?? string.Empty;

            if (stream.TryGetProperty("file", out var fileProp))
                return fileProp.GetString() ?? string.Empty;

            return string.Empty;
        }

        private static string ExtractEmbeddedPageUrl(JsonElement stream)
        {
            if (stream.TryGetProperty("externalUrl", out var externalUrlProp))
                return externalUrlProp.GetString() ?? string.Empty;

            if (stream.TryGetProperty("embedUrl", out var embedUrlProp))
                return embedUrlProp.GetString() ?? string.Empty;

            return string.Empty;
        }

        private static string ExtractMagnetUrl(JsonElement stream)
        {
            if (stream.TryGetProperty("infoHash", out var infoHashProp))
            {
                var infoHash = infoHashProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(infoHash))
                    return $"magnet:?xt=urn:btih:{infoHash}";
            }

            if (stream.TryGetProperty("sources", out var sourcesProp) && sourcesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sourcesProp.EnumerateArray())
                {
                    var value = source.GetString() ?? string.Empty;
                    if (value.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                        return value;

                    if (value.StartsWith("dht:", StringComparison.OrdinalIgnoreCase))
                    {
                        var infoHash = value.Substring(4);
                        if (!string.IsNullOrWhiteSpace(infoHash))
                            return $"magnet:?xt=urn:btih:{infoHash}";
                    }
                }
            }

            return string.Empty;
        }

        private static void AddCandidate(List<string> list, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                list.Add(value);
        }

        private static string NormalizeAddonBaseUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            var value = url.Trim();

            if (value.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                return value[..^"/manifest.json".Length];

            if (value.EndsWith("/"))
                value = value.TrimEnd('/');

            return value;
        }

        private static string NormalizeUrl(string? url, string? baseUrl = null)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            var value = url.Trim();

            if (value.StartsWith("//"))
                return "https:" + value;

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (!string.IsNullOrWhiteSpace(baseUrl) &&
                Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, value, out var resolved))
            {
                return resolved.ToString();
            }

            return value;
        }

        private static string ParseImdbRating(JsonElement meta)
        {
            if (meta.TryGetProperty("imdbRating", out var imdbProp))
            {
                if (imdbProp.ValueKind == JsonValueKind.Number && imdbProp.TryGetDouble(out var d))
                    return d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

                if (imdbProp.ValueKind == JsonValueKind.String)
                {
                    var value = imdbProp.GetString() ?? string.Empty;
                    value = value.Replace(',', '.');

                    if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        return parsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    }

                    return value;
                }
            }

            return string.Empty;
        }

        private static string ParseRuntime(JsonElement meta)
        {
            if (meta.TryGetProperty("runtime", out var runtimeProp))
            {
                var value = runtimeProp.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            if (meta.TryGetProperty("runtimeMinutes", out var runtimeMinutesProp))
            {
                if (runtimeMinutesProp.ValueKind == JsonValueKind.Number && runtimeMinutesProp.TryGetInt32(out var minutes))
                    return $"{minutes} min";

                if (runtimeMinutesProp.ValueKind == JsonValueKind.String && int.TryParse(runtimeMinutesProp.GetString(), out var minutesString))
                    return $"{minutesString} min";
            }

            return string.Empty;
        }

        private static string ParseJoinedArray(JsonElement meta, string propertyName, int maxCount)
        {
            if (!meta.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var values = new List<string>();

            foreach (var item in prop.EnumerateArray())
            {
                var text = item.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                    values.Add(text);

                if (values.Count >= maxCount)
                    break;
            }

            return string.Join(", ", values);
        }

        private static string ExtractYear(string releaseInfo)
        {
            if (string.IsNullOrWhiteSpace(releaseInfo))
                return string.Empty;

            var match = Regex.Match(releaseInfo, @"\b(19|20)\d{2}\b");
            return match.Success ? match.Value : string.Empty;
        }

        private static MetaDetails CloneMetaDetails(MetaDetails details)
        {
            return new MetaDetails
            {
                Name = details.Name,
                PosterUrl = details.PosterUrl,
                BackgroundUrl = details.BackgroundUrl,
                LogoUrl = details.LogoUrl,
                Description = details.Description,
                ReleaseInfo = details.ReleaseInfo,
                Year = details.Year,
                ImdbRating = details.ImdbRating,
                Runtime = details.Runtime,
                Genres = details.Genres,
                Cast = details.Cast,
                Directors = details.Directors
            };
        }

        private static string GetPosterCacheFolderPath()
        {
            return AppPaths.GetFolderPath("PosterCache");
        }

        private static async Task<long> GetPosterCacheLimitBytesAsync()
        {
            await SettingsManager.InitializeAsync();

            if (!SettingsManager.CacheImages)
                return 0;

            if (SettingsManager.CacheLimitGb <= 0)
                return 0;

            return (long)SettingsManager.CacheLimitGb * 1024L * 1024L * 1024L;
        }

        private static async Task EnsurePosterCacheCapacityAsync(long bytesNeeded)
        {
            try
            {
                var limitBytes = await GetPosterCacheLimitBytesAsync();
                if (limitBytes <= 0)
                    return;

                var posterFolderPath = GetPosterCacheFolderPath();
                Directory.CreateDirectory(posterFolderPath);

                var files = Directory
                    .EnumerateFiles(posterFolderPath)
                    .Select(path => new FileInfo(path))
                    .OrderBy(info => info.CreationTimeUtc == DateTime.MinValue ? info.LastWriteTimeUtc : info.CreationTimeUtc)
                    .ThenBy(info => info.LastWriteTimeUtc)
                    .ToList();

                long totalBytes = files.Sum(info =>
                {
                    try { return info.Length; } catch { return 0L; }
                });

                if (bytesNeeded > 0 && bytesNeeded > limitBytes)
                    return;

                foreach (var file in files)
                {
                    if (totalBytes + bytesNeeded <= limitBytes)
                        break;

                    try
                    {
                        long length = file.Length;
                        file.Delete();
                        totalBytes -= length;
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static string TryGetCachedPosterUri(string id, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (url.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            try
            {
                var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                var safeId = string.IsNullOrWhiteSpace(id) ? "poster" : MakeSafeFileName(id);
                var posterFolderPath = GetPosterCacheFolderPath();
                Directory.CreateDirectory(posterFolderPath);

                var existingFile = Directory
                    .EnumerateFiles(posterFolderPath, $"v2_{safeId}_{hash}.*")
                    .FirstOrDefault(path => new FileInfo(path).Length > 0);

                if (string.IsNullOrWhiteSpace(existingFile))
                    return string.Empty;

                return $"ms-appdata:///local/PosterCache/{Uri.EscapeDataString(Path.GetFileName(existingFile))}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void QueuePosterCacheDownload(string id, string url)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                url.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var queueKey = $"{id}|{url}";

            lock (_posterCacheQueueLock)
            {
                if (!_posterCacheQueue.Add(queueKey))
                    return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await TryCachePosterAsync(id, url);
                }
                catch
                {
                }
                finally
                {
                    lock (_posterCacheQueueLock)
                        _posterCacheQueue.Remove(queueKey);
                }
            });
        }

        private static async Task<string> TryCachePosterAsync(string id, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (url.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            await _posterDownloadSemaphore.WaitAsync();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.strem.io/");
                request.Headers.TryAddWithoutValidation("Origin", "https://www.strem.io/");
                request.Headers.TryAddWithoutValidation("Accept", "image/png,image/jpeg,image/jpg,image/gif,image/bmp,image/tiff,image/*;q=0.9,*/*;q=0.5");

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    return string.Empty;

                var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                var safeId = string.IsNullOrWhiteSpace(id) ? "poster" : MakeSafeFileName(id);
                var posterFolderPath = GetPosterCacheFolderPath();
                Directory.CreateDirectory(posterFolderPath);

                var existingFile = Directory
                    .EnumerateFiles(posterFolderPath, $"v2_{safeId}_{hash}.*")
                    .FirstOrDefault(path => new FileInfo(path).Length > 0);

                if (!string.IsNullOrWhiteSpace(existingFile))
                    return $"ms-appdata:///local/PosterCache/{Uri.EscapeDataString(Path.GetFileName(existingFile))}";

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes == null || bytes.Length == 0)
                    return string.Empty;

                var extension = DetectPosterExtension(bytes, mediaType);
                if (string.IsNullOrWhiteSpace(extension))
                    return url;

                var fileName = $"v2_{safeId}_{hash}{extension}";
                var fullPath = Path.Combine(posterFolderPath, fileName);

                await EnsurePosterCacheCapacityAsync(bytes.Length);

                if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
                    await File.WriteAllBytesAsync(fullPath, bytes);

                return $"ms-appdata:///local/PosterCache/{Uri.EscapeDataString(fileName)}";
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                _posterDownloadSemaphore.Release();
            }
        }

        private static string DetectPosterExtension(byte[] bytes, string mediaType)
        {
            if (bytes.Length >= 12)
            {
                if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                    return ".png";

                if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                    return ".jpg";

                if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                    return ".gif";

                if (bytes[0] == 0x42 && bytes[1] == 0x4D)
                    return ".bmp";

                if (bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00)
                    return ".tiff";

                if (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A)
                    return ".tiff";

                if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                    bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                    return string.Empty;

                if (bytes.Length >= 12 &&
                    bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
                {
                    var brand = Encoding.ASCII.GetString(bytes, 8, 4).ToLowerInvariant();
                    if (brand.Contains("avif") || brand.Contains("heic") || brand.Contains("heif"))
                        return string.Empty;
                }
            }

            if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase))
                return ".png";

            if (mediaType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("jpg", StringComparison.OrdinalIgnoreCase))
                return ".jpg";

            if (mediaType.Contains("gif", StringComparison.OrdinalIgnoreCase))
                return ".gif";

            if (mediaType.Contains("bmp", StringComparison.OrdinalIgnoreCase))
                return ".bmp";

            if (mediaType.Contains("tiff", StringComparison.OrdinalIgnoreCase))
                return ".tiff";

            return string.Empty;
        }

        public static async Task<List<Addon>> GetEnabledStreamAddonsAsync(string type)
        {
            await AddonManager.InitializeAsync();

            var addons = AddonManager.GetAddonsSnapshot(enabledOnly: true);
            var checks = addons
                .Select(addon => new
                {
                    Addon = addon,
                    Task = AddonSupportsStreamsAsync(addon, type)
                })
                .ToList();

            await Task.WhenAll(checks.Select(x => x.Task));

            return checks
                .Where(x => x.Task.Result)
                .Select(x => x.Addon)
                .ToList();
        }

        private static async Task<bool> AddonSupportsStreamsAsync(Addon addon, string type)
        {
            if (addon == null || !addon.IsEnabled)
                return false;

            try
            {
                var baseUrl = NormalizeAddonBaseUrl(addon.ManifestUrl);
                using var manifest = await TryGetManifestDocumentAsync(baseUrl);
                if (manifest == null)
                    return false;

                return SupportsStreamResource(manifest.RootElement, type);
            }
            catch
            {
                return false;
            }
        }

        private static bool SupportsStreamResource(JsonElement manifestRoot, string type)
        {
            if (!manifestRoot.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var resource in resources.EnumerateArray())
            {
                if (resource.ValueKind == JsonValueKind.String)
                {
                    var resourceName = resource.GetString() ?? string.Empty;
                    if (string.Equals(resourceName, "stream", StringComparison.OrdinalIgnoreCase))
                        return true;

                    continue;
                }

                if (resource.ValueKind != JsonValueKind.Object)
                    continue;

                var name = resource.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? string.Empty
                    : string.Empty;

                if (!string.Equals(name, "stream", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!resource.TryGetProperty("types", out var typesProp) || typesProp.ValueKind != JsonValueKind.Array)
                    return true;

                foreach (var typeValue in typesProp.EnumerateArray())
                {
                    var parsedType = typeValue.GetString() ?? string.Empty;
                    if (string.Equals(parsedType, type, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }


        private static async Task<List<string>> GetMetadataBaseUrlsAsync(string? preferredBaseUrl = null)
        {
            var results = new List<string>();

            AddCandidate(results, NormalizeAddonBaseUrl(preferredBaseUrl));

            foreach (var candidate in await GetMetadataBaseCandidatesAsync())
                AddCandidate(results, candidate);

            AddCandidate(results, CinemetaBaseUrl);
            return results;
        }

        private static async Task<List<string>> GetMetadataBaseCandidatesAsync()
        {
            await SettingsManager.InitializeAsync();
            await AddonManager.InitializeAsync();

            var enabledAddons = AddonManager.GetAddonsSnapshot(enabledOnly: true);
            var signature = BuildMetadataSignature(SettingsManager.MetadataProvider, enabledAddons);

            lock (_metadataBaseUrlsCacheLock)
            {
                if (string.Equals(_metadataBaseUrlsSignature, signature, StringComparison.Ordinal))
                {
                    return _metadataBaseUrlsCache.ToList();
                }
            }

            var results = new List<string>();

            if (SettingsManager.MetadataProvider == MetadataProviderMode.Cinemeta)
            {
                AddCandidate(results, CinemetaBaseUrl);
            }
            else
            {
                var manifestChecks = enabledAddons
                    .Select(addon =>
                    {
                        var baseUrl = NormalizeAddonBaseUrl(addon.ManifestUrl);
                        return new
                        {
                            BaseUrl = baseUrl,
                            Task = GetManifestDocumentAsync(baseUrl)
                        };
                    })
                    .ToList();

                foreach (var check in manifestChecks)
                {
                    if (string.IsNullOrWhiteSpace(check.BaseUrl))
                        continue;

                    using var manifest = await check.Task;
                    if (manifest == null)
                        continue;

                    if (SupportsMetadataOrDiscover(manifest.RootElement))
                        AddCandidate(results, check.BaseUrl);
                }
            }

            lock (_metadataBaseUrlsCacheLock)
            {
                _metadataBaseUrlsSignature = signature;
                _metadataBaseUrlsCache = results.ToList();
            }

            return results;
        }

        private static string BuildMetadataSignature(MetadataProviderMode providerMode, IReadOnlyList<Addon> enabledAddons)
        {
            var builder = new StringBuilder();
            builder.Append(providerMode);

            foreach (var addon in enabledAddons)
            {
                builder.Append('|');
                builder.Append(NormalizeAddonBaseUrl(addon.ManifestUrl));
            }

            return builder.ToString();
        }

        private static async Task<MetaDetails> TryGetMetaDetailsFromBaseUrlAsync(string type, string id, string metadataBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(metadataBaseUrl))
                return new MetaDetails();

            try
            {
                var json = await _httpClient.GetStringAsync(BuildMetaUrl(metadataBaseUrl, type, id));
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("meta", out var meta))
                    return new MetaDetails();

                var details = new MetaDetails
                {
                    Name = meta.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString() ?? string.Empty
                        : string.Empty,
                    PosterUrl = meta.TryGetProperty("poster", out var posterProp)
                        ? NormalizeUrl(posterProp.GetString(), metadataBaseUrl)
                        : string.Empty,
                    BackgroundUrl = meta.TryGetProperty("background", out var backgroundProp)
                        ? NormalizeUrl(backgroundProp.GetString(), metadataBaseUrl)
                        : string.Empty,
                    LogoUrl = meta.TryGetProperty("logo", out var logoProp)
                        ? NormalizeUrl(logoProp.GetString(), metadataBaseUrl)
                        : string.Empty,
                    Description = meta.TryGetProperty("description", out var descriptionProp)
                        ? descriptionProp.GetString() ?? string.Empty
                        : meta.TryGetProperty("overview", out var overviewProp)
                            ? overviewProp.GetString() ?? string.Empty
                            : string.Empty,
                    ReleaseInfo = meta.TryGetProperty("releaseInfo", out var releaseInfoProp)
                        ? releaseInfoProp.GetString() ?? string.Empty
                        : string.Empty,
                    ImdbRating = ParseImdbRating(meta),
                    Runtime = ParseRuntime(meta),
                    Genres = ParseJoinedArray(meta, "genres", 5),
                    Cast = ParseJoinedArray(meta, "cast", 6),
                    Directors = ParseJoinedArray(meta, "director", 4)
                };

                details.Year = ExtractYear(details.ReleaseInfo);

                if (string.IsNullOrWhiteSpace(details.LogoUrl))
                    details.LogoUrl = BuildMetaHubLogoUrl(id, "medium");

                return details;
            }
            catch
            {
                return new MetaDetails();
            }
        }

        private static bool IsMetaDetailsEmpty(MetaDetails details)
        {
            return string.IsNullOrWhiteSpace(details.Name) &&
                   string.IsNullOrWhiteSpace(details.Description) &&
                   string.IsNullOrWhiteSpace(details.PosterUrl) &&
                   string.IsNullOrWhiteSpace(details.BackgroundUrl) &&
                   string.IsNullOrWhiteSpace(details.LogoUrl) &&
                   string.IsNullOrWhiteSpace(details.ReleaseInfo) &&
                   string.IsNullOrWhiteSpace(details.Year) &&
                   string.IsNullOrWhiteSpace(details.ImdbRating) &&
                   string.IsNullOrWhiteSpace(details.Runtime) &&
                   string.IsNullOrWhiteSpace(details.Genres) &&
                   string.IsNullOrWhiteSpace(details.Cast) &&
                   string.IsNullOrWhiteSpace(details.Directors);
        }

        private static async Task<List<CalendarReleaseEntry>> TryGetCalendarReleaseEntriesFromBaseUrlAsync(MetaItem item, string metadataBaseUrl)
        {
            var results = new List<CalendarReleaseEntry>();

            if (item == null || string.IsNullOrWhiteSpace(metadataBaseUrl))
                return results;

            try
            {
                var json = await _httpClient.GetStringAsync(BuildMetaUrl(metadataBaseUrl, item.Type, item.Id));
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("meta", out var meta))
                    return results;

                var name = meta.TryGetProperty("name", out var nameProp)
                    ? nameProp.GetString() ?? item.Name
                    : item.Name;

                var posterUrl = meta.TryGetProperty("poster", out var posterProp)
                    ? NormalizeUrl(posterProp.GetString(), metadataBaseUrl)
                    : item.PosterUrl;

                var fallbackPosterUrl = !string.IsNullOrWhiteSpace(item.FallbackPosterUrl)
                    ? item.FallbackPosterUrl
                    : BuildMetaHubPosterUrl(item.Id, "large");

                if (string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase))
                {
                    if (!meta.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
                        return results;

                    int fallbackEpisode = 1;

                    foreach (var video in videos.EnumerateArray())
                    {
                        var releaseDate = TryReadDate(video, "released", "releaseDate", "firstAired", "aired", "airDate", "date");
                        if (!releaseDate.HasValue)
                        {
                            fallbackEpisode++;
                            continue;
                        }

                        if (!video.TryGetProperty("id", out var idProp))
                        {
                            fallbackEpisode++;
                            continue;
                        }

                        var videoId = idProp.GetString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(videoId))
                        {
                            fallbackEpisode++;
                            continue;
                        }

                        bool seasonProvided = false;
                        int season = 1;
                        int episode = fallbackEpisode;

                        if (video.TryGetProperty("season", out var seasonProp))
                        {
                            seasonProvided = true;
                            if (seasonProp.ValueKind == JsonValueKind.Number && seasonProp.TryGetInt32(out var seasonNumber))
                                season = seasonNumber;
                            else if (seasonProp.ValueKind == JsonValueKind.String && int.TryParse(seasonProp.GetString(), out var seasonString))
                                season = seasonString;
                        }

                        if (video.TryGetProperty("episode", out var episodeProp))
                        {
                            if (episodeProp.ValueKind == JsonValueKind.Number && episodeProp.TryGetInt32(out var episodeNumber))
                                episode = episodeNumber;
                            else if (episodeProp.ValueKind == JsonValueKind.String && int.TryParse(episodeProp.GetString(), out var episodeString))
                                episode = episodeString;
                        }

                        var title = string.Empty;
                        if (video.TryGetProperty("title", out var titleProp))
                            title = titleProp.GetString() ?? string.Empty;
                        else if (video.TryGetProperty("name", out var videoNameProp))
                            title = videoNameProp.GetString() ?? string.Empty;

                        var normalizedSeason = seasonProvided ? season : 1;
                        if (normalizedSeason < 0)
                            normalizedSeason = 0;

                        results.Add(new CalendarReleaseEntry
                        {
                            Type = item.Type,
                            MetaId = item.Id,
                            VideoId = videoId,
                            Name = name,
                            EpisodeTitle = title,
                            Season = normalizedSeason,
                            Episode = episode <= 0 ? fallbackEpisode : episode,
                            ReleaseDate = releaseDate.Value,
                            PosterUrl = !string.IsNullOrWhiteSpace(posterUrl) ? posterUrl : item.PosterUrl,
                            FallbackPosterUrl = fallbackPosterUrl,
                            SourceBaseUrl = metadataBaseUrl
                        });

                        fallbackEpisode++;
                    }

                    return results
                        .OrderBy(x => x.ReleaseDate)
                        .ThenBy(x => x.Season ?? int.MaxValue)
                        .ThenBy(x => x.Episode ?? int.MaxValue)
                        .ToList();
                }

                var movieReleaseDate = TryReadDate(meta, "released", "releaseDate", "release_info", "premiereDate", "date");
                if (!movieReleaseDate.HasValue)
                    movieReleaseDate = TryParseDate(meta.TryGetProperty("releaseInfo", out var releaseInfoProp) ? releaseInfoProp.GetString() : null);

                if (!movieReleaseDate.HasValue)
                    return results;

                results.Add(new CalendarReleaseEntry
                {
                    Type = item.Type,
                    MetaId = item.Id,
                    Name = name,
                    ReleaseDate = movieReleaseDate.Value,
                    PosterUrl = !string.IsNullOrWhiteSpace(posterUrl) ? posterUrl : item.PosterUrl,
                    FallbackPosterUrl = fallbackPosterUrl,
                    SourceBaseUrl = metadataBaseUrl
                });

                return results;
            }
            catch
            {
                return new List<CalendarReleaseEntry>();
            }
        }

        private static DateTimeOffset? TryReadDate(JsonElement element, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (!element.TryGetProperty(propertyName, out var property))
                    continue;

                if (property.ValueKind == JsonValueKind.String)
                {
                    var value = property.GetString();
                    var parsed = TryParseDate(value);
                    if (parsed.HasValue)
                        return parsed;
                }
                else if (property.ValueKind == JsonValueKind.Number)
                {
                    if (property.TryGetInt64(out var unixValue))
                    {
                        try
                        {
                            return unixValue > 2_000_000_000
                                ? DateTimeOffset.FromUnixTimeMilliseconds(unixValue)
                                : DateTimeOffset.FromUnixTimeSeconds(unixValue);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }

        private static DateTimeOffset? TryParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            if (DateTimeOffset.TryParse(value, out var exact))
                return exact;

            if (DateTime.TryParse(value, out var localDate))
                return new DateTimeOffset(localDate.Date, TimeSpan.Zero);

            if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$") &&
                DateTime.TryParse(value, out var isoDate))
            {
                return new DateTimeOffset(isoDate.Date, TimeSpan.Zero);
            }

            return null;
        }

        private static async Task<List<SeriesEpisodeOption>> TryGetSeriesEpisodesFromBaseUrlAsync(string seriesId, string metadataBaseUrl)
        {
            var results = new List<SeriesEpisodeOption>();

            if (string.IsNullOrWhiteSpace(metadataBaseUrl))
                return results;

            try
            {
                var json = await _httpClient.GetStringAsync(BuildMetaUrl(metadataBaseUrl, "series", seriesId));
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("meta", out var meta))
                    return results;

                if (!meta.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
                    return results;

                int fallbackEpisode = 1;

                foreach (var video in videos.EnumerateArray())
                {
                    if (!video.TryGetProperty("id", out var idProp))
                        continue;

                    var videoId = idProp.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(videoId))
                        continue;

                    bool seasonProvided = false;
                    int season = 1;
                    int episode = fallbackEpisode;

                    if (video.TryGetProperty("season", out var seasonProp))
                    {
                        seasonProvided = true;

                        if (seasonProp.ValueKind == JsonValueKind.Number && seasonProp.TryGetInt32(out var seasonNumber))
                            season = seasonNumber;
                        else if (seasonProp.ValueKind == JsonValueKind.String && int.TryParse(seasonProp.GetString(), out var seasonString))
                            season = seasonString;
                    }

                    if (video.TryGetProperty("episode", out var episodeProp))
                    {
                        if (episodeProp.ValueKind == JsonValueKind.Number && episodeProp.TryGetInt32(out var episodeNumber))
                            episode = episodeNumber;
                        else if (episodeProp.ValueKind == JsonValueKind.String && int.TryParse(episodeProp.GetString(), out var episodeString))
                            episode = episodeString;
                    }

                    var title = string.Empty;

                    if (video.TryGetProperty("title", out var titleProp))
                        title = titleProp.GetString() ?? string.Empty;
                    else if (video.TryGetProperty("name", out var nameProp))
                        title = nameProp.GetString() ?? string.Empty;

                    var normalizedSeason = seasonProvided ? season : 1;
                    if (normalizedSeason < 0)
                        normalizedSeason = 0;

                    var released = ParseEpisodeReleaseDate(video);
                    var thumbnail = ParseEpisodeThumbnail(video, metadataBaseUrl);

                    results.Add(new SeriesEpisodeOption
                    {
                        VideoId = videoId,
                        Season = normalizedSeason,
                        Episode = episode <= 0 ? fallbackEpisode : episode,
                        Title = title,
                        ReleaseDate = released,
                        ThumbnailUrl = thumbnail
                    });

                    fallbackEpisode++;
                }

                return results
                    .OrderBy(x => x.Season)
                    .ThenBy(x => x.Episode)
                    .ThenBy(x => x.Title)
                    .ToList();
            }
            catch
            {
                return new List<SeriesEpisodeOption>();
            }
        }

        private static string ParseEpisodeThumbnail(JsonElement video, string metadataBaseUrl)
        {
            foreach (var propertyName in new[] { "thumbnail", "poster", "background", "image" })
            {
                if (video.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var value = prop.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(value))
                        return NormalizeUrl(value, metadataBaseUrl);
                }
            }

            return string.Empty;
        }

        private static string ParseEpisodeReleaseDate(JsonElement video)
        {
            foreach (var propertyName in new[] { "released", "releaseDate", "firstAired", "airDate", "releaseInfo" })
            {
                if (!video.TryGetProperty(propertyName, out var prop))
                    continue;

                var value = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (DateTimeOffset.TryParse(value, out var date))
                    return date.ToString("MMM d, yyyy");

                return value.Trim();
            }

            return string.Empty;
        }

        private static async Task<string> TryResolveSeriesVideoIdFromBaseUrlAsync(string type, string id, string metadataBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(metadataBaseUrl))
                return id;

            try
            {
                var json = await _httpClient.GetStringAsync(BuildMetaUrl(metadataBaseUrl, type, id));
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("meta", out var meta))
                    return id;

                if (meta.TryGetProperty("behaviorHints", out var behaviorHints) &&
                    behaviorHints.TryGetProperty("defaultVideoId", out var defaultVideoIdProp))
                {
                    var defaultVideoId = defaultVideoIdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(defaultVideoId))
                        return defaultVideoId;
                }

                if (meta.TryGetProperty("videos", out var videos) &&
                    videos.ValueKind == JsonValueKind.Array &&
                    videos.GetArrayLength() > 0)
                {
                    var firstVideo = videos[0];
                    if (firstVideo.TryGetProperty("id", out var videoIdProp))
                    {
                        var videoId = videoIdProp.GetString();
                        if (!string.IsNullOrWhiteSpace(videoId))
                            return videoId;
                    }
                }
            }
            catch
            {
            }

            return id;
        }

        private static string BuildMetaUrl(string metadataBaseUrl, string type, string id)
        {
            return $"{NormalizeAddonBaseUrl(metadataBaseUrl)}/meta/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(id)}.json";
        }

        private static string BuildStreamUrl(string addonBaseUrl, string type, string requestId)
        {
            return $"{NormalizeAddonBaseUrl(addonBaseUrl)}/stream/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(requestId)}.json";
        }

        public static void ClearTransientCaches()
        {
            lock (_metaCacheLock)
            {
                _metaCache.Clear();
            }

            lock (_metadataBaseUrlsCacheLock)
            {
                _metadataBaseUrlsSignature = string.Empty;
                _metadataBaseUrlsCache.Clear();
            }

            lock (_manifestCacheLock)
            {
                _manifestCache.Clear();
            }
        }

        private static string MakeSafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new StringBuilder(value.Length);

            foreach (var c in value)
                cleaned.Append(invalid.Contains(c) ? '_' : c);

            return cleaned.ToString();
        }
    }
}