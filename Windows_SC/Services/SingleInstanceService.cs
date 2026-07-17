using System;
using System.Threading;

namespace Windows_SC.Services;

internal sealed class SingleInstanceService : ISingleInstanceService
{
    private const string MutexName = @"Local\Windows_SC.SingleInstance.x64";
    private Mutex? _mutex;
    private bool _ownsMutex;

    public bool TryAcquire()
    {
        if (_mutex is not null)
        {
            return _ownsMutex;
        }

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;

        if (!_ownsMutex)
        {
            _mutex.Dispose();
            _mutex = null;
        }

        return _ownsMutex;
    }

    public void Dispose()
    {
        if (_ownsMutex && _mutex is not null)
        {
            _mutex.ReleaseMutex();
        }

        _ownsMutex = false;
        _mutex?.Dispose();
        _mutex = null;
    }
}
