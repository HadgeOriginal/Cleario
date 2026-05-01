using Cleario.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using WinRT.Interop;

namespace Cleario.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private static readonly string[] PreferredLanguageOptions = new[]
        {
            "Off", "English", "Dutch", "French", "German", "Spanish", "Italian", "Portuguese", "Polish", "Romanian",
            "Swedish", "Norwegian", "Danish", "Finnish", "Czech", "Hungarian", "Greek", "Turkish", "Russian",
            "Ukrainian", "Arabic", "Hebrew", "Hindi", "Japanese", "Korean", "Chinese", "Thai", "Vietnamese"
        };


        private sealed record DiscordPageOption(string Key, string Label);

        private static readonly DiscordPageOption[] DiscordPageOptions =
        {
            new("Home", "Home"),
            new("Discover", "Discover"),
            new("Library", "Library"),
            new("Calendar", "Calendar"),
            new("Search", "Search"),
            new("Details", "Details"),
            new("Player", "Player"),
            new("History", "History"),
            new("Addons", "Addons"),
            new("Settings", "Settings")
        };

        private bool _isLoading;
        private bool _updatingFallbackOptions;
        private bool _discordOptionsExpanded;
        private bool _openAboutOnLoaded;
        private string _traktDeviceCode = string.Empty;
        private DateTime _traktDeviceCodeExpiresUtc = DateTime.MinValue;
        private bool _traktPollingInProgress;
        private int _traktAuthorizationRunId;
        private ContentDialog? _traktCodeDialog;
        private TextBlock? _traktCodeDialogMessageTextBlock;
        private readonly List<DiscoverService.DiscoverCatalogDefinition> _homeCatalogs = new();

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _openAboutOnLoaded = string.Equals(e.Parameter?.ToString(), "About", StringComparison.OrdinalIgnoreCase);
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSettingsIntoControlsAsync();
        }

        private void SelectAboutTabIfRequested()
        {
            if (!_openAboutOnLoaded)
                return;

            if (SettingsTabView != null && AboutTabItem != null)
                SettingsTabView.SelectedItem = AboutTabItem;
        }

        private async Task LoadSettingsIntoControlsAsync()
        {
            _isLoading = true;

            await SettingsManager.InitializeAsync(forceReload: true);
            await AddonManager.InitializeAsync(forceReload: true);

            EnsureLanguageOptionsLoaded();

            MetadataProviderComboBox.SelectedIndex =
                SettingsManager.MetadataProvider == MetadataProviderMode.Cinemeta ? 0 : 1;

            ShowPosterBadgesToggle.IsOn = SettingsManager.ShowPosterBadges;
            ShowPosterHoverYearToggle.IsOn = SettingsManager.ShowPosterHoverYear;
            ShowPosterHoverImdbToggle.IsOn = SettingsManager.ShowPosterHoverImdbRating;
            UpdatePosterHoverBadgeOptionsVisibility();
            SelectPosterSizeOption(SettingsManager.PosterSize);
            PlayerSingleClickPlayPauseToggle.IsOn = SettingsManager.PlayerSingleClickPlayPause;
            PlayerDoubleClickFullScreenToggle.IsOn = SettingsManager.PlayerDoubleClickFullScreen;
            SelectPlaybackEngineOption(SettingsManager.PlaybackEngine);
            SelectExternalPlayerOption(SettingsManager.ExternalPlayer);
            AutoFallbackCheckBox.IsChecked = SettingsManager.AutoFallbackEnabled;
            SelectFallbackPlaybackEngineOption(SettingsManager.FallbackPlaybackEngine);
            SelectMpvRetryAttemptsOption(SettingsManager.MpvRetryAttempts);
            SelectMpvRetryMessageAttemptOption(SettingsManager.MpvRetryMessageAttempt);
            UpdatePlaybackEngineDependentControls();
            EnableNextEpisodePopupToggle.IsOn = SettingsManager.EnableNextEpisodePopup;
            SelectNextEpisodePopupSecondsOption(SettingsManager.NextEpisodePopupSeconds);
            AutoPlayNextEpisodeToggle.IsOn = SettingsManager.AutoPlayNextEpisode;
            SaveMemoryToggle.IsOn = SettingsManager.SaveMemory;
            SelectLanguageOption(PreferredAudioLanguageComboBox, SettingsManager.PreferredAudioLanguage);
            SelectLanguageOption(PreferredSubtitleLanguageComboBox, SettingsManager.PreferredSubtitleLanguage);
            DisableSubtitlesByDefaultCheckBox.IsChecked = SettingsManager.DisableSubtitlesByDefault;
            UpdateSubtitleDefaultControls();
            SelectDetailsSeriesViewOption(SettingsManager.DetailsSeriesView);
            DetailsBackgroundBrightnessSlider.Value = SettingsManager.DetailsBackgroundBrightnessPercent;
            UpdateDetailsBackgroundBrightnessValueText();
            DetailsShowImdbToggle.IsOn = SettingsManager.DetailsShowImdbRating;
            DisableSpoilersToggle.IsOn = SettingsManager.DisableSpoilers;

            CacheImagesToggle.IsOn = SettingsManager.CacheImages;
            SelectCacheLimitOption(SettingsManager.CacheLimitGb);
            UpdateCacheControlsEnabledState();
            LoadDiscordSettingsIntoControls();
            LoadTraktSettingsIntoControls();
            UpdateAboutControls();

            await LoadHomeCatalogSettingsAsync();
            SelectAboutTabIfRequested();

            _isLoading = false;
        }

        private void SelectCacheLimitOption(int limitGb)
        {
            var match = CacheLimitComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), limitGb.ToString(), StringComparison.OrdinalIgnoreCase));

            CacheLimitComboBox.SelectedItem = match ?? CacheLimitComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "2", StringComparison.OrdinalIgnoreCase));
        }

        private void SelectPosterSizeOption(PosterSizeMode mode)
        {
            var match = PosterSizeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase));

            PosterSizeComboBox.SelectedItem = match ?? PosterSizeComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), PosterSizeMode.Default.ToString(), StringComparison.OrdinalIgnoreCase));
        }


        private void SelectPlaybackEngineOption(PlaybackEngineMode mode)
        {
            var match = PlaybackEngineComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase));

            PlaybackEngineComboBox.SelectedItem = match ?? PlaybackEngineComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), PlaybackEngineMode.MPV.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private void SelectExternalPlayerOption(ExternalPlayerMode mode)
        {
            if (ExternalPlayerComboBox == null)
                return;

            var match = ExternalPlayerComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase));

            ExternalPlayerComboBox.SelectedItem = match ?? ExternalPlayerComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        }

        private void RefreshFallbackPlaybackEngineOptions()
        {
            if (FallbackPlaybackEngineComboBox == null)
                return;

            _updatingFallbackOptions = true;
            try
            {
                var currentEngine = SettingsManager.PlaybackEngine;
                var options = new (PlaybackEngineMode Engine, string Label)[]
                {
                    (PlaybackEngineMode.MPV, "MPV"),
                    (PlaybackEngineMode.LibVLC, "LibVLC"),
                    (PlaybackEngineMode.ExternalPlayer, "External player")
                };

                FallbackPlaybackEngineComboBox.Items.Clear();

                foreach (var option in options.Where(x => x.Engine != currentEngine))
                {
                    FallbackPlaybackEngineComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = option.Label,
                        Tag = option.Engine.ToString()
                    });
                }

                var selectedEngine = SettingsManager.FallbackPlaybackEngine;
                if (selectedEngine == currentEngine || !Enum.IsDefined(typeof(PlaybackEngineMode), selectedEngine))
                    selectedEngine = FallbackPlaybackEngineComboBox.Items
                        .OfType<ComboBoxItem>()
                        .Select(x => x.Tag?.ToString())
                        .Select(x => Enum.TryParse<PlaybackEngineMode>(x, true, out var parsed) ? parsed : PlaybackEngineMode.LibVLC)
                        .FirstOrDefault();

                SettingsManager.FallbackPlaybackEngine = selectedEngine;
                SelectFallbackPlaybackEngineOption(selectedEngine);
            }
            finally
            {
                _updatingFallbackOptions = false;
            }
        }

        private void SelectFallbackPlaybackEngineOption(PlaybackEngineMode mode)
        {
            if (FallbackPlaybackEngineComboBox == null)
                return;

            var match = FallbackPlaybackEngineComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase));

            FallbackPlaybackEngineComboBox.SelectedItem = match ?? FallbackPlaybackEngineComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault();
        }


        private void SelectMpvRetryAttemptsOption(int attempts)
        {
            if (MpvRetryAttemptsComboBox == null)
                return;

            var value = Math.Clamp(attempts, 0, 10);
            var match = MpvRetryAttemptsComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase));

            MpvRetryAttemptsComboBox.SelectedItem = match ?? MpvRetryAttemptsComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "3", StringComparison.OrdinalIgnoreCase));
        }

        private void SelectMpvRetryMessageAttemptOption(int attempt)
        {
            if (MpvRetryMessageAttemptComboBox == null)
                return;

            var value = attempt <= 0 ? 0 : Math.Clamp(attempt, 1, Math.Max(1, SettingsManager.MpvRetryAttempts));
            var match = MpvRetryMessageAttemptComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase));

            MpvRetryMessageAttemptComboBox.SelectedItem = match ?? MpvRetryMessageAttemptComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "0", StringComparison.OrdinalIgnoreCase));
        }

        private void SelectDetailsSeriesViewOption(DetailsSeriesViewMode mode)
        {
            if (DetailsSeriesViewComboBox == null)
                return;

            var match = DetailsSeriesViewComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase));

            DetailsSeriesViewComboBox.SelectedItem = match ?? DetailsSeriesViewComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "Full", StringComparison.OrdinalIgnoreCase));
        }

        private void UpdatePlaybackEngineDependentControls()
        {
            var isExternal = SettingsManager.PlaybackEngine == PlaybackEngineMode.ExternalPlayer;

            if (PlayerSingleClickPlayPauseToggle != null)
            {
                PlayerSingleClickPlayPauseToggle.IsEnabled = !isExternal;
                PlayerSingleClickPlayPauseToggle.Opacity = isExternal ? 0.45 : 1.0;
            }

            if (PlayerDoubleClickFullScreenToggle != null)
            {
                PlayerDoubleClickFullScreenToggle.IsEnabled = !isExternal;
                PlayerDoubleClickFullScreenToggle.Opacity = isExternal ? 0.45 : 1.0;
            }

            if (ExternalPlayerPanel != null)
                ExternalPlayerPanel.Visibility = isExternal ? Visibility.Visible : Visibility.Collapsed;

            RefreshFallbackPlaybackEngineOptions();

            if (FallbackEnginePanel != null)
                FallbackEnginePanel.Visibility = AutoFallbackCheckBox?.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateSubtitleDefaultControls()
        {
            if (PreferredSubtitleLanguageComboBox == null)
                return;

            var disabled = DisableSubtitlesByDefaultCheckBox?.IsChecked == true;
            PreferredSubtitleLanguageComboBox.IsEnabled = !disabled;
            PreferredSubtitleLanguageComboBox.Opacity = disabled ? 0.55 : 1.0;
        }


        private void SelectNextEpisodePopupSecondsOption(int seconds)
        {
            var value = seconds <= 0 ? 30 : seconds;
            var match = NextEpisodePopupSecondsComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase));

            NextEpisodePopupSecondsComboBox.SelectedItem = match ?? NextEpisodePopupSecondsComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "30", StringComparison.OrdinalIgnoreCase));
        }

        private void EnsureLanguageOptionsLoaded()
        {
            LoadLanguageOptions(PreferredAudioLanguageComboBox);
            LoadLanguageOptions(PreferredSubtitleLanguageComboBox);
        }

        private static void LoadLanguageOptions(ComboBox comboBox)
        {
            if (comboBox == null || comboBox.Items.Count > 0)
                return;

            foreach (var language in PreferredLanguageOptions)
            {
                comboBox.Items.Add(new ComboBoxItem
                {
                    Content = language,
                    Tag = language
                });
            }
        }

        private static void SelectLanguageOption(ComboBox comboBox, string language)
        {
            if (comboBox == null)
                return;

            var match = comboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), language, StringComparison.OrdinalIgnoreCase));

            comboBox.SelectedItem = match ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "Off", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetSelectedLanguage(ComboBox comboBox, string fallback)
        {
            return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
        }

        private void UpdateCacheControlsEnabledState()
        {
            if (CacheLimitComboBox != null)
            {
                CacheLimitComboBox.IsEnabled = CacheImagesToggle?.IsOn == true;
                CacheLimitComboBox.Opacity = CacheImagesToggle?.IsOn == true ? 1.0 : 0.55;
            }
        }

        private void UpdateAboutControls()
        {
            if (CurrentVersionTextBlock != null)
                CurrentVersionTextBlock.Text = $"Current version: {UpdateService.GetCurrentVersionText()}";

            if (CheckForUpdatesAtStartupToggle != null)
                CheckForUpdatesAtStartupToggle.IsOn = SettingsManager.CheckForUpdatesAtStartup;

            if (UpdateStatusTextBlock != null)
                UpdateStatusTextBlock.Text = "Check GitHub for the newest Cleario release.";

            if (UpdateProgressRing != null)
                UpdateProgressRing.IsActive = false;

            if (CrashLogLocationTextBlock != null)
                CrashLogLocationTextBlock.Text = CrashLogger.GetLogLocationText();

            if (CheckForUpdatesButton != null)
            {
                CheckForUpdatesButton.IsEnabled = true;
                CheckForUpdatesButton.Content = "Check for updates";
            }
        }

        private void SetUpdateBusy(bool isBusy, string message)
        {
            if (UpdateProgressRing != null)
                UpdateProgressRing.IsActive = isBusy;

            if (CheckForUpdatesButton != null)
                CheckForUpdatesButton.IsEnabled = !isBusy;

            if (UpdateStatusTextBlock != null)
                UpdateStatusTextBlock.Text = message;
        }


        private void LoadDiscordSettingsIntoControls()
        {
            DiscordRichPresenceEnabledToggle.IsOn = SettingsManager.DiscordRichPresenceEnabled;
            DiscordShowPlaybackTimestampToggle.IsOn = SettingsManager.DiscordShowPlaybackTimestamp;
            DiscordShowPosterToggle.IsOn = SettingsManager.DiscordShowPoster;
            DiscordShowTitleToggle.IsOn = SettingsManager.DiscordShowTitle;
            DiscordShowClearioRunningToggle.IsOn = SettingsManager.DiscordShowClearioRunning;
            RenderDiscordPageOptions();
            UpdateDiscordControlsEnabledState();
            UpdateDiscordStatusText();
        }

        private void RenderDiscordPageOptions()
        {
            DiscordPagesPanel.Children.Clear();

            foreach (var option in DiscordPageOptions)
            {
                var toggle = new ToggleSwitch
                {
                    Header = option.Label,
                    Tag = option.Key,
                    IsOn = SettingsManager.DiscordRichPresenceEnabledPages.Contains(option.Key, StringComparer.OrdinalIgnoreCase)
                };

                toggle.Toggled += DiscordPageToggle_Toggled;
                DiscordPagesPanel.Children.Add(toggle);
            }
        }

        private void UpdateDiscordControlsEnabledState()
        {
            var enabled = DiscordRichPresenceEnabledToggle?.IsOn == true;

            if (DiscordOptionsVisibilityButton != null)
            {
                DiscordOptionsVisibilityButton.IsEnabled = enabled;
                DiscordOptionsVisibilityButton.Content = _discordOptionsExpanded ? "Hide options" : "Show options";
            }

            if (DiscordOptionsPanel != null)
            {
                DiscordOptionsPanel.Visibility = enabled && _discordOptionsExpanded ? Visibility.Visible : Visibility.Collapsed;
                DiscordOptionsPanel.IsHitTestVisible = enabled;
                DiscordOptionsPanel.Opacity = enabled ? 1.0 : 0.55;
                SetControlTreeEnabled(DiscordOptionsPanel, enabled);
            }
        }

        private static void SetControlTreeEnabled(DependencyObject root, bool enabled)
        {
            if (root is Control control)
                control.IsEnabled = enabled;

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
                SetControlTreeEnabled(VisualTreeHelper.GetChild(root, i), enabled);
        }

        private void UpdateDiscordStatusText()
        {
            if (DiscordStatusTextBlock == null)
                return;

            if (DiscordRichPresenceEnabledToggle?.IsOn != true)
            {
                DiscordStatusTextBlock.Text = "Discord Rich Presence is off.";
                return;
            }
            DiscordStatusTextBlock.Text = DiscordRichPresenceService.LastStatus;
        }

        private async Task SaveDiscordSettingsAsync()
        {
            await SettingsManager.SaveAsync();
            await DiscordRichPresenceService.RefreshFromSettingsAsync();
            UpdateDiscordStatusText();
        }

        private void LoadTraktSettingsIntoControls()
        {
            if (TraktScrobblingToggle != null)
                TraktScrobblingToggle.IsOn = SettingsManager.TraktScrobblingEnabled;

            if (TraktWatchHistoryCatalogToggle != null)
                TraktWatchHistoryCatalogToggle.IsOn = SettingsManager.TraktWatchHistoryCatalogEnabled;

            if (TraktWatchlistCatalogToggle != null)
                TraktWatchlistCatalogToggle.IsOn = SettingsManager.TraktWatchlistCatalogEnabled;

            if (TraktCodePanel != null)
                TraktCodePanel.Visibility = Visibility.Collapsed;

            UpdateTraktControlsEnabledState();
            UpdateTraktStatusText();
        }

        private void UpdateTraktControlsEnabledState()
        {
            var connected = SettingsManager.TraktConnected;

            if (TraktConnectButton != null)
            {
                TraktConnectButton.IsEnabled = !connected && !_traktPollingInProgress;
                TraktConnectButton.Content = _traktPollingInProgress ? "Connecting..." : "Connect Trakt";
            }

            if (TraktDisconnectButton != null)
                TraktDisconnectButton.IsEnabled = connected;

            if (TraktSyncButton != null)
                TraktSyncButton.IsEnabled = connected && !_traktPollingInProgress;

            if (TraktOptionsPanel != null)
            {
                TraktOptionsPanel.Opacity = connected ? 1.0 : 0.55;
                SetControlTreeEnabled(TraktOptionsPanel, connected);
            }
        }

        private void UpdateTraktStatusText()
        {
            if (TraktStatusTextBlock == null)
                return;

            if (SettingsManager.TraktConnected)
            {
                var user = string.IsNullOrWhiteSpace(SettingsManager.TraktUsername) ? "your Trakt account" : SettingsManager.TraktUsername;
                TraktStatusTextBlock.Text = $"Connected to {user}. Scrobbling and Trakt Home catalogs can be enabled below.";
                return;
            }

            TraktStatusTextBlock.Text = "Trakt is not connected. Connect with a code to enable scrobbling and Trakt catalogs.";
        }

        private void SetTraktConnectionMessage(string message)
        {
            if (TraktConnectMessageTextBlock != null)
                TraktConnectMessageTextBlock.Text = message;

            if (_traktCodeDialogMessageTextBlock != null)
            {
                _traktCodeDialogMessageTextBlock.Text = message;
                _traktCodeDialogMessageTextBlock.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private async Task PollTraktDeviceCodeAsync(int intervalSeconds, int runId)
        {
            if (_traktPollingInProgress || string.IsNullOrWhiteSpace(_traktDeviceCode) || runId != _traktAuthorizationRunId)
                return;

            _traktPollingInProgress = true;
            UpdateTraktControlsEnabledState();

            var startedAtUtc = DateTime.UtcNow;
            var minimumWaitBeforeFailure = TimeSpan.FromSeconds(60);
            var pollDelay = TimeSpan.FromSeconds(Math.Max(3, intervalSeconds));
            var lastPollMessage = "Trakt connection expired or was cancelled. Try connecting again.";

            try
            {
                while (runId == _traktAuthorizationRunId && !string.IsNullOrWhiteSpace(_traktDeviceCode) && DateTime.UtcNow < _traktDeviceCodeExpiresUtc)
                {
                    await Task.Delay(pollDelay);

                    if (runId != _traktAuthorizationRunId || string.IsNullOrWhiteSpace(_traktDeviceCode))
                        break;

                    var result = await TraktService.PollDeviceAuthorizationAsync(_traktDeviceCode);
                    lastPollMessage = result.Message;

                    if (result.Succeeded)
                    {
                        SetTraktConnectionMessage(result.Message);
                        _traktDeviceCode = string.Empty;
                        _traktDeviceCodeExpiresUtc = DateTime.MinValue;
                        if (TraktCodePanel != null)
                            TraktCodePanel.Visibility = Visibility.Collapsed;

                        try
                        {
                            _traktCodeDialog?.Hide();
                        }
                        catch
                        {
                        }
                        _traktCodeDialog = null;
                        _traktCodeDialogMessageTextBlock = null;

                        LoadTraktSettingsIntoControls();
                        await LoadHomeCatalogSettingsAsync();
                        return;
                    }

                    if (result.Pending)
                    {
                        SetTraktConnectionMessage(result.Message);
                        continue;
                    }

                    if (DateTime.UtcNow - startedAtUtc < minimumWaitBeforeFailure)
                    {
                        SetTraktConnectionMessage("Waiting for Trakt authorization...");
                        continue;
                    }

                    break;
                }

                if (runId == _traktAuthorizationRunId)
                {
                    _traktDeviceCode = string.Empty;
                    _traktDeviceCodeExpiresUtc = DateTime.MinValue;
                    if (TraktCodePanel != null)
                        TraktCodePanel.Visibility = Visibility.Visible;

                    SetTraktConnectionMessage(string.IsNullOrWhiteSpace(lastPollMessage) ? "Trakt connection expired or was cancelled. Try connecting again." : lastPollMessage);
                }
            }
            finally
            {
                if (runId == _traktAuthorizationRunId)
                {
                    _traktPollingInProgress = false;
                    UpdateTraktControlsEnabledState();
                }
            }
        }

        private void CancelTraktAuthorization(int runId)
        {
            if (runId != _traktAuthorizationRunId || SettingsManager.TraktConnected)
                return;

            _traktAuthorizationRunId++;
            _traktDeviceCode = string.Empty;
            _traktDeviceCodeExpiresUtc = DateTime.MinValue;
            _traktPollingInProgress = false;

            if (TraktCodePanel != null)
                TraktCodePanel.Visibility = Visibility.Collapsed;

            SetTraktConnectionMessage("Trakt connection cancelled.");
            UpdateTraktControlsEnabledState();
        }

        private async Task LoadHomeCatalogSettingsAsync()
        {
            _homeCatalogs.Clear();
            _homeCatalogs.AddRange(DiscoverService.OrderCatalogsForHome(
                await DiscoverService.GetDiscoverCatalogsAsync(),
                SettingsManager.HomeCatalogOrder));

            RenderHomeCatalogs();
        }

        private void RenderHomeCatalogs()
        {
            HomeCatalogsListView.Items.Clear();

            if (_homeCatalogs.Count == 0)
            {
                HomeCatalogsListView.Items.Add(new TextBlock
                {
                    Text = "No discover catalogs were found from your enabled addons.",
                    Opacity = 0.75,
                    Margin = new Thickness(0, 8, 0, 8)
                });
                return;
            }

            for (int i = 0; i < _homeCatalogs.Count; i++)
            {
                var catalog = _homeCatalogs[i];
                var catalogKey = DiscoverService.BuildCatalogKey(catalog);

                var border = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x17, 0x17, 0x17, 0x17)),
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x26, 0x26, 0x26, 0x26)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14),
                    Margin = new Thickness(0, 0, 0, 10),
                    Tag = catalogKey
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textPanel = new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                textPanel.Children.Add(new TextBlock
                {
                    Text = $"{catalog.Name} - {ToTypeDisplayName(catalog.Type)}",
                    FontSize = 17,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                textPanel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(catalog.SourceName) ? catalog.SourceBaseUrl : catalog.SourceName,
                    Opacity = 0.72,
                    TextWrapping = TextWrapping.WrapWholeWords
                });
                grid.Children.Add(textPanel);

                var upButton = new Button
                {
                    Content = "↑",
                    Tag = i,
                    IsEnabled = i > 0,
                    Width = 42,
                    MinHeight = 36,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                upButton.Click += MoveCatalogUpButton_Click;
                Grid.SetColumn(upButton, 1);
                grid.Children.Add(upButton);

                var downButton = new Button
                {
                    Content = "↓",
                    Tag = i,
                    IsEnabled = i < _homeCatalogs.Count - 1,
                    Width = 42,
                    MinHeight = 36,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 8, 0)
                };
                downButton.Click += MoveCatalogDownButton_Click;
                Grid.SetColumn(downButton, 2);
                grid.Children.Add(downButton);

                var enabledToggle = new ToggleSwitch
                {
                    Header = "Show on Home",
                    IsOn = !SettingsManager.HomeCatalogDisabled.Contains(catalogKey, StringComparer.OrdinalIgnoreCase),
                    Tag = catalogKey,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                enabledToggle.Toggled += HomeCatalogEnabledToggle_Toggled;
                Grid.SetColumn(enabledToggle, 3);
                grid.Children.Add(enabledToggle);

                border.Child = grid;
                HomeCatalogsListView.Items.Add(border);
            }
        }

        private async void HomeCatalogsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            var orderedKeys = new List<string>();
            foreach (var item in HomeCatalogsListView.Items)
            {
                if (item is FrameworkElement { Tag: string key } && !string.IsNullOrWhiteSpace(key))
                    orderedKeys.Add(key);
            }

            if (orderedKeys.Count != _homeCatalogs.Count)
                return;

            var reorderedCatalogs = orderedKeys
                .Select(key => _homeCatalogs.FirstOrDefault(x => string.Equals(DiscoverService.BuildCatalogKey(x), key, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x != null)
                .Cast<DiscoverService.DiscoverCatalogDefinition>()
                .ToList();

            _homeCatalogs.Clear();
            _homeCatalogs.AddRange(reorderedCatalogs);

            await SaveHomeCatalogOrderAsync();
            RenderHomeCatalogs();
        }

        private async Task ShowTraktDeviceCodeDialogAsync(string userCode, string verificationUrl, int runId)
        {
            try
            {
                _traktCodeDialog?.Hide();
            }
            catch
            {
            }
            _traktCodeDialogMessageTextBlock = null;

            var codeBox = new TextBox
            {
                Text = userCode,
                IsReadOnly = true,
                FontSize = 30,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 8)
            };

            var activationUrl = string.IsNullOrWhiteSpace(verificationUrl) ? "https://trakt.tv/activate" : verificationUrl;
            var openButton = new Button
            {
                Content = "Open Trakt",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 10, 16, 10)
            };
            openButton.Click += async (_, _) =>
            {
                await Launcher.LaunchUriAsync(new Uri(activationUrl));
            };

            var content = new StackPanel
            {
                Spacing = 10,
                MaxWidth = 440
            };

            content.Children.Add(new TextBlock
            {
                Text = "Enter this code on your Trakt device activation page.",
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.85
            });
            content.Children.Add(codeBox);
            content.Children.Add(new TextBlock
            {
                Text = activationUrl,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.75
            });
            content.Children.Add(openButton);
            var messageTextBlock = new TextBlock
            {
                Text = string.Empty,
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.75,
                Visibility = Visibility.Collapsed
            };
            _traktCodeDialogMessageTextBlock = messageTextBlock;
            content.Children.Add(messageTextBlock);

            var dialog = new ContentDialog
            {
                Title = "Connect Trakt",
                Content = content,
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };

            _traktCodeDialog = dialog;
            await dialog.ShowAsync();
            if (ReferenceEquals(_traktCodeDialog, dialog))
            {
                _traktCodeDialog = null;
                _traktCodeDialogMessageTextBlock = null;
                CancelTraktAuthorization(runId);
            }
        }

        private async void TraktConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || _traktPollingInProgress)
                return;

            var result = await TraktService.StartDeviceAuthorizationAsync();
            if (!result.Succeeded)
            {
                if (TraktStatusTextBlock != null)
                    TraktStatusTextBlock.Text = result.Message;
                return;
            }

            var runId = ++_traktAuthorizationRunId;
            _traktDeviceCode = result.DeviceCode;
            _traktDeviceCodeExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, result.ExpiresIn));

            if (TraktCodePanel != null)
                TraktCodePanel.Visibility = Visibility.Visible;
            if (TraktConnectMessageTextBlock != null)
                TraktConnectMessageTextBlock.Text = result.Message;

            _ = ShowTraktDeviceCodeDialogAsync(result.UserCode, result.VerificationUrl, runId);
            _ = PollTraktDeviceCodeAsync(result.Interval, runId);
        }

        private async void TraktDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            _traktAuthorizationRunId++;
            _traktDeviceCode = string.Empty;
            _traktDeviceCodeExpiresUtc = DateTime.MinValue;
            _traktPollingInProgress = false;
            await TraktService.DisconnectAsync();
            LoadTraktSettingsIntoControls();
            await LoadHomeCatalogSettingsAsync();
        }


        private async void TraktSyncButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !SettingsManager.TraktConnected)
                return;

            TraktSyncButton.IsEnabled = false;
            var originalContent = TraktSyncButton.Content;
            TraktSyncButton.Content = "Syncing...";

            try
            {
                var imported = await TraktService.SyncContinueWatchingAsync();
                if (TraktStatusTextBlock != null)
                {
                    var user = string.IsNullOrWhiteSpace(SettingsManager.TraktUsername) ? "Trakt" : SettingsManager.TraktUsername;
                    TraktStatusTextBlock.Text = imported > 0
                        ? $"Connected to {user}. Imported {imported} continue watching item(s) from Trakt."
                        : $"Connected to {user}. No new Trakt continue watching items found.";
                }
            }
            catch (Exception ex)
            {
                CrashLogger.LogException(ex, "Manual Trakt continue watching sync failed");
                if (TraktStatusTextBlock != null)
                    TraktStatusTextBlock.Text = "Could not sync Trakt continue watching right now.";
            }
            finally
            {
                TraktSyncButton.Content = originalContent;
                UpdateTraktControlsEnabledState();
            }
        }
        private async void TraktOptionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.TraktScrobblingEnabled = TraktScrobblingToggle?.IsOn == true;
            SettingsManager.TraktWatchHistoryCatalogEnabled = TraktWatchHistoryCatalogToggle?.IsOn == true;
            SettingsManager.TraktWatchlistCatalogEnabled = TraktWatchlistCatalogToggle?.IsOn == true;
            await SettingsManager.SaveAsync();
            await LoadHomeCatalogSettingsAsync();
        }

        private void DiscordOptionsVisibilityButton_Click(object sender, RoutedEventArgs e)
        {
            _discordOptionsExpanded = !_discordOptionsExpanded;
            UpdateDiscordControlsEnabledState();
        }

        private async void DiscordRichPresenceEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.DiscordRichPresenceEnabled = DiscordRichPresenceEnabledToggle.IsOn;
            UpdateDiscordControlsEnabledState();
            await SaveDiscordSettingsAsync();
        }

        private async void DiscordOptionToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.DiscordShowPlaybackTimestamp = DiscordShowPlaybackTimestampToggle.IsOn;
            SettingsManager.DiscordShowPoster = DiscordShowPosterToggle.IsOn;
            SettingsManager.DiscordShowTitle = DiscordShowTitleToggle.IsOn;
            SettingsManager.DiscordShowClearioRunning = DiscordShowClearioRunningToggle.IsOn;
            await SaveDiscordSettingsAsync();
        }

        private async void DiscordPageToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading || sender is not ToggleSwitch toggle || toggle.Tag is not string key)
                return;

            SettingsManager.DiscordRichPresenceEnabledPages = SettingsManager.DiscordRichPresenceEnabledPages
                .Where(x => !string.Equals(x, key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (toggle.IsOn)
                SettingsManager.DiscordRichPresenceEnabledPages.Add(key);

            await SaveDiscordSettingsAsync();
        }

        private async void HomeCatalogEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading || sender is not ToggleSwitch toggle || toggle.Tag is not string key)
                return;

            if (toggle.IsOn)
                SettingsManager.HomeCatalogDisabled = SettingsManager.HomeCatalogDisabled.Where(x => !string.Equals(x, key, StringComparison.OrdinalIgnoreCase)).ToList();
            else if (!SettingsManager.HomeCatalogDisabled.Contains(key, StringComparer.OrdinalIgnoreCase))
                SettingsManager.HomeCatalogDisabled.Add(key);

            await SettingsManager.SaveAsync();
        }

        private async void MoveCatalogUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not int index || index <= 0)
                return;

            (_homeCatalogs[index - 1], _homeCatalogs[index]) = (_homeCatalogs[index], _homeCatalogs[index - 1]);
            await SaveHomeCatalogOrderAsync();
            RenderHomeCatalogs();
        }

        private async void MoveCatalogDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not int index || index >= _homeCatalogs.Count - 1)
                return;

            (_homeCatalogs[index + 1], _homeCatalogs[index]) = (_homeCatalogs[index], _homeCatalogs[index + 1]);
            await SaveHomeCatalogOrderAsync();
            RenderHomeCatalogs();
        }

        private async Task SaveHomeCatalogOrderAsync()
        {
            SettingsManager.HomeCatalogOrder = _homeCatalogs
                .Select(DiscoverService.BuildCatalogKey)
                .ToList();

            await SettingsManager.SaveAsync();
        }

        private async void MetadataProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            if ((MetadataProviderComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "AddonDecided")
                SettingsManager.MetadataProvider = MetadataProviderMode.AddonDecided;
            else
                SettingsManager.MetadataProvider = MetadataProviderMode.Cinemeta;

            CatalogService.ClearTransientCaches();
            await SettingsManager.SaveAsync();
            await LoadHomeCatalogSettingsAsync();
        }

        private async void ShowPosterBadgesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.ShowPosterBadges = ShowPosterBadgesToggle.IsOn;
            UpdatePosterHoverBadgeOptionsVisibility();
            await SettingsManager.SaveAsync();
        }

        private async void PosterHoverBadgeOption_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.ShowPosterHoverYear = ShowPosterHoverYearToggle.IsOn;
            SettingsManager.ShowPosterHoverImdbRating = ShowPosterHoverImdbToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void PosterSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = PosterSizeComboBox.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;

            if (!Enum.TryParse<PosterSizeMode>(selected.Tag?.ToString(), true, out var mode))
                mode = PosterSizeMode.Default;

            SettingsManager.PosterSize = mode;
            await SettingsManager.SaveAsync();
        }

        private async void PlayerSingleClickPlayPauseToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.PlayerSingleClickPlayPause = PlayerSingleClickPlayPauseToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void PlayerDoubleClickFullScreenToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.PlayerDoubleClickFullScreen = PlayerDoubleClickFullScreenToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void PlaybackEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = PlaybackEngineComboBox.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;

            var tag = selected.Tag?.ToString();
            if (!Enum.TryParse<PlaybackEngineMode>(tag, true, out var engine))
                engine = PlaybackEngineMode.MPV;

            SettingsManager.PlaybackEngine = engine;
            UpdatePlaybackEngineDependentControls();
            await SettingsManager.SaveAsync();
        }

        private async void AutoFallbackCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.AutoFallbackEnabled = AutoFallbackCheckBox.IsChecked == true;
            UpdatePlaybackEngineDependentControls();
            await SettingsManager.SaveAsync();
        }

        private async void FallbackPlaybackEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || _updatingFallbackOptions)
                return;

            var selected = FallbackPlaybackEngineComboBox.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;

            if (!Enum.TryParse<PlaybackEngineMode>(selected.Tag?.ToString(), true, out var engine))
                engine = PlaybackEngineMode.LibVLC;

            SettingsManager.FallbackPlaybackEngine = engine;
            await SettingsManager.SaveAsync();
        }

        private async void ExternalPlayerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = ExternalPlayerComboBox.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;

            if (!Enum.TryParse<ExternalPlayerMode>(selected.Tag?.ToString(), true, out var player))
                player = ExternalPlayerMode.VLC;

            SettingsManager.ExternalPlayer = player;
            await SettingsManager.SaveAsync();
        }


        private void AdvancedPlayerSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (AdvancedPlayerSettingsPanel == null)
                return;

            AdvancedPlayerSettingsPanel.Visibility = AdvancedPlayerSettingsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async void MpvRetryAttemptsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = MpvRetryAttemptsComboBox.SelectedItem as ComboBoxItem;
            if (selected == null || !int.TryParse(selected.Tag?.ToString(), out var attempts))
                attempts = 3;

            SettingsManager.MpvRetryAttempts = Math.Clamp(attempts, 0, 10);
            if (SettingsManager.MpvRetryAttempts == 0 && SettingsManager.MpvRetryMessageAttempt != 0)
            {
                SettingsManager.MpvRetryMessageAttempt = 0;
                SelectMpvRetryMessageAttemptOption(SettingsManager.MpvRetryMessageAttempt);
            }
            else if (SettingsManager.MpvRetryMessageAttempt > SettingsManager.MpvRetryAttempts)
            {
                SettingsManager.MpvRetryMessageAttempt = SettingsManager.MpvRetryAttempts;
                SelectMpvRetryMessageAttemptOption(SettingsManager.MpvRetryMessageAttempt);
            }

            await SettingsManager.SaveAsync();
        }

        private async void MpvRetryMessageAttemptComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = MpvRetryMessageAttemptComboBox.SelectedItem as ComboBoxItem;
            if (selected == null || !int.TryParse(selected.Tag?.ToString(), out var attempt))
                attempt = 0;

            SettingsManager.MpvRetryMessageAttempt = attempt <= 0
                ? 0
                : Math.Clamp(attempt, 1, Math.Max(1, SettingsManager.MpvRetryAttempts));
            SelectMpvRetryMessageAttemptOption(SettingsManager.MpvRetryMessageAttempt);
            await SettingsManager.SaveAsync();
        }

        private async void DisableSubtitlesByDefaultCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.DisableSubtitlesByDefault = DisableSubtitlesByDefaultCheckBox.IsChecked == true;
            UpdateSubtitleDefaultControls();
            await SettingsManager.SaveAsync();
        }

        private async void DetailsBackgroundBrightnessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            UpdateDetailsBackgroundBrightnessValueText();

            if (_isLoading)
                return;

            SettingsManager.DetailsBackgroundBrightnessPercent = Math.Clamp((int)Math.Round(DetailsBackgroundBrightnessSlider.Value), 0, 100);
            await SettingsManager.SaveAsync();
        }

        private async void DetailsRatingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.DetailsShowImdbRating = DetailsShowImdbToggle.IsOn;
            await SettingsManager.SaveAsync();
        }


        private async void DetailsSeriesViewComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = DetailsSeriesViewComboBox.SelectedItem as ComboBoxItem;
            if (selected == null || !Enum.TryParse<DetailsSeriesViewMode>(selected.Tag?.ToString(), true, out var mode))
                mode = DetailsSeriesViewMode.Full;

            SettingsManager.DetailsSeriesView = mode;
            await SettingsManager.SaveAsync();
        }

        private async void DisableSpoilersToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.DisableSpoilers = DisableSpoilersToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void EnableNextEpisodePopupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.EnableNextEpisodePopup = EnableNextEpisodePopupToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void NextEpisodePopupSecondsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = NextEpisodePopupSecondsComboBox.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;

            if (!int.TryParse(selected.Tag?.ToString(), out var seconds) || seconds <= 0)
                seconds = 30;

            SettingsManager.NextEpisodePopupSeconds = seconds;
            await SettingsManager.SaveAsync();
        }

        private async void AutoPlayNextEpisodeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.AutoPlayNextEpisode = AutoPlayNextEpisodeToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void CheckForUpdatesAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.CheckForUpdatesAtStartup = CheckForUpdatesAtStartupToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SetUpdateBusy(true, "Checking GitHub for updates...");

            var update = await UpdateService.CheckForUpdatesAsync();
            if (!update.Succeeded)
            {
                SetUpdateBusy(false, update.Message);
                return;
            }

            if (!update.IsUpdateAvailable)
            {
                SetUpdateBusy(false, $"Cleario is up to date. Current version: {update.CurrentVersion}.");
                return;
            }

            SetUpdateBusy(true, $"Version {update.LatestVersion} is available. Downloading installer...");

            var install = await UpdateService.DownloadAndLaunchInstallerAsync(update);
            SetUpdateBusy(false, install.Message);

            if (CheckForUpdatesButton != null)
                CheckForUpdatesButton.Content = "Check again";
        }

        private async void OpenGitHubButton_Click(object sender, RoutedEventArgs e)
        {
            await UpdateService.OpenRepositoryPageAsync();
        }

        private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        {
            CrashLogger.OpenLogFolder();
        }

        private async void SaveMemoryToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.SaveMemory = SaveMemoryToggle.IsOn;
            await SettingsManager.SaveAsync();
        }

        private async void PreferredAudioLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.PreferredAudioLanguage = GetSelectedLanguage(PreferredAudioLanguageComboBox, "English");
            await SettingsManager.SaveAsync();
        }

        private async void PreferredSubtitleLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.PreferredSubtitleLanguage = GetSelectedLanguage(PreferredSubtitleLanguageComboBox, "English");
            await SettingsManager.SaveAsync();
        }

        private async void CacheImagesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SettingsManager.CacheImages = CacheImagesToggle.IsOn;
            UpdateCacheControlsEnabledState();
            await SettingsManager.SaveAsync();
            await CatalogService.EnforcePosterCacheLimitIfEnabledAsync();
        }

        private async void CacheLimitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            var selected = CacheLimitComboBox.SelectedItem as ComboBoxItem;
            if (selected == null)
                return;

            if (int.TryParse(selected.Tag?.ToString(), out var limitGb))
                SettingsManager.CacheLimitGb = limitGb;
            else
                SettingsManager.CacheLimitGb = 2;

            await SettingsManager.SaveAsync();
            await CatalogService.EnforcePosterCacheLimitIfEnabledAsync();
        }

        private async void ExportDataButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsCheckBox = new CheckBox { Content = "Settings", IsChecked = true };
            var addonsCheckBox = new CheckBox { Content = "Addons", IsChecked = true };
            var historyCheckBox = new CheckBox { Content = "Watch history", IsChecked = true };
            var libraryCheckBox = new CheckBox { Content = "Library", IsChecked = true };

            var dialogContent = new StackPanel { Spacing = 10 };
            dialogContent.Children.Add(new TextBlock
            {
                Text = "Choose what to include in the export file.",
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.8
            });
            dialogContent.Children.Add(settingsCheckBox);
            dialogContent.Children.Add(addonsCheckBox);
            dialogContent.Children.Add(historyCheckBox);
            dialogContent.Children.Add(libraryCheckBox);

            var dialog = new ContentDialog
            {
                Title = "Export Cleario data",
                Content = dialogContent,
                PrimaryButtonText = "Export",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            if (settingsCheckBox.IsChecked != true && addonsCheckBox.IsChecked != true && historyCheckBox.IsChecked != true && libraryCheckBox.IsChecked != true)
                return;

            var picker = new FileSavePicker();
            picker.FileTypeChoices.Add("Cleario export", new List<string> { ".json" });
            picker.SuggestedFileName = $"cleario-export-{DateTime.Now:yyyyMMdd-HHmm}";
            picker.DefaultFileExtension = ".json";
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));

            var file = await picker.PickSaveFileAsync();
            if (file == null)
                return;

            var exportJson = await ImportExportService.BuildExportJsonAsync(new ImportExportService.ExportOptions
            {
                IncludeSettings = settingsCheckBox.IsChecked == true,
                IncludeAddons = addonsCheckBox.IsChecked == true,
                IncludeHistory = historyCheckBox.IsChecked == true,
                IncludeLibrary = libraryCheckBox.IsChecked == true
            });

            await FileIO.WriteTextAsync(file, exportJson);
        }

        private async void ImportDataButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));

            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;

            var confirmDialog = new ContentDialog
            {
                Title = "Import Cleario data?",
                Content = "This will replace the selected parts of your current settings, addons, library, and watch history with the imported file.",
                PrimaryButtonText = "Import",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var confirmResult = await confirmDialog.ShowAsync();
            if (confirmResult != ContentDialogResult.Primary)
                return;

            var json = await FileIO.ReadTextAsync(file);
            var imported = await ImportExportService.ImportFromJsonAsync(json);

            var doneDialog = new ContentDialog
            {
                Title = imported ? "Import complete" : "Import failed",
                Content = imported
                    ? "Your data was imported. Cleario settings and lists were reloaded."
                    : "The selected file could not be imported.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await doneDialog.ShowAsync();

            if (!imported)
                return;

            await LoadSettingsIntoControlsAsync();
        }

        private async void ResetWatchStatusButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Reset watch status?",
                Content = "Are you sure you want to remove all watch history, progress, and watched markers?",
                PrimaryButtonText = "Reset",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            await HistoryService.ResetAsync();
        }

        private async void ResetFactorySettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var includeHistoryCheckBox = new CheckBox
            {
                Content = "Also delete watch history, progress, and continue watching",
                IsChecked = false
            };

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(new TextBlock
            {
                Text = "Reset Cleario back to its first-run state. This removes settings, addons, and your library.",
                TextWrapping = TextWrapping.WrapWholeWords,
                Opacity = 0.8
            });
            content.Children.Add(includeHistoryCheckBox);

            var firstDialog = new ContentDialog
            {
                Title = "Reset all settings?",
                Content = content,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            if (await firstDialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var includeHistory = includeHistoryCheckBox.IsChecked == true;
            var confirmDialog = new ContentDialog
            {
                Title = "Are you sure?",
                Content = includeHistory
                    ? "This will reset Cleario to factory settings and delete watch history too. This cannot be undone."
                    : "This will reset Cleario to factory settings. Watch history will be kept. This cannot be undone.",
                PrimaryButtonText = "Reset everything",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };

            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            await SettingsManager.ResetToFactoryDefaultsAsync();
            await AddonManager.ResetAsync();
            await LibraryService.ResetAsync();
            if (includeHistory)
                await HistoryService.ResetAsync();

            CatalogService.ClearTransientCaches();
            await CatalogService.EnforcePosterCacheLimitIfEnabledAsync();
            await LoadSettingsIntoControlsAsync();

            var doneDialog = new ContentDialog
            {
                Title = "Cleario was reset",
                Content = includeHistory
                    ? "Settings, addons, library, and watch history were reset."
                    : "Settings, addons, and library were reset. Watch history was kept.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await doneDialog.ShowAsync();
        }

        private void UpdatePosterHoverBadgeOptionsVisibility()
        {
            if (PosterHoverBadgeOptionsPanel == null)
                return;

            PosterHoverBadgeOptionsPanel.Visibility = ShowPosterBadgesToggle != null && ShowPosterBadgesToggle.IsOn
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateDetailsBackgroundBrightnessValueText()
        {
            if (DetailsBackgroundBrightnessValueTextBlock == null || DetailsBackgroundBrightnessSlider == null)
                return;

            DetailsBackgroundBrightnessValueTextBlock.Text = $"{Math.Clamp((int)Math.Round(DetailsBackgroundBrightnessSlider.Value), 0, 100)}%";
        }

        private static string ToTypeDisplayName(string type)
        {
            if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase))
                return "Series";

            return "Movie";
        }
    }
}
