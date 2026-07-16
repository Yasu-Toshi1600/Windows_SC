using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace Windows_SC;

public sealed partial class MainWindow : Window
{
    private const int HotKeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeySpace = 0x20;
    private const uint WmHotKey = 0x0312;
    private const uint WmKeyDown = 0x0100;
    private const int VirtualKeyEscape = 0x1B;
    private const int GwlpWndProc = -4;

    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly WindowProcedure _windowProcedure;
    private readonly DiagnosticLogger _logger;
    private readonly StartMenuWindowInspector _startMenuWindowInspector;
    private readonly UiAutomationStartMenuInspector _uiAutomationStartMenuInspector;
    private readonly GlobalWindowsKeyMonitor _windowsKeyMonitor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _windowsKeyTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _visibilityTimer;
    private IntPtr _previousWindowProcedure;
    private bool _isVisible;
    private bool _isInitialized;
    private bool _launcherIsActivated;
    private DateTimeOffset _automaticHideAllowedAt;
    private bool? _lastLoggedLauncherFocus;
    private bool? _lastLoggedStartMenuVisibility;

    public MainWindow()
    {
        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _windowProcedure = WindowMessageHandler;
        _logger = new DiagnosticLogger();
        _startMenuWindowInspector = new StartMenuWindowInspector(_logger);
        _uiAutomationStartMenuInspector = new UiAutomationStartMenuInspector(_logger);
        _windowsKeyMonitor = new GlobalWindowsKeyMonitor(OnWindowsKeyReleasedAlone, _logger.Write);
        _windowsKeyTimer = DispatcherQueue.CreateTimer();
        _windowsKeyTimer.Interval = TimeSpan.FromMilliseconds(180);
        _windowsKeyTimer.IsRepeating = false;
        _windowsKeyTimer.Tick += WindowsKeyTimer_Tick;
        _visibilityTimer = DispatcherQueue.CreateTimer();
        _visibilityTimer.Interval = TimeSpan.FromMilliseconds(100);
        _visibilityTimer.IsRepeating = true;
        _visibilityTimer.Tick += VisibilityTimer_Tick;
    }

    public void InitializeBackgroundWindow()
    {
        if (_isInitialized)
        {
            return;
        }

        ConfigureWindow();
        RegisterWindowProcedure();

        if (!RegisterHotKey(_windowHandle, HotKeyId, ModControl | ModAlt | ModNoRepeat, VirtualKeySpace))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Ctrl+Alt+Space のグローバルホットキーを登録できませんでした。");
        }

        Activated += Window_Activated;
        Closed += Window_Closed;
        _windowsKeyMonitor.Start();
        _uiAutomationStartMenuInspector.Start();
        _logger.Write("[Application] ===== diagnostic session started =====");
        _logger.Write($"[Application] input-monitor=started log=\"{_logger.LogFilePath}\"");
        _isInitialized = true;
    }

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        _appWindow.IsShownInSwitchers = false;
        PositionWindow();
    }

    private void PositionWindow()
    {
        const int width = 500;
        const int height = 600;
        DisplayArea displayArea = DisplayArea.Primary;
        Windows.Graphics.RectInt32 workArea = displayArea.WorkArea;

        int x = workArea.X + ((workArea.Width - width) / 2);
        int y = workArea.Y + ((workArea.Height - height) / 2);

        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    private void ToggleWindow(bool activate)
    {
        if (_isVisible)
        {
            HideWindow("toggle-request");
        }
        else
        {
            ShowWindow(activate, activate ? "manual-hotkey" : "windows-key");
        }
    }

    private void OnWindowsKeyReleasedAlone()
    {
        _logger.Write("[WindowsKey] standalone trigger accepted; scan-delay-ms=180");
        _uiAutomationStartMenuInspector.RequestScan();
        _windowsKeyTimer.Stop();
        _windowsKeyTimer.Start();
    }

    private void WindowsKeyTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        _logger.Write("[WindowsKey] delayed scan started");
        _startMenuWindowInspector.InspectAndLog();
        _uiAutomationStartMenuInspector.RequestScan();
        ToggleWindow(activate: false);
    }

    private void ShowWindow(bool activate, string reason)
    {
        _visibilityTimer.Stop();
        PositionWindow();
        _launcherIsActivated = false;
        _automaticHideAllowedAt = DateTimeOffset.UtcNow.AddMilliseconds(750);
        _appWindow.Show(activate);
        _isVisible = true;
        _lastLoggedLauncherFocus = null;
        _lastLoggedStartMenuVisibility = null;
        _logger.Write($"[Launcher] action=show reason={reason} activate={activate} auto-hide-grace-ms=750");
        _visibilityTimer.Start();
    }

    private void HideWindow(string reason)
    {
        if (!_isVisible)
        {
            return;
        }

        _appWindow.Hide();
        _isVisible = false;
        _launcherIsActivated = false;
        _visibilityTimer.Stop();
        _logger.Write($"[Launcher] action=hide reason={reason}");
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        _launcherIsActivated = args.WindowActivationState != WindowActivationState.Deactivated;
        _logger.Write($"[Launcher] activation-state={args.WindowActivationState}");

        if (_isVisible && !_launcherIsActivated)
        {
            EvaluateAutomaticHide();
        }
    }

    private void VisibilityTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        EvaluateAutomaticHide();
    }

    private void EvaluateAutomaticHide()
    {
        if (!_isVisible || DateTimeOffset.UtcNow < _automaticHideAllowedAt)
        {
            return;
        }

        _uiAutomationStartMenuInspector.RequestScan();
        bool launcherHasFocus = _launcherIsActivated || GetForegroundWindow() == _windowHandle;
        bool startMenuIsVisible = _uiAutomationStartMenuInspector.IsStartMenuVisible
            || _startMenuWindowInspector.IsStartMenuVisible();

        if (_lastLoggedLauncherFocus != launcherHasFocus
            || _lastLoggedStartMenuVisibility != startMenuIsVisible)
        {
            _logger.Write(
                $"[VisibilityState] launcher-visible={_isVisible} launcher-focus={launcherHasFocus} " +
                $"start-menu-visible={startMenuIsVisible}");
            _lastLoggedLauncherFocus = launcherHasFocus;
            _lastLoggedStartMenuVisibility = startMenuIsVisible;
        }

        if (!launcherHasFocus && !startMenuIsVisible)
        {
            HideWindow("launcher-unfocused-and-start-menu-hidden");
        }
    }

    private void RootBorder_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            HideWindow("escape-key-xaml");
        }
    }

    private IntPtr WindowMessageHandler(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            ToggleWindow(activate: true);
            return IntPtr.Zero;
        }

        if (message == WmKeyDown && wParam.ToInt32() == VirtualKeyEscape)
        {
            HideWindow("escape-key-win32");
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousWindowProcedure, windowHandle, message, wParam, lParam);
    }

    private void RegisterWindowProcedure()
    {
        IntPtr procedurePointer = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
        _previousWindowProcedure = SetWindowLongPtr(_windowHandle, GwlpWndProc, procedurePointer);

        if (_previousWindowProcedure == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "ウィンドウメッセージの監視を開始できませんでした。");
            }
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _windowsKeyTimer.Stop();
        _visibilityTimer.Stop();
        _windowsKeyMonitor.Dispose();
        _uiAutomationStartMenuInspector.Dispose();
        _logger.Write("[Application] input-monitor=stopped; window=closed");
        UnregisterHotKey(_windowHandle, HotKeyId);

        if (_previousWindowProcedure != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowHandle, GwlpWndProc, _previousWindowProcedure);
            _previousWindowProcedure = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure,
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
