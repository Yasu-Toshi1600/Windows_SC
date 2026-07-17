using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows_SC.Services;
using Windows_SC.ViewModels;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Windows_SC;

public sealed partial class MainWindow : Window
{
    private const uint WmKeyDown = 0x0100;
    private const int VirtualKeyEscape = 0x1B;
    private const int GwlpWndProc = -4;
    private const double WindowAnimationOffsetEffectivePixels = 24;
    private static readonly TimeSpan WindowAnimationDuration = TimeSpan.FromMilliseconds(160);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly WindowProcedure _windowProcedure;
    private readonly DiagnosticLogger _logger;
    private readonly IStartMenuMonitor _startMenuMonitor;
    private readonly IGlobalInputService _inputService;
    private readonly ILauncherPlacementService _placementService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _windowAnimationTimer;
    private IntPtr _previousWindowProcedure;
    private bool _isVisible;
    private bool _isInitialized;
    private bool _launcherIsActivated;
    private bool _shownInResponseToStartMenu;
    private bool _startMenuVisibilityConfirmedForCurrentShow;
    private bool? _lastLoggedLauncherFocus;
    private bool? _lastLoggedStartMenuVisibility;
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
    internal MainWindowViewModel ViewModel { get; }

    internal MainWindow(
        MainWindowViewModel viewModel,
        DiagnosticLogger logger,
        IStartMenuMonitor startMenuMonitor,
        IGlobalInputService inputService,
        ILauncherPlacementService placementService)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _windowProcedure = WindowMessageHandler;
        _logger = logger;
        _startMenuMonitor = startMenuMonitor;
        _inputService = inputService;
        _placementService = placementService;
        _windowAnimationTimer = DispatcherQueue.CreateTimer();
        _windowAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _windowAnimationTimer.IsRepeating = true;
        _windowAnimationTimer.Tick += WindowAnimationTimer_Tick;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
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

        Activated += Window_Activated;
        Closed += Window_Closed;
        _inputService.ManualToggleRequested += InputService_ManualToggleRequested;
        _inputService.WindowsKeyReleasedAlone += InputService_WindowsKeyReleasedAlone;
        _inputService.Start(_windowHandle);
        _startMenuMonitor.SnapshotChanged += StartMenuMonitor_SnapshotChanged;
        _startMenuMonitor.Start();
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
        if (!_placementService.TryCalculate(
            startMenuSnapshot,
            ViewModel.AssumePhonePanelVisible,
            out LauncherPlacement placement))
        {
            return false;
        }

        _targetWindowRect = placement.TargetRect;
        _currentWindowRect = _targetWindowRect;
        _placementDpiPoint = placement.DpiPoint;
        _appWindow.MoveAndResize(_targetWindowRect);
        _logger.Write(
            $"[WindowPlacement] result=success position=({_targetWindowRect.X},{_targetWindowRect.Y}) " +
            $"size={_targetWindowRect.Width}x{_targetWindowRect.Height} " +
            $"display-work-area=({placement.WorkArea.X},{placement.WorkArea.Y}," +
            $"{placement.WorkArea.Width},{placement.WorkArea.Height}) " +
            $"phone-panel-mode={(ViewModel.AssumePhonePanelVisible ? "on" : "off")} " +
            $"phone-panel-detected={startMenuSnapshot?.IsPhonePanelVisible ?? false}");
        return true;
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

    private void InputService_ManualToggleRequested(object? sender, EventArgs args)
    {
        ToggleWindow(activate: true);
    }

    private void InputService_WindowsKeyReleasedAlone(object? sender, EventArgs args)
    {
        _startMenuMonitor.NotifyWindowsKeyReleased();
    }

