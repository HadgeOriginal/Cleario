using Cleario.Models;
using Cleario.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Cleario.Pages
{
    public sealed partial class LibraryPage : Page
    {
        private enum LibrarySortMode
        {
            Recent,
            LastWatched,
            MostWatched,
            TitleAscending,
            TitleDescending
        }

        private enum WatchFilterMode
        {
            All,
            Watched,
            NotWatched
        }

        private sealed class LibraryRow
        {
            public LibraryService.LibraryEntry Entry { get; set; } = new();
            public MetaItem Item { get; set; } = new();
            public HistoryService.ItemWatchSummary Summary { get; set; } = new();
        }

        private readonly List<LibraryRow> _allItems = new();
        private bool _isRefreshing;
        private LibrarySortMode _sortMode = LibrarySortMode.Recent;
        private WatchFilterMode _watchFilterMode = WatchFilterMode.All;

        private PosterLayoutMetrics _posterLayout = PosterLayoutService.GetCurrent();

        public static readonly DependencyProperty PosterCardWidthProperty =
            DependencyProperty.Register(nameof(PosterCardWidth), typeof(double), typeof(LibraryPage), new PropertyMetadata(166d));

        public static readonly DependencyProperty PosterCardHeightProperty =
            DependencyProperty.Register(nameof(PosterCardHeight), typeof(double), typeof(LibraryPage), new PropertyMetadata(244d));

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

        public ObservableCollection<MetaItem> Items { get; } = new();

        private void RefreshPosterLayout()
        {
            _posterLayout = PosterLayoutService.GetCurrent();
            PosterCardWidth = _posterLayout.BrowseWidth;
            PosterCardHeight = _posterLayout.BrowseHeight;
            Bindings?.Update();
        }


        public LibraryPage()
        {
            InitializeComponent();
            RefreshPosterLayout();
            NavigationCacheMode = NavigationCacheMode.Required;
            Loaded += LibraryPage_Loaded;
            Unloaded += LibraryPage_Unloaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await SettingsManager.InitializeAsync();
            NavigationCacheMode = SettingsManager.SaveMemory ? NavigationCacheMode.Disabled : NavigationCacheMode.Required;

            
            
            
            await RefreshAsync(forceReload: true);
        }

        private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            LibraryService.LibraryChanged += LibraryService_LibraryChanged;
            HistoryService.HistoryChanged += HistoryService_HistoryChanged;
        }

        private void LibraryPage_Unloaded(object sender, RoutedEventArgs e)
        {
            LibraryService.LibraryChanged -= LibraryService_LibraryChanged;
            HistoryService.HistoryChanged -= HistoryService_HistoryChanged;
        }

        private async void HistoryService_HistoryChanged(object? sender, EventArgs e)
        {
            await RefreshAsync(forceReload: false);
        }

        private async void LibraryService_LibraryChanged(object? sender, EventArgs e)
        {
            await RefreshAsync(forceReload: true);
        }

        private async Task RefreshAsync(bool forceReload)
        {
            if (_isRefreshing)
                return;

            _isRefreshing = true;
            LoadingPanel.Visibility = Visibility.Visible;

            try
            {
                await SettingsManager.InitializeAsync();
                RefreshPosterLayout();

                _allItems.Clear();
                var entries = await LibraryService.GetEntriesAsync(forceReload);

                foreach (var entry in entries)
                {
                    var item = LibraryService.ToMetaItem(entry);
                    item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                    RefreshPosterVisualForItem(item);
                    var summary = await HistoryService.GetItemSummaryAsync(entry.Type, entry.Id);
                    item.IsWatched = summary.IsWatched;
                    _allItems.Add(new LibraryRow
                    {
                        Entry = entry,
                        Item = item,
                        Summary = summary
                    });
                }

                ApplyFilterAndSort();

                foreach (var row in _allItems)
                    _ = PreparePosterAsync(row.Item);
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                _isRefreshing = false;
            }
        }

        private void ApplyFilterAndSort()
        {
            var selectedType = (TypeFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

            IEnumerable<LibraryRow> filtered = _allItems;
            if (!string.Equals(selectedType, "all", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(x => string.Equals(x.Item.Type, selectedType, StringComparison.OrdinalIgnoreCase));
            }

            filtered = _watchFilterMode switch
            {
                WatchFilterMode.Watched => filtered.Where(x => x.Summary.IsWatched),
                WatchFilterMode.NotWatched => filtered.Where(x => !x.Summary.IsWatched),
                _ => filtered
            };

            filtered = _sortMode switch
            {
                LibrarySortMode.LastWatched => filtered.OrderByDescending(x => x.Summary.LastPlayedUtc ?? DateTime.MinValue).ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase),
                LibrarySortMode.MostWatched => filtered.OrderByDescending(x => x.Summary.WatchCount).ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase),
                LibrarySortMode.TitleAscending => filtered.OrderBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase),
                LibrarySortMode.TitleDescending => filtered.OrderByDescending(x => x.Item.Name, StringComparer.OrdinalIgnoreCase),
                _ => filtered.OrderByDescending(x => x.Entry.AddedUtc).ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            };

            Items.Clear();
            foreach (var row in filtered)
                Items.Add(row.Item);

            if (EmptyPanel != null)
                EmptyPanel.Visibility = Items.Count == 0 && !_isRefreshing ? Visibility.Visible : Visibility.Collapsed;

            if (LibraryGrid != null)
                LibraryGrid.Visibility = Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (LibrarySubtitleTextBlock != null)
            {
                LibrarySubtitleTextBlock.Text = _allItems.Count == 0
                    ? "Your saved movies and series"
                    : $"{_allItems.Count} saved item{(_allItems.Count == 1 ? string.Empty : "s")}";
            }

            UpdateSortButtonStyles();
        }

        private void UpdateSortButtonStyles()
        {
            ApplySortButtonState(RecentSortButton, _sortMode == LibrarySortMode.Recent);
            ApplySortButtonState(LastWatchedSortButton, _sortMode == LibrarySortMode.LastWatched);
            ApplySortButtonState(MostWatchedSortButton, _sortMode == LibrarySortMode.MostWatched);
            ApplySortButtonState(WatchedFilterButton, _watchFilterMode == WatchFilterMode.Watched);
            ApplySortButtonState(NotWatchedFilterButton, _watchFilterMode == WatchFilterMode.NotWatched);
            ApplySortButtonState(AzSortButton, _sortMode == LibrarySortMode.TitleAscending);
            ApplySortButtonState(ZaSortButton, _sortMode == LibrarySortMode.TitleDescending);
        }

        private static void ApplySortButtonState(Button? button, bool isActive)
        {
            if (button == null)
                return;

            button.Background = new SolidColorBrush(isActive
                ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x23, 0x5E, 0xD8)
                : Microsoft.UI.ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
            button.BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            button.BorderThickness = new Thickness(1);
        }

        private async Task PreparePosterAsync(MetaItem item)
        {
            try
            {
                item.IsPosterLoading = true;
                item.IsWatched = await HistoryService.IsItemWatchedAsync(item);
                var candidates = await CatalogService.GetBrowsePosterCandidatesAsync(item.Id, item.PosterUrl, item.FallbackPosterUrl);
                item.SetPosterCandidates(candidates, CatalogService.PlaceholderPosterUri);
                RefreshPosterVisualForItem(item);
            }
            catch
            {
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                RefreshPosterVisualForItem(item);
            }
        }

        private void TypeFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilterAndSort();
        }

        private void RecentSortButton_Click(object sender, RoutedEventArgs e)
        {
            _sortMode = LibrarySortMode.Recent;
            ApplyFilterAndSort();
        }

        private void LastWatchedSortButton_Click(object sender, RoutedEventArgs e)
        {
            _sortMode = LibrarySortMode.LastWatched;
            ApplyFilterAndSort();
        }

        private void MostWatchedSortButton_Click(object sender, RoutedEventArgs e)
        {
            _sortMode = LibrarySortMode.MostWatched;
            ApplyFilterAndSort();
        }

        private void WatchedFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _watchFilterMode = _watchFilterMode == WatchFilterMode.Watched ? WatchFilterMode.All : WatchFilterMode.Watched;
            ApplyFilterAndSort();
        }

        private void NotWatchedFilterButton_Click(object sender, RoutedEventArgs e)
        {
            _watchFilterMode = _watchFilterMode == WatchFilterMode.NotWatched ? WatchFilterMode.All : WatchFilterMode.NotWatched;
            ApplyFilterAndSort();
        }

        private void AzSortButton_Click(object sender, RoutedEventArgs e)
        {
            _sortMode = LibrarySortMode.TitleAscending;
            ApplyFilterAndSort();
        }

        private void ZaSortButton_Click(object sender, RoutedEventArgs e)
        {
            _sortMode = LibrarySortMode.TitleDescending;
            ApplyFilterAndSort();
        }

        private void Poster_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is MetaItem item)
                Frame.Navigate(typeof(DetailsPage), item);
        }

        private async void PosterCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var item = element.Tag as MetaItem ?? element.DataContext as MetaItem;
            if (item == null)
                return;

            var flyout = new MenuFlyout();
            var removeItem = new MenuFlyoutItem
            {
                Text = "Remove from Library",
                Tag = item
            };
            removeItem.Click += async (_, __) =>
            {
                if (removeItem.Tag is not MetaItem metaItem)
                    return;

                await LibraryService.RemoveAsync(metaItem);
                await RefreshAsync(forceReload: true);
            };

            var isWatched = await HistoryService.IsItemWatchedAsync(item);
            var watchedItem = new MenuFlyoutItem
            {
                Text = isWatched ? "Remove watched" : "Mark as watched",
                Tag = item
            };
            watchedItem.Click += async (_, __) =>
            {
                if (watchedItem.Tag is not MetaItem metaItem)
                    return;

                await HistoryService.MarkItemWatchedAsync(metaItem, !isWatched);
                metaItem.IsWatched = !isWatched;
                await RefreshAsync(forceReload: false);
            };

            flyout.Items.Add(removeItem);
            flyout.Items.Add(watchedItem);
            ShowFlyoutAtPointer(flyout, element, e.GetPosition(element));

            e.Handled = true;
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

            if (string.IsNullOrWhiteSpace(item.Poster) || item.Poster == CatalogService.PlaceholderPosterUri)
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

        private void PosterCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                StartPosterShimmerAnimation(element);
                UpdatePosterVisualState(element, false);
            }
        }

        private async void PosterCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                await EnsurePosterBadgeInfoAsync(element);
                UpdatePosterVisualState(element, true);
            }
        }

        private void PosterCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                UpdatePosterVisualState(element, false);
        }

        private async Task EnsurePosterBadgeInfoAsync(FrameworkElement root)
        {
            await SettingsManager.InitializeAsync();
            if (!SettingsManager.ShowPosterBadges)
                return;

            if (root.Tag is not MetaItem item)
                return;

            if (!string.IsNullOrWhiteSpace(item.Year) && !string.IsNullOrWhiteSpace(item.ImdbRating))
                return;

            try
            {
                var meta = await CatalogService.GetMetaDetailsAsync(item.Type, item.Id, item.SourceBaseUrl);
                if (string.IsNullOrWhiteSpace(item.Year))
                    item.Year = !string.IsNullOrWhiteSpace(meta.Year) ? meta.Year : ExtractYear(meta.ReleaseInfo);
                if (string.IsNullOrWhiteSpace(item.ImdbRating))
                    item.ImdbRating = meta.ImdbRating;
            }
            catch
            {
            }
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

        private static void StartPosterShimmerAnimation(FrameworkElement shimmer)
        {
            if (shimmer == null)
                return;

            shimmer.Opacity = 1.0;
        }

        private void UpdatePosterVisualStateFromElement(FrameworkElement source)
        {
            var root = FindAncestorByName(source, "PosterCardRoot");
            if (root != null)
                UpdatePosterVisualState(root, false);
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
            if (imdbPanel != null)
                imdbPanel.Visibility = string.IsNullOrWhiteSpace(item.ImdbRating) ? Visibility.Collapsed : Visibility.Visible;

            var hasBadges = SettingsManager.ShowPosterBadges && isHovered &&
                            (!string.IsNullOrWhiteSpace(item.Year) || !string.IsNullOrWhiteSpace(item.ImdbRating));
            if (overlay != null)
                overlay.Visibility = hasBadges ? Visibility.Visible : Visibility.Collapsed;

            var watchedBadge = FindDescendantByName(root, "WatchedBadge");
            if (watchedBadge != null)
                watchedBadge.Visibility = item.IsWatched ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshPosterVisualForItem(MetaItem item)
        {
            var container = LibraryGrid?.ContainerFromItem(item) as GridViewItem;
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
    }
}
