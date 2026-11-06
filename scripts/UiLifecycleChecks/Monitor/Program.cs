using Microsoft.UI.Dispatching;
using Windows_SC;
using Windows_SC.Services;

// Production coordinator, controlled native/dispatcher boundaries. No GUI test.
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var dispatcher = new DispatcherQueue();
var monitor = new HybridStartMenuMonitor(dispatcher, new DiagnosticLogger());
int delivered = 0;
monitor.ReadyChanged += (_, _) => delivered++;
monitor.Start();
UiAutomationStartMenuInspector.Latest.RaiseReady();
monitor.Dispose();
dispatcher.Drain();
Check(!dispatcher.Timer.IsRunning && delivered == 0, "Queued ready callback survived Dispose");
monitor.SetLauncherVisible(true);
monitor.SetLauncherInteractive(false);
monitor.CancelPresentationRecovery();
Check(!dispatcher.Timer.IsRunning, "Public notification restarted disposed timer");

var snapshotDispatcher = new DispatcherQueue();
var snapshotMonitor = new HybridStartMenuMonitor(snapshotDispatcher, new DiagnosticLogger());
snapshotMonitor.Start();
snapshotMonitor.BeginPresentationRecovery();
UiAutomationStartMenuInspector.Latest.RaiseSnapshot();
snapshotMonitor.Dispose();
snapshotDispatcher.Drain();
Check(!snapshotDispatcher.Timer.IsRunning, "Queued snapshot restarted timer");

var recoveryDispatcher = new DispatcherQueue();
var recovery = new HybridStartMenuMonitor(recoveryDispatcher, new DiagnosticLogger());
recovery.Start();
var old = UiAutomationStartMenuInspector.Latest;
old.RaiseReady(); // Remains queued across recovery.
old.CanRestart = false;
int created = UiAutomationStartMenuInspector.Created;
for (int i = 0; i < 100; i++)
{
    Check(recovery.RecoverAfterResume(), "Win32 recovery not enabled");
    recoveryDispatcher.Timer.Fire();
}
Check(UiAutomationStartMenuInspector.Created == created, "Blocked workers accumulated generations");
StartMenuWindowInspector.Visible = true;
recovery.NotifyWindowsKeyReleased();
recoveryDispatcher.Timer.Fire();
Check(recovery.Snapshot.IsVisible && !recovery.IsReady, "Win32 fallback did not detect Start");
StartMenuWindowInspector.Visible = false;
recoveryDispatcher.Timer.Fire();
Check(!recovery.Snapshot.IsVisible, "Win32 fallback did not detect hidden Start");
old.CanRestart = true;
recoveryDispatcher.Timer.Fire();
Check(UiAutomationStartMenuInspector.Created == created + 1, "Stopped generation not replaced exactly once");
int staleNotifications = 0;
recovery.ReadyChanged += (_, _) => staleNotifications++;
recoveryDispatcher.Drain();
Check(staleNotifications == 0, "Old generation delivered ready callback");
UiAutomationStartMenuInspector.Latest.RaiseReady();
recoveryDispatcher.Drain();
Check(staleNotifications == 1 && recovery.IsReady, "New generation did not deliver notification");
recovery.Dispose();
foreach (bool enabled in new[] { false, true })
{
    var modeDispatcher = new DispatcherQueue();
    var modeMonitor = new HybridStartMenuMonitor(modeDispatcher, new DiagnosticLogger(), enabled);
    modeMonitor.Start();
    string expectedMode = enabled ? "hybrid-event-driven" : "win32-only";
    Check(UiAutomationStartMenuInspector.Latest.DetectionMode == expectedMode,
        "Selected mode was not used at startup");
    var previous = UiAutomationStartMenuInspector.Latest;
    previous.CanRestart = false;
    modeMonitor.RecoverAfterResume();
    modeDispatcher.Timer.Fire();
    Check(ReferenceEquals(previous, UiAutomationStartMenuInspector.Latest),
        "Mode recovery replaced a still-running worker");
    previous.CanRestart = true;
    modeDispatcher.Timer.Fire();
    Check(UiAutomationStartMenuInspector.Latest.DetectionMode == expectedMode,
        "Resume lost the selected mode");
    modeMonitor.Dispose();
    modeDispatcher.Drain();
}
var eventDispatcher = new DispatcherQueue();
var eventMonitor = new HybridStartMenuMonitor(eventDispatcher, new DiagnosticLogger());
eventMonitor.Start();
var eventSource = Win32StartMenuEventMonitor.Latest;
var eventInspector = UiAutomationStartMenuInspector.Latest;
for (int i = 0; i < 100; i++) eventSource.Raise();
eventDispatcher.Drain();
Check(eventInspector.ScanRequests == 1, "WinEvent burst was not coalesced");
eventMonitor.SetLauncherInteractive(true);
eventSource.Raise();
eventDispatcher.Drain();
Check(eventInspector.ScanRequests == 1, "WinEvent interrupted launcher interaction");
eventMonitor.SetLauncherInteractive(false);
eventSource.Raise();
eventMonitor.RecoverAfterResume();
eventDispatcher.Drain();
Check(eventInspector.ScanRequests == 1, "Queued old WinEvent survived recovery");
eventSource.Raise();
eventMonitor.Dispose();
eventDispatcher.Drain();
Check(eventSource.Disposed, "WinEvent source was not disposed");
Console.WriteLine("PASS: queued notifications, disposed inputs, blocked recovery x100, Win32 fallback, generation replacement");

