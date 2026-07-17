using System;

namespace Windows_SC.Services;

internal interface IStartMenuMonitor : IDisposable
{
    event EventHandler? SnapshotChanged;

    StartMenuSnapshot Snapshot { get; }

    void Start();

    void NotifyWindowsKeyReleased();

    void SetLauncherVisible(bool isVisible);
}
