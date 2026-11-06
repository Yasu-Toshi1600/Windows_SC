using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

// All methods and callbacks run on the UI thread, which owns a message loop.
internal sealed class Win32StartMenuEventMonitor : IDisposable
{
    private static readonly uint[] Events = [3, 0x8002, 0x8003, 0x8005, 0x800B, 0x8017, 0x8018];
    private readonly DiagnosticLogger _logger;
    private readonly Action _changed;
    private readonly IWin32StartEventApi _api;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<IntPtr> _hooks = [];
    private HashSet<uint> _targets = [];
    private GCHandle _callbackRoot;
    private long _generation;
    private bool _disposed;
    private DateTimeOffset _nextRefresh;

    public Win32StartMenuEventMonitor(DiagnosticLogger logger, Action changed,
        IWin32StartEventApi? api = null)
    {
        _logger = logger;
        _changed = changed;
        _api = api ?? new Win32StartEventApi();
    }

    public void Start()
    {
        VerifyThread();
        if (_disposed || _hooks.Count != 0) return;
        RefreshTargets(force: true);
        long generation = ++_generation;
        Win32StartEventCallback callback = (_, type, window, objectId, childId, _, _) =>
        {
            // Never resolve an accessibility object from this notification.
            try
            {
                if (_disposed || generation != _generation || window == IntPtr.Zero) return;
                if (type != 3 && type != 0x8005 && (objectId != 0 || childId != 0)) return;
                if (type != 3 && !_targets.Contains(_api.GetProcessId(window))) return;
                _changed();
            }
            catch (Exception exception)
            {
                _logger.Write($"[StartMenu] action=receive-win-event result=failed exception={exception.GetType().Name}");
            }
        };
        _callbackRoot = GCHandle.Alloc(callback);
        try
        {
            foreach (uint type in Events)
            {
                IntPtr hook = _api.Register(type, callback);
                if (hook == IntPtr.Zero) throw new InvalidOperationException("WinEvent registration failed.");
                _hooks.Add(hook);
            }
            _logger.Write("[StartMenu] action=start-win-events result=success");
        }
        catch (Exception exception)
        {
            Stop();
            _logger.Write($"[StartMenu] action=start-win-events result=failed exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
        }
    }

    public void RefreshTargets(bool force = false)
    {
        VerifyThread();
        if (_disposed || (!force && DateTimeOffset.UtcNow < _nextRefresh)) return;
        _nextRefresh = DateTimeOffset.UtcNow.AddSeconds(2);
        try { _targets = _api.GetTargetProcessIds(); }
        catch (Exception exception)
        {
            _logger.Write($"[StartMenu] action=refresh-win-event-targets result=failed exception={exception.GetType().Name}");
        }
    }

    public void Restart()
    {
        VerifyThread();
        if (_disposed) return;
        Stop();
        Start();
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void Stop()
    {
        ++_generation;
        for (int i = _hooks.Count - 1; i >= 0; i--)
        {
            if (_api.Unregister(_hooks[i])) _hooks.RemoveAt(i);
        }
        // Retain the callback if native removal fails; never leave a native
        // function pointer referring to a collected delegate or add duplicates.
        if (_hooks.Count == 0 && _callbackRoot.IsAllocated) _callbackRoot.Free();
        _logger.Write($"[StartMenu] action=stop-win-events result={(_hooks.Count == 0 ? "success" : "failed")}");
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("WinEvent hooks must use their owning thread.");
    }
}

internal delegate void Win32StartEventCallback(IntPtr hook, uint type, IntPtr window,
    int objectId, int childId, uint threadId, uint time);

internal interface IWin32StartEventApi
{
    IntPtr Register(uint type, Win32StartEventCallback callback);
    bool Unregister(IntPtr hook);
    uint GetProcessId(IntPtr window);
    HashSet<uint> GetTargetProcessIds();
}

internal sealed class Win32StartEventApi : IWin32StartEventApi
{
    public IntPtr Register(uint type, Win32StartEventCallback callback) =>
        SetWinEventHook(type, type, IntPtr.Zero, callback, 0, 0, 2); // OUTOFCONTEXT | SKIPOWNPROCESS
    public bool Unregister(IntPtr hook) => UnhookWinEvent(hook);
    public uint GetProcessId(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint process);
        return process;
    }
    public HashSet<uint> GetTargetProcessIds()
    {
        HashSet<uint> result = [];
        foreach (string name in new[] { "StartMenuExperienceHost", "SearchHost", "explorer" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process) result.Add(unchecked((uint)process.Id));
            }
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
        Win32StartEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
