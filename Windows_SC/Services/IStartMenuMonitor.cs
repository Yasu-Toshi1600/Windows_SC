using System;

namespace Windows_SC.Services;

internal interface IStartMenuMonitor : IDisposable
{
    event EventHandler? SnapshotChanged;

    event EventHandler? ReadyChanged;

    event EventHandler? StartConfirmationExpired;

    StartMenuSnapshot Snapshot { get; }

    bool IsReady { get; }

    void Start();

    bool RecoverAfterResume();

    void BeginPresentationRecovery();

    void CancelPresentationRecovery();

    void NotifyWindowsKeyReleased();

    void NotifyStartMenuClosing();

    void SetLauncherVisible(bool isVisible);

    void SetLauncherInteractive(bool isInteractive);
}
