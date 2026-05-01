using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cleario.Services
{
    public enum MetadataProviderMode
    {
        Cinemeta = 0,
        AddonDecided = 1
    }

    public enum PosterSizeMode
    {
        Compact = 0,
        Default = 1,
        Large = 2,
        ExtraLarge = 3
    }

    public enum PlaybackEngineMode
    {
        LibVLC = 0,
        MPV = 1,
        ExternalPlayer = 2
    }

    public enum DetailsSeriesViewMode
    {
        Compact = 0,
        Full = 1
    }

    public enum ExternalPlayerMode
    {
        VLC = 0
    }

    public static class SettingsManager
    {
        private static readonly string _settingsPath = AppPaths.GetFilePath("cleario.settings.json");

        private static bool _initialized;

        static SettingsManager()
        {
            ResetDefaults();
            TryLoadFromDisk();
            _initialized = true;
        }

        public static MetadataProviderMode MetadataProvider { get; set; } = MetadataProviderMode.Cinemeta;

        public static bool CacheImages { get; set; } = false;
        public static int CacheLimitGb { get; set; } = 2;
        public static bool ShowPosterBadges { get; set; } = true;
        public static bool ShowPosterHoverYear { get; set; } = true;
        public static bool ShowPosterHoverImdbRating { get; set; } = true;
        public static PosterSizeMode PosterSize { get; set; } = PosterSizeMode.Default;
        public static bool PlayerSingleClickPlayPause { get; set; } = true;
        public static bool PlayerDoubleClickFullScreen { get; set; } = true;
        public static PlaybackEngineMode PlaybackEngine { get; set; } = PlaybackEngineMode.MPV;
        public static ExternalPlayerMode ExternalPlayer { get; set; } = ExternalPlayerMode.VLC;
        public static bool AutoFallbackEnabled { get; set; } = false;
        public static PlaybackEngineMode FallbackPlaybackEngine { get; set; } = PlaybackEngineMode.LibVLC;
        public static int MpvRetryAttempts { get; set; } = 3;
        public static int MpvRetryMessageAttempt { get; set; } = 0;
        public static bool SaveMemory { get; set; } = false;
        public static string PreferredAudioLanguage { get; set; } = "English";
        public static string PreferredSubtitleLanguage { get; set; } = "English";
        public static bool DisableSubtitlesByDefault { get; set; } = false;
        public static DetailsSeriesViewMode DetailsSeriesView { get; set; } = DetailsSeriesViewMode.Full;
        public static int DetailsBackgroundBrightnessPercent { get; set; } = 100;
        public static bool DetailsShowImdbRating { get; set; } = true;
        public static bool DisableSpoilers { get; set; } = false;
        public static bool EnableNextEpisodePopup { get; set; } = true;
        public static int NextEpisodePopupSeconds { get; set; } = 30;
        public static bool AutoPlayNextEpisode { get; set; } = true;
        public static bool CheckForUpdatesAtStartup { get; set; } = true;
        public static bool DiscordRichPresenceEnabled { get; set; } = false;
        public static bool DiscordShowPlaybackTimestamp { get; set; } = true;
        public static bool DiscordShowPoster { get; set; } = true;
        public static bool DiscordShowTitle { get; set; } = true;
        public static bool DiscordShowClearioRunning { get; set; } = true;
        public static List<string> DiscordRichPresenceEnabledPages { get; set; } = CreateDefaultDiscordPages();
        public static bool TraktConnected { get; set; } = false;
        public static bool TraktScrobblingEnabled { get; set; } = true;
        public static bool TraktWatchHistoryCatalogEnabled { get; set; } = true;
        public static bool TraktWatchlistCatalogEnabled { get; set; } = true;
        public static string TraktUsername { get; set; } = string.Empty;
        public static string TraktAccessToken { get; set; } = string.Empty;
        public static string TraktRefreshToken { get; set; } = string.Empty;
        public static long TraktTokenCreatedAtUnix { get; set; } = 0;
        public static long TraktTokenExpiresInSeconds { get; set; } = 0;
        public static List<string> HomeCatalogOrder { get; set; } = new();
        public static List<string> HomeCatalogDisabled { get; set; } = new();

        public static async Task InitializeAsync(bool forceReload = false)
        {
            if (_initialized && !forceReload)
                return;

            ResetDefaults();

            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = await File.ReadAllTextAsync(_settingsPath);
                    var dto = JsonSerializer.Deserialize<SettingsDto>(json);
                    var shouldResave = ContainsUnprotectedSensitiveData(dto);
                    ApplyDto(dto);
                    if (shouldResave)
                        await SaveAsync();
                }
            }
            catch
            {
                ResetDefaults();
            }

            _initialized = true;
        }

        public static async Task SaveAsync()
        {
            try
            {
                var dto = new SettingsDto
                {
                    MetadataProvider = MetadataProvider,
                    CacheImages = CacheImages,
                    CacheLimitGb = CacheLimitGb,
                    ShowPosterBadges = ShowPosterBadges,
                    ShowPosterHoverYear = ShowPosterHoverYear,
                    ShowPosterHoverImdbRating = ShowPosterHoverImdbRating,
                    PosterSize = PosterSize,
                    PlayerSingleClickPlayPause = PlayerSingleClickPlayPause,
                    PlayerDoubleClickFullScreen = PlayerDoubleClickFullScreen,
                    PlaybackEngine = PlaybackEngine,
                    ExternalPlayer = ExternalPlayer,
                    AutoFallbackEnabled = AutoFallbackEnabled,
                    FallbackPlaybackEngine = FallbackPlaybackEngine,
                    MpvRetryAttempts = MpvRetryAttempts,
                    MpvRetryMessageAttempt = MpvRetryMessageAttempt,
                    SaveMemory = SaveMemory,
                    PreferredAudioLanguage = PreferredAudioLanguage,
                    PreferredSubtitleLanguage = PreferredSubtitleLanguage,
                    DisableSubtitlesByDefault = DisableSubtitlesByDefault,
                    DetailsSeriesView = DetailsSeriesView,
                    DetailsBackgroundBrightnessPercent = DetailsBackgroundBrightnessPercent,
                    DetailsShowImdbRating = DetailsShowImdbRating,
                    DisableSpoilers = DisableSpoilers,
                    EnableNextEpisodePopup = EnableNextEpisodePopup,
                    NextEpisodePopupSeconds = NextEpisodePopupSeconds,
                    AutoPlayNextEpisode = AutoPlayNextEpisode,
                    CheckForUpdatesAtStartup = CheckForUpdatesAtStartup,
                    DiscordRichPresenceEnabled = DiscordRichPresenceEnabled,
                    DiscordShowPlaybackTimestamp = DiscordShowPlaybackTimestamp,
                    DiscordShowPoster = DiscordShowPoster,
                    DiscordShowTitle = DiscordShowTitle,
                    DiscordShowClearioRunning = DiscordShowClearioRunning,
                    DiscordRichPresenceEnabledPages = DiscordRichPresenceEnabledPages?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>(),
                    TraktConnected = TraktConnected,
                    TraktScrobblingEnabled = TraktScrobblingEnabled,
                    TraktWatchHistoryCatalogEnabled = TraktWatchHistoryCatalogEnabled,
                    TraktWatchlistCatalogEnabled = TraktWatchlistCatalogEnabled,
                    TraktUsername = TraktUsername,
                    TraktAccessToken = SecureStorageService.Protect(TraktAccessToken),
                    TraktRefreshToken = SecureStorageService.Protect(TraktRefreshToken),
                    TraktTokenCreatedAtUnix = TraktTokenCreatedAtUnix,
                    TraktTokenExpiresInSeconds = TraktTokenExpiresInSeconds,
                    HomeCatalogOrder = HomeCatalogOrder?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>(),
                    HomeCatalogDisabled = HomeCatalogDisabled?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>()
                };

                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(_settingsPath, json);
            }
            catch
            {
            }
        }

        public static string ExportJson()
        {
            var dto = new SettingsDto
            {
                MetadataProvider = MetadataProvider,
                CacheImages = CacheImages,
                CacheLimitGb = CacheLimitGb,
                ShowPosterBadges = ShowPosterBadges,
                ShowPosterHoverYear = ShowPosterHoverYear,
                ShowPosterHoverImdbRating = ShowPosterHoverImdbRating,                PosterSize = PosterSize,
                PlayerSingleClickPlayPause = PlayerSingleClickPlayPause,
                PlayerDoubleClickFullScreen = PlayerDoubleClickFullScreen,
                    PlaybackEngine = PlaybackEngine,
                    ExternalPlayer = ExternalPlayer,
                    AutoFallbackEnabled = AutoFallbackEnabled,
                    FallbackPlaybackEngine = FallbackPlaybackEngine,
                    MpvRetryAttempts = MpvRetryAttempts,
                    MpvRetryMessageAttempt = MpvRetryMessageAttempt,
                    SaveMemory = SaveMemory,
                PreferredAudioLanguage = PreferredAudioLanguage,
                PreferredSubtitleLanguage = PreferredSubtitleLanguage,
                DisableSubtitlesByDefault = DisableSubtitlesByDefault,
                DetailsSeriesView = DetailsSeriesView,
                DetailsBackgroundBrightnessPercent = DetailsBackgroundBrightnessPercent,
                DetailsShowImdbRating = DetailsShowImdbRating,
                DisableSpoilers = DisableSpoilers,
                EnableNextEpisodePopup = EnableNextEpisodePopup,
                NextEpisodePopupSeconds = NextEpisodePopupSeconds,
                AutoPlayNextEpisode = AutoPlayNextEpisode,
                CheckForUpdatesAtStartup = CheckForUpdatesAtStartup,
                DiscordRichPresenceEnabled = DiscordRichPresenceEnabled,
                DiscordShowPlaybackTimestamp = DiscordShowPlaybackTimestamp,
                DiscordShowPoster = DiscordShowPoster,
                DiscordShowTitle = DiscordShowTitle,
                DiscordShowClearioRunning = DiscordShowClearioRunning,
                DiscordRichPresenceEnabledPages = DiscordRichPresenceEnabledPages?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>(),
                TraktConnected = TraktConnected,
                TraktScrobblingEnabled = TraktScrobblingEnabled,
                TraktWatchHistoryCatalogEnabled = TraktWatchHistoryCatalogEnabled,
                TraktWatchlistCatalogEnabled = TraktWatchlistCatalogEnabled,
                TraktUsername = TraktUsername,
                TraktAccessToken = SecureStorageService.Protect(TraktAccessToken),
                TraktRefreshToken = SecureStorageService.Protect(TraktRefreshToken),
                TraktTokenCreatedAtUnix = TraktTokenCreatedAtUnix,
                TraktTokenExpiresInSeconds = TraktTokenExpiresInSeconds,
                HomeCatalogOrder = HomeCatalogOrder?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>(),
                HomeCatalogDisabled = HomeCatalogDisabled?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>()
            };

            return JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        public static async Task ImportJsonAsync(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var dto = JsonSerializer.Deserialize<SettingsDto>(json);
                ResetDefaults();
                ApplyDto(dto);
                _initialized = true;
                await SaveAsync();
            }
            catch
            {
            }
        }


        public static async Task ResetToFactoryDefaultsAsync()
        {
            ResetDefaults();
            _initialized = true;
            await SaveAsync();
        }

        public static List<string> CreateDefaultDiscordPages()
        {
            return new List<string>
            {
                "Home",
                "Discover",
                "Library",
                "Calendar",
                "Search",
                "Details",
                "Player",
                "History",
                "Addons",
                "Settings"
            };
        }


        private static bool ContainsUnprotectedSensitiveData(SettingsDto? dto)
        {
            if (dto == null)
                return false;

            var hasPlainAccessToken = !string.IsNullOrWhiteSpace(dto.TraktAccessToken) && !SecureStorageService.IsProtected(dto.TraktAccessToken);
            var hasPlainRefreshToken = !string.IsNullOrWhiteSpace(dto.TraktRefreshToken) && !SecureStorageService.IsProtected(dto.TraktRefreshToken);
            return hasPlainAccessToken || hasPlainRefreshToken;
        }

        private static void SaveToDiskSync()
        {
            try
            {
                var dto = new SettingsDto
                {
                    MetadataProvider = MetadataProvider,
                    CacheImages = CacheImages,
                    CacheLimitGb = CacheLimitGb,
                    ShowPosterBadges = ShowPosterBadges,
                    ShowPosterHoverYear = ShowPosterHoverYear,
                    ShowPosterHoverImdbRating = ShowPosterHoverImdbRating,
                    PosterSize = PosterSize,
                    PlayerSingleClickPlayPause = PlayerSingleClickPlayPause,
                    PlayerDoubleClickFullScreen = PlayerDoubleClickFullScreen,
                    PlaybackEngine = PlaybackEngine,
                    ExternalPlayer = ExternalPlayer,
                    AutoFallbackEnabled = AutoFallbackEnabled,
                    FallbackPlaybackEngine = FallbackPlaybackEngine,
                    MpvRetryAttempts = MpvRetryAttempts,
                    MpvRetryMessageAttempt = MpvRetryMessageAttempt,
                    SaveMemory = SaveMemory,
                    PreferredAudioLanguage = PreferredAudioLanguage,
                    PreferredSubtitleLanguage = PreferredSubtitleLanguage,
                    DisableSubtitlesByDefault = DisableSubtitlesByDefault,
                    DetailsSeriesView = DetailsSeriesView,
                    DetailsBackgroundBrightnessPercent = DetailsBackgroundBrightnessPercent,
                    DetailsShowImdbRating = DetailsShowImdbRating,
                    DisableSpoilers = DisableSpoilers,
                    EnableNextEpisodePopup = EnableNextEpisodePopup,
                    NextEpisodePopupSeconds = NextEpisodePopupSeconds,
                    AutoPlayNextEpisode = AutoPlayNextEpisode,
                    CheckForUpdatesAtStartup = CheckForUpdatesAtStartup,
                    DiscordRichPresenceEnabled = DiscordRichPresenceEnabled,
                    DiscordShowPlaybackTimestamp = DiscordShowPlaybackTimestamp,
                    DiscordShowPoster = DiscordShowPoster,
                    DiscordShowTitle = DiscordShowTitle,
                    DiscordShowClearioRunning = DiscordShowClearioRunning,
                    DiscordRichPresenceEnabledPages = DiscordRichPresenceEnabledPages?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>(),
                    TraktConnected = TraktConnected,
                    TraktScrobblingEnabled = TraktScrobblingEnabled,
                    TraktWatchHistoryCatalogEnabled = TraktWatchHistoryCatalogEnabled,
                    TraktWatchlistCatalogEnabled = TraktWatchlistCatalogEnabled,
                    TraktUsername = TraktUsername,
                    TraktAccessToken = SecureStorageService.Protect(TraktAccessToken),
                    TraktRefreshToken = SecureStorageService.Protect(TraktRefreshToken),
                    TraktTokenCreatedAtUnix = TraktTokenCreatedAtUnix,
                    TraktTokenExpiresInSeconds = TraktTokenExpiresInSeconds,
                    HomeCatalogOrder = HomeCatalogOrder?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>(),
                    HomeCatalogDisabled = HomeCatalogDisabled?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>()
                };

                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
            }
        }

        private static void ResetDefaults()
        {
            MetadataProvider = MetadataProviderMode.Cinemeta;
            CacheImages = false;
            CacheLimitGb = 2;
            ShowPosterBadges = true;
            ShowPosterHoverYear = true;
            ShowPosterHoverImdbRating = true;            PosterSize = PosterSizeMode.Default;
            PlayerSingleClickPlayPause = true;
            PlayerDoubleClickFullScreen = true;
            PlaybackEngine = PlaybackEngineMode.MPV;
            ExternalPlayer = ExternalPlayerMode.VLC;
            AutoFallbackEnabled = false;
            FallbackPlaybackEngine = PlaybackEngineMode.LibVLC;
            MpvRetryAttempts = 3;
            MpvRetryMessageAttempt = 0;
            SaveMemory = false;
            PreferredAudioLanguage = "English";
            PreferredSubtitleLanguage = "English";
            DisableSubtitlesByDefault = false;
            DetailsSeriesView = DetailsSeriesViewMode.Full;
            DetailsBackgroundBrightnessPercent = 100;
            DetailsShowImdbRating = true;
            DisableSpoilers = false;
            EnableNextEpisodePopup = true;
            NextEpisodePopupSeconds = 30;
            AutoPlayNextEpisode = true;
            CheckForUpdatesAtStartup = true;
            DiscordRichPresenceEnabled = false;
            DiscordShowPlaybackTimestamp = true;
            DiscordShowPoster = true;
            DiscordShowTitle = true;
            DiscordShowClearioRunning = true;
            DiscordRichPresenceEnabledPages = CreateDefaultDiscordPages();
            TraktConnected = false;
            TraktScrobblingEnabled = true;
            TraktWatchHistoryCatalogEnabled = true;
            TraktWatchlistCatalogEnabled = true;
            TraktUsername = string.Empty;
            TraktAccessToken = string.Empty;
            TraktRefreshToken = string.Empty;
            TraktTokenCreatedAtUnix = 0;
            TraktTokenExpiresInSeconds = 0;
            HomeCatalogOrder = new List<string>();
            HomeCatalogDisabled = new List<string>();
        }

        private static void TryLoadFromDisk()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var dto = JsonSerializer.Deserialize<SettingsDto>(json);
                    var shouldResave = ContainsUnprotectedSensitiveData(dto);
                    ApplyDto(dto);
                    if (shouldResave)
                        SaveToDiskSync();
                }
            }
            catch
            {
                ResetDefaults();
            }
        }

        private static void ApplyDto(SettingsDto? dto)
        {
            if (dto == null)
                return;

            MetadataProvider = dto.MetadataProvider;
            CacheImages = dto.CacheImages;
            CacheLimitGb = dto.CacheLimitGb <= 0 ? 0 : dto.CacheLimitGb;
            ShowPosterBadges = dto.ShowPosterBadges;
            ShowPosterHoverYear = dto.ShowPosterHoverYear;
            ShowPosterHoverImdbRating = dto.ShowPosterHoverImdbRating;            PosterSize = dto.PosterSize;
            PlayerSingleClickPlayPause = dto.PlayerSingleClickPlayPause;
            PlayerDoubleClickFullScreen = dto.PlayerDoubleClickFullScreen;
            PlaybackEngine = Enum.IsDefined(typeof(PlaybackEngineMode), dto.PlaybackEngine) ? dto.PlaybackEngine : PlaybackEngineMode.MPV;
            ExternalPlayer = Enum.IsDefined(typeof(ExternalPlayerMode), dto.ExternalPlayer) ? dto.ExternalPlayer : ExternalPlayerMode.VLC;
            AutoFallbackEnabled = dto.AutoFallbackEnabled;
            FallbackPlaybackEngine = Enum.IsDefined(typeof(PlaybackEngineMode), dto.FallbackPlaybackEngine) ? dto.FallbackPlaybackEngine : PlaybackEngineMode.LibVLC;
            MpvRetryAttempts = Math.Clamp(dto.MpvRetryAttempts, 0, 10);
            MpvRetryMessageAttempt = dto.MpvRetryMessageAttempt <= 0
                ? 0
                : Math.Clamp(dto.MpvRetryMessageAttempt, 1, Math.Max(1, MpvRetryAttempts));
            SaveMemory = dto.SaveMemory;
            PreferredAudioLanguage = string.IsNullOrWhiteSpace(dto.PreferredAudioLanguage) ? "English" : dto.PreferredAudioLanguage;
            PreferredSubtitleLanguage = string.IsNullOrWhiteSpace(dto.PreferredSubtitleLanguage) ? "English" : dto.PreferredSubtitleLanguage;
            DisableSubtitlesByDefault = dto.DisableSubtitlesByDefault;
            DetailsSeriesView = Enum.IsDefined(typeof(DetailsSeriesViewMode), dto.DetailsSeriesView) ? dto.DetailsSeriesView : DetailsSeriesViewMode.Full;
            DetailsBackgroundBrightnessPercent = Math.Clamp(dto.DetailsBackgroundBrightnessPercent, 0, 100);
            DetailsShowImdbRating = dto.DetailsShowImdbRating;            DisableSpoilers = dto.DisableSpoilers;
            EnableNextEpisodePopup = dto.EnableNextEpisodePopup;
            NextEpisodePopupSeconds = dto.NextEpisodePopupSeconds <= 0 ? 30 : dto.NextEpisodePopupSeconds;
            AutoPlayNextEpisode = dto.AutoPlayNextEpisode;
            CheckForUpdatesAtStartup = dto.CheckForUpdatesAtStartup;
            DiscordRichPresenceEnabled = dto.DiscordRichPresenceEnabled;
            DiscordShowPlaybackTimestamp = dto.DiscordShowPlaybackTimestamp;
            DiscordShowPoster = dto.DiscordShowPoster;
            DiscordShowTitle = dto.DiscordShowTitle;
            DiscordShowClearioRunning = dto.DiscordShowClearioRunning;
            DiscordRichPresenceEnabledPages = dto.DiscordRichPresenceEnabledPages == null
                ? CreateDefaultDiscordPages()
                : dto.DiscordRichPresenceEnabledPages.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            TraktScrobblingEnabled = dto.TraktScrobblingEnabled;
            TraktWatchHistoryCatalogEnabled = dto.TraktWatchHistoryCatalogEnabled;
            TraktWatchlistCatalogEnabled = dto.TraktWatchlistCatalogEnabled;
            TraktUsername = dto.TraktUsername ?? string.Empty;
            TraktAccessToken = SecureStorageService.Unprotect(dto.TraktAccessToken);
            TraktRefreshToken = SecureStorageService.Unprotect(dto.TraktRefreshToken);
            TraktConnected = dto.TraktConnected && !string.IsNullOrWhiteSpace(TraktAccessToken);
            TraktTokenCreatedAtUnix = dto.TraktTokenCreatedAtUnix;
            TraktTokenExpiresInSeconds = dto.TraktTokenExpiresInSeconds;
            HomeCatalogOrder = dto.HomeCatalogOrder?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>();
            HomeCatalogDisabled = dto.HomeCatalogDisabled?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList() ?? new List<string>();
        }

        private sealed class SettingsDto
        {
            public MetadataProviderMode MetadataProvider { get; set; } = MetadataProviderMode.Cinemeta;
            public bool CacheImages { get; set; } = false;
            public int CacheLimitGb { get; set; } = 2;
            public bool ShowPosterBadges { get; set; } = true;
            public bool ShowPosterHoverYear { get; set; } = true;
            public bool ShowPosterHoverImdbRating { get; set; } = true;
            public PosterSizeMode PosterSize { get; set; } = PosterSizeMode.Default;
            public bool PlayerSingleClickPlayPause { get; set; } = true;
            public bool PlayerDoubleClickFullScreen { get; set; } = true;
            public PlaybackEngineMode PlaybackEngine { get; set; } = PlaybackEngineMode.MPV;
            public ExternalPlayerMode ExternalPlayer { get; set; } = ExternalPlayerMode.VLC;
            public bool AutoFallbackEnabled { get; set; } = false;
            public PlaybackEngineMode FallbackPlaybackEngine { get; set; } = PlaybackEngineMode.LibVLC;
            public int MpvRetryAttempts { get; set; } = 3;
            public int MpvRetryMessageAttempt { get; set; } = 0;
            public bool SaveMemory { get; set; } = false;
            public string PreferredAudioLanguage { get; set; } = "English";
            public string PreferredSubtitleLanguage { get; set; } = "English";
            public bool DisableSubtitlesByDefault { get; set; } = false;
            public DetailsSeriesViewMode DetailsSeriesView { get; set; } = DetailsSeriesViewMode.Full;
            public int DetailsBackgroundBrightnessPercent { get; set; } = 100;
            public bool DetailsShowImdbRating { get; set; } = true;            public bool DisableSpoilers { get; set; } = false;
            public bool EnableNextEpisodePopup { get; set; } = true;
            public int NextEpisodePopupSeconds { get; set; } = 30;
            public bool AutoPlayNextEpisode { get; set; } = true;
            public bool CheckForUpdatesAtStartup { get; set; } = true;
            public bool DiscordRichPresenceEnabled { get; set; } = false;
            public bool DiscordShowPlaybackTimestamp { get; set; } = true;
            public bool DiscordShowPoster { get; set; } = true;
            public bool DiscordShowTitle { get; set; } = true;
            public bool DiscordShowClearioRunning { get; set; } = true;
            public List<string>? DiscordRichPresenceEnabledPages { get; set; } = CreateDefaultDiscordPages();
            public bool TraktConnected { get; set; } = false;
            public bool TraktScrobblingEnabled { get; set; } = true;
            public bool TraktWatchHistoryCatalogEnabled { get; set; } = true;
            public bool TraktWatchlistCatalogEnabled { get; set; } = true;
            public string TraktUsername { get; set; } = string.Empty;
            public string TraktAccessToken { get; set; } = string.Empty;
            public string TraktRefreshToken { get; set; } = string.Empty;
            public long TraktTokenCreatedAtUnix { get; set; } = 0;
            public long TraktTokenExpiresInSeconds { get; set; } = 0;
            public List<string> HomeCatalogOrder { get; set; } = new();
            public List<string> HomeCatalogDisabled { get; set; } = new();
        }
    }
}
