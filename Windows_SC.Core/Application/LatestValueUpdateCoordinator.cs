using System;
using System.Threading.Tasks;

namespace Windows_SC.Services;


internal sealed class LatestValueUpdateCoordinator<T>
{
    private readonly object _gate = new();
    private readonly Func<T, Task> _applyAsync;
    private readonly Action<Exception>? _handleError;
    private TaskCompletionSource _idleCompletion = CreateCompletedCompletion();
    private T _latestValue = default!;
    private bool _hasPendingValue;
    private bool _isProcessing;

    public LatestValueUpdateCoordinator(
        Func<T, Task> applyAsync,
        Action<Exception>? handleError = null)
    {
        ArgumentNullException.ThrowIfNull(applyAsync);
        _applyAsync = applyAsync;
        _handleError = handleError;
    }

    public bool IsProcessing
    {
        get
        {
            lock (_gate)
            {
                return _isProcessing;
            }
        }
    }

    public void Request(T value)
    {
        bool startProcessing = false;
        lock (_gate)
        {
            _latestValue = value;
            _hasPendingValue = true;
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
            T value;
            lock (_gate)
            {
                value = _latestValue;
                _hasPendingValue = false;
            }

            try
            {
                await _applyAsync(value);
            }
            catch (Exception exception)
            {
                try
                {
                    _handleError?.Invoke(exception);
                }
                catch
                {
                    // Error reporting must not strand the coordinator in a busy state.
                }
            }

            lock (_gate)
            {
                if (_hasPendingValue)
                {
                    continue;
                }

                _isProcessing = false;
                _idleCompletion.TrySetResult();
                return;
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
