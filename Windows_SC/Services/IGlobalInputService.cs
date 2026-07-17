using System;

namespace Windows_SC.Services;

internal interface IGlobalInputService : IDisposable
{
    event EventHandler? ManualToggleRequested;

    event EventHandler? WindowsKeyReleasedAlone;

    void Start(IntPtr windowHandle);

    bool TryHandleWindowMessage(uint message, IntPtr wParam);
}
