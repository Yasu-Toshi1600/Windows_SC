using Microsoft.UI.Dispatching;
using System;

namespace Windows_SC.Services;

internal sealed class HybridStartMenuMonitor : IStartMenuMonitor
{
    private static readonly TimeSpan FastMonitoringDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan FastMonitoringInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan VisibleFallbackInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan IdleFallbackInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan InitializationFallbackInterval = TimeSpan.FromMilliseconds(125);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DiagnosticLogger _logger;
    private readonly bool _enableUiAutomation;
    private readonly Win32StartMenuEventMonitor _winEvents;
    private bool _winEventScanQueued;
    private long _winEventGeneration;
    private UiAutomationStartMenuInspector _uiAutomationInspector;
    private readonly StartMenuWindowInspector _recoveryWindowInspector;
    private bool _inspectorRestartPending;
    private StartMenuSnapshot _recoverySnapshot = StartMenuSnapshot.Hidden;
    private DateTimeOffset _nextRestartAttempt;
    private readonly DispatcherQueueTimer _fallbackTimer;
    private DateTimeOffset _fastMonitoringUntil;
    private bool _launcherIsVisible;
    private bool _launcherIsInteractive;
    private bool _isStarted;
    private bool _isDisposed;
    private bool _awaitingStartConfirmation;
    private bool _presentationRecoveryPending;

    public HybridStartMenuMonitor(DispatcherQueue dispatcherQueue, DiagnosticLogger logger,
        bool enableUiAutomation = false)
    {
        _dispatcherQueue = dispatcherQueue;
        _logger = logger;
        _enableUiAutomation = enableUiAutomation;
        _winEvents = new Win32StartMenuEventMonitor(logger, WinEventChanged);
        _uiAutomationInspector = new UiAutomationStartMenuInspector(logger, _enableUiAutomation);
        _recoveryWindowInspector = new StartMenuWindowInspector(logger);

        _fallbackTimer = dispatcherQueue.CreateTimer();
        _fallbackTimer.Interval = FastMonitoringInterval;
        _fallbackTimer.IsRepeating = true;
        _fallbackTimer.Tick += FallbackTimer_Tick;
    }

    public event EventHandler? SnapshotChanged;

    public event EventHandler? ReadyChanged;

    public event EventHandler? StartConfirmationExpired;

    public StartMenuSnapshot Snapshot => _inspectorRestartPending
        ? _recoverySnapshot : _uiAutomationInspector.Snapshot;

