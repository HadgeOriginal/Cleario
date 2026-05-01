using Cleario.Models;
using Cleario.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using VirtualKey = Windows.System.VirtualKey;

namespace Cleario.Pages
{
    public sealed partial class DiscoverPage : Page
    {
        public ObservableCollection<MetaItem> Items { get; } = new();

        private readonly List<DiscoverService.DiscoverCatalogDefinition> _allCatalogs = new();
        private readonly DispatcherTimer _scrollProbeTimer;

        private const int CatalogSkipStep = 100;

        private ScrollViewer? _scrollViewer;
        private bool _isLoading;
        private bool _hasMore = true;
        private int _skip;
        private bool _pageInitialized;
        private bool _initializingPage;
        private bool _suppressSelectionEvents;
        private bool _autoFillRunning;
        private string _lastFetchSignature = string.Empty;
        private int _samePageRepeatCount;
        private string _pendingType = string.Empty;
        private string _pendingCatalogId = string.Empty;
        private string _pendingSourceBaseUrl = string.Empty;

        private PosterLayoutMetrics _posterLayout = PosterLayoutService.GetCurrent();

        public static readonly DependencyProperty PosterCardWidthProperty =
            DependencyProperty.Register(nameof(PosterCardWidth), typeof(double), typeof(DiscoverPage), new PropertyMetadata(166d));

        public static readonly DependencyProperty PosterCardHeightProperty =
            DependencyProperty.Register(nameof(PosterCardHeight), typeof(double), typeof(DiscoverPage), new PropertyMetadata(244d));

        public double PosterCardWidth
        {
            get => (double)GetValue(PosterCardWidthProperty);
            set => SetValue(PosterCardWidthProperty, value);
        }

        public double PosterCardHeight
        {
            get => (double)GetValue(PosterCardHeightProperty);
            set => SetValue(PosterCardHeightProperty, value);
        }

        public DiscoverPage()
        {
            InitializeComponent();
            RefreshPosterLayout();

            NavigationCacheMode = NavigationCacheMode.Required;

            _scrollProbeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _scrollProbeTimer.Tick += ScrollProbeTimer_Tick;

            Loaded += DiscoverPage_Loaded;
            Unloaded += DiscoverPage_Unloaded;
            PosterGrid.SizeChanged += PosterGrid_SizeChanged;
        }

