using Cleario.Models;
using Cleario.Services;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using WinRT.Interop;

namespace Cleario.Pages
{
    public sealed partial class PlayerPage : Page
    {
        private static readonly HttpClient _probeClient = new()
        {
            Timeout = TimeSpan.FromSeconds(4)
        };

        [DllImport("user32.dll")]
        private static extern int ShowCursor(bool bShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

        [DllImport("user32.dll")]
        private static extern bool DestroyCursor(IntPtr hCursor);

        [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
        private static extern int RoGetActivationFactory(IntPtr runtimeClassId, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IActivationFactory factory);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

        [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private const int IDC_ARROW = 32512;
        private const int GWLP_WNDPROC = -4;
        private const uint WM_SETCURSOR = 0x0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private delegate IntPtr WindowProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [ComImport, Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IInputCursorStaticsInterop
        {
            void GetIids();
            void GetRuntimeClassName();
            void GetTrustLevel();

            [PreserveSig]
            int CreateFromHCursor(IntPtr hcursor, out IntPtr inputCursor);
        }

        [ComImport, Guid("00000035-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivationFactory
        {
            void GetIids();
            void GetRuntimeClassName();
            void GetTrustLevel();

            [PreserveSig]
            int ActivateInstance(out IntPtr instance);
        }

        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _startupTimeoutTimer;
        private readonly DispatcherTimer _controlsHideTimer;
        private readonly DispatcherTimer _cursorHideReapplyTimer;
        private readonly DispatcherTimer _singleTapTimer;
        private readonly DispatcherTimer _mpvSeekDebounceTimer;

        private readonly MenuFlyout _audioTracksFlyout = new();
        private readonly MenuFlyout _subtitleTracksFlyout = new();

        private CatalogService.StreamOption? _stream;
        private LibVLC? _libVlc;
        private MediaPlayer? _mediaPlayer;
        private Media? _currentMedia;
        private InitializedEventArgs? _videoInitArgs;
        private LibMpvWindowPlayer? _mpvPlayer;
        private NativeChildWindowHost? _mpvVideoHost;
        private Popup? _mpvOverlayPopup;
        private bool _interactionSurfaceInMpvPopup;
        private Process? _externalPlayerProcess;

        private bool _positionUpdateInProgress;
        private bool _trackRefreshInProgress;
        private bool _directPlaybackInitialized;
        private bool _isFullScreen;
        private bool _controlsVisible = true;
        private DateTime _lastControlsVisibilityChangeUtc = DateTime.MinValue;
        private DateTime _lastFullScreenToggleUtc = DateTime.MinValue;
        private bool _cursorHidden;
        private int _showCursorHideBalance;
        private bool _pointerOverInteractiveRegion;
        private IntPtr _blankCursor = IntPtr.Zero;
        private IntPtr _windowHandle = IntPtr.Zero;
        private IntPtr _oldWndProc = IntPtr.Zero;
        private WindowProcDelegate? _windowProcDelegate;
        private bool _cursorHookInstalled;
        private bool _forceBlankCursor;

        private NavigationView? _hostNavigationView;
        private bool _restorePaneVisible;

        private string _pendingDirectPlaybackUrl = string.Empty;
        private InputCursor? _hiddenProtectedCursor;
        private long _knownDurationMs;
        private long _lastSavedProgressMs = -1;
        private bool _resumePositionApplied;
        private DateTime _lastHistorySaveUtc = DateTime.MinValue;
        private DateTime _lastPointerActivityUtc = DateTime.UtcNow;
        private POINT _lastPointerScreenPosition;
        private bool _hasLastPointerScreenPosition;
        private DateTime _suppressPointerWakeUntilUtc = DateTime.MinValue;
        private bool _preferredAudioApplied;
        private bool _preferredSubtitleApplied;
        private bool _exitInProgress;
        private bool _pageClosed;
        private bool _seekOverlayActive;
        private long _pendingSeekTargetMs = -1;
        private DateTime _seekOverlayStartedUtc = DateTime.MinValue;
        private int _preferredAudioAttempts;
        private int _preferredSubtitleAttempts;
        private NextEpisodeTarget? _nextEpisodeTarget;
        private string _nextEpisodePopupRenderedVideoId = string.Empty;
        private string _nextEpisodePopupRenderedPreviewUrl = string.Empty;
        private bool _nextEpisodePopupDismissed;
        private bool _nextEpisodeActionInProgress;
        private bool _isHandlingPlaybackCompletion;
        private bool _loadingForPlaybackStartup;
        private bool _bufferingOverlayActive;
        private DateTime _suppressBufferingOverlayUntilUtc = DateTime.MinValue;
        private int _selectedAudioTrackId = int.MinValue;
        private int _selectedSubtitleTrackId = -1;
        private bool _discordPresenceUpdateInProgress;
        private bool _traktScrobbleUpdateInProgress;
        private bool? _lastTraktPlayingState;
        private DateTime _lastTraktStartSentUtc = DateTime.MinValue;
        private bool _currentEpisodeWatchedMarked;
        private bool _currentEpisodeWatchedMarkInProgress;
        private bool _currentEpisodeTraktStopSent;

        private List<TrackChoice> _lastAudioTrackSnapshot = new();
        private List<TrackChoice> _lastSubtitleTrackSnapshot = new();
        private DateTime _lastTrackRefreshUtc = DateTime.MinValue;
        private long _mpvSessionVersion;
        private Task? _mpvCleanupTask;
        private PlaybackEngineMode _activePlaybackEngine = PlaybackEngineMode.MPV;
        private int _mpvStartupAttempt;
        private bool _playbackFailureHandlingInProgress;
        private long _retryResumePositionMs;
        private long _lastKnownPlaybackPositionMs;
        private long _pendingMpvSeekTargetMs = -1;
        private DateTime _lastMpvSeekCommandUtc = DateTime.MinValue;
        private string _playbackLoadingStatusMessage = string.Empty;
        private string? _loadingLogoSourceUrl;
        private bool _loadingLogoPulseStoryboardActive;
        private DateTime _loadingOverlayShownAtUtc = DateTime.MinValue;
        private bool _mpvStartupOverlayDismissalQueued;
        private bool _timelineSeekPointerDown;
        private DateTime _seekBufferingOverlayEligibleAtUtc = DateTime.MinValue;

        private const int MpvMaxStartupAttempts = 5;
        private static readonly TimeSpan MpvSeekDebounceInterval = TimeSpan.FromMilliseconds(280);
        private static readonly TimeSpan MinimumMpvStartupLogoVisibleDuration = TimeSpan.FromMilliseconds(1100);

        private int EffectiveEmbeddedRetryAttempts => Math.Clamp(SettingsManager.MpvRetryAttempts, 0, 10);

        private int EffectiveEmbeddedRetryMessageAttempt => SettingsManager.MpvRetryMessageAttempt <= 0
            ? 0
            : Math.Clamp(SettingsManager.MpvRetryMessageAttempt, 1, Math.Max(1, EffectiveEmbeddedRetryAttempts));

        private bool UseMpvEngine => _activePlaybackEngine == PlaybackEngineMode.MPV;

        private bool UseExternalPlayerEngine => _activePlaybackEngine == PlaybackEngineMode.ExternalPlayer;

        private bool HasActivePlayer => UseMpvEngine ? _mpvPlayer != null : _mediaPlayer != null;

        private bool ActivePlayerIsPlaying => UseMpvEngine ? _mpvPlayer?.IsPlaying == true : _mediaPlayer?.IsPlaying == true;

        private long ActivePlayerTime => UseMpvEngine ? Math.Max(0, _mpvPlayer?.Time ?? 0) : Math.Max(0, _mediaPlayer?.Time ?? 0);

        private long ActivePlayerLength => UseMpvEngine ? (_mpvPlayer?.Length > 0 ? _mpvPlayer.Length : _knownDurationMs) : (_mediaPlayer?.Length > 0 ? _mediaPlayer!.Length : _knownDurationMs);

        private sealed class NextEpisodeTarget
        {
            public MetaItem Item { get; set; } = new();
            public string VideoId { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
            public string EpisodeTitle { get; set; } = string.Empty;
            public string ThumbnailUrl { get; set; } = string.Empty;
            public bool IsReleased { get; set; } = true;

            public string EpisodeCode => SeasonNumber.HasValue && EpisodeNumber.HasValue
                ? (SeasonNumber.Value <= 0 ? $"Special {EpisodeNumber.Value:00}" : $"S{SeasonNumber.Value}E{EpisodeNumber.Value:00}")
                : string.Empty;
        }

        public PlayerPage()
        {
            InitializeComponent();

            _probeClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Cleario/1.0");

            _uiTimer = new DispatcherTimer
            {
                // MPV updates its cached state on the background libmpv event thread.
                // Keep WinUI work light during startup so controls stay responsive.
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _uiTimer.Tick += UiTimer_Tick;

            _startupTimeoutTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(8)
            };
            _startupTimeoutTimer.Tick += StartupTimeoutTimer_Tick;

            _controlsHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;

            _cursorHideReapplyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _cursorHideReapplyTimer.Tick += CursorHideReapplyTimer_Tick;

            _singleTapTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };
            _singleTapTimer.Tick += SingleTapTimer_Tick;

            _mpvSeekDebounceTimer = new DispatcherTimer
            {
                Interval = MpvSeekDebounceInterval
            };
            _mpvSeekDebounceTimer.Tick += MpvSeekDebounceTimer_Tick;

            Unloaded += PlayerPage_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            PrepareForStream(e.Parameter as CatalogService.StreamOption);
            HideAppShell();
            App.MainAppWindow?.SetSearchBarVisible(false);
            _ = InitializeStreamAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            _pageClosed = true;
            RefreshFullScreenState();
            RestoreAppShell();
            if (App.MainAppWindow != null)
                App.MainAppWindow.Activated -= MainAppWindow_Activated;
            App.MainAppWindow?.SetSearchBarVisible(true);
            EnsureCursorVisible();
            CleanupPlayer();
        }

        private void EnsureMpvNativeVideoHost()
        {
            if (!UseMpvEngine || PlayerArea == null || App.MainAppWindow == null)
                return;

            if (_mpvVideoHost == null)
            {
                _mpvVideoHost = new NativeChildWindowHost();
                _mpvVideoHost.PointerActivity += MpvNativeHost_PointerActivity;
                _mpvVideoHost.Tapped += MpvNativeHost_Tapped;
                _mpvVideoHost.DoubleTapped += MpvNativeHost_DoubleTapped;
                _mpvVideoHost.KeyDown += MpvNativeHost_KeyDown;
            }

            _mpvVideoHost.EnsureCreated(App.MainAppWindow, PlayerArea);
            UpdateMpvNativeVideoBounds();
        }

        private void UpdateMpvNativeVideoBounds()
        {
            if (!UseMpvEngine || _mpvVideoHost == null || PlayerArea == null)
                return;

            _mpvVideoHost.UpdateBounds(PlayerArea);
            _mpvVideoHost.Show();
            UpdateMpvOverlayWindows();
        }

        private void MpvNativeHost_PointerActivity(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_pageClosed || _exitInProgress)
                    return;

                if (!RegisterPointerActivity())
                    return;

                _pointerOverInteractiveRegion = false;
                RootGrid.Focus(FocusState.Programmatic);
                _mpvVideoHost?.FocusNative();
                ShowControls(true);
            });
        }

        private void MpvNativeHost_Tapped(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_pageClosed || _exitInProgress)
                    return;

                RootGrid.Focus(FocusState.Programmatic);
                _mpvVideoHost?.FocusNative();
                _pointerOverInteractiveRegion = false;

                if (SettingsManager.PlayerDoubleClickFullScreen)
                {
                    _singleTapTimer.Stop();
                    _singleTapTimer.Start();
                }
                else
                {
                    ExecuteSingleTapAction();
                }
            });
        }

        private void MpvNativeHost_DoubleTapped(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_pageClosed || _exitInProgress || !SettingsManager.PlayerDoubleClickFullScreen)
                    return;

