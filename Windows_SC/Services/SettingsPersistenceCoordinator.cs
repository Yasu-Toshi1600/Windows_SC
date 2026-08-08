using System;
using System.Threading;
using System.Threading.Tasks;

namespace Windows_SC.Services;

internal sealed class SettingsPersistenceCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        finally
        {
            _gate.Release();
        }
    }
}
