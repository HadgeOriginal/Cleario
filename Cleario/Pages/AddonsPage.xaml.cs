using Cleario.Models;
using Cleario.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;

namespace Cleario.Pages
{
    public sealed partial class AddonsPage : Page
    {
        private readonly DispatcherTimer _copyToastTimer = new();

        public AddonsPage()
        {
            InitializeComponent();
            _copyToastTimer.Interval = TimeSpan.FromSeconds(2.2);
            _copyToastTimer.Tick += CopyToastTimer_Tick;
            Loaded += AddonsPage_Loaded;
        }

        private async void AddonsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await AddonManager.InitializeAsync();
            AddonsListView.ItemsSource = AddonManager.Addons;
        }

        private async void AddAddonButton_Click(object sender, RoutedEventArgs e)
        {
            var url = ManifestUrlTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(url))
                return;

            var added = await AddonManager.AddAddonAsync(url);
            if (added)
                ManifestUrlTextBox.Text = string.Empty;
        }

        private async void AddonToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleSwitch toggle || toggle.Tag is not Addon addon)
                return;

            await AddonManager.SetAddonEnabledAsync(addon, toggle.IsOn);
        }


        private async void ConfigureButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Addon addon || string.IsNullOrWhiteSpace(addon.ConfigurationUrl))
                return;

            if (Uri.TryCreate(addon.ConfigurationUrl, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
        }

        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Addon addon || string.IsNullOrWhiteSpace(addon.ManifestUrl))
                return;

            var package = new DataPackage();
            package.SetText(addon.ManifestUrl);
            Clipboard.SetContent(package);
            ShowCopyToast("Addon URL copied");
        }

        private void ShowCopyToast(string message)
        {
            CopyToastTextBlock.Text = string.IsNullOrWhiteSpace(message) ? "Copied" : message;
            CopyToastBorder.Visibility = Visibility.Visible;
            _copyToastTimer.Stop();
            _copyToastTimer.Start();
        }

        private void CopyToastTimer_Tick(object? sender, object e)
        {
            _copyToastTimer.Stop();
            CopyToastBorder.Visibility = Visibility.Collapsed;
        }

        private async void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Addon addon)
                return;

            await AddonManager.RemoveAddonAsync(addon);
        }

        private async void MoveUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Addon addon)
                return;

            await AddonManager.MoveAddonUpAsync(addon);
        }

        private async void MoveDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not Addon addon)
                return;

            await AddonManager.MoveAddonDownAsync(addon);
        }

        private async void AddonsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
        {
            await AddonManager.SaveReorderedAddonsAsync();
        }
    }
}