                _singleTapTimer.Stop();
                RootGrid.Focus(FocusState.Programmatic);
                _mpvVideoHost?.FocusNative();
                ToggleFullScreenFromUser();
                ShowControls(true);
            });
        }

        private void MpvNativeHost_KeyDown(object? sender, NativeChildWindowHost.NativeKeyEventArgs e)
        {
            var key = (VirtualKey)e.VirtualKey;
            if (!IsPlayerKeyboardShortcutKey(key))
                return;

            e.Handled = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_pageClosed || _exitInProgress)
                    return;

                RootGrid.Focus(FocusState.Programmatic);
                HandlePlayerKeyboardShortcut(key);
            });
        }

        private void UpdateMpvOverlayWindows()
        {
            if (_pageClosed || _exitInProgress)
                return;

            if (!UseMpvEngine || _mpvPlayer == null || _mpvVideoHost == null || PlayerArea == null || InteractionSurface == null)
            {
                RestoreInteractionSurfaceToPlayerArea();
                return;
            }

            // Keep mpv as one native GPU surface. The XAML overlay is shown in a transparent
            // popup above it only while Cleario has something visible or clickable to show.
            // When the controls are hidden, close the popup so the native mpv HWND owns the mouse
            // again; otherwise the transparent popup can keep restoring the arrow cursor.
            _mpvVideoHost.ClearClipRegion();

            if (!ShouldKeepMpvOverlayPopupOpen())
            {
                CloseMpvOverlayPopup();
                return;
            }

            EnsureMpvOverlayPopup();
            UpdateMpvOverlayPopupBounds();
        }

        private bool ShouldKeepMpvOverlayPopupOpen()
        {
            return TopOverlay.Visibility == Visibility.Visible
                || BottomOverlay.Visibility == Visibility.Visible
                || LoadingOverlay.Visibility == Visibility.Visible
                || MessagePanel.Visibility == Visibility.Visible
                || NextEpisodePopupBorder.Visibility == Visibility.Visible
                || HoverTimeBorder.Visibility == Visibility.Visible;
        }

        private void EnsureMpvOverlayPopup()
        {
            if (_pageClosed || _exitInProgress || PlayerArea == null || InteractionSurface == null || RootGrid?.XamlRoot == null)
                return;

            _mpvOverlayPopup ??= new Popup
            {
                IsLightDismissEnabled = false,
                ShouldConstrainToRootBounds = false
            };

            try
            {
                _mpvOverlayPopup.XamlRoot = RootGrid.XamlRoot;
            }
            catch
            {
            }

            var movedInteractionSurface = false;
            if (!_interactionSurfaceInMpvPopup || !ReferenceEquals(_mpvOverlayPopup.Child, InteractionSurface))
            {
                try
                {
                    if (_mpvOverlayPopup.Child != null && !ReferenceEquals(_mpvOverlayPopup.Child, InteractionSurface))
                        _mpvOverlayPopup.Child = null;

                    DetachInteractionSurfaceFromParent();
                    _mpvOverlayPopup.Child = InteractionSurface;
                    _interactionSurfaceInMpvPopup = true;
                    movedInteractionSurface = true;
                }
                catch
                {
                    _interactionSurfaceInMpvPopup = false;
                    try { _mpvOverlayPopup.IsOpen = false; } catch { }
                    return;
                }
            }

            InteractionSurface.Visibility = Visibility.Visible;
            InteractionSurface.IsHitTestVisible = true;

            try
            {
                if (!_mpvOverlayPopup.IsOpen)
                {
                    _mpvOverlayPopup.IsOpen = true;
                    movedInteractionSurface = true;
                }
            }
            catch
            {
                _interactionSurfaceInMpvPopup = false;
            }

            if (movedInteractionSurface
                && LoadingOverlay.Visibility == Visibility.Visible
                && LoadingLogoImage.Visibility == Visibility.Visible)
            {
                BeginLoadingLogoPulseStoryboard(restart: true);
            }
        }

        private void UpdateMpvOverlayPopupBounds()
        {
            if (_pageClosed || _exitInProgress || _mpvOverlayPopup == null || !_interactionSurfaceInMpvPopup || PlayerArea == null || InteractionSurface == null)
                return;

            try
            {
                var topLeft = PlayerArea.TransformToVisual(null).TransformPoint(new Point(0, 0));
                var rawWidth = Math.Max(1, PlayerArea.ActualWidth > 0 ? PlayerArea.ActualWidth : ActualWidth);
                var rawHeight = Math.Max(1, PlayerArea.ActualHeight > 0 ? PlayerArea.ActualHeight : ActualHeight);

                // Leave the normal overlapped-window resize strip free. A transparent Popup that
                // covers the very edge of the window can block left/right/bottom resizing.
                var edgeInset = _isFullScreen ? 0 : 8;
                var width = Math.Max(1, rawWidth - (edgeInset * 2));
                var height = Math.Max(1, rawHeight - edgeInset);

                _mpvOverlayPopup.HorizontalOffset = topLeft.X + edgeInset;
                _mpvOverlayPopup.VerticalOffset = topLeft.Y;
                InteractionSurface.Width = width;
                InteractionSurface.Height = height;
            }
            catch
            {
            }
        }

        private void DetachInteractionSurfaceFromParent()
        {
            if (InteractionSurface == null)
                return;

            try
            {
                if (InteractionSurface.Parent is Panel panel)
                {
                    panel.Children.Remove(InteractionSurface);
                    return;
                }

                if (InteractionSurface.Parent is ContentControl contentControl && ReferenceEquals(contentControl.Content, InteractionSurface))
                {
                    contentControl.Content = null;
                    return;
                }

                if (InteractionSurface.Parent is Border border && ReferenceEquals(border.Child, InteractionSurface))
                    border.Child = null;
            }
            catch
            {
            }
        }

        private void RestoreInteractionSurfaceToPlayerArea()
        {
            if (InteractionSurface == null || PlayerArea == null)
                return;

            if (_pageClosed || _exitInProgress || PlayerArea.XamlRoot == null)
            {
                CloseMpvOverlayPopup();
                return;
            }

            try
            {
                if (_mpvOverlayPopup != null)
                {
                    _mpvOverlayPopup.IsOpen = false;
                    if (ReferenceEquals(_mpvOverlayPopup.Child, InteractionSurface))
                        _mpvOverlayPopup.Child = null;
                }
            }
            catch
            {
            }

            try
            {
                if (!ReferenceEquals(InteractionSurface.Parent, PlayerArea))
                {
                    DetachInteractionSurfaceFromParent();
                    PlayerArea.Children.Add(InteractionSurface);
                }

                InteractionSurface.Width = double.NaN;
                InteractionSurface.Height = double.NaN;
                Canvas.SetZIndex(InteractionSurface, 1000);
                _interactionSurfaceInMpvPopup = false;
            }
            catch
            {
            }
        }

        private void CloseMpvOverlayPopup()
        {
            try
            {
                if (_mpvOverlayPopup != null)
                {
                    _mpvOverlayPopup.IsOpen = false;
                    if (!_pageClosed && !_exitInProgress && ReferenceEquals(_mpvOverlayPopup.Child, InteractionSurface))
                        _mpvOverlayPopup.Child = null;
                }
            }
            catch
            {
            }

            _interactionSurfaceInMpvPopup = false;
        }

        private void DisposeMpvNativeHosts()
        {
            CloseMpvOverlayPopup();

            if (_mpvVideoHost != null)
            {
                try
                {
                    _mpvVideoHost.PointerActivity -= MpvNativeHost_PointerActivity;
                    _mpvVideoHost.Tapped -= MpvNativeHost_Tapped;
                    _mpvVideoHost.DoubleTapped -= MpvNativeHost_DoubleTapped;
                    _mpvVideoHost.KeyDown -= MpvNativeHost_KeyDown;
                    _mpvVideoHost.Dispose();
                }
                catch { }
                _mpvVideoHost = null;
            }

            if (!_pageClosed && !_exitInProgress)
                RestoreInteractionSurfaceToPlayerArea();
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            FocusPlaybackInput();
            RefreshFullScreenState();
            UpdateFullScreenIcon();
            HideAppShell();

            if (App.MainAppWindow != null)
            {
                App.MainAppWindow.Activated -= MainAppWindow_Activated;
                App.MainAppWindow.Activated += MainAppWindow_Activated;
            }
        }

        private void MainAppWindow_Activated(object sender, WindowActivatedEventArgs e)
        {
            if (_pageClosed || _exitInProgress || !UseMpvEngine)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                FocusPlaybackInput();
                _ = Task.Delay(80).ContinueWith(_ => DispatcherQueue.TryEnqueue(FocusPlaybackInput));
            });
        }

        private void FocusPlaybackInput()
        {
            try
            {
                RootGrid?.Focus(FocusState.Programmatic);
                if (UseMpvEngine)
                {
                    EnsureMpvNativeVideoHost();
                    _mpvVideoHost?.FocusNative();
                }
            }
            catch
            {
            }
        }

        private void LoadingLogoImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ResetLoadingOverlayPresentationToSpinner();
        }

        private async System.Threading.Tasks.Task InitializeStreamAsync()
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                UpdateLoadingOverlayPresentation();
                MessagePanel.Visibility = Visibility.Collapsed;
                MessageBodyTextBlock.Visibility = Visibility.Visible;
                EnsureCursorVisible();

                if (_stream == null)
                {
                    ShowMessage("Stream missing", "No stream data was passed to the player page.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_stream.EmbeddedPageUrl) && string.IsNullOrWhiteSpace(_stream.DirectUrl))
                {
                    await OpenExternalFallbackAsync(_stream.EmbeddedPageUrl);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_stream.DirectUrl))
                {
                    if (!CatalogService.CouldBeHtmlPageUrl(_stream.DirectUrl) || CatalogService.LooksLikeDirectMediaUrl(_stream.DirectUrl))
                    {
                        if (_pageClosed || _exitInProgress)
                            return;

                        if (UseExternalPlayerEngine)
                        {
                            await LaunchExternalPlayerAsync(_stream.DirectUrl);
                            return;
                        }

                        QueueDirectPlayback(_stream.DirectUrl);
                        return;
                    }

                    var probe = await ProbePlaybackUrlAsync(_stream.DirectUrl);
                    if (_pageClosed || _exitInProgress)
                        return;

                    if (probe.UseEmbeddedBrowser)
                    {
                        await OpenExternalFallbackAsync(probe.Url);
                        return;
                    }

                    if (UseExternalPlayerEngine)
                    {
                        await LaunchExternalPlayerAsync(probe.Url);
                        return;
                    }

                    QueueDirectPlayback(probe.Url);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_stream.MagnetUrl))
                {
                    ShowMessage(
                        "Torrent-only stream",
                        "This result only exposed a torrent hash. Cleario can open direct streams and embeddable player pages, but it does not have a torrent engine yet.");
                    return;
                }

                ShowMessage("Unsupported stream", "This stream cannot be opened in-app.");
            }
            catch (Exception ex)
            {
                ShowMessage("Player failed to initialize", ex.Message);
            }
        }

        private void PrepareForStream(CatalogService.StreamOption? stream)
        {
            _stream = stream;

            var title = _stream?.ContentName;
            if (string.IsNullOrWhiteSpace(title))
                title = _stream?.DisplayName ?? "Player";

            TitleTextBlock.Text = title;
            SubtitleTextBlock.Text = BuildStreamFactsLine(_stream);
            _activePlaybackEngine = SettingsManager.PlaybackEngine;
            _mpvStartupAttempt = 0;
            _playbackFailureHandlingInProgress = false;
            _playbackLoadingStatusMessage = string.Empty;
            _loadingLogoSourceUrl = null;
            _loadingLogoPulseStoryboardActive = false;
            _loadingOverlayShownAtUtc = DateTime.MinValue;
            _mpvStartupOverlayDismissalQueued = false;
            _retryResumePositionMs = Math.Max(0, stream?.StartPositionMs ?? 0);
            _lastKnownPlaybackPositionMs = _retryResumePositionMs;
            _pendingMpvSeekTargetMs = -1;
            _lastSavedProgressMs = -1;
            _resumePositionApplied = false;
            _lastHistorySaveUtc = DateTime.MinValue;
            _preferredAudioApplied = false;
            _preferredSubtitleApplied = false;
            _preferredAudioAttempts = 0;
            _preferredSubtitleAttempts = 0;
            _exitInProgress = false;
            _pageClosed = false;
            _seekOverlayActive = false;
            _pendingSeekTargetMs = -1;
            _seekOverlayStartedUtc = DateTime.MinValue;
            _nextEpisodeTarget = null;
            _nextEpisodePopupRenderedVideoId = string.Empty;
            _nextEpisodePopupRenderedPreviewUrl = string.Empty;
            _nextEpisodePopupDismissed = false;
            _nextEpisodeActionInProgress = false;
            _isHandlingPlaybackCompletion = false;
            _loadingForPlaybackStartup = false;
            _bufferingOverlayActive = false;
            _suppressBufferingOverlayUntilUtc = DateTime.MinValue;
            _lastControlsVisibilityChangeUtc = DateTime.MinValue;
            _selectedAudioTrackId = int.MinValue;
            _selectedSubtitleTrackId = -1;
            _discordPresenceUpdateInProgress = false;
            _currentEpisodeWatchedMarked = false;
            _currentEpisodeWatchedMarkInProgress = false;
            _currentEpisodeTraktStopSent = false;
            if (MessageActionPanel != null)
                MessageActionPanel.Visibility = Visibility.Collapsed;
            _playbackLoadingStatusMessage = string.Empty;
            if (LoadingStatusTextBlock != null)
                LoadingStatusTextBlock.Visibility = Visibility.Collapsed;
            UpdateLoadingOverlayPresentation();
            HideNextEpisodePopup();
            RefreshFullScreenState();
            ShowControls(true);
            EnsureCursorVisible();
            UpdatePlayPauseIcon(false);
            UpdateFullScreenIcon();
            UpdateNextEpisodeButtonVisibility();
            _ = UpdateDiscordPresenceAsync();
        }

        private void UpdateNextEpisodeButtonVisibility()
        {
            if (NextEpisodeButton == null)
                return;

            NextEpisodeButton.Visibility = string.Equals(_stream?.ContentType, "series", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void HideNextEpisodePopup()
        {
            if (NextEpisodePopupBorder != null)
                NextEpisodePopupBorder.Visibility = Visibility.Collapsed;

            UpdateMpvOverlayWindows();
        }

        private static string BuildBaseSeriesName(string? contentName, string? fallback)
        {
            var raw = !string.IsNullOrWhiteSpace(contentName) ? contentName! : (fallback ?? string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            return Regex.Replace(raw, @"\s*\((\d+x\d{2}|special\s+\d{2})\)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        }

        private void ShowNextEpisodePopup(NextEpisodeTarget target)
        {
            if (NextEpisodePopupBorder == null || _nextEpisodePopupDismissed || !target.IsReleased)
                return;

            var popupWasVisible = NextEpisodePopupBorder.Visibility == Visibility.Visible;

            NextEpisodeHeadingTextBlock.Text = $"Next on {target.Item.Name}";
            NextEpisodeTitleTextBlock.Text = SettingsManager.DisableSpoilers || string.IsNullOrWhiteSpace(target.EpisodeTitle)
                ? target.EpisodeCode
                : $"{target.EpisodeTitle} ({target.EpisodeCode})";

            var previewUrl = !string.IsNullOrWhiteSpace(target.ThumbnailUrl)
                ? target.ThumbnailUrl
                : (!string.IsNullOrWhiteSpace(target.Item.PosterUrl) ? target.Item.PosterUrl : target.Item.Poster);
            if (string.IsNullOrWhiteSpace(previewUrl))
                previewUrl = CatalogService.PlaceholderPosterUri;
            if (NextEpisodePreviewImage != null
                && (!string.Equals(_nextEpisodePopupRenderedVideoId, target.VideoId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_nextEpisodePopupRenderedPreviewUrl, previewUrl, StringComparison.OrdinalIgnoreCase)))
            {
                if (Uri.TryCreate(previewUrl, UriKind.Absolute, out var previewUri))
                    NextEpisodePreviewImage.Source = new BitmapImage(previewUri);
                else if (NextEpisodePreviewImage.Source == null)
                    NextEpisodePreviewImage.Source = null;

                _nextEpisodePopupRenderedVideoId = target.VideoId;
                _nextEpisodePopupRenderedPreviewUrl = previewUrl;
            }
            if (NextEpisodePreviewImage != null)
                NextEpisodePreviewImage.Opacity = SettingsManager.DisableSpoilers ? 0.25 : 1.0;
            if (NextEpisodePreviewSpoilerOverlay != null)
                NextEpisodePreviewSpoilerOverlay.Visibility = SettingsManager.DisableSpoilers ? Visibility.Visible : Visibility.Collapsed;

            NextEpisodePopupBorder.Visibility = Visibility.Visible;
            if (!popupWasVisible)
                UpdateMpvOverlayWindows();
        }

        private static int NormalizeSeasonForSequence(int season)
        {
            return season <= 0 ? int.MaxValue : season;
        }

        private async System.Threading.Tasks.Task<NextEpisodeTarget?> ResolveNextEpisodeTargetAsync()
        {
            if (_stream == null || !string.Equals(_stream.ContentType, "series", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(_stream.ContentId))
                return null;

            try
            {
                var episodes = await CatalogService.GetSeriesEpisodesAsync(_stream.ContentId, _stream.SourceBaseUrl);
                if (episodes == null || episodes.Count == 0)
                    return null;

                var ordered = episodes
                    .OrderBy(x => NormalizeSeasonForSequence(x.Season))
                    .ThenBy(x => x.Episode)
                    .ToList();

                CatalogService.SeriesEpisodeOption? currentEpisode = null;
                if (!string.IsNullOrWhiteSpace(_stream.VideoId))
                    currentEpisode = ordered.FirstOrDefault(x => string.Equals(x.VideoId, _stream.VideoId, StringComparison.OrdinalIgnoreCase));

                if (currentEpisode == null && _stream.SeasonNumber.HasValue && _stream.EpisodeNumber.HasValue)
                    currentEpisode = ordered.FirstOrDefault(x => x.Season == _stream.SeasonNumber.Value && x.Episode == _stream.EpisodeNumber.Value);

                if (currentEpisode == null)
                    return null;

                var currentIndex = ordered.FindIndex(x => string.Equals(x.VideoId, currentEpisode.VideoId, StringComparison.OrdinalIgnoreCase));
                if (currentIndex < 0 || currentIndex + 1 >= ordered.Count)
                    return null;

                var next = ordered[currentIndex + 1];
                if (!next.IsReleased)
                    return null;

                var seriesName = BuildBaseSeriesName(_stream.ContentName, _stream.DisplayName);
                var posterUrl = !string.IsNullOrWhiteSpace(_stream.PosterUrl) ? _stream.PosterUrl : _stream.FallbackPosterUrl;
                var fallbackPosterUrl = !string.IsNullOrWhiteSpace(_stream.FallbackPosterUrl) ? _stream.FallbackPosterUrl : _stream.PosterUrl;

                return new NextEpisodeTarget
                {
                    Item = new MetaItem
                    {
                        Id = _stream.ContentId,
                        Type = "series",
                        Name = string.IsNullOrWhiteSpace(seriesName) ? (TitleTextBlock?.Text ?? string.Empty) : seriesName,
                        PosterUrl = posterUrl ?? string.Empty,
                        FallbackPosterUrl = fallbackPosterUrl ?? string.Empty,
                        Poster = !string.IsNullOrWhiteSpace(posterUrl) ? posterUrl : (!string.IsNullOrWhiteSpace(fallbackPosterUrl) ? fallbackPosterUrl : CatalogService.PlaceholderPosterUri),
                        Year = _stream.Year ?? string.Empty,
                        ImdbRating = _stream.ImdbRating ?? string.Empty,
                        SourceBaseUrl = _stream.SourceBaseUrl ?? string.Empty
                    },
                    VideoId = next.VideoId,
                    SeasonNumber = next.Season,
                    EpisodeNumber = next.Episode,
                    EpisodeTitle = next.Title ?? string.Empty,
                    ThumbnailUrl = !string.IsNullOrWhiteSpace(next.ThumbnailUrl) ? next.ThumbnailUrl : (posterUrl ?? string.Empty),
                    IsReleased = next.IsReleased
                };
            }
            catch
            {
                return null;
            }
        }

        private void UpdateNextEpisodePopupState(long currentTimeMs, long durationMs)
        {
            if (string.IsNullOrWhiteSpace(_stream?.ContentId) || !string.Equals(_stream?.ContentType, "series", StringComparison.OrdinalIgnoreCase))
            {
                HideNextEpisodePopup();
                return;
            }

            if (!SettingsManager.EnableNextEpisodePopup || _nextEpisodePopupDismissed || !ActivePlayerIsPlaying)
            {
                if (_nextEpisodePopupDismissed || !SettingsManager.EnableNextEpisodePopup)
                    HideNextEpisodePopup();
                return;
            }

            if (durationMs <= 0 || _nextEpisodeTarget == null || !_nextEpisodeTarget.IsReleased)
                return;

            var remainingMs = Math.Max(0, durationMs - currentTimeMs);
            if (remainingMs <= Math.Max(5, SettingsManager.NextEpisodePopupSeconds) * 1000L)
                ShowNextEpisodePopup(_nextEpisodeTarget);
            else
                HideNextEpisodePopup();
        }

        private CatalogService.StreamOption PrepareNextStreamMetadata(CatalogService.StreamOption stream, NextEpisodeTarget target)
        {
            var baseSeriesName = BuildBaseSeriesName(_stream?.ContentName, _stream?.DisplayName);
            stream.ContentType = "series";
            stream.ContentId = target.Item.Id;
            stream.SourceBaseUrl = target.Item.SourceBaseUrl ?? string.Empty;
            stream.VideoId = target.VideoId;
            stream.SeasonNumber = target.SeasonNumber;
            stream.EpisodeNumber = target.EpisodeNumber;
            stream.EpisodeTitle = target.EpisodeTitle ?? string.Empty;
            stream.ContentName = (target.SeasonNumber ?? 0) <= 0
                ? $"{baseSeriesName} (Special {(target.EpisodeNumber ?? 0):00})"
                : $"{baseSeriesName} ({target.SeasonNumber ?? 0}x{(target.EpisodeNumber ?? 0):00})";
            stream.Year = _stream?.Year ?? stream.Year;
            stream.ImdbRating = _stream?.ImdbRating ?? stream.ImdbRating;
            stream.ContentLogoUrl = _stream?.ContentLogoUrl ?? stream.ContentLogoUrl;
            stream.PosterUrl = !string.IsNullOrWhiteSpace(_stream?.PosterUrl) ? _stream!.PosterUrl : stream.PosterUrl;
            stream.FallbackPosterUrl = !string.IsNullOrWhiteSpace(_stream?.FallbackPosterUrl) ? _stream!.FallbackPosterUrl : stream.FallbackPosterUrl;
            return stream;
        }

        private static string GetUrlFolderPrefix(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                    return Path.GetDirectoryName(uri.LocalPath) ?? string.Empty;

                var path = uri.GetLeftPart(UriPartial.Path);
                var idx = path.LastIndexOf('/');
                return idx > 0 ? path[..idx] : path;
            }

            try
            {
                return Path.GetDirectoryName(url) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private CatalogService.StreamOption? PickBestNextEpisodeStream(List<CatalogService.StreamOption> streams)
        {
            if (_stream == null || streams == null || streams.Count == 0)
                return null;

            var currentFolder = GetUrlFolderPrefix(_stream.DirectUrl);
            CatalogService.StreamOption? best = null;
            var bestScore = int.MinValue;

            foreach (var candidate in streams)
            {
                var score = 0;
                if (string.Equals(candidate.AddonName, _stream.AddonName, StringComparison.OrdinalIgnoreCase))
                    score += 100;
                if (string.Equals(candidate.DisplayName, _stream.DisplayName, StringComparison.OrdinalIgnoreCase))
                    score += 80;
                if (string.Equals(candidate.Description, _stream.Description, StringComparison.OrdinalIgnoreCase))
                    score += 30;

                var candidateFolder = GetUrlFolderPrefix(candidate.DirectUrl);
                if (!string.IsNullOrWhiteSpace(currentFolder) && string.Equals(candidateFolder, currentFolder, StringComparison.OrdinalIgnoreCase))
                    score += 120;

                if (CatalogService.LooksLikeDirectMediaUrl(candidate.DirectUrl))
                    score += 20;
                if (!string.IsNullOrWhiteSpace(candidate.MagnetUrl))
                    score -= 20;
                if (!string.IsNullOrWhiteSpace(candidate.EmbeddedPageUrl))
                    score -= 30;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        private DetailsNavigationRequest BuildDetailsNavigationRequestForCurrentOrNext(NextEpisodeTarget? target, bool useNextTarget)
        {
            if (useNextTarget && target != null)
            {
                return new DetailsNavigationRequest
                {
                    Item = target.Item,
                    SeasonNumber = target.SeasonNumber,
                    VideoId = target.VideoId
                };
            }

            var item = BuildHistoryMetaItem() ?? new MetaItem
            {
                Id = _stream?.ContentId ?? string.Empty,
                Type = _stream?.ContentType ?? "movie",
                Name = BuildBaseSeriesName(_stream?.ContentName, _stream?.DisplayName),
                PosterUrl = _stream?.PosterUrl ?? string.Empty,
                FallbackPosterUrl = _stream?.FallbackPosterUrl ?? string.Empty,
                Poster = !string.IsNullOrWhiteSpace(_stream?.PosterUrl) ? _stream!.PosterUrl : (!string.IsNullOrWhiteSpace(_stream?.FallbackPosterUrl) ? _stream!.FallbackPosterUrl : CatalogService.PlaceholderPosterUri),
                Year = _stream?.Year ?? string.Empty,
                ImdbRating = _stream?.ImdbRating ?? string.Empty,
                SourceBaseUrl = _stream?.SourceBaseUrl ?? string.Empty
            };

            return new DetailsNavigationRequest
            {
                Item = item,
                SeasonNumber = _stream?.SeasonNumber,
                VideoId = _stream?.VideoId ?? string.Empty
            };
        }

        private async System.Threading.Tasks.Task SwitchToNextEpisodeOrNavigateAsync()
        {
            if (_nextEpisodeActionInProgress)
                return;

            _nextEpisodeActionInProgress = true;
            ShowNextEpisodeTransitionLoading();

            try
            {
                await MaybePersistPlaybackProgressAsync(true);
                await MarkCurrentEpisodeWatchedAsync(sendTraktStop: true);
                StopCurrentMediaForTransition();

                var nextTarget = _nextEpisodeTarget ?? await ResolveNextEpisodeTargetAsync();
                if (nextTarget == null)
                {
                    App.MainAppWindow?.ClosePlayerToPage(typeof(DetailsPage), BuildDetailsNavigationRequestForCurrentOrNext(null, false));
                    return;
                }

                try
                {
                    var nextStreams = await CatalogService.GetStreamsAsync("series", nextTarget.VideoId);
                    var matchingStream = PickBestNextEpisodeStream(nextStreams);
                    if (matchingStream != null && (!string.IsNullOrWhiteSpace(matchingStream.DirectUrl) || !string.IsNullOrWhiteSpace(matchingStream.EmbeddedPageUrl) || !string.IsNullOrWhiteSpace(matchingStream.MagnetUrl)))
                    {
                        await CleanupPlayerForStreamSwitchAsync();
                        PrepareForStream(PrepareNextStreamMetadata(matchingStream, nextTarget));
                        HideAppShell();
                        App.MainAppWindow?.SetSearchBarVisible(false);
                        _ = InitializeStreamAsync();
                        return;
                    }
                }
                catch
                {
                }

                App.MainAppWindow?.ClosePlayerToPage(typeof(DetailsPage), BuildDetailsNavigationRequestForCurrentOrNext(nextTarget, true));
            }
            finally
            {
                _nextEpisodeActionInProgress = false;
            }
        }

        private async System.Threading.Tasks.Task CleanupPlayerForStreamSwitchAsync()
        {
            CleanupPlayer();

            var cleanupTask = _mpvCleanupTask;
            if (cleanupTask == null || cleanupTask.IsCompleted)
                return;

            try
            {
                // Give the old MPV instance a short chance to release the native HWND before
                // the next episode attaches a new MPV instance. This fixes the black-screen
                // next-episode race without blocking the WinUI thread indefinitely.
                await Task.WhenAny(cleanupTask, Task.Delay(1500));
            }
            catch
            {
            }
        }

        private void ShowNextEpisodeTransitionLoading()
        {
            HideNextEpisodePopup();
            MessagePanel.Visibility = Visibility.Collapsed;
            _seekOverlayActive = false;
            _pendingSeekTargetMs = -1;
            _bufferingOverlayActive = false;
            _loadingForPlaybackStartup = true;
            LoadingOverlay.Visibility = Visibility.Visible;
            UpdateLoadingOverlayPresentation();
            EnsureCursorVisible();
            ShowControls(false);
        }

        private void StopCurrentMediaForTransition()
        {
            try
            {
                if (UseMpvEngine && _mpvPlayer != null)
                {
                    _mpvPlayer.Pause();
                    _mpvPlayer.Stop();
                }
                else if (_mediaPlayer != null)
                {
                    PauseActivePlayer();
                    _mediaPlayer.Stop();
                }
            }
            catch
            {
            }

            UpdatePlayPauseIcon(false);
        }

        private async System.Threading.Tasks.Task HandlePlaybackCompletedAsync()
        {
            if (_isHandlingPlaybackCompletion || _exitInProgress || _pageClosed)
                return;

            _isHandlingPlaybackCompletion = true;
            try
            {
                await MaybePersistPlaybackProgressAsync(true);

                if (string.Equals(_stream?.ContentType, "series", StringComparison.OrdinalIgnoreCase))
                {
                    await MarkCurrentEpisodeWatchedAsync(sendTraktStop: false);

                    if (SettingsManager.AutoPlayNextEpisode)
                    {
                        await SwitchToNextEpisodeOrNavigateAsync();
                    }
                    else
                    {
                        App.MainAppWindow?.ClosePlayerToPage(typeof(DetailsPage), BuildDetailsNavigationRequestForCurrentOrNext(null, false));
                    }
                }
                else
                {
                    var item = BuildHistoryMetaItem() ?? new MetaItem
                    {
                        Id = _stream?.ContentId ?? string.Empty,
                        Type = _stream?.ContentType ?? "movie",
                        Name = _stream?.ContentName ?? _stream?.DisplayName ?? string.Empty,
                        PosterUrl = _stream?.PosterUrl ?? string.Empty,
                        FallbackPosterUrl = _stream?.FallbackPosterUrl ?? string.Empty,
                        Poster = !string.IsNullOrWhiteSpace(_stream?.PosterUrl) ? _stream!.PosterUrl : (!string.IsNullOrWhiteSpace(_stream?.FallbackPosterUrl) ? _stream!.FallbackPosterUrl : CatalogService.PlaceholderPosterUri),
                        Year = _stream?.Year ?? string.Empty,
                        ImdbRating = _stream?.ImdbRating ?? string.Empty,
                        SourceBaseUrl = _stream?.SourceBaseUrl ?? string.Empty
                    };
                    App.MainAppWindow?.ClosePlayerToPage(typeof(DetailsPage), item);
                }
            }
            finally
            {
                _isHandlingPlaybackCompletion = false;
            }
        }

        private async System.Threading.Tasks.Task WarmNextEpisodeTargetAsync()
        {
            if (_nextEpisodeTarget != null || _stream == null || !string.Equals(_stream.ContentType, "series", StringComparison.OrdinalIgnoreCase))
                return;

            var target = await ResolveNextEpisodeTargetAsync();
            if (target == null)
                return;

            _nextEpisodeTarget = target;
            DispatcherQueue.TryEnqueue(() =>
            {
                var currentTime = ActivePlayerTime;
                var totalLength = ActivePlayerLength;
                UpdateNextEpisodePopupState(currentTime, totalLength);
            });
        }

        private static string BuildStreamFactsLine(CatalogService.StreamOption? stream)
        {
            if (stream == null)
                return string.Empty;

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(stream.Year))
                parts.Add(stream.Year);

            if (!string.IsNullOrWhiteSpace(stream.ImdbRating))
                parts.Add($"IMDb {stream.ImdbRating}");

            return string.Join(" • ", parts);
        }


        private async System.Threading.Tasks.Task UpdateTraktScrobbleAsync(bool forceStop)
        {
            if (_stream == null || !HasActivePlayer || _traktScrobbleUpdateInProgress)
                return;

            if (!SettingsManager.TraktConnected || !SettingsManager.TraktScrobblingEnabled)
                return;

            _traktScrobbleUpdateInProgress = true;

            try
            {
                var totalLength = ActivePlayerLength;
                var currentTime = ActivePlayerTime;
                var isPlaying = ActivePlayerIsPlaying;

                if (forceStop)
                {
                    await TraktService.ScrobbleStopAsync(_stream, currentTime, totalLength);
                    _lastTraktPlayingState = null;
                    return;
                }

                if (_lastTraktPlayingState == null)
                {
                    if (isPlaying)
                    {
                        await TraktService.ScrobbleStartAsync(_stream, currentTime, totalLength);
                        _lastTraktStartSentUtc = DateTime.UtcNow;
                    }
                    _lastTraktPlayingState = isPlaying;
                    return;
                }

                if (_lastTraktPlayingState.Value != isPlaying)
                {
                    if (isPlaying)
                    {
                        await TraktService.ScrobbleStartAsync(_stream, currentTime, totalLength);
                        _lastTraktStartSentUtc = DateTime.UtcNow;
                    }
                    else
                    {
                        await TraktService.ScrobblePauseAsync(_stream, currentTime, totalLength);
                    }

                    _lastTraktPlayingState = isPlaying;
                    return;
                }

                if (isPlaying && DateTime.UtcNow - _lastTraktStartSentUtc > TimeSpan.FromMinutes(10))
                {
                    await TraktService.ScrobbleStartAsync(_stream, currentTime, totalLength);
                    _lastTraktStartSentUtc = DateTime.UtcNow;
                }
            }
            finally
            {
                _traktScrobbleUpdateInProgress = false;
            }
        }

        private async System.Threading.Tasks.Task UpdateDiscordPresenceAsync()
        {
            if (_stream == null || _discordPresenceUpdateInProgress)
                return;

            _discordPresenceUpdateInProgress = true;

            try
            {
                var contentTitle = string.Equals(_stream.ContentType, "series", StringComparison.OrdinalIgnoreCase)
                    ? BuildBaseSeriesName(_stream.ContentName, _stream.DisplayName)
                    : (!string.IsNullOrWhiteSpace(_stream.ContentName) ? _stream.ContentName : _stream.DisplayName);

                var totalLength = HasActivePlayer ? ActivePlayerLength : _knownDurationMs;
                var currentTime = HasActivePlayer ? ActivePlayerTime : Math.Max(0, _stream.StartPositionMs);
                var poster = !string.IsNullOrWhiteSpace(_stream.PosterUrl)
                    ? _stream.PosterUrl
                    : (!string.IsNullOrWhiteSpace(_stream.FallbackPosterUrl) ? _stream.FallbackPosterUrl : string.Empty);

                await DiscordRichPresenceService.SetPlaybackActivityAsync(new DiscordPresenceActivityRequest
                {
                    ContentTitle = contentTitle,
                    EpisodeTitle = _stream.EpisodeTitle ?? string.Empty,
                    EpisodeCode = BuildDiscordEpisodeCode(_stream),
                    PosterUrl = poster,
                    IsPlaying = ActivePlayerIsPlaying,
                    PositionMs = currentTime,
                    DurationMs = Math.Max(0, totalLength)
                });
            }
            finally
            {
                _discordPresenceUpdateInProgress = false;
            }
        }

        private static string BuildDiscordEpisodeCode(CatalogService.StreamOption stream)
        {
            if (!stream.SeasonNumber.HasValue || !stream.EpisodeNumber.HasValue)
                return string.Empty;

            if (stream.SeasonNumber.Value <= 0)
                return $"Special {stream.EpisodeNumber.Value:00}";

            return $"S{stream.SeasonNumber.Value}E{stream.EpisodeNumber.Value:00}";
        }

        private void ShowSeekLoadingOverlay(long targetMs)
        {
            if (_pageClosed || _exitInProgress)
                return;

            // Seeking should not immediately show the buffering/startup logo. It only
            // becomes eligible after a short delay, so quick arrow presses and dragging
            // the timeline dot do not flash the logo. If MPV is still waiting for data
            // after that delay, the normal buffering overlay is allowed to appear.
            var now = DateTime.UtcNow;
            _seekOverlayActive = true;
            _pendingSeekTargetMs = Math.Max(0, targetMs);
            _seekOverlayStartedUtc = now;
            _seekBufferingOverlayEligibleAtUtc = now.AddMilliseconds(_timelineSeekPointerDown ? 900 : 650);
            _suppressBufferingOverlayUntilUtc = _seekBufferingOverlayEligibleAtUtc;
        }

        private void TryDismissSeekLoadingOverlay()
        {
            if (!_seekOverlayActive || !HasActivePlayer)
                return;

            var reachedTarget = _pendingSeekTargetMs <= 0
                || Math.Abs(ActivePlayerTime - _pendingSeekTargetMs) <= 3000
                || ActivePlayerTime >= Math.Max(0, _pendingSeekTargetMs - 2000);
            var timedOut = (DateTime.UtcNow - _seekOverlayStartedUtc) >= TimeSpan.FromSeconds(4);

            if ((!ActivePlayerIsPlaying && !timedOut) || (!reachedTarget && !timedOut))
                return;

            _seekOverlayActive = false;
            _pendingSeekTargetMs = -1;
            _seekBufferingOverlayEligibleAtUtc = DateTime.MinValue;

            if (MessagePanel.Visibility != Visibility.Visible && !_loadingForPlaybackStartup && !_bufferingOverlayActive && !_nextEpisodeActionInProgress)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ResetLoadingOverlayPresentationToSpinner();
            }
        }

        private void BeginLoadingLogoPulseStoryboard(bool restart = false)
        {
            if (!restart && _loadingLogoPulseStoryboardActive)
                return;

            try
            {
                var storyboard = Resources["LoadingLogoPulseStoryboard"] as Storyboard;
                if (storyboard == null)
                {
                    _loadingLogoPulseStoryboardActive = false;
                    LoadingLogoImage.Opacity = 0.88;
                    return;
                }

                foreach (var child in storyboard.Children)
                {
                    Storyboard.SetTarget(child, LoadingLogoImage);
                    Storyboard.SetTargetProperty(child, "Opacity");
                }

                if (restart)
                    storyboard.Stop();

                LoadingLogoImage.Opacity = 0.42;
                storyboard.Begin();
                _loadingLogoPulseStoryboardActive = true;
            }
            catch
            {
                _loadingLogoPulseStoryboardActive = false;
                LoadingLogoImage.Opacity = 0.88;
            }
        }

        private void StopLoadingLogoPulseStoryboard()
        {
            try
            {
                (Resources["LoadingLogoPulseStoryboard"] as Storyboard)?.Stop();
            }
            catch
            {
            }
            finally
            {
                _loadingLogoPulseStoryboardActive = false;
                LoadingLogoImage.Opacity = 0.88;
            }
        }

        private void MarkLoadingOverlayVisible()
        {
            if (LoadingOverlay.Visibility == Visibility.Visible && _loadingOverlayShownAtUtc == DateTime.MinValue)
                _loadingOverlayShownAtUtc = DateTime.UtcNow;
        }

        private void UpdateLoadingOverlayPresentation()
        {
            MarkLoadingOverlayVisible();

            var logoUrl = _stream?.ContentLogoUrl;
            if (!string.IsNullOrWhiteSpace(logoUrl) && Uri.TryCreate(logoUrl, UriKind.Absolute, out var logoUri))
            {
                if (!string.Equals(_loadingLogoSourceUrl, logoUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _loadingLogoSourceUrl = logoUrl;
                    LoadingLogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(logoUri);
                    _loadingLogoPulseStoryboardActive = false;
                }

                LoadingLogoImage.Visibility = Visibility.Visible;
                LoadingLogoBackdrop.Visibility = Visibility.Visible;
                LoadingProgressRing.Visibility = Visibility.Collapsed;
                BeginLoadingLogoPulseStoryboard();
                UpdateLoadingStatusText();
                UpdateMpvOverlayWindows();
                return;
            }

            ResetLoadingOverlayPresentationToSpinner();
        }

        private void ResetLoadingOverlayPresentationToSpinner()
        {
            StopLoadingLogoPulseStoryboard();
            _loadingLogoSourceUrl = null;
            LoadingLogoImage.Source = null;
            LoadingLogoImage.Visibility = Visibility.Collapsed;
            LoadingLogoBackdrop.Visibility = Visibility.Collapsed;
            LoadingProgressRing.Visibility = Visibility.Visible;
            if (LoadingOverlay.Visibility != Visibility.Visible)
                _loadingOverlayShownAtUtc = DateTime.MinValue;
            UpdateLoadingStatusText();
            UpdateMpvOverlayWindows();
        }

        private void UpdateLoadingStatusText()
        {
            if (LoadingStatusTextBlock == null)
                return;

            if (!string.IsNullOrWhiteSpace(_playbackLoadingStatusMessage) && LoadingOverlay.Visibility == Visibility.Visible)
            {
                LoadingStatusTextBlock.Text = _playbackLoadingStatusMessage;
                LoadingStatusTextBlock.Visibility = Visibility.Visible;
                return;
            }

            var maxAttempts = EffectiveEmbeddedRetryAttempts;
            var showFromAttempt = EffectiveEmbeddedRetryMessageAttempt;
            if (!UseExternalPlayerEngine
                && _loadingForPlaybackStartup
                && maxAttempts > 1
                && showFromAttempt > 0
                && _mpvStartupAttempt >= showFromAttempt)
            {
                LoadingStatusTextBlock.Text = $"Trying {_mpvStartupAttempt}/{maxAttempts}";
                LoadingStatusTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                LoadingStatusTextBlock.Text = string.Empty;
                LoadingStatusTextBlock.Visibility = Visibility.Collapsed;
            }
        }

        private void InitializeTimelinePlaceholderForStartup()
        {
            var startMs = Math.Max(0, _stream?.StartPositionMs ?? 0);

            _positionUpdateInProgress = true;
            try
            {
                if (startMs > 0)
                {
                    PositionSlider.IsEnabled = false;
                    PositionSlider.Maximum = Math.Max(1, Math.Max(_knownDurationMs, startMs + 1000));
                    PositionSlider.Value = Math.Clamp(startMs, 0, PositionSlider.Maximum);
                    TimeTextBlock.Text = _knownDurationMs > 0
                        ? $"{FormatTime(startMs)} / {FormatTime(_knownDurationMs)}"
                        : FormatTime(startMs);
                }
                else
                {
                    PositionSlider.IsEnabled = false;
                    PositionSlider.Maximum = 1;
                    PositionSlider.Value = 0;
                    TimeTextBlock.Text = "00:00";
                }
            }
            finally
            {
                _positionUpdateInProgress = false;
            }
        }

        private void HideStaleMpvLoadingOverlayIfPlaybackIsVisible()
        {
            if (MessagePanel.Visibility == Visibility.Visible || LoadingOverlay.Visibility != Visibility.Visible || !HasActivePlayer)
                return;

            if (!ActivePlayerIsPlaying)
                return;

            if (_seekOverlayActive || _nextEpisodeActionInProgress)
                return;

            // During initial MPV startup, wait for mpv to report that the VO has rendered/been
            // configured. After that, any playing state means the buffering/startup logo is stale.
            if (UseMpvEngine && _loadingForPlaybackStartup && _mpvPlayer?.HasRenderedFrame != true)
                return;

            if (UseMpvEngine)
            {
                FinishMpvStartupLoadingOverlay();
                return;
            }

            _loadingForPlaybackStartup = false;
            _playbackLoadingStatusMessage = string.Empty;
            _bufferingOverlayActive = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ResetLoadingOverlayPresentationToSpinner();
            UpdateMpvOverlayWindows();
        }

        private bool ShouldDeferSeekBufferingOverlay()
        {
            if (!_seekOverlayActive)
                return false;

            if (_timelineSeekPointerDown)
                return true;

            var now = DateTime.UtcNow;
            if (_seekBufferingOverlayEligibleAtUtc != DateTime.MinValue && now < _seekBufferingOverlayEligibleAtUtc)
                return true;

            return false;
        }

        private void UpdateMpvBufferingOverlayState()
        {
            if (!UseMpvEngine || _mpvPlayer == null || _pageClosed || _exitInProgress || MessagePanel.Visibility == Visibility.Visible)
                return;

            if (_nextEpisodeActionInProgress || ShouldDeferSeekBufferingOverlay() || DateTime.UtcNow < _suppressBufferingOverlayUntilUtc)
                return;

            if (_loadingForPlaybackStartup && _mpvPlayer.HasRenderedFrame != true)
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                UpdateLoadingOverlayPresentation();
                return;
            }

            var mpvIsBuffering = _mpvPlayer.IsBuffering
                || (_mpvPlayer.HasRenderedFrame && _mpvPlayer.CacheBufferingState < 98 && !ActivePlayerIsPlaying);

            if (mpvIsBuffering && !_loadingForPlaybackStartup && _mpvPlayer.HasRenderedFrame)
            {
                _bufferingOverlayActive = true;
                LoadingOverlay.Visibility = Visibility.Visible;
                UpdateLoadingOverlayPresentation();
                return;
            }

            if (_seekOverlayActive && !_loadingForPlaybackStartup && _mpvPlayer.HasRenderedFrame && !ActivePlayerIsPlaying)
            {
                _bufferingOverlayActive = true;
                LoadingOverlay.Visibility = Visibility.Visible;
                UpdateLoadingOverlayPresentation();
                return;
            }

            if (_bufferingOverlayActive && (ActivePlayerIsPlaying || !mpvIsBuffering))
            {
                _bufferingOverlayActive = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ResetLoadingOverlayPresentationToSpinner();
                UpdateMpvOverlayWindows();
            }
        }

        private void EnsureMpvStartupLoadingOverlayVisible()
        {
            if (!UseMpvEngine || _pageClosed || _exitInProgress || MessagePanel.Visibility == Visibility.Visible)
                return;

            _loadingForPlaybackStartup = true;
            _bufferingOverlayActive = false;
            LoadingOverlay.Visibility = Visibility.Visible;
            MarkLoadingOverlayVisible();
            UpdateLoadingOverlayPresentation();
            UpdateMpvOverlayWindows();
        }

        private bool TryDeferMpvStartupOverlayDismissal()
        {
            if (!UseMpvEngine || LoadingOverlay.Visibility != Visibility.Visible)
                return false;

            if (_loadingOverlayShownAtUtc == DateTime.MinValue)
                _loadingOverlayShownAtUtc = DateTime.UtcNow;

            var elapsed = DateTime.UtcNow - _loadingOverlayShownAtUtc;
            var remaining = MinimumMpvStartupLogoVisibleDuration - elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            if (_mpvStartupOverlayDismissalQueued)
                return true;

            _mpvStartupOverlayDismissalQueued = true;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await System.Threading.Tasks.Task.Delay(remaining);
                }
                catch
                {
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    _mpvStartupOverlayDismissalQueued = false;
                    if (_pageClosed || _exitInProgress || !UseMpvEngine || MessagePanel.Visibility == Visibility.Visible)
                        return;

                    if (_seekOverlayActive || _nextEpisodeActionInProgress)
                        return;

                    if (_mpvPlayer?.HasRenderedFrame == true || ActivePlayerIsPlaying)
                        FinishMpvStartupLoadingOverlay();
                });
            });

            return true;
        }

        private void FinishMpvStartupLoadingOverlay()
        {
            if (TryDeferMpvStartupOverlayDismissal())
                return;

            _loadingForPlaybackStartup = false;
            _mpvStartupOverlayDismissalQueued = false;
            _playbackLoadingStatusMessage = string.Empty;
            _bufferingOverlayActive = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ResetLoadingOverlayPresentationToSpinner();
            UpdateMpvOverlayWindows();
        }

        private void QueueDirectPlayback(string url)
        {
            CleanupPlayer();
            BeginDirectPlayback(url);
        }

        private void BeginDirectPlayback(string url)
        {
            _pendingDirectPlaybackUrl = url;
            if (!UseExternalPlayerEngine && _mpvStartupAttempt <= 0)
                _mpvStartupAttempt = 1;
            _loadingForPlaybackStartup = !UseExternalPlayerEngine;
            _bufferingOverlayActive = false;
            VideoHost.IsHitTestVisible = false;
            VideoHost.Visibility = (UseMpvEngine || UseExternalPlayerEngine) ? Visibility.Collapsed : Visibility.Visible;
            MessagePanel.Visibility = Visibility.Collapsed;
            if (MessageActionPanel != null)
                MessageActionPanel.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = UseExternalPlayerEngine ? Visibility.Collapsed : Visibility.Visible;
            UpdateLoadingOverlayPresentation();
            InitializeTimelinePlaceholderForStartup();

            if (UseExternalPlayerEngine)
            {
                _ = LaunchExternalPlayerAsync(url);
                return;
            }

            if (UseMpvEngine || _videoInitArgs != null)
                StartDirectPlayback(_pendingDirectPlaybackUrl);
        }

        private static bool IsKnownMediaContentType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
                return false;

            return mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownWebsiteContentType(string mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
                return false;

            return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);
        }

        private async System.Threading.Tasks.Task<ProbeResult> ProbePlaybackUrlAsync(string url)
        {
            if (CatalogService.LooksLikeDirectMediaUrl(url))
                return new ProbeResult(url, false);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(0, 511);

                using var response = await _probeClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead);

                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                if (IsKnownMediaContentType(mediaType) || CatalogService.LooksLikeDirectMediaUrl(finalUrl))
                    return new ProbeResult(finalUrl, false);

                // If the server says this is HTML/text/json/xml, it is a website or web player,
                // not a playable media stream. Open it immediately instead of retrying the players.
                if (IsKnownWebsiteContentType(mediaType))
                    return new ProbeResult(finalUrl, true);

                return new ProbeResult(finalUrl, false);
            }
            catch
            {
                return new ProbeResult(url, false);
            }
        }

        private async System.Threading.Tasks.Task LaunchExternalPlayerAsync(string url)
        {
            _startupTimeoutTimer.Stop();
            _mpvStartupAttempt = 0;
            _playbackLoadingStatusMessage = string.Empty;
            _loadingForPlaybackStartup = false;
            _bufferingOverlayActive = false;
            _seekOverlayActive = false;
            _pendingMpvSeekTargetMs = -1;
            _mpvSeekDebounceTimer.Stop();
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ResetLoadingOverlayPresentationToSpinner();
            ShowMessage("Stream launched in external player", "Cleario opened this stream in VLC. You can return to the details page with Back or Stop.");

            if (string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                var vlcPath = FindExternalVlcPath();
                var args = BuildVlcArguments(url, _stream?.StartPositionMs ?? 0);

                if (!string.IsNullOrWhiteSpace(vlcPath) && File.Exists(vlcPath))
                {
                    _externalPlayerProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = vlcPath,
                        Arguments = args,
                        UseShellExecute = false
                    });
                }
                else
                {
                    _externalPlayerProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "vlc",
                        Arguments = args,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                ShowMessage("External player failed", ex.Message);
            }

            await System.Threading.Tasks.Task.CompletedTask;
        }

        private static string FindExternalVlcPath()
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Players", "vlc", "vlc.exe"),
                Path.Combine(baseDir, "vlc", "vlc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
            };

            return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
        }

        private static string BuildVlcArguments(string url, long startPositionMs)
        {
            var escapedUrl = url.Replace("\"", "\\\"");
            var args = $"\"{escapedUrl}\"";

            if (startPositionMs > 15_000)
            {
                var seconds = Math.Max(0, startPositionMs / 1000);
                args = $"--start-time={seconds} " + args;
            }

            return args;
        }

        private async System.Threading.Tasks.Task OpenExternalFallbackAsync(string url, string? message = null)
        {
            _startupTimeoutTimer.Stop();
            _mpvStartupAttempt = 0;
            _playbackLoadingStatusMessage = string.Empty;
            _loadingForPlaybackStartup = false;
            _bufferingOverlayActive = false;
            _seekOverlayActive = false;
            _pendingMpvSeekTargetMs = -1;
            _mpvSeekDebounceTimer.Stop();
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ResetLoadingOverlayPresentationToSpinner();

            ShowMessage("Opened site in browser", message ?? "This stream opened a website, so Cleario opened it in your default browser.");

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;

            try
            {
                await Launcher.LaunchUriAsync(uri);
            }
            catch
            {
            }
        }

        private async System.Threading.Tasks.Task<bool> TryOpenWebFallbackAfterPlaybackFailureAsync(bool onlyWhenDetectedWebsite = false)
        {
            var url = !string.IsNullOrWhiteSpace(_stream?.EmbeddedPageUrl)
                ? _stream.EmbeddedPageUrl
                : _stream?.DirectUrl;

            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (string.Equals(url, _stream?.DirectUrl, StringComparison.OrdinalIgnoreCase) &&
                CatalogService.LooksLikeDirectMediaUrl(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            if (onlyWhenDetectedWebsite && string.IsNullOrWhiteSpace(_stream?.EmbeddedPageUrl))
            {
                var probe = await ProbePlaybackUrlAsync(url);
                if (!probe.UseEmbeddedBrowser)
                    return false;

                url = probe.Url;
            }

            await OpenExternalFallbackAsync(url, onlyWhenDetectedWebsite
                ? "This stream is a website, so Cleario opened it in your default browser instead of retrying the media players."
                : "The player could not play this stream, so Cleario opened the web link in your default browser.");
            return true;
        }


        private long CaptureResumePositionForRecovery()
        {
            var activePosition = HasActivePlayer ? ActivePlayerTime : 0;
            var position = Math.Max(activePosition, _lastKnownPlaybackPositionMs);
            position = Math.Max(position, _stream?.StartPositionMs ?? 0);

            if (position > 10_000)
                position = Math.Max(0, position - 3_000);

            _retryResumePositionMs = position;
            _lastKnownPlaybackPositionMs = Math.Max(_lastKnownPlaybackPositionMs, position);
            return position;
        }

        private bool HasRetryableDirectStream()
        {
            return !string.IsNullOrWhiteSpace(_pendingDirectPlaybackUrl)
                || !string.IsNullOrWhiteSpace(_stream?.DirectUrl);
        }

        private PlaybackEngineMode NormalizeFallbackEngine(PlaybackEngineMode fallbackEngine)
        {
            if (!Enum.IsDefined(typeof(PlaybackEngineMode), fallbackEngine))
                fallbackEngine = PlaybackEngineMode.LibVLC;

            if (fallbackEngine == _activePlaybackEngine)
            {
                fallbackEngine = _activePlaybackEngine == PlaybackEngineMode.MPV
                    ? PlaybackEngineMode.LibVLC
                    : PlaybackEngineMode.MPV;
            }

            return fallbackEngine;
        }

        private string GetPlaybackEngineDisplayName(PlaybackEngineMode engine)
        {
            return engine switch
            {
                PlaybackEngineMode.MPV => "MPV",
                PlaybackEngineMode.LibVLC => "LibVLC",
                PlaybackEngineMode.ExternalPlayer => "VLC",
                _ => engine.ToString()
            };
        }

        private async System.Threading.Tasks.Task<bool> TryRetryEmbeddedPlaybackAsync(long resumePositionMs)
        {
            if (UseExternalPlayerEngine || !HasRetryableDirectStream())
                return false;

            var maxAttempts = EffectiveEmbeddedRetryAttempts;
            if (maxAttempts <= 1 || _mpvStartupAttempt >= maxAttempts)
                return false;

            var nextAttempt = Math.Clamp(_mpvStartupAttempt + 1, 1, maxAttempts);
            _playbackLoadingStatusMessage = string.Empty;
            await RestartPlaybackWithEngineAsync(_activePlaybackEngine, resumePositionMs, nextAttempt);
            return true;
        }

        private async System.Threading.Tasks.Task<bool> TryAutoFallbackAfterFailureAsync(long resumePositionMs)
        {
            if (!SettingsManager.AutoFallbackEnabled || !HasRetryableDirectStream())
                return false;

            var failedEngine = _activePlaybackEngine;
            var fallbackEngine = NormalizeFallbackEngine(SettingsManager.FallbackPlaybackEngine);
            if (fallbackEngine == _activePlaybackEngine)
                return false;

            _playbackLoadingStatusMessage = $"{GetPlaybackEngineDisplayName(failedEngine)} failed. Falling back to {GetPlaybackEngineDisplayName(fallbackEngine)}...";
            await RestartPlaybackWithEngineAsync(fallbackEngine, resumePositionMs, fallbackEngine == PlaybackEngineMode.ExternalPlayer ? 0 : 1);
            return true;
        }

        private async System.Threading.Tasks.Task RestartPlaybackWithEngineAsync(PlaybackEngineMode engine, long resumePositionMs, int mpvAttempt = 0)
        {
            var url = !string.IsNullOrWhiteSpace(_pendingDirectPlaybackUrl)
                ? _pendingDirectPlaybackUrl
                : _stream?.DirectUrl ?? string.Empty;

            if (string.IsNullOrWhiteSpace(url))
                return;

            if (_stream != null && resumePositionMs > 0)
                _stream.StartPositionMs = resumePositionMs;

            _activePlaybackEngine = engine;
            _mpvStartupAttempt = engine == PlaybackEngineMode.ExternalPlayer
                ? 0
                : Math.Clamp(mpvAttempt <= 0 ? 1 : mpvAttempt, 1, Math.Max(1, EffectiveEmbeddedRetryAttempts));
            _pendingMpvSeekTargetMs = -1;
            _loadingForPlaybackStartup = engine != PlaybackEngineMode.ExternalPlayer;
            _bufferingOverlayActive = false;
            _seekOverlayActive = false;
            _pendingSeekTargetMs = -1;
            _seekBufferingOverlayEligibleAtUtc = DateTime.MinValue;
            _timelineSeekPointerDown = false;
            _playbackFailureHandlingInProgress = false;

            CleanupPlayer();

            _activePlaybackEngine = engine;
            _mpvStartupAttempt = engine == PlaybackEngineMode.ExternalPlayer
                ? 0
                : Math.Clamp(mpvAttempt <= 0 ? 1 : mpvAttempt, 1, Math.Max(1, EffectiveEmbeddedRetryAttempts));
            _pendingDirectPlaybackUrl = url;
            _directPlaybackInitialized = false;
            _loadingForPlaybackStartup = engine != PlaybackEngineMode.ExternalPlayer;
            _bufferingOverlayActive = false;
            _seekOverlayActive = false;
            _pendingSeekTargetMs = -1;
            _seekBufferingOverlayEligibleAtUtc = DateTime.MinValue;
            _timelineSeekPointerDown = false;

            if (engine == PlaybackEngineMode.ExternalPlayer)
            {
                await LaunchExternalPlayerAsync(url);
                return;
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            MessagePanel.Visibility = Visibility.Collapsed;
            if (MessageActionPanel != null)
                MessageActionPanel.Visibility = Visibility.Collapsed;
            UpdateLoadingOverlayPresentation();
            InitializeTimelinePlaceholderForStartup();
            BeginDirectPlayback(url);
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private async System.Threading.Tasks.Task HandleMpvPlaybackFailureAsync(string title, string message, bool startupFailure, bool allowAutoRetry)
        {
            if (_pageClosed || _exitInProgress)
                return;

            if (_playbackFailureHandlingInProgress)
                return;

            _playbackFailureHandlingInProgress = true;

            try
            {
                _startupTimeoutTimer.Stop();
                var resumePositionMs = CaptureResumePositionForRecovery();

                if (await TryOpenWebFallbackAfterPlaybackFailureAsync(onlyWhenDetectedWebsite: true))
                    return;

                if (allowAutoRetry && await TryRetryEmbeddedPlaybackAsync(resumePositionMs))
                    return;

                if (await TryAutoFallbackAfterFailureAsync(resumePositionMs))
                    return;

                if (await TryOpenWebFallbackAfterPlaybackFailureAsync())
                    return;

                ShowPlaybackFailureMessage(title, message);
            }
            finally
            {
                _playbackFailureHandlingInProgress = false;
            }
        }


        private async System.Threading.Tasks.Task HandleDirectPlaybackFailureAsync(string title, string message, bool allowAutoRetry = true)
        {
            if (_pageClosed || _exitInProgress)
                return;

            if (_playbackFailureHandlingInProgress)
                return;

            _playbackFailureHandlingInProgress = true;

            try
            {
                _startupTimeoutTimer.Stop();
                var resumePositionMs = CaptureResumePositionForRecovery();

                if (await TryOpenWebFallbackAfterPlaybackFailureAsync(onlyWhenDetectedWebsite: true))
                    return;

                if (allowAutoRetry && await TryRetryEmbeddedPlaybackAsync(resumePositionMs))
                    return;

                if (await TryAutoFallbackAfterFailureAsync(resumePositionMs))
                    return;

                if (await TryOpenWebFallbackAfterPlaybackFailureAsync())
                    return;

                ShowPlaybackFailureMessage(title, message);
            }
            finally
            {
                _playbackFailureHandlingInProgress = false;
            }
        }

        private void RefreshFallbackPlaybackMenu()
        {
            if (FallbackPlaybackButton?.Flyout is not MenuFlyout flyout)
                return;

            flyout.Items.Clear();

            var options = new (PlaybackEngineMode Engine, string Label)[]
            {
                (PlaybackEngineMode.MPV, "Try MPV"),
                (PlaybackEngineMode.LibVLC, "Try LibVLC"),
                (PlaybackEngineMode.ExternalPlayer, "Open in VLC")
            };

            foreach (var option in options.Where(x => x.Engine != _activePlaybackEngine))
            {
                var item = new MenuFlyoutItem
                {
                    Text = option.Label,
                    Tag = option.Engine.ToString()
                };
                item.Click += FallbackPlaybackMenuItem_Click;
                flyout.Items.Add(item);
            }
        }

        private void ShowPlaybackFailureMessage(string title, string body)
        {
            var failedUrl = _pendingDirectPlaybackUrl;
            ShowMessage(title, body);
            if (!string.IsNullOrWhiteSpace(failedUrl))
                _pendingDirectPlaybackUrl = failedUrl;

            if (MessageActionPanel != null)
                MessageActionPanel.Visibility = HasRetryableDirectStream() ? Visibility.Visible : Visibility.Collapsed;

            if (RetryPlaybackButton != null)
                RetryPlaybackButton.Visibility = HasRetryableDirectStream() ? Visibility.Visible : Visibility.Collapsed;

            if (FallbackPlaybackButton != null)
            {
                RefreshFallbackPlaybackMenu();
                FallbackPlaybackButton.Visibility = HasRetryableDirectStream() && FallbackPlaybackButton.Flyout is MenuFlyout flyout && flyout.Items.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void VideoHost_Initialized(object sender, InitializedEventArgs e)
        {
            _videoInitArgs = e;

            if (!UseMpvEngine && !_directPlaybackInitialized && !string.IsNullOrWhiteSpace(_pendingDirectPlaybackUrl))
                StartDirectPlayback(_pendingDirectPlaybackUrl);
        }

        private async void StartDirectPlayback(string url)
        {
            if (_directPlaybackInitialized || _pageClosed || _exitInProgress)
                return;

            if (UseMpvEngine)
            {
                StartMpvPlayback(url);
                return;
            }

            if (_videoInitArgs == null)
                return;

            try
            {
                _directPlaybackInitialized = true;
                _pendingDirectPlaybackUrl = url;

                // Let WinUI draw the loading overlay before LibVLC does its first native startup work.
                // Without this, first VLC playback can look like a short app freeze.
                LoadingOverlay.Visibility = Visibility.Visible;
                UpdateLoadingOverlayPresentation();
                await System.Threading.Tasks.Task.Yield();
                await System.Threading.Tasks.Task.Delay(60);

                if (_pageClosed || _exitInProgress)
                    return;

                _libVlc = new LibVLC(enableDebugLogs: false, _videoInitArgs.SwapChainOptions);
                _mediaPlayer = new MediaPlayer(_libVlc)
                {
                    EnableHardwareDecoding = true
                };

                _mediaPlayer.Volume = Math.Clamp((int)VolumeSlider.Value, 0, 200);
                _selectedAudioTrackId = _mediaPlayer.AudioTrack;
                _selectedSubtitleTrackId = _mediaPlayer.Spu;
                ConfigureMediaPlayerInputCapture(_mediaPlayer);
                _mediaPlayer.Playing += MediaPlayer_Playing;
                _mediaPlayer.Buffering += MediaPlayer_Buffering;
                _mediaPlayer.EncounteredError += MediaPlayer_EncounteredError;
                _mediaPlayer.Stopped += MediaPlayer_Stopped;
                _mediaPlayer.EndReached += MediaPlayer_EndReached;

                VideoHost.MediaPlayer = _mediaPlayer;

                _currentMedia?.Dispose();
                _currentMedia = BuildMedia(_libVlc, url);
                var media = _currentMedia;
                // Do not parse network media synchronously before playback.
                // Some streams make LibVLC block during Parse(), which freezes the player page before
                // the loading overlay has a chance to animate. The timer/events update duration once
                // playback starts.
                if (_pageClosed || _exitInProgress)
                    return;

                _mediaPlayer.Play(media);

                _uiTimer.Start();
                _startupTimeoutTimer.Start();
            }
            catch (Exception ex)
            {
                await HandleDirectPlaybackFailureAsync("Player failed to start", ex.Message);
            }
        }
        private async void StartMpvPlayback(string url)
        {
            try
            {
                System.Threading.Interlocked.Increment(ref _mpvSessionVersion);
                _directPlaybackInitialized = true;
                _pendingDirectPlaybackUrl = url;

                if (_mpvStartupAttempt <= 0)
                    _mpvStartupAttempt = 1;

                VideoHost.Visibility = Visibility.Collapsed;
                LoadingOverlay.Visibility = Visibility.Visible;
                UpdateLoadingOverlayPresentation();
        
                EnsureMpvNativeVideoHost();
                if (_mpvVideoHost == null || _mpvVideoHost.WindowHandle == IntPtr.Zero)
                    throw new InvalidOperationException("Could not create the MPV native video host window.");

                _mpvPlayer = new LibMpvWindowPlayer(_mpvVideoHost.WindowHandle);
                _mpvPlayer.FirstFrameRendered += MpvPlayer_FirstFrameRendered;
                _mpvPlayer.EndReached += MpvPlayer_EndReached;
                _mpvPlayer.PlaybackError += MpvPlayer_PlaybackError;
                _mpvPlayer.PlaybackStateChanged += MpvPlayer_PlaybackStateChanged;

                // Open the XAML overlay popup before mpv begins loading so the logo remains
                // visible above the native mpv HWND during startup/buffering.
                UpdateMpvOverlayWindows();
                EnsureMpvStartupLoadingOverlayVisible();
                await System.Threading.Tasks.Task.Yield();

                _mpvPlayer.Start(url, Math.Clamp((int)VolumeSlider.Value, 0, 200), _stream?.StartPositionMs ?? 0);

                _selectedAudioTrackId = _mpvPlayer.AudioTrack;
                _selectedSubtitleTrackId = _mpvPlayer.SubtitleTrack;
                UpdateMpvNativeVideoBounds();
                EnsureMpvStartupLoadingOverlayVisible();
                UpdateMpvOverlayWindows();
                _uiTimer.Start();
                _startupTimeoutTimer.Start();
            }
            catch (Exception ex)
            {
                await HandleMpvPlaybackFailureAsync("MPV failed to start", ex.Message, startupFailure: true, allowAutoRetry: true);
            }
        }

        private void MpvPlayer_FirstFrameRendered(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTimelineUiFromActivePlayer();
                _mpvStartupAttempt = 0;
                _playbackLoadingStatusMessage = string.Empty;
                _bufferingOverlayActive = false;
                _seekOverlayActive = false;
                _pendingSeekTargetMs = -1;
                _startupTimeoutTimer.Stop();
                if (MessagePanel.Visibility != Visibility.Visible)
                    FinishMpvStartupLoadingOverlay();
                UpdatePlayPauseIcon(true);
                ShowControls(true);
                if (UseMpvEngine)
                    _resumePositionApplied = true;
                else
                    ApplyResumePositionIfNeeded();
                RebuildAudioFlyout();
                RebuildSubtitleFlyout();
                _ = WarmNextEpisodeTargetAsync();
            });
        }

        private void MpvPlayer_EndReached(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var length = ActivePlayerLength;
                var position = ActivePlayerTime;

                // MPV can raise end-file for failed/aborted streams. Only treat it as
                // playback completed when we were actually near the end.
                if (length > 0 && position < Math.Max(0, length - 15_000))
                {
                    await HandleMpvPlaybackFailureAsync("MPV playback stopped", "MPV stopped before the stream reached the end.", startupFailure: false, allowAutoRetry: true);
                    return;
                }

                _ = UpdateTraktScrobbleAsync(true);
                await HandlePlaybackCompletedAsync();
            });
        }

        private void MpvPlayer_PlaybackError(object? sender, string message)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                _startupTimeoutTimer.Stop();
                var startupFailure = _loadingForPlaybackStartup || _mpvPlayer?.HasRenderedFrame != true;
                await HandleMpvPlaybackFailureAsync("MPV playback failed", message, startupFailure, allowAutoRetry: true);
            });
        }

        private void MpvPlayer_PlaybackStateChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_pageClosed || _exitInProgress || !ReferenceEquals(sender, _mpvPlayer))
                    return;

                UpdateTimelineUiFromActivePlayer();
                UpdatePlayPauseIcon(ActivePlayerIsPlaying);
                UpdateMpvBufferingOverlayState();
                HideStaleMpvLoadingOverlayIfPlaybackIsVisible();
            });
        }



        private Media BuildMedia(LibVLC libVlc, string url)
        {
            var media = new Media(libVlc, new Uri(url));
            media.AddOption(":network-caching=1200");
            media.AddOption(":file-caching=400");
            media.AddOption(":input-fast-seek");
            media.AddOption(":http-reconnect=true");
            media.AddOption(":audio-replay-gain-mode=none");
            media.AddOption(":gain=1.0");
            media.AddOption(":no-audio-time-stretch");
            media.AddOption(":audio-desync=0");
            media.AddOption(":mouse-hide-timeout=1");
            media.AddOption(":no-mouse-events");
            return media;
        }

        private static void ConfigureMediaPlayerInputCapture(MediaPlayer mediaPlayer)
        {
            SetBoolPropertyIfPresent(mediaPlayer, "EnableMouseInput", false);
            SetBoolPropertyIfPresent(mediaPlayer, "EnableKeyInput", false);
        }

        private static void SetBoolPropertyIfPresent(object target, string propertyName, bool value)
        {
            try
            {
                var property = target.GetType().GetProperty(propertyName);
                if (property?.CanWrite == true && property.PropertyType == typeof(bool))
                    property.SetValue(target, value);
            }
            catch
            {
            }
        }

        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateTimelineUiFromActivePlayer();
                _loadingForPlaybackStartup = false;
                _mpvStartupAttempt = 0;
                _playbackLoadingStatusMessage = string.Empty;
                _bufferingOverlayActive = false;
                _seekOverlayActive = false;
                _pendingSeekTargetMs = -1;
                if (MessagePanel.Visibility != Visibility.Visible)
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    ResetLoadingOverlayPresentationToSpinner();
                }
                _startupTimeoutTimer.Stop();
                UpdatePlayPauseIcon(true);
                ShowControls(true);
                ApplyResumePositionIfNeeded();
                RebuildAudioFlyout();
                RebuildSubtitleFlyout();
                _ = WarmNextEpisodeTargetAsync();
            });
        }

        private void MediaPlayer_Buffering(object? sender, MediaPlayerBufferingEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_pageClosed || _exitInProgress || _mediaPlayer == null || MessagePanel.Visibility == Visibility.Visible)
                    return;

                if (_seekOverlayActive || DateTime.UtcNow < _suppressBufferingOverlayUntilUtc)
                    return;

                if (e.Cache < 98f)
                {
                    _bufferingOverlayActive = true;
                    LoadingOverlay.Visibility = Visibility.Visible;
                    UpdateLoadingOverlayPresentation();
                }
                else
                {
                    _bufferingOverlayActive = false;
                    if (!_loadingForPlaybackStartup && !_nextEpisodeActionInProgress)
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        ResetLoadingOverlayPresentationToSpinner();
                    }
                }
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            _ = UpdateTraktScrobbleAsync(true);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await HandlePlaybackCompletedAsync();
            });
        }

        private void MediaPlayer_EncounteredError(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                _startupTimeoutTimer.Stop();
                await HandleDirectPlaybackFailureAsync("Playback failed", "This stream could not be opened.");
            });
        }

        private void MediaPlayer_Stopped(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                _startupTimeoutTimer.Stop();
                UpdatePlayPauseIcon(false);

                if (_pageClosed || _exitInProgress || _isHandlingPlaybackCompletion || _nextEpisodeActionInProgress)
                    return;

                var length = ActivePlayerLength;
                var position = ActivePlayerTime;
                if (length > 0 && position > 0 && position < Math.Max(0, length - 15_000))
                    await HandleDirectPlaybackFailureAsync("Playback stopped", "This stream stopped before it reached the end.", allowAutoRetry: true);
            });
        }

        private async void StartupTimeoutTimer_Tick(object? sender, object e)
        {
            _startupTimeoutTimer.Stop();

            if (ActivePlayerIsPlaying)
                return;

            if (UseMpvEngine)
                await HandleMpvPlaybackFailureAsync("MPV loading failed", "MPV did not start before the timeout.", startupFailure: true, allowAutoRetry: true);
            else
                await HandleDirectPlaybackFailureAsync("Loading failed", "This stream did not start before the timeout.");
        }


        private void ShowMessage(string title, string body)
        {
            CleanupPlayer();
            HideNextEpisodePopup();

            VideoHost.Visibility = Visibility.Collapsed;
            _seekOverlayActive = false;
            _pendingSeekTargetMs = -1;
            _loadingForPlaybackStartup = false;
            _playbackLoadingStatusMessage = string.Empty;
            _bufferingOverlayActive = false;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ResetLoadingOverlayPresentationToSpinner();
            if (LoadingStatusTextBlock != null)
                LoadingStatusTextBlock.Visibility = Visibility.Collapsed;
            if (MessageActionPanel != null)
                MessageActionPanel.Visibility = Visibility.Collapsed;
            MessagePanel.Visibility = Visibility.Visible;
            MessageTitleTextBlock.Text = title;
            MessageBodyTextBlock.Text = body;
            MessageBodyTextBlock.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
            EnsureCursorVisible();
            ShowControls(false);
        }


        private void UpdateTimelineUiFromActivePlayer()
        {
            var totalLength = ActivePlayerLength;
            var currentTime = ActivePlayerTime;

            if (totalLength > 0)
            {
                _knownDurationMs = totalLength;
                _positionUpdateInProgress = true;
                PositionSlider.IsEnabled = true;
                PositionSlider.Maximum = totalLength;
                PositionSlider.Value = Math.Clamp(currentTime, 0, totalLength);
                _positionUpdateInProgress = false;
                TimeTextBlock.Text = $"{FormatTime(currentTime)} / {FormatTime(totalLength)}";
            }
            else
            {
                PositionSlider.IsEnabled = currentTime > 0;
                _positionUpdateInProgress = true;
                PositionSlider.Maximum = Math.Max(1, currentTime + 1000);
                PositionSlider.Value = Math.Max(0, currentTime);
                _positionUpdateInProgress = false;
                TimeTextBlock.Text = currentTime > 0 ? FormatTime(currentTime) : "00:00";
            }
        }

        private void UiTimer_Tick(object? sender, object e)
        {
            if (!HasActivePlayer)
                return;

            ReapplyHiddenCursorIfNeeded();

            // Do not call synchronous mpv_get_property here. On slow network/HDR files it can
            // stall the WinUI thread during startup. LibMpvWindowPlayer keeps these values fresh
            // from its background event loop.
            UpdateTimelineUiFromActivePlayer();
            var currentPosition = ActivePlayerTime;
            if (currentPosition > 0)
                _lastKnownPlaybackPositionMs = currentPosition;
            var totalLength = ActivePlayerLength;

            UpdatePlayPauseIcon(ActivePlayerIsPlaying);
            UpdateMpvBufferingOverlayState();
            HideStaleMpvLoadingOverlayIfPlaybackIsVisible();
            RefreshTrackLists();
            TryDismissSeekLoadingOverlay();
            UpdateNextEpisodePopupState(currentPosition, totalLength);
            _ = UpdateDiscordPresenceAsync();
            _ = UpdateTraktScrobbleAsync(false);
            _ = MaybeMarkCurrentEpisodeWatchedNearEndAsync();
            _ = MaybePersistPlaybackProgressAsync(false);
        }


        private MetaItem? BuildHistoryMetaItem()
        {
            if (_stream == null)
                return null;

            var id = !string.IsNullOrWhiteSpace(_stream.ContentId)
                ? _stream.ContentId
                : (!string.IsNullOrWhiteSpace(_stream.DirectUrl) ? _stream.DirectUrl : _stream.DisplayName);

            if (string.IsNullOrWhiteSpace(id))
                return null;

            var contentType = !string.IsNullOrWhiteSpace(_stream.ContentType) ? _stream.ContentType : "movie";
            var rawName = !string.IsNullOrWhiteSpace(_stream.ContentName) ? _stream.ContentName : _stream.DisplayName;
            if (string.Equals(contentType, "series", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(rawName))
            {
                rawName = System.Text.RegularExpressions.Regex.Replace(rawName, @"\s*\((\d+x\d{2}|Special\s+\d{2})\)\s*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            }

            return new MetaItem
            {
                Id = id,
                Type = contentType,
                Name = rawName,
                Poster = !string.IsNullOrWhiteSpace(_stream.PosterUrl) ? _stream.PosterUrl : (!string.IsNullOrWhiteSpace(_stream.FallbackPosterUrl) ? _stream.FallbackPosterUrl : CatalogService.PlaceholderPosterUri),
                PosterUrl = _stream.PosterUrl ?? string.Empty,
                FallbackPosterUrl = _stream.FallbackPosterUrl ?? string.Empty,
                Year = _stream.Year ?? string.Empty,
                ImdbRating = _stream.ImdbRating ?? string.Empty,
                SourceBaseUrl = _stream.SourceBaseUrl ?? string.Empty
            };
        }

        private void ApplyResumePositionIfNeeded()
        {
            if (_resumePositionApplied || !HasActivePlayer || _stream == null)
                return;

            if (_stream.StartPositionMs <= 15_000)
            {
                _resumePositionApplied = true;
                return;
            }

            var target = _knownDurationMs > 0
                ? Math.Min(_stream.StartPositionMs, Math.Max(0, _knownDurationMs - 1000))
                : _stream.StartPositionMs;

            if (target <= 0)
            {
                _resumePositionApplied = true;
                return;
            }

            _resumePositionApplied = true;

            // MPV already receives the continue-watching position during Start() and does
            // a single keyframe seek after the file has loaded. Repeating exact UI seeks
            // here makes some network streams stutter badly on resume.
            if (UseMpvEngine)
                return;

            _ = RetryApplyResumePositionAsync(target);
        }

        private async System.Threading.Tasks.Task RetryApplyResumePositionAsync(long targetMs)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                if (_pageClosed || _exitInProgress || !HasActivePlayer)
                    return;

                try
                {
                    SetActiveTime(targetMs);
                }
                catch
                {
                }

                await System.Threading.Tasks.Task.Delay(attempt < 3 ? 350 : 800);

                if (_pageClosed || _exitInProgress || !HasActivePlayer)
                    return;

                var current = ActivePlayerTime;
                if (current > Math.Max(0, targetMs - 5000))
                    return;
            }
        }

        private static bool ShouldAutoMarkEpisodeWatched(long positionMs, long durationMs)
        {
            if (durationMs <= 0 || positionMs <= 0)
                return false;

            if (durationMs - positionMs <= 5 * 60 * 1000L)
                return true;

            return (double)positionMs / durationMs >= 0.95;
        }

        private async System.Threading.Tasks.Task MaybeMarkCurrentEpisodeWatchedNearEndAsync()
        {
            if (!string.Equals(_stream?.ContentType, "series", StringComparison.OrdinalIgnoreCase))
                return;

            if (_currentEpisodeWatchedMarked || _currentEpisodeWatchedMarkInProgress)
                return;

            var totalLength = ActivePlayerLength;
            var currentTime = ActivePlayerTime;
            if (!ShouldAutoMarkEpisodeWatched(currentTime, totalLength))
                return;

            await MarkCurrentEpisodeWatchedAsync(sendTraktStop: false);
        }

        private async System.Threading.Tasks.Task MarkCurrentEpisodeWatchedAsync(bool sendTraktStop)
        {
            if (_stream == null || !string.Equals(_stream.ContentType, "series", StringComparison.OrdinalIgnoreCase))
                return;

            if (_currentEpisodeWatchedMarked || _currentEpisodeWatchedMarkInProgress)
            {
                if (sendTraktStop && !_currentEpisodeWatchedMarkInProgress)
                    await SendCurrentEpisodeWatchedScrobbleStopAsync();
                return;
            }

            var item = BuildHistoryMetaItem();
            if (item == null)
                return;

            var videoId = !string.IsNullOrWhiteSpace(_stream.VideoId)
                ? _stream.VideoId
                : (!string.IsNullOrWhiteSpace(_stream.ContentId) ? _stream.ContentId : item.Id);

            if (string.IsNullOrWhiteSpace(videoId))
                return;

            _currentEpisodeWatchedMarkInProgress = true;
            try
            {
                await HistoryService.SetSeriesEpisodesWatchedAsync(item, new[]
                {
                    new CatalogService.SeriesEpisodeOption
                    {
                        VideoId = videoId,
                        Season = Math.Max(0, _stream.SeasonNumber ?? 1),
                        Episode = Math.Max(1, _stream.EpisodeNumber ?? 1),
                        Title = _stream.EpisodeTitle ?? string.Empty
                    }
                }, true, markWholeSeries: false);

                _currentEpisodeWatchedMarked = true;

                if (sendTraktStop)
                    await SendCurrentEpisodeWatchedScrobbleStopAsync();
            }
            finally
            {
                _currentEpisodeWatchedMarkInProgress = false;
            }
        }

        private async System.Threading.Tasks.Task SendCurrentEpisodeWatchedScrobbleStopAsync()
        {
            if (_currentEpisodeTraktStopSent || _stream == null || !SettingsManager.TraktConnected || !SettingsManager.TraktScrobblingEnabled)
                return;

            var totalLength = ActivePlayerLength;
            if (totalLength <= 0)
                return;

            var currentTime = ActivePlayerTime;
            await TraktService.ScrobbleStopAsync(_stream, Math.Max(currentTime, Math.Max(0, totalLength - 1000)), totalLength);
            _currentEpisodeTraktStopSent = true;
        }

        private async System.Threading.Tasks.Task MaybePersistPlaybackProgressAsync(bool force)
        {
            if (!HasActivePlayer || _stream == null)
                return;

            var totalLength = ActivePlayerLength;
            var currentTime = ActivePlayerTime;
            if (currentTime <= 0 && !force)
                return;

            if (!force)
            {
                if ((DateTime.UtcNow - _lastHistorySaveUtc) < TimeSpan.FromSeconds(5))
                    return;

                if (Math.Abs(currentTime - _lastSavedProgressMs) < 5000)
                    return;
            }

            var item = BuildHistoryMetaItem();
            if (item == null)
                return;

            _stream.ContentId = item.Id;
            _stream.StreamKey = CatalogService.BuildStreamIdentity(_stream);
            await HistoryService.SaveProgressAsync(item, _stream, currentTime, totalLength);
            if (string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase) && ShouldAutoMarkEpisodeWatched(currentTime, totalLength))
                _currentEpisodeWatchedMarked = true;
            _lastSavedProgressMs = currentTime;
            _lastHistorySaveUtc = DateTime.UtcNow;
        }

        private void RefreshTrackLists()
        {
            var minRefreshInterval = UseMpvEngine && (!_preferredAudioApplied || !_preferredSubtitleApplied)
                ? TimeSpan.FromMilliseconds(250)
                : TimeSpan.FromSeconds(1);

            if ((DateTime.UtcNow - _lastTrackRefreshUtc) < minRefreshInterval)
                return;

            _lastTrackRefreshUtc = DateTime.UtcNow;

            if (!HasActivePlayer || _trackRefreshInProgress)
                return;

            _trackRefreshInProgress = true;

            try
            {
                if (UseMpvEngine && _mpvPlayer != null)
                {
                    AudioTracksSnapshotFromPlayer(_mpvPlayer.GetAudioTracks());
                    SubtitleTracksSnapshotFromPlayer(_mpvPlayer.GetSubtitleTracks());
                }
                else
                {
                    var audioTrackDescriptions = _mediaPlayer?.AudioTrackDescription;
                    AudioTracksSnapshotFromPlayer(audioTrackDescriptions);

                    var subtitleDescriptions = _mediaPlayer?.SpuDescription;
                    SubtitleTracksSnapshotFromPlayer(subtitleDescriptions);
                }
                ApplyPreferredTrackSelectionIfNeeded();
            }
            finally
            {
                _trackRefreshInProgress = false;
            }
        }

        private void AudioTracksSnapshotFromPlayer(TrackDescription[]? audioTrackDescriptions)
        {
            var currentSnapshot = new List<TrackChoice>();

            if (audioTrackDescriptions != null && audioTrackDescriptions.Length > 0)
            {
                foreach (var track in audioTrackDescriptions)
                {
                    currentSnapshot.Add(new TrackChoice(
                        track.Id,
                        string.IsNullOrWhiteSpace(track.Name) ? $"Audio {track.Id}" : track.Name));
                }
            }

            if (!_lastAudioTrackSnapshot.Select(x => $"{x.Id}|{x.Label}")
                .SequenceEqual(currentSnapshot.Select(x => $"{x.Id}|{x.Label}")))
            {
                _lastAudioTrackSnapshot = currentSnapshot;
                RebuildAudioFlyout();
            }
        }

        private void SubtitleTracksSnapshotFromPlayer(TrackDescription[]? subtitleDescriptions)
        {
            var currentSnapshot = new List<TrackChoice>
            {
                new TrackChoice(-1, "Off")
            };

            if (subtitleDescriptions != null)
            {
                foreach (var track in subtitleDescriptions)
                {
                    if (track.Id == -1)
                        continue;

                    currentSnapshot.Add(new TrackChoice(
                        track.Id,
                        string.IsNullOrWhiteSpace(track.Name) ? $"Subtitle {track.Id}" : track.Name));
                }
            }

            if (!_lastSubtitleTrackSnapshot.Select(x => $"{x.Id}|{x.Label}")
                .SequenceEqual(currentSnapshot.Select(x => $"{x.Id}|{x.Label}")))
            {
                _lastSubtitleTrackSnapshot = currentSnapshot;
                RebuildSubtitleFlyout();
            }
        }

        private void AudioTracksSnapshotFromPlayer(IReadOnlyList<PlayerTrackChoice>? audioTracks)
        {
            var currentSnapshot = new List<TrackChoice>();

            if (audioTracks != null)
            {
                foreach (var track in audioTracks)
                {
                    currentSnapshot.Add(new TrackChoice(
                        track.Id,
                        string.IsNullOrWhiteSpace(track.Label) ? $"Audio {track.Id}" : track.Label));
                }
            }

            if (!_lastAudioTrackSnapshot.Select(x => $"{x.Id}|{x.Label}")
                .SequenceEqual(currentSnapshot.Select(x => $"{x.Id}|{x.Label}")))
            {
                _lastAudioTrackSnapshot = currentSnapshot;
                RebuildAudioFlyout();
            }
        }

        private void SubtitleTracksSnapshotFromPlayer(IReadOnlyList<PlayerTrackChoice>? subtitleTracks)
        {
            var currentSnapshot = new List<TrackChoice>
            {
                new TrackChoice(-1, "Off")
            };

            if (subtitleTracks != null)
            {
                foreach (var track in subtitleTracks)
                {
                    if (track.Id < 0)
                        continue;

                    currentSnapshot.Add(new TrackChoice(
                        track.Id,
                        string.IsNullOrWhiteSpace(track.Label) ? $"Subtitle {track.Id}" : track.Label));
                }
            }

            if (!_lastSubtitleTrackSnapshot.Select(x => $"{x.Id}|{x.Label}")
                .SequenceEqual(currentSnapshot.Select(x => $"{x.Id}|{x.Label}")))
            {
                _lastSubtitleTrackSnapshot = currentSnapshot;
                RebuildSubtitleFlyout();
            }
        }

        private void ApplyPreferredTrackSelectionIfNeeded()
        {
            if (!HasActivePlayer)
                return;

            if (!_preferredAudioApplied)
            {
                var preferredAudio = SettingsManager.PreferredAudioLanguage ?? "English";
                if (string.Equals(preferredAudio, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    _preferredAudioApplied = true;
                }
                else if (_lastAudioTrackSnapshot.Count > 0)
                {
                    var match = FindTrackByLanguage(_lastAudioTrackSnapshot, preferredAudio);
                    if (match != null)
                    {
                        SetActiveAudioTrack(match.Id);
                        _selectedAudioTrackId = match.Id;
                        _preferredAudioApplied = true;
                        RebuildAudioFlyout();
                    }
                    else if (++_preferredAudioAttempts >= 20)
                    {
                        _preferredAudioApplied = true;
                    }
                }
            }

            if (!_preferredSubtitleApplied)
            {
                var preferredSubtitle = SettingsManager.PreferredSubtitleLanguage ?? "English";
                if (SettingsManager.DisableSubtitlesByDefault || string.Equals(preferredSubtitle, "Off", StringComparison.OrdinalIgnoreCase))
                {
                    SetActiveSubtitleTrack(-1);
                    _selectedSubtitleTrackId = -1;
                    _preferredSubtitleApplied = true;
                    RebuildSubtitleFlyout();
                }
                else if (_lastSubtitleTrackSnapshot.Count > 1)
                {
                    var match = FindTrackByLanguage(_lastSubtitleTrackSnapshot, preferredSubtitle);
                    if (match != null)
                    {
                        SetActiveSubtitleTrack(match.Id);
                        _selectedSubtitleTrackId = match.Id;
                        _preferredSubtitleApplied = true;
                        RebuildSubtitleFlyout();
                    }
                    else if (++_preferredSubtitleAttempts >= 20)
                    {
                        _preferredSubtitleApplied = true;
                    }
                }
            }
        }

        private static TrackChoice? FindTrackByLanguage(IEnumerable<TrackChoice> tracks, string preferredLanguage)
        {
            if (tracks == null || string.IsNullOrWhiteSpace(preferredLanguage))
                return null;

            var aliases = GetLanguageTokens(preferredLanguage)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (aliases.Count == 0)
                return null;

            TrackChoice? bestMatch = null;
            var bestScore = int.MinValue;

            foreach (var track in tracks)
            {
                if (track == null || string.IsNullOrWhiteSpace(track.Label) || string.Equals(track.Label, "Off", StringComparison.OrdinalIgnoreCase))
                    continue;

                var score = ScoreTrackLabel(track.Label, aliases);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = track;
                }
            }

            return bestScore > 0 ? bestMatch : null;
        }

        private static int ScoreTrackLabel(string label, IReadOnlyCollection<string> aliases)
        {
            if (string.IsNullOrWhiteSpace(label) || aliases == null || aliases.Count == 0)
                return 0;

            var normalizedLabel = label.Trim().ToLowerInvariant();
            var tokenSet = TokenizeTrackLabel(normalizedLabel);
            var score = 0;

            foreach (var alias in aliases)
            {
                if (tokenSet.Contains(alias))
                    score = Math.Max(score, 120);
                else if (HasWholeWord(normalizedLabel, alias))
                    score = Math.Max(score, 90);
                else if (normalizedLabel.Contains(alias, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 50);
            }

            if (normalizedLabel.Contains("commentary", StringComparison.OrdinalIgnoreCase))
                score -= 25;
            if (normalizedLabel.Contains("forced", StringComparison.OrdinalIgnoreCase))
                score -= 8;
            if (normalizedLabel.Contains("sign", StringComparison.OrdinalIgnoreCase))
                score -= 6;
            if (normalizedLabel.Contains("sdh", StringComparison.OrdinalIgnoreCase) || normalizedLabel.Contains("hearing", StringComparison.OrdinalIgnoreCase))
                score -= 4;

            return score;
        }

        private static HashSet<string> TokenizeTrackLabel(string label)
        {
            var tokens = Regex.Split(label ?? string.Empty, @"[^a-z]+")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var composite in new[] { "english", "dutch", "french", "german", "spanish", "italian", "portuguese", "polish", "romanian", "swedish", "norwegian", "danish", "finnish", "czech", "hungarian", "greek", "turkish", "russian", "ukrainian", "arabic", "hebrew", "hindi", "japanese", "korean", "chinese", "thai", "vietnamese" })
            {
                if ((label ?? string.Empty).Contains(composite, StringComparison.OrdinalIgnoreCase))
                    tokens.Add(composite);
            }

            return tokens;
        }

        private static bool HasWholeWord(string label, string alias)
        {
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(alias))
                return false;

            return Regex.IsMatch(label, $@"(?<![a-z]){Regex.Escape(alias)}(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static IReadOnlyList<string> GetLanguageTokens(string language)
        {
            return language.Trim().ToLowerInvariant() switch
            {
                "english" => new[] { "english", "eng", "en" },
                "dutch" => new[] { "dutch", "nederlands", "dut", "nl" },
                "french" => new[] { "french", "français", "fra", "fre", "fr" },
                "german" => new[] { "german", "deutsch", "ger", "deu", "de" },
                "spanish" => new[] { "spanish", "español", "esp", "spa", "es" },
                "italian" => new[] { "italian", "ita", "it" },
                "portuguese" => new[] { "portuguese", "português", "por", "pt" },
                "polish" => new[] { "polish", "pol", "pl" },
                "romanian" => new[] { "romanian", "rum", "ron", "ro" },
                "swedish" => new[] { "swedish", "swe", "sv" },
                "norwegian" => new[] { "norwegian", "nor", "no" },
                "danish" => new[] { "danish", "dan", "da" },
                "finnish" => new[] { "finnish", "fin", "fi" },
                "czech" => new[] { "czech", "ces", "cze", "cs" },
                "hungarian" => new[] { "hungarian", "hun", "hu" },
                "greek" => new[] { "greek", "ell", "gre", "el" },
                "turkish" => new[] { "turkish", "tur", "tr" },
                "russian" => new[] { "russian", "rus", "ru" },
                "ukrainian" => new[] { "ukrainian", "ukr", "ua" },
                "arabic" => new[] { "arabic", "ara", "ar" },
                "hebrew" => new[] { "hebrew", "heb", "he" },
                "hindi" => new[] { "hindi", "hin", "hi" },
                "japanese" => new[] { "japanese", "jpn", "ja" },
                "korean" => new[] { "korean", "kor", "ko" },
                "chinese" => new[] { "chinese", "chi", "zho", "cmn", "zh" },
                "thai" => new[] { "thai", "tha", "th" },
                "vietnamese" => new[] { "vietnamese", "vie", "vi" },
                _ => new[] { language.Trim() }
            };
        }

        private int GetDisplayedAudioTrackId()
        {
            // With libmpv the selected track can be applied immediately, while the
            // track-list "selected" flag may arrive a little later. Prefer Cleario's
            // last requested id so the flyout check mark follows the actual setting.
            if (UseMpvEngine && _selectedAudioTrackId != int.MinValue && _lastAudioTrackSnapshot.Any(x => x.Id == _selectedAudioTrackId))
                return _selectedAudioTrackId;

            var playerTrack = GetActiveAudioTrackId();
            if (_lastAudioTrackSnapshot.Any(x => x.Id == playerTrack))
            {
                _selectedAudioTrackId = playerTrack;
                return playerTrack;
            }

            if (_selectedAudioTrackId != int.MinValue && _lastAudioTrackSnapshot.Any(x => x.Id == _selectedAudioTrackId))
                return _selectedAudioTrackId;

            return playerTrack;
        }

        private int GetDisplayedSubtitleTrackId()
        {
            // Same as audio: for MPV, keep the check mark on the subtitle Cleario
            // selected instead of waiting for a delayed track-list refresh.
            if (UseMpvEngine && _lastSubtitleTrackSnapshot.Any(x => x.Id == _selectedSubtitleTrackId))
                return _selectedSubtitleTrackId;

            var playerTrack = GetActiveSubtitleTrackId();
            if (_lastSubtitleTrackSnapshot.Any(x => x.Id == playerTrack))
            {
                _selectedSubtitleTrackId = playerTrack;
                return playerTrack;
            }

            if (_lastSubtitleTrackSnapshot.Any(x => x.Id == _selectedSubtitleTrackId))
                return _selectedSubtitleTrackId;

            return playerTrack;
        }

        private void RebuildAudioFlyout()
        {
            _audioTracksFlyout.Items.Clear();

            if (_lastAudioTrackSnapshot.Count == 0)
            {
                _audioTracksFlyout.Items.Add(new MenuFlyoutItem
                {
                    Text = "No audio tracks",
                    IsEnabled = false
                });
                return;
            }

            var current = GetDisplayedAudioTrackId();

            foreach (var track in _lastAudioTrackSnapshot)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = track.Label,
                    IsChecked = track.Id == current,
                    Tag = track
                };
                item.Click += AudioTrackMenuItem_Click;
                _audioTracksFlyout.Items.Add(item);
            }
        }

        private void RebuildSubtitleFlyout()
        {
            _subtitleTracksFlyout.Items.Clear();

            if (_lastSubtitleTrackSnapshot.Count == 0)
            {
                _subtitleTracksFlyout.Items.Add(new MenuFlyoutItem
                {
                    Text = "No subtitles",
                    IsEnabled = false
                });
                return;
            }

            var current = GetDisplayedSubtitleTrackId();

            foreach (var track in _lastSubtitleTrackSnapshot)
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = track.Label,
                    IsChecked = track.Id == current,
                    Tag = track
                };
                item.Click += SubtitleTrackMenuItem_Click;
                _subtitleTracksFlyout.Items.Add(item);
            }
        }

        private void AudioTrackMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!HasActivePlayer || sender is not ToggleMenuFlyoutItem menuItem || menuItem.Tag is not TrackChoice track)
                return;

            SetActiveAudioTrack(track.Id);
            _selectedAudioTrackId = track.Id;
            RebuildAudioFlyout();
        }

        private void SubtitleTrackMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!HasActivePlayer || sender is not ToggleMenuFlyoutItem menuItem || menuItem.Tag is not TrackChoice track)
                return;

            SetActiveSubtitleTrack(track.Id);
            _selectedSubtitleTrackId = track.Id;
            RebuildSubtitleFlyout();
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!HasActivePlayer || _positionUpdateInProgress)
                return;

            if (ActivePlayerLength <= 0)
                return;

            var newTime = (long)e.NewValue;
            if (Math.Abs(ActivePlayerTime - newTime) > 1200)
            {
                ShowSeekLoadingOverlay(newTime);
                SetActiveTime(newTime);
            }
        }

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _timelineSeekPointerDown = true;
            InteractiveControl_PointerPressed(sender, e);
        }

        private void PositionSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _timelineSeekPointerDown = false;
            if (_seekOverlayActive)
            {
                var now = DateTime.UtcNow;
                _seekOverlayStartedUtc = now;
                _seekBufferingOverlayEligibleAtUtc = now.AddMilliseconds(650);
                _suppressBufferingOverlayUntilUtc = _seekBufferingOverlayEligibleAtUtc;
            }
        }

        private void PositionSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            PositionSlider_PointerReleased(sender, e);
        }

        private void PositionSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!RegisterPointerActivity())
                return;

            _pointerOverInteractiveRegion = true;
            ShowControls(true);

            if (PositionSlider.Maximum <= 0 || PositionSlider.ActualWidth <= 0)
                return;

            var point = e.GetCurrentPoint(PositionSlider).Position;
            var ratio = point.X / PositionSlider.ActualWidth;
            ratio = Math.Clamp(ratio, 0, 1);

            var previewTime = (long)(ratio * PositionSlider.Maximum);
            HoverTimeTextBlock.Text = FormatTime(previewTime);

            PreviewSeekFill.Width = ratio * Math.Max(0, PositionSlider.ActualWidth - 16);

            HoverTimeBorder.Visibility = Visibility.Visible;
            HoverTimeBorder.UpdateLayout();

            var tooltipWidth = HoverTimeBorder.ActualWidth <= 0 ? 70 : HoverTimeBorder.ActualWidth;
            var x = point.X - (tooltipWidth / 2.0);
            x = Math.Clamp(x, 0, Math.Max(0, PositionSlider.ActualWidth - tooltipWidth));

            HoverTimeTransform.X = x;
        }

        private void PositionSlider_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerOverInteractiveRegion = false;
            HoverTimeBorder.Visibility = Visibility.Collapsed;
            PreviewSeekFill.Width = 0;

            if (ShouldAutoHideControls())
                RestartControlsHideTimer();
        }

        private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            var safeVolume = Math.Clamp((int)e.NewValue, 0, 200);
            SetActiveVolume(safeVolume);

            VolumeIcon.Glyph = safeVolume <= 0 ? "\uE198" : "\uE15D";
        }

        private void InteractiveControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            RegisterPointerActivity(forceWake: true);
            _pointerOverInteractiveRegion = true;
            RootGrid.Focus(FocusState.Programmatic);
            ShowControls(false);
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            ExitPlayerAndReturn();
        }

        private async void RetryPlaybackButton_Click(object sender, RoutedEventArgs e)
        {
            if (!HasRetryableDirectStream())
                return;

            _playbackLoadingStatusMessage = string.Empty;
            await RestartPlaybackWithEngineAsync(_activePlaybackEngine, Math.Max(0, _retryResumePositionMs), _activePlaybackEngine == PlaybackEngineMode.ExternalPlayer ? 0 : 1);
        }

        private async void FallbackPlaybackMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!HasRetryableDirectStream() || sender is not MenuFlyoutItem menuItem)
                return;

            if (!Enum.TryParse<PlaybackEngineMode>(menuItem.Tag?.ToString(), ignoreCase: true, out var engine))
                return;

            _playbackLoadingStatusMessage = $"Switching to {GetPlaybackEngineDisplayName(engine)}...";
            await RestartPlaybackWithEngineAsync(engine, Math.Max(0, _retryResumePositionMs), engine == PlaybackEngineMode.ExternalPlayer ? 0 : 1);
        }

        private async void NextEpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.Equals(_stream?.ContentType, "series", StringComparison.OrdinalIgnoreCase))
                return;

            await SwitchToNextEpisodeOrNavigateAsync();
        }

        private void DismissNextEpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            _nextEpisodePopupDismissed = true;
            HideNextEpisodePopup();
        }

        private async void WatchNextEpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            await SwitchToNextEpisodeOrNavigateAsync();
        }

        private void AudioTracksButton_Click(object sender, RoutedEventArgs e)
        {
            _lastTrackRefreshUtc = DateTime.MinValue;
            RefreshTrackLists();
            RebuildAudioFlyout();
            FlyoutBase.SetAttachedFlyout(AudioTracksButton, _audioTracksFlyout);
            FlyoutBase.ShowAttachedFlyout(AudioTracksButton);
        }

        private void SubtitleTracksButton_Click(object sender, RoutedEventArgs e)
        {
            _lastTrackRefreshUtc = DateTime.MinValue;
            RefreshTrackLists();
            RebuildSubtitleFlyout();
            FlyoutBase.SetAttachedFlyout(SubtitleTracksButton, _subtitleTracksFlyout);
            FlyoutBase.ShowAttachedFlyout(SubtitleTracksButton);
        }

        private void FullScreenButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreenFromUser();
            ShowControls(true);
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (HandlePlayerKeyboardShortcut(e.Key))
                e.Handled = true;
        }

        private static bool IsPlayerKeyboardShortcutKey(VirtualKey key)
        {
            return key is VirtualKey.Space
                or VirtualKey.Left
                or VirtualKey.Right
                or VirtualKey.Up
                or VirtualKey.Down
                or VirtualKey.F11
                or VirtualKey.Escape;
        }

        private bool HandlePlayerKeyboardShortcut(VirtualKey key)
        {
            switch (key)
            {
                case VirtualKey.Space:
                    TogglePlayPause();
                    return true;

                case VirtualKey.Left:
                    SeekRelative(-10000);
                    return true;

                case VirtualKey.Right:
                    SeekRelative(10000);
                    return true;

                case VirtualKey.Up:
                    AdjustVolume(5);
                    return true;

                case VirtualKey.Down:
                    AdjustVolume(-5);
                    return true;

                case VirtualKey.F11:
                    ToggleFullScreenFromUser();
                    ShowControls(true);
                    return true;

                case VirtualKey.Escape:
                    RefreshFullScreenState();
                    if (_isFullScreen)
                    {
                        SetFullScreen(false);
                        ShowControls(true);
                        return true;
                    }
                    return false;
            }

            return false;
        }

        private void AdjustVolume(double delta)
        {
            var newValue = Math.Clamp(VolumeSlider.Value + delta, VolumeSlider.Minimum, VolumeSlider.Maximum);
            VolumeSlider.Value = newValue;

            SetActiveVolume((int)newValue);

            ShowControls(true);
        }

        private void PauseActivePlayer()
        {
            if (UseMpvEngine)
                _mpvPlayer?.Pause();
            else
                _mediaPlayer?.Pause();
        }

        private void SetActivePause(bool pause)
        {
            if (UseMpvEngine)
                _mpvPlayer?.SetPause(pause);
            else
                _mediaPlayer?.SetPause(pause);
        }

        private void SetActiveTime(long milliseconds)
        {
            milliseconds = Math.Max(0, milliseconds);

            if (UseMpvEngine && _mpvPlayer != null)
                QueueMpvSeek(milliseconds);
            else if (_mediaPlayer != null)
                _mediaPlayer.Time = milliseconds;
        }

        private void QueueMpvSeek(long milliseconds)
        {
            _pendingMpvSeekTargetMs = Math.Max(0, milliseconds);
            _suppressBufferingOverlayUntilUtc = DateTime.UtcNow.AddMilliseconds(900);

            var elapsedSinceLastSeek = DateTime.UtcNow - _lastMpvSeekCommandUtc;
            _mpvSeekDebounceTimer.Stop();
            _mpvSeekDebounceTimer.Interval = elapsedSinceLastSeek < MpvSeekDebounceInterval
                ? MpvSeekDebounceInterval
                : TimeSpan.FromMilliseconds(140);
            _mpvSeekDebounceTimer.Start();
        }

        private void MpvSeekDebounceTimer_Tick(object? sender, object e)
        {
            _mpvSeekDebounceTimer.Stop();

            var targetMs = _pendingMpvSeekTargetMs;
            _pendingMpvSeekTargetMs = -1;

            if (!UseMpvEngine || _mpvPlayer == null || targetMs < 0 || _pageClosed || _exitInProgress)
                return;

            try
            {
                _lastMpvSeekCommandUtc = DateTime.UtcNow;
                _suppressBufferingOverlayUntilUtc = DateTime.UtcNow.AddMilliseconds(900);
                _mpvPlayer.Time = targetMs;
            }
            catch
            {
            }
        }

        private void SetActiveVolume(int volume)
        {
            volume = Math.Clamp(volume, 0, 200);

            if (UseMpvEngine && _mpvPlayer != null)
                _mpvPlayer.Volume = volume;
            else if (_mediaPlayer != null)
                _mediaPlayer.Volume = volume;
        }

        private void SetActiveAudioTrack(int id)
        {
            if (UseMpvEngine && _mpvPlayer != null)
                _mpvPlayer.SetAudioTrack(id);
            else
                _mediaPlayer?.SetAudioTrack(id);
        }

        private void SetActiveSubtitleTrack(int id)
        {
            if (UseMpvEngine && _mpvPlayer != null)
                _mpvPlayer.SetSubtitleTrack(id);
            else
                _mediaPlayer?.SetSpu(id);
        }

        private int GetActiveAudioTrackId()
        {
            return UseMpvEngine ? (_mpvPlayer?.AudioTrack ?? int.MinValue) : (_mediaPlayer?.AudioTrack ?? int.MinValue);
        }

        private int GetActiveSubtitleTrackId()
        {
            return UseMpvEngine ? (_mpvPlayer?.SubtitleTrack ?? -1) : (_mediaPlayer?.Spu ?? -1);
        }

        private void TogglePlayPause()
        {
            if (!HasActivePlayer)
                return;

            if (ActivePlayerIsPlaying)
            {
                PauseActivePlayer();
                UpdatePlayPauseIcon(false);
                ShowControls(false);
            }
            else
            {
                SetActivePause(false);
                UpdatePlayPauseIcon(true);
                ShowControls(true);
            }
        }

        private void SeekRelative(long deltaMs)
        {
            if (!HasActivePlayer || ActivePlayerLength <= 0)
                return;

            var newTime = Math.Clamp(ActivePlayerTime + deltaMs, 0, ActivePlayerLength);
            ShowSeekLoadingOverlay(newTime);
            SetActiveTime(newTime);
            _pointerOverInteractiveRegion = false;
            ShowControls(true);
            if (ShouldAutoHideControls())
                RestartControlsHideTimer();
        }

        private void UpdatePlayPauseIcon(bool isPlaying)
        {
            PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
        }

        private void UpdateFullScreenIcon()
        {
            if (FullScreenIcon != null)
                FullScreenIcon.Glyph = _isFullScreen ? "\uE73F" : "\uE740";
        }

        private void RefreshFullScreenState()
        {
            if (App.MainAppWindow != null)
            {
                App.MainAppWindow.RefreshFullScreenState();
                _isFullScreen = App.MainAppWindow.IsFullScreen;
            }
            else
            {
                var appWindow = GetCurrentAppWindow();
                _isFullScreen = appWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            }

            UpdateFullScreenIcon();
        }

        private AppWindow? GetCurrentAppWindow()
        {
            IntPtr hwnd = GetForegroundWindow();

            if (hwnd == IntPtr.Zero && App.MainAppWindow != null)
                hwnd = WindowNative.GetWindowHandle(App.MainAppWindow);

            if (hwnd == IntPtr.Zero)
                return null;

            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }

        private void ToggleFullScreenFromUser()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastFullScreenToggleUtc) < TimeSpan.FromMilliseconds(450))
                return;

            _lastFullScreenToggleUtc = now;
            RefreshFullScreenState();
            SetFullScreen(!_isFullScreen);
        }

        private void SetFullScreen(bool fullScreen)
        {
            if (App.MainAppWindow != null)
            {
                App.MainAppWindow.SetFullScreen(fullScreen);
                _isFullScreen = App.MainAppWindow.IsFullScreen;
                UpdateFullScreenIcon();
                if (UseMpvEngine)
                    DispatcherQueue.TryEnqueue(UpdateMpvNativeVideoBounds);
                return;
            }

            var appWindow = GetCurrentAppWindow();
            if (appWindow == null)
                return;

            appWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
            _isFullScreen = fullScreen;
            UpdateFullScreenIcon();
            if (UseMpvEngine)
                DispatcherQueue.TryEnqueue(UpdateMpvNativeVideoBounds);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            ExitPlayerAndReturn();
        }

        private void ExitPlayerAndReturn()
        {
            if (_exitInProgress)
                return;

            _exitInProgress = true;
            _pageClosed = true;

            try
            {
                RefreshFullScreenState();
                ResetLoadingOverlayPresentationToSpinner();
                EnsureCursorVisible();
                RestoreAppShell();
                App.MainAppWindow?.SetSearchBarVisible(true);
                CleanupPlayer();
                App.MainAppWindow?.ClosePlayer();
            }
            catch
            {
                _exitInProgress = false;
            }
        }

        private bool RegisterPointerActivity(bool forceWake = false)
        {
            var now = DateTime.UtcNow;

            try
            {
                if (GetCursorPos(out var point))
                {
                    var moved = !_hasLastPointerScreenPosition
                        || Math.Abs(point.X - _lastPointerScreenPosition.X) > 1
                        || Math.Abs(point.Y - _lastPointerScreenPosition.Y) > 1;

                    // Native video surfaces and transparent XAML overlays can emit synthetic pointer
                    // move messages even while the physical mouse is still. Do not let those wake the
                    // overlay or the cursor after auto-hide.
                    if (!moved && !forceWake)
                        return false;

                    if (!forceWake && now < _suppressPointerWakeUntilUtc)
                        return false;

                    _lastPointerScreenPosition = point;
                    _hasLastPointerScreenPosition = true;
                }
            }
            catch
            {
            }

            _lastPointerActivityUtc = now;
            return true;
        }

        private void SnapshotPointerPositionForHide()
        {
            try
            {
                if (GetCursorPos(out var point))
                {
                    _lastPointerScreenPosition = point;
                    _hasLastPointerScreenPosition = true;
                }
            }
            catch
            {
            }
        }

        private void PlayerArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!UseMpvEngine)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateMpvNativeVideoBounds();
                UpdateMpvOverlayWindows();
            });
        }

        private void PlayerArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            FocusPlaybackInput();
        }

        private void PlayerArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!RegisterPointerActivity())
                return;

            RootGrid.Focus(FocusState.Programmatic);

            if (IsInteractiveOverlaySource(e.OriginalSource))
            {
                _pointerOverInteractiveRegion = true;
                ShowControls(true);
                return;
            }

            _pointerOverInteractiveRegion = false;
            ShowControls(true);
        }

        private void PlayerArea_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!RegisterPointerActivity(forceWake: true))
                return;

            if (IsInteractiveOverlaySource(e.OriginalSource))
            {
                _pointerOverInteractiveRegion = true;
                ShowControls(true);
                return;
            }

            _pointerOverInteractiveRegion = false;
            ShowControls(true);
        }

        private void PlayerArea_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerOverInteractiveRegion && ShouldAutoHideControls())
                RestartControlsHideTimer();
        }

        private void PlayerArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FocusPlaybackInput();

            if (IsInteractiveOverlaySource(e.OriginalSource))
            {
                _pointerOverInteractiveRegion = true;
                e.Handled = true;
                return;
            }

            _pointerOverInteractiveRegion = false;

            if (SettingsManager.PlayerDoubleClickFullScreen)
            {
                _singleTapTimer.Stop();
                _singleTapTimer.Start();
            }
            else
            {
                ExecuteSingleTapAction();
            }
        }

        private void PlayerArea_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (IsInteractiveOverlaySource(e.OriginalSource))
                return;

            _singleTapTimer.Stop();

            if (!SettingsManager.PlayerDoubleClickFullScreen)
                return;

            FocusPlaybackInput();
            ToggleFullScreenFromUser();
            ShowControls(true);
            e.Handled = true;
        }

        private void SingleTapTimer_Tick(object? sender, object e)
        {
            _singleTapTimer.Stop();
            ExecuteSingleTapAction();
        }

        private void ExecuteSingleTapAction()
        {
            if (SettingsManager.PlayerSingleClickPlayPause)
            {
                TogglePlayPause();
                return;
            }

            if (_controlsVisible)
                HideControls();
            else
                ShowControls(true);
        }

        private void OverlayInteractiveRegion_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!RegisterPointerActivity(forceWake: true))
                return;

            _pointerOverInteractiveRegion = true;
            ShowControls(true);
        }

        private void OverlayInteractiveRegion_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!RegisterPointerActivity())
                return;

            _pointerOverInteractiveRegion = true;
            ShowControls(true);
        }

        private void OverlayInteractiveRegion_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerOverInteractiveRegion = false;

            if (ShouldAutoHideControls())
                RestartControlsHideTimer();
        }

        private void OverlayInteractiveRegion_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _pointerOverInteractiveRegion = true;
            e.Handled = true;
        }

        private bool IsInteractiveOverlaySource(object? source)
        {
            if (source is not DependencyObject dependencyObject)
                return false;

            return IsDescendantOf(dependencyObject, TopOverlay) || IsDescendantOf(dependencyObject, BottomOverlay);
        }

        private static bool IsDescendantOf(DependencyObject? child, DependencyObject? ancestor)
        {
            var current = child;

            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void ControlsHideTimer_Tick(object? sender, object e)
        {
            _controlsHideTimer.Stop();
            _singleTapTimer.Stop();

            if (!ShouldAutoHideControls())
                return;

            var idleFor = DateTime.UtcNow - _lastPointerActivityUtc;
            if (idleFor < TimeSpan.FromMilliseconds(1050))
            {
                RestartControlsHideTimer();
                return;
            }

            HideControls();
        }

        private void ShowControls(bool autoHideLater)
        {
            _lastPointerActivityUtc = DateTime.UtcNow;

            if (!_controlsVisible || TopOverlay.Visibility != Visibility.Visible || BottomOverlay.Visibility != Visibility.Visible)
            {
                _controlsVisible = true;
                _lastControlsVisibilityChangeUtc = DateTime.UtcNow;
                TopOverlay.Visibility = Visibility.Visible;
                BottomOverlay.Visibility = Visibility.Visible;
                TopOverlay.Opacity = 1;
                BottomOverlay.Opacity = 1;
            }

            EnsureCursorVisible();

            UpdateMpvOverlayWindows();

            if (autoHideLater && ShouldAutoHideControls())
                RestartControlsHideTimer();
            else
                _controlsHideTimer.Stop();
        }

        private void HideControls()
        {
            if (LoadingOverlay.Visibility == Visibility.Visible || MessagePanel.Visibility == Visibility.Visible)
                return;

            // Hide even if the cursor is resting on the seek bar/buttons. When the mouse stops moving,
            // Cleario should behave like VLC: the overlay and cursor disappear together.

            // Prevent rapid hide/show/hide races when WinUI sends duplicate pointer events
            // while the mouse crosses the seek bar/top bar.
            if ((DateTime.UtcNow - _lastControlsVisibilityChangeUtc) < TimeSpan.FromMilliseconds(450))
            {
                RestartControlsHideTimer();
                return;
            }

            if (!_controlsVisible && TopOverlay.Visibility == Visibility.Collapsed && BottomOverlay.Visibility == Visibility.Collapsed)
                return;

            SnapshotPointerPositionForHide();
            _suppressPointerWakeUntilUtc = DateTime.UtcNow.AddMilliseconds(350);

            _controlsVisible = false;
            _lastControlsVisibilityChangeUtc = DateTime.UtcNow;
            TopOverlay.Visibility = Visibility.Collapsed;
            BottomOverlay.Visibility = Visibility.Collapsed;
            HoverTimeBorder.Visibility = Visibility.Collapsed;
            PreviewSeekFill.Width = 0;
            _controlsHideTimer.Stop();
            EnsureCursorHidden();
            UpdateMpvOverlayWindows();
        }

        private void RestartControlsHideTimer()
        {
            _controlsHideTimer.Stop();
            _controlsHideTimer.Start();
        }

        private bool ShouldAutoHideControls()
        {
            if (LoadingOverlay.Visibility == Visibility.Visible || MessagePanel.Visibility == Visibility.Visible)
                return false;

            if (HasActivePlayer && ActivePlayerIsPlaying)
                return true;

            return false;
        }

        private bool IsCursorOverActualInteractiveControl()
        {
            if (InteractionSurface == null || App.MainAppWindow == null)
                return false;

            if (!GetCursorPointInInteractionSurface(out var point))
                return false;

            return IsPointOverElement(point, BackButton, 8, 8)
                || IsPointOverElement(point, PositionSlider, 10, 12)
                || IsPointOverElement(point, PlayPauseButton, 4, 4)
                || IsPointOverElement(point, StopButton, 4, 4)
                || IsPointOverElement(point, NextEpisodeButton, 4, 4)
                || IsPointOverElement(point, VolumeSlider, 8, 8)
                || IsPointOverElement(point, AudioTracksButton, 4, 4)
                || IsPointOverElement(point, SubtitleTracksButton, 4, 4)
                || IsPointOverElement(point, FullScreenButton, 4, 4)
                || IsPointOverElement(point, DismissNextEpisodeButton, 4, 4)
                || IsPointOverElement(point, WatchNextEpisodeButton, 4, 4);
        }

        private bool GetCursorPointInInteractionSurface(out Point point)
        {
            point = default;

            if (PlayerArea == null || InteractionSurface == null || App.MainAppWindow == null || InteractionSurface.XamlRoot == null)
                return false;

            try
            {
                if (!GetCursorPos(out var screenPoint))
                    return false;

                var ownerHwnd = WindowNative.GetWindowHandle(App.MainAppWindow);
                if (ownerHwnd == IntPtr.Zero)
                    return false;

                var clientTopLeft = new POINT { X = 0, Y = 0 };
                if (!ClientToScreen(ownerHwnd, ref clientTopLeft))
                    return false;

                var scale = InteractionSurface.XamlRoot.RasterizationScale;
                var rootX = (screenPoint.X - clientTopLeft.X) / scale;
                var rootY = (screenPoint.Y - clientTopLeft.Y) / scale;
                var surfaceTopLeft = InteractionSurface.TransformToVisual(null).TransformPoint(new Point(0, 0));

                point = new Point(rootX - surfaceTopLeft.X, rootY - surfaceTopLeft.Y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsPointOverElement(Point point, FrameworkElement? element, double inflateX = 0, double inflateY = 0)
        {
            if (element == null || InteractionSurface == null || element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;

            try
            {
                var topLeft = element.TransformToVisual(InteractionSurface).TransformPoint(new Point(0, 0));
                return point.X >= topLeft.X - inflateX
                    && point.X <= topLeft.X + element.ActualWidth + inflateX
                    && point.Y >= topLeft.Y - inflateY
                    && point.Y <= topLeft.Y + element.ActualHeight + inflateY;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyProtectedCursorToPlayer(InputCursor? cursor)
        {
            try { ProtectedCursor = cursor; } catch { }

            SetProtectedCursor(RootGrid, cursor);
            SetProtectedCursor(PlayerArea, cursor);
            SetProtectedCursor(InteractionSurface, cursor);
            SetProtectedCursor(VideoHost, cursor);
            SetProtectedCursor(TopOverlay, cursor);
            SetProtectedCursor(BottomOverlay, cursor);
            SetProtectedCursor(LoadingOverlay, cursor);
            SetProtectedCursor(MessagePanel, cursor);
        }

        private static void SetProtectedCursor(UIElement? element, InputCursor? cursor)
        {
            if (element == null)
                return;

            try
            {
                typeof(UIElement).InvokeMember(
                    "ProtectedCursor",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                    null,
                    element,
                    new object?[] { cursor });
            }
            catch
            {
            }
        }

        private InputCursor? GetHiddenInputCursor()
        {
            if (_hiddenProtectedCursor != null)
                return _hiddenProtectedCursor;

            try
            {
                var blankCursor = GetBlankCursor();
                if (blankCursor == IntPtr.Zero)
                    return null;

                _hiddenProtectedCursor = CreateInputCursorFromHCursor(blankCursor);
            }
            catch
            {
                _hiddenProtectedCursor = null;
            }

            return _hiddenProtectedCursor;
        }

        private static InputCursor? CreateInputCursorFromHCursor(IntPtr hcursor)
        {
            if (hcursor == IntPtr.Zero)
                return null;

            const string runtimeClassName = "Microsoft.UI.Input.InputCursor";
            var hstring = IntPtr.Zero;
            try
            {
                if (WindowsCreateString(runtimeClassName, runtimeClassName.Length, out hstring) != 0 || hstring == IntPtr.Zero)
                    return null;

                if (RoGetActivationFactory(hstring, typeof(IActivationFactory).GUID, out var factory) != 0)
                    return null;

                if (factory is not IInputCursorStaticsInterop interop)
                    return null;

                if (interop.CreateFromHCursor(hcursor, out var cursorAbi) != 0 || cursorAbi == IntPtr.Zero)
                    return null;

                return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursorAbi);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hstring != IntPtr.Zero)
                {
                    try { WindowsDeleteString(hstring); } catch { }
                }
            }
        }

        private void EnsureCursorHookInstalled()
        {
            if (_cursorHookInstalled || App.MainAppWindow == null)
                return;

            try
            {
                _windowHandle = WindowNative.GetWindowHandle(App.MainAppWindow);
                if (_windowHandle == IntPtr.Zero)
                    return;

                _windowProcDelegate = WindowProcOverride;
                var newWndProc = Marshal.GetFunctionPointerForDelegate(_windowProcDelegate);
                _oldWndProc = IntPtr.Size == 8
                    ? SetWindowLongPtr64(_windowHandle, GWLP_WNDPROC, newWndProc)
                    : SetWindowLong32(_windowHandle, GWLP_WNDPROC, newWndProc);

                if (_oldWndProc != IntPtr.Zero)
                    _cursorHookInstalled = true;
            }
            catch
            {
                _cursorHookInstalled = false;
            }
        }

        private void RemoveCursorHook()
        {
            if (!_cursorHookInstalled || _windowHandle == IntPtr.Zero || _oldWndProc == IntPtr.Zero)
                return;

            try
            {
                if (IntPtr.Size == 8)
                    SetWindowLongPtr64(_windowHandle, GWLP_WNDPROC, _oldWndProc);
                else
                    SetWindowLong32(_windowHandle, GWLP_WNDPROC, _oldWndProc);
            }
            catch
            {
            }

            _cursorHookInstalled = false;
            _oldWndProc = IntPtr.Zero;
            _windowHandle = IntPtr.Zero;
            _windowProcDelegate = null;
            _forceBlankCursor = false;
        }

        private IntPtr WindowProcOverride(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_SETCURSOR && _forceBlankCursor)
            {
                try
                {
                    var blankCursor = GetBlankCursor();
                    SetCursor(blankCursor != IntPtr.Zero ? blankCursor : IntPtr.Zero);
                    return new IntPtr(1);
                }
                catch
                {
                }
            }

            return _oldWndProc != IntPtr.Zero
                ? CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam)
                : IntPtr.Zero;
        }

        private IntPtr GetBlankCursor()
        {
            if (_blankCursor != IntPtr.Zero)
                return _blankCursor;

            try
            {
                var andMask = Enumerable.Repeat((byte)0xFF, 128).ToArray();
                var xorMask = new byte[128];
                _blankCursor = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andMask, xorMask);
            }
            catch
            {
                _blankCursor = IntPtr.Zero;
            }

            return _blankCursor;
        }

        private void CursorHideReapplyTimer_Tick(object? sender, object e)
        {
            if (!_cursorHidden || _controlsVisible || LoadingOverlay.Visibility == Visibility.Visible || MessagePanel.Visibility == Visibility.Visible)
            {
                _cursorHideReapplyTimer.Stop();
                return;
            }

            ReapplyHiddenCursorIfNeeded();
        }

        private void ReapplyHiddenCursorIfNeeded()
        {
            if (!_cursorHidden)
                return;

            try
            {
                _forceBlankCursor = true;
                _mpvVideoHost?.SetCursorHidden(true);
                ApplyProtectedCursorToPlayer(GetHiddenInputCursor());

                var blankCursor = GetBlankCursor();
                if (blankCursor != IntPtr.Zero)
                    SetCursor(blankCursor);

                // WinUI, LibVLC, and MPV can restore the cursor from different layers.
                // Push the display counter back below zero, then balance every call on wake/exit.
                for (var i = 0; i < 4; i++)
                {
                    var counter = ShowCursor(false);
                    _showCursorHideBalance++;
                    if (counter < 0)
                        break;
                }

                _cursorHideReapplyTimer.Start();
            }
            catch
            {
            }
        }

        private void EnsureCursorHidden()
        {
            if (_cursorHidden)
            {
                ReapplyHiddenCursorIfNeeded();
                if (!_cursorHideReapplyTimer.IsEnabled)
                    _cursorHideReapplyTimer.Start();
                return;
            }

            try
            {
                EnsureCursorHookInstalled();
                _forceBlankCursor = true;
                _mpvVideoHost?.SetCursorHidden(true);
                ApplyProtectedCursorToPlayer(GetHiddenInputCursor());

                // LibVLC, MPV, and WinUI can all restore the cursor from different HWND/UIElement
                // layers. Force both the WinUI ProtectedCursor and the Win32 cursor display counter
                // into the hidden state, then restore the exact number of ShowCursor calls on wake/exit.
                for (var i = 0; i < 32; i++)
                {
                    var counter = ShowCursor(false);
                    _showCursorHideBalance++;
                    if (counter < 0)
                        break;
                }

                var blankCursor = GetBlankCursor();
                if (blankCursor != IntPtr.Zero)
                    SetCursor(blankCursor);
            }
            catch
            {
            }

            _cursorHideReapplyTimer.Start();

            _cursorHidden = true;
        }

        private void EnsureCursorVisible()
        {
            try
            {
                _cursorHideReapplyTimer.Stop();
                _forceBlankCursor = false;
                _mpvVideoHost?.SetCursorHidden(false);
                ApplyProtectedCursorToPlayer(null);

                while (_showCursorHideBalance > 0)
                {
                    ShowCursor(true);
                    _showCursorHideBalance--;
                }

                var arrow = LoadCursor(IntPtr.Zero, IDC_ARROW);
                if (arrow != IntPtr.Zero)
                    SetCursor(arrow);
            }
            catch
            {
                _showCursorHideBalance = 0;
            }

            try
            {
                ProtectedCursor = null;
            }
            catch
            {
            }

            _cursorHidden = false;
        }

        private void HideAppShell()
        {
            if (_hostNavigationView == null)
                _hostNavigationView = FindAncestor<NavigationView>(this) ?? FindAncestor<NavigationView>(RootGrid);

            if (_hostNavigationView == null)
                return;

            _restorePaneVisible = _hostNavigationView.IsPaneVisible;
            _hostNavigationView.IsPaneVisible = false;
            _hostNavigationView.IsPaneOpen = false;
        }

        private void RestoreAppShell()
        {
            if (_hostNavigationView == null)
                return;

            _hostNavigationView.IsPaneVisible = _restorePaneVisible;
        }

        private T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
        {
            var current = start;

            while (current != null)
            {
                if (current is T match)
                    return match;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void PlayerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _pageClosed = true;
            RefreshFullScreenState();
            RestoreAppShell();
            App.MainAppWindow?.SetSearchBarVisible(true);
            EnsureCursorVisible();
            CleanupPlayer();
            _exitInProgress = false;
        }

        private void CleanupPlayer()
        {
            _seekOverlayActive = false;
            _pendingSeekTargetMs = -1;
            _seekOverlayStartedUtc = DateTime.MinValue;
            _nextEpisodeTarget = null;
            _nextEpisodePopupRenderedVideoId = string.Empty;
            _nextEpisodePopupRenderedPreviewUrl = string.Empty;
            _nextEpisodePopupDismissed = false;
            _nextEpisodeActionInProgress = false;
            _loadingForPlaybackStartup = false;
            _bufferingOverlayActive = false;
            _suppressBufferingOverlayUntilUtc = DateTime.MinValue;
            _loadingLogoSourceUrl = null;
            _loadingLogoPulseStoryboardActive = false;
            _loadingOverlayShownAtUtc = DateTime.MinValue;
            _mpvStartupOverlayDismissalQueued = false;
            HideNextEpisodePopup();
            _ = MaybePersistPlaybackProgressAsync(true);
            _ = UpdateTraktScrobbleAsync(true);
            _uiTimer.Stop();
            _startupTimeoutTimer.Stop();
            _controlsHideTimer.Stop();
            _singleTapTimer.Stop();
            _mpvSeekDebounceTimer.Stop();
            _pendingMpvSeekTargetMs = -1;
            EnsureCursorVisible();

            if (LoadingStatusTextBlock != null)
                LoadingStatusTextBlock.Visibility = Visibility.Collapsed;
            if (MessageActionPanel != null)
                MessageActionPanel.Visibility = Visibility.Collapsed;

            HoverTimeBorder.Visibility = Visibility.Collapsed;
            PreviewSeekFill.Width = 0;
            ResetLoadingOverlayPresentationToSpinner();
            VideoHost.IsHitTestVisible = true;

            KillExternalPlayerProcess();

            if (_mpvPlayer != null)
            {
                var player = _mpvPlayer;
                _mpvPlayer = null;
                var cleanupSessionVersion = System.Threading.Interlocked.Increment(ref _mpvSessionVersion);

                try
                {
                    player.FirstFrameRendered -= MpvPlayer_FirstFrameRendered;
                    player.EndReached -= MpvPlayer_EndReached;
                    player.PlaybackError -= MpvPlayer_PlaybackError;
                    player.PlaybackStateChanged -= MpvPlayer_PlaybackStateChanged;
                }
                catch
                {
                }

                // Never tear down libmpv on the WinUI thread. Bad 4K HDR/DV opens can make
                // stop/destroy wait inside libmpv/ffmpeg, which looks like the whole PC froze.
                var cleanupTask = Task.Run(() =>
                {
                    try
                    {
                        player.Stop();
                        player.Dispose();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            // Do not destroy the native host if a new episode/player already started.
                            if (_mpvPlayer == null && System.Threading.Interlocked.Read(ref _mpvSessionVersion) == cleanupSessionVersion)
                                DisposeMpvNativeHosts();
                        });
                    }
                });
                _mpvCleanupTask = cleanupTask;
            }
            else
            {
                DisposeMpvNativeHosts();
            }


            if (_mediaPlayer != null)
            {
                try
                {
                    _mediaPlayer.Stop();
                }
                catch
                {
                }

                _mediaPlayer.Playing -= MediaPlayer_Playing;
                _mediaPlayer.Buffering -= MediaPlayer_Buffering;
                _mediaPlayer.EncounteredError -= MediaPlayer_EncounteredError;
                _mediaPlayer.Stopped -= MediaPlayer_Stopped;
                _mediaPlayer.EndReached -= MediaPlayer_EndReached;

                VideoHost.MediaPlayer = null;
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            _currentMedia?.Dispose();
            _currentMedia = null;

            _libVlc?.Dispose();
            _libVlc = null;
            _directPlaybackInitialized = false;
            _pendingDirectPlaybackUrl = string.Empty;
            _knownDurationMs = 0;

            _cursorHideReapplyTimer.Stop();
            ApplyProtectedCursorToPlayer(null);
            try
            {
                _hiddenProtectedCursor?.Dispose();
            }
            catch
            {
            }

            _hiddenProtectedCursor = null;

            if (_blankCursor != IntPtr.Zero)
            {
                try { DestroyCursor(_blankCursor); } catch { }
                _blankCursor = IntPtr.Zero;
            }

            RemoveCursorHook();
        }

        private void KillExternalPlayerProcess()
        {
            var process = _externalPlayerProcess;
            _externalPlayerProcess = null;

            if (process == null)
                return;

            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try
            {
                process.Dispose();
            }
            catch
            {
            }
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

        private readonly record struct ProbeResult(string Url, bool UseEmbeddedBrowser);

        public sealed class TrackChoice
        {
            public int Id { get; }
            public string Label { get; }

            public TrackChoice(int id, string label)
            {
                Id = id;
                Label = label;
            }
        }
    }
}