        private string SelectedType =>
            (TypeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "movie";

        private DiscoverService.DiscoverCatalogDefinition? SelectedCatalog =>
            (CatalogComboBox.SelectedItem as ComboBoxItem)?.Tag as DiscoverService.DiscoverCatalogDefinition;

        private string SelectedGenreValue =>
            (GenreComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

        private string CurrentSearchQuery => string.Empty;


        private void RefreshPosterLayout()
        {
            _posterLayout = PosterLayoutService.GetCurrent();
            PosterCardWidth = _posterLayout.BrowseWidth;
            PosterCardHeight = _posterLayout.BrowseHeight;
            Bindings?.Update();
        }

        private static string BuildYearDisplayFromReleaseInfo(string releaseInfo)
        {
            if (string.IsNullOrWhiteSpace(releaseInfo))
                return string.Empty;

            var rangeMatch = System.Text.RegularExpressions.Regex.Match(releaseInfo, @"(?<start>(?:19|20)\d{2})\s*[-–]\s*(?<end>(?:19|20)\d{2})?");
            if (rangeMatch.Success)
            {
                var start = rangeMatch.Groups["start"].Value;
                var end = rangeMatch.Groups["end"].Value;
                return string.IsNullOrWhiteSpace(end) ? $"{start}-" : $"{start}-{end}";
            }

            return ExtractYear(releaseInfo);
        }

        private static string ExtractYear(string releaseInfo)
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

        private async void DiscoverPage_Loaded(object sender, RoutedEventArgs e)
        {
            StartScrollProbe();
            EnsureScrollViewerAttached();
            HistoryService.HistoryChanged -= HistoryService_HistoryChanged;
            HistoryService.HistoryChanged += HistoryService_HistoryChanged;

            if (_pageInitialized || _initializingPage)
                return;

            _initializingPage = true;
            try
            {
                await SettingsManager.InitializeAsync();
                RefreshPosterLayout();
                await AddonManager.InitializeAsync();
                await LoadDiscoverOptionsAsync();
                ApplyPendingSelection();
                await ReloadDiscoverAsync();
                _pageInitialized = true;
            }
            finally
            {
                _initializingPage = false;
            }
        }

        private void DiscoverPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopScrollProbe();
            DetachScrollViewer();
            HistoryService.HistoryChanged -= HistoryService_HistoryChanged;
        }

        private async void HistoryService_HistoryChanged(object? sender, EventArgs e)
        {
            await RefreshVisibleWatchStatesAsync();
        }

        private async Task RefreshVisibleWatchStatesAsync()
        {
            foreach (var item in Items.ToList())
            {
                item.IsWatched = await HistoryService.IsItemWatchedAsync(item);
                RefreshPosterVisualForItem(item);
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await SettingsManager.InitializeAsync();
            NavigationCacheMode = SettingsManager.SaveMemory ? NavigationCacheMode.Disabled : NavigationCacheMode.Required;
            RefreshPosterLayout();

            if (e.Parameter is DiscoverNavigationRequest request)
            {
                _pendingType = request.Type ?? string.Empty;
                _pendingCatalogId = request.CatalogId ?? string.Empty;
                _pendingSourceBaseUrl = request.SourceBaseUrl ?? string.Empty;
            }

            StartScrollProbe();

            if (_pageInitialized && SettingsManager.SaveMemory)
            {
                await SettingsManager.InitializeAsync(true);
                RefreshPosterLayout();
                await AddonManager.InitializeAsync(true);
                await LoadDiscoverOptionsAsync();
                ApplyPendingSelection();
                await ReloadDiscoverAsync();
            }
            else if (_pageInitialized)
            {
                ApplyPendingSelection();
                EnsureScrollViewerAttached();
                await RefreshVisibleWatchStatesAsync();
                return;
            }

            await Task.Delay(60);
            PosterGrid.UpdateLayout();
            EnsureScrollViewerAttached();
            await AutoFillViewportAsync();

            await Task.Delay(120);
            PosterGrid.UpdateLayout();
            EnsureScrollViewerAttached();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            StopScrollProbe();
            DetachScrollViewer();
        }

        private void StartScrollProbe()
        {
            if (!_scrollProbeTimer.IsEnabled)
                _scrollProbeTimer.Start();
        }

        private void StopScrollProbe()
        {
            if (_scrollProbeTimer.IsEnabled)
                _scrollProbeTimer.Stop();
        }

        private async void ScrollProbeTimer_Tick(object? sender, object e)
        {
            EnsureScrollViewerAttached();

            if (_scrollViewer == null || _isLoading || !_hasMore)
                return;

            if (_scrollViewer.ScrollableHeight <= 40)
            {
                await AutoFillViewportAsync();
                return;
            }

            if (_scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 250)
                await LoadMoreItemsAsync();
        }

        private async void PosterGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            EnsureScrollViewerAttached();
            await AutoFillViewportAsync();
        }

        private async Task LoadDiscoverOptionsAsync()
        {
            _allCatalogs.Clear();
            _allCatalogs.AddRange(await DiscoverService.GetDiscoverCatalogsAsync());

            _suppressSelectionEvents = true;
            try
            {
                PopulateTypeComboBox();
                PopulateCatalogComboBox();
                PopulateGenreComboBox();
            }
            finally
            {
                _suppressSelectionEvents = false;
            }
        }


private void ApplyPendingSelection()
{
    if (string.IsNullOrWhiteSpace(_pendingType) && string.IsNullOrWhiteSpace(_pendingCatalogId))
        return;

    _suppressSelectionEvents = true;
    try
    {
        var typeItem = TypeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), _pendingType, StringComparison.OrdinalIgnoreCase));

        if (typeItem != null)
            TypeComboBox.SelectedItem = typeItem;

        PopulateCatalogComboBox();

