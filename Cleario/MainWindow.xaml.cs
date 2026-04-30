using Cleario.Services;
using Cleario.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;

namespace Cleario
{
    public sealed partial class MainWindow : Window
    {
        private static readonly HashSet<string> SupportedDroppedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m3u8", ".mpd", ".ts", ".m4v", ".mp3", ".aac", ".flac", ".wav", ".ogg"
        };

        private bool _isFullScreen;
        private Type? _lastContentPageType;
        private object? _lastContentParameter;
        private object? _lastSelectedNavItem;
        private bool _startupUpdateCheckStarted;

        public MainWindow()
        {
            InitializeComponent();
            SetWindowIcon();

            contentFrame.Navigated += ContentFrame_Navigated;
            RefreshFullScreenState();
        }

        private void SetWindowIcon()
        {
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "cleario.ico");

                if (!File.Exists(iconPath))
                    return;

                var hwnd = WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                appWindow.SetIcon(iconPath);
            }
            catch
            {
            }
        }


        private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.SourcePageType != null)
                _lastContentPageType = e.SourcePageType;

            _lastContentParameter = e.Parameter;

            _ = UpdateDiscordPagePresenceAsync(e.SourcePageType, e.Parameter);
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e)
        {
            NavigateToPage(typeof(Pages.HomePage), navView.MenuItems[0]);
        }

        private void NavigateToPage(Type pageType, object? selectedItem = null, object? parameter = null)
        {
            _lastContentPageType = pageType;
            _lastContentParameter = parameter;
            _lastSelectedNavItem = selectedItem;
            contentFrame.Navigate(pageType, parameter);
            navView.SelectedItem = selectedItem;
        }

        private void ExecuteGlobalSearch()
        {
            var query = GlobalSearchTextBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
                return;

            if (TryOpenDirectMediaFromInput(query))
                return;

            NavigateToSearch(query);
        }



        private bool TryOpenDirectMediaFromInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            var value = input.Trim();

            if (TryCreatePlayableUri(value, out var uri, out var displayName))
            {
                ShowPlayer(new CatalogService.StreamOption
                {
                    DirectUrl = uri.ToString(),
                    ContentName = displayName,
                    DisplayName = displayName,
                    ContentType = "movie"
                });
                return true;
            }

            return false;
        }

        private static bool TryCreatePlayableUri(string input, out Uri uri, out string displayName)
        {
            uri = null!;
            displayName = string.Empty;

            if (Uri.TryCreate(input, UriKind.Absolute, out var absoluteUri))
            {
                if (absoluteUri.IsFile)
                {
                    var localPath = absoluteUri.LocalPath;
                    if (File.Exists(localPath) && IsSupportedMediaPath(localPath))
                    {
                        uri = absoluteUri;
                        displayName = Path.GetFileNameWithoutExtension(localPath);
                        return true;
                    }
                }
                else if ((absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps) && CatalogService.LooksLikeDirectMediaUrl(input))
                {
                    uri = absoluteUri;
                    displayName = GetDisplayNameFromUri(absoluteUri);
                    return true;
                }
            }

            if (Path.IsPathRooted(input) && File.Exists(input) && IsSupportedMediaPath(input))
            {
                uri = new Uri(input);
                displayName = Path.GetFileNameWithoutExtension(input);
                return true;
            }

            return false;
        }

        private static bool IsSupportedMediaPath(string path)
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(extension) && SupportedDroppedExtensions.Contains(extension);
        }

        private static string GetDisplayNameFromUri(Uri uri)
        {
            var segment = uri.Segments.LastOrDefault()?.Trim('/');
            if (!string.IsNullOrWhiteSpace(segment))
                return Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(segment));

            return "Direct media";
        }

        public void NavigateToSearch(string query)
        {
            _lastContentPageType = typeof(Pages.SearchPage);
            _lastContentParameter = query;
            _lastSelectedNavItem = null;
            contentFrame.Navigate(typeof(Pages.SearchPage), query);
            navView.SelectedItem = null;
        }

        public void NavigateToDiscover(object? parameter = null)
        {
            var item = navView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "DiscoverPage", StringComparison.OrdinalIgnoreCase));

            NavigateToPage(typeof(Pages.DiscoverPage), item, parameter);
        }

        private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (playerOverlayFrame.Visibility == Visibility.Visible)
                return;

            if (args.IsSettingsInvoked)
            {
                NavigateToPage(typeof(Pages.SettingsPage), navView.SettingsItem);
                return;
            }

            var item = args.InvokedItemContainer as NavigationViewItem;
            var pageType = item?.Tag?.ToString();

            switch (pageType)
            {
                case "HomePage":
                    NavigateToPage(typeof(Pages.HomePage), item);
                    break;
                case "HistoryPage":
                    NavigateToPage(typeof(Pages.HistoryPage), item);
                    break;
                case "DiscoverPage":
                    NavigateToPage(typeof(Pages.DiscoverPage), item);
                    break;
                case "LibraryPage":
                    NavigateToPage(typeof(Pages.LibraryPage), item);
                    break;
                case "CalendarPage":
                    NavigateToPage(typeof(Pages.CalendarPage), item);
                    break;
                case "AddonsPage":
                    NavigateToPage(typeof(Pages.AddonsPage), item);
                    break;
            }
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Focus(FocusState.Programmatic);
            UpdateGlobalSearchButtonState();
            HideBuiltInSearchClearButton();
            BeginStartupUpdateCheckOnce();
        }

        private void BeginStartupUpdateCheckOnce()
        {
            if (_startupUpdateCheckStarted)
                return;

            _startupUpdateCheckStarted = true;
            _ = CheckForStartupUpdateAsync();
        }

        private async Task CheckForStartupUpdateAsync()
        {
            try
            {
                await SettingsManager.InitializeAsync(forceReload: true);
                if (!SettingsManager.CheckForUpdatesAtStartup)
                    return;

                var update = await UpdateService.CheckForUpdatesAsync();
                if (!update.Succeeded || !update.IsUpdateAvailable)
                    return;

                await ShowStartupUpdateDialogAsync(update);
            }
            catch (Exception ex)
            {
                CrashLogger.LogException(ex, "MainWindow.CheckForStartupUpdateAsync");
            }
        }

        private async Task ShowStartupUpdateDialogAsync(UpdateService.UpdateCheckResult update)
        {
            if (RootGrid?.XamlRoot == null)
                return;

            var doNotAskAgainCheckBox = new CheckBox
            {
                Content = "Do not ask again"
            };

            var contentPanel = new StackPanel
            {
                Spacing = 12
            };

            contentPanel.Children.Add(new TextBlock
            {
                Text = $"Cleario {update.LatestVersion} is available. Do you want to update now?",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            contentPanel.Children.Add(doNotAskAgainCheckBox);

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Update available",
                Content = contentPanel,
                PrimaryButtonText = "Yes",
                SecondaryButtonText = "No",
                CloseButtonText = "Update settings",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (doNotAskAgainCheckBox.IsChecked == true)
            {
                SettingsManager.CheckForUpdatesAtStartup = false;
                await SettingsManager.SaveAsync();
            }

            if (result == ContentDialogResult.Primary)
                await UpdateService.DownloadAndLaunchInstallerAsync(update);
            else if (result == ContentDialogResult.None)
                NavigateToUpdateSettings();
        }

        private void NavigateToUpdateSettings()
        {
            NavigateToPage(typeof(Pages.SettingsPage), navView.SettingsItem, "About");
        }

        private void GlobalSearchActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(GlobalSearchTextBox?.Text))
            {
                GlobalSearchTextBox.Text = string.Empty;
                GlobalSearchTextBox.Focus(FocusState.Programmatic);
                return;
            }

            ExecuteGlobalSearch();
        }

        private void GlobalSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateGlobalSearchButtonState();
            HideBuiltInSearchClearButton();
        }

        private void GlobalSearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                ExecuteGlobalSearch();
                e.Handled = true;
            }
        }


        private void UpdateGlobalSearchButtonState()
        {
            if (GlobalSearchActionIcon != null)
                GlobalSearchActionIcon.Symbol = string.IsNullOrWhiteSpace(GlobalSearchTextBox?.Text) ? Symbol.Find : Symbol.Cancel;
        }

        private void HideBuiltInSearchClearButton()
        {
            if (GlobalSearchTextBox == null)
                return;

            HideDescendantByName(GlobalSearchTextBox, "DeleteButton");
        }

        private static bool HideDescendantByName(DependencyObject root, string name)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                {
                    fe.Visibility = Visibility.Collapsed;
                    fe.IsHitTestVisible = false;
                    return true;
                }

                if (HideDescendantByName(child, name))
                    return true;
            }

            return false;
        }



        private void RootGrid_DragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
        }

        private async void RootGrid_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                    return;

                var storageItems = await e.DataView.GetStorageItemsAsync();
                var file = storageItems.OfType<StorageFile>().FirstOrDefault(x => IsSupportedMediaPath(x.Path));
                if (file == null)
                    return;

                ShowPlayer(new CatalogService.StreamOption
                {
                    DirectUrl = new Uri(file.Path).ToString(),
                    ContentName = Path.GetFileNameWithoutExtension(file.Name),
                    DisplayName = file.Name,
                    ContentType = "movie"
                });
            }
            catch
            {
            }
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case VirtualKey.F11:
                    ToggleFullScreen();
                    e.Handled = true;
                    break;

                case VirtualKey.Escape:
                    RefreshFullScreenState();
                    if (_isFullScreen)
                    {
                        SetFullScreen(false);
                        e.Handled = true;
                    }
                    break;
            }
        }

        public bool IsFullScreen
        {
            get
            {
                RefreshFullScreenState();
                return _isFullScreen;
            }
        }

        public void ToggleFullScreen()
        {
            RefreshFullScreenState();
            SetFullScreen(!_isFullScreen);
        }

        public void SetFullScreen(bool fullScreen)
        {
            var appWindow = GetCurrentAppWindow();
            if (appWindow == null)
                return;

            appWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
            _isFullScreen = fullScreen;
        }

        public void RefreshFullScreenState()
        {
            var appWindow = GetCurrentAppWindow();
            if (appWindow == null)
            {
                _isFullScreen = false;
                return;
            }

            _isFullScreen = appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        }

        private AppWindow? GetCurrentAppWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
                return null;

            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(windowId);
        }

        public void SetSearchBarVisible(bool visible)
        {
            if (SearchBarHost != null)
                SearchBarHost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ShowPlayer(CatalogService.StreamOption stream)
        {
            navView.Visibility = Visibility.Collapsed;
            SetSearchBarVisible(false);
            playerOverlayFrame.IsHitTestVisible = true;
            playerOverlayFrame.Opacity = 1;
            playerOverlayFrame.BackStack.Clear();
            playerOverlayFrame.ForwardStack.Clear();
            playerOverlayFrame.Content = null;
            playerOverlayFrame.Visibility = Visibility.Visible;
            playerOverlayFrame.Navigate(typeof(Pages.PlayerPage), stream);
        }

        public void ClosePlayer()
        {
            ResetPlayerOverlayChrome();
            RestoreLastContentPage();
            contentFrame.UpdateLayout();
            RootGrid.Focus(FocusState.Programmatic);
        }

        public void ClosePlayerToPage(Type pageType, object? parameter = null, object? selectedItem = null)
        {
            ResetPlayerOverlayChrome();
            _lastContentPageType = pageType;
            _lastContentParameter = parameter;
            _lastSelectedNavItem = selectedItem;
            contentFrame.Navigate(pageType, parameter);
            navView.SelectedItem = selectedItem;
            contentFrame.UpdateLayout();
            RootGrid.Focus(FocusState.Programmatic);
        }

        private void ResetPlayerOverlayChrome()
        {
            playerOverlayFrame.IsHitTestVisible = false;
            playerOverlayFrame.Opacity = 0;
            playerOverlayFrame.Visibility = Visibility.Collapsed;
            playerOverlayFrame.BackStack.Clear();
            playerOverlayFrame.ForwardStack.Clear();
            playerOverlayFrame.Content = null;

            navView.Visibility = Visibility.Visible;
            navView.IsHitTestVisible = true;
            SetSearchBarVisible(true);
            contentFrame.Visibility = Visibility.Visible;
            contentFrame.IsHitTestVisible = true;
            RootGrid.IsHitTestVisible = true;
        }


        private static async Task UpdateDiscordPagePresenceAsync(Type? pageType, object? parameter)
        {
            if (pageType == null)
                return;

            var key = GetDiscordPageKey(pageType);
            if (string.IsNullOrWhiteSpace(key))
                return;

            var pageName = key;
            var details = string.Empty;
            var state = string.Empty;
            var posterUrl = string.Empty;
            var contentTitle = string.Empty;

            if (pageType == typeof(Pages.SearchPage))
            {
                details = "Searching";
                if (parameter is string query && !string.IsNullOrWhiteSpace(query))
                    state = query;
            }
            else if (pageType == typeof(Pages.DetailsPage))
            {
                var item = GetDetailsMetaItem(parameter);
                if (item != null)
                {
                    details = item.Name;
                    contentTitle = item.Name;
                    state = "About to start";
                    posterUrl = !string.IsNullOrWhiteSpace(item.PosterUrl)
                        ? item.PosterUrl
                        : (!string.IsNullOrWhiteSpace(item.FallbackPosterUrl) ? item.FallbackPosterUrl : item.Poster);
                }
            }

            await DiscordRichPresenceService.SetPageActivityAsync(key, pageName, details, state, posterUrl, contentTitle);
        }

        private static string GetDiscordPageKey(Type pageType)
        {
            if (pageType == typeof(Pages.HomePage))
                return "Home";
            if (pageType == typeof(Pages.DiscoverPage))
                return "Discover";
            if (pageType == typeof(Pages.LibraryPage))
                return "Library";
            if (pageType == typeof(Pages.CalendarPage))
                return "Calendar";
            if (pageType == typeof(Pages.SearchPage))
                return "Search";
            if (pageType == typeof(Pages.DetailsPage))
                return "Details";
            if (pageType == typeof(Pages.HistoryPage))
                return "History";
            if (pageType == typeof(Pages.AddonsPage))
                return "Addons";
            if (pageType == typeof(Pages.SettingsPage))
                return "Settings";

            return string.Empty;
        }

        private static MetaItem? GetDetailsMetaItem(object? parameter)
        {
            if (parameter is MetaItem item)
                return item;

            if (parameter is DetailsNavigationRequest request)
                return request.Item;

            return null;
        }

        private static string ToDisplayType(string type)
        {
            if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase))
                return "Series";

            if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase))
                return "Movie";

            return type;
        }

        private void RestoreLastContentPage()
        {
            if (_lastContentPageType == null)
                return;

            try
            {
                if (contentFrame.Content == null || contentFrame.SourcePageType != _lastContentPageType)
                {
                    contentFrame.Navigate(_lastContentPageType, _lastContentParameter);
                }
                else if (contentFrame.Content is FrameworkElement currentPage)
                {
                    currentPage.Visibility = Visibility.Visible;
                    currentPage.IsHitTestVisible = true;
                    currentPage.UpdateLayout();
                }

                navView.SelectedItem = _lastSelectedNavItem;
            }
            catch
            {
            }
        }
    }
}
