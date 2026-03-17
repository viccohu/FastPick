using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace FastPick
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 当前应用的主窗口实例
        /// </summary>
        public static Window Window { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Window ??= new Window();

            if (Window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                rootFrame.CacheSize = 10;
                Window.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(Views.MainPage), e.Arguments);
            
            // 监听窗口关闭事件
            Window.Closed += Window_Closed;
            
            // 设置窗口标题
            Window.Title = "FastPick";
            
            // 设置窗口最小尺寸（通过 AppWindow）
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow != null)
            {
                appWindow.Resize(new SizeInt32(1280, 720));
            }
            
            Window.Activate();
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new System.Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void Window_Closed(object sender, WindowEventArgs args)
        {
            if (Window.Content is Frame frame)
            {
                if (frame.Content is Views.MainPage mainPage)
                {
                    mainPage.SaveCurrentPaths();
                }
            }
        }
    }
}