namespace Microsoft.UI.Dispatching
{
    internal sealed class DispatcherQueue
    {
        private readonly Queue<Action> _pending = new();
        public DispatcherQueueTimer Timer { get; } = new();
        public DispatcherQueueTimer CreateTimer() => Timer;
        public bool TryEnqueue(Action action) { _pending.Enqueue(action); return true; }
        public void Drain() { while (_pending.TryDequeue(out var action)) action(); }
    }
    internal sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public bool IsRunning { get; private set; }
        public event Action<DispatcherQueueTimer, object>? Tick;
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Fire() { if (IsRunning) Tick?.Invoke(this, EventArgs.Empty); }
    }
}
namespace Windows_SC
{
    internal readonly record struct StartMenuSnapshot(bool IsVisible, object? Bounds = null,
        bool IsPhonePanelVisible = false, object? PhonePanelBounds = null)
    {
        public static StartMenuSnapshot Hidden => new(false);
    }
    internal enum StartMenuScanTrigger
    {
        VisibleFallback, WindowsKeyImmediate, WindowsKeyFallback, IdleFallback, InitializationFallback, Win32Event
    }
    internal sealed class UiAutomationStartMenuInspector : IDisposable
    {
        private readonly bool _enableUiAutomation;
        public UiAutomationStartMenuInspector(DiagnosticLogger logger, bool enableUiAutomation = false)
        { _enableUiAutomation = enableUiAutomation; Latest = this; Created++; }
        public static UiAutomationStartMenuInspector Latest { get; private set; } = null!;
        public static int Created { get; private set; }
        public StartMenuSnapshot Snapshot => StartMenuSnapshot.Hidden;
        public bool CanRestart { get; set; } = true;
        public bool IsReady { get; private set; }
        public int ScanRequests { get; private set; }
        public string DetectionMode => _enableUiAutomation ? "hybrid-event-driven" : "win32-only";
        public event EventHandler? SnapshotChanged;
        public event EventHandler? ReadyChanged;
        public void Start() { }
        public void RaiseReady() { IsReady = true; ReadyChanged?.Invoke(this, EventArgs.Empty); }
        public void RaiseSnapshot() => SnapshotChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose() => IsReady = false;
        public void SetMonitoringActive(bool value, StartMenuScanTrigger trigger = default) { }
        public void RequestScanIfStartMenuWindowVisible(StartMenuScanTrigger trigger) { }
        public void RequestScan(StartMenuScanTrigger trigger) { ScanRequests++; }
        public void AssumeHidden() { }
    }
    internal sealed class StartMenuWindowInspector(DiagnosticLogger logger)
    {
        public static bool Visible { get; set; }
        public bool TryGetStartMenuBounds(object? preferred, out object bounds)
        { bounds = "bounds"; logger.Write("stub Win32 scan"); return Visible; }
    }
    internal sealed class DiagnosticLogger
    {
        public void Write(string message) { }
        public void WriteDetailed(string message) { }
    }
}

namespace Windows_SC.Services
{
    internal sealed class Win32StartMenuEventMonitor : IDisposable
    {
        private readonly Action _changed;
        public static Win32StartMenuEventMonitor Latest { get; private set; } = null!;
        public bool Disposed { get; private set; }
        public Win32StartMenuEventMonitor(Windows_SC.DiagnosticLogger logger, Action changed)
        { _changed = changed; Latest = this; }
        public void Start() { }
        public void Restart() { }
        public void RefreshTargets() { }
        public void Raise() => _changed();
        public void Dispose() => Disposed = true;
    }
}
