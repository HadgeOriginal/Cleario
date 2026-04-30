using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public sealed class DiscordPresenceActivityRequest
    {
        public string PageKey { get; set; } = string.Empty;
        public string PageName { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string ContentTitle { get; set; } = string.Empty;
        public string EpisodeTitle { get; set; } = string.Empty;
        public string EpisodeCode { get; set; } = string.Empty;
        public string PosterUrl { get; set; } = string.Empty;
        public bool IsPlayback { get; set; }
        public bool IsPlaying { get; set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
    }

    public static class DiscordRichPresenceService
    {
        private const int OpHandshake = 0;
        private const int OpFrame = 1;
        private const int OpClose = 2;
        private const int OpPing = 3;
        private const int OpPong = 4;
        private const string ClearioDiscordApplicationId = "1497740198434443446";
        private const string ClearioLogoAssetKey = "cleario_logo";

        private static readonly SemaphoreSlim Sync = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static NamedPipeClientStream? _pipe;
        private static CancellationTokenSource? _readCancellation;
        private static DiscordPresenceActivityRequest? _lastRequest;
        private static string _lastStatus = "Discord Rich Presence is off.";
        private static DateTimeOffset _lastActivityUpdateUtc = DateTimeOffset.MinValue;
        private static string _lastActivitySignature = string.Empty;
        private static long _lastPlaybackPositionMs = -1;
        private static readonly TimeSpan RichPresenceUpdateInterval = TimeSpan.FromSeconds(5);

        public static string LastStatus => _lastStatus;

        public static async Task InitializeAsync()
        {
            await SettingsManager.InitializeAsync();
            if (!SettingsManager.DiscordRichPresenceEnabled)
            {
                _lastStatus = "Discord Rich Presence is off.";
                return;
            }

            await SetPageActivityAsync("Home", "Home");
        }

        public static async Task RefreshFromSettingsAsync()
        {
            await SettingsManager.InitializeAsync(forceReload: true);

            if (!SettingsManager.DiscordRichPresenceEnabled)
            {
                await ClearActivityAsync();
                Disconnect();
                _lastStatus = "Discord Rich Presence is off.";
                return;
            }

            if (_lastRequest != null)
                await SetActivityAsync(_lastRequest, force: true);
        }

        public static async Task SetPageActivityAsync(string pageKey, string pageName, string details = "", string state = "", string posterUrl = "", string contentTitle = "")
        {
            var request = new DiscordPresenceActivityRequest
            {
                PageKey = pageKey,
                PageName = pageName,
                Details = details,
                State = state,
                ContentTitle = contentTitle,
                PosterUrl = posterUrl,
                IsPlayback = false,
                IsPlaying = false
            };

            await SetActivityAsync(request);
        }

        public static async Task SetPlaybackActivityAsync(DiscordPresenceActivityRequest request)
        {
            request.PageKey = "Player";
            request.PageName = "Player";
            request.IsPlayback = true;
            await SetActivityAsync(request);
        }

        public static async Task ClearActivityAsync()
        {
            await Sync.WaitAsync();
            try
            {
                if (!await EnsureConnectedAsync())
                    return;

                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = (object?)null
                    },
                    nonce = Guid.NewGuid().ToString("N")
                };

                await WriteFrameAsync(OpFrame, JsonSerializer.Serialize(payload, JsonOptions));
                _lastActivitySignature = string.Empty;
                _lastPlaybackPositionMs = -1;
                _lastStatus = "Discord activity cleared.";
            }
            catch (Exception ex)
            {
                _lastStatus = $"Discord clear failed: {ex.Message}";
                CrashLogger.LogException(ex, "DiscordRichPresenceService.ClearActivityAsync");
                Disconnect();
            }
            finally
            {
                Sync.Release();
            }
        }

        private static async Task SetActivityAsync(DiscordPresenceActivityRequest request, bool force = false)
        {
            _lastRequest = request;

            await SettingsManager.InitializeAsync();

            if (!SettingsManager.DiscordRichPresenceEnabled)
            {
                _lastStatus = "Discord Rich Presence is off.";
                return;
            }
            if (!IsConfiguredClientId(ClearioDiscordApplicationId))
            {
                _lastStatus = "Discord Rich Presence needs the Cleario Discord application ID in code.";
                return;
            }

            if (!IsPageEnabled(request.PageKey))
            {
                await ClearActivityAsync();
                _lastStatus = $"{request.PageName} is disabled for Discord Rich Presence.";
                return;
            }

            var activitySignature = BuildActivitySignature(request);
            var now = DateTimeOffset.UtcNow;
            var positionJumped = request.IsPlayback && _lastPlaybackPositionMs >= 0 && Math.Abs(request.PositionMs - _lastPlaybackPositionMs) > 15000;
            var canThrottleUpdate = !force && !positionJumped && string.Equals(activitySignature, _lastActivitySignature, StringComparison.Ordinal);

            if (canThrottleUpdate && now - _lastActivityUpdateUtc < RichPresenceUpdateInterval)
                return;

            await Sync.WaitAsync();
            try
            {
                if (!await EnsureConnectedAsync())
                    return;

                var activity = BuildActivity(request);
                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity
                    },
                    nonce = Guid.NewGuid().ToString("N")
                };

                await WriteFrameAsync(OpFrame, JsonSerializer.Serialize(payload, JsonOptions));
                _lastActivityUpdateUtc = DateTimeOffset.UtcNow;
                _lastActivitySignature = activitySignature;
                if (request.IsPlayback)
                    _lastPlaybackPositionMs = request.PositionMs;
                else
                    _lastPlaybackPositionMs = -1;
                _lastStatus = "Discord Rich Presence is active.";
            }
            catch (Exception ex)
            {
                _lastStatus = $"Discord update failed: {ex.Message}";
                CrashLogger.LogException(ex, "DiscordRichPresenceService.SetActivityAsync");
                Disconnect();
            }
            finally
            {
                Sync.Release();
            }
        }

        private static object BuildActivity(DiscordPresenceActivityRequest request)
        {
            var details = BuildDetails(request);
            var state = BuildState(request);
            var assets = BuildAssets(request);
            var timestamps = BuildTimestamps(request);
            return new
            {
                details = string.IsNullOrWhiteSpace(details) ? null : details,
                state = string.IsNullOrWhiteSpace(state) ? null : state,
                assets,
                timestamps
            };
        }

        private static string BuildDetails(DiscordPresenceActivityRequest request)
        {
            if (request.IsPlayback)
                return BuildPlaybackMainLine(request);

            if (!SettingsManager.DiscordShowClearioRunning)
                return string.IsNullOrWhiteSpace(request.Details) ? request.PageName : request.Details;

            if (!string.IsNullOrWhiteSpace(request.Details))
                return request.Details;

            return "Cleario is open";
        }

        private static string BuildState(DiscordPresenceActivityRequest request)
        {
            if (request.IsPlayback)
                return BuildPlaybackStatusLine(request);

            if (!string.IsNullOrWhiteSpace(request.State))
                return request.State;

            return $"On {request.PageName}";
        }

        private static string BuildPlaybackMainLine(DiscordPresenceActivityRequest request)
        {
            var episode = BuildEpisodeLine(request);
            var title = SettingsManager.DiscordShowTitle ? request.ContentTitle?.Trim() ?? string.Empty : string.Empty;
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(title))
                parts.Add(SettingsManager.DiscordShowClearioRunning ? $"Watching {title}" : title);
            else if (SettingsManager.DiscordShowClearioRunning)
                parts.Add("Watching");

            if (!string.IsNullOrWhiteSpace(episode) && !parts.Contains(episode, StringComparer.OrdinalIgnoreCase))
                parts.Add(episode);

            return string.Join(" - ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static string BuildPlaybackStatusLine(DiscordPresenceActivityRequest request)
        {
            if (SettingsManager.DiscordShowPlaybackTimestamp && request.DurationMs > 0)
            {
                var progress = $"{FormatTime(request.PositionMs)} / {FormatTime(request.DurationMs)}";
                return request.IsPlaying ? progress : $"Paused at {progress}";
            }

            return string.IsNullOrWhiteSpace(request.State) ? string.Empty : request.State;
        }

        private static string BuildEpisodeLine(DiscordPresenceActivityRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.EpisodeTitle) && !string.IsNullOrWhiteSpace(request.EpisodeCode))
                return $"{request.EpisodeCode} - {request.EpisodeTitle}";

            if (!string.IsNullOrWhiteSpace(request.EpisodeTitle))
                return request.EpisodeTitle;

            if (!string.IsNullOrWhiteSpace(request.EpisodeCode))
                return request.EpisodeCode;

            return string.Empty;
        }

        private static object? BuildTimestamps(DiscordPresenceActivityRequest request)
        {
            return null;
        }


        private static string BuildActivitySignature(DiscordPresenceActivityRequest request)
        {
            return string.Join("|", new[]
            {
                request.PageKey ?? string.Empty,
                request.PageName ?? string.Empty,
                request.Details ?? string.Empty,
                request.State ?? string.Empty,
                request.ContentTitle ?? string.Empty,
                request.EpisodeTitle ?? string.Empty,
                request.EpisodeCode ?? string.Empty,
                request.PosterUrl ?? string.Empty,
                request.IsPlayback ? "1" : "0",
                request.IsPlaying ? "1" : "0",
                Math.Max(0, request.DurationMs).ToString(),
                SettingsManager.DiscordShowPlaybackTimestamp ? "t1" : "t0",
                SettingsManager.DiscordShowPoster ? "p1" : "p0",
                SettingsManager.DiscordShowTitle ? "title1" : "title0",
                SettingsManager.DiscordShowClearioRunning ? "app1" : "app0"
            });
        }

        private static object? BuildAssets(DiscordPresenceActivityRequest request)
        {
            var showPoster = SettingsManager.DiscordShowPoster && !string.IsNullOrWhiteSpace(request.PosterUrl) && (request.IsPlayback || string.Equals(request.PageKey, "Details", StringComparison.OrdinalIgnoreCase));
            var showLogo = ShouldShowClearioLogo(request);

            if (!showPoster && !showLogo)
                return null;

            var contentTitle = !string.IsNullOrWhiteSpace(request.ContentTitle)
                ? request.ContentTitle
                : (!string.IsNullOrWhiteSpace(request.Details) ? request.Details : "Cleario");

            string? largeImage = null;
            string? largeText = null;
            string? smallImage = null;
            string? smallText = null;

            if (showPoster)
            {
                largeImage = request.PosterUrl;
                largeText = contentTitle;
            }
            else if (showLogo)
            {
                largeImage = ClearioLogoAssetKey;
                largeText = "Cleario";
            }

            if (showLogo && largeImage != ClearioLogoAssetKey)
            {
                smallImage = ClearioLogoAssetKey;
                smallText = "Cleario";
            }

            return new
            {
                large_image = largeImage,
                large_text = largeText,
                small_image = smallImage,
                small_text = smallText
            };
        }

        private static bool ShouldShowClearioLogo(DiscordPresenceActivityRequest request)
        {
            return request.IsPlayback || SettingsManager.DiscordShowClearioRunning;
        }

        private static async Task<bool> EnsureConnectedAsync()
        {
            if (_pipe?.IsConnected == true)
                return true;

            Disconnect();
            var clientId = ClearioDiscordApplicationId.Trim();
            if (!IsConfiguredClientId(clientId))
            {
                _lastStatus = "Discord Rich Presence needs the Cleario Discord application ID in code.";
                return false;
            }

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                    using var connectTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                    await pipe.ConnectAsync(connectTimeout.Token);

                    _pipe = pipe;
                    _readCancellation = new CancellationTokenSource();

                    var handshake = new
                    {
                        v = 1,
                        client_id = clientId
                    };

                    await WriteFrameAsync(OpHandshake, JsonSerializer.Serialize(handshake, JsonOptions));
                    _ = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
                    _lastStatus = "Connected to Discord.";
                    return true;
                }
                catch
                {
                }
            }

            _lastStatus = "Discord is not running or the Discord IPC pipe is unavailable.";
            return false;
        }

        private static async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (_pipe?.IsConnected == true && !cancellationToken.IsCancellationRequested)
                {
                    var header = new byte[8];
                    if (!await ReadExactAsync(header, cancellationToken))
                        break;

                    var opCode = BitConverter.ToInt32(header, 0);
                    var length = BitConverter.ToInt32(header, 4);
                    if (length < 0 || length > 1024 * 1024)
                        break;

                    var payload = new byte[length];
                    if (length > 0 && !await ReadExactAsync(payload, cancellationToken))
                        break;

                    if (opCode == OpPing)
                        await WriteFrameAsync(OpPong, Encoding.UTF8.GetString(payload));
                    else if (opCode == OpClose)
                        break;
                }
            }
            catch
            {
            }

            Disconnect();
        }

        private static async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
        {
            if (_pipe == null)
                return false;

            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await _pipe.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
                if (read <= 0)
                    return false;

                offset += read;
            }

            return true;
        }

        private static async Task WriteFrameAsync(int opCode, string payload)
        {
            if (_pipe == null)
                return;

            var bytes = Encoding.UTF8.GetBytes(payload);
            var header = new byte[8];
            BitConverter.GetBytes(opCode).CopyTo(header, 0);
            BitConverter.GetBytes(bytes.Length).CopyTo(header, 4);

            await _pipe.WriteAsync(header, 0, header.Length);
            await _pipe.WriteAsync(bytes, 0, bytes.Length);
            await _pipe.FlushAsync();
        }

        private static void Disconnect()
        {
            try
            {
                _readCancellation?.Cancel();
                _readCancellation?.Dispose();
            }
            catch
            {
            }

            _readCancellation = null;

            try
            {
                _pipe?.Dispose();
            }
            catch
            {
            }

            _pipe = null;
        }

        private static bool IsPageEnabled(string pageKey)
        {
            if (SettingsManager.DiscordRichPresenceEnabledPages == null)
                return true;

            return SettingsManager.DiscordRichPresenceEnabledPages.Contains(pageKey, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsConfiguredClientId(string? clientId)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                return false;

            var value = clientId.Trim();
            if (value == "000000000000000000")
                return false;

            foreach (var ch in value)
            {
                if (!char.IsDigit(ch))
                    return false;
            }

            return value.Length >= 15;
        }

        private static string FormatTime(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";

            return $"{time.Minutes}:{time.Seconds:00}";
        }
    }
}
