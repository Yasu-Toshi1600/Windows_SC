using System;
using Windows.Graphics;

namespace Windows_SC.Services;

internal interface IWindowAnimationService : IDisposable
{
    event EventHandler<WindowHideCompletedEventArgs>? HideCompleted;

    bool IsHideRunning { get; }

    void SetPosition(RectInt32 rectangle);

    void PrepareShow(RectInt32 targetRect, PointInt32 dpiPoint);

    void StartShow();

    bool StartHide(RectInt32 targetRect, PointInt32 dpiPoint, string reason);

    void Stop();
}

internal sealed class WindowHideCompletedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}
