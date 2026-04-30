using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cleario.Models
{
    public sealed class Addon : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _manifestUrl = string.Empty;
        private bool _isEnabled = true;

        private bool _hasConfiguration;
        private string _configurationUrl = string.Empty;


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

        public string ManifestUrl
        {
            get => _manifestUrl;
            set
            {
                if (_manifestUrl == value) return;
                _manifestUrl = value;
                OnPropertyChanged();
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool HasConfiguration
        {
            get => _hasConfiguration;
            set
            {
                if (_hasConfiguration == value) return;
                _hasConfiguration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConfigurationVisibility));
            }
        }

        public string ConfigurationUrl
        {
            get => _configurationUrl;
            set
            {
                if (_configurationUrl == value) return;
                _configurationUrl = value;
                OnPropertyChanged();
            }
        }

        public Visibility ConfigurationVisibility => HasConfiguration ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}