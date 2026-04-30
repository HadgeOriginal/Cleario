using Cleario.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public static class DiscoverService
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();

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

        static DiscoverService()
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
            public List<string> ExtraOrder { get; set; } = new();
            public string SourceBaseUrl { get; set; } = string.Empty;
            public string SourceName { get; set; } = string.Empty;
        }


public static string BuildCatalogKey(DiscoverCatalogDefinition catalog)
{
    var source = NormalizeAddonBaseUrl(catalog.SourceBaseUrl);
    return $"{source}|{catalog.Type}|{catalog.Id}";
}

public static List<DiscoverCatalogDefinition> OrderCatalogsForHome(
    IEnumerable<DiscoverCatalogDefinition> catalogs,
    IEnumerable<string>? savedOrder)
{
    var orderMap = (savedOrder ?? Enumerable.Empty<string>())
        .Select((key, index) => new { key, index })
        .Where(x => !string.IsNullOrWhiteSpace(x.key))
        .GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().index, StringComparer.OrdinalIgnoreCase);

    return catalogs
        .OrderBy(c => orderMap.TryGetValue(BuildCatalogKey(c), out var explicitIndex) ? explicitIndex : int.MaxValue)
        .ThenBy(c => GetDefaultHomeCatalogRank(c))
        .ThenBy(c => c.SourceName)
        .ThenBy(c => c.Name)
        .ToList();
}

