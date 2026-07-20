using Microsoft.UI.Dispatching;
using System;

namespace Windows_SC.Services;

internal sealed class HybridStartMenuMonitor : IStartMenuMonitor
{
    private static readonly TimeSpan FastMonitoringDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan FastMonitoringInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan VisibleFallbackInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DiagnosticLogger _logger;
    private readonly UiAutomationStartMenuInspector _uiAutomationInspector;
    private readonly DispatcherQueueTimer _fallbackTimer;
    private DateTimeOffset _fastMonitoringUntil;
    private bool _launcherIsVisible;
    private bool _launcherIsInteractive;
    private bool _isStarted;
    private bool _isDisposed;
    private bool _awaitingStartConfirmation;

    public HybridStartMenuMonitor(DispatcherQueue dispatcherQueue, DiagnosticLogger logger)
    {
        _dispatcherQueue = dispatcherQueue;
        _logger = logger;
        _uiAutomationInspector = new UiAutomationStartMenuInspector(logger);

        _fallbackTimer = dispatcherQueue.CreateTimer();
        _fallbackTimer.Interval = FastMonitoringInterval;
        _fallbackTimer.IsRepeating = true;
        _fallbackTimer.Tick += FallbackTimer_Tick;
    }

    public event EventHandler? SnapshotChanged;

    public event EventHandler? ReadyChanged;

    public event EventHandler? StartConfirmationExpired;

    public StartMenuSnapshot Snapshot => _uiAutomationInspector.Snapshot;

    public bool IsReady => _uiAutomationInspector.IsReady;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isStarted)
        {
            return;
        }

        _uiAutomationInspector.SnapshotChanged += UiAutomationInspector_SnapshotChanged;
        _uiAutomationInspector.ReadyChanged += UiAutomationInspector_ReadyChanged;
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

        _logger.Write("[WindowsKey] standalone trigger accepted; fallback-window-ms=1500");
        _awaitingStartConfirmation = true;
        _fastMonitoringUntil = DateTimeOffset.UtcNow + FastMonitoringDuration;
        _uiAutomationInspector.SetMonitoringActive(true);
        _fallbackTimer.Interval = FastMonitoringInterval;
        _fallbackTimer.Start();
        _uiAutomationInspector.RequestScan();
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
        _logger.Write("[StartMenuMonitor] state=hidden source=windows-key-close");
    }

    public void SetLauncherVisible(bool isVisible)
    {
        _launcherIsVisible = isVisible;
        if (!isVisible)
        {
            _launcherIsInteractive = false;
        }

        UpdateFallbackMonitoring();
    }

    public void SetLauncherInteractive(bool isInteractive)
    {
        _launcherIsInteractive = isInteractive;
        if (isInteractive)
        {
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
        _fallbackTimer.Stop();
        _fallbackTimer.Tick -= FallbackTimer_Tick;
        _uiAutomationInspector.SnapshotChanged -= UiAutomationInspector_SnapshotChanged;
        _uiAutomationInspector.ReadyChanged -= UiAutomationInspector_ReadyChanged;
        _uiAutomationInspector.Dispose();
        _logger.Write("[StartMenuMonitor] state=stopped");
    }

    private void FallbackTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _uiAutomationInspector.RequestScan();
        UpdateFallbackMonitoring();
    }

    private void UpdateFallbackMonitoring()
    {
        if (_launcherIsInteractive)
        {
            _fallbackTimer.Stop();
            _uiAutomationInspector.SetMonitoringActive(false);
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

        _fallbackTimer.Stop();
        _uiAutomationInspector.SetMonitoringActive(false);
        if (_awaitingStartConfirmation && !Snapshot.IsVisible)
        {
            _awaitingStartConfirmation = false;
            StartConfirmationExpired?.Invoke(this, EventArgs.Empty);
        }
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
        if (_uiAutomationInspector.Snapshot.IsVisible)
        {
            _awaitingStartConfirmation = false;
        }

        _dispatcherQueue.TryEnqueue(() => SnapshotChanged?.Invoke(this, EventArgs.Empty));
    }

    private void UiAutomationInspector_ReadyChanged(object? sender, EventArgs args) =>
        _dispatcherQueue.TryEnqueue(() => ReadyChanged?.Invoke(this, EventArgs.Empty));
}
