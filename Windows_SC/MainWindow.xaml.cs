using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
    private const int MaximumPresentationAttempts = 5;
    private static readonly TimeSpan ResumeRecoveryDelay = TimeSpan.FromSeconds(1);

    private readonly IntPtr _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly DiagnosticLogger _logger;
    private readonly IStartMenuMonitor _startMenuMonitor;
    private readonly IGlobalInputService _inputService;
    private readonly ILauncherPlacementService _placementService;
    private readonly IWindowInteropService _windowInteropService;
    private readonly EnvironmentInformationService _environmentInformationService;
    private readonly DispatcherQueueTimer _environmentCheckTimer;
    private readonly DispatcherQueueTimer _resumeRecoveryTimer;
    private readonly DispatcherQueueTimer _activationRetryTimer;
    private readonly DispatcherQueueTimer _presentationVerificationTimer;
    private readonly DispatcherQueueTimer _actionFocusTransferTimer;
    private readonly ILauncherMotionService _motionService;
    private readonly LauncherMotionCoordinator _motionCoordinator;
    private readonly StartLinkedPresentationRecoveryGuard _presentationRecoveryGuard = new();
    private readonly ShortcutKeyExecutionCoordinator _shortcutKeyExecutionCoordinator;
    private readonly SequentialAsyncQueue<string> _actionErrorQueue;
    private readonly UISettings _uiSettings;
    private bool _isVisible;
    private bool _isInitialized;
    private bool _shutdownResourcesReleased;
    private bool _launcherIsActivated;
    private bool? _lastLoggedLauncherFocus;
    private bool? _lastLoggedStartMenuVisibility;
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
    private int _presentationAttemptCount;
    private string _presentationReason = "manual";
    private bool _pendingActionFocusTransfer;
    private bool _preserveVisibilityWhileInactive;
    private TaskCompletionSource? _shortcutTargetPreparationCompletion;
    private readonly ConditionalWeakTable<Slider, SliderWheelState> _sliderWheelStates = new();

    internal MainWindowViewModel ViewModel { get; }

    internal MainWindow(
        MainWindowViewModel viewModel,
        DiagnosticLogger logger,
        IStartMenuMonitor startMenuMonitor,
        IGlobalInputService inputService,
        ILauncherPlacementService placementService,
        IWindowInteropService windowInteropService,
        EnvironmentInformationService environmentInformationService,
        ShortcutKeyExecutionCoordinator shortcutKeyExecutionCoordinator)
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
        _actionErrorQueue = new SequentialAsyncQueue<string>(
            ShowActionErrorAsync,
            LogActionErrorDialogFailure);
        _logger.WriteDetailed(
            $"[Launcher] action=initialize-bindings result=success " +
            $"items={LauncherItemsControl.Items.Count}");
        _startMenuMonitor = startMenuMonitor;
        _inputService = inputService;
        _placementService = placementService;
        _windowInteropService = windowInteropService;
        _environmentInformationService = environmentInformationService;
        _shortcutKeyExecutionCoordinator = shortcutKeyExecutionCoordinator;
        _shortcutKeyExecutionCoordinator.Attach(PrepareShortcutKeyTargetAsync);
        _environmentCheckTimer = DispatcherQueue.CreateTimer();
        _environmentCheckTimer.Interval = TimeSpan.FromMilliseconds(750);
        _environmentCheckTimer.IsRepeating = false;
        _environmentCheckTimer.Tick += EnvironmentCheckTimer_Tick;
        _resumeRecoveryTimer = DispatcherQueue.CreateTimer();
        _resumeRecoveryTimer.Interval = ResumeRecoveryDelay;
        _resumeRecoveryTimer.IsRepeating = false;
        _resumeRecoveryTimer.Tick += ResumeRecoveryTimer_Tick;
        _activationRetryTimer = DispatcherQueue.CreateTimer();
        _activationRetryTimer.Interval = TimeSpan.FromMilliseconds(50);
        _activationRetryTimer.IsRepeating = false;
        _activationRetryTimer.Tick += ActivationRetryTimer_Tick;
        _presentationVerificationTimer = DispatcherQueue.CreateTimer();
        _presentationVerificationTimer.Interval = TimeSpan.FromMilliseconds(50);
        _presentationVerificationTimer.IsRepeating = false;
        _presentationVerificationTimer.Tick += PresentationVerificationTimer_Tick;
        _actionFocusTransferTimer = DispatcherQueue.CreateTimer();
        _actionFocusTransferTimer.Interval = TimeSpan.FromSeconds(1);
        _actionFocusTransferTimer.IsRepeating = false;
        _actionFocusTransferTimer.Tick += ActionFocusTransferTimer_Tick;
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
        _inputService.ManualToggleRequested += InputService_ManualToggleRequested;
        _inputService.WindowsKeyReleasedAlone += InputService_WindowsKeyReleasedAlone;
        _inputService.Start(_windowHandle);
        _startMenuMonitor.SnapshotChanged += StartMenuMonitor_SnapshotChanged;
        _startMenuMonitor.ReadyChanged += StartMenuMonitor_ReadyChanged;
        _startMenuMonitor.StartConfirmationExpired += StartMenuMonitor_StartConfirmationExpired;
        _startMenuMonitor.Start();
        _logger.Write("[Application] action=start result=success");
        _environmentInformationService.LogIfChanged("startup", force: true);
        _logger.Write("[InputMonitor] action=start result=success");
        _logger.WriteDetailed(
            $"[InputMonitor] action=start result=success log=\"{_logger.LogFilePath}\"");
        _logger.Write(
            $"[Motion] action=initialize result=success engine=composition " +
            $"animations-enabled={_motionService.AnimationsEnabled} " +
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
            $"[WindowPlacement] action=position result=success " +
            $"position=({_targetWindowRect.X},{_targetWindowRect.Y}) " +
            $"size={_targetWindowRect.Width}x{_targetWindowRect.Height} " +
            $"display-work-area=({placement.WorkArea.X},{placement.WorkArea.Y}," +
            $"{placement.WorkArea.Width},{placement.WorkArea.Height}) " +
            $"phone-panel-mode={(ViewModel.AssumePhonePanelVisible ? "on" : "off")} " +
            $"phone-panel-detected={startMenuSnapshot?.IsPhonePanelVisible ?? false}");
        return true;
    }

    private void ToggleWindow()
    {
        MoveLauncherToCurrentVirtualDesktop("manual-hotkey");
        if (_motionCoordinator.State == LauncherMotionState.Exiting)
        {
            PrepareManualPresentation();
            ShowWindow(activate: true, "manual-reverse", null);
        }
        else if (_motionCoordinator.IsWindowVisible)
        {
            RequestExit("toggle-request");
        }
        else
        {
            PrepareManualPresentation();
            ShowWindow(activate: true, "manual-hotkey", null);
        }
    }

    internal void RequestManualShow()
    {
        MoveLauncherToCurrentVirtualDesktop("system-tray");
        if (_motionCoordinator.State == LauncherMotionState.Exiting)
        {
            PrepareManualPresentation();
            ShowWindow(activate: true, "tray-reverse", null);
        }
        else if (!_motionCoordinator.IsWindowVisible)
        {
            PrepareManualPresentation();
            ShowWindow(activate: true, "system-tray", null);
        }
    }

    private void PrepareManualPresentation()
    {
        _presentationRecoveryGuard.Reset();
        _startMenuMonitor.CancelPresentationRecovery();
    }

    private void InputService_ManualToggleRequested(object? sender, EventArgs args) =>
        ToggleWindow();

    private void InputService_WindowsKeyReleasedAlone(object? sender, EventArgs args)
    {
        // This event is raised from WH_KEYBOARD_LL. Queue all COM and UI work so
        // the hook returns before virtual-desktop or Start-menu processing begins.
        long releasedTimestamp = Stopwatch.GetTimestamp();
        if (!DispatcherQueue.TryEnqueue(
            () => HandleWindowsKeyReleasedAlone(releasedTimestamp)))
        {
            _logger.Write(
                "[InputMonitor] action=dispatch-windows-key result=failed");
        }
    }

    private void HandleWindowsKeyReleasedAlone(long releasedTimestamp)
    {
        _windowsKeyReleasedTimestamp = releasedTimestamp;
        _logger.WriteDetailed(
            "[InputMonitor] action=dispatch-windows-key result=success " +
            $"queue-ms={ElapsedMilliseconds(releasedTimestamp):F1}");
        MoveLauncherToCurrentVirtualDesktop("windows-key");

        // When the launcher is following an already-visible Start surface, a
        // standalone Windows key closes Start. Do not wait for the slower UIA
        // hidden-state confirmation before beginning the launcher exit.
        if (_motionCoordinator.State is LauncherMotionState.EnteringWithStart
            or LauncherMotionState.VisibleWithStart)
        {
            _startLinkedVisibilityRequested = false;
            _logger.Write(
                "[Launcher] action=exit-request result=success " +
                "reason=windows-key-close mode=immediate");
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
        MoveLauncherToCurrentVirtualDesktop(reason);
        bool reversingExit = _motionCoordinator.State == LauncherMotionState.Exiting;
        bool needsPlacement = !_isVisible
            || (reversingExit && startMenuSnapshot is { IsVisible: true, Bounds: not null });
        if (needsPlacement && !PositionWindow(startMenuSnapshot))
        {
            _motionCoordinator.CancelStartConfirmation("placement-failed");
            _logger.Write(
                $"[Launcher] action=show result=cancelled reason={reason} " +
                "placement-failed=true");
            return;
        }

        bool startLinked = startMenuSnapshot is { IsVisible: true, Bounds: not null };
        float entranceTranslation = GetEntranceTranslation(startLinked);
        _launcherIsActivated = false;
        _pendingActionFocusTransfer = false;
        _preserveVisibilityWhileInactive = false;
        _actionFocusTransferTimer.Stop();
        ViewModel.RefreshAudioOutputState();
        _ = ViewModel.AudioOutputService.RefreshAsync();
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

        bool keptTopmost = _windowInteropService.TryKeepTopmost(_windowHandle);
        _logger.Write(
            $"[Launcher] action=keep-topmost " +
            $"result={(keptTopmost ? "success" : "failed")} reason={reason}");
        BeginPresentationVerification(reason);

        if (activate)
        {
            BeginActivationVerification(reason);
        }
        else
        {
            _activationRetryTimer.Stop();
        }

        _logger.Write(
            $"[Launcher] action=show-request result=success reason={reason} " +
            $"activate={activate} " +
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
            _logger.Write(
                $"[Launcher] action=exit-request result=success reason={reason}");
            return;
        }

        CompleteHide(reason);
    }

    private void CompleteHide(string reason)
    {
        _activationRetryTimer.Stop();
        _presentationVerificationTimer.Stop();
        _appWindow.Hide();
        WindowPresentationState presentationState =
            _windowInteropService.GetPresentationState(_windowHandle);
        if (presentationState.IsVisible)
        {
            _ = _windowInteropService.TryHide(_windowHandle);
            presentationState = _windowInteropService.GetPresentationState(_windowHandle);
        }

        _isVisible = false;
        _launcherIsActivated = false;
        _pendingActionFocusTransfer = false;
        _preserveVisibilityWhileInactive = false;
        _actionFocusTransferTimer.Stop();
        _lastPlacementStartSnapshot = null;
        _motionCoordinator.CompleteExit(reason);
        _logger.Write(
            $"[Launcher] action=hide " +
            $"result={(presentationState.IsVisible ? "failed" : "success")} " +
            $"reason={reason} visible={presentationState.IsVisible.ToString().ToLowerInvariant()}");
        _startMenuMonitor.SetLauncherVisible(false);
        _shortcutTargetPreparationCompletion?.TrySetResult();

        StartMenuSnapshot latestSnapshot = _startMenuMonitor.Snapshot;
        bool startMenuIsVisible = latestSnapshot is { IsVisible: true, Bounds: not null };
        bool allowStartLinkedPresentation =
            _presentationRecoveryGuard.ShouldAllowPresentation(startMenuIsVisible);
        if (_startLinkedVisibilityRequested
            && allowStartLinkedPresentation)
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
            $"[Motion] action=complete result=success direction={args.Direction} " +
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
            $"[Launcher] action=entrance-complete result=success " +
            $"key-to-start-ms={ElapsedBetweenMilliseconds(_windowsKeyReleasedTimestamp, _startDetectedTimestamp):F1} " +
            $"start-to-request-ms={ElapsedBetweenMilliseconds(_startDetectedTimestamp, _showRequestedTimestamp):F1} " +
            $"request-to-complete-ms={ElapsedMilliseconds(_showRequestedTimestamp):F1} " +
            $"motion-ms={motionElapsed.TotalMilliseconds:F1}");
    }

    private void RootBorder_Loaded(object sender, RoutedEventArgs args)
    {
        _logger.WriteDetailed(
            $"[Launcher] action=load-visual result=success " +
            $"size={RootBorder.ActualWidth:F0}x{RootBorder.ActualHeight:F0} " +
            $"shortcuts={ViewModel.Shortcuts.Count} request-to-loaded-ms={ElapsedMilliseconds(_showRequestedTimestamp):F1}");
    }

    private void RootBorder_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_isVisible)
        {
            MarkLauncherInteractive("pointer-pressed");
        }
    }

    private void VolumeSlider_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        int delta = args.GetCurrentPoint(slider).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        SliderWheelState wheelState = _sliderWheelStates.GetValue(
            slider,
            static _ => new SliderWheelState());
        int accumulatedDelta = wheelState.Delta + delta;
        int notchCount = accumulatedDelta / 120;
        wheelState.Delta = accumulatedDelta - (notchCount * 120);
        args.Handled = true;

        if (notchCount == 0)
        {
            return;
        }

        slider.Value = Math.Clamp(
            slider.Value + (notchCount * 2),
            slider.Minimum,
            slider.Maximum);
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        _launcherIsActivated = args.WindowActivationState != WindowActivationState.Deactivated;
        _logger.WriteDetailed(
            $"[Launcher] action=activation-change result=success " +
            $"state={args.WindowActivationState}");

        if (!_isVisible)
        {
            return;
        }

        if (_launcherIsActivated)
        {
            _pendingActionFocusTransfer = false;
            _preserveVisibilityWhileInactive = false;
            _actionFocusTransferTimer.Stop();
            _activationRetryTimer.Stop();
            MarkLauncherInteractive("window-activated");
        }
        else if (_pendingActionFocusTransfer || _preserveVisibilityWhileInactive)
        {
            _pendingActionFocusTransfer = false;
            _preserveVisibilityWhileInactive = true;
            _actionFocusTransferTimer.Stop();
            _logger.WriteDetailed(
                "[Launcher] action=handle-deactivation result=success " +
                "reason=action-focus-transfer behavior=keep-visible");
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
        StartMenuSnapshot snapshot = _startMenuMonitor.Snapshot;
        bool startMenuIsVisible = snapshot.IsVisible && snapshot.Bounds is not null;
        bool presentationWasBlocked = _presentationRecoveryGuard.IsBlocked;
        bool allowStartLinkedPresentation =
            _presentationRecoveryGuard.ShouldAllowPresentation(startMenuIsVisible);
        if (presentationWasBlocked && !startMenuIsVisible)
        {
            _logger.Write(
                "[Launcher] action=complete-presentation-recovery result=success " +
                "reason=start-menu-hidden");
        }

        if (startMenuIsVisible && !allowStartLinkedPresentation)
        {
            _startLinkedVisibilityRequested = false;
            _logger.WriteDetailed(
                "[Launcher] action=synchronize-visibility result=skipped " +
                "reason=presentation-recovery-pending");
            return;
        }

        if (startMenuIsVisible)
        {
            MoveLauncherToCurrentVirtualDesktop("start-menu-snapshot");
        }

        bool launcherHasFocus = _launcherIsActivated
            || _windowInteropService.IsForeground(_windowHandle);

        if (_lastLoggedLauncherFocus != launcherHasFocus
            || _lastLoggedStartMenuVisibility != startMenuIsVisible)
        {
            _logger.WriteDetailed(
                $"[Launcher] action=synchronize-visibility result=success " +
                $"state={_motionCoordinator.State} launcher-focus={launcherHasFocus} " +
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
                $"[Launcher] action=confirm-start result=success " +
                $"key-to-start-ms={ElapsedMilliseconds(_windowsKeyReleasedTimestamp):F1}");
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

    private void MoveLauncherToCurrentVirtualDesktop(string reason)
    {
        VirtualDesktopMoveResult result =
            _windowInteropService.MoveToCurrentVirtualDesktop(_windowHandle);
        if (result.Status == VirtualDesktopMoveStatus.AlreadyCurrent)
        {
            return;
        }

        if (result.Status != VirtualDesktopMoveStatus.Moved)
        {
            _logger.Write(
                $"[VirtualDesktop] action=move result=failed " +
                $"state={result.Status.ToString().ToLowerInvariant()} " +
                $"reason={reason} hresult=0x{result.HResult:X8}");
            return;
        }

        _activationRetryTimer.Stop();
        _presentationVerificationTimer.Stop();
        _motionService.SetHidden(GetEntranceTranslation(startLinked: true));
        _appWindow.Hide();
        _isVisible = false;
        _launcherIsActivated = false;
        _pendingActionFocusTransfer = false;
        _preserveVisibilityWhileInactive = false;
        _actionFocusTransferTimer.Stop();
        _startLinkedVisibilityRequested = false;
        _lastPlacementStartSnapshot = null;
        _lastLoggedLauncherFocus = null;
        _lastLoggedStartMenuVisibility = null;
        _motionCoordinator.ResetHidden("virtual-desktop-move");
        _startMenuMonitor.SetLauncherVisible(false);
        _logger.Write(
            $"[VirtualDesktop] action=move result=success reason={reason}");
    }

    private void StartMenuMonitor_SnapshotChanged(object? sender, EventArgs args) =>
        SynchronizeWithStartMenu();

    private void StartMenuMonitor_ReadyChanged(object? sender, EventArgs args) =>
        _logger.Write(
            $"[StartMenu] action=monitor-ready result=success ready={_startMenuMonitor.IsReady}");

    private void StartMenuMonitor_StartConfirmationExpired(object? sender, EventArgs args)
    {
        _startLinkedVisibilityRequested = false;
        _motionCoordinator.CancelStartConfirmation("start-confirmation-timeout");
        _logger.Write(
            "[Launcher] action=confirm-start result=cancelled reason=timeout");
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
            _logger.Write(
                $"[Motion] action=configure result=success " +
                $"animations-enabled={sender.AnimationsEnabled}");
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
                ? $"[Settings] action=update-phone-panel result=success source=launcher " +
                  $"value={(ViewModel.AssumePhonePanelVisible ? "on" : "off")}"
                : $"[Settings] action=update-layout result=success source=settings " +
                  $"value={ViewModel.LayoutMode}");

        if (_isVisible && !_motionService.IsRunning)
        {
            _ = PositionWindow(_lastPlacementStartSnapshot);
        }
    }

    private void ViewModel_LauncherItemExecuted(
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
            if (args.MayTransferFocus)
            {
                _pendingActionFocusTransfer = true;
                _actionFocusTransferTimer.Stop();
                _actionFocusTransferTimer.Start();
                _logger.WriteDetailed(
                    "[Launcher] action=wait-focus-transfer result=success timeout-ms=1000");
            }
            return;
        }

        _actionErrorQueue.Enqueue(args.Result.ErrorMessage);
    }

    private async Task ShowActionErrorAsync(string errorMessage)
    {
        if (RootBorder.XamlRoot is null)
        {
            _logger.Write(
                "[Launcher] action=show-action-error result=skipped reason=no-xaml-root");
            return;
        }

        ContentDialog dialog = new()
        {
            Title = "操作を実行できませんでした",
            Content = errorMessage,
            CloseButtonText = "閉じる",
            XamlRoot = RootBorder.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void LogActionErrorDialogFailure(Exception exception)
    {
        _logger.Write(
            $"[Launcher] action=show-action-error result=failed " +
            $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        _logger.WriteDetailed(
            $"[Launcher] action=show-action-error result=failed " +
            $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8} " +
            $"message=\"{LogValue.Normalize(exception.Message)}\"");
    }

    private async Task PrepareShortcutKeyTargetAsync(CancellationToken cancellationToken)
    {
        if (!_isVisible)
        {
            return;
        }

        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _shortcutTargetPreparationCompletion = completion;
        _startLinkedVisibilityRequested = false;
        RequestExit("shortcut-key-target");

        if (!_isVisible)
        {
            completion.TrySetResult();
        }

        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellationToken);
            _logger.WriteDetailed(
                "[ShortcutKey] action=prepare-target result=success launcher-hidden=true");
        }
        catch (TimeoutException)
        {
            _logger.Write(
                "[ShortcutKey] action=prepare-target result=failed reason=hide-timeout");
            throw;
        }
        finally
        {
            if (_shortcutTargetPreparationCompletion == completion)
            {
                _shortcutTargetPreparationCompletion = null;
            }
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

    private void BeginPresentationVerification(string reason)
    {
        _presentationVerificationTimer.Stop();
        _presentationAttemptCount = 0;
        _presentationReason = reason;
        VerifyLauncherPresentation();
    }

    private void PresentationVerificationTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        VerifyLauncherPresentation();
    }

    private void VerifyLauncherPresentation()
    {
        if (!_isVisible || !_motionCoordinator.IsWindowVisible)
        {
            _presentationVerificationTimer.Stop();
            return;
        }

        WindowPresentationState state =
            _windowInteropService.GetPresentationState(_windowHandle);
        if (state.IsPresented)
        {
            _presentationVerificationTimer.Stop();
            _logger.Write(
                $"[Launcher] action=verify-presentation result=success " +
                $"reason={_presentationReason} attempts={_presentationAttemptCount} " +
                $"visible=true topmost=true " +
                $"cloaked={FormatCloakedState(state)}");
            return;
        }

        if (_presentationAttemptCount >= MaximumPresentationAttempts)
        {
            HandlePresentationFailure(state);
            return;
        }

        _presentationAttemptCount++;
        // AppWindow's presenter can still report AlwaysOnTop while the initial
        // non-activating HWND presentation has lost WS_EX_TOPMOST. Reapply the
        // presenter policy once, then retain the native non-activating retries.
        if (_presentationAttemptCount == 1 && !state.IsTopmost
            && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            try
            {
                presenter.IsAlwaysOnTop = false;
                presenter.IsAlwaysOnTop = true;
                _logger.Write(
                    "[Launcher] action=reapply-topmost-policy result=success");
            }
            catch (Exception exception)
            {
                _logger.Write(
                    $"[Launcher] action=reapply-topmost-policy result=failed " +
                    $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            }
        }
        _ = _windowInteropService.TryKeepTopmost(_windowHandle);
        // Observe every retry, including the final one, on the next UI tick.
        // Previously the last attempt was declared failed using its BEFORE state.
        _presentationVerificationTimer.Start();
    }

    private void HandlePresentationFailure(WindowPresentationState state)
    {
        _logger.Write(
            $"[Launcher] action=verify-presentation result=failed " +
            $"reason={_presentationReason} attempts={_presentationAttemptCount} " +
            $"visible={state.IsVisible.ToString().ToLowerInvariant()} " +
            $"topmost={state.IsTopmost.ToString().ToLowerInvariant()} " +
            $"cloaked={FormatCloakedState(state)}");
        if (_startLinkedVisibilityRequested)
        {
            _presentationRecoveryGuard.BlockUntilStartMenuHidden();
            _startLinkedVisibilityRequested = false;
            _startMenuMonitor.BeginPresentationRecovery();
        }
        RequestExit("presentation-verification-failed");
    }

    private static string FormatCloakedState(WindowPresentationState state) =>
        state.IsCloakingStateKnown
            ? state.IsCloaked.ToString().ToLowerInvariant()
            : "unknown";

    private void ActionFocusTransferTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        if (!_pendingActionFocusTransfer)
        {
            return;
        }

        _pendingActionFocusTransfer = false;
        _logger.WriteDetailed(
            "[Launcher] action=wait-focus-transfer result=cancelled " +
            "reason=timeout fallback=normal-light-dismiss");
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
                $"[Launcher] action=activate result=success reason={_activationReason} " +
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
                $"[Launcher] action=activate result=success reason={_activationReason} " +
                $"attempts={_activationAttemptCount}");
            return;
        }

        if (_activationAttemptCount >= MaximumActivationAttempts)
        {
            _logger.Write(
                $"[Launcher] action=activate result=failed reason={_activationReason} " +
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
        if (args.Reason == "resume")
        {
            _resumeRecoveryTimer.Stop();
            _resumeRecoveryTimer.Start();
            _logger.Write(
                "[Recovery] action=schedule result=success reason=resume delay-ms=1000");
        }
    }

    private void ResumeRecoveryTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        _logger.RotateLogs("resume");
        bool launcherRecovered = TryResetLauncherAfterResume();
        bool inputRecovered = _inputService.RecoverAfterResume();
        bool startMenuRecovered = _startMenuMonitor.RecoverAfterResume();
        bool succeeded = launcherRecovered && inputRecovered && startMenuRecovered;
        _logger.Write(
            $"[Recovery] action=resume result={(succeeded ? "success" : "failed")} " +
            $"launcher={(launcherRecovered ? "success" : "failed")} " +
            $"input={(inputRecovered ? "success" : "failed")} " +
            $"start-menu={(startMenuRecovered ? "success" : "failed")}");
    }

    private bool TryResetLauncherAfterResume()
    {
        try
        {
            _activationRetryTimer.Stop();
            _presentationVerificationTimer.Stop();
            _actionFocusTransferTimer.Stop();
            _motionService.SetHidden(GetEntranceTranslation(startLinked: true));
            _appWindow.Hide();
            WindowPresentationState state =
                _windowInteropService.GetPresentationState(_windowHandle);
            if (state.IsVisible)
            {
                _ = _windowInteropService.TryHide(_windowHandle);
                state = _windowInteropService.GetPresentationState(_windowHandle);
            }

            _isVisible = false;
            _launcherIsActivated = false;
            _pendingActionFocusTransfer = false;
            _preserveVisibilityWhileInactive = false;
            _startLinkedVisibilityRequested = false;
            _lastPlacementStartSnapshot = null;
            _lastLoggedLauncherFocus = null;
            _lastLoggedStartMenuVisibility = null;
            _windowsKeyReleasedTimestamp = 0;
            _startDetectedTimestamp = 0;
            _showRequestedTimestamp = 0;
            _presentationRecoveryGuard.Reset();
            _startMenuMonitor.CancelPresentationRecovery();
            _motionCoordinator.ResetHidden("resume-recovery");
            _startMenuMonitor.SetLauncherVisible(false);
            _shortcutTargetPreparationCompletion?.TrySetResult();

            bool hidden = !state.IsVisible;
            _logger.Write(
                $"[Launcher] action=recover result={(hidden ? "success" : "failed")} " +
                $"reason=resume visible={state.IsVisible.ToString().ToLowerInvariant()}");
            return hidden;
        }
        catch (Exception exception)
        {
            _logger.Write(
                $"[Launcher] action=recover result=failed reason=resume " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            return false;
        }
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

    internal void ReleaseShutdownResources(Action<string, Action> release)
    {
        if (_shutdownResourcesReleased)
        {
            return;
        }
        _shutdownResourcesReleased = true;
        _logger.Write("[Application] action=release-window-resources result=success phase=begin");
        _shortcutKeyExecutionCoordinator.Detach(PrepareShortcutKeyTargetAsync);
        _shortcutTargetPreparationCompletion?.TrySetCanceled();
        _shortcutTargetPreparationCompletion = null;
        _motionService.Completed -= MotionService_Completed;
        release("motion", _motionService.Dispose);
        release("ui-settings", () =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                _uiSettings.AnimationsEnabledChanged -= UISettings_AnimationsEnabledChanged;
            }
        });
        RootBorder.Loaded -= RootBorder_Loaded;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.LauncherItemExecuted -= ViewModel_LauncherItemExecuted;
        ViewModel.AudioOutputService.StateChanged -= AudioOutputService_StateChanged;
        _inputService.ManualToggleRequested -= InputService_ManualToggleRequested;
        _inputService.WindowsKeyReleasedAlone -= InputService_WindowsKeyReleasedAlone;
        _startMenuMonitor.SnapshotChanged -= StartMenuMonitor_SnapshotChanged;
        _startMenuMonitor.ReadyChanged -= StartMenuMonitor_ReadyChanged;
        _startMenuMonitor.StartConfirmationExpired -= StartMenuMonitor_StartConfirmationExpired;
        release("start-menu", _startMenuMonitor.Dispose);
        _windowInteropService.EscapePressed -= WindowInteropService_EscapePressed;
        _windowInteropService.DisplayEnvironmentChanged -=
            WindowInteropService_DisplayEnvironmentChanged;
        _environmentCheckTimer.Stop();
        _environmentCheckTimer.Tick -= EnvironmentCheckTimer_Tick;
        _resumeRecoveryTimer.Stop();
        _resumeRecoveryTimer.Tick -= ResumeRecoveryTimer_Tick;
        _activationRetryTimer.Stop();
        _activationRetryTimer.Tick -= ActivationRetryTimer_Tick;
        _presentationVerificationTimer.Stop();
        _presentationVerificationTimer.Tick -= PresentationVerificationTimer_Tick;
        _actionFocusTransferTimer.Stop();
        _actionFocusTransferTimer.Tick -= ActionFocusTransferTimer_Tick;
        release("window-interop", _windowInteropService.Dispose);
        release("input", _inputService.Dispose);
        _logger.Write("[InputMonitor] action=stop result=success");
        _logger.Write("[Application] action=window-close result=success");
    }

    private sealed class SliderWheelState
    {
        public int Delta { get; set; }
    }
}
