using System;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal interface IGlobalInputService : IDisposable
{
    event EventHandler? ManualToggleRequested;

    event EventHandler? WindowsKeyReleasedAlone;

    event EventHandler<ShortcutKeyCapturedEventArgs>? ShortcutKeyCaptured;

    void SetSuppressed(bool suppressed);

    void Start(IntPtr windowHandle);

    bool RecoverAfterResume();

    bool TryHandleWindowMessage(uint message, IntPtr wParam);
}

internal sealed class ShortcutKeyCapturedEventArgs(ShortcutKeyDefinition shortcutKey)
    : EventArgs
{
    public ShortcutKeyDefinition ShortcutKey { get; } = shortcutKey;
}
