using System;
using System.Threading;
using System.Threading.Tasks;

namespace Windows_SC.Services;

internal sealed class ShortcutKeyExecutionCoordinator
{
    private Func<CancellationToken, Task>? _prepareTargetAsync;

    public void Attach(Func<CancellationToken, Task> prepareTargetAsync) =>
        _prepareTargetAsync = prepareTargetAsync;

    public void Detach(Func<CancellationToken, Task> prepareTargetAsync)
    {
        if (_prepareTargetAsync == prepareTargetAsync)
        {
            _prepareTargetAsync = null;
        }
    }

    public Task PrepareTargetAsync(CancellationToken cancellationToken) =>
        _prepareTargetAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;
}
