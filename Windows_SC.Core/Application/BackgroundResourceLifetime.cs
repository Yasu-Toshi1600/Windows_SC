using System;
using System.Threading;

namespace Windows_SC.Services;

// Serializes native resource use and release. A blocked native call must never
// cause release to race that call, or keep the UI waiting indefinitely.
internal sealed class BackgroundResourceLifetime
{
    private readonly object _gate = new();
    private int _stopping;

    public bool TryRun(Action operation)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return false;
            }

            operation();
            return true;
        }
    }

    public bool Stop(Action release, Action<Exception> onFailure, TimeSpan wait)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return false;
        }

        Thread cleanup = new(() =>
        {
            try
            {
                lock (_gate)
                {
                    release();
                }
            }
            catch (Exception exception)
            {
                onFailure(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Windows_SC native resource cleanup"
        };
        cleanup.Start();
        return cleanup.Join(wait);
    }
}