    private void ShowWindow(bool activate, string reason, StartMenuSnapshot? startMenuSnapshot)
    {
        if (!PositionWindow(startMenuSnapshot))
        {
            _logger.Write($"[Launcher] action=show-cancelled reason={reason} placement-failed=true");
            return;
        }
        _launcherIsActivated = false;
        _shownInResponseToStartMenu = startMenuSnapshot is { IsVisible: true };
        _startMenuVisibilityConfirmedForCurrentShow = _shownInResponseToStartMenu;
        LauncherScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        PrepareWindowShowAnimation();
        _appWindow.Show(activate);
        _isVisible = true;
        _startMenuMonitor.SetLauncherVisible(true);
        _lastPlacementStartSnapshot = startMenuSnapshot;
        _lastLoggedLauncherFocus = null;
        _lastLoggedStartMenuVisibility = null;
        _logger.Write(
            $"[Launcher] action=show reason={reason} activate={activate} " +
            $"start-linked={_shownInResponseToStartMenu} start-confirmed={_startMenuVisibilityConfirmedForCurrentShow}");
        StartPreparedWindowAnimation();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            AnimateLauncherItems);
    }

    private void AnimateLauncherItems()
    {
        if (!_animationsEnabled || !_isVisible)
        {
            return;
        }

        int itemCount = Math.Min(ViewModel.Shortcuts.Count, 12);
        for (int index = 0; index < itemCount; index++)
        {
            if (LauncherItemsControl.ContainerFromIndex(index) is not UIElement element)
            {
                continue;
            }

            Microsoft.UI.Composition.Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Vector3 restingOffset = visual.Offset;
            TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(index, 8) * 22);

            visual.StopAnimation(nameof(visual.Opacity));
            visual.StopAnimation(nameof(visual.Offset));
            visual.Opacity = 0;

            Microsoft.UI.Composition.ScalarKeyFrameAnimation opacityAnimation =
                visual.Compositor.CreateScalarKeyFrameAnimation();
            opacityAnimation.InsertKeyFrame(0, 0);
            opacityAnimation.InsertKeyFrame(1, 1);
            opacityAnimation.Duration = TimeSpan.FromMilliseconds(170);
            opacityAnimation.DelayTime = delay;
            opacityAnimation.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

            Microsoft.UI.Composition.Vector3KeyFrameAnimation offsetAnimation =
                visual.Compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.InsertKeyFrame(0, restingOffset + new Vector3(0, 12, 0));
            offsetAnimation.InsertKeyFrame(1, restingOffset);
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(190);
            offsetAnimation.DelayTime = delay;
            offsetAnimation.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;

            visual.StartAnimation(nameof(visual.Opacity), opacityAnimation);
            visual.StartAnimation(nameof(visual.Offset), offsetAnimation);
        }
    }

    private void PrepareWindowShowAnimation()
    {
        StopWindowAnimation();

        if (!_animationsEnabled)
        {
            _currentWindowRect = _targetWindowRect;
            return;
        }

        int offset = _placementService.ConvertEffectivePixelsToPhysical(
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
            int offset = _placementService.ConvertEffectivePixelsToPhysical(
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
        _shownInResponseToStartMenu = false;
        _startMenuVisibilityConfirmedForCurrentShow = false;
        _lastPlacementStartSnapshot = null;
        _logger.Write($"[Launcher] action=hide reason={reason}");
        _startMenuMonitor.SetLauncherVisible(false);
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainWindowViewModel.AssumePhonePanelVisible))
        {
            return;
        }

        _logger.Write(
            $"[PhonePanelSetting] source=launcher value={(ViewModel.AssumePhonePanelVisible ? "on" : "off")}");

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

    private void SynchronizeWithStartMenu()
    {
        bool launcherHasFocus = _launcherIsActivated || GetForegroundWindow() == _windowHandle;
        StartMenuSnapshot startMenuSnapshot = _startMenuMonitor.Snapshot;
        bool startMenuIsVisible = startMenuSnapshot.IsVisible;

        if (_isVisible && startMenuIsVisible)
        {
            _startMenuVisibilityConfirmedForCurrentShow = true;
        }

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
                startMenuSnapshot);
            return;
        }

        if (_isVisible
            && !launcherHasFocus
            && !startMenuIsVisible
            && (!_shownInResponseToStartMenu || _startMenuVisibilityConfirmedForCurrentShow))
        {
            HideWindow("launcher-unfocused-and-start-menu-hidden");
        }
    }

    private void StartMenuMonitor_SnapshotChanged(object? sender, EventArgs args)
    {
        SynchronizeWithStartMenu();
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
        if (_inputService.TryHandleWindowMessage(message, wParam))
        {
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
        _windowAnimationTimer.Stop();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _inputService.ManualToggleRequested -= InputService_ManualToggleRequested;
        _inputService.WindowsKeyReleasedAlone -= InputService_WindowsKeyReleasedAlone;
        _inputService.Dispose();
        _startMenuMonitor.SnapshotChanged -= StartMenuMonitor_SnapshotChanged;
        _startMenuMonitor.Dispose();
        _logger.Write("[Application] input-monitor=stopped; window=closed");
        if (_previousWindowProcedure != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowHandle, GwlpWndProc, _previousWindowProcedure);
            _previousWindowProcedure = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

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
