using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace OverlayHud
{
    public partial class MainWindow : Window
    {
        // Display affinity constants
        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_MONITOR = 0x00000001;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        public MainWindow()
        {
            InitializeComponent();

            // When the underlying HWND exists, we can apply display affinity
            this.SourceInitialized += MainWindow_SourceInitialized;

            // Initialize WebView2 after the window is loaded
            this.Loaded += MainWindow_Loaded;

            // Allow dragging the window by holding left mouse anywhere on background
            this.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            bool ok = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            if (!ok)
            {
                // If you want to debug: uncomment this.
                // MessageBox.Show("SetWindowDisplayAffinity failed. Your OS may not support WDA_EXCLUDEFROMCAPTURE.",
                //                 "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // A dedicated user-data folder so your login/session persists
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OverlayHud",
                    "WebView2");

                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder);

                await WebView.EnsureCoreWebView2Async(env);

                // If you want to switch to Gemini, change URL here
                const string defaultUrl = "https://chatgpt.com/";
                WebView.CoreWebView2.Navigate(defaultUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to initialize WebView2.\n\n" + ex.Message,
                    "WebView2 Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}