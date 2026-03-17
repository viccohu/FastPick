using FastPick.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FastPick.Views
{
    public sealed partial class SettingsPage : Page
    {
        private SettingsService _settingsService => SettingsService.Instance;
        private bool _isLoading = false;

        public SettingsPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadSettings();
        }

        private void LoadSettings()
        {
            _isLoading = true;
            RawHighResDecodeToggle.IsOn = _settingsService.EnableRawHighResDecode;
            AutoLoadLastPathToggle.IsOn = _settingsService.AutoLoadLastPath;
            JpgFolderNameTextBox.Text = _settingsService.JpgFolderName;
            RawFolderNameTextBox.Text = _settingsService.RawFolderName;
            DeleteToRecycleBinToggle.IsOn = _settingsService.DeleteToRecycleBin;
            _isLoading = false;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.Frame.CanGoBack)
            {
                this.Frame.GoBack();
            }
        }

        private void RawHighResDecodeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoading)
                _settingsService.EnableRawHighResDecode = RawHighResDecodeToggle.IsOn;
        }

        private void AutoLoadLastPathToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoading)
                _settingsService.AutoLoadLastPath = AutoLoadLastPathToggle.IsOn;
        }

        private void JpgFolderNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isLoading)
                _settingsService.JpgFolderName = JpgFolderNameTextBox.Text;
        }

        private void RawFolderNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isLoading)
                _settingsService.RawFolderName = RawFolderNameTextBox.Text;
        }

        private void DeleteToRecycleBinToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoading)
                _settingsService.DeleteToRecycleBin = DeleteToRecycleBinToggle.IsOn;
        }
    }
}
