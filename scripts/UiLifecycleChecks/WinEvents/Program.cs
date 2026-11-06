using Windows_SC;
using Windows_SC.Services;
using System.Runtime.InteropServices;
using System.Reflection;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
if (args.Contains("--native-smoke"))
{
    int notifications = 0;
    using var native = new Win32StartMenuEventMonitor(new DiagnosticLogger(), () => notifications++);
    native.Start();
    var hooks = (List<IntPtr>)typeof(Win32StartMenuEventMonitor)
        .GetField("_hooks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(native)!;
    Check(hooks.Count == 7, "Native hook registration failed");
    var until = DateTime.UtcNow.AddSeconds(2);
    while (DateTime.UtcNow < until)
    {
        while (SmokeApi.PeekMessage(out var message, 0, 0, 0, 1))
        { SmokeApi.TranslateMessage(ref message); SmokeApi.DispatchMessage(ref message); }
        Thread.Sleep(10);
    }
    native.Dispose();
    Check(hooks.Count == 0, "Native hook removal failed");
    Console.WriteLine($"PASS: native registration/message loop/removal; notifications={notifications}");
    return;
}
var api = new FakeApi();
int received = 0;
using var monitor = new Win32StartMenuEventMonitor(new DiagnosticLogger(), () => received++, api);
monitor.Start();
monitor.Start();
Check(api.Hooks.Count == 7, "Duplicate registrations");
var callback = api.Callback!;
callback(0, 0x8005, 1, -4, 2, 0, 0);
Check(received == 1, "Target focus notification missing");
callback(0, 0x8005, 2, -4, 2, 0, 0);
callback(0, 0x800B, 1, -8, 0, 0, 0);
callback(0, 3, 0, 0, 0, 0, 0);
Check(received == 1, "Unrelated event accepted");
callback(0, 3, 2, 0, 0, 0, 0);
Check(received == 2, "Foreground notification missing");
monitor.Restart();
Check(api.Hooks.Count == 7, "Hooks accumulated after restart");
callback(0, 3, 1, 0, 0, 0, 0);
Check(received == 2, "Old callback survived restart");
api.Targets = [2];
monitor.RefreshTargets(force: true);
api.Callback!(0, 0x8002, 2, 0, 0, 0, 0);
Check(received == 3, "PID refresh did not apply");
api.FailRemoval = true;
monitor.Restart();
Check(api.Hooks.Count == 7, "Failed removal accumulated hooks");
api.Callback!(0, 3, 1, 0, 0, 0, 0);
Check(received == 3, "Failed-removal callback still active");
api.FailRemoval = false;
monitor.Restart();
Exception? wrongThread = null;
var thread = new Thread(() => { try { monitor.Dispose(); } catch(Exception e) { wrongThread = e; } });
thread.Start(); thread.Join();
Check(wrongThread is InvalidOperationException, "Cross-thread removal was allowed");
monitor.Dispose();
Check(api.Hooks.Count == 0, "Disposal leaked hooks");
api.Callback!(0, 3, 1, 0, 0, 0, 0);
Check(received == 3, "Disposed callback still active");
var partialApi = new FakeApi { FailRegistrationAt = 3 };
using var partial = new Win32StartMenuEventMonitor(new DiagnosticLogger(), () => {}, partialApi);
partial.Start();
Check(partialApi.Hooks.Count == 0, "Partial registration leaked hooks");
using var throwing = new Win32StartMenuEventMonitor(new DiagnosticLogger(),
    () => throw new InvalidOperationException(), api);
throwing.Start();
api.Callback!(0, 3, 1, 0, 0, 0, 0); // Must not throw through the native callback boundary.
Console.WriteLine("PASS: filtering, PID refresh, stale callbacks, partial failure, removal failure, thread ownership, disposal");

internal sealed class FakeApi : IWin32StartEventApi
{
    public HashSet<IntPtr> Hooks { get; } = [];
    public HashSet<uint> Targets { get; set; } = [1];
    public Win32StartEventCallback? Callback { get; private set; }
    public int FailRegistrationAt { get; set; }
    public bool FailRemoval { get; set; }
    private int _next;
    public IntPtr Register(uint type, Win32StartEventCallback callback)
    {
        Callback = callback;
        if (++_next == FailRegistrationAt) return IntPtr.Zero;
        IntPtr hook = _next; Hooks.Add(hook); return hook;
    }
    public bool Unregister(IntPtr hook) => !FailRemoval && Hooks.Remove(hook);
    public uint GetProcessId(IntPtr window) => (uint)window;
    public HashSet<uint> GetTargetProcessIds() => Targets;
}
namespace Windows_SC
{
    internal sealed class DiagnosticLogger { public void Write(string message) { } }
}

internal static class SmokeApi
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public IntPtr Window;
        public uint Type;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message,
        IntPtr window, uint first, uint last, uint flags);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref Message message);
}
