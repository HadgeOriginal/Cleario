using Cleario.Models;
using Cleario.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Cleario.Pages
{
    public sealed partial class HistoryPage : Page
    {
        public sealed class HistoryCardItem
        {
            public MetaItem Item { get; set; } = new();
            public HistoryService.HistoryEntry Entry { get; set; } = new();
            public string DisplayTitle => BuildDisplayTitle(Item.Name, Entry.SeasonNumber, Entry.EpisodeNumber, Item.Type);
            public double PosterWidth => PosterLayoutService.GetCurrent().HomeWidth;
            public double PosterHeight => PosterLayoutService.GetCurrent().HomeHeight;
            public double ProgressTrackWidth => Math.Max(0, PosterWidth - 28);
            public double ProgressWidth => Math.Max(12, ProgressTrackWidth * ProgressRatio);
            public double ProgressRatio => Entry.DurationMs > 0 ? Math.Clamp((double)Entry.PositionMs / Entry.DurationMs, 0, 1) : 0;
        }

        public ObservableCollection<HistoryCardItem> Items { get; } = new();

        public HistoryPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            Loaded += HistoryPage_Loaded;
            Unloaded += HistoryPage_Unloaded;
        }

        private void HistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            HistoryService.HistoryChanged += HistoryService_HistoryChanged;
        }

        private void HistoryPage_Unloaded(object sender, RoutedEventArgs e)
        {
            HistoryService.HistoryChanged -= HistoryService_HistoryChanged;
        }

        private async void HistoryService_HistoryChanged(object? sender, EventArgs e)
        {
            await RefreshAsync();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await RefreshAsync();
        }

        private static string BuildDisplayTitle(string? rawName, int? seasonNumber, int? episodeNumber, string? type)
        {
            var baseName = System.Text.RegularExpressions.Regex.Replace(rawName?.Trim() ?? string.Empty, @"\s*\((\d+x\d{2}|Special\s+\d{2})\)\s*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (!string.Equals(type, "series", StringComparison.OrdinalIgnoreCase) || !seasonNumber.HasValue || !episodeNumber.HasValue)
                return baseName;

            return seasonNumber.Value <= 0
                ? $"{baseName} (Special {episodeNumber.Value:00})"
                : $"{baseName} ({seasonNumber.Value}x{episodeNumber.Value:00})";
        }

        private async Task RefreshAsync()
        {
            await SettingsManager.InitializeAsync();
            var items = await HistoryService.GetContinueWatchingAsync(forceReload: true);

            Items.Clear();
            foreach (var continueItem in items)
            {
                continueItem.Item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                var entry = new HistoryService.HistoryEntry
                {
                    Id = continueItem.Item.Id,
                    Type = continueItem.Item.Type,
                    VideoId = continueItem.VideoId,
                    SeasonNumber = continueItem.SeasonNumber,
                    EpisodeNumber = continueItem.EpisodeNumber,
                    EpisodeTitle = continueItem.EpisodeTitle,
                    Name = continueItem.Item.Name,
                    PosterUrl = continueItem.Item.PosterUrl,
                    FallbackPosterUrl = continueItem.Item.FallbackPosterUrl,
                    SourceBaseUrl = continueItem.Item.SourceBaseUrl,
                    Year = continueItem.Item.Year,
                    ImdbRating = continueItem.Item.ImdbRating,
                    AddonName = continueItem.Stream.AddonName,
                    StreamDisplayName = continueItem.Stream.DisplayName,
                    Description = continueItem.Stream.Description,
                    DirectUrl = continueItem.Stream.DirectUrl,
                    EmbeddedPageUrl = continueItem.Stream.EmbeddedPageUrl,
                    MagnetUrl = continueItem.Stream.MagnetUrl,
                    ContentLogoUrl = continueItem.Stream.ContentLogoUrl,
                    StreamPosterUrl = continueItem.Stream.PosterUrl,
                    StreamFallbackPosterUrl = continueItem.Stream.FallbackPosterUrl,
                    StreamKey = continueItem.Stream.StreamKey,
                    PositionMs = continueItem.PositionMs,
                    DurationMs = continueItem.DurationMs,
                    LastPlayedUtc = continueItem.LastPlayedUtc
                };

                var card = new HistoryCardItem
                {
                    Entry = entry,
                    Item = continueItem.Item
                };
                Items.Add(card);
                _ = PreparePosterAsync(card.Item);
            }

            EmptyPanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryGrid.Visibility = Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SubtitleTextBlock.Text = Items.Count == 0 ? "Resume from where you left off" : $"{Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}";
        }

        private async Task PreparePosterAsync(MetaItem item)
        {
            try
            {
                item.IsWatched = await HistoryService.IsItemWatchedAsync(item);
                item.IsPosterLoading = true;
                var candidates = await CatalogService.GetAllPosterCandidatesAsync(item.Type, item.Id, item.PosterUrl, item.FallbackPosterUrl);
                item.SetPosterCandidates(candidates, CatalogService.PlaceholderPosterUri);
            }
            catch
            {
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
            }
        }

        private void PosterImageElement_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is Image image && image.DataContext is HistoryCardItem card)
            {
                card.Item.IsPosterLoading = false;
                if (!string.IsNullOrWhiteSpace(card.Item.Poster) && !string.Equals(card.Item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase))
                    _ = CatalogService.QueuePosterCacheIfEnabledAsync(card.Item.Id, card.Item.Poster);

                var root = FindAncestorByName(image, "PosterCardRoot");
                if (root != null)
                    UpdatePosterVisualState(root, false);
            }
        }

        private async void PosterImageElement_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is not Image image || image.DataContext is not HistoryCardItem card)
                return;

            if (!card.Item.MoveToNextPosterCandidate())
                await PreparePosterAsync(card.Item);

            var root = FindAncestorByName(image, "PosterCardRoot");
            if (root != null)
                UpdatePosterVisualState(root, false);
        }

        private async void PosterRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                await EnsurePosterBadgeInfoAsync(element);
                UpdatePosterVisualState(element, true);
            }
        }

        private void PosterRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
                UpdatePosterVisualState(element, false);
        }

        private async Task EnsurePosterBadgeInfoAsync(FrameworkElement root)
        {
            await SettingsManager.InitializeAsync();
            if (!SettingsManager.ShowPosterBadges)
                return;

            if (root.Tag is not HistoryCardItem card)
                return;

            var item = card.Item;
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

        private void UpdatePosterVisualState(FrameworkElement root, bool isHovered)
        {
            if (root.Tag is not HistoryCardItem card)
                return;

            var item = card.Item;

            if (root.RenderTransform is ScaleTransform scale)
            {
                scale.ScaleX = isHovered ? 1.06 : 1.0;
                scale.ScaleY = isHovered ? 1.06 : 1.0;
            }

            Canvas.SetZIndex(root, isHovered ? 20 : 0);

            var hasLoadedPoster = !item.IsPosterLoading && !string.IsNullOrWhiteSpace(item.Poster);

            if (FindDescendantByName(root, "PosterImageElement") is FrameworkElement posterImage)
                posterImage.Visibility = hasLoadedPoster ? Visibility.Visible : Visibility.Collapsed;

            if (FindDescendantByName(root, "PosterShimmerOverlay") is FrameworkElement shimmer)
                shimmer.Visibility = hasLoadedPoster ? Visibility.Collapsed : Visibility.Visible;

            var play = FindDescendantByName(root, "PlayOverlayButton");
            if (play != null)
                play.Visibility = isHovered ? Visibility.Visible : Visibility.Collapsed;

            var watchedBadge = FindDescendantByName(root, "WatchedBadge");
            if (watchedBadge != null)
                watchedBadge.Visibility = item.IsWatched ? Visibility.Visible : Visibility.Collapsed;

            if (FindDescendantByName(root, "PosterYearTextBlock") is TextBlock yearText)
                yearText.Text = item.Year ?? string.Empty;

            if (FindDescendantByName(root, "PosterImdbTextBlock") is TextBlock imdbText)
                imdbText.Text = item.ImdbRating ?? string.Empty;

            var imdbPanel = FindDescendantByName(root, "PosterImdbPanel");
            if (imdbPanel != null)
                imdbPanel.Visibility = string.IsNullOrWhiteSpace(item.ImdbRating) ? Visibility.Collapsed : Visibility.Visible;

            var hasBadges = SettingsManager.ShowPosterBadges &&
                            isHovered &&
                            (!string.IsNullOrWhiteSpace(item.Year) || !string.IsNullOrWhiteSpace(item.ImdbRating));

            var overlay = FindDescendantByName(root, "PosterInfoOverlay");
            if (overlay != null)
                overlay.Visibility = hasBadges ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PosterBackgroundGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is HistoryCardItem card)
            {
                Frame.Navigate(typeof(DetailsPage), new DetailsNavigationRequest
                {
                    Item = card.Item,
                    SeasonNumber = card.Entry.SeasonNumber,
                    VideoId = card.Entry.VideoId
                });
            }
        }

        private void PlayOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is HistoryCardItem card)
                OpenEntry(card);
        }

        private void PlayOverlayButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                if (button.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 1.1;
                    scale.ScaleY = 1.1;
                }

                button.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0, 0, 0));
            }
        }

        private void PlayOverlayButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                if (button.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 1.0;
                    scale.ScaleY = 1.0;
                }

                button.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xD8, 0, 0, 0));
            }
        }

        private static FrameworkElement? FindAncestorByName(DependencyObject? element, string name)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                    return fe;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static FrameworkElement? FindDescendantByName(DependencyObject? root, string name)
        {
            if (root == null)
                return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
                    return fe;

                var nested = FindDescendantByName(child, name);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private void HistoryGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not HistoryCardItem card)
                return;

            OpenEntry(card);
        }

        private void OpenEntry(HistoryCardItem card)
        {
            var stream = new CatalogService.StreamOption
            {
                AddonName = card.Entry.AddonName,
                DisplayName = card.Entry.StreamDisplayName,
                Description = card.Entry.Description,
                DirectUrl = card.Entry.DirectUrl,
                EmbeddedPageUrl = card.Entry.EmbeddedPageUrl,
                MagnetUrl = card.Entry.MagnetUrl,
                ContentName = card.Entry.Name,
                ContentType = card.Entry.Type,
                ContentLogoUrl = card.Entry.ContentLogoUrl,
                PosterUrl = card.Entry.StreamPosterUrl,
                FallbackPosterUrl = card.Entry.StreamFallbackPosterUrl,
                Year = card.Entry.Year,
                ImdbRating = card.Entry.ImdbRating,
                ContentId = card.Entry.Id,
                SourceBaseUrl = card.Entry.SourceBaseUrl,
                VideoId = card.Entry.VideoId,
                SeasonNumber = card.Entry.SeasonNumber,
                EpisodeNumber = card.Entry.EpisodeNumber,
                EpisodeTitle = card.Entry.EpisodeTitle,
                StreamKey = card.Entry.StreamKey,
                StartPositionMs = card.Entry.PositionMs
            };

            if (!string.IsNullOrWhiteSpace(stream.DirectUrl) || !string.IsNullOrWhiteSpace(stream.EmbeddedPageUrl) || !string.IsNullOrWhiteSpace(stream.MagnetUrl))
            {
                App.MainAppWindow?.ShowPlayer(stream);
                return;
            }

            Frame.Navigate(typeof(DetailsPage), new DetailsNavigationRequest
            {
                Item = card.Item,
                SeasonNumber = card.Entry.SeasonNumber,
                VideoId = card.Entry.VideoId
            });
        }

        private async void DismissButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not HistoryCardItem card)
                return;

            await HistoryService.DismissContinueWatchingAsync(card.Item.Type, card.Item.Id);
            Items.Remove(card);
            EmptyPanel.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HistoryGrid.Visibility = Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SubtitleTextBlock.Text = Items.Count == 0 ? "Resume from where you left off" : $"{Items.Count} item{(Items.Count == 1 ? string.Empty : "s")}";
        }

        private async Task MarkHistoryCardWatchedAsync(HistoryCardItem card)
        {
            if (card == null || card.Item == null)
                return;

            if (string.Equals(card.Item.Type, "series", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(card.Entry.VideoId))
            {
                var episode = new CatalogService.SeriesEpisodeOption
                {
                    VideoId = card.Entry.VideoId,
                    Season = card.Entry.SeasonNumber ?? 0,
                    Episode = card.Entry.EpisodeNumber ?? 0,
                    Title = card.Entry.EpisodeTitle ?? string.Empty
                };

                await HistoryService.SetSeriesEpisodesWatchedAsync(card.Item, new[] { episode }, true, markWholeSeries: false);
            }
            else
            {
                await HistoryService.MarkItemWatchedAsync(card.Item, true);
            }

            await RefreshAsync();
        }

        private async void PosterRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not HistoryCardItem card)
                return;

            var flyout = new MenuFlyout();
            var watchedItem = new MenuFlyoutItem { Text = "Mark as watched", Tag = card };
            watchedItem.Click += async (_, __) =>
            {
                if (watchedItem.Tag is HistoryCardItem historyCard)
                    await MarkHistoryCardWatchedAsync(historyCard);
            };

            var dismissItem = new MenuFlyoutItem { Text = "Dismiss", Tag = card };
            dismissItem.Click += async (_, __) =>
            {
                if (dismissItem.Tag is HistoryCardItem historyCard)
                    await HistoryService.DismissContinueWatchingAsync(historyCard.Item.Type, historyCard.Item.Id);
            };
            var detailsItem = new MenuFlyoutItem { Text = "Details", Tag = card };
            detailsItem.Click += (_, __) =>
            {
                if (detailsItem.Tag is HistoryCardItem historyCard)
                {
                    Frame.Navigate(typeof(DetailsPage), new DetailsNavigationRequest
                    {
                        Item = historyCard.Item,
                        SeasonNumber = historyCard.Entry.SeasonNumber,
                        VideoId = historyCard.Entry.VideoId
                    });
                }
            };
            flyout.Items.Add(watchedItem);
            flyout.Items.Add(dismissItem);
            flyout.Items.Add(detailsItem);
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
    }
}
