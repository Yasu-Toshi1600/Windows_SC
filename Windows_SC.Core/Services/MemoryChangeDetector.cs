using System;
using System.Collections.Generic;

namespace Windows_SC.Services;

/// <summary>Detects resident-memory changes; does not infer their cause.</summary>
public sealed class MemoryChangeDetector
{
    private readonly Queue<(long Time, long Bytes)> _history = new();
    private long _lastTrigger = -60_000;
    public int HistoryCount => _history.Count;

    public MemoryChange? Observe(long elapsedMilliseconds, long? privateWorkingSetBytes)
    {
        if (privateWorkingSetBytes is not { } bytes)
        {
            _history.Clear();
            return null;
        }

        if (_history.Count > 0)
        {
            var last = _last;
            // Do not describe a suspension or missed sampling interval as a one-second change.
            if (elapsedMilliseconds <= last.Time || elapsedMilliseconds - last.Time > 5_000)
                _history.Clear();
        }
        while (_history.Count > 0 && elapsedMilliseconds - _history.Peek().Time > 30_000)
            _history.Dequeue();

        MemoryChange? change = null;
        if (_history.Count > 0 && elapsedMilliseconds - _lastTrigger >= 60_000)
        {
            var baseline = IsLargeChange(_last.Bytes, bytes) ? _last : _history.Peek();
            if (IsLargeChange(baseline.Bytes, bytes))
            {
                change = new(baseline.Time, bytes - baseline.Bytes);
                _lastTrigger = elapsedMilliseconds;
            }
        }
        _last = (elapsedMilliseconds, bytes);
        _history.Enqueue(_last);
        while (_history.Count > 31) _history.Dequeue();
        return change;
    }

    private (long Time, long Bytes) _last;

    private static bool IsLargeChange(long before, long after)
    {
        long delta = Math.Abs(after - before);
        return delta >= 16 * 1024 * 1024
            || (delta >= 4 * 1024 * 1024 && delta >= before * 0.20);
    }
}

public readonly record struct MemoryChange(long BaselineMilliseconds, long DeltaBytes);
