using Cleario.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class LibraryService
    {
        private const string FileName = "library.json";
        private static readonly SemaphoreSlim _sync = new(1, 1);
        private static List<LibraryEntry>? _cache;

        public static event EventHandler? LibraryChanged;

        public sealed class LibraryEntry
        {
            public string Id { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string PosterUrl { get; set; } = string.Empty;
            public string FallbackPosterUrl { get; set; } = string.Empty;
            public string Year { get; set; } = string.Empty;
            public string ImdbRating { get; set; } = string.Empty;
            public string SourceBaseUrl { get; set; } = string.Empty;
            public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
        }

        public static async Task<IReadOnlyList<LibraryEntry>> GetEntriesAsync(bool forceReload = false)
        {
            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(forceReload);
                return items
                    .OrderByDescending(x => x.AddedUtc)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(CloneEntry)
                    .ToList();
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task<bool> ContainsAsync(MetaItem? item)
        {
            if (item == null)
                return false;

            return await ContainsAsync(item.Type, item.Id);
        }

        public static async Task<bool> ContainsAsync(string? type, string? id)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                return false;

            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(forceReload: false);
                return items.Any(x =>
                    string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _sync.Release();
            }
        }

        public static async Task AddOrUpdateAsync(MetaItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Id))
                return;

            bool changed = false;

            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(forceReload: false);
                var existing = items.FirstOrDefault(x =>
                    string.Equals(x.Type, item.Type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    items.Add(CreateEntry(item));
                    changed = true;
                }
                else
                {
                    existing.Name = !string.IsNullOrWhiteSpace(item.Name) ? item.Name : existing.Name;
                    existing.PosterUrl = GetPreferredPosterUrl(item, existing.PosterUrl);
                    existing.FallbackPosterUrl = GetPreferredFallbackPosterUrl(item, existing.FallbackPosterUrl);
                    existing.Year = !string.IsNullOrWhiteSpace(item.Year) ? item.Year : existing.Year;
                    existing.ImdbRating = !string.IsNullOrWhiteSpace(item.ImdbRating) ? item.ImdbRating : existing.ImdbRating;
                    existing.SourceBaseUrl = !string.IsNullOrWhiteSpace(item.SourceBaseUrl) ? item.SourceBaseUrl : existing.SourceBaseUrl;
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
                LibraryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task RemoveAsync(MetaItem? item)
        {
            if (item == null)
                return;

            await RemoveAsync(item.Type, item.Id);
        }

        public static async Task RemoveAsync(string? type, string? id)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                return;

            bool changed = false;

            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(forceReload: false);
                changed = items.RemoveAll(x =>
                    string.Equals(x.Type, type, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;

                if (changed)
                    await SaveEntriesCoreAsync(items);
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                LibraryChanged?.Invoke(null, EventArgs.Empty);
        }


        public static async Task ResetAsync()
        {
            bool changed = false;
            await _sync.WaitAsync();
            try
            {
                _cache = new List<LibraryEntry>();
                await SaveEntriesCoreAsync(_cache);
                changed = true;
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                LibraryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task<string> ExportJsonAsync()
        {
            await _sync.WaitAsync();
            try
            {
                var items = await LoadEntriesCoreAsync(false);
                return System.Text.Json.JsonSerializer.Serialize(items.Select(CloneEntry).ToList(), new System.Text.Json.JsonSerializerOptions
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
                var items = System.Text.Json.JsonSerializer.Deserialize<List<LibraryEntry>>(json) ?? new List<LibraryEntry>();
                _cache = items
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Type))
                    .GroupBy(x => $"{x.Type.Trim().ToLowerInvariant()}::{x.Id.Trim().ToLowerInvariant()}")
                    .Select(g => g.OrderByDescending(x => x.AddedUtc).First())
                    .OrderByDescending(x => x.AddedUtc)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                await SaveEntriesCoreAsync(_cache);
                changed = true;
            }
            finally
            {
                _sync.Release();
            }

            if (changed)
                LibraryChanged?.Invoke(null, EventArgs.Empty);
        }

        public static async Task<bool> ToggleAsync(MetaItem item)
        {
            if (await ContainsAsync(item))
            {
                await RemoveAsync(item);
                return false;
            }

            await AddOrUpdateAsync(item);
            return true;
        }

        public static MetaItem ToMetaItem(LibraryEntry entry)
        {
            return new MetaItem
            {
                Id = entry.Id,
                Type = entry.Type,
                Name = entry.Name,
                PosterUrl = entry.PosterUrl,
                FallbackPosterUrl = entry.FallbackPosterUrl,
                Poster = !string.IsNullOrWhiteSpace(entry.PosterUrl)
                    ? entry.PosterUrl
                    : (!string.IsNullOrWhiteSpace(entry.FallbackPosterUrl)
                        ? entry.FallbackPosterUrl
                        : CatalogService.PlaceholderPosterUri),
                Year = entry.Year,
                ImdbRating = entry.ImdbRating,
                IsPosterLoading = true,
                SourceBaseUrl = entry.SourceBaseUrl
            };
        }

        private static async Task<List<LibraryEntry>> LoadEntriesCoreAsync(bool forceReload)
        {
            if (_cache != null && !forceReload)
                return _cache;

            _cache = await StorageService.LoadAsync<List<LibraryEntry>>(FileName) ?? new List<LibraryEntry>();
            _cache = _cache
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Type))
                .GroupBy(x => $"{x.Type.Trim().ToLowerInvariant()}::{x.Id.Trim().ToLowerInvariant()}")
                .Select(g => g.OrderByDescending(x => x.AddedUtc).First())
                .ToList();

            return _cache;
        }

        private static async Task SaveEntriesCoreAsync(List<LibraryEntry> items)
        {
            _cache = items
                .OrderByDescending(x => x.AddedUtc)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await StorageService.SaveAsync(FileName, _cache);
        }

        private static LibraryEntry CreateEntry(MetaItem item)
        {
            return new LibraryEntry
            {
                Id = item.Id,
                Type = item.Type,
                Name = item.Name,
                PosterUrl = GetPreferredPosterUrl(item, string.Empty),
                FallbackPosterUrl = GetPreferredFallbackPosterUrl(item, string.Empty),
                Year = item.Year,
                ImdbRating = item.ImdbRating,
                SourceBaseUrl = item.SourceBaseUrl,
                AddedUtc = DateTime.UtcNow
            };
        }

        private static string GetPreferredPosterUrl(MetaItem item, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(item.PosterUrl))
                return item.PosterUrl;

            if (!string.IsNullOrWhiteSpace(item.Poster) &&
                !string.Equals(item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase))
            {
                return item.Poster;
            }

            return fallback;
        }

        private static string GetPreferredFallbackPosterUrl(MetaItem item, string fallback)
        {
            if (!string.IsNullOrWhiteSpace(item.FallbackPosterUrl))
                return item.FallbackPosterUrl;

            if (!string.IsNullOrWhiteSpace(item.Poster) &&
                !string.Equals(item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase))
            {
                return item.Poster;
            }

            return fallback;
        }

        private static LibraryEntry CloneEntry(LibraryEntry entry)
        {
            return new LibraryEntry
            {
                Id = entry.Id,
                Type = entry.Type,
                Name = entry.Name,
                PosterUrl = entry.PosterUrl,
                FallbackPosterUrl = entry.FallbackPosterUrl,
                Year = entry.Year,
                ImdbRating = entry.ImdbRating,
                SourceBaseUrl = entry.SourceBaseUrl,
                AddedUtc = entry.AddedUtc
            };
        }
    }
}
