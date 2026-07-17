using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.System;
using Windows.UI.ViewManagement;
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
    private const int LauncherWidth = 500;
    private const int LauncherHeight = 600;
    private const int LauncherMargin = 12;
    private const double PhonePanelReservedEffectivePixels = 281;
    private const double LauncherBottomMarginEffectivePixels = 12;
    private const double WindowAnimationOffsetEffectivePixels = 24;
    private static readonly TimeSpan WindowAnimationDuration = TimeSpan.FromMilliseconds(160);
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly WindowProcedure _windowProcedure;
    private readonly DiagnosticLogger _logger;
    private readonly StartMenuWindowInspector _startMenuWindowInspector;
    private readonly UiAutomationStartMenuInspector _uiAutomationStartMenuInspector;
    private readonly GlobalWindowsKeyMonitor _windowsKeyMonitor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _windowsKeyTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _visibilityTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _windowAnimationTimer;
    private IntPtr _previousWindowProcedure;
    private bool _isVisible;
    private bool _isInitialized;
    private bool _launcherIsActivated;
    private DateTimeOffset _automaticHideAllowedAt;
    private bool? _lastLoggedLauncherFocus;
    private bool? _lastLoggedStartMenuVisibility;
    private bool _assumePhonePanelVisible = true;
    private StartMenuSnapshot? _lastPlacementStartSnapshot;
    private readonly bool _animationsEnabled;
    private Windows.Graphics.RectInt32 _targetWindowRect;
    private Windows.Graphics.RectInt32 _currentWindowRect;
    private Windows.Graphics.PointInt32 _placementDpiPoint;
    private Windows.Graphics.RectInt32 _animationStartRect;
    private Windows.Graphics.RectInt32 _animationEndRect;
    private DateTimeOffset _windowAnimationStartedAt;
    private bool _isWindowAnimationRunning;
    private bool _isHideAnimation;
    private string _pendingHideReason = string.Empty;

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
        _visibilityTimer.Interval = TimeSpan.FromMilliseconds(33);
        _visibilityTimer.IsRepeating = true;
        _visibilityTimer.Tick += VisibilityTimer_Tick;
        _windowAnimationTimer = DispatcherQueue.CreateTimer();
        _windowAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _windowAnimationTimer.IsRepeating = true;
        _windowAnimationTimer.Tick += WindowAnimationTimer_Tick;
        PhonePanelToggle.IsOn = _assumePhonePanelVisible;
        _animationsEnabled = new UISettings().AnimationsEnabled;
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
        _visibilityTimer.Start();
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
        PositionWindow(null);
    }

    private bool PositionWindow(StartMenuSnapshot? startMenuSnapshot)
    {
        DisplayArea displayArea;
        int x;
        int y;

        if (startMenuSnapshot is { IsVisible: true, Bounds: not null } snapshot)
        {
            Windows.Graphics.RectInt32 startBounds = snapshot.Bounds.Value;
            int anchorRight = startBounds.X + startBounds.Width;

            if (snapshot.PhonePanelBounds is Windows.Graphics.RectInt32 phoneBounds)
            {
                anchorRight = Math.Max(anchorRight, phoneBounds.X + phoneBounds.Width);
            }

            Windows.Graphics.PointInt32 startCenter = new(
                startBounds.X + (startBounds.Width / 2),
                startBounds.Y + (startBounds.Height / 2));
            displayArea = DisplayArea.GetFromPoint(
                startCenter,
                DisplayAreaFallback.Nearest) ?? DisplayArea.Primary;
            Windows.Graphics.RectInt32 targetWorkArea = displayArea.WorkArea;
            int bottomMargin = ConvertEffectivePixelsToPhysical(
                LauncherBottomMarginEffectivePixels,
                startCenter);

            if (_assumePhonePanelVisible && snapshot.PhonePanelBounds is null)
            {
                anchorRight += ConvertEffectivePixelsToPhysical(
                    PhonePanelReservedEffectivePixels,
                    startCenter);
            }

            x = anchorRight + LauncherMargin;
            y = Math.Clamp(
                startBounds.Y + startBounds.Height - LauncherHeight - bottomMargin,
                targetWorkArea.Y,
                targetWorkArea.Y + targetWorkArea.Height - LauncherHeight);

            if (x + LauncherWidth > targetWorkArea.X + targetWorkArea.Width)
            {
                _logger.Write(
                    $"[WindowPlacement] result=failed reason=insufficient-right-space " +
                    $"start=({startBounds.X},{startBounds.Y},{startBounds.Width},{startBounds.Height}) " +
                    $"work-area=({targetWorkArea.X},{targetWorkArea.Y},{targetWorkArea.Width},{targetWorkArea.Height}) " +
                    $"required-width={LauncherWidth}");
                return false;
            }

            _placementDpiPoint = startCenter;
        }
        else
        {
            GetCursorPos(out NativePoint cursorPosition);
            displayArea = DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(cursorPosition.X, cursorPosition.Y),
                DisplayAreaFallback.Nearest) ?? DisplayArea.Primary;
            Windows.Graphics.RectInt32 targetWorkArea = displayArea.WorkArea;
            x = targetWorkArea.X + ((targetWorkArea.Width - LauncherWidth) / 2);
            y = targetWorkArea.Y + ((targetWorkArea.Height - LauncherHeight) / 2);
            _placementDpiPoint = new Windows.Graphics.PointInt32(
                cursorPosition.X,
                cursorPosition.Y);
        }

        Windows.Graphics.RectInt32 workArea = displayArea.WorkArea;
        _targetWindowRect = new Windows.Graphics.RectInt32(
            x,
            y,
            LauncherWidth,
            LauncherHeight);
        _currentWindowRect = _targetWindowRect;
        _appWindow.MoveAndResize(_targetWindowRect);
        _logger.Write(
            $"[WindowPlacement] result=success position=({x},{y}) size={LauncherWidth}x{LauncherHeight} " +
            $"display-work-area=({workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}) " +
            $"phone-panel-mode={(_assumePhonePanelVisible ? "on" : "off")} " +
            $"phone-panel-detected={startMenuSnapshot?.IsPhonePanelVisible ?? false}");
        return true;
    }

    private static int ConvertEffectivePixelsToPhysical(
        double effectivePixels,
        Windows.Graphics.PointInt32 point)
    {
        NativePoint nativePoint = new() { X = point.X, Y = point.Y };
        IntPtr monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);

        if (monitor != IntPtr.Zero
            && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0)
        {
            return (int)Math.Round(effectivePixels * dpiX / 96d);
        }

        return (int)Math.Round(effectivePixels);
    }

    private void ToggleWindow(bool activate)
    {
        if (_isVisible)
        {
            HideWindow("toggle-request");
        }
        else
        {
            ShowWindow(activate, "manual-hotkey", null);
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
    }

    private void ShowWindow(bool activate, string reason, StartMenuSnapshot? startMenuSnapshot)
    {
        if (!PositionWindow(startMenuSnapshot))
        {
            _logger.Write($"[Launcher] action=show-cancelled reason={reason} placement-failed=true");
            return;
        }
        _launcherIsActivated = false;
        _automaticHideAllowedAt = DateTimeOffset.UtcNow.AddMilliseconds(750);
        PrepareWindowShowAnimation();
        _appWindow.Show(activate);
        _isVisible = true;
        _lastPlacementStartSnapshot = startMenuSnapshot;
        _lastLoggedLauncherFocus = null;
        _lastLoggedStartMenuVisibility = null;
        _logger.Write($"[Launcher] action=show reason={reason} activate={activate} auto-hide-grace-ms=750");
        StartPreparedWindowAnimation();
    }

    private void PrepareWindowShowAnimation()
    {
        StopWindowAnimation();

        if (!_animationsEnabled)
        {
            _currentWindowRect = _targetWindowRect;
            return;
        }

        int offset = ConvertEffectivePixelsToPhysical(
            WindowAnimationOffsetEffectivePixels,
            _placementDpiPoint);
        _animationStartRect = OffsetVertically(_targetWindowRect, offset);
        _animationEndRect = _targetWindowRect;
        _currentWindowRect = _animationStartRect;
        MoveWindowForAnimation(_animationStartRect);
        _isHideAnimation = false;
    }

    private void StartPreparedWindowAnimation()
    {
        if (_animationsEnabled)
        {
            _windowAnimationStartedAt = DateTimeOffset.UtcNow;
            _isWindowAnimationRunning = true;
            _windowAnimationTimer.Start();
        }
    }

    private void HideWindow(string reason)
    {
        if (!_isVisible || (_isWindowAnimationRunning && _isHideAnimation))
        {
            return;
        }

        if (_animationsEnabled)
        {
            StopWindowAnimation();
            int offset = ConvertEffectivePixelsToPhysical(
                WindowAnimationOffsetEffectivePixels,
                _placementDpiPoint);
            _animationStartRect = _currentWindowRect;
            _animationEndRect = OffsetVertically(_targetWindowRect, offset);
            _windowAnimationStartedAt = DateTimeOffset.UtcNow;
            _isHideAnimation = true;
            _isWindowAnimationRunning = true;
            _pendingHideReason = reason;
            _windowAnimationTimer.Start();
            _logger.Write($"[Launcher] action=hide-animation-start reason={reason}");
            return;
        }

        CompleteHide(reason);
    }

    private void CompleteHide(string reason)
    {
        _appWindow.Hide();
        _isVisible = false;
        _launcherIsActivated = false;
        _lastPlacementStartSnapshot = null;
        _logger.Write($"[Launcher] action=hide reason={reason}");
    }

    private void WindowAnimationTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        double progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _windowAnimationStartedAt).TotalMilliseconds
                / WindowAnimationDuration.TotalMilliseconds,
            0,
            1);
        double easedProgress = _isHideAnimation
            ? progress * progress * progress
            : 1 - Math.Pow(1 - progress, 3);

        _currentWindowRect = InterpolateRectangle(
            _animationStartRect,
            _animationEndRect,
            easedProgress);
        MoveWindowForAnimation(_currentWindowRect);

        if (progress < 1)
        {
            return;
        }

        bool completedHideAnimation = _isHideAnimation;
        string hideReason = _pendingHideReason;
        StopWindowAnimation();

        if (completedHideAnimation)
        {
            CompleteHide(hideReason);
        }
    }

    private void StopWindowAnimation()
    {
        _windowAnimationTimer.Stop();
        _isWindowAnimationRunning = false;
        _isHideAnimation = false;
        _pendingHideReason = string.Empty;
    }

    private static Windows.Graphics.RectInt32 OffsetVertically(
        Windows.Graphics.RectInt32 rectangle,
        int offset) =>
        new(rectangle.X, rectangle.Y + offset, rectangle.Width, rectangle.Height);

    private static Windows.Graphics.RectInt32 InterpolateRectangle(
        Windows.Graphics.RectInt32 start,
        Windows.Graphics.RectInt32 end,
        double progress) =>
        new(
            Interpolate(start.X, end.X, progress),
            Interpolate(start.Y, end.Y, progress),
            Interpolate(start.Width, end.Width, progress),
            Interpolate(start.Height, end.Height, progress));

    private static int Interpolate(int start, int end, double progress) =>
        (int)Math.Round(start + ((end - start) * progress));

    private void MoveWindowForAnimation(Windows.Graphics.RectInt32 rectangle)
    {
        _ = SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            rectangle.X,
            rectangle.Y,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private void PhonePanelToggle_Toggled(object sender, RoutedEventArgs args)
    {
        _assumePhonePanelVisible = PhonePanelToggle.IsOn;
        _logger.Write(
            $"[PhonePanelSetting] source=launcher value={(_assumePhonePanelVisible ? "on" : "off")}");

        if (_isVisible && _lastPlacementStartSnapshot is StartMenuSnapshot snapshot)
        {
            _ = PositionWindow(snapshot);
        }
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        _launcherIsActivated = args.WindowActivationState != WindowActivationState.Deactivated;
        _logger.Write($"[Launcher] activation-state={args.WindowActivationState}");

        if (_isVisible && !_launcherIsActivated)
        {
            SynchronizeWithStartMenu();
        }
    }

    private void VisibilityTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        SynchronizeWithStartMenu();
    }

    private void SynchronizeWithStartMenu()
    {
        _uiAutomationStartMenuInspector.RequestScan();
        bool launcherHasFocus = _launcherIsActivated || GetForegroundWindow() == _windowHandle;
        bool startMenuIsVisible = _uiAutomationStartMenuInspector.IsStartMenuVisible;

        if (_lastLoggedLauncherFocus != launcherHasFocus
            || _lastLoggedStartMenuVisibility != startMenuIsVisible)
        {
            _logger.Write(
                $"[VisibilityState] launcher-visible={_isVisible} launcher-focus={launcherHasFocus} " +
                $"start-menu-visible={startMenuIsVisible}");
            _lastLoggedLauncherFocus = launcherHasFocus;
            _lastLoggedStartMenuVisibility = startMenuIsVisible;
        }

        if (startMenuIsVisible && !_isVisible)
        {
            ShowWindow(
                activate: false,
                "start-menu-detected",
                _uiAutomationStartMenuInspector.Snapshot);
            return;
        }

        if (_isVisible
            && DateTimeOffset.UtcNow >= _automaticHideAllowedAt
            && !launcherHasFocus
            && !startMenuIsVisible)
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
        _windowAnimationTimer.Stop();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
