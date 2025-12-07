using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace OverlayHud
{
    public partial class MainWindow : Window
    {
        // Display affinity constants
        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_MONITOR = 0x00000001;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // Keyboard hook constants
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_INSERT = 0x2D;
        private const int VK_DELETE = 0x2E;

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public int VkCode;
            public int ScanCode;
            public int Flags;
            public int Time;
            public IntPtr DwExtraInfo;
        }

        private LowLevelKeyboardProc? _keyboardCallback;
        private IntPtr _keyboardHookHandle = IntPtr.Zero;
        private bool _insertDown;
        private bool _deleteDown;
        private bool _isDeleting;
        private Slider? _opacitySlider;
        private CheckBox? _topMostCheckBox;
        private TextBlock? _statusText;
        private Border? _browserSurface;
        private Button? _browserReloadButton;
        private Button? _browserReopenButton;
        private Button? _browserForceReopenButton;
        private WebView2? _webView;
        private bool _startHidden = true;
        private const string DefaultBrowserUrl = "https://chatgpt.com/";
        private bool _webViewReady;
        private TextBlock? _proxyStatusText;
        private const string DefaultProxyHost = "84.55.7.37";
        private const int DefaultProxyPort = 5432;
        private const string DefaultProxyUser = "j3vun";
        private const string DefaultProxyPass = "uu12zs79";
        private const string DefaultBypassList = "localhost;127.0.0.1;<local>";
        private bool _proxyInUse;
        private string _userDataRoot = string.Empty;
        private string? _currentProfilePath;
        private bool _forceProxy;

        public MainWindow()
        {
            InitializeComponent();
            _opacitySlider = FindName("OpacitySlider") as Slider;
            _topMostCheckBox = FindName("TopMostToggle") as CheckBox;
            _statusText = FindName("StatusText") as TextBlock;
            _browserSurface = FindName("BrowserSurface") as Border;
            _browserReloadButton = FindName("BrowserReloadButton") as Button;
            _browserReopenButton = FindName("BrowserReopenButton") as Button;
            _browserForceReopenButton = FindName("BrowserForceReopenButton") as Button;
            _webView = FindName("WebView") as Microsoft.Web.WebView2.Wpf.WebView2;
            _proxyStatusText = FindName("ProxyStatusText") as TextBlock;

            // When the underlying HWND exists, we can apply display affinity
            this.SourceInitialized += MainWindow_SourceInitialized;
            this.Closing += MainWindow_Closing;

            this.Loaded += MainWindow_Loaded;
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

            SetupKeyboardHook();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateStatus("Initializing...");
                SetProxyStatus("Proxy: checking...");
                this.Opacity = _opacitySlider?.Value ?? 1.0;

                _userDataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OverlayHud",
                    "WebView2");
                Directory.CreateDirectory(_userDataRoot);

                if (_startHidden)
                {
                    this.Hide();
                    UpdateStatus("HUD hidden (Insert to show)");
                }

                await EnsureWebViewAsync();
            }
            catch (Exception ex)
            {
                UpdateStatus("Initialization failed");
                MessageBox.Show(
                    "Failed to initialize browser view.\n\n" + ex.Message,
                    "Browser Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            InitiateSelfDelete(force: true);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void TopMostToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool keepOnTop = (sender as CheckBox ?? _topMostCheckBox)?.IsChecked == true;
            this.Topmost = keepOnTop;
            UpdateStatus(keepOnTop ? "Pinned above other apps" : "Topmost disabled");
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded)
                return;

            double newOpacity = e.NewValue;
            this.Opacity = newOpacity;
            UpdateStatus($"Opacity {Math.Round(newOpacity * 100)}%");
            SetWebContentOpacity(newOpacity);
        }

        private void ShowHideButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleWindowVisibility();
        }

        private void BrowserReloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_webViewReady)
            {
                _ = EnsureWebViewAsync();
                return;
            }

            _webView?.CoreWebView2?.Reload();
            UpdateStatus(_proxyInUse ? "Reloaded via proxy" : "Reloaded direct");
        }

        private void BrowserReopenButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ReopenWebViewAsync();
        }

        private void BrowserForceReopenButton_Click(object sender, RoutedEventArgs e)
        {
            _forceProxy = true;
            _ = ReopenWebViewAsync(forceProxy: true);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            InitiateSelfDelete();
        }

        private void SetupKeyboardHook()
        {
            _keyboardCallback = KeyboardHookCallback;
            IntPtr moduleHandle = GetModuleHandle(null);
            _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardCallback, moduleHandle, 0);

            if (_keyboardHookHandle == IntPtr.Zero)
            {
                MessageBox.Show(
                    "Failed to register the global keyboard hook. Hotkeys will not work.",
                    "Hotkey Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookData = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                bool isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                bool isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

                if (isKeyDown)
                {
                    if (hookData.VkCode == VK_INSERT && !_insertDown)
                    {
                        _insertDown = true;

                        if (_deleteDown)
                        {
                            Dispatcher.Invoke(() => InitiateSelfDelete(force: true));
                        }
                        else
                        {
                            Dispatcher.Invoke(ToggleWindowVisibility);
                        }
                    }
                    else if (hookData.VkCode == VK_DELETE && !_deleteDown)
                    {
                        _deleteDown = true;

                        if (_insertDown)
                        {
                            Dispatcher.Invoke(() => InitiateSelfDelete(force: true));
                        }
                    }
                }
                else if (isKeyUp)
                {
                    if (hookData.VkCode == VK_INSERT)
                    {
                        _insertDown = false;
                    }
                    else if (hookData.VkCode == VK_DELETE)
                    {
                        _deleteDown = false;
                    }
                }
            }

            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        private void ToggleWindowVisibility()
        {
            bool isCurrentlyVisible = this.Visibility == Visibility.Visible && this.WindowState != WindowState.Minimized;

            if (isCurrentlyVisible)
            {
                this.Hide();
                UpdateStatus("HUD hidden");
            }
            else
            {
                if (this.WindowState == WindowState.Minimized)
                {
                    this.WindowState = WindowState.Normal;
                }

                this.Show();
                this.Activate();
                UpdateStatus("HUD visible");
            }
        }

        private void InitiateSelfDelete(bool force = false)
        {
            if (_isDeleting)
                return;

            _isDeleting = true;

            if (!force)
            {
                var result = MessageBox.Show(
                    "Delete OverlayHud from this computer? This closes the app and removes its folder.",
                    "Confirm Deletion",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    _isDeleting = false;
                    return;
                }
            }

            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                _isDeleting = false;
                MessageBox.Show(
                    "Unable to determine the application location. Delete manually.",
                    "Deletion Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            string? appFolder = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(appFolder))
            {
                _isDeleting = false;
                MessageBox.Show(
                    "Unable to resolve the install folder. Delete manually.",
                    "Deletion Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            string batPath = Path.Combine(Path.GetTempPath(), $"OverlayHud_Delete_{Guid.NewGuid():N}.bat");

            string script = $"""
@echo off
timeout /t 1 /nobreak > nul
rmdir /s /q "{appFolder}"
del "{exePath}"
del "%~f0"
""";

            File.WriteAllText(batPath, script);

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = batPath,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                _isDeleting = false;
                MessageBox.Show(
                    "Failed to schedule deletion:\n\n" + ex.Message,
                    "Deletion Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            UpdateStatus("Removing OverlayHud...");
            Application.Current.Shutdown();
        }

        private void UpdateStatus(string message)
        {
            if (_statusText == null)
                return;

            _statusText.Text = message;
        }

        private void SetProxyStatus(string message)
        {
            if (_proxyStatusText == null)
                return;

            _proxyStatusText.Text = message;
        }

        private async Task EnsureWebViewAsync(bool freshProfile = false, bool forceProxy = false)
        {
            if (_webViewReady && !freshProfile)
            {
                return;
            }
            if (_webView == null)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(_userDataRoot))
                {
                    _userDataRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "OverlayHud",
                        "WebView2");
                    Directory.CreateDirectory(_userDataRoot);
                }

                if (freshProfile || string.IsNullOrWhiteSpace(_currentProfilePath))
                {
                    _currentProfilePath = Path.Combine(_userDataRoot, $"Profile_{Guid.NewGuid():N}");
                    Directory.CreateDirectory(_currentProfilePath);
                    ResetWebViewControl();
                }

                CoreWebView2EnvironmentOptions? envOptions = null;

                var proxy = Environment.GetEnvironmentVariable("MASK_PROXY");
                var proxyBypass = Environment.GetEnvironmentVariable("MASK_PROXY_BYPASS");

                // If forced, always use the default hardcoded proxy.
                if (forceProxy || _forceProxy)
                {
                    proxy = $"http://{DefaultProxyUser}:{DefaultProxyPass}@{DefaultProxyHost}:{DefaultProxyPort}";
                }
                else if (string.IsNullOrWhiteSpace(proxy))
                {
                    proxy = $"http://{DefaultProxyUser}:{DefaultProxyPass}@{DefaultProxyHost}:{DefaultProxyPort}";
                }

                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    var sanitizedProxy = SanitizeProxy(proxy);
                    var bypass = string.IsNullOrWhiteSpace(proxyBypass) ? DefaultBypassList : proxyBypass;

                    // Map both http/https to the same proxy to satisfy Chromium's supported schemes
                    var args = $"--proxy-server=http={sanitizedProxy};https={sanitizedProxy} --proxy-bypass-list={bypass}";
                    if (!string.IsNullOrWhiteSpace(proxyBypass))
                    {
                        // already appended above; kept for clarity
                    }
                    envOptions = new CoreWebView2EnvironmentOptions(args);
                    SetProxyStatus($"Proxy on: {sanitizedProxy}");
                    _proxyInUse = true;
                }
                else
                {
                    SetProxyStatus("Proxy: none (direct)");
                    _proxyInUse = false;
                }

                var customUA = Environment.GetEnvironmentVariable("MASK_USER_AGENT");

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _currentProfilePath,
                    options: envOptions);

                await _webView.EnsureCoreWebView2Async(env);
                var core = _webView.CoreWebView2;

                if (core != null)
                {
                    if (!string.IsNullOrWhiteSpace(customUA))
                    {
                        core.Settings.UserAgent = customUA;
                    }

                    core.NavigationCompleted -= WebView_NavigationCompleted;
                    core.NavigationCompleted += WebView_NavigationCompleted;
                    core.Navigate(DefaultBrowserUrl);
                }

                _webViewReady = true;
                UpdateStatus(_proxyInUse ? "Browser ready (proxy)" : "Browser ready (direct)");
            }
            catch (Exception ex)
            {
                UpdateStatus("Browser failed");
                SetProxyStatus("Proxy: error");
                MessageBox.Show(
                    "Failed to initialize browser view.\n\n" + ex.Message,
                    "Browser Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string SanitizeProxy(string raw)
        {
            var trimmed = raw.Trim();
            if (trimmed.EndsWith("/"))
            {
                trimmed = trimmed.TrimEnd('/');
            }
            return trimmed;
        }

        private async Task ReopenWebViewAsync(bool forceProxy = false)
        {
            _webViewReady = false;
            _currentProfilePath = Path.Combine(_userDataRoot, $"Profile_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_currentProfilePath);
            await EnsureWebViewAsync(freshProfile: true, forceProxy: forceProxy || _forceProxy);
            _forceProxy = false;
            UpdateStatus(_proxyInUse ? "Reopened via proxy" : "Reopened direct");
        }

        private void ResetWebViewControl()
        {
            if (_webView == null)
            {
                return;
            }

            var parentGrid = _webView.Parent as Grid;
            if (parentGrid == null)
            {
                return;
            }

            parentGrid.Children.Remove(_webView);
            _webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                DefaultBackgroundColor = Color.Transparent
            };
            parentGrid.Children.Add(_webView);
        }

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                UpdateStatus(_proxyInUse ? "Connected via proxy" : "Connected direct");
                SetWebContentOpacity(this.Opacity);
            }
            else
            {
                UpdateStatus($"Navigation error: {e.WebErrorStatus}");
            }
        }

        private async void SetWebContentOpacity(double opacity)
        {
            if (_webView?.CoreWebView2 == null)
            {
                return;
            }

            var clamped = Math.Max(0.0, Math.Min(1.0, opacity));
            string opStr = clamped.ToString("0.##", CultureInfo.InvariantCulture);

            string script = $@"
(function() {{
  const html = document.documentElement;
  const body = document.body || document.createElement('body');
  html.style.opacity = '{opStr}';
  body.style.opacity = '{opStr}';
  html.style.background = 'transparent';
  body.style.background = 'transparent';
}})();";

            try
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch
            {
                // ignore script errors (e.g., before page load)
            }
        }

    }
}