private static int GetDefaultHomeCatalogRank(DiscoverCatalogDefinition catalog)
{
    var label = $"{catalog.Name} {catalog.Id}".ToLowerInvariant();
    var isMovie = string.Equals(catalog.Type, "movie", StringComparison.OrdinalIgnoreCase);
    var isSeries = string.Equals(catalog.Type, "series", StringComparison.OrdinalIgnoreCase);

    if (label.Contains("popular") && isMovie) return 0;
    if (label.Contains("popular") && isSeries) return 1;
    if (label.Contains("featured") && isMovie) return 2;
    if (label.Contains("featured") && isSeries) return 3;
    if (label.Contains("trending") && isMovie) return 4;
    if (label.Contains("trending") && isSeries) return 5;
    return 100;
}

        public static async Task<List<DiscoverCatalogDefinition>> GetDiscoverCatalogsAsync()
        {
            await SettingsManager.InitializeAsync();
            await AddonManager.InitializeAsync();

            var results = new List<DiscoverCatalogDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = new List<(string BaseUrl, string SourceName)>();

            if (SettingsManager.MetadataProvider == MetadataProviderMode.Cinemeta)
                sources.Add((CatalogService.CinemetaBaseUrl, "Cinemeta"));

            foreach (var addon in AddonManager.GetAddonsSnapshot(enabledOnly: true))
                sources.Add((NormalizeAddonBaseUrl(addon.ManifestUrl), addon.Name));

            var tasks = sources
                .Select(source => LoadCatalogsFromSourceAsync(source.BaseUrl, source.SourceName))
                .ToArray();

            var catalogGroups = await Task.WhenAll(tasks);

            foreach (var catalogs in catalogGroups)
            {
                foreach (var catalog in catalogs)
                {
                    var key = $"{catalog.SourceBaseUrl}|{catalog.Type}|{catalog.Id}";
                    if (seen.Add(key))
                        results.Add(catalog);
                }
            }

            foreach (var catalog in TraktService.GetCatalogDefinitions())
            {
                var key = $"{catalog.SourceBaseUrl}|{catalog.Type}|{catalog.Id}";
                if (seen.Add(key))
                    results.Add(catalog);
            }

            return results;
        }

        private static async Task<List<DiscoverCatalogDefinition>> LoadCatalogsFromSourceAsync(
            string addonBaseUrl,
            string sourceName)
        {
            var results = new List<DiscoverCatalogDefinition>();

            if (string.IsNullOrWhiteSpace(addonBaseUrl))
                return results;

            using var manifest = await CatalogService.GetManifestDocumentAsync(addonBaseUrl);
            if (manifest == null)
                return results;

            foreach (var catalog in ParseDiscoverCatalogs(manifest.RootElement))
            {
                catalog.SourceBaseUrl = NormalizeAddonBaseUrl(addonBaseUrl);
                catalog.SourceName = sourceName;
                results.Add(catalog);
            }

            return results;
        }

        public static async Task<List<MetaItem>> GetCatalogItemsAsync(
            string addonBaseUrl,
            DiscoverCatalogDefinition catalog,
            int skip = 0,
            string? searchQuery = null,
            string? genre = null)
        {
            var results = new List<MetaItem>();

            try
            {
                var baseUrl = NormalizeAddonBaseUrl(addonBaseUrl);
                if (string.Equals(baseUrl, TraktService.SourceBaseUrl, StringComparison.OrdinalIgnoreCase))
                    return await TraktService.GetCatalogItemsAsync(catalog, skip);

                var extraPath = BuildExtraPath(catalog, skip, searchQuery, genre);
                var url = $"{baseUrl}/catalog/{catalog.Type}/{catalog.Id}{extraPath}.json";

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
                        ? typeProp.GetString() ?? catalog.Type
                        : catalog.Type;

                    var poster = meta.TryGetProperty("poster", out var posterProp)
                        ? posterProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(poster) &&
                        meta.TryGetProperty("background", out var backgroundProp))
                    {
                        poster = backgroundProp.GetString() ?? string.Empty;
                    }

                    var year = meta.TryGetProperty("year", out var yearProp)
                        ? yearProp.GetRawText().Trim('"')
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(year) &&
                        meta.TryGetProperty("releaseInfo", out var releaseInfoProp))
                    {
                        year = ExtractYearFromReleaseInfo(releaseInfoProp.GetString() ?? string.Empty);
                    }

                    var imdbRating = meta.TryGetProperty("imdbRating", out var imdbProp)
                        ? imdbProp.GetRawText().Trim('"')
                        : string.Empty;

                    results.Add(new MetaItem
                    {
                        Id = id,
                        Name = name,
                        Type = itemType,
                        Poster = string.Empty,
                        PosterUrl = NormalizeUrl(poster, baseUrl),
                        FallbackPosterUrl = CatalogService.BuildMetaHubPosterUrl(id, "large"),
                        Year = year,
                        ImdbRating = imdbRating,
                        IsPosterLoading = true,
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

        private static string ExtractYearFromReleaseInfo(string releaseInfo)
        {
            if (string.IsNullOrWhiteSpace(releaseInfo))
                return string.Empty;

            foreach (var token in releaseInfo.Split(' ', '-', '/', '–'))
            {
                if (token.Length == 4 && int.TryParse(token, out _))
                    return token;
            }

            return string.Empty;
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

                        if (!definition.ExtraOrder.Contains(extraName, StringComparer.OrdinalIgnoreCase))
                            definition.ExtraOrder.Add(extraName);

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

                foreach (var known in new[] { "genre", "search", "skip" })
                {
                    if (extraNames.Contains(known) &&
                        !definition.ExtraOrder.Contains(known, StringComparer.OrdinalIgnoreCase))
                    {
                        definition.ExtraOrder.Add(known);
                    }
                }

                results.Add(definition);
            }

            return results;
        }

        private static string BuildExtraPath(
            DiscoverCatalogDefinition catalog,
            int skip,
            string? searchQuery,
            string? genre)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (catalog.SupportsSearch && !string.IsNullOrWhiteSpace(searchQuery))
                values["search"] = searchQuery.Trim();

            if (catalog.SupportsGenre && !string.IsNullOrWhiteSpace(genre))
                values["genre"] = genre.Trim();

            if (catalog.SupportsSkip && skip > 0)
                values["skip"] = skip.ToString();

            if (values.Count == 0)
                return string.Empty;

            var orderedParts = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in catalog.ExtraOrder)
            {
                if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                    continue;

                orderedParts.Add($"{name}={Uri.EscapeDataString(value)}");
                used.Add(name);
            }

            foreach (var pair in values)
            {
                if (used.Contains(pair.Key))
                    continue;

                orderedParts.Add($"{pair.Key}={Uri.EscapeDataString(pair.Value)}");
            }

            if (orderedParts.Count == 0)
                return string.Empty;

            
            
            return "/" + string.Join("&", orderedParts);
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
    }
}