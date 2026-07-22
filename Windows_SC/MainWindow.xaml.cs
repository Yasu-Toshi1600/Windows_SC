using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Diagnostics;
using Windows_SC.Services;
using Windows_SC.ViewModels;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Windows_SC;

public sealed partial class MainWindow : Window
{
    private const int MaximumActivationAttempts = 5;

    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly DiagnosticLogger _logger;
    private readonly IStartMenuMonitor _startMenuMonitor;
    private readonly IGlobalInputService _inputService;
    private readonly ILauncherPlacementService _placementService;
    private readonly IWindowInteropService _windowInteropService;
    private readonly EnvironmentInformationService _environmentInformationService;
    private readonly DispatcherQueueTimer _environmentCheckTimer;
    private readonly DispatcherQueueTimer _activationRetryTimer;
    private readonly ILauncherMotionService _motionService;
    private readonly LauncherMotionCoordinator _motionCoordinator;
    private readonly UISettings _uiSettings;
    private bool _isVisible;
    private bool _isInitialized;
    private bool _launcherIsActivated;
    private bool? _lastLoggedLauncherFocus;
    private bool? _lastLoggedStartMenuVisibility;
    private bool _isActionErrorDialogOpen;
    private StartMenuSnapshot? _lastPlacementStartSnapshot;
    private Windows.Graphics.RectInt32 _targetWindowRect;
    private Windows.Graphics.PointInt32 _placementDpiPoint;
    private long _windowsKeyReleasedTimestamp;
    private long _startDetectedTimestamp;
    private long _showRequestedTimestamp;
    private bool _startLinkedVisibilityRequested;
    private string _pendingEnvironmentChangeReason = "display-change";
    private int _activationAttemptCount;
    private string _activationReason = "manual";

    internal MainWindowViewModel ViewModel { get; }

    internal MainWindow(
        MainWindowViewModel viewModel,
        DiagnosticLogger logger,
        IStartMenuMonitor startMenuMonitor,
        IGlobalInputService inputService,
        ILauncherPlacementService placementService,
        IWindowInteropService windowInteropService,
        EnvironmentInformationService environmentInformationService)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // Window is created hidden, so initialize compiled bindings once before the
        // first presentation. This stays outside the launcher display hot path.
        Bindings.Update();

