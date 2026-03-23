using FastPick.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.IO;
using Windows.System;

namespace FastPick.Views
{
    public sealed partial class SettingsPage : Page
    {
        private SettingsService _settingsService => SettingsService.Instance;
        private bool _isLoading = false;
        private ThumbnailService _thumbnailService = new ThumbnailService();

        public SettingsPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadSettings();
            _ = LoadCacheSizeAsync();
        }

        private async System.Threading.Tasks.Task LoadCacheSizeAsync()
        {
            try
            {
                var sizeBytes = await _thumbnailService.GetLocalCacheSizeAsync();
                var sizeMB = sizeBytes / (1024.0 * 1024.0);
                CacheSizeTextBlock.Text = $"缓存大小: {sizeMB:F2} MB";
            }
            catch (Exception ex)
            {
                CacheSizeTextBlock.Text = $"无法获取缓存大小: {ex.Message}";
            }
        }

        private void LoadSettings()
        {
            _isLoading = true;
            RawHighResDecodeToggle.IsOn = _settingsService.EnableRawHighResDecode;
            UseRawForHighResDecodeToggle.IsOn = _settingsService.UseRawForHighResDecode;
            AutoLoadLastPathToggle.IsOn = _settingsService.AutoLoadLastPath;
            BackgroundThumbnailDecodingToggle.IsOn = _settingsService.EnableBackgroundThumbnailDecoding;
            JpgFolderNameTextBox.Text = _settingsService.JpgFolderName;
            RawFolderNameTextBox.Text = _settingsService.RawFolderName;
            DeleteToRecycleBinToggle.IsOn = _settingsService.DeleteToRecycleBin;
            
            PreviewLoadModeComboBox.SelectedIndex = _settingsService.PreviewLoadMode == Services.PreviewLoadMode.Hierarchical ? 1 : 0;
            
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

        private void UseRawForHighResDecodeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoading)
                _settingsService.UseRawForHighResDecode = UseRawForHighResDecodeToggle.IsOn;
        }

        private void PreviewLoadModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoading && PreviewLoadModeComboBox.SelectedIndex >= 0)
            {
                _settingsService.PreviewLoadMode = PreviewLoadModeComboBox.SelectedIndex == 1 
                    ? Services.PreviewLoadMode.Hierarchical 
                    : Services.PreviewLoadMode.OnDemand;
            }
        }

        private void BackgroundThumbnailDecodingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isLoading)
                _settingsService.EnableBackgroundThumbnailDecoding = BackgroundThumbnailDecodingToggle.IsOn;
        }

        private async void OpenCacheFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cachePath = await _thumbnailService.GetCachePathAsync();
                if (!string.IsNullOrEmpty(cachePath) && Directory.Exists(cachePath))
                {
                    await Launcher.LaunchFolderPathAsync(cachePath);
                }
                else
                {
                    var dialog = new ContentDialog
                    {
                        Title = "提示",
                        Content = "缓存目录不存在或尚未创建。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "错误",
                    Content = $"无法打开缓存目录: {ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            var confirmDialog = new ContentDialog
            {
                Title = "确认清除缓存",
                Content = "确定要清除所有缩略图缓存吗？\n这将删除所有已缓存的缩略图，下次加载时需要重新生成。",
                PrimaryButtonText = "清除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                try
                {
                    ClearCacheButton.IsEnabled = false;
                    ClearCacheButton.Content = "清除中...";
                    
                    await _thumbnailService.ClearLocalCacheAsync();
                    
                    // 刷新缓存大小显示
                    await LoadCacheSizeAsync();
                    
                    var successDialog = new ContentDialog
                    {
                        Title = "成功",
                        Content = "缓存已清除。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await successDialog.ShowAsync();
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "错误",
                        Content = $"清除缓存失败: {ex.Message}",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
                finally
                {
                    ClearCacheButton.IsEnabled = true;
                    ClearCacheButton.Content = "清除缓存";
                }
            }
        }
    }
}
