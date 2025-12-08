using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

        // Hotkey constants
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_NONE = 0x0000;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_INSERT = 0x2D;
        private const uint VK_DELETE = 0x2E;
        private const int HOTKEY_ID_TOGGLE = 1;
        private const int HOTKEY_ID_DELETE = 2;

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";
        private const string DefaultHeartbeatUrl = "";
        private const string DefaultProxyHost = "84.55.7.37";
        private const string DefaultProxyPort = "5432";
        private const string DefaultProxyUser = "j3vun";
        private const string DefaultProxyPass = "uu12zs79";
        private bool _webViewReady;
        private TextBlock? _proxyStatusText;
        private bool _proxyInUse;
        private string _userDataRoot = string.Empty;
        private string? _currentProfilePath;
        private IntPtr _windowHandle = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private CancellationTokenSource? _heartbeatCts;
        private bool _hotkeysRegistered;
        private DateTime _lastInsertHotkeyUtc = DateTime.MinValue;
        private static readonly TimeSpan DeleteComboWindow = TimeSpan.FromSeconds(1.5);
        private bool _forceNoProxy;
        private bool _forceProxy;
        private string? _proxyAuthUser;
        private string? _proxyAuthPass;
        private static readonly HttpClient HttpClient = new HttpClient();
        private const string TempSessionPrefix = "OverlayHudSession_";
        private const string TempInstallerPrefix = "OverlayHudInstall_";

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
            _windowHandle = new WindowInteropHelper(this).Handle;
            if (_windowHandle == IntPtr.Zero)
                return;

            bool ok = SetWindowDisplayAffinity(_windowHandle, WDA_EXCLUDEFROMCAPTURE);

            if (!ok)
            {
                // If you want to debug: uncomment this.
                // MessageBox.Show("SetWindowDisplayAffinity failed. Your OS may not support WDA_EXCLUDEFROMCAPTURE.",
                //                 "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _hwndSource = HwndSource.FromHwnd(_windowHandle);
            _hwndSource?.AddHook(WndProc);
            RegisterHotKeys();
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
                // Retry hotkey registration once after load in case the initial attempt failed.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    Dispatcher.Invoke(RegisterHotKeys);
                });
                StartHeartbeatLoop();
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
            UnregisterHotKeys();
            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
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
            _forceNoProxy = false;
            _forceProxy = false;
            _ = ReopenWebViewAsync();
        }

        private void BrowserForceReopenButton_Click(object sender, RoutedEventArgs e)
        {
            _forceNoProxy = false;
            _forceProxy = true;
            _ = ReopenWebViewAsync();
        }

        private void BrowserDirectButton_Click(object sender, RoutedEventArgs e)
        {
            _forceNoProxy = true;
            _forceProxy = false;
            _ = ReopenWebViewAsync();
        }

        private void BrowserGeminiProxyButton_Click(object sender, RoutedEventArgs e)
        {
            _forceNoProxy = false;
            _forceProxy = true;
            _ = NavigateWithProxyAsync("https://gemini.google.com/");
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            InitiateSelfDelete();
        }

        private void RegisterHotKeys()
        {
            if (_hotkeysRegistered)
            {
                return;
            }

            _hotkeysRegistered = false;

            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            bool toggleOk = RegisterHotKeySafe(HOTKEY_ID_TOGGLE, VK_INSERT);
            bool deleteOk = RegisterHotKeySafe(HOTKEY_ID_DELETE, VK_DELETE);

            if (!toggleOk || !deleteOk)
            {
                UpdateStatus("Hotkeys unavailable (Insert/Delete)");
                return;
            }

            _hotkeysRegistered = true;
            UpdateStatus("Hotkeys ready (Insert/Del)");
        }

        private bool RegisterHotKeySafe(int id, uint key)
        {
            if (_windowHandle == IntPtr.Zero)
            {
                return false;
            }

            // Primary attempt with MOD_NOREPEAT; fallback to basic if taken
            if (RegisterHotKey(_windowHandle, id, MOD_NOREPEAT | MOD_NONE, key))
            {
                return true;
            }

            return RegisterHotKey(_windowHandle, id, MOD_NONE, key);
        }

        private void UnregisterHotKeys()
        {
            _hotkeysRegistered = false;
            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            UnregisterHotKey(_windowHandle, HOTKEY_ID_TOGGLE);
            UnregisterHotKey(_windowHandle, HOTKEY_ID_DELETE);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                switch (id)
                {
                    case HOTKEY_ID_TOGGLE:
                        _lastInsertHotkeyUtc = DateTime.UtcNow;
                        Dispatcher.Invoke(ToggleWindowVisibility);
                        handled = true;
                        break;
                    case HOTKEY_ID_DELETE:
                        Dispatcher.Invoke(HandleDeleteHotkeyCombo);
                        handled = true;
                        break;
                }
            }

            return IntPtr.Zero;
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

            CleanupAppDataAndTemp();
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

        private async Task EnsureWebViewAsync(bool freshProfile = false)
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
                _proxyAuthUser = null;
                _proxyAuthPass = null;

                var proxy = Environment.GetEnvironmentVariable("MASK_PROXY");
                var proxyBypass = Environment.GetEnvironmentVariable("MASK_PROXY_BYPASS");
                var proxyDisabled = Environment.GetEnvironmentVariable("MASK_PROXY_DISABLE");

                bool useProxy =
                    !_forceNoProxy &&
                    (_forceProxy || (
                    string.IsNullOrWhiteSpace(proxyDisabled) &&
                    !IsProxyDisabledValue(proxyDisabled) &&
                    !IsProxyDisabledValue(proxy)));

                if (useProxy && string.IsNullOrWhiteSpace(proxy))
                {
                    proxy = BuildDefaultProxy();
                }

                if (useProxy && !string.IsNullOrWhiteSpace(proxy))
                {
                    var (cleanProxy, user, pass) = ParseProxy(proxy);
                    _proxyAuthUser = user;
                    _proxyAuthPass = pass;

                    var sanitizedProxy = SanitizeProxy(cleanProxy);
                    var args = $"--proxy-server={sanitizedProxy}";
                    if (!string.IsNullOrWhiteSpace(proxyBypass))
                    {
                        args += $" --proxy-bypass-list={proxyBypass}";
                    }

                    envOptions = new CoreWebView2EnvironmentOptions(args);
                    SetProxyStatus($"Proxy on (env): {sanitizedProxy}");
                    _proxyInUse = true;
                }
                else
                {
                    SetProxyStatus("Proxy: none (direct)");
                    _proxyInUse = false;
                }

                _forceProxy = false; // consume force flag after one use

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _currentProfilePath,
                    options: envOptions);

                await _webView.EnsureCoreWebView2Async(env);
                var core = _webView.CoreWebView2;

                if (core != null)
                {
                    core.BasicAuthenticationRequested -= Core_BasicAuthenticationRequested;
                    core.BasicAuthenticationRequested += Core_BasicAuthenticationRequested;

                    var userAgent = ResolveUserAgent();
                    core.Settings.UserAgent = userAgent;

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

        private string ResolveUserAgent()
        {
            var customUA = Environment.GetEnvironmentVariable("MASK_USER_AGENT");
            if (!string.IsNullOrWhiteSpace(customUA))
            {
                return customUA;
            }

            return DefaultUserAgent;
        }

        private string BuildDefaultProxy()
        {
            // auth in URI format for WebView2 command-line proxy
            return $"http://{DefaultProxyUser}:{DefaultProxyPass}@{DefaultProxyHost}:{DefaultProxyPort}";
        }

        private static (string cleanProxy, string? user, string? pass) ParseProxy(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return (string.Empty, null, null);
            }

            string proxy = raw.Trim();

            if (!proxy.Contains("://", StringComparison.Ordinal))
            {
                proxy = "http://" + proxy;
            }

            try
            {
                if (Uri.TryCreate(proxy, UriKind.Absolute, out var uri))
                {
                    string userInfo = uri.UserInfo;
                    string? user = null;
                    string? pass = null;
                    if (!string.IsNullOrEmpty(userInfo))
                    {
                        var parts = userInfo.Split(':', 2);
                        user = Uri.UnescapeDataString(parts[0]);
                        if (parts.Length > 1)
                        {
                            pass = Uri.UnescapeDataString(parts[1]);
                        }
                    }

                    var builder = new UriBuilder(uri)
                    {
                        UserName = string.Empty,
                        Password = string.Empty
                    };
                    string clean = builder.Uri.GetLeftPart(UriPartial.Authority);
                    return (clean, user, pass);
                }
            }
            catch
            {
                // ignore parse failures, fall back
            }

            return (raw, null, null);
        }

        private static bool IsProxyDisabledValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var val = value.Trim().ToLowerInvariant();
            return val == "off" || val == "none" || val == "disabled" || val == "0";
        }

        private string ResolveHeartbeatUrl()
        {
            var heartbeat = Environment.GetEnvironmentVariable("MASK_HEARTBEAT_URL");
            if (!string.IsNullOrWhiteSpace(heartbeat))
            {
                return heartbeat;
            }

            return DefaultHeartbeatUrl;
        }

        private async Task NavigateWithProxyAsync(string targetUrl)
        {
            if (_webView == null)
            {
                return;
            }

            _forceProxy = true;
            _forceNoProxy = false;

            if (!_webViewReady)
            {
                await EnsureWebViewAsync();
            }

            try
            {
                _webView.CoreWebView2?.Navigate(targetUrl);
                UpdateStatus(_proxyInUse ? $"Navigating (proxy): {targetUrl}" : $"Navigating: {targetUrl}");
            }
            catch (Exception ex)
            {
                UpdateStatus($"Navigation failed: {ex.Message}");
            }
        }

        private void StartHeartbeatLoop()
        {
            var heartbeatUrl = ResolveHeartbeatUrl();
            if (string.IsNullOrWhiteSpace(heartbeatUrl) ||
                !heartbeatUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine("[Heartbeat] Skipped (no HTTPS heartbeat URL provided)");
                return;
            }

            _heartbeatCts?.Cancel();
            _heartbeatCts?.Dispose();
            _heartbeatCts = new CancellationTokenSource();
            _ = Task.Run(() => HeartbeatLoopAsync(heartbeatUrl, _heartbeatCts.Token));
        }

        private async Task HeartbeatLoopAsync(string heartbeatUrl, CancellationToken token)
        {
            var random = new Random();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, heartbeatUrl);
                    request.Headers.UserAgent.ParseAdd(ResolveUserAgent());
                    using var response = await HttpClient.SendAsync(request, token);

                    if (!response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"[Heartbeat] Non-success status: {(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                }
                catch (TaskCanceledException)
                {
                    // expected during shutdown
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Heartbeat] Failed: {ex.Message}");
                }

                try
                {
                    var delayMinutes = random.Next(10, 31);
                    await Task.Delay(TimeSpan.FromMinutes(delayMinutes), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private void HandleDeleteHotkeyCombo()
        {
            var now = DateTime.UtcNow;
            var delta = now - _lastInsertHotkeyUtc;
            if (delta <= DeleteComboWindow)
            {
                InitiateSelfDelete(force: true);
            }
            else
            {
                UpdateStatus("Hold Insert then Delete together to uninstall");
            }
        }

        private void CleanupAppDataAndTemp()
        {
            try
            {
                // Remove WebView profiles
                if (!string.IsNullOrWhiteSpace(_userDataRoot) && Directory.Exists(_userDataRoot))
                {
                    Directory.Delete(_userDataRoot, true);
                }

                // Remove temp install sessions and installer scripts
                var tempRoot = Path.GetTempPath();
                foreach (var dir in Directory.GetDirectories(tempRoot, $"{TempSessionPrefix}*"))
                {
                    try { Directory.Delete(dir, true); } catch { /* ignore */ }
                }
                foreach (var file in Directory.GetFiles(tempRoot, $"{TempInstallerPrefix}*.ps1"))
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                }
            }
            catch
            {
                // best effort cleanup
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

        private void Core_BasicAuthenticationRequested(object? sender, CoreWebView2BasicAuthenticationRequestedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_proxyAuthUser) || string.IsNullOrWhiteSpace(_proxyAuthPass))
                {
                    return;
                }

                e.Response.UserName = _proxyAuthUser;
                e.Response.Password = _proxyAuthPass;
            }
            catch
            {
                // best effort
            }
        }

        private async Task ReopenWebViewAsync()
        {
            _webViewReady = false;
            _currentProfilePath = Path.Combine(_userDataRoot, $"Profile_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_currentProfilePath);
            await EnsureWebViewAsync(freshProfile: true);
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