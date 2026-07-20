using System;

namespace Windows_SC.Services;

internal interface IWindowInteropService : IDisposable
{
    event EventHandler? EscapePressed;

    void Start(IntPtr windowHandle);

    bool IsForeground(IntPtr windowHandle);
}
