using System;
using System.Threading;
using System.Threading.Tasks;

namespace Windows_SC.Services;


internal sealed class SettingsPersistenceCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RunAsync(Func<Task> operation)
    {
        await _gate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WaitForIdleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }
}