        var catalogItem = CatalogComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(x =>
                x.Tag is DiscoverService.DiscoverCatalogDefinition def &&
                string.Equals(def.Id, _pendingCatalogId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(_pendingSourceBaseUrl) ||
                 string.Equals(def.SourceBaseUrl, _pendingSourceBaseUrl, StringComparison.OrdinalIgnoreCase)));

        if (catalogItem != null)
            CatalogComboBox.SelectedItem = catalogItem;

        PopulateGenreComboBox();
    }
    finally
    {
        _pendingType = string.Empty;
        _pendingCatalogId = string.Empty;
        _pendingSourceBaseUrl = string.Empty;
        _suppressSelectionEvents = false;
    }
}

        private void PopulateTypeComboBox()
        {
            var current = SelectedType;

            var types = _allCatalogs
                .Select(x => x.Type)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            TypeComboBox.Items.Clear();

            foreach (var type in types)
            {
                TypeComboBox.Items.Add(new ComboBoxItem
                {
                    Content = ToTypeDisplayName(type),
                    Tag = type
                });
            }

            if (TypeComboBox.Items.Count == 0)
                return;

            var selected = TypeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), current, StringComparison.OrdinalIgnoreCase));

            TypeComboBox.SelectedItem = selected ?? TypeComboBox.Items[0];
        }

        private void PopulateCatalogComboBox()
        {
            var selectedType = SelectedType;
            var previousId = SelectedCatalog?.Id;

            var catalogs = _allCatalogs
                .Where(x => string.Equals(x.Type, selectedType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            CatalogComboBox.Items.Clear();

            foreach (var catalog in catalogs)
            {
                CatalogComboBox.Items.Add(new ComboBoxItem
                {
                    Content = catalog.Name,
                    Tag = catalog
                });
            }

            if (CatalogComboBox.Items.Count == 0)
                return;

            var selected = CatalogComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(x =>
                    x.Tag is DiscoverService.DiscoverCatalogDefinition def &&
                    string.Equals(def.Id, previousId, StringComparison.OrdinalIgnoreCase));

            CatalogComboBox.SelectedItem = selected ?? CatalogComboBox.Items[0];
        }

        private void PopulateGenreComboBox()
        {
            var catalog = SelectedCatalog;
            GenreComboBox.Items.Clear();

            if (catalog == null)
                return;

            if (catalog.SupportsGenre && catalog.GenreOptions.Count > 0)
            {
                if (!catalog.RequiresGenre)
                {
                    GenreComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = "Genre",
                        Tag = string.Empty
                    });
                }

                foreach (var option in catalog.GenreOptions)
                {
                    GenreComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = option,
                        Tag = option
                    });
                }

                GenreComboBox.SelectedIndex = 0;
            }
            else
            {
                GenreComboBox.Items.Add(new ComboBoxItem
                {
                    Content = "Genre",
                    Tag = string.Empty
                });

                GenreComboBox.SelectedIndex = 0;
            }
        }

        private async Task ReloadDiscoverAsync()
        {
            _skip = 0;
            _hasMore = true;
            _lastFetchSignature = string.Empty;
            _samePageRepeatCount = 0;
            Items.Clear();

            EnsureScrollViewerAttached();
            await LoadMoreItemsAsync();
            await AutoFillViewportAsync();
        }

        private async Task LoadMoreItemsAsync()
        {
            if (_isLoading || !_hasMore)
                return;

            var catalog = SelectedCatalog;
            if (catalog == null)
                return;

            _isLoading = true;

            try
            {
                var catalogBaseUrl = !string.IsNullOrWhiteSpace(catalog.SourceBaseUrl)
                    ? catalog.SourceBaseUrl
                    : await CatalogService.GetMetadataCatalogBaseUrlAsync();

                var genre = SelectedGenreValue;
                if (catalog.RequiresGenre && string.IsNullOrWhiteSpace(genre) && catalog.GenreOptions.Count > 0)
                    genre = catalog.GenreOptions[0];

                var search = string.Empty;

                int pagesScanned = 0;
                int addedThisCall = 0;

                while (_hasMore && pagesScanned < 10 && addedThisCall == 0)
                {
                    pagesScanned++;

                    var fetched = await DiscoverService.GetCatalogItemsAsync(
                        catalogBaseUrl,
                        catalog,
                        _skip,
                        search,
                        genre);

                    if (fetched.Count == 0)
                    {
                        _hasMore = false;
                        break;
                    }

                    var signature = string.Join("|", fetched.Select(x => x.Id));

                    if (string.Equals(signature, _lastFetchSignature, StringComparison.Ordinal))
                    {
                        _samePageRepeatCount++;
                    }
                    else
                    {
                        _samePageRepeatCount = 0;
                        _lastFetchSignature = signature;
                    }

                    if (_samePageRepeatCount >= 3)
                    {
                        _hasMore = false;
                        break;
                    }

                    foreach (var item in fetched)
                    {
                        if (Items.Any(x => x.Id == item.Id))
                            continue;

                        item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                RefreshPosterVisualForItem(item);
                        Items.Add(item);
                        _ = PreparePosterAsync(item);
                        addedThisCall++;
                    }

                    
                    
                    _skip += CatalogSkipStep;
                }
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async Task AutoFillViewportAsync()
        {
            if (_autoFillRunning)
                return;

            _autoFillRunning = true;

            try
            {
                for (int i = 0; i < 15; i++)
                {
                    EnsureScrollViewerAttached();

                    if (_scrollViewer == null || !_hasMore || _isLoading)
                        break;

                    if (_scrollViewer.ScrollableHeight > 40)
                        break;

                    await LoadMoreItemsAsync();
                    await Task.Delay(40);
                    PosterGrid.UpdateLayout();
                }
            }
            finally
            {
                _autoFillRunning = false;
            }
        }

        private async Task PreparePosterAsync(MetaItem item)
        {
            try
            {
                item.IsPosterLoading = true;
                item.IsWatched = await HistoryService.IsItemWatchedAsync(item);

                var candidates = await CatalogService.GetBrowsePosterCandidatesAsync(
                    item.Id,
                    item.PosterUrl,
                    item.FallbackPosterUrl);

                item.SetPosterCandidates(candidates, CatalogService.PlaceholderPosterUri);
                RefreshPosterVisualForItem(item);
            }
            catch
            {
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                RefreshPosterVisualForItem(item);
            }
        }

        private async void TypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents || !_pageInitialized || _initializingPage)
                return;

            _suppressSelectionEvents = true;
            try
            {
                PopulateCatalogComboBox();
                PopulateGenreComboBox();
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            await ReloadDiscoverAsync();
        }

        private async void CatalogComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents || !_pageInitialized || _initializingPage)
                return;

            _suppressSelectionEvents = true;
            try
            {
                PopulateGenreComboBox();
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            await ReloadDiscoverAsync();
        }

        private async void GenreComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents || !_pageInitialized || _initializingPage)
                return;

            await ReloadDiscoverAsync();
        }

        private void Poster_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image || image.Tag is not MetaItem item)
                return;

            if (!string.Equals(item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.Poster))
            {
                item.IsPosterLoading = false;
                _ = CatalogService.QueuePosterCacheIfEnabledAsync(item.Id, item.Poster);
            }

            UpdatePosterVisualStateFromElement(image);
        }

        private void PosterCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                StartPosterShimmerAnimation(element);
                UpdatePosterVisualState(element, isHovered: false);
            }
        }

        private static void StartPosterShimmerAnimation(FrameworkElement shimmer)
        {
            if (shimmer == null)
                return;

            shimmer.Opacity = 1.0;
        }

        private void PosterCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.Resources["PosterHoverState"] = true;
                UpdatePosterVisualState(element, isHovered: true);
                _ = EnsurePosterBadgeInfoAsync(element);
            }
        }

        private void PosterCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.Resources["PosterHoverState"] = false;
                UpdatePosterVisualState(element, isHovered: false);
            }
        }


        private async Task EnsurePosterBadgeInfoAsync(FrameworkElement root)
        {
            if (!SettingsManager.ShowPosterBadges)
                return;

            if (root.Tag is not MetaItem item)
                return;

            if (!string.IsNullOrWhiteSpace(item.Year) &&
                !string.IsNullOrWhiteSpace(item.ImdbRating))
                return;

            try
            {
                var meta = await CatalogService.GetMetaDetailsAsync(item.Type, item.Id, item.SourceBaseUrl);

                if (string.IsNullOrWhiteSpace(item.Year))
                    item.Year = BuildYearDisplayFromReleaseInfo(!string.IsNullOrWhiteSpace(meta.ReleaseInfo) ? meta.ReleaseInfo : meta.Year);

                if (string.IsNullOrWhiteSpace(item.ImdbRating))
                    item.ImdbRating = meta.ImdbRating;
            }
            catch
            {
            }

            var stillHovered = root.Resources.TryGetValue("PosterHoverState", out var value) && value is bool hovered && hovered;
            UpdatePosterVisualState(root, isHovered: stillHovered);
        }

        private void UpdatePosterVisualStateFromElement(FrameworkElement source)
        {
            var root = FindAncestorByName(source, "PosterCardRoot");
            if (root != null)
                UpdatePosterVisualState(root, isHovered: false);
        }

        private void UpdatePosterVisualState(FrameworkElement root, bool isHovered)
        {
            if (root.Tag is not MetaItem item)
                return;

            if (root.RenderTransform is ScaleTransform scaleTransform)
            {
                scaleTransform.ScaleX = isHovered ? 1.05 : 1.0;
                scaleTransform.ScaleY = isHovered ? 1.05 : 1.0;
            }

            Canvas.SetZIndex(root, isHovered ? 20 : 0);

            var posterImage = FindDescendantByName(root, "PosterImageElement");
            var hasLoadedPoster = !item.IsPosterLoading && !string.IsNullOrWhiteSpace(item.Poster);

            if (posterImage != null)
                posterImage.Visibility = hasLoadedPoster ? Visibility.Visible : Visibility.Collapsed;

            var shimmer = FindDescendantByName(root, "PosterShimmerOverlay");
            if (shimmer != null)
                shimmer.Visibility = hasLoadedPoster ? Visibility.Collapsed : Visibility.Visible;

            var overlay = FindDescendantByName(root, "PosterInfoOverlay");
            var imdbPanel = FindDescendantByName(root, "PosterImdbPanel");
var yearText = FindDescendantByName(root, "PosterYearTextBlock") as TextBlock;
            var imdbText = FindDescendantByName(root, "PosterImdbTextBlock") as TextBlock;
            if (yearText != null)
                yearText.Text = item.Year ?? string.Empty;

            if (imdbText != null)
                imdbText.Text = item.ImdbRating ?? string.Empty;
            var showYear = SettingsManager.ShowPosterHoverYear && !string.IsNullOrWhiteSpace(item.Year);
            var showImdb = SettingsManager.ShowPosterHoverImdbRating && !string.IsNullOrWhiteSpace(item.ImdbRating);
            if (yearText != null)
                yearText.Visibility = showYear ? Visibility.Visible : Visibility.Collapsed;

            if (imdbPanel != null)
                imdbPanel.Visibility = showImdb ? Visibility.Visible : Visibility.Collapsed;
            var hasBadges = SettingsManager.ShowPosterBadges &&
                            isHovered &&
                            (showYear || showImdb);

            if (overlay != null)
                overlay.Visibility = hasBadges ? Visibility.Visible : Visibility.Collapsed;

            var watchedBadge = FindDescendantByName(root, "WatchedBadge");
            if (watchedBadge != null)
                watchedBadge.Visibility = item.IsWatched ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshPosterVisualForItem(MetaItem item)
        {
            var container = PosterGrid?.ContainerFromItem(item) as GridViewItem;
            if (container == null)
                return;

            var root = FindDescendantByName(container, "PosterCardRoot");
            if (root != null)
                UpdatePosterVisualState(root, false);
        }

        private static FrameworkElement? FindDescendantByName(DependencyObject? root, string name)
        {
            if (root == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);

                if (child is FrameworkElement fe && fe.Name == name)
                    return fe;

                var nested = FindDescendantByName(child, name);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static FrameworkElement? FindAncestorByName(DependencyObject? child, string name)
        {
            var current = child;

            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Name == name)
                    return fe;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void Poster_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not MetaItem item)
                return;

            Frame.Navigate(typeof(DetailsPage), item);
        }

        private async void PosterCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var item = element.Tag as MetaItem ?? element.DataContext as MetaItem;
            if (item == null)
                return;

            var isInLibrary = await LibraryService.ContainsAsync(item);
            var isWatched = await HistoryService.IsItemWatchedAsync(item);
            var flyout = new MenuFlyout();
            var menuItem = new MenuFlyoutItem
            {
                Text = isInLibrary ? "Remove from Library" : "Add to Library",
                Tag = item
            };

            menuItem.Click += async (_, __) =>
            {
                if (menuItem.Tag is not MetaItem menuItemMeta)
                    return;

                if (await LibraryService.ContainsAsync(menuItemMeta))
                    await LibraryService.RemoveAsync(menuItemMeta);
                else
                    await LibraryService.AddOrUpdateAsync(menuItemMeta);
            };

            var watchedItem = new MenuFlyoutItem
            {
                Text = isWatched ? "Remove watched" : "Mark as watched",
                Tag = item
            };
            watchedItem.Click += async (_, __) =>
            {
                if (watchedItem.Tag is not MetaItem menuItemMeta)
                    return;

                await HistoryService.MarkItemWatchedAsync(menuItemMeta, !isWatched);
            };

            flyout.Items.Add(menuItem);
            flyout.Items.Add(watchedItem);
            ShowFlyoutAtPointer(flyout, element, e.GetPosition(element));

            e.Handled = true;
        }


        private static void ShowFlyoutAtPointer(MenuFlyout flyout, FrameworkElement element, Point position)
        {
            try
            {
                flyout.ShowAt(element, new FlyoutShowOptions { Position = position });
            }
            catch
            {
                flyout.ShowAt(element);
            }
        }

        private async void Poster_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is not Image image || image.Tag is not MetaItem item)
                return;

            item.IsPosterLoading = true;

            if (string.IsNullOrWhiteSpace(item.Poster) ||
                item.Poster == CatalogService.PlaceholderPosterUri)
            {
                await PreparePosterAsync(item);
                UpdatePosterVisualStateFromElement(image);
                return;
            }

            if (!item.MoveToNextPosterCandidate())
            {
                item.Poster = string.Empty;
                item.IsPosterLoading = true;
            }

            UpdatePosterVisualStateFromElement(image);
        }

        private async void PosterGrid_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewerAttached();
            await AutoFillViewportAsync();
        }

        private void EnsureScrollViewerAttached()
        {
            var newScrollViewer = FindScrollViewer(PosterGrid);

            if (ReferenceEquals(_scrollViewer, newScrollViewer))
                return;

            if (_scrollViewer != null)
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;

            _scrollViewer = newScrollViewer;

            if (_scrollViewer != null)
                _scrollViewer.ViewChanged += ScrollViewer_ViewChanged;
        }

        private void DetachScrollViewer()
        {
            if (_scrollViewer != null)
                _scrollViewer.ViewChanged -= ScrollViewer_ViewChanged;

            _scrollViewer = null;
        }

        private async void ScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_scrollViewer == null || _isLoading || !_hasMore)
                return;

            if (_scrollViewer.ScrollableHeight <= 40)
            {
                await AutoFillViewportAsync();
                return;
            }

            if (_scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 250)
                await LoadMoreItemsAsync();
        }

        private ScrollViewer? FindScrollViewer(DependencyObject parent)
        {
            if (parent == null)
                return null;

            if (parent is ScrollViewer scrollViewer)
                return scrollViewer;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var result = FindScrollViewer(VisualTreeHelper.GetChild(parent, i));
                if (result != null)
                    return result;
            }

            return null;
        }

        private static string ToTypeDisplayName(string type)
        {
            return string.Equals(type, "series", StringComparison.OrdinalIgnoreCase)
                ? "Series"
                : "Movie";
        }
    }
}