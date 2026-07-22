using System;

namespace Windows_SC.Services;

internal interface IWindowInteropService : IDisposable
{
    event EventHandler? EscapePressed;
    event EventHandler<DisplayEnvironmentChangedEventArgs>? DisplayEnvironmentChanged;

    void Start(IntPtr windowHandle);

    bool IsForeground(IntPtr windowHandle);

    bool TryActivate(IntPtr windowHandle);
}

internal sealed class DisplayEnvironmentChangedEventArgs(string reason) : EventArgs
{
    internal string Reason { get; } = reason;
}
