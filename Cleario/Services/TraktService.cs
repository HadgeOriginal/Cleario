using Cleario.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public sealed class TraktDeviceCodeResult
    {
        public bool Succeeded { get; set; }
        public string Message { get; set; } = string.Empty;
        public string DeviceCode { get; set; } = string.Empty;
        public string UserCode { get; set; } = string.Empty;
        public string VerificationUrl { get; set; } = string.Empty;
        public int ExpiresIn { get; set; }
        public int Interval { get; set; } = 5;
    }

    public sealed class TraktConnectResult
    {
        public bool Succeeded { get; set; }
        public bool Pending { get; set; }
        public string Message { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
    }

    public static class TraktService
    {
        private static string TraktClientId => GetTraktCredential("ClientId");
        private static string TraktClientSecret => GetTraktCredential("ClientSecret");

        private static string GetTraktCredential(string propertyName)
        {
            try
            {
                var credentialTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(assembly =>
                    {
                        try
                        {
                            return assembly.GetTypes();
                        }
                        catch
                        {
                            return Array.Empty<Type>();
                        }
                    })
                    .Where(type => type.Name == "TraktCredentials");

                foreach (var type in credentialTypes)
                {
                    var property = type.GetProperty(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (property?.GetValue(null) is string propertyValue && !string.IsNullOrWhiteSpace(propertyValue))
                        return propertyValue;

                    var field = type.GetField(propertyName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (field?.GetValue(null) is string fieldValue && !string.IsNullOrWhiteSpace(fieldValue))
                        return fieldValue;
                }
            }
            catch
            {
            }

            return string.Empty;
        }
        private const string ApiBaseUrl = "https://api.trakt.tv";
        public const string SourceBaseUrl = "trakt://cleario";

        private static readonly HttpClient _httpClient = CreateHttpClient();
        private static DateTime _lastScrobbleSentUtc = DateTime.MinValue;
        private static string _lastScrobbleSignature = string.Empty;

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 8,
                UseCookies = false
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        static TraktService()
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Cleario/0.2.0");
        }

        public static bool HasAppCredentials =>
            !string.IsNullOrWhiteSpace(TraktClientId) &&
            !string.IsNullOrWhiteSpace(TraktClientSecret) &&
            !TraktClientId.Contains("PUT_TRAKT", StringComparison.OrdinalIgnoreCase) &&
            !TraktClientSecret.Contains("PUT_TRAKT", StringComparison.OrdinalIgnoreCase);

        public static bool IsConnected => !string.IsNullOrWhiteSpace(SettingsManager.TraktAccessToken);

        public static async Task<TraktDeviceCodeResult> StartDeviceAuthorizationAsync()
        {
            await SettingsManager.InitializeAsync();

            if (!HasAppCredentials)
            {
                return new TraktDeviceCodeResult
                {
                    Succeeded = false,
                    Message = "Trakt is not configured in this build yet."
                };
            }

            try
            {
                using var response = await PostJsonAsync("/oauth/device/code", new
                {
                    client_id = TraktClientId
                }, authenticated: false);

                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    return new TraktDeviceCodeResult
                    {
                        Succeeded = false,
                        Message = $"Trakt did not return a device code. {response.StatusCode}"
                    };
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var result = new TraktDeviceCodeResult
                {
                    Succeeded = true,
                    DeviceCode = GetString(root, "device_code"),
                    UserCode = GetString(root, "user_code"),
                    VerificationUrl = GetString(root, "verification_url"),
                    ExpiresIn = GetInt(root, "expires_in", 600),
                    Interval = Math.Max(3, GetInt(root, "interval", 5)),
                    Message = "Enter the code on Trakt to connect Cleario."
                };

                return result;
            }
            catch (Exception ex)
            {
                CrashLogger.LogException(ex, "Trakt device authorization failed");
                return new TraktDeviceCodeResult
                {
                    Succeeded = false,
                    Message = "Could not start Trakt authorization."
                };
            }
        }

        public static async Task<TraktConnectResult> PollDeviceAuthorizationAsync(string deviceCode)
        {
            if (string.IsNullOrWhiteSpace(deviceCode))
            {
                return new TraktConnectResult
                {
                    Succeeded = false,
                    Message = "No Trakt device code is active."
                };
            }

            try
            {
                using var response = await PostJsonAsync("/oauth/device/token", new
                {
                    code = deviceCode,
                    client_id = TraktClientId,
                    client_secret = TraktClientSecret
                }, authenticated: false);

                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var pending = statusCode == 400 || statusCode == 429 || json.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase) || json.Contains("slow_down", StringComparison.OrdinalIgnoreCase);
                    var message = pending ? "Waiting for Trakt authorization..." : "Trakt connection was not completed.";

                    if (statusCode == 410)
                        message = "Trakt connection expired. Try connecting again.";
                    else if (statusCode == 418)
                        message = "Trakt connection was denied in Trakt.";
                    else if (statusCode == 409)
                        message = "This Trakt code was already used. Try connecting again.";
                    else if (statusCode == 404)
                        message = "Trakt did not recognize this code. Try connecting again.";

                    return new TraktConnectResult
                    {
                        Succeeded = false,
                        Pending = pending,
                        Message = message
                    };
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                SettingsManager.TraktAccessToken = GetString(root, "access_token");
                SettingsManager.TraktRefreshToken = GetString(root, "refresh_token");
                SettingsManager.TraktTokenCreatedAtUnix = GetLong(root, "created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                SettingsManager.TraktTokenExpiresInSeconds = GetLong(root, "expires_in", 7776000);
                SettingsManager.TraktConnected = !string.IsNullOrWhiteSpace(SettingsManager.TraktAccessToken);
                SettingsManager.TraktScrobblingEnabled = true;
                SettingsManager.TraktWatchHistoryCatalogEnabled = true;
                SettingsManager.TraktWatchlistCatalogEnabled = true;

                var username = await LoadUsernameAsync();
                SettingsManager.TraktUsername = username;
                await SettingsManager.SaveAsync();

                return new TraktConnectResult
                {
                    Succeeded = true,
                    Username = username,
                    Message = string.IsNullOrWhiteSpace(username) ? "Trakt connected." : $"Trakt connected as {username}."
                };
            }
            catch (Exception ex)
            {
                CrashLogger.LogException(ex, "Trakt device authorization polling failed");
                return new TraktConnectResult
                {
                    Succeeded = false,
                    Message = "Could not finish Trakt connection."
                };
            }
        }

        public static async Task DisconnectAsync()
        {
            SettingsManager.TraktConnected = false;
            SettingsManager.TraktAccessToken = string.Empty;
            SettingsManager.TraktRefreshToken = string.Empty;
            SettingsManager.TraktUsername = string.Empty;
            SettingsManager.TraktTokenCreatedAtUnix = 0;
            SettingsManager.TraktTokenExpiresInSeconds = 0;
            await SettingsManager.SaveAsync();
        }

        public static IEnumerable<DiscoverService.DiscoverCatalogDefinition> GetCatalogDefinitions()
        {
            if (!SettingsManager.TraktConnected)
                yield break;

            if (SettingsManager.TraktWatchHistoryCatalogEnabled)
            {
                yield return CreateCatalog("movie", "trakt-history-movies", "Trakt watch history");
                yield return CreateCatalog("series", "trakt-history-series", "Trakt watch history");
            }

            if (SettingsManager.TraktWatchlistCatalogEnabled)
            {
                yield return CreateCatalog("movie", "trakt-watchlist-movies", "Trakt watchlist");
                yield return CreateCatalog("series", "trakt-watchlist-series", "Trakt watchlist");
            }
        }

        public static async Task<List<MetaItem>> GetCatalogItemsAsync(DiscoverService.DiscoverCatalogDefinition catalog, int skip)
        {
            await SettingsManager.InitializeAsync();

            if (!SettingsManager.TraktConnected)
                return new List<MetaItem>();

            await EnsureValidAccessTokenAsync();

            var path = catalog.Id switch
            {
                "trakt-history-movies" => "/sync/history/movies",
                "trakt-history-series" => "/sync/history/shows",
                "trakt-watchlist-movies" => "/sync/watchlist/movies",
                "trakt-watchlist-series" => "/sync/watchlist/shows",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(path))
                return new List<MetaItem>();

            var page = Math.Max(1, (skip / 20) + 1);
            using var request = CreateRequest(HttpMethod.Get, $"{path}?page={page}&limit=20&extended=full", authenticated: true);
            using var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return new List<MetaItem>();

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return new List<MetaItem>();

            var results = new List<MetaItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var mediaProp = string.Equals(catalog.Type, "series", StringComparison.OrdinalIgnoreCase) ? "show" : "movie";
                if (!item.TryGetProperty(mediaProp, out var media) || media.ValueKind != JsonValueKind.Object)
                    continue;

                var mapped = MapTraktMediaToMetaItem(media, catalog.Type);
                if (mapped == null)
                    continue;

                if (seen.Add(mapped.Id))
                    results.Add(mapped);
            }

            return results;
        }

        public static async Task<int> SyncContinueWatchingAsync()
        {
            await SettingsManager.InitializeAsync();

            if (!SettingsManager.TraktConnected)
                return 0;

            await EnsureValidAccessTokenAsync();

            try
            {
                using var request = CreateRequest(HttpMethod.Get, "/sync/playback?extended=full", authenticated: true);
                using var response = await _httpClient.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    return 0;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return 0;

                var entries = new List<HistoryService.HistoryEntry>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var entry = MapPlaybackItemToHistoryEntry(item);
                    if (entry != null)
                        entries.Add(entry);
                }

                return await HistoryService.ImportContinueWatchingEntriesAsync(entries);
            }
            catch (Exception ex)
            {
                CrashLogger.LogException(ex, "Trakt continue watching sync failed");
                return 0;
            }
        }

        private static HistoryService.HistoryEntry? MapPlaybackItemToHistoryEntry(JsonElement item)
        {
            var type = GetString(item, "type");
            var progress = GetDouble(item, "progress", 0);
            if (progress <= 0.1 || progress >= 99.5)
                return null;

            if (string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase))
            {
                if (!item.TryGetProperty("show", out var show) || show.ValueKind != JsonValueKind.Object)
                    return null;
                if (!item.TryGetProperty("episode", out var episode) || episode.ValueKind != JsonValueKind.Object)
                    return null;

                var id = GetBestTraktId(show);
                if (string.IsNullOrWhiteSpace(id))
                    return null;

                var title = GetString(show, "title");
                var episodeTitle = GetString(episode, "title");
                var season = GetInt(episode, "season", 0);
                var number = GetInt(episode, "number", 1);
                var durationMs = BuildDurationMs(GetInt(episode, "runtime", 0), 45);
                var positionMs = BuildPositionMs(durationMs, progress);
                var poster = id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? CatalogService.BuildMetaHubPosterUrl(id, "large") : CatalogService.PlaceholderPosterUri;

                return new HistoryService.HistoryEntry
                {
                    Id = id,
                    Type = "series",
                    VideoId = $"{id}:{season}:{number}",
                    SeasonNumber = season,
                    EpisodeNumber = number,
                    EpisodeTitle = episodeTitle,
                    Name = title,
                    PosterUrl = poster,
                    FallbackPosterUrl = poster,
                    Year = GetRawString(show, "year"),
                    SourceBaseUrl = CatalogService.CinemetaBaseUrl,
                    PositionMs = positionMs,
                    DurationMs = durationMs,
                    IsWatched = false,
                    IsDismissed = false,
                    LastPlayedUtc = ParseTraktDateUtc(GetString(item, "paused_at"))
                };
            }

            if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
            {
                if (!item.TryGetProperty("movie", out var movie) || movie.ValueKind != JsonValueKind.Object)
                    return null;

                var id = GetBestTraktId(movie);
                if (string.IsNullOrWhiteSpace(id))
                    return null;

                var durationMs = BuildDurationMs(GetInt(movie, "runtime", 0), 100);
                var positionMs = BuildPositionMs(durationMs, progress);
                var poster = id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? CatalogService.BuildMetaHubPosterUrl(id, "large") : CatalogService.PlaceholderPosterUri;

                return new HistoryService.HistoryEntry
                {
                    Id = id,
                    Type = "movie",
                    VideoId = id,
                    Name = GetString(movie, "title"),
                    PosterUrl = poster,
                    FallbackPosterUrl = poster,
                    Year = GetRawString(movie, "year"),
                    SourceBaseUrl = CatalogService.CinemetaBaseUrl,
                    PositionMs = positionMs,
                    DurationMs = durationMs,
                    IsWatched = false,
                    IsDismissed = false,
                    LastPlayedUtc = ParseTraktDateUtc(GetString(item, "paused_at"))
                };
            }

            return null;
        }

        private static string GetBestTraktId(JsonElement media)
        {
            if (!media.TryGetProperty("ids", out var ids) || ids.ValueKind != JsonValueKind.Object)
                return string.Empty;

            var imdb = GetString(ids, "imdb");
            if (!string.IsNullOrWhiteSpace(imdb))
                return imdb;

            var slug = GetString(ids, "slug");
            if (!string.IsNullOrWhiteSpace(slug))
                return slug;

            return GetRawString(ids, "trakt");
        }

        private static long BuildDurationMs(int runtimeMinutes, int fallbackMinutes)
        {
            return TimeSpan.FromMinutes(runtimeMinutes > 0 ? runtimeMinutes : fallbackMinutes).Ticks / TimeSpan.TicksPerMillisecond;
        }

        private static long BuildPositionMs(long durationMs, double progress)
        {
            return (long)Math.Clamp(durationMs * (progress / 100.0), 5_000, Math.Max(5_000, durationMs - 5_000));
        }

        private static DateTime ParseTraktDateUtc(string value)
        {
            if (DateTimeOffset.TryParse(value, out var parsed))
                return parsed.UtcDateTime;

            return DateTime.UtcNow;
        }

        public static async Task ScrobbleStartAsync(CatalogService.StreamOption? stream, long positionMs, long durationMs)
        {
            await SendScrobbleAsync("start", stream, positionMs, durationMs, throttle: true);
        }

        public static async Task ScrobblePauseAsync(CatalogService.StreamOption? stream, long positionMs, long durationMs)
        {
            await SendScrobbleAsync("pause", stream, positionMs, durationMs, throttle: false);
        }

        public static async Task ScrobbleStopAsync(CatalogService.StreamOption? stream, long positionMs, long durationMs)
        {
            await SendScrobbleAsync("stop", stream, positionMs, durationMs, throttle: false);
        }

        private static async Task SendScrobbleAsync(string action, CatalogService.StreamOption? stream, long positionMs, long durationMs, bool throttle)
        {
            if (stream == null || !SettingsManager.TraktConnected || !SettingsManager.TraktScrobblingEnabled || durationMs <= 0)
                return;

            var progress = Math.Clamp(durationMs <= 0 ? 0 : (double)positionMs / durationMs * 100.0, 0, 100);
            if (progress <= 0.1 && !string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
                return;

            var body = BuildScrobbleBody(stream, progress);
            if (body == null)
                return;

            var signature = $"{action}|{stream.ContentType}|{stream.ContentId}|{stream.SeasonNumber}|{stream.EpisodeNumber}|{Math.Round(progress)}";
            if (throttle && string.Equals(signature, _lastScrobbleSignature, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _lastScrobbleSentUtc < TimeSpan.FromSeconds(20))
                return;

            try
            {
                await EnsureValidAccessTokenAsync();
                using var response = await PostJsonAsync($"/scrobble/{action}", body, authenticated: true);
                if (response.IsSuccessStatusCode)
                {
                    _lastScrobbleSignature = signature;
                    _lastScrobbleSentUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                CrashLogger.LogException(ex, $"Trakt scrobble {action} failed");
            }
        }

        private static object? BuildScrobbleBody(CatalogService.StreamOption stream, double progress)
        {
            var title = !string.IsNullOrWhiteSpace(stream.ContentName) ? stream.ContentName : stream.DisplayName;
            var year = ParseYear(stream.Year);
            var ids = BuildIds(stream.ContentId);

            if (string.Equals(stream.ContentType, "series", StringComparison.OrdinalIgnoreCase))
            {
                var show = new Dictionary<string, object?>
                {
                    ["title"] = CleanSeriesTitle(title),
                    ["ids"] = ids
                };

                if (year.HasValue)
                    show["year"] = year.Value;

                return new Dictionary<string, object?>
                {
                    ["show"] = show,
                    ["episode"] = new Dictionary<string, object?>
                    {
                        ["season"] = Math.Max(0, stream.SeasonNumber ?? 0),
                        ["number"] = Math.Max(1, stream.EpisodeNumber ?? 1)
                    },
                    ["progress"] = Math.Round(progress, 2),
                    ["app_version"] = UpdateService.GetCurrentVersionText(),
                    ["app_date"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
                };
            }

            var movie = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["ids"] = ids
            };

            if (year.HasValue)
                movie["year"] = year.Value;

            return new Dictionary<string, object?>
            {
                ["movie"] = movie,
                ["progress"] = Math.Round(progress, 2),
                ["app_version"] = UpdateService.GetCurrentVersionText(),
                ["app_date"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
            };
        }

        private static Dictionary<string, object?> BuildIds(string id)
        {
            var ids = new Dictionary<string, object?>();
            if (string.IsNullOrWhiteSpace(id))
                return ids;

            if (id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                ids["imdb"] = id;
            else if (int.TryParse(id, out var traktId))
                ids["trakt"] = traktId;
            else
                ids["slug"] = id;

            return ids;
        }

        private static async Task<string> LoadUsernameAsync()
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "/users/settings", authenticated: true);
                using var response = await _httpClient.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    return string.Empty;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
                    return GetString(user, "username");
            }
            catch
            {
            }

            return string.Empty;
        }

        private static async Task EnsureValidAccessTokenAsync()
        {
            if (!SettingsManager.TraktConnected || string.IsNullOrWhiteSpace(SettingsManager.TraktRefreshToken))
                return;

            var created = SettingsManager.TraktTokenCreatedAtUnix <= 0
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.FromUnixTimeSeconds(SettingsManager.TraktTokenCreatedAtUnix);

            var expiresAt = created.AddSeconds(Math.Max(0, SettingsManager.TraktTokenExpiresInSeconds));
            if (DateTimeOffset.UtcNow < expiresAt.AddHours(-24))
                return;

            using var response = await PostJsonAsync("/oauth/token", new
            {
                refresh_token = SettingsManager.TraktRefreshToken,
                client_id = TraktClientId,
                client_secret = TraktClientSecret,
                redirect_uri = "urn:ietf:wg:oauth:2.0:oob",
                grant_type = "refresh_token"
            }, authenticated: false);

            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            SettingsManager.TraktAccessToken = GetString(root, "access_token");
            SettingsManager.TraktRefreshToken = GetString(root, "refresh_token");
            SettingsManager.TraktTokenCreatedAtUnix = GetLong(root, "created_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            SettingsManager.TraktTokenExpiresInSeconds = GetLong(root, "expires_in", 7776000);
            SettingsManager.TraktConnected = !string.IsNullOrWhiteSpace(SettingsManager.TraktAccessToken);
            await SettingsManager.SaveAsync();
        }

        private static DiscoverService.DiscoverCatalogDefinition CreateCatalog(string type, string id, string name)
        {
            return new DiscoverService.DiscoverCatalogDefinition
            {
                Type = type,
                Id = id,
                Name = name,
                SourceBaseUrl = SourceBaseUrl,
                SourceName = "Trakt"
            };
        }

        private static MetaItem? MapTraktMediaToMetaItem(JsonElement media, string type)
        {
            var title = GetString(media, "title");
            if (string.IsNullOrWhiteSpace(title))
                title = GetString(media, "name");

            if (string.IsNullOrWhiteSpace(title))
                return null;

            var year = GetRawString(media, "year");
            string id = string.Empty;
            if (media.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Object)
            {
                id = GetString(ids, "imdb");
                if (string.IsNullOrWhiteSpace(id))
                    id = GetRawString(ids, "trakt");
                if (string.IsNullOrWhiteSpace(id))
                    id = GetString(ids, "slug");
            }

            if (string.IsNullOrWhiteSpace(id))
                id = title;

            var poster = id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                ? CatalogService.BuildMetaHubPosterUrl(id, "large")
                : CatalogService.PlaceholderPosterUri;

            return new MetaItem
            {
                Id = id,
                Name = title,
                Type = type,
                Poster = poster,
                PosterUrl = poster,
                FallbackPosterUrl = id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? CatalogService.BuildMetaHubPosterUrl(id, "large") : CatalogService.PlaceholderPosterUri,
                Year = year,
                SourceBaseUrl = CatalogService.CinemetaBaseUrl,
                IsPosterLoading = true
            };
        }

        private static async Task<HttpResponseMessage> PostJsonAsync(string path, object body, bool authenticated)
        {
            using var request = CreateRequest(HttpMethod.Post, path, authenticated);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return await _httpClient.SendAsync(request);
        }

        private static HttpRequestMessage CreateRequest(HttpMethod method, string path, bool authenticated)
        {
            var request = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ApiBaseUrl + path);
            request.Headers.TryAddWithoutValidation("trakt-api-version", "2");
            request.Headers.TryAddWithoutValidation("trakt-api-key", TraktClientId);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (authenticated && !string.IsNullOrWhiteSpace(SettingsManager.TraktAccessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SettingsManager.TraktAccessToken);

            return request;
        }

        private static string CleanSeriesTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            return System.Text.RegularExpressions.Regex.Replace(title, @"\s*\((\d+x\d{2}|Special\s+\d{2}|S\d+E\d+)\)\s*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        }

        private static int? ParseYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            foreach (var part in value.Split(' ', '-', '/', '–'))
            {
                if (part.Length == 4 && int.TryParse(part, out var year))
                    return year;
            }

            return null;
        }

        private static string GetString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
                return string.Empty;

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText().Trim('"');
        }

        private static string GetRawString(JsonElement element, string property)
        {
            if (!element.TryGetProperty(property, out var value))
                return string.Empty;

            if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
                return string.Empty;

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText().Trim('"');
        }

        private static double GetDouble(JsonElement element, string property, double fallback)
        {
            if (!element.TryGetProperty(property, out var value))
                return fallback;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;

            return double.TryParse(value.GetRawText().Trim('"'), out number) ? number : fallback;
        }

        private static int GetInt(JsonElement element, string property, int fallback)
        {
            if (!element.TryGetProperty(property, out var value))
                return fallback;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            return int.TryParse(value.GetRawText().Trim('"'), out number) ? number : fallback;
        }

        private static long GetLong(JsonElement element, string property, long fallback)
        {
            if (!element.TryGetProperty(property, out var value))
                return fallback;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;

            return long.TryParse(value.GetRawText().Trim('"'), out number) ? number : fallback;
        }
    }
}