        _windowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _logger = logger;
        _logger.WriteDetailed(
            $"[LauncherVisual] compiled-bindings=initialized items={LauncherItemsControl.Items.Count}");
        _startMenuMonitor = startMenuMonitor;
        _inputService = inputService;
        _placementService = placementService;
        _windowInteropService = windowInteropService;
        _environmentInformationService = environmentInformationService;
        _environmentCheckTimer = DispatcherQueue.CreateTimer();
        _environmentCheckTimer.Interval = TimeSpan.FromMilliseconds(750);
        _environmentCheckTimer.IsRepeating = false;
        _environmentCheckTimer.Tick += EnvironmentCheckTimer_Tick;
        _activationRetryTimer = DispatcherQueue.CreateTimer();
        _activationRetryTimer.Interval = TimeSpan.FromMilliseconds(50);
        _activationRetryTimer.IsRepeating = false;
        _activationRetryTimer.Tick += ActivationRetryTimer_Tick;
        _motionCoordinator = new LauncherMotionCoordinator(logger);
        _uiSettings = new UISettings();
        _motionService = new CompositionLauncherMotionService(
            DispatcherQueue,
            _uiSettings.AnimationsEnabled);
        _motionService.Attach(LauncherSurface);

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.LauncherItemExecuted += ViewModel_LauncherItemExecuted;
        ViewModel.AudioOutputService.StateChanged += AudioOutputService_StateChanged;
        _motionService.Completed += MotionService_Completed;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged += UISettings_AnimationsEnabledChanged;
        }
        RootBorder.Loaded += RootBorder_Loaded;
    }

    public void InitializeBackgroundWindow()
    {
        if (_isInitialized)
        {
            return;
        }

        ConfigureWindow();
        _windowInteropService.EscapePressed += WindowInteropService_EscapePressed;
        _windowInteropService.DisplayEnvironmentChanged +=
            WindowInteropService_DisplayEnvironmentChanged;
        _windowInteropService.Start(_windowHandle);

        Activated += Window_Activated;
        Closed += Window_Closed;
        _inputService.ManualToggleRequested += InputService_ManualToggleRequested;
        _inputService.WindowsKeyReleasedAlone += InputService_WindowsKeyReleasedAlone;
        _inputService.Start(_windowHandle);
        _startMenuMonitor.SnapshotChanged += StartMenuMonitor_SnapshotChanged;
        _startMenuMonitor.ReadyChanged += StartMenuMonitor_ReadyChanged;
        _startMenuMonitor.StartConfirmationExpired += StartMenuMonitor_StartConfirmationExpired;
        _startMenuMonitor.Start();
        _logger.Write("[Application] ===== diagnostic session started =====");
        _environmentInformationService.LogIfChanged("startup", force: true);
        _logger.Write($"[Application] input-monitor=started log=\"{_logger.LogFilePath}\"");
        _logger.Write(
            $"[Motion] engine=composition animations-enabled={_motionService.AnimationsEnabled} " +
            "card-animation=disabled hwnd-frame-move=disabled");
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
        if (PositionWindow(null))
        {
            _motionService.SetHidden(GetEntranceTranslation(startLinked: true));
        }
    }

    private bool PositionWindow(StartMenuSnapshot? startMenuSnapshot)
    {
        if (!_placementService.TryCalculate(
            startMenuSnapshot,
            ViewModel.AssumePhonePanelVisible,
            ViewModel.LayoutMode,
            out LauncherPlacement placement))
        {
            return false;
        }

        _targetWindowRect = placement.TargetRect;
        _placementDpiPoint = placement.DpiPoint;
        _appWindow.MoveAndResize(_targetWindowRect);
        _logger.WriteDetailed(
            $"[WindowPlacement] result=success position=({_targetWindowRect.X},{_targetWindowRect.Y}) " +
            $"size={_targetWindowRect.Width}x{_targetWindowRect.Height} " +
            $"display-work-area=({placement.WorkArea.X},{placement.WorkArea.Y}," +
            $"{placement.WorkArea.Width},{placement.WorkArea.Height}) " +
            $"phone-panel-mode={(ViewModel.AssumePhonePanelVisible ? "on" : "off")} " +
            $"phone-panel-detected={startMenuSnapshot?.IsPhonePanelVisible ?? false}");
        return true;
    }

    private void ToggleWindow()
    {
        if (_motionCoordinator.State == LauncherMotionState.Exiting)
        {
            ShowWindow(activate: true, "manual-reverse", null);
        }
        else if (_motionCoordinator.IsWindowVisible)
        {
            RequestExit("toggle-request");
        }
        else
        {
            ShowWindow(activate: true, "manual-hotkey", null);
        }
    }

    internal void RequestManualShow()
    {
        if (_motionCoordinator.State == LauncherMotionState.Exiting)
        {
            ShowWindow(activate: true, "tray-reverse", null);
        }
        else if (!_motionCoordinator.IsWindowVisible)
        {
            ShowWindow(activate: true, "system-tray", null);
        }
    }

    private void InputService_ManualToggleRequested(object? sender, EventArgs args) =>
        ToggleWindow();

    private void InputService_WindowsKeyReleasedAlone(object? sender, EventArgs args)
    {
        _windowsKeyReleasedTimestamp = Stopwatch.GetTimestamp();

        // When the launcher is following an already-visible Start surface, a
        // standalone Windows key closes Start. Do not wait for the slower UIA
        // hidden-state confirmation before beginning the launcher exit.
        if (_motionCoordinator.State is LauncherMotionState.EnteringWithStart
            or LauncherMotionState.VisibleWithStart)
        {
            _startLinkedVisibilityRequested = false;
            _logger.Write("[LaunchTiming] event=windows-key-close action=exit-immediate");
            _startMenuMonitor.NotifyStartMenuClosing();
            RequestExit("windows-key-close");
            return;
        }

        if (!_motionCoordinator.IsWindowVisible)
        {
            _motionCoordinator.AwaitStartConfirmation();
        }
        _startMenuMonitor.NotifyWindowsKeyReleased();
    }

    private void ShowWindow(bool activate, string reason, StartMenuSnapshot? startMenuSnapshot)
    {
        bool reversingExit = _motionCoordinator.State == LauncherMotionState.Exiting;
        bool needsPlacement = !_isVisible
            || (reversingExit && startMenuSnapshot is { IsVisible: true, Bounds: not null });
        if (needsPlacement && !PositionWindow(startMenuSnapshot))
        {
            _motionCoordinator.CancelStartConfirmation("placement-failed");
            _logger.Write($"[Launcher] action=show-cancelled reason={reason} placement-failed=true");
            return;
        }

        bool startLinked = startMenuSnapshot is { IsVisible: true, Bounds: not null };
        float entranceTranslation = GetEntranceTranslation(startLinked);
        _launcherIsActivated = false;
        ViewModel.RefreshAudioOutputState();
        LauncherScrollViewer.ChangeView(null, 0, null, disableAnimation: true);

        if (!reversingExit)
        {
            _motionService.PrepareEntrance(entranceTranslation, startLinked);
        }

        _motionCoordinator.BeginEntrance(startLinked, reason);
        _startLinkedVisibilityRequested = startLinked;
        _lastPlacementStartSnapshot = startMenuSnapshot;
        _lastLoggedLauncherFocus = null;
        _lastLoggedStartMenuVisibility = null;
        _showRequestedTimestamp = Stopwatch.GetTimestamp();

        if (!_isVisible)
        {
            _isVisible = true;
            _appWindow.Show(activate);
            _startMenuMonitor.SetLauncherVisible(true);
        }
        else if (activate)
        {
            _appWindow.Show(true);
        }

        if (activate)
        {
            BeginActivationVerification(reason);
        }
        else
        {
            _activationRetryTimer.Stop();
        }

        _logger.Write(
            $"[Launcher] action=show-request reason={reason} activate={activate} " +
            $"start-linked={startLinked} reverse={reversingExit} " +
            $"detect-to-request-ms={ElapsedMilliseconds(_startDetectedTimestamp):F1}");

        if (!_motionService.StartEntrance(entranceTranslation, startLinked))
        {
            _motionCoordinator.CompleteEntrance();
            LogEntranceCompleted(TimeSpan.Zero);
        }
    }

    private void RequestExit(string reason)
    {
        if (!_motionCoordinator.BeginExit(reason))
        {
            return;
        }

        float exitTranslation = GetEntranceTranslation(
            _lastPlacementStartSnapshot is { IsVisible: true });
        if (_motionService.StartExit(exitTranslation, reason))
        {
            _logger.Write($"[Launcher] action=exit-request reason={reason}");
            return;
        }

        CompleteHide(reason);
    }

    private void CompleteHide(string reason)
    {
        _activationRetryTimer.Stop();
        _appWindow.Hide();
        _isVisible = false;
        _launcherIsActivated = false;
        _lastPlacementStartSnapshot = null;
        _motionCoordinator.CompleteExit(reason);
        _logger.Write($"[Launcher] action=hidden reason={reason}");
        _startMenuMonitor.SetLauncherVisible(false);

        StartMenuSnapshot latestSnapshot = _startMenuMonitor.Snapshot;
        if (_startLinkedVisibilityRequested
            && latestSnapshot is { IsVisible: true, Bounds: not null })
        {
            _startDetectedTimestamp = Stopwatch.GetTimestamp();
            ShowWindow(
                activate: false,
                "start-menu-final-state-reconcile",
                latestSnapshot);
        }
    }

    private void MotionService_Completed(
        object? sender,
        LauncherMotionCompletedEventArgs args)
    {
        _logger.Write(
            $"[Motion] direction={args.Direction} completed=true " +
            $"elapsed-ms={args.Elapsed.TotalMilliseconds:F1} reason={args.Reason}");

        if (args.Direction == LauncherMotionDirection.Exit)
        {
            CompleteHide(args.Reason);
            return;
        }

        _motionCoordinator.CompleteEntrance();
        LogEntranceCompleted(args.Elapsed);
    }

    private void LogEntranceCompleted(TimeSpan motionElapsed)
    {
        _logger.Write(
            $"[LaunchTiming] key-to-start-ms={ElapsedBetweenMilliseconds(_windowsKeyReleasedTimestamp, _startDetectedTimestamp):F1} " +
            $"start-to-request-ms={ElapsedBetweenMilliseconds(_startDetectedTimestamp, _showRequestedTimestamp):F1} " +
            $"request-to-complete-ms={ElapsedMilliseconds(_showRequestedTimestamp):F1} " +
            $"motion-ms={motionElapsed.TotalMilliseconds:F1}");
    }

    private void RootBorder_Loaded(object sender, RoutedEventArgs args)
    {
        _logger.WriteDetailed(
            $"[LauncherVisual] event=loaded size={RootBorder.ActualWidth:F0}x{RootBorder.ActualHeight:F0} " +
            $"shortcuts={ViewModel.Shortcuts.Count} request-to-loaded-ms={ElapsedMilliseconds(_showRequestedTimestamp):F1}");
    }

    private void RootBorder_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_isVisible)
        {
            MarkLauncherInteractive("pointer-pressed");
        }
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        _launcherIsActivated = args.WindowActivationState != WindowActivationState.Deactivated;
        _logger.WriteDetailed($"[Launcher] activation-state={args.WindowActivationState}");

        if (!_isVisible)
        {
            return;
        }

        if (_launcherIsActivated)
        {
            _activationRetryTimer.Stop();
            MarkLauncherInteractive("window-activated");
        }
        else if (_motionCoordinator.IsInteractive)
        {
            RequestExit("outside-click");
        }
        else
        {
            SynchronizeWithStartMenu();
        }
    }

    private void SynchronizeWithStartMenu()
    {
        bool launcherHasFocus = _launcherIsActivated
            || _windowInteropService.IsForeground(_windowHandle);
        StartMenuSnapshot snapshot = _startMenuMonitor.Snapshot;
        bool startMenuIsVisible = snapshot.IsVisible && snapshot.Bounds is not null;

        if (_lastLoggedLauncherFocus != launcherHasFocus
            || _lastLoggedStartMenuVisibility != startMenuIsVisible)
        {
            _logger.WriteDetailed(
                $"[VisibilityState] state={_motionCoordinator.State} launcher-focus={launcherHasFocus} " +
                $"start-menu-visible={startMenuIsVisible}");
            _lastLoggedLauncherFocus = launcherHasFocus;
            _lastLoggedStartMenuVisibility = startMenuIsVisible;
        }

        if (launcherHasFocus)
        {
            MarkLauncherInteractive("focus-detected");
        }

        if (_motionCoordinator.State is LauncherMotionState.EnteringManual
            or LauncherMotionState.VisibleInteractive)
        {
            return;
        }

        _startLinkedVisibilityRequested = startMenuIsVisible;

        if (startMenuIsVisible
            && (_motionCoordinator.State == LauncherMotionState.Hidden
                || _motionCoordinator.State == LauncherMotionState.AwaitingStartConfirmation
                || _motionCoordinator.State == LauncherMotionState.Exiting))
        {
            bool openedWithoutWindowsKey = _motionCoordinator.State == LauncherMotionState.Hidden;
            bool reopenedDuringExit = _motionCoordinator.State == LauncherMotionState.Exiting;
            if (openedWithoutWindowsKey)
            {
                _windowsKeyReleasedTimestamp = 0;
            }

            _startDetectedTimestamp = Stopwatch.GetTimestamp();
            _logger.Write(
                $"[LaunchTiming] event=start-confirmed key-to-start-ms={ElapsedMilliseconds(_windowsKeyReleasedTimestamp):F1}");
            ShowWindow(
                activate: false,
                openedWithoutWindowsKey
                    ? "start-menu-click-detected"
                    : reopenedDuringExit
                        ? "start-menu-reopened-during-exit"
                        : "start-menu-detected",
                snapshot);
            return;
        }

        if (!startMenuIsVisible
            && _motionCoordinator.State is LauncherMotionState.EnteringWithStart
                or LauncherMotionState.VisibleWithStart)
        {
            RequestExit("start-menu-hidden");
        }
    }

    private void MarkLauncherInteractive(string reason)
    {
        _startLinkedVisibilityRequested = false;
        _motionCoordinator.MarkInteractive(reason);
        if (_motionCoordinator.IsInteractive)
        {
            _startMenuMonitor.SetLauncherInteractive(true);
        }
    }

    private void StartMenuMonitor_SnapshotChanged(object? sender, EventArgs args) =>
        SynchronizeWithStartMenu();

    private void StartMenuMonitor_ReadyChanged(object? sender, EventArgs args) =>
        _logger.Write($"[StartMenuMonitor] ready={_startMenuMonitor.IsReady}");

    private void StartMenuMonitor_StartConfirmationExpired(object? sender, EventArgs args)
    {
        _startLinkedVisibilityRequested = false;
        _motionCoordinator.CancelStartConfirmation("start-confirmation-timeout");
        _logger.Write("[Launcher] start-confirmation=expired action=none");
    }

    private void AudioOutputService_StateChanged(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.RefreshAudioOutputState();
        });
    }

    private void UISettings_AnimationsEnabledChanged(
        UISettings sender,
        UISettingsAnimationsEnabledChangedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _motionService.AnimationsEnabled = sender.AnimationsEnabled;
            _logger.Write($"[Motion] animations-enabled={sender.AnimationsEnabled}");
            if (!sender.AnimationsEnabled && _motionCoordinator.State == LauncherMotionState.Exiting)
            {
                _motionService.SetHidden(GetEntranceTranslation(
                    _lastPlacementStartSnapshot is { IsVisible: true }));
                CompleteHide("animations-disabled");
            }
            else if (!sender.AnimationsEnabled && _motionCoordinator.IsWindowVisible)
            {
                _motionService.SetVisible();
                _motionCoordinator.CompleteEntrance();
            }
        });
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not nameof(MainWindowViewModel.AssumePhonePanelVisible)
            and not nameof(MainWindowViewModel.LayoutMode))
        {
            return;
        }

        _logger.Write(
            args.PropertyName == nameof(MainWindowViewModel.AssumePhonePanelVisible)
                ? $"[PhonePanelSetting] source=launcher value={(ViewModel.AssumePhonePanelVisible ? "on" : "off")}"
                : $"[LayoutSetting] source=settings value={ViewModel.LayoutMode}");

        if (_isVisible && !_motionService.IsRunning)
        {
            _ = PositionWindow(_lastPlacementStartSnapshot);
        }
    }

    private async void ViewModel_LauncherItemExecuted(
        object? sender,
        LauncherItemExecutedEventArgs args)
    {
        if (args.Result.IsSuccess && args.ShouldCloseOnSuccess)
        {
            RequestExit("action-executed");
            return;
        }

        if (args.Result.IsSuccess)
        {
            return;
        }

        if (_isActionErrorDialogOpen || RootBorder.XamlRoot is null)
        {
            return;
        }

        _isActionErrorDialogOpen = true;
        try
        {
            ContentDialog dialog = new()
            {
                Title = "操作を実行できませんでした",
                Content = args.Result.ErrorMessage,
                CloseButtonText = "閉じる",
                XamlRoot = RootBorder.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isActionErrorDialogOpen = false;
        }
    }

    private void RootBorder_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            RequestExit("escape-key-xaml");
        }
    }

    private void WindowInteropService_EscapePressed(object? sender, EventArgs args) =>
        RequestExit("escape-key-win32");

    private void BeginActivationVerification(string reason)
    {
        _activationRetryTimer.Stop();
        _activationAttemptCount = 0;
        _activationReason = reason;
        TryActivateLauncher();
    }

    private void ActivationRetryTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        TryActivateLauncher();
    }

    private void TryActivateLauncher()
    {
        if (!_isVisible || !_motionCoordinator.IsWindowVisible)
        {
            _activationRetryTimer.Stop();
            return;
        }

        if (_windowInteropService.IsForeground(_windowHandle))
        {
            _activationRetryTimer.Stop();
            _launcherIsActivated = true;
            MarkLauncherInteractive("activation-confirmed");
            _logger.WriteDetailed(
                $"[Launcher] activation-result=success reason={_activationReason} " +
                $"attempts={_activationAttemptCount}");
            return;
        }

        _activationAttemptCount++;
        bool activated = _windowInteropService.TryActivate(_windowHandle);
        if (activated)
        {
            _activationRetryTimer.Stop();
            _launcherIsActivated = true;
            MarkLauncherInteractive("activation-retry");
            _logger.WriteDetailed(
                $"[Launcher] activation-result=success reason={_activationReason} " +
                $"attempts={_activationAttemptCount}");
            return;
        }

        if (_activationAttemptCount >= MaximumActivationAttempts)
        {
            _logger.Write(
                $"[Launcher] activation-result=failed reason={_activationReason} " +
                $"attempts={_activationAttemptCount}");
            return;
        }

        _activationRetryTimer.Start();
    }

    private void WindowInteropService_DisplayEnvironmentChanged(
        object? sender,
        DisplayEnvironmentChangedEventArgs args)
    {
        _pendingEnvironmentChangeReason = args.Reason;
        _environmentCheckTimer.Stop();
        _environmentCheckTimer.Start();
    }

    private void EnvironmentCheckTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        _environmentInformationService.LogIfChanged(_pendingEnvironmentChangeReason);
    }

    private float GetEntranceTranslation(bool startLinked)
    {
        if (!startLinked)
        {
            return 24;
        }

        if (RootBorder.ActualHeight > 1)
        {
            return (float)RootBorder.ActualHeight;
        }

        return (float)_placementService.ConvertPhysicalPixelsToEffective(
            _targetWindowRect.Height,
            _placementDpiPoint);
    }

    private static double ElapsedMilliseconds(long startedTimestamp) =>
        startedTimestamp == 0
            ? -1
            : Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    private static double ElapsedBetweenMilliseconds(long start, long end) =>
        start == 0 || end == 0 || end < start
            ? -1
            : Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _motionService.Completed -= MotionService_Completed;
        _motionService.Dispose();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            _uiSettings.AnimationsEnabledChanged -= UISettings_AnimationsEnabledChanged;
        }
        RootBorder.Loaded -= RootBorder_Loaded;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.LauncherItemExecuted -= ViewModel_LauncherItemExecuted;
        ViewModel.AudioOutputService.StateChanged -= AudioOutputService_StateChanged;
        _inputService.ManualToggleRequested -= InputService_ManualToggleRequested;
        _inputService.WindowsKeyReleasedAlone -= InputService_WindowsKeyReleasedAlone;
        _startMenuMonitor.SnapshotChanged -= StartMenuMonitor_SnapshotChanged;
        _startMenuMonitor.ReadyChanged -= StartMenuMonitor_ReadyChanged;
        _startMenuMonitor.StartConfirmationExpired -= StartMenuMonitor_StartConfirmationExpired;
        _startMenuMonitor.Dispose();
        _windowInteropService.EscapePressed -= WindowInteropService_EscapePressed;
        _windowInteropService.DisplayEnvironmentChanged -=
            WindowInteropService_DisplayEnvironmentChanged;
        _environmentCheckTimer.Stop();
        _environmentCheckTimer.Tick -= EnvironmentCheckTimer_Tick;
        _activationRetryTimer.Stop();
        _activationRetryTimer.Tick -= ActivationRetryTimer_Tick;
        _windowInteropService.Dispose();
        _inputService.Dispose();
        _logger.Write("[Application] input-monitor=stopped; window=closed");
    }
}