    public bool IsReady => !_inspectorRestartPending && _uiAutomationInspector.IsReady;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isStarted)
        {
            return;
        }

        AttachInspectorEvents();
        _isStarted = true;
        _uiAutomationInspector.Start();
        _winEvents.Start();
        UpdateFallbackMonitoring();
        _logger.Write(
            $"[StartMenu] action=start-monitor result=success mode={_uiAutomationInspector.DetectionMode}");
    }

    public bool RecoverAfterResume()
    {
        if (!_isStarted || _isDisposed)
        {
            _logger.Write(
                "[StartMenu] action=recover-monitor result=skipped reason=not-started");
            return false;
        }

        _fallbackTimer.Stop();
        ++_winEventGeneration;
        _winEventScanQueued = false;
        _winEvents.Restart();
        if (!_inspectorRestartPending)
        {
            DetachInspectorEvents();
            _uiAutomationInspector.Dispose();
            _inspectorRestartPending = true;
        }
        _recoverySnapshot = StartMenuSnapshot.Hidden;
        _awaitingStartConfirmation = false;
        _presentationRecoveryPending = false;
        _fastMonitoringUntil = DateTimeOffset.MinValue;
        _launcherIsVisible = false;
        _launcherIsInteractive = false;

        TryRestartInspector();
        UpdateFallbackMonitoring();
        ReadyChanged?.Invoke(this, EventArgs.Empty);
        _logger.Write(
            "[StartMenu] action=recover-monitor result=success reason=resume " +
            $"mode={(_inspectorRestartPending ? "win32-pending-worker-stop" : _uiAutomationInspector.DetectionMode)}");
        return true;
    }

    public void BeginPresentationRecovery()
    {
        if (!_isStarted || _isDisposed)
        {
            return;
        }

        _awaitingStartConfirmation = false;
        _fastMonitoringUntil = DateTimeOffset.MinValue;
        _presentationRecoveryPending = true;
        _uiAutomationInspector.SetMonitoringActive(
            true,
            StartMenuScanTrigger.VisibleFallback);
        SetFallbackInterval(VisibleFallbackInterval);
        _logger.Write(
            "[StartMenu] action=begin-presentation-recovery result=success " +
            "condition=wait-for-hidden");
    }

    public void CancelPresentationRecovery()
    {
        if (_isDisposed || !_presentationRecoveryPending)
        {
            return;
        }

        _presentationRecoveryPending = false;
        UpdateFallbackMonitoring();
        _logger.WriteDetailed(
            "[StartMenu] action=cancel-presentation-recovery result=success");
    }

    public void NotifyWindowsKeyReleased()
    {
        if (!_isStarted || _isDisposed)
        {
            return;
        }

        _logger.Write(
            "[StartMenu] action=begin-confirmation result=success " +
            "source=windows-key fallback-window-ms=1500");
        _awaitingStartConfirmation = true;
        _fastMonitoringUntil = DateTimeOffset.UtcNow + FastMonitoringDuration;
        _uiAutomationInspector.SetMonitoringActive(
            true,
            StartMenuScanTrigger.WindowsKeyImmediate);
        _fallbackTimer.Interval = FastMonitoringInterval;
        _fallbackTimer.Start();
    }

    public void NotifyStartMenuClosing()
    {
        if (!_isStarted || _isDisposed)
        {
            return;
        }

        _awaitingStartConfirmation = false;
        _fastMonitoringUntil = DateTimeOffset.MinValue;
        _fallbackTimer.Stop();
        _uiAutomationInspector.SetMonitoringActive(false);
        _uiAutomationInspector.AssumeHidden();
        if (_inspectorRestartPending) UpdateRecoverySnapshot(StartMenuSnapshot.Hidden);
        _logger.Write(
            "[StartMenu] action=assume-hidden result=success source=windows-key-close");
    }

    public void SetLauncherVisible(bool isVisible)
    {
        if (_isDisposed) return;
        _launcherIsVisible = isVisible;
        if (!isVisible)
        {
            _launcherIsInteractive = false;
        }

        UpdateFallbackMonitoring();
    }

    public void SetLauncherInteractive(bool isInteractive)
    {
        if (_isDisposed) return;
        _launcherIsInteractive = isInteractive;
        if (isInteractive)
        {
            _presentationRecoveryPending = false;
            _awaitingStartConfirmation = false;
            _uiAutomationInspector.AssumeHidden();
        }

        UpdateFallbackMonitoring();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        ++_winEventGeneration;
        _winEvents.Dispose();
        _fallbackTimer.Stop();
        _fallbackTimer.Tick -= FallbackTimer_Tick;
        DetachInspectorEvents();
        _uiAutomationInspector.Dispose();
        _logger.Write("[StartMenu] action=stop-monitor result=success");
    }

    private void FallbackTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_isDisposed) return;
        _winEvents.RefreshTargets();
        TryRestartInspector();
        if (_inspectorRestartPending)
        {
            // Keep Windows-key and Start-button detection available while an old
            // native UIA call finishes. Never add another generation meanwhile.
            bool visible = _recoveryWindowInspector.TryGetStartMenuBounds(null, out var bounds);
            UpdateRecoverySnapshot(visible
                ? new StartMenuSnapshot(true, bounds, false, null)
                : StartMenuSnapshot.Hidden);
            UpdateFallbackMonitoring();
            return;
        }
        if (!_presentationRecoveryPending
            && !_awaitingStartConfirmation
            && !_launcherIsVisible)
        {
            _uiAutomationInspector.RequestScanIfStartMenuWindowVisible(
                GetFallbackScanTrigger());
        }
        else
        {
            _uiAutomationInspector.RequestScan(GetFallbackScanTrigger());
        }

        UpdateFallbackMonitoring();
    }

    private StartMenuScanTrigger GetFallbackScanTrigger()
    {
        if (_presentationRecoveryPending)
        {
            return StartMenuScanTrigger.VisibleFallback;
        }

        if (_awaitingStartConfirmation)
        {
            return StartMenuScanTrigger.WindowsKeyFallback;
        }

        if (_launcherIsVisible)
        {
            return StartMenuScanTrigger.VisibleFallback;
        }

        return IsReady
            ? StartMenuScanTrigger.IdleFallback
            : StartMenuScanTrigger.InitializationFallback;
    }

    private void WinEventChanged()
    {
        if (_isDisposed || !_isStarted || _launcherIsInteractive || _winEventScanQueued) return;
        _winEventScanQueued = true;
        long generation = _winEventGeneration;
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || generation != _winEventGeneration) return;
            _winEventScanQueued = false;
            if (_launcherIsInteractive || _inspectorRestartPending) return;
            _uiAutomationInspector.RequestScan(StartMenuScanTrigger.Win32Event);
        })) _winEventScanQueued = false;
    }

    private void UpdateFallbackMonitoring()
    {
        if (!_isStarted || _isDisposed) return;
        if (_launcherIsInteractive)
        {
            _fallbackTimer.Stop();
            _uiAutomationInspector.SetMonitoringActive(false);
            return;
        }

        if (_presentationRecoveryPending)
        {
            _uiAutomationInspector.SetMonitoringActive(true);
            SetFallbackInterval(VisibleFallbackInterval);
            return;
        }

        if (DateTimeOffset.UtcNow < _fastMonitoringUntil)
        {
            SetFallbackInterval(FastMonitoringInterval);
            return;
        }

        if (_launcherIsVisible)
        {
            _uiAutomationInspector.SetMonitoringActive(true);
            SetFallbackInterval(VisibleFallbackInterval);
            return;
        }

        _uiAutomationInspector.SetMonitoringActive(false);
        if (_awaitingStartConfirmation && !Snapshot.IsVisible)
        {
            _awaitingStartConfirmation = false;
            StartConfirmationExpired?.Invoke(this, EventArgs.Empty);
        }

        if (!IsReady)
        {
            SetFallbackInterval(InitializationFallbackInterval);
            return;
        }

        // The default Win32-only mode relies on this bounded idle check for
        // Start-button clicks, independently of the Windows-key path.
        SetFallbackInterval(IdleFallbackInterval);
    }

    private void SetFallbackInterval(TimeSpan interval)
    {
        if (_isDisposed) return;
        if (_fallbackTimer.Interval != interval)
        {
            _fallbackTimer.Interval = interval;
        }

        if (!_fallbackTimer.IsRunning)
        {
            _fallbackTimer.Start();
        }
    }

    private void UiAutomationInspector_SnapshotChanged(object? sender, EventArgs args)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || _inspectorRestartPending
                || !ReferenceEquals(sender, _uiAutomationInspector)) return;
            bool isVisible = _uiAutomationInspector.Snapshot.IsVisible;
            if (isVisible)
            {
                _awaitingStartConfirmation = false;
            }

            if (_presentationRecoveryPending && !isVisible)
            {
                _presentationRecoveryPending = false;
                _logger.Write(
                    "[StartMenu] action=complete-presentation-recovery result=success " +
                    "reason=start-menu-hidden");
                UpdateFallbackMonitoring();
            }

            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void UiAutomationInspector_ReadyChanged(object? sender, EventArgs args) =>
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isDisposed || _inspectorRestartPending
                || !ReferenceEquals(sender, _uiAutomationInspector)) return;
            UpdateFallbackMonitoring();
            ReadyChanged?.Invoke(this, EventArgs.Empty);
        });

    private void TryRestartInspector()
    {
        if (_isDisposed || !_inspectorRestartPending || !_uiAutomationInspector.CanRestart
            || DateTimeOffset.UtcNow < _nextRestartAttempt) return;
        _uiAutomationInspector = new UiAutomationStartMenuInspector(_logger, _enableUiAutomation);
        AttachInspectorEvents();
        try
        {
            _uiAutomationInspector.Start();
            _inspectorRestartPending = false;
            _logger.Write($"[StartMenu] action=restart-inspector result=success previous-workers=stopped mode={_uiAutomationInspector.DetectionMode}");
        }
        catch (Exception exception)
        {
            DetachInspectorEvents();
            _uiAutomationInspector.Dispose();
            _nextRestartAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
            _logger.Write(
                $"[StartMenu] action=restart-inspector result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
    }

    private void UpdateRecoverySnapshot(StartMenuSnapshot snapshot)
    {
        if (_isDisposed || snapshot == _recoverySnapshot) return;
        _recoverySnapshot = snapshot;
        if (snapshot.IsVisible) _awaitingStartConfirmation = false;
        if (_presentationRecoveryPending && !snapshot.IsVisible)
            _presentationRecoveryPending = false;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AttachInspectorEvents()
    {
        _uiAutomationInspector.SnapshotChanged += UiAutomationInspector_SnapshotChanged;
        _uiAutomationInspector.ReadyChanged += UiAutomationInspector_ReadyChanged;
    }

    private void DetachInspectorEvents()
    {
        _uiAutomationInspector.SnapshotChanged -= UiAutomationInspector_SnapshotChanged;
        _uiAutomationInspector.ReadyChanged -= UiAutomationInspector_ReadyChanged;
    }
}
