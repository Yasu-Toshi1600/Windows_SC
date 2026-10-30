using System.Reflection;
using Windows_SC;

// Actual request/stop/worker-finally code and WaitHandles. Native UIA is not invoked.
static object Field(UiAutomationStartMenuInspector inspector, string name) =>
    typeof(UiAutomationStartMenuInspector).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(inspector)!;
static void SetField(UiAutomationStartMenuInspector inspector, string name, object value) =>
    typeof(UiAutomationStartMenuInspector).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(inspector, value);
static void Check(bool value, string message) { if (!value) throw new Exception(message); }

for (int repetition = 0; repetition < 100; repetition++)
{
    var inspector = new UiAutomationStartMenuInspector(new DiagnosticLogger());
    var gate = Field(inspector, "_scanRequestLock");
    var scan = (AutoResetEvent)Field(inspector, "_scanRequested");
    var exit = (ManualResetEvent)Field(inspector, "_disposeRequested");
    Check(((Thread)Field(inspector, "_workerThread")).GetApartmentState() == ApartmentState.MTA,
        "Scan worker is not MTA");
    Check(((Thread)Field(inspector, "_focusEventThread")).GetApartmentState() == ApartmentState.MTA,
        "Event worker is not MTA");
    Exception? observed = null;
    var requester = new Thread(() =>
    {
        try { inspector.RequestScan(StartMenuScanTrigger.FocusEvent); }
        catch (Exception exception) { observed = exception; }
    }) { IsBackground = true };
    lock (gate)
    {
        requester.Start();
        Check(SpinWait.SpinUntil(() => (requester.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(3)), "Requester did not reach request lock");
        inspector.Dispose(); // Also verifies disposal before Start is valid.
    }
    Check(requester.Join(TimeSpan.FromSeconds(3)), "Requester did not finish");
    Check(observed is null, $"Request raced with disposal: {observed?.GetType().Name}");
    Check(scan.SafeWaitHandle.IsClosed && exit.SafeWaitHandle.IsClosed, "Wait handles not released");
    inspector.Dispose(); // Idempotence.
}

var pending = new UiAutomationStartMenuInspector(new DiagnosticLogger());
var pendingScan = (AutoResetEvent)Field(pending, "_scanRequested");
var pendingExit = (ManualResetEvent)Field(pending, "_disposeRequested");
using var allowCompletion = new ManualResetEventSlim();
using var entered = new ManualResetEventSlim();
SetField(pending, "_runningWorkers", 1);
var runWorker = typeof(UiAutomationStartMenuInspector).GetMethod("RunWorker",
    BindingFlags.Instance | BindingFlags.NonPublic)!;
var controlledWorker = new Thread(() => runWorker.Invoke(pending, new object[]
{
    (Action)(() => { entered.Set(); pendingExit.WaitOne(); allowCompletion.Wait(); })
})) { IsBackground = true };
controlledWorker.Start();
try
{
    Check(entered.Wait(TimeSpan.FromSeconds(3)), "Worker did not start");
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    pending.Dispose();
    Check(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "UI stop waited on blocked worker");
    Check(!pending.CanRestart, "Replacement allowed before old worker completed");
    Check(!pendingScan.SafeWaitHandle.IsClosed && !pendingExit.SafeWaitHandle.IsClosed,
        "Handles released while worker could use them");
    pending.RequestScan(StartMenuScanTrigger.FocusEvent); // Late callback must be harmless.
}
finally
{
    allowCompletion.Set();
    Check(controlledWorker.Join(TimeSpan.FromSeconds(3)), "Worker did not finish");
}
Check(pending.CanRestart, "Replacement not allowed after successful cleanup");
Check(pendingScan.SafeWaitHandle.IsClosed && pendingExit.SafeWaitHandle.IsClosed,
    "Worker completion did not release handles");
SetField(pending, "_eventCleanupFailed", true);
Check(!pending.CanRestart, "Replacement allowed despite failed event removal");
Console.WriteLine("PASS: request/dispose race x100, MTA, no-start/double disposal, pending worker, deferred handle release, failed removal gate");

namespace Windows.Graphics
{
    internal readonly record struct RectInt32(int X, int Y, int Width, int Height);
}
namespace Windows_SC
{
    internal sealed class DiagnosticLogger
    {
        public void Write(string message) { }
        public void WriteDetailed(string message) { }
    }
    internal sealed class StartMenuWindowInspector
    {
        public StartMenuWindowInspector(DiagnosticLogger logger) { }
        public bool IsStartMenuVisible() => false;
        public bool TryGetStartMenuBounds(Windows.Graphics.RectInt32? preferred,
            out Windows.Graphics.RectInt32 bounds) { bounds = default; return false; }
    }
    internal static class StartMenuBoundsValidator
    {
        public static bool CoversMostOfMonitor(Windows.Graphics.RectInt32 rect) => false;
    }
}
namespace Windows_SC.Services
{
    internal static class LogValue
    {
        public static string Normalize(string value) => value;
    }
}
