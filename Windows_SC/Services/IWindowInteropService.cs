using System;

namespace Windows_SC.Services;

internal interface IWindowInteropService : IDisposable
{
    event EventHandler? EscapePressed;
    event EventHandler<DisplayEnvironmentChangedEventArgs>? DisplayEnvironmentChanged;

    void Start(IntPtr windowHandle);

    bool IsForeground(IntPtr windowHandle);

    WindowPresentationState GetPresentationState(IntPtr windowHandle);

    bool TryKeepTopmost(IntPtr windowHandle);

    bool TryHide(IntPtr windowHandle);

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

internal readonly record struct WindowPresentationState(
    bool IsVisible,
    bool IsTopmost,
    bool IsCloaked,
    bool IsCloakingStateKnown)
{
    internal bool IsPresented =>
        IsVisible && IsTopmost && (!IsCloakingStateKnown || !IsCloaked);
}

internal sealed class DisplayEnvironmentChangedEventArgs(string reason) : EventArgs
{
    internal string Reason { get; } = reason;
}
