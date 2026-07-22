using System;

namespace Windows_SC.Services;

internal interface IWindowInteropService : IDisposable
{
    event EventHandler? EscapePressed;
    event EventHandler<DisplayEnvironmentChangedEventArgs>? DisplayEnvironmentChanged;

    void Start(IntPtr windowHandle);

    bool IsForeground(IntPtr windowHandle);

    bool TryActivate(IntPtr windowHandle);

    VirtualDesktopMoveResult MoveToCurrentVirtualDesktop(IntPtr windowHandle);
}

internal enum VirtualDesktopMoveStatus
{
    AlreadyCurrent,
    Moved,
    ReferenceWindowUnavailable,
    Failed
}

internal readonly record struct VirtualDesktopMoveResult(
    VirtualDesktopMoveStatus Status,
    int HResult = 0);

internal sealed class DisplayEnvironmentChangedEventArgs(string reason) : EventArgs
{
    internal string Reason { get; } = reason;
}
