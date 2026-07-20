using System;
using Microsoft.UI.Xaml;

namespace Windows_SC.Services;

internal interface ILauncherMotionService : IDisposable
{
    event EventHandler<LauncherMotionCompletedEventArgs>? Completed;

    bool AnimationsEnabled { get; set; }

    bool IsRunning { get; }

    bool IsExiting { get; }

    void Attach(UIElement surface);

    void PrepareEntrance(float translationY, bool startLinked);

    bool StartEntrance(float translationY, bool startLinked);

    bool StartExit(float translationY, string reason);

    void SetVisible();

    void SetHidden(float translationY);
}

internal enum LauncherMotionDirection
{
    Entrance,
    Exit
}

internal sealed class LauncherMotionCompletedEventArgs(
    LauncherMotionDirection direction,
    string reason,
    TimeSpan elapsed) : EventArgs
{
    public LauncherMotionDirection Direction { get; } = direction;

    public string Reason { get; } = reason;

    public TimeSpan Elapsed { get; } = elapsed;
}
