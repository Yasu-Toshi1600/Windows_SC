using System;

namespace Windows_SC.Services;

// SetVisible runs on the UI thread. Background notifications only request a refresh.
internal sealed class VisibleUiUpdateCoordinator(IUiDispatcher dispatcher, Action refresh) : IDisposable
{
    private readonly object _gate = new();
    private bool _visible;
    private bool _queued;
    private bool _disposed;

    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            if (_disposed || _visible == visible) return;
            _visible = visible;
        }
        // Refresh from the cache before showing the window, without adding a sample.
        if (visible) refresh();
    }

    public void RequestUpdate()
    {
        lock (_gate)
        {
            if (_disposed || !_visible || _queued) return;
            _queued = true;
        }
        if (!dispatcher.TryEnqueue(RefreshOnUiThread))
        {
            lock (_gate) _queued = false;
        }
    }

    private void RefreshOnUiThread()
    {
        bool shouldRefresh;
        lock (_gate)
        {
            _queued = false;
            shouldRefresh = !_disposed && _visible;
        }
        if (shouldRefresh) refresh();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _visible = false;
        }
    }
}
