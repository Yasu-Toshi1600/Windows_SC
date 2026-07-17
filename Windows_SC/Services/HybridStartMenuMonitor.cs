using Microsoft.UI.Dispatching;
using System;

namespace Windows_SC.Services;

internal sealed class HybridStartMenuMonitor : IStartMenuMonitor
{
    private static readonly TimeSpan FastMonitoringDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan FastMonitoringInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan VisibleFallbackInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DelayedDiagnosticInterval = TimeSpan.FromMilliseconds(180);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DiagnosticLogger _logger;
    private readonly StartMenuWindowInspector _diagnosticInspector;
    private readonly UiAutomationStartMenuInspector _uiAutomationInspector;
    private readonly DispatcherQueueTimer _delayedDiagnosticTimer;
    private readonly DispatcherQueueTimer _fallbackTimer;
    private DateTimeOffset _fastMonitoringUntil;
    private bool _launcherIsVisible;
    private bool _isStarted;
    private bool _isDisposed;

    public HybridStartMenuMonitor(DispatcherQueue dispatcherQueue, DiagnosticLogger logger)
    {
        _dispatcherQueue = dispatcherQueue;
        _logger = logger;
        _diagnosticInspector = new StartMenuWindowInspector(logger);
        _uiAutomationInspector = new UiAutomationStartMenuInspector(logger);

        _delayedDiagnosticTimer = dispatcherQueue.CreateTimer();
        _delayedDiagnosticTimer.Interval = DelayedDiagnosticInterval;
        _delayedDiagnosticTimer.IsRepeating = false;
        _delayedDiagnosticTimer.Tick += DelayedDiagnosticTimer_Tick;

        _fallbackTimer = dispatcherQueue.CreateTimer();
        _fallbackTimer.Interval = FastMonitoringInterval;
        _fallbackTimer.IsRepeating = true;
        _fallbackTimer.Tick += FallbackTimer_Tick;
    }

    public event EventHandler? SnapshotChanged;

    public StartMenuSnapshot Snapshot => _uiAutomationInspector.Snapshot;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isStarted)
        {
            return;
        }

        _uiAutomationInspector.SnapshotChanged += UiAutomationInspector_SnapshotChanged;
        _uiAutomationInspector.Start();
        _isStarted = true;
        _logger.Write("[StartMenuMonitor] mode=hybrid-event-driven state=started");
    }

    public void NotifyWindowsKeyReleased()
    {
        if (!_isStarted || _isDisposed)
        {
            return;
        }

        _logger.Write("[WindowsKey] standalone trigger accepted; scan-delay-ms=180");
        _fastMonitoringUntil = DateTimeOffset.UtcNow + FastMonitoringDuration;
        _fallbackTimer.Interval = FastMonitoringInterval;
        _fallbackTimer.Start();
        _uiAutomationInspector.RequestScan();
        _delayedDiagnosticTimer.Stop();
        _delayedDiagnosticTimer.Start();
    }

    public void SetLauncherVisible(bool isVisible)
    {
        _launcherIsVisible = isVisible;
        UpdateFallbackMonitoring();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _delayedDiagnosticTimer.Stop();
        _fallbackTimer.Stop();
        _delayedDiagnosticTimer.Tick -= DelayedDiagnosticTimer_Tick;
        _fallbackTimer.Tick -= FallbackTimer_Tick;
        _uiAutomationInspector.SnapshotChanged -= UiAutomationInspector_SnapshotChanged;
        _uiAutomationInspector.Dispose();
        _logger.Write("[StartMenuMonitor] state=stopped");
    }

    private void DelayedDiagnosticTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _logger.Write("[WindowsKey] delayed scan started");
        _diagnosticInspector.InspectAndLog();
        _uiAutomationInspector.RequestScan();
    }

    private void FallbackTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _uiAutomationInspector.RequestScan();
        UpdateFallbackMonitoring();
    }

    private void UpdateFallbackMonitoring()
    {
        if (DateTimeOffset.UtcNow < _fastMonitoringUntil)
        {
            SetFallbackInterval(FastMonitoringInterval);
            return;
        }

        if (_launcherIsVisible)
        {
            SetFallbackInterval(VisibleFallbackInterval);
            return;
        }

        _fallbackTimer.Stop();
    }

    private void SetFallbackInterval(TimeSpan interval)
    {
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
        _dispatcherQueue.TryEnqueue(() => SnapshotChanged?.Invoke(this, EventArgs.Empty));
    }
}
