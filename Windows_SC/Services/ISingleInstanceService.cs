using System;

namespace Windows_SC.Services;

internal interface ISingleInstanceService : IDisposable
{
    bool TryAcquire();
}
