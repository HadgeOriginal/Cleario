using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Cleario.Models
{
    public sealed class MetaItem : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _name = string.Empty;
        private string _type = string.Empty;
        private string _poster = string.Empty;
        private string _posterUrl = string.Empty;
        private string _fallbackPosterUrl = string.Empty;
        private string _year = string.Empty;
        private string _imdbRating = string.Empty;
        private bool _isPosterLoading = true;
        private string _sourceBaseUrl = string.Empty;
        private bool _isWatched;

        private List<string> _posterCandidates = new();
        private int _posterCandidateIndex = -1;

        public string Id
        {
            get => _id;
            set
            {
                if (_id == value) return;
                _id = value;
                OnPropertyChanged();
            }
        }

        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                OnPropertyChanged();
            }
        }

        public string Type
        {
            get => _type;
            set
            {
                if (_type == value) return;
                _type = value;
                OnPropertyChanged();
            }
        }

        public string Poster
        {
            get => _poster;
            set
            {
                if (_poster == value) return;
                _poster = value;
                OnPropertyChanged();
            }
        }

        public string PosterUrl
        {
            get => _posterUrl;
            set
            {
                if (_posterUrl == value) return;
                _posterUrl = value;
                OnPropertyChanged();
            }
        }

        public string FallbackPosterUrl
        {
            get => _fallbackPosterUrl;
            set
            {
                if (_fallbackPosterUrl == value) return;
                _fallbackPosterUrl = value;
                OnPropertyChanged();
            }
        }

        public string Year
        {
            get => _year;
            set
            {
                if (_year == value) return;
                _year = value;
                OnPropertyChanged();
            }
        }

        public string ImdbRating
        {
            get => _imdbRating;
            set
            {
                if (_imdbRating == value) return;
                _imdbRating = value;
                OnPropertyChanged();
            }
        }
        public string SourceBaseUrl
        {
            get => _sourceBaseUrl;
            set
            {
                if (_sourceBaseUrl == value) return;
                _sourceBaseUrl = value;
                OnPropertyChanged();
            }
        }


        public bool IsWatched
        {
            get => _isWatched;
            set
            {
                if (_isWatched == value) return;
                _isWatched = value;
                OnPropertyChanged();
            }
        }

        public bool IsPosterLoading
        {
            get => _isPosterLoading;
            set
            {
                if (_isPosterLoading == value) return;
                _isPosterLoading = value;
                OnPropertyChanged();
            }
        }

        public void SetPosterCandidates(IEnumerable<string> candidates, string placeholderPoster)
        {
            _posterCandidates = candidates
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            if (_posterCandidates.Count == 0)
                _posterCandidates.Add(placeholderPoster);

            _posterCandidateIndex = 0;
            IsPosterLoading = true;
            Poster = _posterCandidates[0];
        }

        public bool MoveToNextPosterCandidate()
        {
            if (_posterCandidates.Count == 0)
                return false;

            var nextIndex = _posterCandidateIndex + 1;
            if (nextIndex >= _posterCandidates.Count)
                return false;

            _posterCandidateIndex = nextIndex;
            Poster = _posterCandidates[_posterCandidateIndex];
            return true;
        }

        public void SetPlaceholderPoster(string placeholderPoster)
        {
            IsPosterLoading = false;
            Poster = placeholderPoster;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}