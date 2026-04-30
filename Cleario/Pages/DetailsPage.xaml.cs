using Cleario.Models;
using Cleario.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Cleario.Pages
{
    public sealed partial class DetailsPage : Page, INotifyPropertyChanged
    {
        private MetaItem? _currentItem;
        private CatalogService.MetaDetails _meta = new();
        private List<CatalogService.SeriesEpisodeOption> _seriesEpisodes = new();
        private readonly List<CatalogService.StreamOption> _allStreams = new();

        private bool _suppressSeasonSelection;
        private bool _suppressEpisodeSelection;
        private bool _suppressAddonFilterSelection;
        private bool _streamsLoading;
        private int _streamLoadVersion;
        private int _completedStreamAddons;
        private int _totalStreamAddons;
        private Dictionary<string, int> _streamAddonOrder = new(StringComparer.OrdinalIgnoreCase);

        private int? _selectedSeasonNumber;
        private string _selectedEpisodeVideoId = string.Empty;
        private bool _autoSelectNextUnwatchedEpisode;
        private bool _loadSelectedEpisodeStreamsOnLoad;
        private bool _showingFullEpisodeList;

        private readonly List<string> _posterImageCandidates = new();
        private readonly List<string> _backgroundImageCandidates = new();
        private int _posterImageCandidateIndex = -1;
        private int _backgroundImageCandidateIndex = -1;
        private string _imdbUrl = string.Empty;
        private bool _isInLibrary;
        private bool _isWatched;
        private string _currentResumeStreamKey = string.Empty;
        private long _currentResumePositionMs;
        private long _currentResumeDurationMs;

        private PosterLayoutMetrics _posterLayout = PosterLayoutService.GetCurrent();
        public double DetailsPosterWidth => _posterLayout.DetailsWidth;
        public double DetailsPosterMaxHeight => _posterLayout.DetailsMaxHeight;

        public ObservableCollection<SeasonOption> Seasons { get; } = new();
        public ObservableCollection<CatalogService.SeriesEpisodeOption> Episodes { get; } = new();
        public ObservableCollection<CatalogService.SeriesEpisodeOption> FullViewEpisodes { get; } = new();
        public ObservableCollection<string> AddonFilters { get; } = new();
        public ObservableCollection<CatalogService.StreamOption> VisibleStreams { get; } = new();

        private string _pageTitle = string.Empty;
        public string PageTitle
        {
            get => _pageTitle;
            set => SetProperty(ref _pageTitle, value);
        }

        private string _pageSubtitle = string.Empty;
        public string PageSubtitle
        {
            get => _pageSubtitle;
            set => SetProperty(ref _pageSubtitle, value);
        }

        private string _summary = string.Empty;
        public string Summary
        {
            get => _summary;
            set => SetProperty(ref _summary, value);
        }

        private string _genres = string.Empty;
        public string Genres
        {
            get => _genres;
            set => SetProperty(ref _genres, value);
        }

        private string _cast = string.Empty;
        public string Cast
        {
            get => _cast;
            set => SetProperty(ref _cast, value);
        }

        private string _directors = string.Empty;
        public string Directors
        {
            get => _directors;
            set => SetProperty(ref _directors, value);
        }

        private string _streamsHeader = "Streams";
        public string StreamsHeader
        {
            get => _streamsHeader;
            set => SetProperty(ref _streamsHeader, value);
        }

        private string _streamsSubHeader = "Loading streams...";
        public string StreamsSubHeader
        {
            get => _streamsSubHeader;
            set => SetProperty(ref _streamsSubHeader, value);
        }

        private string _selectedAddonFilter = "All";
        public string SelectedAddonFilter
        {
            get => _selectedAddonFilter;
            set => SetProperty(ref _selectedAddonFilter, value);
        }

        public DetailsPage()
        {
            InitializeComponent();
            RefreshPosterLayout();
            NavigationCacheMode = NavigationCacheMode.Required;
            DataContext = this;
            Loaded += DetailsPage_Loaded;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RefreshPosterLayout()
        {
            _posterLayout = PosterLayoutService.GetCurrent();

            if (PosterHost != null)
            {
                PosterHost.Width = _posterLayout.DetailsWidth;
            }

            if (PosterImage != null)
            {
                PosterImage.MaxWidth = _posterLayout.DetailsWidth;
                PosterImage.MaxHeight = _posterLayout.DetailsMaxHeight;
            }
        }


        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            await SettingsManager.InitializeAsync();
            RefreshPosterLayout();

            if (e.NavigationMode == NavigationMode.Back && _currentItem != null)
            {
                ApplyCollectionsToControls();
                ApplyTextToControls();
                ApplyLoadingVisuals();
                await RefreshLibraryButtonAsync();
                await RefreshWatchedButtonAsync();
                var currentItemId = _currentItem?.Id;
                if (string.Equals(_currentItem?.Type, "series", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currentItemId))
                    await LoadSeriesEpisodesAsync(currentItemId);
                return;
            }

            MetaItem? item = null;
            int? requestedSeasonNumber = null;
            string requestedVideoId = string.Empty;

            if (e.Parameter is DetailsNavigationRequest request)
            {
                item = request.Item;
                requestedSeasonNumber = request.SeasonNumber;
                requestedVideoId = request.VideoId ?? string.Empty;
            }
            else if (e.Parameter is MetaItem metaItem)
            {
                item = metaItem;
            }

            if (item != null)
            {
                var itemChanged =
                    _currentItem == null ||
                    !string.Equals(_currentItem.Id, item.Id, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(_currentItem.Type, item.Type, StringComparison.OrdinalIgnoreCase);

                var hasExplicitEpisodeTarget =
                    requestedSeasonNumber.HasValue ||
                    !string.IsNullOrWhiteSpace(requestedVideoId);

                _autoSelectNextUnwatchedEpisode =
                    !hasExplicitEpisodeTarget &&
                    string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase);
                _loadSelectedEpisodeStreamsOnLoad =
                    hasExplicitEpisodeTarget &&
                    string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase);

                _currentItem = item;

                if (_autoSelectNextUnwatchedEpisode)
                {
                    _selectedSeasonNumber = null;
                    _selectedEpisodeVideoId = string.Empty;
                }
                else if (itemChanged)
                {
                    _selectedSeasonNumber = requestedSeasonNumber;
                    _selectedEpisodeVideoId = requestedVideoId;
                }
                else
                {
                    if (requestedSeasonNumber.HasValue)
                        _selectedSeasonNumber = requestedSeasonNumber;

                    if (!string.IsNullOrWhiteSpace(requestedVideoId))
                        _selectedEpisodeVideoId = requestedVideoId;
                }

                await LoadPageAsync();
            }
        }

        private void DetailsPage_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyCollectionsToControls();
            ApplyTextToControls();
            ApplyLoadingVisuals();
        }

        private bool IsFullSeriesEpisodeViewActive()
        {
            return SettingsManager.DetailsSeriesView == DetailsSeriesViewMode.Full
                && string.Equals(_currentItem?.Type, "series", StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyEpisodeDisplaySettings()
        {
            var hideSpoilers = SettingsManager.DisableSpoilers;
            var fallbackThumbnail = GetEpisodeFallbackThumbnailUrl();
            foreach (var episode in _seriesEpisodes)
            {
                episode.HideSpoilers = hideSpoilers;
                episode.FallbackThumbnailUrl = fallbackThumbnail;
            }
        }

        private string GetEpisodeFallbackThumbnailUrl()
        {
            if (!string.IsNullOrWhiteSpace(_meta.BackgroundUrl))
                return _meta.BackgroundUrl;

            if (!string.IsNullOrWhiteSpace(_meta.PosterUrl))
                return _meta.PosterUrl;

            if (!string.IsNullOrWhiteSpace(_currentItem?.FallbackPosterUrl))
                return _currentItem.FallbackPosterUrl;

            if (!string.IsNullOrWhiteSpace(_currentItem?.PosterUrl))
                return _currentItem.PosterUrl;

            if (!string.IsNullOrWhiteSpace(_currentItem?.Poster))
                return _currentItem.Poster;

            return CatalogService.PlaceholderPosterUri;
        }

        private string BuildSelectedEpisodeHeader()
        {
            var episode = _seriesEpisodes.FirstOrDefault(x => string.Equals(x.VideoId, _selectedEpisodeVideoId, StringComparison.OrdinalIgnoreCase));
            if (episode == null)
                return string.Empty;

            var title = episode.ShouldHideSpoilers || string.IsNullOrWhiteSpace(episode.Title)
                ? episode.EpisodeCode
                : $"{episode.DisplayTitle} ({episode.EpisodeCode})";

            return $"Streams for {title}";
        }

        private void ShowFullEpisodeList()
        {
            if (!IsFullSeriesEpisodeViewActive())
                return;

            _streamLoadVersion++;
            _streamsLoading = false;
            _showingFullEpisodeList = true;
            VisibleStreams.Clear();
            _allStreams.Clear();
            StreamsSubHeader = FullViewEpisodes.Count == 1
                ? "1 episode"
                : $"{FullViewEpisodes.Count} episodes";
            ApplyCollectionsToControls();
            ApplyTextToControls();
            ApplyLoadingVisuals();
        }

        private async Task LoadPageAsync()
        {
            if (_currentItem == null)
                return;

            PageTitle = _currentItem.Name;
            PageSubtitle = "Loading...";
            Summary = "Loading...";
            Genres = string.Empty;
            Cast = string.Empty;
            Directors = string.Empty;
            StreamsHeader = string.Empty;
            StreamsSubHeader = "Loading streams...";
            UpdateImdbControls(string.Empty);

            ApplyTextToControls();
            await ApplyImagesAsync();
            ApplyLoadingVisuals();
            await RefreshLibraryButtonAsync();
            await RefreshWatchedButtonAsync();

            await AddonManager.InitializeAsync();

            _meta = await CatalogService.GetMetaDetailsAsync(_currentItem.Type, _currentItem.Id, _currentItem.SourceBaseUrl);

            var displayTitle = !string.IsNullOrWhiteSpace(_meta.Name) ? _meta.Name : _currentItem.Name;
            var year = BuildYearDisplayFromReleaseInfo(!string.IsNullOrWhiteSpace(_meta.Year) ? _meta.Year : _meta.ReleaseInfo);
            var runtime = _meta.Runtime;
            var imdb = _meta.ImdbRating;

            PageTitle = displayTitle;
            PageSubtitle = BuildMetaLine(year, runtime);
            UpdateImdbControls(imdb);
            Summary = string.IsNullOrWhiteSpace(_meta.Description) ? string.Empty : _meta.Description;
            Genres = PrefixIfNotEmpty("Genres: ", _meta.Genres);
            Cast = PrefixIfNotEmpty("Cast: ", _meta.Cast);
            Directors = PrefixIfNotEmpty("Directors: ", _meta.Directors);
            StreamsSubHeader = "Checking your enabled addons.";

            ApplyTextToControls();
            await ApplyImagesAsync();
            await UpdateDiscordDetailsPresenceAsync();
            ApplyLoadingVisuals();
            await RefreshLibraryButtonAsync();
            await RefreshWatchedButtonAsync();

            if (string.Equals(_currentItem.Type, "series", StringComparison.OrdinalIgnoreCase))
            {
                await LoadSeriesEpisodesAsync(_currentItem.Id);
            }
            else
            {
                ClearSeriesControls();
                await LoadStreamsForVideoIdAsync(_currentItem.Id);
            }
        }

        private async Task ApplyImagesAsync()
        {
            if (_currentItem == null)
                return;

            var logoCandidates = new List<string>();
            AddImageCandidate(logoCandidates, _meta.LogoUrl);
            AddImageCandidate(logoCandidates, CatalogService.BuildMetaHubLogoUrl(_currentItem.Id, "large"));
            AddImageCandidate(logoCandidates, CatalogService.BuildMetaHubLogoUrl(_currentItem.Id, "medium"));

            var posterCandidates = await CatalogService.GetAllPosterCandidatesAsync(
                _currentItem.Type,
                _currentItem.Id,
                !string.IsNullOrWhiteSpace(_meta.PosterUrl) ? _meta.PosterUrl : _currentItem.PosterUrl,
                !string.IsNullOrWhiteSpace(_currentItem.FallbackPosterUrl) ? _currentItem.FallbackPosterUrl : _currentItem.Poster);

            foreach (var candidate in posterCandidates)
                AddImageCandidate(logoCandidates, candidate);

            SetPosterCandidates(logoCandidates);

            var backgroundCandidates = new List<string>();
            AddImageCandidate(backgroundCandidates, _meta.BackgroundUrl);
            AddImageCandidate(backgroundCandidates, _meta.PosterUrl);
            AddImageCandidate(backgroundCandidates, _currentItem.PosterUrl);
            AddImageCandidate(backgroundCandidates, _currentItem.FallbackPosterUrl);

            foreach (var candidate in posterCandidates)
                AddImageCandidate(backgroundCandidates, candidate);

            AddImageCandidate(backgroundCandidates, CatalogService.PlaceholderPosterUri);
            SetBackgroundCandidates(backgroundCandidates);
        }

        private async Task UpdateDiscordDetailsPresenceAsync()
        {
            if (_currentItem == null)
                return;

            var title = !string.IsNullOrWhiteSpace(_meta.Name) ? _meta.Name : _currentItem.Name;
            var image = !string.IsNullOrWhiteSpace(_meta.LogoUrl)
                ? _meta.LogoUrl
                : (!string.IsNullOrWhiteSpace(_meta.PosterUrl)
                    ? _meta.PosterUrl
                    : (!string.IsNullOrWhiteSpace(_currentItem.PosterUrl)
                        ? _currentItem.PosterUrl
                        : (!string.IsNullOrWhiteSpace(_currentItem.FallbackPosterUrl) ? _currentItem.FallbackPosterUrl : _currentItem.Poster)));

            await DiscordRichPresenceService.SetPageActivityAsync("Details", "Details", title, "About to start", image, title);
        }

        private async Task LoadSeriesEpisodesAsync(string seriesId)
        {
            _seriesEpisodes = await CatalogService.GetSeriesEpisodesAsync(seriesId, _currentItem?.SourceBaseUrl);
            await ApplyEpisodeWatchedStateAsync();
            ApplyEpisodeDisplaySettings();
            PageSubtitle = BuildMetaLine(BuildSeriesYearDisplay(), _meta.Runtime);
            ApplyTextToControls();

            Seasons.Clear();
            Episodes.Clear();

            foreach (var group in _seriesEpisodes
                .GroupBy(x => x.Season)
                .OrderBy(g => g.Key <= 0 ? int.MaxValue : g.Key))
            {
                var allWatched = group.All(x => x.IsWatched);
                var label = group.Key <= 0 ? "Specials" : $"Season {group.Key}";
                Seasons.Add(new SeasonOption
                {
                    SeasonNumber = group.Key,
                    Label = allWatched ? $"{label} ✓" : label
                });
            }

            SetVisibility(true, "SeasonComboBox", "EpisodeSelectorsPanel");
            ApplyCollectionsToControls();

            if (Seasons.Count == 0)
            {
                VisibleStreams.Clear();
                StreamsSubHeader = "No episodes were found.";
                ApplyTextToControls();
                ApplyLoadingVisuals();
                _autoSelectNextUnwatchedEpisode = false;
                return;
            }

            if (_autoSelectNextUnwatchedEpisode)
            {
                var nextEpisode = FindNextUnwatchedEpisodeAfterLastWatched();
                if (nextEpisode != null)
                {
                    _selectedSeasonNumber = nextEpisode.Season;
                    _selectedEpisodeVideoId = nextEpisode.VideoId;
                }

                _autoSelectNextUnwatchedEpisode = false;
            }

            var defaultSeason = _selectedSeasonNumber.HasValue
                ? Seasons.FirstOrDefault(x => x.SeasonNumber == _selectedSeasonNumber.Value) ?? Seasons.First()
                : Seasons.First();

            _suppressSeasonSelection = true;
            if (SeasonComboBox != null)
                SeasonComboBox.SelectedItem = defaultSeason;
            _suppressSeasonSelection = false;

            _selectedSeasonNumber = defaultSeason.SeasonNumber;
            await LoadEpisodesForSeasonAsync(defaultSeason.SeasonNumber);
        }

        private async Task ApplyEpisodeWatchedStateAsync()
        {
            if (_currentItem == null || !string.Equals(_currentItem.Type, "series", StringComparison.OrdinalIgnoreCase))
                return;

            var watchedIds = await HistoryService.GetWatchedVideoIdsAsync(_currentItem.Type, _currentItem.Id);
            var allWatched = watchedIds.Contains("__series__");
            foreach (var episode in _seriesEpisodes)
                episode.IsWatched = allWatched || watchedIds.Contains(episode.VideoId);
        }

        private CatalogService.SeriesEpisodeOption? FindNextUnwatchedEpisodeAfterLastWatched()
        {
            var orderedEpisodes = _seriesEpisodes
                .OrderBy(x => x.Season <= 0 ? int.MaxValue : x.Season)
                .ThenBy(x => x.Episode)
                .ThenBy(x => x.Title)
                .ToList();

            if (orderedEpisodes.Count == 0)
                return null;

            var lastWatchedIndex = orderedEpisodes.FindLastIndex(x => x.IsWatched);
            if (lastWatchedIndex >= 0)
            {
                var nextUnwatchedAfterLastWatched = orderedEpisodes
                    .Skip(lastWatchedIndex + 1)
                    .FirstOrDefault(x => !x.IsWatched);

                if (nextUnwatchedAfterLastWatched != null)
                    return nextUnwatchedAfterLastWatched;
            }

            return orderedEpisodes.FirstOrDefault(x => !x.IsWatched) ?? orderedEpisodes.FirstOrDefault();
        }

        private async Task LoadEpisodesForSeasonAsync(int seasonNumber)
        {
            Episodes.Clear();
            FullViewEpisodes.Clear();
            ApplyEpisodeDisplaySettings();

            foreach (var episode in _seriesEpisodes
                .Where(x => x.Season == seasonNumber)
                .OrderBy(x => x.Episode)
                .ThenBy(x => x.Title))
            {
                Episodes.Add(episode);
                FullViewEpisodes.Add(episode);
            }

            ApplyCollectionsToControls();

            if (Episodes.Count == 0)
            {
                VisibleStreams.Clear();
                _allStreams.Clear();
                _showingFullEpisodeList = false;
                StreamsSubHeader = "No episodes were found for this season.";
                ApplyTextToControls();
                ApplyLoadingVisuals();
                return;
            }

            var selectedEpisode = !string.IsNullOrWhiteSpace(_selectedEpisodeVideoId)
                ? Episodes.FirstOrDefault(x => string.Equals(x.VideoId, _selectedEpisodeVideoId, StringComparison.OrdinalIgnoreCase)) ?? Episodes[0]
                : Episodes[0];

            _selectedEpisodeVideoId = selectedEpisode.VideoId;

            _suppressEpisodeSelection = true;
            if (EpisodeComboBox != null)
                EpisodeComboBox.SelectedItem = selectedEpisode;
            _suppressEpisodeSelection = false;

            if (IsFullSeriesEpisodeViewActive() && !_loadSelectedEpisodeStreamsOnLoad)
            {
                ShowFullEpisodeList();
                return;
            }

            _loadSelectedEpisodeStreamsOnLoad = false;
            await LoadEpisodeAsync(selectedEpisode);
        }

        private async Task LoadEpisodeAsync(CatalogService.SeriesEpisodeOption episode)
        {
            if (_currentItem == null)
                return;

            // Coming soon / unreleased episodes are still allowed to open the stream view.
            // Some addons can already provide streams before the metadata release date, so do not
            // block the click here. Only show the coming-soon message if the episode has no id to query.
            if (string.IsNullOrWhiteSpace(episode.VideoId))
            {
                _selectedSeasonNumber = episode.Season;
                _selectedEpisodeVideoId = episode.VideoId;
                VisibleStreams.Clear();
                _allStreams.Clear();
                _streamsLoading = false;
                StreamsSubHeader = "This episode is coming soon.";
                _showingFullEpisodeList = false;
                ApplyCollectionsToControls();
                ApplyTextToControls();
                ApplyLoadingVisuals();
                return;
            }

            _selectedSeasonNumber = episode.Season;
            _selectedEpisodeVideoId = episode.VideoId;
            _showingFullEpisodeList = false;

            var episodeLabel = episode.Season <= 0
                ? $"Special {episode.Episode:00}"
                : $"S{episode.Season:00}E{episode.Episode:00}";

            StreamsSubHeader = "Checking your enabled addons.";
            ApplyTextToControls();

            await LoadStreamsForVideoIdAsync(episode.VideoId);
        }

        private async Task LoadStreamsForVideoIdAsync(string videoId)
        {
            var currentItem = _currentItem;
            if (_currentItem == null || string.IsNullOrWhiteSpace(videoId))
                return;

            int loadVersion = ++_streamLoadVersion;
            _streamsLoading = true;
            _completedStreamAddons = 0;
            _totalStreamAddons = 0;

            VisibleStreams.Clear();
            _allStreams.Clear();
            AddonFilters.Clear();
            AddonFilters.Add("All");
            SelectedAddonFilter = "All";
            _streamAddonOrder.Clear();

            ApplyCollectionsToControls();
            ApplyTextToControls();
            ApplyLoadingVisuals();

            try
            {
                var requestId = await CatalogService.ResolveStreamRequestIdAsync(_currentItem.Type, videoId, _currentItem.SourceBaseUrl);
                var historyEntry = await HistoryService.GetEntryForVideoAsync(_currentItem.Type, _currentItem.Id, videoId);
                _currentResumeStreamKey = historyEntry?.StreamKey ?? string.Empty;
                _currentResumePositionMs = historyEntry?.PositionMs ?? 0;
                _currentResumeDurationMs = historyEntry?.DurationMs ?? 0;
                var enabledAddons = await CatalogService.GetEnabledStreamAddonsAsync(_currentItem.Type);
                _streamAddonOrder = enabledAddons
                    .Select((addon, index) => new { addon.Name, Index = index })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                    .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.First().Index, StringComparer.OrdinalIgnoreCase);
                _totalStreamAddons = enabledAddons.Count;
                _completedStreamAddons = 0;
                ApplyLoadingVisuals();

                if (enabledAddons.Count == 0)
                {
                    StreamsSubHeader = "No enabled stream addons were found.";
                    return;
                }

                var pendingTasks = enabledAddons
                    .Select(addon => new
                    {
                        Addon = addon,
                        Task = CatalogService.GetStreamsForAddonAsync(addon, _currentItem.Type, requestId)
                    })
                    .ToList();

                int completedAddons = 0;

                while (pendingTasks.Count > 0)
                {
                    var finishedTask = await Task.WhenAny(pendingTasks.Select(x => x.Task));
                    var finished = pendingTasks.First(x => x.Task == finishedTask);
                    pendingTasks.Remove(finished);
                    completedAddons++;
                    _completedStreamAddons = completedAddons;

                    if (loadVersion != _streamLoadVersion)
                        return;

                    List<CatalogService.StreamOption> addonStreams;
                    try
                    {
                        addonStreams = await finished.Task;
                    }
                    catch
                    {
                        addonStreams = new List<CatalogService.StreamOption>();
                    }

                    if (addonStreams.Count > 0)
                    {
                        var selectedEpisode = Episodes.FirstOrDefault(x => string.Equals(x.VideoId, _selectedEpisodeVideoId, StringComparison.OrdinalIgnoreCase));

                        foreach (var stream in addonStreams)
                        {
                            if (string.Equals(_currentItem.Type, "series", StringComparison.OrdinalIgnoreCase))
                            {
                                if (selectedEpisode != null)
                                    stream.ContentName = selectedEpisode.Season <= 0
                                        ? $"{PageTitle} (Special {selectedEpisode.Episode:00})"
                                        : $"{PageTitle} ({selectedEpisode.Season}x{selectedEpisode.Episode:00})";
                                else
                                    stream.ContentName = PageTitle;
                            }
                            else
                            {
                                stream.ContentName = PageTitle;
                            }

                            stream.ContentType = _currentItem.Type;
                            stream.ContentId = _currentItem.Id;
                            stream.VideoId = videoId;
                            stream.SeasonNumber = selectedEpisode?.Season;
                            stream.EpisodeNumber = selectedEpisode?.Episode;
                            stream.EpisodeTitle = selectedEpisode?.Title ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(stream.PosterUrl))
                                stream.PosterUrl = _currentItem.PosterUrl;
                            if (string.IsNullOrWhiteSpace(stream.FallbackPosterUrl))
                                stream.FallbackPosterUrl = _currentItem.FallbackPosterUrl;
                            stream.StreamKey = CatalogService.BuildStreamIdentity(stream);
                            stream.IsResumeCandidate = StreamMatchesResumeHistory(historyEntry, stream);
                            stream.ResumePositionMs = stream.IsResumeCandidate ? _currentResumePositionMs : 0;
                            stream.ResumeDurationMs = stream.IsResumeCandidate ? Math.Max(1, _currentResumeDurationMs) : 1;
                            stream.ResumeBarHeight = stream.IsResumeCandidate && _currentResumePositionMs > 0 ? 8 : 0;

                            bool duplicate = _allStreams.Any(x =>
                                string.Equals(x.DisplayName, stream.DisplayName, StringComparison.Ordinal) &&
                                string.Equals(x.Description, stream.Description, StringComparison.Ordinal) &&
                                string.Equals(x.DirectUrl, stream.DirectUrl, StringComparison.Ordinal) &&
                                string.Equals(x.EmbeddedPageUrl, stream.EmbeddedPageUrl, StringComparison.Ordinal) &&
                                string.Equals(x.MagnetUrl, stream.MagnetUrl, StringComparison.Ordinal));

                            if (!duplicate)
                                _allStreams.Add(stream);
                        }

                        RebuildAddonFilters();
                    }

                    ApplyAddonFilterInternal();
                    StreamsSubHeader = _allStreams.Count == 0
                        ? $"0 streams found · {completedAddons}/{enabledAddons.Count} addons loaded"
                        : $"{_allStreams.Count} streams found · {completedAddons}/{enabledAddons.Count} addons loaded";

                    ApplyCollectionsToControls();
                    ApplyTextToControls();
                    ApplyLoadingVisuals();
                }

                ApplyFallbackResumeCandidate(_allStreams, historyEntry, _currentResumePositionMs, _currentResumeDurationMs);
                ApplyAddonFilterInternal();
                ApplyCollectionsToControls();

                if (_allStreams.Count == 0)
                    StreamsSubHeader = "No streams were found.";
            }
            catch
            {
                if (loadVersion != _streamLoadVersion)
                    return;

                VisibleStreams.Clear();
                _allStreams.Clear();
                _completedStreamAddons = 0;
                _totalStreamAddons = 0;
                AddonFilters.Clear();
                AddonFilters.Add("All");
                _streamAddonOrder.Clear();
                StreamsSubHeader = "Failed to load streams.";
            }
            finally
            {
                if (loadVersion == _streamLoadVersion)
                {
                    _streamsLoading = false;
                    ApplyCollectionsToControls();
                    ApplyTextToControls();
                    ApplyLoadingVisuals();
                }
            }
        }

        private void ApplyAddonFilterInternal()
        {
            VisibleStreams.Clear();

            IEnumerable<CatalogService.StreamOption> filtered = _allStreams;
            if (!string.Equals(SelectedAddonFilter, "All", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(x => string.Equals(x.AddonName, SelectedAddonFilter, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var stream in filtered
                         .OrderByDescending(x => x.IsResumeCandidate)
                         .ThenBy(x => GetAddonSortIndex(x.AddonName)))
            {
                VisibleStreams.Add(stream);
            }

            ApplyCollectionsToControls();
        }

        private int GetAddonSortIndex(string? addonName)
        {
            if (!string.IsNullOrWhiteSpace(addonName) && _streamAddonOrder.TryGetValue(addonName, out var index))
                return index;

            return int.MaxValue;
        }

        private void RebuildAddonFilters()
        {
            var selected = SelectedAddonFilter;
            AddonFilters.Clear();
            AddonFilters.Add("All");

            foreach (var addonName in _allStreams
                         .Select(x => x.AddonName)
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => GetAddonSortIndex(x))
                         .ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                AddonFilters.Add(addonName!);
            }

            if (!AddonFilters.Any(x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)))
                SelectedAddonFilter = "All";
        }

        private static bool StreamMatchesResumeHistory(HistoryService.HistoryEntry? historyEntry, CatalogService.StreamOption stream)
        {
            if (historyEntry == null || stream == null)
                return false;

            var currentKey = CatalogService.BuildStreamIdentity(stream);
            if (!string.IsNullOrWhiteSpace(historyEntry.StreamKey) && string.Equals(historyEntry.StreamKey, currentKey, StringComparison.OrdinalIgnoreCase))
                return true;

            bool sameDirect = !string.IsNullOrWhiteSpace(historyEntry.DirectUrl) && string.Equals(historyEntry.DirectUrl, stream.DirectUrl, StringComparison.OrdinalIgnoreCase);
            bool sameEmbedded = !string.IsNullOrWhiteSpace(historyEntry.EmbeddedPageUrl) && string.Equals(historyEntry.EmbeddedPageUrl, stream.EmbeddedPageUrl, StringComparison.OrdinalIgnoreCase);
            bool sameMagnet = !string.IsNullOrWhiteSpace(historyEntry.MagnetUrl) && string.Equals(historyEntry.MagnetUrl, stream.MagnetUrl, StringComparison.OrdinalIgnoreCase);
            if (sameDirect || sameEmbedded || sameMagnet)
                return true;

            var sameAddon = !string.IsNullOrWhiteSpace(historyEntry.AddonName) && string.Equals(historyEntry.AddonName, stream.AddonName, StringComparison.OrdinalIgnoreCase);
            var sameDisplay = NormalizeStreamText(historyEntry.StreamDisplayName) == NormalizeStreamText(stream.DisplayName);
            var sameDescriptionSignature = GetStreamSignature(historyEntry.Description) == GetStreamSignature(stream.Description);

            return sameAddon && sameDisplay && sameDescriptionSignature && !string.IsNullOrWhiteSpace(historyEntry.StreamDisplayName);
        }

        private static string NormalizeStreamText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static string GetStreamSignature(string? description)
        {
            if (string.IsNullOrWhiteSpace(description))
                return string.Empty;

            var lines = description
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => NormalizeStreamText(x))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(4)
                .ToList();

            return string.Join("|", lines);
        }

        private static void ApplyFallbackResumeCandidate(IList<CatalogService.StreamOption> streams, HistoryService.HistoryEntry? historyEntry, long resumePositionMs, long resumeDurationMs)
        {
            if (historyEntry == null || resumePositionMs <= 0 || streams == null || streams.Count == 0 || streams.Any(x => x.IsResumeCandidate))
                return;

            CatalogService.StreamOption? fallback = streams.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(historyEntry.AddonName) &&
                string.Equals(historyEntry.AddonName, x.AddonName, StringComparison.OrdinalIgnoreCase) &&
                NormalizeStreamText(historyEntry.StreamDisplayName) == NormalizeStreamText(x.DisplayName));

            fallback ??= streams.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(historyEntry.AddonName) &&
                string.Equals(historyEntry.AddonName, x.AddonName, StringComparison.OrdinalIgnoreCase));

            fallback ??= streams.FirstOrDefault();
            if (fallback == null)
                return;

            fallback.IsResumeCandidate = true;
            fallback.ResumePositionMs = resumePositionMs;
            fallback.ResumeDurationMs = Math.Max(1, resumeDurationMs);
            fallback.ResumeBarHeight = 8;
        }

        private void ClearSeriesControls()
        {
            _selectedSeasonNumber = null;
            _selectedEpisodeVideoId = string.Empty;
            Seasons.Clear();
            Episodes.Clear();
            FullViewEpisodes.Clear();
            _showingFullEpisodeList = false;
            SetVisibility(false, "SeasonComboBox", "EpisodeSelectorsPanel");
            ApplyCollectionsToControls();
        }

        private void ApplyLoadingVisuals()
        {
            var isSeries = string.Equals(_currentItem?.Type, "series", StringComparison.OrdinalIgnoreCase);
            var isFullSeriesView = IsFullSeriesEpisodeViewActive();
            var fullEpisodeListVisible = isFullSeriesView && _showingFullEpisodeList && FullViewEpisodes.Count > 0;
            var selectedEpisodeStreamsVisible = isFullSeriesView && !_showingFullEpisodeList && !string.IsNullOrWhiteSpace(_selectedEpisodeVideoId);

            if (SeasonComboBox != null)
                SeasonComboBox.Visibility = isSeries && Seasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (EpisodeSelectorsPanel != null)
                EpisodeSelectorsPanel.Visibility = isSeries && !isFullSeriesView && Episodes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (EpisodeComboBox != null)
                EpisodeComboBox.Visibility = isFullSeriesView ? Visibility.Collapsed : Visibility.Visible;

            if (FullEpisodesListView != null)
                FullEpisodesListView.Visibility = fullEpisodeListVisible ? Visibility.Visible : Visibility.Collapsed;

            if (SelectedEpisodeHeaderPanel != null)
                SelectedEpisodeHeaderPanel.Visibility = selectedEpisodeStreamsVisible ? Visibility.Visible : Visibility.Collapsed;

            if (SelectedEpisodeHeaderTextBlock != null)
                SelectedEpisodeHeaderTextBlock.Text = BuildSelectedEpisodeHeader();

            if (StreamsLoadingPanel != null)
                StreamsLoadingPanel.Visibility = !fullEpisodeListVisible && _streamsLoading && VisibleStreams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (StreamsProgressPanel != null)
                StreamsProgressPanel.Visibility = !fullEpisodeListVisible && _streamsLoading && _totalStreamAddons > 0 ? Visibility.Visible : Visibility.Collapsed;

            var totalAddons = Math.Max(1, _totalStreamAddons);
            var completedAddons = Math.Clamp(_completedStreamAddons, 0, totalAddons);

            if (StreamsProgressBar != null)
            {
                StreamsProgressBar.Maximum = totalAddons;
                StreamsProgressBar.Value = completedAddons;
            }

            if (StreamsProgressTextBlock != null)
                StreamsProgressTextBlock.Text = _totalStreamAddons > 0
                    ? $"Loading addons: {completedAddons}/{_totalStreamAddons} loaded"
                    : "Loading addons...";

            if (StreamsProgressStreamsTextBlock != null)
                StreamsProgressStreamsTextBlock.Text = _allStreams.Count == 1
                    ? "1 stream found"
                    : $"{_allStreams.Count} streams found";

            if (NoStreamsPanel != null)
                NoStreamsPanel.Visibility = !fullEpisodeListVisible && !_streamsLoading && VisibleStreams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (StreamsListView != null)
                StreamsListView.Visibility = !fullEpisodeListVisible && VisibleStreams.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (NoStreamsTitleTextBlock != null)
                NoStreamsTitleTextBlock.Text = "No streams found";

            if (NoStreamsBodyTextBlock != null)
                NoStreamsBodyTextBlock.Text = "Try another addon, season, or episode.";
        }

        private void ApplyTextToControls()
        {
            SetText(PageTitle, "PageTitleTextBlock", "TitleTextBlock");
            SetText(PageSubtitle, "PageSubtitleTextBlock", "FactsTextBlock");
            SetText(Genres, "GenresTextBlock");
            SetText(Cast, "CastTextBlock");
            SetText(Directors, "DirectorsTextBlock");
            SetText(Summary, "DescriptionTextBlock");
            SetText(StreamsSubHeader, "StreamsLoadingSubtitleTextBlock");
            SetText(_streamsLoading && VisibleStreams.Count == 0 ? "Loading streams..." : (VisibleStreams.Count == 0 ? "No streams found" : string.Empty), "StreamsLoadingTitleTextBlock", "NoStreamsTitleTextBlock");

            if (NoStreamsBodyTextBlock != null && !_streamsLoading && VisibleStreams.Count == 0)
                NoStreamsBodyTextBlock.Text = "Try another addon, season, or episode.";

            UpdateLayout();
        }

        private void ApplyCollectionsToControls()
        {
            SetItemsSourceIfDifferent(SeasonComboBox, Seasons);
            SetItemsSourceIfDifferent(EpisodeComboBox, Episodes);
            SetItemsSourceIfDifferent(AddonFilterComboBox, AddonFilters);
            SetItemsSourceIfDifferent(StreamsListView, VisibleStreams);
            SetItemsSourceIfDifferent(FullEpisodesListView, FullViewEpisodes);

            if (AddonFilterComboBox != null && AddonFilters.Count > 0)
            {
                _suppressAddonFilterSelection = true;
                AddonFilterComboBox.SelectedItem = AddonFilters.Contains(SelectedAddonFilter) ? SelectedAddonFilter : "All";
                _suppressAddonFilterSelection = false;
            }

            UpdateLayout();
        }

        private static void SetItemsSourceIfDifferent(ItemsControl? control, object source)
        {
            if (control == null)
                return;

            if (!ReferenceEquals(control.ItemsSource, source))
                control.ItemsSource = source;
        }

        private void SetText(string value, params string[] names)
        {
            foreach (var name in names)
            {
                if (FindName(name) is TextBlock tb)
                    tb.Text = value ?? string.Empty;
            }
        }

        private void SetVisibility(bool visible, params string[] names)
        {
            foreach (var name in names)
            {
                if (FindName(name) is FrameworkElement fe)
                    fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void SetPosterCandidates(IEnumerable<string> candidates)
        {
            _posterImageCandidates.Clear();

            foreach (var candidate in candidates)
                AddImageCandidate(_posterImageCandidates, candidate);

            AddImageCandidate(_posterImageCandidates, CatalogService.PlaceholderPosterUri);

            _posterImageCandidateIndex = -1;
            ShowNextPosterCandidate();
        }

        private void SetBackgroundCandidates(IEnumerable<string> candidates)
        {
            _backgroundImageCandidates.Clear();

            foreach (var candidate in candidates)
                AddImageCandidate(_backgroundImageCandidates, candidate);

            AddImageCandidate(_backgroundImageCandidates, CatalogService.PlaceholderPosterUri);

            _backgroundImageCandidateIndex = -1;
            ShowNextBackgroundCandidate();
        }

        private bool ShowNextPosterCandidate()
        {
            return ShowNextImageCandidate(
                PosterImage,
                _posterImageCandidates,
                ref _posterImageCandidateIndex,
                CatalogService.PlaceholderPosterUri);
        }

        private bool ShowNextBackgroundCandidate()
        {
            return ShowNextImageCandidate(
                PageBackgroundImage,
                _backgroundImageCandidates,
                ref _backgroundImageCandidateIndex,
                CatalogService.PlaceholderPosterUri);
        }

        private static bool ShowNextImageCandidate(
            Image? image,
            List<string> candidates,
            ref int currentIndex,
            string fallbackUrl)
        {
            if (image == null)
                return false;

            while (currentIndex + 1 < candidates.Count)
            {
                currentIndex++;
                if (TrySetImageSource(image, candidates[currentIndex]))
                    return true;
            }

            return TrySetImageSource(image, fallbackUrl);
        }

        private static bool TrySetImageSource(Image? image, string? url)
        {
            if (image == null || string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            image.Source = new BitmapImage(uri);
            return true;
        }

        private static void AddImageCandidate(List<string> candidates, string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (!candidates.Contains(url, StringComparer.OrdinalIgnoreCase))
                candidates.Add(url);
        }

        private string BuildSeriesYearDisplay()
        {
            if (HasKnownUpcomingEpisodes())
            {
                var startYear = ExtractYears(string.Join(" ", new[] { _currentItem?.Year, _meta.ReleaseInfo, _meta.Year }
                        .Where(x => !string.IsNullOrWhiteSpace(x))))
                    .Concat(_seriesEpisodes.SelectMany(x => ExtractYears(x.ReleaseDate)))
                    .Where(x => x > 0)
                    .DefaultIfEmpty(0)
                    .Min();

                if (startYear > 0)
                    return $"{startYear} -";
            }

            var explicitRange = BuildExplicitSeriesYearRangeFromMetadataText(_currentItem?.Year);
            if (!string.IsNullOrWhiteSpace(explicitRange))
                return explicitRange;

            explicitRange = BuildExplicitSeriesYearRangeFromMetadataText(_meta.ReleaseInfo);
            if (!string.IsNullOrWhiteSpace(explicitRange))
                return explicitRange;

            explicitRange = BuildExplicitSeriesYearRangeFromMetadataText(_meta.Year);
            if (!string.IsNullOrWhiteSpace(explicitRange))
                return explicitRange;

            var metadataYears = ExtractYears(string.Join(" ", new[] { _currentItem?.Year, _meta.ReleaseInfo, _meta.Year }
                    .Where(x => !string.IsNullOrWhiteSpace(x))))
                .Where(x => x > 0)
                .ToList();

            var releasedEpisodeYears = _seriesEpisodes
                .Where(x => x.IsReleased)
                .SelectMany(x => ExtractYears(x.ReleaseDate))
                .Where(x => x > 0 && x <= DateTimeOffset.Now.Year)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            var allYears = metadataYears.Concat(releasedEpisodeYears).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
            if (allYears.Count == 0)
                return string.Empty;

            var start = allYears.Min();
            var end = releasedEpisodeYears.Count > 0 ? releasedEpisodeYears.Max() : allYears.Max();
            return HasKnownUpcomingEpisodes() ? $"{start} -" : (end > start ? $"{start} - {end}" : start.ToString());
        }

        private static string BuildExplicitSeriesYearRangeFromMetadataText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var trimmed = text.Trim();
            var years = ExtractYears(trimmed).Distinct().OrderBy(x => x).ToList();
            if (years.Count == 0)
                return string.Empty;

            var start = years.First();
            if (LooksLikeOpenEndedYearRange(trimmed))
                return $"{start} -";

            if (years.Count >= 2)
                return $"{start} - {years.Last()}";

            return string.Empty;
        }

        private bool HasKnownUpcomingEpisodes()
        {
            return _seriesEpisodes.Any(x => !x.IsReleased);
        }

        private static string BuildYearDisplayFromReleaseInfo(string releaseInfo)
        {
            if (string.IsNullOrWhiteSpace(releaseInfo))
                return string.Empty;

            var trimmed = releaseInfo.Trim();
            var years = ExtractYears(trimmed).Distinct().OrderBy(x => x).ToList();
            if (years.Count == 0)
                return string.Empty;

            var start = years.Min();
            if (LooksLikeOpenEndedYearRange(trimmed))
                return $"{start} -";

            var end = years.Max();
            return end > start ? $"{start} - {end}" : start.ToString();
        }

        private static bool LooksLikeOpenEndedYearRange(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return Regex.IsMatch(text.Trim(), @"\b(19|20)\d{2}\s*[-–—]\s*$");
        }

        private static List<int> ExtractYears(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<int>();

            return Regex.Matches(text, @"\b(19|20)\d{2}\b")
                .Select(x => int.TryParse(x.Value, out var year) ? year : 0)
                .Where(x => x > 0)
                .ToList();
        }

        private static string BuildMetaLine(string year, string runtime)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(year)) parts.Add(year);
            if (!string.IsNullOrWhiteSpace(runtime)) parts.Add(runtime);
            return string.Join("  •  ", parts);
        }

        private void UpdateImdbControls(string imdbRating)
        {
            var imdbId = ExtractImdbId(_currentItem?.Id);
            _imdbUrl = !string.IsNullOrWhiteSpace(imdbId)
                ? $"https://www.imdb.com/title/{imdbId}/"
                : string.Empty;

            if (ImdbButton != null)
                ImdbButton.Visibility = !string.IsNullOrWhiteSpace(_imdbUrl) && !string.IsNullOrWhiteSpace(imdbRating)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (ImdbRatingTextBlock != null)
                ImdbRatingTextBlock.Text = imdbRating ?? string.Empty;
        }

        private static string ExtractImdbId(string? rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId))
                return string.Empty;

            var match = Regex.Match(rawId, @"tt\d+");
            return match.Success ? match.Value : string.Empty;
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

        private static string PrefixIfNotEmpty(string prefix, string value)
            => string.IsNullOrWhiteSpace(value) ? string.Empty : prefix + value;

        private async Task OpenSelectedStreamAsync(CatalogService.StreamOption? stream)
        {
            if (stream == null)
                return;

            var externalWebUrl = GetExternalWebPageUrl(stream);
            if (!string.IsNullOrWhiteSpace(externalWebUrl))
            {
                await OpenExternalWebPageAsync(externalWebUrl);
                return;
            }

            if (string.Equals(_currentItem?.Type, "series", StringComparison.OrdinalIgnoreCase))
            {
                var selectedEpisode = Episodes.FirstOrDefault(x => string.Equals(x.VideoId, _selectedEpisodeVideoId, StringComparison.OrdinalIgnoreCase));
                if (selectedEpisode != null)
                {
                    stream.ContentName = selectedEpisode.Season <= 0
                        ? $"{PageTitle} (Special {selectedEpisode.Episode:00})"
                        : $"{PageTitle} ({selectedEpisode.Season}x{selectedEpisode.Episode:00})";
                    stream.VideoId = selectedEpisode.VideoId;
                    stream.SeasonNumber = selectedEpisode.Season;
                    stream.EpisodeNumber = selectedEpisode.Episode;
                    stream.EpisodeTitle = selectedEpisode.Title ?? string.Empty;
                }
                else
                {
                    stream.ContentName = PageTitle;
                    stream.VideoId = !string.IsNullOrWhiteSpace(_selectedEpisodeVideoId) ? _selectedEpisodeVideoId : stream.VideoId;
                }
            }
            else
            {
                stream.ContentName = PageTitle;
            }
            stream.ContentType = _currentItem?.Type ?? stream.ContentType;
            stream.ContentId = _currentItem?.Id ?? stream.ContentId;
            stream.SourceBaseUrl = _currentItem?.SourceBaseUrl ?? stream.SourceBaseUrl;
            stream.Year = !string.IsNullOrWhiteSpace(_meta.Year) ? _meta.Year : ExtractYear(_meta.ReleaseInfo);
            stream.ImdbRating = _meta.ImdbRating;
            stream.ContentLogoUrl = !string.IsNullOrWhiteSpace(_meta.LogoUrl) ? _meta.LogoUrl : CatalogService.BuildMetaHubLogoUrl(_currentItem?.Id ?? string.Empty, "medium");
            stream.PosterUrl = !string.IsNullOrWhiteSpace(_meta.PosterUrl) ? _meta.PosterUrl : (_currentItem?.PosterUrl ?? string.Empty);
            stream.FallbackPosterUrl = !string.IsNullOrWhiteSpace(_currentItem?.FallbackPosterUrl) ? _currentItem.FallbackPosterUrl : (_currentItem?.Poster ?? string.Empty);
            if (stream.IsResumeCandidate && stream.ResumePositionMs > 15_000)
                stream.StartPositionMs = stream.ResumePositionMs;
            else if (_currentResumePositionMs > 15_000 && stream.StartPositionMs <= 0)
                stream.StartPositionMs = _currentResumePositionMs;

            if (App.MainAppWindow != null)
            {
                App.MainAppWindow.ShowPlayer(stream);
            }
            else
            {
                Frame.Navigate(typeof(PlayerPage), stream);
            }

            await Task.CompletedTask;
        }

        private static string GetExternalWebPageUrl(CatalogService.StreamOption stream)
        {
            
            
            
            if (!string.IsNullOrWhiteSpace(stream.DirectUrl))
                return string.Empty;

            
            
            if (!string.IsNullOrWhiteSpace(stream.EmbeddedPageUrl) &&
                Uri.TryCreate(stream.EmbeddedPageUrl, UriKind.Absolute, out var embeddedUri) &&
                (embeddedUri.Scheme == Uri.UriSchemeHttp || embeddedUri.Scheme == Uri.UriSchemeHttps))
            {
                return embeddedUri.ToString();
            }

            return string.Empty;
        }

        private static async Task OpenExternalWebPageAsync(string url)
        {
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

        private CatalogService.StreamOption? GetStreamFromSender(object sender)
        {
            if (sender is FrameworkElement fe)
            {
                if (fe.Tag is CatalogService.StreamOption taggedStream)
                    return taggedStream;

                if (fe.DataContext is CatalogService.StreamOption dcStream)
                    return dcStream;
            }

            return null;
        }

        private MetaItem? BuildLibraryMetaItem()
        {
            if (_currentItem == null)
                return null;

            return new MetaItem
            {
                Id = _currentItem.Id,
                Type = _currentItem.Type,
                Name = !string.IsNullOrWhiteSpace(PageTitle) ? PageTitle : _currentItem.Name,
                PosterUrl = !string.IsNullOrWhiteSpace(_meta.PosterUrl)
                    ? _meta.PosterUrl
                    : (!string.IsNullOrWhiteSpace(_currentItem.PosterUrl) ? _currentItem.PosterUrl : _currentItem.Poster),
                FallbackPosterUrl = !string.IsNullOrWhiteSpace(_currentItem.FallbackPosterUrl)
                    ? _currentItem.FallbackPosterUrl
                    : (!string.IsNullOrWhiteSpace(_currentItem.Poster) ? _currentItem.Poster : string.Empty),
                Poster = !string.IsNullOrWhiteSpace(_meta.PosterUrl)
                    ? _meta.PosterUrl
                    : (!string.IsNullOrWhiteSpace(_currentItem.Poster) ? _currentItem.Poster : CatalogService.PlaceholderPosterUri),
                Year = !string.IsNullOrWhiteSpace(_meta.Year) ? _meta.Year : ExtractYear(_meta.ReleaseInfo),
                ImdbRating = _meta.ImdbRating,
                SourceBaseUrl = !string.IsNullOrWhiteSpace(_currentItem.SourceBaseUrl) ? _currentItem.SourceBaseUrl : string.Empty
            };
        }

        private async Task RefreshWatchedButtonAsync()
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
            if (item == null)
                return;

            _isWatched = await HistoryService.IsItemWatchedAsync(item);
            if (WatchedToggleButton != null)
                WatchedToggleButton.Content = _isWatched ? "Remove watched" : "Mark as watched";
            if (PosterWatchedBadge != null)
                PosterWatchedBadge.Visibility = _isWatched ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task RefreshLibraryButtonAsync()
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
            _isInLibrary = await LibraryService.ContainsAsync(item);

            if (LibraryToggleButton != null)
                LibraryToggleButton.Content = _isInLibrary ? "Remove from Library" : "Add to Library";
        }

        private async Task ShowPosterLibraryFlyoutAsync(FrameworkElement element, Point position)
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
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
                if (menuItem.Tag is not MetaItem metaItem)
                    return;

                if (await LibraryService.ContainsAsync(metaItem))
                    await LibraryService.RemoveAsync(metaItem);
                else
                    await LibraryService.AddOrUpdateAsync(metaItem);

                await RefreshLibraryButtonAsync();
            };

            var watchedItem = new MenuFlyoutItem
            {
                Text = isWatched ? "Remove watched" : "Mark as watched",
                Tag = item
            };
            watchedItem.Click += async (_, __) =>
            {
                if (watchedItem.Tag is not MetaItem metaItem)
                    return;

                if (string.Equals(metaItem.Type, "series", StringComparison.OrdinalIgnoreCase))
                {
                    var newWatchedState = !isWatched;
                    SetLocalEpisodeWatchedState(_seriesEpisodes, newWatchedState);
                    await HistoryService.MarkSeriesEpisodesWatchedAsync(metaItem, _seriesEpisodes, newWatchedState);
                    await RefreshSeriesWatchUiAsync();
                }
                else
                {
                    await HistoryService.MarkItemWatchedAsync(metaItem, !isWatched);
                    await RefreshWatchedButtonAsync();
                    ApplyCollectionsToControls();
                }
            };

            flyout.Items.Add(menuItem);
            flyout.Items.Add(watchedItem);
            ShowFlyoutAtPointer(flyout, element, position);
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

        private async void LibraryToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
            if (item == null)
                return;

            if (await LibraryService.ContainsAsync(item))
                await LibraryService.RemoveAsync(item);
            else
                await LibraryService.AddOrUpdateAsync(item);

            await RefreshLibraryButtonAsync();
        }

        private async void WatchedToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
            if (item == null)
                return;

            if (string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase))
            {
                var newWatchedState = !_isWatched;
                SetLocalEpisodeWatchedState(_seriesEpisodes, newWatchedState);
                await HistoryService.MarkSeriesEpisodesWatchedAsync(item, _seriesEpisodes, newWatchedState);
                await RefreshSeriesWatchUiAsync();
            }
            else
            {
                await HistoryService.MarkItemWatchedAsync(item, !_isWatched);
                await RefreshWatchedButtonAsync();
                ApplyCollectionsToControls();
            }
        }

        private async void PosterHost_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            await ShowPosterLibraryFlyoutAsync(element, e.GetPosition(element));
            e.Handled = true;
        }

        private async void ImdbButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_imdbUrl) || !Uri.TryCreate(_imdbUrl, UriKind.Absolute, out var uri))
                return;

            try
            {
                await Launcher.LaunchUriAsync(uri);
            }
            catch
            {
            }
        }

        private async Task RefreshSeriesWatchUiAsync()
        {
            await RefreshWatchedButtonAsync();
            await ApplyEpisodeWatchedStateAsync();
            RebuildSeasonAndEpisodeListsWithoutReload();
            ApplyTextToControls();
            ApplyLoadingVisuals();
            UpdateLayout();
        }

        private void SetLocalEpisodeWatchedState(IEnumerable<CatalogService.SeriesEpisodeOption> episodes, bool watched)
        {
            foreach (var episode in episodes)
                episode.IsWatched = watched;
        }

        private void RebuildSeasonAndEpisodeListsWithoutReload()
        {
            var selectedSeason = _selectedSeasonNumber ?? (SeasonComboBox?.SelectedItem as SeasonOption)?.SeasonNumber ?? Seasons.FirstOrDefault()?.SeasonNumber ?? 0;
            var selectedVideoId = !string.IsNullOrWhiteSpace(_selectedEpisodeVideoId)
                ? _selectedEpisodeVideoId
                : (EpisodeComboBox?.SelectedItem as CatalogService.SeriesEpisodeOption)?.VideoId ?? string.Empty;

            ApplyEpisodeDisplaySettings();

            Seasons.Clear();
            foreach (var group in _seriesEpisodes
                .GroupBy(x => x.Season)
                .OrderBy(g => g.Key <= 0 ? int.MaxValue : g.Key))
            {
                var allWatched = group.All(x => x.IsWatched);
                var label = group.Key <= 0 ? "Specials" : $"Season {group.Key}";
                Seasons.Add(new SeasonOption
                {
                    SeasonNumber = group.Key,
                    Label = allWatched ? $"{label} ✓" : label
                });
            }

            Episodes.Clear();
            FullViewEpisodes.Clear();
            foreach (var episode in _seriesEpisodes
                .Where(x => x.Season == selectedSeason)
                .OrderBy(x => x.Episode)
                .ThenBy(x => x.Title))
            {
                Episodes.Add(episode);
                FullViewEpisodes.Add(episode);
            }

            _suppressSeasonSelection = true;
            if (SeasonComboBox != null)
                SeasonComboBox.SelectedItem = Seasons.FirstOrDefault(x => x.SeasonNumber == selectedSeason);
            _suppressSeasonSelection = false;

            _suppressEpisodeSelection = true;
            if (EpisodeComboBox != null)
                EpisodeComboBox.SelectedItem = Episodes.FirstOrDefault(x => string.Equals(x.VideoId, selectedVideoId, StringComparison.OrdinalIgnoreCase)) ?? Episodes.FirstOrDefault();
            _suppressEpisodeSelection = false;

            ApplyCollectionsToControls();
        }

        private async Task ToggleSeasonWatchedAsync(SeasonOption season)
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
            if (item == null)
                return;

            var seasonEpisodes = _seriesEpisodes.Where(x => x.Season == season.SeasonNumber).OrderBy(x => x.Episode).ToList();
            var allWatched = seasonEpisodes.Count > 0 && seasonEpisodes.All(x => x.IsWatched);
            var newWatchedState = !allWatched;
            SetLocalEpisodeWatchedState(seasonEpisodes, newWatchedState);
            await HistoryService.SetSeriesEpisodesWatchedAsync(item, seasonEpisodes, newWatchedState, markWholeSeries: false);
            await RefreshSeriesWatchUiAsync();
        }

        private async Task ToggleEpisodeWatchedAsync(CatalogService.SeriesEpisodeOption episode)
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
            if (item == null)
                return;

            var newWatchedState = !episode.IsWatched;
            episode.IsWatched = newWatchedState;
            await HistoryService.SetSeriesEpisodesWatchedAsync(item, new[] { episode }, newWatchedState, markWholeSeries: false);
            await RefreshSeriesWatchUiAsync();
        }

        private async Task MarkEpisodesWatchedTillHereAsync(CatalogService.SeriesEpisodeOption episode)
        {
            var item = BuildLibraryMetaItem() ?? _currentItem;
            if (item == null)
                return;

            var watchedThroughEpisodes = _seriesEpisodes
                .Where(x => episode.Season <= 0
                    ? x.Season <= 0 && x.Episode <= episode.Episode
                    : x.Season > 0 && (x.Season < episode.Season || (x.Season == episode.Season && x.Episode <= episode.Episode)))
                .OrderBy(x => x.Season)
                .ThenBy(x => x.Episode)
                .ToList();

            SetLocalEpisodeWatchedState(watchedThroughEpisodes, true);
            await HistoryService.SetSeriesEpisodesWatchedAsync(item, watchedThroughEpisodes, true, markWholeSeries: false);
            await RefreshSeriesWatchUiAsync();
        }

        private async void SeasonOption_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not SeasonOption season)
                return;

            var seasonEpisodes = _seriesEpisodes.Where(x => x.Season == season.SeasonNumber).ToList();
            if (seasonEpisodes.Count == 0)
                return;

            var allWatched = seasonEpisodes.All(x => x.IsWatched);
            var flyout = new MenuFlyout();
            var toggleItem = new MenuFlyoutItem { Text = allWatched ? "Remove watched" : "Mark as watched", Tag = season };
            toggleItem.Click += async (_, __) =>
            {
                if (toggleItem.Tag is SeasonOption targetSeason)
                    await ToggleSeasonWatchedAsync(targetSeason);
            };
            flyout.Items.Add(toggleItem);
            ShowFlyoutAtPointer(flyout, element, e.GetPosition(element));
            e.Handled = true;
        }

        private async void EpisodeOption_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not CatalogService.SeriesEpisodeOption episode)
                return;

            var flyout = new MenuFlyout();
            var watchedItem = new MenuFlyoutItem { Text = episode.IsWatched ? "Remove watched" : "Mark as watched", Tag = episode };
            watchedItem.Click += async (_, __) =>
            {
                if (watchedItem.Tag is CatalogService.SeriesEpisodeOption targetEpisode)
                    await ToggleEpisodeWatchedAsync(targetEpisode);
            };
            var restItem = new MenuFlyoutItem { Text = "Watched till here", Tag = episode };
            restItem.Click += async (_, __) =>
            {
                if (restItem.Tag is CatalogService.SeriesEpisodeOption targetEpisode)
                    await MarkEpisodesWatchedTillHereAsync(targetEpisode);
            };
            flyout.Items.Add(watchedItem);
            flyout.Items.Add(restItem);
            ShowFlyoutAtPointer(flyout, element, e.GetPosition(element));
            e.Handled = true;
        }

        private async void FullEpisodeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is CatalogService.SeriesEpisodeOption episode)
                await LoadEpisodeAsync(episode);
        }

        private void BackToEpisodesButton_Click(object sender, RoutedEventArgs e)
        {
            ShowFullEpisodeList();
        }

        private async void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();

            await Task.CompletedTask;
        }

        private async void SeasonComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSeasonSelection)
                return;

            if (sender is not ComboBox comboBox || comboBox.SelectedItem is not SeasonOption season)
                return;

            _selectedSeasonNumber = season.SeasonNumber;
            _selectedEpisodeVideoId = string.Empty;
            await LoadEpisodesForSeasonAsync(season.SeasonNumber);
        }

        private async void EpisodeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEpisodeSelection)
                return;

            if (sender is not ComboBox comboBox || comboBox.SelectedItem is not CatalogService.SeriesEpisodeOption episode)
                return;

            await LoadEpisodeAsync(episode);
        }

        private void AddonFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressAddonFilterSelection)
                return;

            if (sender is ComboBox comboBox)
            {
                SelectedAddonFilter = comboBox.SelectedItem?.ToString() ?? "All";
                ApplyAddonFilterInternal();
            }
        }

        private async void StreamButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedStreamAsync(GetStreamFromSender(sender));
        }

        private async void StreamItemButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedStreamAsync(GetStreamFromSender(sender));
        }

        private async void StreamPlayButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedStreamAsync(GetStreamFromSender(sender));
        }

        private async void StreamCard_Click(object sender, RoutedEventArgs e)
        {
            await OpenSelectedStreamAsync(GetStreamFromSender(sender));
        }

        private void StreamButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;

            var stream = GetStreamFromSender(sender);
            if (stream == null)
                return;

            var urlToCopy = GetBestStreamUrl(stream);
            if (string.IsNullOrWhiteSpace(urlToCopy))
                return;

            var flyout = new MenuFlyout();
            var item = new MenuFlyoutItem
            {
                Text = "Copy stream URL",
                Tag = urlToCopy
            };
            item.Click += CopyStreamUrlMenuItem_Click;
            flyout.Items.Add(item);
            ShowFlyoutAtPointer(flyout, element, e.GetPosition(element));
            e.Handled = true;
        }

        private void CopyStreamUrlMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string url || string.IsNullOrWhiteSpace(url))
                return;

            var dataPackage = new DataPackage();
            dataPackage.SetText(url);
            Clipboard.SetContent(dataPackage);
        }

        private static string GetBestStreamUrl(CatalogService.StreamOption stream)
        {
            if (!string.IsNullOrWhiteSpace(stream.DirectUrl))
                return stream.DirectUrl;

            if (!string.IsNullOrWhiteSpace(stream.EmbeddedPageUrl))
                return stream.EmbeddedPageUrl;

            if (!string.IsNullOrWhiteSpace(stream.MagnetUrl))
                return stream.MagnetUrl;

            return string.Empty;
        }

        private void PosterImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowNextPosterCandidate();
        }

        private void PageBackgroundImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowNextBackgroundCandidate();
        }

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
                return false;

            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }

        public sealed class SeasonOption
        {
            public int SeasonNumber { get; set; }
            public string Label { get; set; } = string.Empty;
            public override string ToString() => Label;
        }
    }
}
