using System;
using Microsoft.UI.Dispatching;

namespace Windows_SC.Services;

internal sealed class DispatcherQueueUiDispatcher(DispatcherQueue dispatcherQueue) : IUiDispatcher
{
    public bool TryEnqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return dispatcherQueue.TryEnqueue(() => action());
    }
}
