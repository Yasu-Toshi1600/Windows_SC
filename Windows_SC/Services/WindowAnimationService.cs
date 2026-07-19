using Microsoft.UI.Dispatching;
using System;
using Windows.Graphics;

namespace Windows_SC.Services;

internal sealed class WindowAnimationService : IWindowAnimationService
{
    private const double OffsetEffectivePixels = 24;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(160);

    private readonly IntPtr _windowHandle;
    private readonly IWindowInteropService _windowInteropService;
    private readonly ILauncherPlacementService _placementService;
    private readonly DispatcherQueueTimer _timer;
    private readonly bool _animationsEnabled;
    private RectInt32 _currentRect;
    private RectInt32 _startRect;
    private RectInt32 _endRect;
    private DateTimeOffset _startedAt;
    private bool _isRunning;
    private bool _isHide;
    private string _hideReason = string.Empty;

    public WindowAnimationService(
        IntPtr windowHandle,
        DispatcherQueue dispatcherQueue,
        IWindowInteropService windowInteropService,
        ILauncherPlacementService placementService,
        bool animationsEnabled)
    {
        _windowHandle = windowHandle;
        _windowInteropService = windowInteropService;
        _placementService = placementService;
        _animationsEnabled = animationsEnabled;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.IsRepeating = true;
        _timer.Tick += Timer_Tick;
    }

    public event EventHandler<WindowHideCompletedEventArgs>? HideCompleted;

    public bool IsHideRunning => _isRunning && _isHide;

    public void SetPosition(RectInt32 rectangle) => _currentRect = rectangle;

    public void PrepareShow(RectInt32 targetRect, PointInt32 dpiPoint)
    {
        Stop();
        _currentRect = targetRect;

        if (!_animationsEnabled)
        {
            return;
        }

        int offset = _placementService.ConvertEffectivePixelsToPhysical(
            OffsetEffectivePixels,
            dpiPoint);
        _startRect = OffsetVertically(targetRect, offset);
        _endRect = targetRect;
        _currentRect = _startRect;
        Move(_currentRect);
    }

    public void StartShow()
    {
        if (!_animationsEnabled)
        {
            return;
        }

        _startedAt = DateTimeOffset.UtcNow;
        _isHide = false;
        _isRunning = true;
        _timer.Start();
    }

    public bool StartHide(RectInt32 targetRect, PointInt32 dpiPoint, string reason)
    {
        if (!_animationsEnabled)
        {
            return false;
        }

        Stop();
        int offset = _placementService.ConvertEffectivePixelsToPhysical(
            OffsetEffectivePixels,
            dpiPoint);
        _startRect = _currentRect;
        _endRect = OffsetVertically(targetRect, offset);
        _startedAt = DateTimeOffset.UtcNow;
        _isHide = true;
        _isRunning = true;
        _hideReason = reason;
        _timer.Start();
        return true;
    }

    public void Stop()
    {
        _timer.Stop();
        _isRunning = false;
        _isHide = false;
        _hideReason = string.Empty;
    }

    public void Dispose()
    {
        Stop();
        _timer.Tick -= Timer_Tick;
    }

    private void Timer_Tick(DispatcherQueueTimer sender, object args)
    {
        double progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _startedAt).TotalMilliseconds / Duration.TotalMilliseconds,
            0,
            1);
        double easedProgress = _isHide
            ? progress * progress * progress
            : 1 - Math.Pow(1 - progress, 3);

        _currentRect = InterpolateRectangle(_startRect, _endRect, easedProgress);
        Move(_currentRect);

        if (progress < 1)
        {
            return;
        }

        bool completedHide = _isHide;
        string reason = _hideReason;
        Stop();

        if (completedHide)
        {
            HideCompleted?.Invoke(this, new WindowHideCompletedEventArgs(reason));
        }
    }

    private void Move(RectInt32 rectangle) =>
        _windowInteropService.Move(_windowHandle, rectangle.X, rectangle.Y);

    private static RectInt32 OffsetVertically(RectInt32 rectangle, int offset) =>
        new(rectangle.X, rectangle.Y + offset, rectangle.Width, rectangle.Height);

    private static RectInt32 InterpolateRectangle(
        RectInt32 start,
        RectInt32 end,
        double progress) =>
        new(
            Interpolate(start.X, end.X, progress),
            Interpolate(start.Y, end.Y, progress),
            Interpolate(start.Width, end.Width, progress),
            Interpolate(start.Height, end.Height, progress));

    private static int Interpolate(int start, int end, double progress) =>
        (int)Math.Round(start + ((end - start) * progress));
}
