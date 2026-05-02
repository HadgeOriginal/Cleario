using Cleario.Models;
using Cleario.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cleario.Pages
{
    public sealed partial class HomePage : Page
    {
        private bool _isLoading;
        private readonly List<DiscoverService.DiscoverCatalogDefinition> _catalogs = new();
        private PosterSizeMode _lastAppliedPosterSize = SettingsManager.PosterSize;
        private string _lastHomeCatalogSettingsSignature = string.Empty;
        private bool _suppressNextContinueCardClick;
        private StackPanel? _continueWatchingSection;
        private StackPanel? _continueWatchingRowPanel;
        private bool _suppressNextContinueWatchingRefresh;
        private int _homeLoadVersion;
        private bool _continueWatchingRefreshInProgress;
        private int _continueWatchingSectionVersion;
        private int _lastRequestedVisiblePosterCount;
        private int _homePosterFillRepairAttempt;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _homeSizeReloadTimer;
        private static readonly object _homeCatalogPreviewCacheLock = new();
        private static string _homeCatalogPreviewCacheSignature = string.Empty;
        private static PosterSizeMode _homeCatalogPreviewCachePosterSize = SettingsManager.PosterSize;
        private static int _homeCatalogPreviewCacheVisiblePosterCount;
        private static List<CatalogPreviewData> _homeCatalogPreviewCache = new();

        private sealed class CatalogPreviewData
        {
            public DiscoverService.DiscoverCatalogDefinition Catalog { get; init; } = new();
            public List<MetaItem> Items { get; init; } = new();
        }

        private sealed class CatalogSectionTag
        {
            public string Key { get; init; } = string.Empty;
            public int ItemCount { get; init; }
            public int RequestedVisiblePosterCount { get; init; }
        }


        public HomePage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            Loaded += HomePage_Loaded;
            Unloaded += HomePage_Unloaded;
            SizeChanged += HomePage_SizeChanged;
        }

        private void HomePage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_catalogs.Count == 0 || _isLoading)
                return;

            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 40)
                return;

            ScheduleHomeReloadForSizeChange();
        }

        private void ScheduleHomeReloadForSizeChange()
        {
            _homeSizeReloadTimer ??= DispatcherQueue.CreateTimer();
            _homeSizeReloadTimer.Stop();
            _homeSizeReloadTimer.Interval = TimeSpan.FromMilliseconds(350);
            _homeSizeReloadTimer.Tick -= HomeSizeReloadTimer_Tick;
            _homeSizeReloadTimer.Tick += HomeSizeReloadTimer_Tick;
            _homeSizeReloadTimer.Start();
        }

        private async void HomeSizeReloadTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            sender.Stop();

            if (_isLoading || _catalogs.Count == 0)
                return;

            var currentVisiblePosterCount = GetVisiblePosterCount();
            if (currentVisiblePosterCount <= 0 || currentVisiblePosterCount == _lastRequestedVisiblePosterCount)
                return;

            _homePosterFillRepairAttempt = 0;
            ClearHomeCatalogPreviewCache();
            await LoadHomeAsync(forceReload: true);
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            HistoryService.HistoryChanged -= HistoryService_HistoryChanged;
            HistoryService.HistoryChanged += HistoryService_HistoryChanged;
        }

        private void HomePage_Unloaded(object sender, RoutedEventArgs e)
        {
            HistoryService.HistoryChanged -= HistoryService_HistoryChanged;
        }

        private async void HistoryService_HistoryChanged(object? sender, EventArgs e)
        {
            if (_suppressNextContinueWatchingRefresh)
            {
                _suppressNextContinueWatchingRefresh = false;
                return;
            }

            DispatcherQueue.TryEnqueue(async () =>
            {
                await RefreshContinueWatchingSectionAsync();
                await RefreshVisiblePosterWatchStatesAsync();
            });
            await Task.CompletedTask;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await SettingsManager.InitializeAsync();
            var posterSizeChanged = _lastAppliedPosterSize != SettingsManager.PosterSize;
            var homeCatalogSettingsChanged = !string.Equals(_lastHomeCatalogSettingsSignature, BuildHomeCatalogSettingsSignature(), StringComparison.OrdinalIgnoreCase);
            if (posterSizeChanged || homeCatalogSettingsChanged || SettingsManager.SaveMemory)
                _homePosterFillRepairAttempt = 0;

            if (_catalogs.Count == 0 || posterSizeChanged || homeCatalogSettingsChanged || SettingsManager.SaveMemory)
                await LoadHomeAsync(forceReload: true);
            else
            {
                await RefreshContinueWatchingSectionAsync();
                await RefreshVisiblePosterWatchStatesAsync();
            }
        }

        private static string BuildHomeCatalogSettingsSignature()
        {
            var order = SettingsManager.HomeCatalogOrder == null ? string.Empty : string.Join("|", SettingsManager.HomeCatalogOrder);
            var disabled = SettingsManager.HomeCatalogDisabled == null ? string.Empty : string.Join("|", SettingsManager.HomeCatalogDisabled.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return $"{order}::{disabled}";
        }

        private async Task RefreshVisiblePosterWatchStatesAsync()
        {
            foreach (var refs in EnumeratePosterCardRefs(SectionsHost).ToList())
            {
                refs.Item.IsWatched = await HistoryService.IsItemWatchedAsync(refs.Item);
                UpdatePosterCardVisualState(refs, false);
            }
        }

        private static IEnumerable<PosterCardVisualRefs> EnumeratePosterCardRefs(DependencyObject? root)
        {
            if (root == null)
                yield break;

            if (root is FrameworkElement { Tag: PosterCardVisualRefs refs })
                yield return refs;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                foreach (var nested in EnumeratePosterCardRefs(VisualTreeHelper.GetChild(root, i)))
                    yield return nested;
            }
        }

        private async Task LoadHomeAsync(bool forceReload)
        {
            if (_isLoading)
                return;

            _isLoading = true;
            var loadVersion = ++_homeLoadVersion;
            LoadingPanel.Visibility = Visibility.Visible;
            ClearCatalogSections();

            try
            {
                await SettingsManager.InitializeAsync(forceReload);
                NavigationCacheMode = SettingsManager.SaveMemory ? NavigationCacheMode.Disabled : NavigationCacheMode.Required;
                _lastAppliedPosterSize = SettingsManager.PosterSize;
                _lastHomeCatalogSettingsSignature = BuildHomeCatalogSettingsSignature();
                await AddonManager.InitializeAsync(forceReload);

                var visiblePosterCount = GetVisiblePosterCount();
                _lastRequestedVisiblePosterCount = visiblePosterCount;

                if (await TryRestoreCatalogSectionsFromCacheAsync(_lastHomeCatalogSettingsSignature, visiblePosterCount))
                    return;

                var catalogs = await DiscoverService.GetDiscoverCatalogsAsync();
                _catalogs.Clear();
                _catalogs.AddRange(DiscoverService.OrderCatalogsForHome(catalogs, SettingsManager.HomeCatalogOrder)
                    .Where(c => !SettingsManager.HomeCatalogDisabled.Contains(DiscoverService.BuildCatalogKey(c), StringComparer.OrdinalIgnoreCase)));

                await AddContinueWatchingSectionAsync();
                EnsureSingleContinueWatchingSection();

                var previewTasks = _catalogs
                    .Select(catalog => LoadCatalogPreviewAsync(catalog, visiblePosterCount))
                    .ToList();

                CatalogPreviewData?[] previews;
                try
                {
                    previews = await Task.WhenAll(previewTasks);
                }
                catch
                {
                    previews = new CatalogPreviewData?[previewTasks.Count];

                    for (var i = 0; i < previewTasks.Count; i++)
                    {
                        try
                        {
                            previews[i] = previewTasks[i].IsCompletedSuccessfully ? previewTasks[i].Result : await previewTasks[i];
                        }
                        catch
                        {
                            previews[i] = null;
                        }
                    }
                }

                if (loadVersion != _homeLoadVersion)
                    return;

                var addedAnyCatalog = false;
                foreach (var preview in previews)
                {
                    if (preview != null && preview.Items.Count > 0)
                    {
                        AddCatalogSection(preview.Catalog, preview.Items);
                        EnsureSingleContinueWatchingSection();
                        addedAnyCatalog = true;
                    }
                }

                SaveCatalogSectionsToCache(_lastHomeCatalogSettingsSignature, visiblePosterCount, previews);
                EnsureSingleContinueWatchingSection();

                if (!addedAnyCatalog && _continueWatchingSection == null)
                {
                    SectionsHost.Children.Add(new TextBlock
                    {
                        Text = "No home catalogs found from your enabled addons.",
                        Opacity = 0.75
                    });
                }
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                _isLoading = false;
                _ = ValidateHomePosterFillAfterLayoutAsync(loadVersion, _lastRequestedVisiblePosterCount);
            }
        }

        private async Task<bool> TryRestoreCatalogSectionsFromCacheAsync(string signature, int visiblePosterCount)
        {
            if (SettingsManager.SaveMemory)
                return false;

            List<CatalogPreviewData> cachedPreviews;
            lock (_homeCatalogPreviewCacheLock)
            {
                if (_homeCatalogPreviewCache.Count == 0 ||
                    !string.Equals(_homeCatalogPreviewCacheSignature, signature, StringComparison.OrdinalIgnoreCase) ||
                    _homeCatalogPreviewCachePosterSize != SettingsManager.PosterSize ||
                    _homeCatalogPreviewCacheVisiblePosterCount != visiblePosterCount)
                {
                    return false;
                }

                cachedPreviews = _homeCatalogPreviewCache
                    .Select(preview => new CatalogPreviewData
                    {
                        Catalog = preview.Catalog,
                        Items = preview.Items.ToList()
                    })
                    .ToList();
            }

            _catalogs.Clear();
            _catalogs.AddRange(cachedPreviews.Select(preview => preview.Catalog));

            await AddContinueWatchingSectionAsync();
            EnsureSingleContinueWatchingSection();

            foreach (var preview in cachedPreviews)
            {
                if (preview.Items.Count == 0)
                    continue;

                AddCatalogSection(preview.Catalog, preview.Items);
                EnsureSingleContinueWatchingSection();
            }

            EnsureSingleContinueWatchingSection();
            return cachedPreviews.Count > 0 || _continueWatchingSection != null;
        }

        private static void SaveCatalogSectionsToCache(string signature, int visiblePosterCount, IEnumerable<CatalogPreviewData?> previews)
        {
            if (SettingsManager.SaveMemory)
                return;

            var cachedPreviews = previews
                .Where(preview => preview != null && preview.Items.Count > 0)
                .Select(preview => new CatalogPreviewData
                {
                    Catalog = preview!.Catalog,
                    Items = preview.Items.ToList()
                })
                .ToList();

            lock (_homeCatalogPreviewCacheLock)
            {
                _homeCatalogPreviewCacheSignature = signature;
                _homeCatalogPreviewCachePosterSize = SettingsManager.PosterSize;
                _homeCatalogPreviewCacheVisiblePosterCount = visiblePosterCount;
                _homeCatalogPreviewCache = cachedPreviews;
            }
        }

        private static void ClearHomeCatalogPreviewCache()
        {
            lock (_homeCatalogPreviewCacheLock)
            {
                _homeCatalogPreviewCacheSignature = string.Empty;
                _homeCatalogPreviewCacheVisiblePosterCount = 0;
                _homeCatalogPreviewCache = new List<CatalogPreviewData>();
            }
        }

        private async Task ValidateHomePosterFillAfterLayoutAsync(int loadVersion, int requestedVisiblePosterCount)
        {
            try
            {
                await Task.Delay(550);
            }
            catch
            {
                return;
            }

            if (!IsLoaded || _isLoading || loadVersion != _homeLoadVersion || requestedVisiblePosterCount <= 0)
                return;

            var currentVisiblePosterCount = GetVisiblePosterCount();
            if (currentVisiblePosterCount <= requestedVisiblePosterCount)
                return;

            if (!HasUnderfilledCatalogSection(currentVisiblePosterCount))
                return;

            if (_homePosterFillRepairAttempt >= 2)
                return;

            _homePosterFillRepairAttempt++;
            ClearHomeCatalogPreviewCache();
            await LoadHomeAsync(forceReload: true);
        }


        private bool HasUnderfilledCatalogSection(int expectedVisiblePosterCount)
        {
            if (SectionsHost == null)
                return false;

            foreach (var section in SectionsHost.Children.OfType<FrameworkElement>())
            {
                if (section.Tag is CatalogSectionTag tag && tag.ItemCount < expectedVisiblePosterCount)
                    return true;
            }

            return false;
        }

        private void ClearCatalogSections()
        {
            _continueWatchingSection = null;
            _continueWatchingRowPanel = null;
            for (int i = SectionsHost.Children.Count - 1; i >= 0; i--)
            {
                var element = SectionsHost.Children[i];
                if (!ReferenceEquals(element, LoadingPanel))
                    SectionsHost.Children.RemoveAt(i);
            }
        }

        private int GetVisiblePosterCount()
        {
            var layout = PosterLayoutService.GetCurrent();
            var cardWidth = layout.HomeWidth;
            const double spacing = 18;
            const double outerPadding = 48;

            var availableWidth = HomeRootScrollViewer?.ActualWidth > 0
                ? HomeRootScrollViewer.ActualWidth - outerPadding
                : (ActualWidth > 0 ? ActualWidth - outerPadding : 1400);

            if (availableWidth < cardWidth)
                return 1;

            var count = (int)Math.Floor((availableWidth + spacing) / (cardWidth + spacing));
            if (count < 1)
                count = 1;

            return count;
        }

        private async Task<CatalogPreviewData?> LoadCatalogPreviewAsync(
            DiscoverService.DiscoverCatalogDefinition catalog,
            int visiblePosterCount)
        {
            var genre = catalog.RequiresGenre && catalog.GenreOptions.Count > 0 ? catalog.GenreOptions[0] : string.Empty;
            var rowItems = new List<MetaItem>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skip = 0;

            for (var attempt = 0; attempt < 4 && rowItems.Count < visiblePosterCount; attempt++)
            {
                var items = await DiscoverService.GetCatalogItemsAsync(catalog.SourceBaseUrl, catalog, skip, string.Empty, genre);
                if (items.Count == 0)
                    break;

                foreach (var item in items)
                {
                    var key = !string.IsNullOrWhiteSpace(item.Id) ? item.Id : $"{item.Name}|{item.Type}|{rowItems.Count}";
                    if (seenIds.Add(key))
                        rowItems.Add(item);

                    if (rowItems.Count >= visiblePosterCount)
                        break;
                }

                if (!catalog.SupportsSkip)
                    break;

                skip += items.Count;
                if (rowItems.Count < visiblePosterCount)
                    await Task.Delay(120);
            }

            return rowItems.Count == 0
                ? null
                : new CatalogPreviewData
                {
                    Catalog = catalog,
                    Items = rowItems.Take(visiblePosterCount).ToList()
                };
        }

        private async Task RefreshContinueWatchingSectionAsync()
        {
            if (SectionsHost == null || _continueWatchingRefreshInProgress || _isLoading)
                return;

            _continueWatchingRefreshInProgress = true;
            try
            {
                RemoveAllContinueWatchingSections();
                _continueWatchingSection = null;
                _continueWatchingRowPanel = null;
                await AddContinueWatchingSectionAsync();
                EnsureSingleContinueWatchingSection();
            }
            finally
            {
                _continueWatchingRefreshInProgress = false;
            }
        }

        private void EnsureSingleContinueWatchingSection()
        {
            RemoveDuplicateContinueWatchingSections();

            if (SectionsHost == null)
                return;

            var continueSection = SectionsHost.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(x => string.Equals(x.Tag?.ToString(), "ContinueWatchingSection", StringComparison.Ordinal));

            if (continueSection == null)
                return;

            var currentIndex = SectionsHost.Children.IndexOf(continueSection);
            var loadingIndex = SectionsHost.Children.IndexOf(LoadingPanel);
            var targetIndex = loadingIndex >= 0 ? loadingIndex : 0;

            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                SectionsHost.Children.RemoveAt(currentIndex);
                SectionsHost.Children.Insert(targetIndex, continueSection);
            }
        }

        private void RemoveAllContinueWatchingSections()
        {
            if (SectionsHost == null)
                return;

            for (int i = SectionsHost.Children.Count - 1; i >= 0; i--)
            {
                if (SectionsHost.Children[i] is FrameworkElement existing
                    && string.Equals(existing.Tag?.ToString(), "ContinueWatchingSection", StringComparison.Ordinal))
                {
                    SectionsHost.Children.RemoveAt(i);
                }
            }
        }

        private void RemoveDuplicateContinueWatchingSections()
        {
            if (SectionsHost == null)
                return;

            var foundFirst = false;
            for (int i = SectionsHost.Children.Count - 1; i >= 0; i--)
            {
                if (SectionsHost.Children[i] is not FrameworkElement existing
                    || !string.Equals(existing.Tag?.ToString(), "ContinueWatchingSection", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!foundFirst)
                {
                    foundFirst = true;
                    continue;
                }

                SectionsHost.Children.RemoveAt(i);
            }
        }

        private async Task AddContinueWatchingSectionAsync()
        {
            var buildVersion = ++_continueWatchingSectionVersion;

            RemoveAllContinueWatchingSections();
            _continueWatchingSection = null;
            _continueWatchingRowPanel = null;

            var items = await HistoryService.GetContinueWatchingAsync(forceReload: true);
            if (buildVersion != _continueWatchingSectionVersion)
                return;

            if (items.Count == 0)
                return;

            RemoveAllContinueWatchingSections();

            var section = new StackPanel { Spacing = 12, Tag = "ContinueWatchingSection" };
            _continueWatchingSection = section;
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            headerGrid.Children.Add(new TextBlock
            {
                Text = "Continue watching",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var seeAllButton = new Button
            {
                Content = "See All",
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(12, 6, 12, 6)
            };
            seeAllButton.Click += ContinueWatchingSeeAllButton_Click;
            Grid.SetColumn(seeAllButton, 1);
            headerGrid.Children.Add(seeAllButton);
            section.Children.Add(headerGrid);

            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 18
            };
            _continueWatchingRowPanel = rowPanel;

            foreach (var item in items.Take(GetVisiblePosterCount()))
            {
                item.Item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                rowPanel.Children.Add(CreateContinueWatchingCard(item));
                _ = PrepareContinueWatchingPosterAsync(item.Item);
            }

            section.Children.Add(rowPanel);
            var insertIndex = SectionsHost.Children.IndexOf(LoadingPanel);
            if (insertIndex < 0)
                insertIndex = 0;
            SectionsHost.Children.Insert(insertIndex, section);
            EnsureSingleContinueWatchingSection();
        }

        private UIElement CreateContinueWatchingCard(HistoryService.ContinueWatchingItem entry)
        {
            var layout = PosterLayoutService.GetCurrent();
            var root = new Grid { Width = layout.HomeWidth, Tag = entry.DismissKey };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var posterRoot = new Grid
            {
                Width = layout.HomeWidth,
                Height = layout.HomeHeight,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Tag = entry
            };
            var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            posterRoot.RenderTransform = scale;
            posterRoot.PointerEntered += PosterCardRoot_PointerEntered;
            posterRoot.PointerExited += PosterCardRoot_PointerExited;
            posterRoot.Loaded += PosterCardRoot_Loaded;
            posterRoot.Tapped += ContinueWatchingPoster_Tapped;
            posterRoot.RightTapped += ContinueWatchingCard_RightTapped;

            var border = new Border
            {
                Width = layout.HomeWidth,
                Height = layout.HomeHeight,
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x2C, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x13, 0x13, 0x13, 0x13))
            };

            var inner = new Grid();
            var image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
            image.SetBinding(Image.SourceProperty, new Binding { Path = new PropertyPath(nameof(MetaItem.Poster)), Mode = BindingMode.OneWay, Source = entry.Item });
            image.ImageFailed += Poster_ImageFailed;
            image.ImageOpened += Poster_ImageOpened;

            var shimmer = new Grid { Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x18, 0x18, 0x18)), IsHitTestVisible = false };
            shimmer.Children.Add(new Border
            {
                Opacity = 0.9,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF,0x1B,0x1B,0x1B), Offset = 0 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF,0x2A,0x2A,0x2A), Offset = 0.5 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF,0x1B,0x1B,0x1B), Offset = 1 }
                    }
                }
            });

            var overlay = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8, 8, 8, 26),
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xD0, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            var overlayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var yearText = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var imdbPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            imdbPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xC5, 0x18)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5,1,5,1),
                Child = new TextBlock { Text = "IMDb", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold }
            });
            var imdbText = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xC5, 0x18)), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            imdbPanel.Children.Add(imdbText);
            overlayRow.Children.Add(yearText);
            overlayRow.Children.Add(imdbPanel);
            overlay.Child = overlayRow;

            var playButton = new Button
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(29),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xD8, 0, 0, 0)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Visibility = Visibility.Collapsed,
                Tag = entry,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 },
                Content = new FontIcon
                {
                    Glyph = "\uE768",
                    FontSize = 28,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            playButton.Click += ContinueWatchingPlayButton_Click;
            playButton.PointerEntered += ContinueWatchingPlayButton_PointerEntered;
            playButton.PointerExited += ContinueWatchingPlayButton_PointerExited;

            var progressTrack = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(14, 0, 14, 14)
            };
            var progressFill = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                Width = Math.Max(12, (layout.HomeWidth - 28) * entry.ProgressRatio),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(14, 0, 14, 14)
            };

            var watchedBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xE0, 0x23, 0x5E, 0xD8)),
                Child = new TextBlock
                {
                    Text = "✓",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                },
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            var dismissButton = new Button
            {
                Content = "✕",
                Tag = entry,
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xBB, 0, 0, 0)),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(0)
            };
            dismissButton.Click += ContinueWatchingDismissButton_Click;
            dismissButton.Tapped += ContinueWatchingDismissButton_Tapped;
            dismissButton.PointerPressed += ContinueWatchingDismissButton_PointerPressed;

            inner.Children.Add(image);
            inner.Children.Add(shimmer);
            inner.Children.Add(overlay);
            inner.Children.Add(playButton);
            inner.Children.Add(progressTrack);
            inner.Children.Add(progressFill);
            inner.Children.Add(watchedBadge);
            border.Child = inner;
            posterRoot.Children.Add(border);
            posterRoot.Children.Add(dismissButton);
            root.Children.Add(posterRoot);

            var titleText = new TextBlock
            {
                Text = BuildContinueWatchingTitle(entry),
                TextWrapping = TextWrapping.WrapWholeWords,
                MaxLines = 2,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(titleText, 1);
            root.Children.Add(titleText);

            var refs = new PosterCardVisualRefs(entry.Item, posterRoot, image, shimmer, overlay, yearText, imdbPanel, imdbText, scale, posterRoot, playButton, watchedBadge, true, root, entry);
            posterRoot.Tag = refs;
            image.Tag = refs;
            return root;
        }

        private static string BuildContinueWatchingTitle(HistoryService.ContinueWatchingItem entry)
        {
            var baseName = StripEpisodeSuffix(entry.Item.Name);
            if (!string.Equals(entry.Item.Type, "series", StringComparison.OrdinalIgnoreCase))
                return baseName;

            if (entry.SeasonNumber.HasValue && entry.EpisodeNumber.HasValue)
            {
                if (entry.SeasonNumber.Value <= 0)
                    return $"{baseName} (Special {entry.EpisodeNumber.Value:00})";

                return $"{baseName} ({entry.SeasonNumber.Value}x{entry.EpisodeNumber.Value:00})";
            }

            return baseName;
        }

        private static string StripEpisodeSuffix(string? name)
        {
            var value = name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = System.Text.RegularExpressions.Regex.Replace(value, @"\s*\((\d+x\d{2}|Special\s+\d{2})\)\s*$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return value.Trim();
        }

        private void AddCatalogSection(DiscoverService.DiscoverCatalogDefinition catalog, List<MetaItem> rowItems)
        {
            if (rowItems.Count == 0)
                return;

            var section = new StackPanel
            {
                Spacing = 12,
                Tag = new CatalogSectionTag
                {
                    Key = DiscoverService.BuildCatalogKey(catalog),
                    ItemCount = rowItems.Count,
                    RequestedVisiblePosterCount = _lastRequestedVisiblePosterCount
                }
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            headerGrid.Children.Add(new TextBlock
            {
                Text = $"{catalog.Name} - {ToTypeDisplayName(catalog.Type)}",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });

            var seeAllButton = new Button
            {
                Content = "See All",
                Tag = catalog,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(12, 6, 12, 6)
            };
            seeAllButton.Click += SeeAllButton_Click;
            Grid.SetColumn(seeAllButton, 1);
            headerGrid.Children.Add(seeAllButton);
            section.Children.Add(headerGrid);

            var rowPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 18
            };

            foreach (var item in rowItems)
            {
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
                rowPanel.Children.Add(CreatePosterCard(item));
                _ = PreparePosterAsync(item);
            }

            section.Children.Add(rowPanel);
            SectionsHost.Children.Add(section);
        }

        private UIElement CreatePosterCard(MetaItem item)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            button.Click += PosterCard_Click;
            button.RightTapped += PosterCard_RightTapped;

            var layout = PosterLayoutService.GetCurrent();
            var container = new StackPanel { Width = layout.HomeWidth, Spacing = 8 };
            var posterRoot = new Grid { Width = layout.HomeWidth, Height = layout.HomeHeight, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5) };
            var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
            posterRoot.RenderTransform = scale;
            posterRoot.PointerEntered += PosterCardRoot_PointerEntered;
            posterRoot.PointerExited += PosterCardRoot_PointerExited;
            posterRoot.Loaded += PosterCardRoot_Loaded;

            var border = new Border
            {
                Width = layout.HomeWidth,
                Height = layout.HomeHeight,
                CornerRadius = new CornerRadius(10),
                BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x2C, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x13, 0x13, 0x13, 0x13))
            };

            var inner = new Grid();
            var image = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
            image.SetBinding(Image.SourceProperty, new Binding { Path = new PropertyPath(nameof(MetaItem.Poster)), Mode = BindingMode.OneWay, Source = item });
            image.ImageFailed += Poster_ImageFailed;
            image.ImageOpened += Poster_ImageOpened;

            var shimmer = new Grid { Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x18, 0x18, 0x18)), IsHitTestVisible = false };
            shimmer.Children.Add(new Border
            {
                Opacity = 0.9,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF,0x1B,0x1B,0x1B), Offset = 0 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF,0x2A,0x2A,0x2A), Offset = 0.5 },
                        new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0xFF,0x1B,0x1B,0x1B), Offset = 1 }
                    }
                }
            });

            var overlay = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(8),
                Padding = new Thickness(10, 6, 10, 6),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xD0, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            var overlayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var yearText = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var imdbPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            imdbPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xC5, 0x18)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5,1,5,1),
                Child = new TextBlock { Text = "IMDb", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold }
            });
            var imdbText = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF5, 0xC5, 0x18)), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            imdbPanel.Children.Add(imdbText);
            overlayRow.Children.Add(yearText);
            overlayRow.Children.Add(imdbPanel);
            overlay.Child = overlayRow;

            var watchedBadge = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xE0, 0x23, 0x5E, 0xD8)),
                Child = new TextBlock
                {
                    Text = "✓",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                },
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            inner.Children.Add(image);
            inner.Children.Add(shimmer);
            inner.Children.Add(overlay);
            inner.Children.Add(watchedBadge);
            border.Child = inner;
            posterRoot.Children.Add(border);
            container.Children.Add(posterRoot);
            container.Children.Add(new TextBlock { Text = item.Name, TextWrapping = TextWrapping.WrapWholeWords, MaxLines = 2, TextAlignment = TextAlignment.Center });
            button.Content = container;

            var refs = new PosterCardVisualRefs(item, posterRoot, image, shimmer, overlay, yearText, imdbPanel, imdbText, scale, button, null, watchedBadge, false, button, null);
            posterRoot.Tag = refs;
            image.Tag = refs;
            button.Tag = item;
            return button;
        }

        private async Task PreparePosterAsync(MetaItem item)
        {
            try
            {
                item.IsPosterLoading = true;
                item.IsWatched = await HistoryService.IsItemWatchedAsync(item);
                var candidates = await CatalogService.GetBrowsePosterCandidatesAsync(item.Id, item.PosterUrl, item.FallbackPosterUrl);
                item.SetPosterCandidates(candidates, CatalogService.PlaceholderPosterUri);
            }
            catch
            {
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
            }
        }

        private async Task PrepareContinueWatchingPosterAsync(MetaItem item)
        {
            try
            {
                item.IsPosterLoading = true;
                item.IsWatched = await HistoryService.IsItemWatchedAsync(item);

                var candidates = new List<string>();
                foreach (var candidate in await CatalogService.GetAllPosterCandidatesAsync(item.Type, item.Id, item.PosterUrl, item.FallbackPosterUrl))
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(candidate);
                }

                if (candidates.Count == 0)
                    candidates.Add(CatalogService.PlaceholderPosterUri);

                item.SetPosterCandidates(candidates, CatalogService.PlaceholderPosterUri);
            }
            catch
            {
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
            }
        }

        private void Poster_ImageOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image || image.Tag is not PosterCardVisualRefs refs)
                return;

            if (!string.Equals(refs.Item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(refs.Item.Poster))
            {
                refs.Item.IsPosterLoading = false;
                _ = CatalogService.QueuePosterCacheIfEnabledAsync(refs.Item.Id, refs.Item.Poster);
            }

            UpdatePosterCardVisualState(refs, false);
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
            if (sender is not Image image || image.Tag is not PosterCardVisualRefs refs)
                return;

            refs.Item.IsPosterLoading = true;
            if (string.IsNullOrWhiteSpace(refs.Item.Poster) || refs.Item.Poster == CatalogService.PlaceholderPosterUri)
            {
                if (refs.PushOverlayAboveProgress)
                    await PrepareContinueWatchingPosterAsync(refs.Item);
                else
                    await PreparePosterAsync(refs.Item);
                UpdatePosterCardVisualState(refs, false);
                return;
            }

            if (!refs.Item.MoveToNextPosterCandidate())
            {
                refs.Item.Poster = string.Empty;
                refs.Item.IsPosterLoading = true;
            }

            UpdatePosterCardVisualState(refs, false);
        }

        private void PosterCardRoot_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid root || root.Tag is not PosterCardVisualRefs refs)
                return;

            StartPosterShimmerAnimation(refs.Shimmer);
            UpdatePosterCardVisualState(refs, false);
        }

        private void PosterCardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid root || root.Tag is not PosterCardVisualRefs refs)
                return;

            refs.IsHovered = true;
            UpdatePosterCardVisualState(refs, true);
            _ = EnsurePosterBadgeInfoAsync(refs);
        }

        private void PosterCardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Grid root && root.Tag is PosterCardVisualRefs refs)
            {
                refs.IsHovered = false;
                UpdatePosterCardVisualState(refs, false);
            }
        }

        private async Task EnsurePosterBadgeInfoAsync(PosterCardVisualRefs refs)
        {
            var item = refs.Item;
            await SettingsManager.InitializeAsync();
            if (!SettingsManager.ShowPosterBadges)
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

            UpdatePosterCardVisualState(refs, refs.IsHovered);
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

        private static void StartPosterShimmerAnimation(FrameworkElement shimmer)
        {
            if (shimmer == null)
                return;

            shimmer.Opacity = 0.82;
        }

        private static void UpdatePosterCardVisualState(PosterCardVisualRefs refs, bool isHovered)
        {
            refs.Scale.ScaleX = isHovered ? 1.05 : 1.0;
            refs.Scale.ScaleY = isHovered ? 1.05 : 1.0;
            Canvas.SetZIndex(refs.Root, isHovered ? 20 : 0);

            var hasLoadedPoster = !refs.Item.IsPosterLoading && !string.IsNullOrWhiteSpace(refs.Item.Poster);
            refs.Image.Visibility = hasLoadedPoster ? Visibility.Visible : Visibility.Collapsed;
            refs.Shimmer.Visibility = hasLoadedPoster ? Visibility.Collapsed : Visibility.Visible;
            refs.YearText.Text = refs.Item.Year ?? string.Empty;
            refs.ImdbText.Text = refs.Item.ImdbRating ?? string.Empty;

            var showYear = SettingsManager.ShowPosterHoverYear && !string.IsNullOrWhiteSpace(refs.Item.Year);
            var showImdb = SettingsManager.ShowPosterHoverImdbRating && !string.IsNullOrWhiteSpace(refs.Item.ImdbRating);

            refs.YearText.Visibility = showYear ? Visibility.Visible : Visibility.Collapsed;
            refs.ImdbPanel.Visibility = showImdb ? Visibility.Visible : Visibility.Collapsed;
            refs.Overlay.Margin = refs.PushOverlayAboveProgress ? new Thickness(8, 8, 8, 26) : new Thickness(8);
            refs.Overlay.Visibility = SettingsManager.ShowPosterBadges && isHovered && (showYear || showImdb) ? Visibility.Visible : Visibility.Collapsed;
            if (refs.PlayButton != null)
                refs.PlayButton.Visibility = isHovered ? Visibility.Visible : Visibility.Collapsed;
            if (refs.WatchedBadge != null)
                refs.WatchedBadge.Visibility = refs.Item.IsWatched ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PosterCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is MetaItem item)
                Frame.Navigate(typeof(DetailsPage), item);
        }

        private async void PosterCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not MetaItem item)
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
                if (watchedItem.Tag is not MetaItem meta)
                    return;

                if (string.Equals(meta.Type, "series", StringComparison.OrdinalIgnoreCase))
                    await HistoryService.MarkItemWatchedAsync(meta, !isWatched);
                else
                    await HistoryService.MarkItemWatchedAsync(meta, !isWatched);

                meta.IsWatched = !isWatched;
            };

            flyout.Items.Add(menuItem);
            flyout.Items.Add(watchedItem);
            ShowFlyoutAtPointer(flyout, element, e.GetPosition(element));

            e.Handled = true;
        }

        private void ContinueWatchingDismissButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _suppressNextContinueCardClick = true;
            e.Handled = true;
        }

        private void ContinueWatchingDismissButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _suppressNextContinueCardClick = true;
            e.Handled = true;
        }

        private void ContinueWatchingPlayButton_PointerEntered(object sender, PointerRoutedEventArgs e)
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

        private void ContinueWatchingPlayButton_PointerExited(object sender, PointerRoutedEventArgs e)
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

        private static bool HasAncestorButton(object? originalSource)
        {
            var current = originalSource as DependencyObject;
            while (current != null)
            {
                if (current is Button)
                    return true;

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void RemoveContinueWatchingCardLocally(string dismissKey)
        {
            if (_continueWatchingRowPanel == null || string.IsNullOrWhiteSpace(dismissKey))
                return;

            UIElement? target = null;
            foreach (var child in _continueWatchingRowPanel.Children)
            {
                if (child is FrameworkElement element && string.Equals(element.Tag?.ToString(), dismissKey, StringComparison.OrdinalIgnoreCase))
                {
                    target = element;
                    break;
                }
            }

            if (target != null)
                _continueWatchingRowPanel.Children.Remove(target);

            if (_continueWatchingRowPanel.Children.Count == 0 && _continueWatchingSection != null)
            {
                SectionsHost.Children.Remove(_continueWatchingSection);
                _continueWatchingSection = null;
                _continueWatchingRowPanel = null;
            }
        }

        private void ContinueWatchingSeeAllButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(HistoryPage));
        }

        private async void ContinueWatchingPoster_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_suppressNextContinueCardClick)
            {
                _suppressNextContinueCardClick = false;
                e.Handled = true;
                return;
            }

            if (HasAncestorButton(e.OriginalSource))
            {
                e.Handled = true;
                return;
            }

            if (sender is not FrameworkElement element || element.Tag is not PosterCardVisualRefs refs)
                return;

            var entry = refs.PlayButton?.Tag as HistoryService.ContinueWatchingItem ?? refs.Button.Tag as HistoryService.ContinueWatchingItem;
            if (entry == null)
                return;

            OpenContinueWatchingDetails(entry);
            e.Handled = true;
            await Task.CompletedTask;
        }

        private async void ContinueWatchingDismissButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not HistoryService.ContinueWatchingItem entry)
                return;

            _suppressNextContinueCardClick = true;
            _suppressNextContinueWatchingRefresh = true;
            await HistoryService.DismissContinueWatchingAsync(entry.Item.Type, entry.Item.Id);
            DispatcherQueue.TryEnqueue(() => RemoveContinueWatchingCardLocally(entry.DismissKey));
        }

        private void OpenContinueWatchingDetails(HistoryService.ContinueWatchingItem entry)
        {
            Frame.Navigate(typeof(DetailsPage), new DetailsNavigationRequest
            {
                Item = entry.Item,
                SeasonNumber = entry.SeasonNumber,
                VideoId = entry.VideoId
            });
        }

        private async void ContinueWatchingPlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement button || button.Tag is not HistoryService.ContinueWatchingItem entry)
                return;

            _suppressNextContinueCardClick = true;

            if (!string.IsNullOrWhiteSpace(entry.Stream.DirectUrl) || !string.IsNullOrWhiteSpace(entry.Stream.EmbeddedPageUrl) || !string.IsNullOrWhiteSpace(entry.Stream.MagnetUrl))
            {
                App.MainAppWindow?.ShowPlayer(entry.Stream);
                return;
            }

            OpenContinueWatchingDetails(entry);
            await Task.CompletedTask;
        }

        private async Task MarkContinueWatchingItemWatchedAsync(HistoryService.ContinueWatchingItem entry)
        {
            if (entry == null || entry.Item == null)
                return;

            if (string.Equals(entry.Item.Type, "series", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.VideoId))
            {
                var episode = new CatalogService.SeriesEpisodeOption
                {
                    VideoId = entry.VideoId,
                    Season = entry.SeasonNumber ?? 0,
                    Episode = entry.EpisodeNumber ?? 0,
                    Title = entry.EpisodeTitle ?? string.Empty
                };

                await HistoryService.SetSeriesEpisodesWatchedAsync(entry.Item, new[] { episode }, true, markWholeSeries: false);
            }
            else
            {
                await HistoryService.MarkItemWatchedAsync(entry.Item, true);
            }

            await RefreshContinueWatchingSectionAsync();
        }

        private async void ContinueWatchingCard_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var entry = element.Tag as HistoryService.ContinueWatchingItem;
            if (entry == null && element.Tag is PosterCardVisualRefs refs)
                entry = refs.PlayButton?.Tag as HistoryService.ContinueWatchingItem;
            if (entry == null)
                return;

            var flyout = new MenuFlyout();

            var watchedItem = new MenuFlyoutItem { Text = "Mark as watched", Tag = entry };
            watchedItem.Click += async (_, __) =>
            {
                if (watchedItem.Tag is HistoryService.ContinueWatchingItem item)
                    await MarkContinueWatchingItemWatchedAsync(item);
            };

            var dismissItem = new MenuFlyoutItem { Text = "Dismiss", Tag = entry };
            dismissItem.Click += async (_, __) =>
            {
                if (dismissItem.Tag is HistoryService.ContinueWatchingItem item)
                    await HistoryService.DismissContinueWatchingAsync(item.Item.Type, item.Item.Id);
            };
            var detailsItem = new MenuFlyoutItem { Text = "Details", Tag = entry };
            detailsItem.Click += (_, __) =>
            {
                if (detailsItem.Tag is HistoryService.ContinueWatchingItem item)
                    OpenContinueWatchingDetails(item);
            };
            flyout.Items.Add(watchedItem);
            flyout.Items.Add(dismissItem);
            flyout.Items.Add(detailsItem);
            ShowFlyoutAtPointer(flyout, element, e.GetPosition(element));
            e.Handled = true;
        }

        private void SeeAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not DiscoverService.DiscoverCatalogDefinition catalog)
                return;

            var request = new DiscoverNavigationRequest { Type = catalog.Type, CatalogId = catalog.Id, SourceBaseUrl = catalog.SourceBaseUrl };
            App.MainAppWindow?.NavigateToDiscover(request);
        }

        private static string ToTypeDisplayName(string type)
        {
            if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase))
                return "Series";
            return "Movie";
        }

        private sealed class PosterCardVisualRefs
        {
            public MetaItem Item { get; }
            public Grid Root { get; }
            public Image Image { get; }
            public Grid Shimmer { get; }
            public Border Overlay { get; }
            public TextBlock YearText { get; }
            public StackPanel ImdbPanel { get; }
            public TextBlock ImdbText { get; }
public ScaleTransform Scale { get; }
            public FrameworkElement Button { get; }
            public FrameworkElement? PlayButton { get; }
            public FrameworkElement? WatchedBadge { get; }
            public bool PushOverlayAboveProgress { get; }
            public FrameworkElement? CardContainer { get; }
            public HistoryService.ContinueWatchingItem? ContinueWatchingEntry { get; }
            public bool IsHovered { get; set; }

            public PosterCardVisualRefs(MetaItem item, Grid root, Image image, Grid shimmer, Border overlay, TextBlock yearText, StackPanel imdbPanel, TextBlock imdbText, ScaleTransform scale, FrameworkElement button, FrameworkElement? playButton, FrameworkElement? watchedBadge, bool pushOverlayAboveProgress, FrameworkElement? cardContainer, HistoryService.ContinueWatchingItem? continueWatchingEntry)
            {
                Item = item;
                Root = root;
                Image = image;
                Shimmer = shimmer;
                Overlay = overlay;
                YearText = yearText;
                ImdbPanel = imdbPanel;
                ImdbText = imdbText;
Scale = scale;
                Button = button;
                PlayButton = playButton;
                WatchedBadge = watchedBadge;
                PushOverlayAboveProgress = pushOverlayAboveProgress;
                CardContainer = cardContainer;
                ContinueWatchingEntry = continueWatchingEntry;
            }
        }
    }
}
