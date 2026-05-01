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
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Cleario.Pages
{
    public sealed partial class SearchPage : Page
    {
        private string _query = string.Empty;

        private PosterLayoutMetrics _posterLayout = PosterLayoutService.GetCurrent();

        public static readonly DependencyProperty PosterCardWidthProperty =
            DependencyProperty.Register(nameof(PosterCardWidth), typeof(double), typeof(SearchPage), new PropertyMetadata(166d));

        public static readonly DependencyProperty PosterCardHeightProperty =
            DependencyProperty.Register(nameof(PosterCardHeight), typeof(double), typeof(SearchPage), new PropertyMetadata(244d));

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

        public ObservableCollection<MetaItem> MovieResults { get; } = new();
        public ObservableCollection<MetaItem> SeriesResults { get; } = new();

        public SearchPage()
        {
            InitializeComponent();
            RefreshPosterLayout();
            NavigationCacheMode = NavigationCacheMode.Required;
        }

        private void RefreshPosterLayout()
        {
            _posterLayout = PosterLayoutService.GetCurrent();
            PosterCardWidth = _posterLayout.BrowseWidth;
            PosterCardHeight = _posterLayout.BrowseHeight;
            Bindings?.Update();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await SettingsManager.InitializeAsync();
            NavigationCacheMode = SettingsManager.SaveMemory ? NavigationCacheMode.Disabled : NavigationCacheMode.Required;
            RefreshPosterLayout();

            var query = e.Parameter?.ToString()?.Trim() ?? string.Empty;
            if (!SettingsManager.SaveMemory && e.NavigationMode != NavigationMode.Back && string.Equals(_query, query, StringComparison.OrdinalIgnoreCase) && (MovieResults.Count > 0 || SeriesResults.Count > 0))
            {
                ApplySectionVisibility();
                await RefreshVisibleWatchStatesAsync();
                return;
            }

            if (e.NavigationMode == NavigationMode.Back && !string.IsNullOrWhiteSpace(_query))
            {
                ApplySectionVisibility();
                await RefreshVisibleWatchStatesAsync();
                return;
            }

            if (string.Equals(_query, query, StringComparison.OrdinalIgnoreCase) &&
                (MovieResults.Count > 0 || SeriesResults.Count > 0))
            {
                ApplySectionVisibility();
                await RefreshVisibleWatchStatesAsync();
                return;
            }

            _query = query;
            await LoadSearchResultsAsync();
        }

        private async Task RefreshVisibleWatchStatesAsync()
        {
            foreach (var item in MovieResults.Concat(SeriesResults).ToList())
            {
                item.IsWatched = await HistoryService.IsItemWatchedAsync(item);
                RefreshPosterVisualForItem(item);
            }
        }

        private async Task LoadSearchResultsAsync()
        {
            SearchTitleTextBlock.Text = string.IsNullOrWhiteSpace(_query) ? "Search" : _query;
            SearchSubtitleTextBlock.Text = string.IsNullOrWhiteSpace(_query)
                ? "Type a title in the search bar above."
                : $"Results for “{_query}”";

            MovieResults.Clear();
            SeriesResults.Clear();
            MoviesHeaderTextBlock.Text = "Popular - Movie";
            SeriesHeaderTextBlock.Text = "Popular - Series";

            LoadingPanel.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            MoviesSection.Visibility = Visibility.Collapsed;
            SeriesSection.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(_query))
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                EmptyPanel.Visibility = Visibility.Visible;
                EmptyBodyTextBlock.Text = "Type a movie or series title in the search bar above.";
                return;
            }

            try
            {
                await AddonManager.InitializeAsync();

                var catalogs = await DiscoverService.GetDiscoverCatalogsAsync();

                var movieCatalog = catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "movie", StringComparison.OrdinalIgnoreCase) &&
                        x.Name.Contains("popular", StringComparison.OrdinalIgnoreCase) &&
                        x.SupportsSearch && !x.RequiresGenre)
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "movie", StringComparison.OrdinalIgnoreCase) &&
                        x.Name.Contains("popular", StringComparison.OrdinalIgnoreCase) &&
                        x.SupportsSearch)
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "movie", StringComparison.OrdinalIgnoreCase) &&
                        x.Name.Contains("popular", StringComparison.OrdinalIgnoreCase))
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "movie", StringComparison.OrdinalIgnoreCase) &&
                        x.SupportsSearch)
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "movie", StringComparison.OrdinalIgnoreCase));

                var seriesCatalog = catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "series", StringComparison.OrdinalIgnoreCase) &&
                        x.Name.Contains("popular", StringComparison.OrdinalIgnoreCase) &&
                        x.SupportsSearch && !x.RequiresGenre)
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "series", StringComparison.OrdinalIgnoreCase) &&
                        x.Name.Contains("popular", StringComparison.OrdinalIgnoreCase) &&
                        x.SupportsSearch)
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "series", StringComparison.OrdinalIgnoreCase) &&
                        x.Name.Contains("popular", StringComparison.OrdinalIgnoreCase))
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "series", StringComparison.OrdinalIgnoreCase) &&
                        x.SupportsSearch)
                    ?? catalogs.FirstOrDefault(x =>
                        string.Equals(x.Type, "series", StringComparison.OrdinalIgnoreCase));

                Task<System.Collections.Generic.List<MetaItem>>? movieTask = null;
                Task<System.Collections.Generic.List<MetaItem>>? seriesTask = null;

                if (movieCatalog != null)
                {
                    MoviesHeaderTextBlock.Text = $"{movieCatalog.Name} - Movie";
                    movieTask = DiscoverService.GetCatalogItemsAsync(
                        movieCatalog.SourceBaseUrl,
                        movieCatalog,
                        0,
                        _query,
                        string.Empty);
                }

                if (seriesCatalog != null)
                {
                    SeriesHeaderTextBlock.Text = $"{seriesCatalog.Name} - Series";
                    seriesTask = DiscoverService.GetCatalogItemsAsync(
                        seriesCatalog.SourceBaseUrl,
                        seriesCatalog,
                        0,
                        _query,
                        string.Empty);
                }

                if (movieTask != null)
                {
                    var movies = await movieTask;
                    foreach (var item in movies.Take(24))
                    {
                        item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                RefreshPosterVisualForItem(item);
                        MovieResults.Add(item);
                        _ = PreparePosterAsync(item);
                    }
                }

                if (seriesTask != null)
                {
                    var series = await seriesTask;
                    foreach (var item in series.Take(24))
                    {
                        item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                        SeriesResults.Add(item);
                        _ = PreparePosterAsync(item);
                    }
                }
            }
            catch
            {
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                ApplySectionVisibility();
            }
        }

        private void ApplySectionVisibility()
        {
            MoviesSection.Visibility = MovieResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SeriesSection.Visibility = SeriesResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (MovieResults.Count == 0 && SeriesResults.Count == 0)
            {
                EmptyPanel.Visibility = Visibility.Visible;
                EmptyBodyTextBlock.Text = string.IsNullOrWhiteSpace(_query)
                    ? "Type a movie or series title in the search bar above."
                    : "Try a different title or spelling.";
            }
            else
            {
                EmptyPanel.Visibility = Visibility.Collapsed;
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

        private async Task EnsurePosterBadgeInfoAsync(FrameworkElement root)
        {
            await SettingsManager.InitializeAsync();
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
            UpdatePosterVisualState(root, stillHovered);
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
                UpdatePosterVisualState(element, false);
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
                UpdatePosterVisualState(element, true);
                _ = EnsurePosterBadgeInfoAsync(element);
            }
        }

        private void PosterCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.Resources["PosterHoverState"] = false;
                UpdatePosterVisualState(element, false);
            }
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
            var container = (MovieGrid?.ContainerFromItem(item) as GridViewItem)
                ?? (SeriesGrid?.ContainerFromItem(item) as GridViewItem);
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
    }
}
