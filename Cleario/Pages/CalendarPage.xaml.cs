using Cleario.Models;
using Cleario.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Cleario.Pages
{
    public sealed partial class CalendarPage : Page
    {
        private sealed class CalendarEventItem
        {
            public MetaItem Item { get; set; } = new();
            public string VideoId { get; set; } = string.Empty;
            public int? SeasonNumber { get; set; }
            public int? EpisodeNumber { get; set; }
            public DateTime ReleaseDateLocal { get; set; }
            public string EpisodeTitle { get; set; } = string.Empty;

            public string CodeText
            {
                get
                {
                    if (!string.Equals(Item.Type, "series", StringComparison.OrdinalIgnoreCase))
                        return string.Empty;

                    if ((SeasonNumber ?? 0) <= 0)
                        return EpisodeNumber.HasValue ? $"Special {EpisodeNumber.Value}" : "Special";

                    return SeasonNumber.HasValue && EpisodeNumber.HasValue
                        ? $"S{SeasonNumber.Value}E{EpisodeNumber.Value}"
                        : string.Empty;
                }
            }
        }

        private sealed class CalendarDayContext
        {
            public DateTime Date { get; set; }
            public List<CalendarEventItem> Events { get; set; } = new();
        }

        private sealed class SidebarEntryViewModel : INotifyPropertyChanged
        {
            private static readonly Brush DefaultBackground = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            private static readonly Brush DefaultBorder = new SolidColorBrush(ColorHelper.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            private static readonly Brush SelectedBackground = new SolidColorBrush(ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            private static readonly Brush SelectedBorder = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE0, 0xE0, 0xE0));

            private bool _isSelected;

            public CalendarEventItem Event { get; set; } = new();
            public string DateText { get; set; } = string.Empty;
            public string TitleText { get; set; } = string.Empty;
            public string SubtitleText { get; set; } = string.Empty;
            public string CodeText { get; set; } = string.Empty;

            public Visibility SubtitleVisibility => string.IsNullOrWhiteSpace(SubtitleText) ? Visibility.Collapsed : Visibility.Visible;
            public Brush BackgroundBrush => _isSelected ? SelectedBackground : DefaultBackground;
            public Brush BorderBrush => _isSelected ? SelectedBorder : DefaultBorder;
            public Thickness CardBorderThickness => _isSelected ? new Thickness(1.5) : new Thickness(1);

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value)
                        return;

                    _isSelected = value;
                    OnPropertyChanged(nameof(BackgroundBrush));
                    OnPropertyChanged(nameof(BorderBrush));
                    OnPropertyChanged(nameof(CardBorderThickness));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private readonly List<CalendarEventItem> _allEvents = new();
        private readonly ObservableCollection<SidebarEntryViewModel> _sidebarItems = new();
        private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime _activeDate = DateTime.Today.Date;
        private SidebarEntryViewModel? _selectedSidebarItem;
        private bool _ignoreSidebarSelectionChanged;

        public CalendarPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            UpcomingListView.ItemsSource = _sidebarItems;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _activeDate = DateTime.Today.Date;
            _displayMonth = new DateTime(_activeDate.Year, _activeDate.Month, 1);
            await LoadCalendarAsync();
        }

        private async Task LoadCalendarAsync()
        {
            LoadingPanel.Visibility = Visibility.Visible;
            EmptyTextBlock.Visibility = Visibility.Collapsed;
            CalendarGrid.Visibility = Visibility.Visible;

            try
            {
                _allEvents.Clear();
                _selectedSidebarItem = null;

                var entries = await LibraryService.GetEntriesAsync(forceReload: true);
                var libraryItems = entries.Select(LibraryService.ToMetaItem).ToList();

                var tasks = libraryItems.Select(async item =>
                {
                    try
                    {
                        return await CatalogService.GetCalendarReleaseEntriesAsync(item);
                    }
                    catch
                    {
                        return new List<CatalogService.CalendarReleaseEntry>();
                    }
                }).ToArray();

                var results = await Task.WhenAll(tasks);

                foreach (var release in results.SelectMany(x => x))
                {
                    var item = new MetaItem
                    {
                        Id = release.MetaId,
                        Type = release.Type,
                        Name = release.Name,
                        PosterUrl = release.PosterUrl,
                        FallbackPosterUrl = release.FallbackPosterUrl,
                        SourceBaseUrl = release.SourceBaseUrl,
                        IsPosterLoading = true
                    };

                    await PrepareCalendarPosterAsync(item);

                    _allEvents.Add(new CalendarEventItem
                    {
                        Item = item,
                        VideoId = release.VideoId,
                        SeasonNumber = release.Season,
                        EpisodeNumber = release.Episode,
                        EpisodeTitle = release.EpisodeTitle,
                        ReleaseDateLocal = release.ReleaseDate.ToLocalTime().DateTime.Date
                    });
                }

                var distinct = _allEvents
                    .GroupBy(x => $"{x.Item.Type}|{x.Item.Id}|{x.VideoId}|{x.ReleaseDateLocal:yyyy-MM-dd}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(x => x.ReleaseDateLocal)
                    .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.SeasonNumber ?? int.MaxValue)
                    .ThenBy(x => x.EpisodeNumber ?? int.MaxValue)
                    .ToList();

                _allEvents.Clear();
                _allEvents.AddRange(distinct);

                if (_allEvents.Count == 0)
                {
                    _sidebarItems.Clear();
                    UpcomingSubtitleTextBlock.Text = "Nothing scheduled";
                    CalendarGrid.Children.Clear();
                    CalendarGrid.RowDefinitions.Clear();
                    CalendarGrid.ColumnDefinitions.Clear();
                    EmptyTextBlock.Visibility = Visibility.Visible;
                    CalendarGrid.Visibility = Visibility.Collapsed;
                    return;
                }

                RefreshMonth();
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshMonth()
        {
            MonthYearTextBlock.Text = _displayMonth.ToString("MMMM yyyy");

            var visibleMonthEvents = _allEvents
                .Where(x => x.ReleaseDateLocal.Year == _displayMonth.Year && x.ReleaseDateLocal.Month == _displayMonth.Month)
                .OrderBy(x => x.ReleaseDateLocal)
                .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SeasonNumber ?? int.MaxValue)
                .ThenBy(x => x.EpisodeNumber ?? int.MaxValue)
                .ToList();

            UpcomingSubtitleTextBlock.Text = visibleMonthEvents.Count == 0
                ? "Nothing scheduled this month"
                : $"{visibleMonthEvents.Count} release{(visibleMonthEvents.Count == 1 ? string.Empty : "s")} this month";

            _sidebarItems.Clear();
            foreach (var calendarEvent in visibleMonthEvents)
            {
                _sidebarItems.Add(new SidebarEntryViewModel
                {
                    Event = calendarEvent,
                    DateText = calendarEvent.ReleaseDateLocal.ToString("MMM d"),
                    TitleText = calendarEvent.Item.Name,
                    SubtitleText = calendarEvent.EpisodeTitle,
                    CodeText = calendarEvent.CodeText
                });
            }

            if (_sidebarItems.Count == 0)
            {
                _selectedSidebarItem = null;
                _activeDate = new DateTime(_displayMonth.Year, _displayMonth.Month, Math.Min(_activeDate.Day, DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month)));
            }
            else
            {
                var preferredItem = _sidebarItems.FirstOrDefault(x => x.Event.ReleaseDateLocal.Date == _activeDate.Date)
                    ?? _sidebarItems.FirstOrDefault(x => x.Event.ReleaseDateLocal.Date > _activeDate.Date)
                    ?? _sidebarItems.First();

                _selectedSidebarItem = preferredItem;
                _activeDate = preferredItem.Event.ReleaseDateLocal.Date;
            }

            ApplySidebarSelection();
            BuildCalendarGrid(visibleMonthEvents);
        }

        private void BuildCalendarGrid(List<CalendarEventItem> visibleMonthEvents)
        {
            CalendarGrid.Children.Clear();
            CalendarGrid.RowDefinitions.Clear();
            CalendarGrid.ColumnDefinitions.Clear();

            for (int c = 0; c < 7; c++)
                CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition());

            for (int r = 0; r < 6; r++)
                CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var firstOfMonth = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
            int offset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
            var firstGridDate = firstOfMonth.AddDays(-offset);

            for (int index = 0; index < 42; index++)
            {
                var cellDate = firstGridDate.AddDays(index).Date;
                var eventsForDate = visibleMonthEvents
                    .Where(x => x.ReleaseDateLocal.Date == cellDate)
                    .ToList();

                var isToday = cellDate == DateTime.Today.Date;
                var isActiveDate = cellDate == _activeDate.Date;
                var containsSelectedEvent = _selectedSidebarItem != null && eventsForDate.Any(x => IsSameEvent(x, _selectedSidebarItem.Event));

                var border = new Border
                {
                    Tag = new CalendarDayContext
                    {
                        Date = cellDate,
                        Events = eventsForDate
                    },
                    Margin = new Thickness(0.5),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = containsSelectedEvent || isActiveDate
                        ? new Thickness(1.6)
                        : (isToday ? new Thickness(1.2) : new Thickness(1)),
                    BorderBrush = new SolidColorBrush(containsSelectedEvent || isActiveDate
                        ? ColorHelper.FromArgb(0xFF, 0xE0, 0xE0, 0xE0)
                        : (isToday
                            ? ColorHelper.FromArgb(0x80, 0xE0, 0xE0, 0xE0)
                            : ColorHelper.FromArgb(0x10, 0xFF, 0xFF, 0xFF))),
                    Background = new SolidColorBrush(cellDate.Month == _displayMonth.Month
                        ? ColorHelper.FromArgb(0x16, 0xFF, 0xFF, 0xFF)
                        : ColorHelper.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
                    MinHeight = 132,
                    Padding = new Thickness(8)
                };
                border.Tapped += CalendarDayBorder_Tapped;

                var cellStack = new StackPanel { Spacing = 6 };
                cellStack.Children.Add(new TextBlock
                {
                    Text = cellDate.Day.ToString(),
                    Opacity = cellDate.Month == _displayMonth.Month ? 0.92 : 0.45,
                    FontWeight = FontWeights.SemiBold
                });

                if (eventsForDate.Count > 0)
                {
                    var postersPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6
                    };

                    foreach (var calendarEvent in eventsForDate)
                    {
                        postersPanel.Children.Add(CreateCalendarPosterButton(calendarEvent));
                    }

                    cellStack.Children.Add(new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        HorizontalScrollMode = ScrollMode.Enabled,
                        VerticalScrollMode = ScrollMode.Disabled,
                        ZoomMode = ZoomMode.Disabled,
                        Content = postersPanel
                    });
                }

                border.Child = cellStack;
                Grid.SetColumn(border, index % 7);
                Grid.SetRow(border, index / 7);
                CalendarGrid.Children.Add(border);
            }
        }


        private static async Task PrepareCalendarPosterAsync(MetaItem item)
        {
            try
            {
                var candidates = await CatalogService.GetBrowsePosterCandidatesAsync(item.Id, item.PosterUrl, item.FallbackPosterUrl);
                item.SetPosterCandidates(candidates, CatalogService.PlaceholderPosterUri);
            }
            catch
            {
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);
            }
        }

        private static BitmapImage CreatePosterImageSource(string? poster)
        {
            var value = !string.IsNullOrWhiteSpace(poster)
                ? poster
                : CatalogService.PlaceholderPosterUri;

            return new BitmapImage(new Uri(value));
        }

        private async void CalendarPosterImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is not Image image || image.Tag is not MetaItem item)
                return;

            if (string.IsNullOrWhiteSpace(item.Poster) || string.Equals(item.Poster, CatalogService.PlaceholderPosterUri, StringComparison.OrdinalIgnoreCase))
                await PrepareCalendarPosterAsync(item);

            if (!item.MoveToNextPosterCandidate())
                item.SetPlaceholderPoster(CatalogService.PlaceholderPosterUri);

            image.Source = CreatePosterImageSource(item.Poster);
        }

        private void CalendarDayBorder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not Border border || border.Tag is not CalendarDayContext context)
                return;

            _activeDate = context.Date.Date;

            if (context.Events.Count > 0)
            {
                var eventToSelect = _selectedSidebarItem != null && context.Events.Any(x => IsSameEvent(x, _selectedSidebarItem.Event))
                    ? _selectedSidebarItem.Event
                    : context.Events.OrderBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.SeasonNumber ?? int.MaxValue)
                        .ThenBy(x => x.EpisodeNumber ?? int.MaxValue)
                        .First();

                SelectEvent(eventToSelect, bringIntoView: true);
            }
            else
            {
                _selectedSidebarItem = null;
                ApplySidebarSelection();
                BuildCalendarGrid(_allEvents.Where(x => x.ReleaseDateLocal.Year == _displayMonth.Year && x.ReleaseDateLocal.Month == _displayMonth.Month).ToList());
            }

            e.Handled = true;
        }

        private Button CreateCalendarPosterButton(CalendarEventItem calendarEvent)
        {
            var hoverOverlay = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0xD8, 0x00, 0x00, 0x00)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform { ScaleX = 0.84, ScaleY = 0.84 },
                Child = new FontIcon
                {
                    Glyph = "",
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var posterGrid = new Grid
            {
                Width = 56,
                Height = 82
            };

            var posterImage = new Image
            {
                Tag = calendarEvent.Item,
                Source = CreatePosterImageSource(calendarEvent.Item.Poster),
                Width = 56,
                Height = 82,
                Stretch = Stretch.UniformToFill
            };
            posterImage.ImageFailed += CalendarPosterImage_ImageFailed;

            posterGrid.Children.Add(posterImage);
            posterGrid.Children.Add(hoverOverlay);

            var posterButton = new Button
            {
                Tag = calendarEvent,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(IsSelected(calendarEvent)
                    ? ColorHelper.FromArgb(0xFF, 0xE0, 0xE0, 0xE0)
                    : ColorHelper.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                BorderThickness = IsSelected(calendarEvent) ? new Thickness(1.5) : new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Content = posterGrid
            };

            posterButton.PointerEntered += (_, __) =>
            {
                hoverOverlay.Opacity = 1;
                if (hoverOverlay.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                }
            };
            posterButton.PointerExited += (_, __) =>
            {
                hoverOverlay.Opacity = 0;
                if (hoverOverlay.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 0.84;
                    scale.ScaleY = 0.84;
                }
            };
            posterButton.PointerCanceled += (_, __) =>
            {
                hoverOverlay.Opacity = 0;
                if (hoverOverlay.RenderTransform is ScaleTransform scale)
                {
                    scale.ScaleX = 0.84;
                    scale.ScaleY = 0.84;
                }
            };

            posterButton.Click += CalendarPosterButton_Click;
            posterButton.DoubleTapped += CalendarPosterButton_DoubleTapped;
            return posterButton;
        }

        private void CalendarPosterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not CalendarEventItem calendarEvent)
                return;

            SelectEvent(calendarEvent, bringIntoView: true);
            NavigateToEvent(calendarEvent);
        }

        private void CalendarPosterButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void CalendarPosterButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button { Content: Grid grid } && grid.Children.Count > 1 && grid.Children[1] is Border overlay)
                overlay.Visibility = Visibility.Visible;
        }

        private void CalendarPosterButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button { Content: Grid grid } && grid.Children.Count > 1 && grid.Children[1] is Border overlay)
                overlay.Visibility = Visibility.Collapsed;
        }

        private void CalendarPosterButton_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not CalendarEventItem calendarEvent)
                return;

            SelectEvent(calendarEvent, bringIntoView: true);
            NavigateToEvent(calendarEvent);
            e.Handled = true;
        }

        private void SelectEvent(CalendarEventItem calendarEvent, bool bringIntoView)
        {
            var match = _sidebarItems.FirstOrDefault(x => IsSameEvent(x.Event, calendarEvent));
            if (match == null)
                return;

            _selectedSidebarItem = match;
            _activeDate = calendarEvent.ReleaseDateLocal.Date;
            ApplySidebarSelection();
            BuildCalendarGrid(_allEvents.Where(x => x.ReleaseDateLocal.Year == _displayMonth.Year && x.ReleaseDateLocal.Month == _displayMonth.Month).ToList());

            if (bringIntoView)
                UpcomingListView.ScrollIntoView(match);
        }

        private void ApplySidebarSelection()
        {
            _ignoreSidebarSelectionChanged = true;

            foreach (var item in _sidebarItems)
                item.IsSelected = _selectedSidebarItem != null && IsSameEvent(item.Event, _selectedSidebarItem.Event);

            UpcomingListView.SelectedItem = _selectedSidebarItem;
            _ignoreSidebarSelectionChanged = false;
        }

        private void UpcomingListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ignoreSidebarSelectionChanged)
                return;

            if (UpcomingListView.SelectedItem is SidebarEntryViewModel selected)
            {
                _selectedSidebarItem = selected;
                _activeDate = selected.Event.ReleaseDateLocal.Date;
                ApplySidebarSelection();
                BuildCalendarGrid(_allEvents.Where(x => x.ReleaseDateLocal.Year == _displayMonth.Year && x.ReleaseDateLocal.Month == _displayMonth.Month).ToList());
            }
        }

        private void UpcomingListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not SidebarEntryViewModel selected)
                return;

            _selectedSidebarItem = selected;
            _activeDate = selected.Event.ReleaseDateLocal.Date;
            ApplySidebarSelection();
            BuildCalendarGrid(_allEvents.Where(x => x.ReleaseDateLocal.Year == _displayMonth.Year && x.ReleaseDateLocal.Month == _displayMonth.Month).ToList());
            NavigateToEvent(selected.Event);
        }



        private void NavigateToEvent(CalendarEventItem calendarEvent)
        {
            if (string.Equals(calendarEvent.Item.Type, "series", StringComparison.OrdinalIgnoreCase))
            {
                Frame.Navigate(typeof(DetailsPage), new DetailsNavigationRequest
                {
                    Item = calendarEvent.Item,
                    SeasonNumber = calendarEvent.SeasonNumber,
                    VideoId = calendarEvent.VideoId
                });
                return;
            }

            Frame.Navigate(typeof(DetailsPage), calendarEvent.Item);
        }

        private void PreviousMonthButton_Click(object sender, RoutedEventArgs e)
        {
            _displayMonth = _displayMonth.AddMonths(-1);
            _activeDate = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
            RefreshMonth();
        }

        private void NextMonthButton_Click(object sender, RoutedEventArgs e)
        {
            _displayMonth = _displayMonth.AddMonths(1);
            _activeDate = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
            RefreshMonth();
        }

        private bool IsSelected(CalendarEventItem calendarEvent)
        {
            return _selectedSidebarItem != null && IsSameEvent(calendarEvent, _selectedSidebarItem.Event);
        }

        private static bool IsSameEvent(CalendarEventItem left, CalendarEventItem right)
        {
            return string.Equals(left.Item.Type, right.Item.Type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.Item.Id, right.Item.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.VideoId, right.VideoId, StringComparison.OrdinalIgnoreCase)
                && left.ReleaseDateLocal.Date == right.ReleaseDateLocal.Date;
        }
    }
}
