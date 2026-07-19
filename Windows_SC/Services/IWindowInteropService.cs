using System;

namespace Windows_SC.Services;

internal interface IWindowInteropService : IDisposable
{
    event EventHandler? EscapePressed;

    void Start(IntPtr windowHandle);

    bool IsForeground(IntPtr windowHandle);

    void Move(IntPtr windowHandle, int x, int y);

    void RequestRedraw(IntPtr windowHandle);
}
