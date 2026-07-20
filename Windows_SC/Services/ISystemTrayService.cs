using System;

namespace Windows_SC.Services;

internal interface ISystemTrayService : IDisposable
{
    event EventHandler? ShowLauncherRequested;

    event EventHandler? SettingsRequested;

    event EventHandler? ExitRequested;

    void Start(IntPtr windowHandle);

    bool TryHandleWindowMessage(
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result);
}
