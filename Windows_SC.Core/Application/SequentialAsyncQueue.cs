using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Windows_SC.Services;


internal sealed class SequentialAsyncQueue<T>
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();
    private readonly Func<T, Task> _processAsync;
    private readonly Action<Exception>? _handleError;
    private TaskCompletionSource _idleCompletion = CreateCompletedCompletion();
    private bool _isProcessing;

    public SequentialAsyncQueue(
        Func<T, Task> processAsync,
        Action<Exception>? handleError = null)
    {
        ArgumentNullException.ThrowIfNull(processAsync);
        _processAsync = processAsync;
        _handleError = handleError;
    }

    public void Enqueue(T item)
    {
        bool startProcessing = false;
        lock (_gate)
        {
            _items.Enqueue(item);
            if (!_isProcessing)
            {
                _isProcessing = true;
                _idleCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                startProcessing = true;
            }
        }

        if (startProcessing)
        {
            _ = ProcessAsync();
        }
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _idleCompletion.Task;
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            T item;
            lock (_gate)
            {
                if (_items.Count == 0)
                {
                    _isProcessing = false;
                    _idleCompletion.TrySetResult();
                    return;
                }

                item = _items.Dequeue();
            }

            try
            {
                await _processAsync(item);
            }
            catch (Exception exception)
            {
                try
                {
                    _handleError?.Invoke(exception);
                }
                catch
                {
                    // Error reporting must not prevent later queued items from running.
                }
            }
        }
    }

    private static TaskCompletionSource CreateCompletedCompletion()
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
