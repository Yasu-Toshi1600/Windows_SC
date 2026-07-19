using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Numerics;
using Windows_SC.Services;
using Windows_SC.ViewModels;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Windows_SC;

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly DiagnosticLogger _logger;
    private readonly IStartMenuMonitor _startMenuMonitor;
    private readonly IGlobalInputService _inputService;
    private readonly ILauncherPlacementService _placementService;
    private readonly IWindowInteropService _windowInteropService;
    private readonly IWindowAnimationService _windowAnimationService;
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
    private Windows.Graphics.PointInt32 _placementDpiPoint;
    internal MainWindowViewModel ViewModel { get; }

    internal MainWindow(
        MainWindowViewModel viewModel,
        DiagnosticLogger logger,
        IStartMenuMonitor startMenuMonitor,
        IGlobalInputService inputService,
        ILauncherPlacementService placementService,
        IWindowInteropService windowInteropService)
    {
        ViewModel = viewModel;
        InitializeComponent();

        _windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _logger = logger;
        _startMenuMonitor = startMenuMonitor;
        _inputService = inputService;
        _placementService = placementService;
        _windowInteropService = windowInteropService;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        _animationsEnabled = new UISettings().AnimationsEnabled;
        _windowAnimationService = new WindowAnimationService(
            _windowHandle,
            DispatcherQueue,
            _windowInteropService,
            _placementService,
            _animationsEnabled);
        _windowAnimationService.HideCompleted += WindowAnimationService_HideCompleted;
    }

    public void InitializeBackgroundWindow()
    {
        if (_isInitialized)
        {
            return;
        }

        ConfigureWindow();
        _windowInteropService.EscapePressed += WindowInteropService_EscapePressed;
        _windowInteropService.Start(_windowHandle);

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
        _placementDpiPoint = placement.DpiPoint;
        _appWindow.MoveAndResize(_targetWindowRect);
        _windowAnimationService.SetPosition(_targetWindowRect);
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
        _windowAnimationService.PrepareShow(_targetWindowRect, _placementDpiPoint);
        _appWindow.Show(activate);
        _isVisible = true;
        _startMenuMonitor.SetLauncherVisible(true);
        _lastPlacementStartSnapshot = startMenuSnapshot;
        _lastLoggedLauncherFocus = null;
        _lastLoggedStartMenuVisibility = null;
        _logger.Write(
            $"[Launcher] action=show reason={reason} activate={activate} " +
            $"start-linked={_shownInResponseToStartMenu} start-confirmed={_startMenuVisibilityConfirmedForCurrentShow}");
        _windowAnimationService.StartShow();
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

    private void HideWindow(string reason)
    {
        if (!_isVisible || _windowAnimationService.IsHideRunning)
        {
            return;
        }

        if (_windowAnimationService.StartHide(
            _targetWindowRect,
            _placementDpiPoint,
            reason))
        {
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

    private void WindowAnimationService_HideCompleted(
        object? sender,
        WindowHideCompletedEventArgs args)
    {
        CompleteHide(args.Reason);
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
        bool launcherHasFocus = _launcherIsActivated
            || _windowInteropService.IsForeground(_windowHandle);
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

    private void WindowInteropService_EscapePressed(object? sender, EventArgs args)
    {
        HideWindow("escape-key-win32");
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _windowAnimationService.HideCompleted -= WindowAnimationService_HideCompleted;
        _windowAnimationService.Dispose();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _inputService.ManualToggleRequested -= InputService_ManualToggleRequested;
        _inputService.WindowsKeyReleasedAlone -= InputService_WindowsKeyReleasedAlone;
        _startMenuMonitor.SnapshotChanged -= StartMenuMonitor_SnapshotChanged;
        _startMenuMonitor.Dispose();
        _windowInteropService.EscapePressed -= WindowInteropService_EscapePressed;
        _windowInteropService.Dispose();
        _inputService.Dispose();
        _logger.Write("[Application] input-monitor=stopped; window=closed");
    }
}
