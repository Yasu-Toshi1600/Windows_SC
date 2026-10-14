using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace Windows_SC.Services;

/// <summary>Opt-in, one-second recording. Never forces GC or trims the working set.</summary>
internal sealed class MemoryFlightRecorder : IDisposable
{
    private const string Header = "utc,pid,sequence,elapsed_ms,interval_ms,private_ws_bytes,working_set_bytes," +
        "private_commit_bytes,page_faults_total,page_faults_delta,gen0,gen1,gen2,managed_bytes," +
        "gc_committed_bytes,allocated_total_bytes,system_available_bytes,system_physical_bytes," +
        "system_commit_bytes,system_commit_limit_bytes,cpu_total_ms,handles,launcher_visible," +
        "launcher_cloaked,launcher_foreground,settings_open,last_marker,marker_elapsed_ms,sample_us";
    private readonly DiagnosticLogger _logger;
    private readonly IntPtr _window;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly MemoryChangeDetector _detector = new();
    private readonly Queue<(long Time, string Csv)> _before = new();
    private readonly RotatingCsvWriter _samples;
    private readonly RotatingCsvWriter _events;
    private readonly Timer _timer;
    private readonly string _signalPath;
    private long _sequence, _previousTime, _eventUntil, _eventNumber;
    private uint? _previousFaults;
    private bool _disposed, _failed;
    private int _settingsOpen;
    private Marker _marker = new("startup", 0);

    private MemoryFlightRecorder(DiagnosticLogger logger, IntPtr window, string directory)
    {
        _logger = logger;
        _window = window;
        _signalPath = Path.Combine(directory, "trigger.txt");
        _samples = new RotatingCsvWriter(Path.Combine(directory, "samples.csv"), Header);
        try
        {
            _events = new RotatingCsvWriter(Path.Combine(directory, "events.csv"), "event_id,phase," + Header);
        }
        catch
        {
            _samples.Dispose();
            throw;
        }
        // Delayed start avoids a callback before construction completes.
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _logger.Write("[MemoryFlight] action=start result=success interval-ms=1000");
    }

    public static MemoryFlightRecorder? StartIfEnabled(DiagnosticLogger logger, IntPtr window)
    {
        if (Environment.GetEnvironmentVariable("WINDOWS_SC_MEMORY_FLIGHT") != "1") return null;
        try
        {
            return new MemoryFlightRecorder(logger, window,
                Path.Combine(logger.LogDirectoryPath, "MemoryFlight"));
        }
        catch (Exception exception)
        {
            logger.Write($"[MemoryFlight] action=start result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8}");
            return null;
        }
    }

    // Called on the UI thread. No file access, native sampling or recorder lock here.
    public void Mark(string reason, bool? settingsOpen)
    {
        if (settingsOpen.HasValue) Volatile.Write(ref _settingsOpen, settingsOpen.Value ? 1 : 0);
        string token = reason switch
        {
            "startup" or "shutdown" or "settings-opening" or "settings-opened" or "settings-closed" => reason,
            _ => "other"
        };
        Volatile.Write(ref _marker, new Marker(token, _clock.ElapsedMilliseconds));
    }

    private void Sample()
    {
        // Slow file access must not queue unlimited overlapping timer callbacks.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed || _failed) return;
            long start = Stopwatch.GetTimestamp();
            long elapsed = _clock.ElapsedMilliseconds;
            var memory = MemoryFlightNativeSampler.ReadProcess();
            var system = MemoryFlightNativeSampler.ReadSystem();
            long? privateWs = memory.PrivateWorkingSet == nuint.MaxValue
                ? null : checked((long)memory.PrivateWorkingSet);
            uint? faults = _previousFaults is { } previous
                ? unchecked(memory.PageFaultCount - previous) : null;
            GCMemoryInfo gc = GC.GetGCMemoryInfo();
            Marker marker = Volatile.Read(ref _marker);
            long? cpu = MemoryFlightNativeSampler.ReadCpuMilliseconds();
            uint? handles = MemoryFlightNativeSampler.ReadHandles();
            bool? visible = _window == IntPtr.Zero ? null : MemoryFlightNativeSampler.IsWindowVisible(_window);
            bool? cloaked = MemoryFlightNativeSampler.ReadCloaked(_window);
            bool? foreground = _window == IntPtr.Zero ? null : MemoryFlightNativeSampler.GetForegroundWindow() == _window;
            // CSV contains only numbers, timestamps and fixed tokens. Empty means unavailable.
            string row = string.Join(",", new object?[]
            {
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Environment.ProcessId,
                ++_sequence, elapsed, _previousFaults.HasValue ? elapsed - _previousTime : null,
                privateWs, (ulong)memory.WorkingSet, (ulong)memory.PrivateUsage, memory.PageFaultCount, faults,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalMemory(false),
                gc.TotalCommittedBytes, GC.GetTotalAllocatedBytes(false),
                system is { } s ? (ulong)s.PhysicalAvailable * (ulong)s.PageSize : null,
                system is { } p ? (ulong)p.PhysicalTotal * (ulong)p.PageSize : null,
                system is { } c ? (ulong)c.CommitTotal * (ulong)c.PageSize : null,
                system is { } l ? (ulong)l.CommitLimit * (ulong)l.PageSize : null,
                cpu, handles, Flag(visible), Flag(cloaked), Flag(foreground),
                Volatile.Read(ref _settingsOpen), marker.Name, marker.ElapsedMilliseconds,
                (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000)
            });
            _samples.WriteLine(row);
            _previousFaults = memory.PageFaultCount;
            _previousTime = elapsed;
            while (_before.Count > 0 && elapsed - _before.Peek().Time > 30_000) _before.Dequeue();
            MemoryChange? change = _detector.Observe(elapsed, privateWs);
            if (change is { } detected)
            {
                _eventNumber++;
                _eventUntil = elapsed + 30_000;
                foreach (var before in _before) WriteEvent("before", before.Csv);
                WriteEvent("trigger", row);
                _logger.Write($"[MemoryFlight] action=trigger result=success event={_eventNumber} " +
                    $"delta-bytes={detected.DeltaBytes} baseline-ms={detected.BaselineMilliseconds} " +
                    $"elapsed-ms={elapsed}");
                // Atomic replacement lets the optional WPR watcher detect a new trigger.
                string temporary = _signalPath + ".tmp";
                File.WriteAllText(temporary, $"{Environment.ProcessId}:{_eventNumber}:{DateTimeOffset.UtcNow:O}");
                File.Move(temporary, _signalPath, overwrite: true);
            }
            else if (_eventNumber > 0 && elapsed <= _eventUntil)
            {
                WriteEvent("after", row);
            }
            _before.Enqueue((elapsed, row));
            while (_before.Count > 31) _before.Dequeue();
        }
        catch (Exception exception)
        {
            // Fail closed once: a full disk or unavailable API must not produce a log storm.
            _failed = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _logger.Write($"[MemoryFlight] action=sample result=failed exception={exception.GetType().Name} " +
                $"hresult=0x{exception.HResult:X8}");
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private static string Flag(bool? value) => value.HasValue ? (value.Value ? "1" : "0") : "";
    private void WriteEvent(string phase, string row) =>
        _events.WriteLine($"{Environment.ProcessId}-{_eventNumber},{phase},{row}");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            try { _samples.Dispose(); }
            finally { _events.Dispose(); }
            _before.Clear();
            _logger.Write("[MemoryFlight] action=stop result=success");
        }
    }

    private sealed record Marker(string Name, long ElapsedMilliseconds);
}
