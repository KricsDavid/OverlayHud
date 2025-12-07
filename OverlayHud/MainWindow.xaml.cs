using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

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

        public MainWindow()
        {
            InitializeComponent();

            // When the underlying HWND exists, we can apply display affinity
            this.SourceInitialized += MainWindow_SourceInitialized;
            this.Closing += MainWindow_Closing;

            // Initialize WebView2 after the window is loaded
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
                UpdateStatus("Loading ChatGPT...");
                this.Opacity = OpacitySlider.Value;

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
                WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            }
            catch (Exception ex)
            {
                UpdateStatus("WebView2 failed");
                MessageBox.Show(
                    "Failed to initialize WebView2.\n\n" + ex.Message,
                    "WebView2 Error",
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
            this.Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void TopMostToggle_Changed(object sender, RoutedEventArgs e)
        {
            this.Topmost = TopMostToggle.IsChecked == true;
            UpdateStatus(this.Topmost ? "Pinned above other apps" : "Topmost disabled");
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded)
                return;

            this.Opacity = e.NewValue;
            UpdateStatus($"Opacity {Math.Round(e.NewValue * 100)}%");
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (WebView.CoreWebView2 == null)
                {
                    await WebView.EnsureCoreWebView2Async();
                }

                WebView.CoreWebView2.Reload();
                UpdateStatus("ChatGPT refreshed");
            }
            catch (Exception ex)
            {
                UpdateStatus("Refresh failed");
                MessageBox.Show(
                    "Unable to refresh ChatGPT.\n\n" + ex.Message,
                    "Refresh Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ShowHideButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleWindowVisibility();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            InitiateSelfDelete();
        }

        private void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                UpdateStatus("ChatGPT ready");
            }
            else
            {
                UpdateStatus($"Navigation error: {args.WebErrorStatus}");
            }
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
                            Dispatcher.Invoke(InitiateSelfDelete);
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
                            Dispatcher.Invoke(InitiateSelfDelete);
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

        private void InitiateSelfDelete()
        {
            if (_isDeleting)
                return;

            _isDeleting = true;

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

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!appFolder.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
            {
                _isDeleting = false;
                MessageBox.Show(
                    "OverlayHud lives outside your user profile. To avoid needing administrator rights, delete it manually from its install folder.",
                    "Deletion Requires Manual Step",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
            if (StatusText == null)
                return;

            StatusText.Text = message;
        }
    }
}