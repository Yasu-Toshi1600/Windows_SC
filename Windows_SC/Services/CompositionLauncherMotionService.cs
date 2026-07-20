using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Windows_SC.Services;

internal sealed class CompositionLauncherMotionService : ILauncherMotionService
{
    private static readonly TimeSpan StartEntranceDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ManualEntranceDuration = TimeSpan.FromMilliseconds(167);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(83);

    private readonly DispatcherQueue _dispatcherQueue;
    private UIElement? _surface;
    private CompositionPropertySet? _motionProperties;
    private CompositionScopedBatch? _batch;
    private Vector3 _fromTranslation;
    private Vector3 _toTranslation;
    private float _fromOpacity;
    private float _toOpacity;
    private long _startedTimestamp;
    private TimeSpan _duration;
    private TimeSpan _opacityDuration;
    private LauncherMotionDirection _direction;
    private string _reason = string.Empty;
    private long _generation;
    private bool _isDisposed;

    public CompositionLauncherMotionService(
        DispatcherQueue dispatcherQueue,
        bool animationsEnabled)
    {
        _dispatcherQueue = dispatcherQueue;
        AnimationsEnabled = animationsEnabled;
    }

    public event EventHandler<LauncherMotionCompletedEventArgs>? Completed;

    public bool AnimationsEnabled { get; set; }

    public bool IsRunning { get; private set; }

    public bool IsExiting => IsRunning && _direction == LauncherMotionDirection.Exit;

    public void Attach(UIElement surface)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _surface = surface;
        Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
        _motionProperties = compositor.CreatePropertySet();
        _motionProperties.InsertVector3("Translation", Vector3.Zero);
        _motionProperties.InsertScalar("Opacity", 1);

        ExpressionAnimation translationExpression = compositor.CreateExpressionAnimation(
            "motion.Translation");
        translationExpression.Target = "Translation";
        translationExpression.SetReferenceParameter("motion", _motionProperties);
        surface.StartAnimation(translationExpression);

        ExpressionAnimation opacityExpression = compositor.CreateExpressionAnimation(
            "motion.Opacity");
        opacityExpression.Target = "Opacity";
        opacityExpression.SetReferenceParameter("motion", _motionProperties);
        surface.StartAnimation(opacityExpression);
    }

    public void PrepareEntrance(float translationY, bool startLinked)
    {
        StopAtCurrentValue();
        SetValues(new Vector3(0, translationY, 0), startLinked ? 1 : 0);
    }

    public bool StartEntrance(float translationY, bool startLinked)
    {
        TimeSpan duration = startLinked ? StartEntranceDuration : ManualEntranceDuration;
        return StartTransition(
            LauncherMotionDirection.Entrance,
            Vector3.Zero,
            1,
            duration,
            startLinked ? "start-linked" : "manual");
    }

    public bool StartExit(float translationY, string reason) =>
        StartTransition(
            LauncherMotionDirection.Exit,
            new Vector3(0, translationY, 0),
            0,
            ExitDuration,
            reason);

    public void SetVisible()
    {
        StopAtCurrentValue();
        SetValues(Vector3.Zero, 1);
    }

    public void SetHidden(float translationY)
    {
        StopAtCurrentValue();
        SetValues(new Vector3(0, translationY, 0), 0);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _generation++;
        StopAtCurrentValue();
        _batch?.Dispose();
        _batch = null;
        _motionProperties?.Dispose();
        _motionProperties = null;
        _surface = null;
    }

    private bool StartTransition(
        LauncherMotionDirection direction,
        Vector3 targetTranslation,
        float targetOpacity,
        TimeSpan duration,
        string reason)
    {
        if (_surface is null || _motionProperties is null)
        {
            throw new InvalidOperationException("モーション対象が設定されていません。");
        }

        CaptureCurrentValues();
        StopPropertyAnimations();

        _fromTranslation = ReadTranslation();
        _fromOpacity = ReadOpacity();
        _toTranslation = targetTranslation;
        _toOpacity = targetOpacity;
        _direction = direction;
        _reason = reason;
        _duration = duration;
        _opacityDuration = duration < FadeDuration ? duration : FadeDuration;
        _startedTimestamp = Stopwatch.GetTimestamp();

        if (!AnimationsEnabled)
        {
            SetValues(targetTranslation, targetOpacity);
            IsRunning = false;
            return false;
        }

        Compositor compositor = _motionProperties.Compositor;
        CubicBezierEasingFunction easing = direction == LauncherMotionDirection.Entrance
            ? compositor.CreateCubicBezierEasingFunction(
                new Vector2(0, 0),
                new Vector2(0, 1))
            : compositor.CreateCubicBezierEasingFunction(
                new Vector2(1, 0),
                new Vector2(1, 1));

        Vector3KeyFrameAnimation translationAnimation =
            compositor.CreateVector3KeyFrameAnimation();
        translationAnimation.InsertKeyFrame(0, _fromTranslation);
        translationAnimation.InsertKeyFrame(1, targetTranslation, easing);
        translationAnimation.Duration = duration;

        ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.InsertKeyFrame(0, _fromOpacity);
        opacityAnimation.InsertKeyFrame(1, targetOpacity, easing);
        opacityAnimation.Duration = _opacityDuration;

        long generation = ++_generation;
        _batch?.Dispose();
        _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch.Completed += (_, _) =>
        {
            _dispatcherQueue.TryEnqueue(() => CompleteTransition(generation));
        };

        _motionProperties.StartAnimation("Translation", translationAnimation);
        _motionProperties.StartAnimation("Opacity", opacityAnimation);
        _batch.End();
        IsRunning = true;
        return true;
    }

    private void CompleteTransition(long generation)
    {
        if (_isDisposed || generation != _generation || !IsRunning)
        {
            return;
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(_startedTimestamp);
        LauncherMotionDirection direction = _direction;
        string reason = _reason;
        StopPropertyAnimations();
        SetValues(_toTranslation, _toOpacity);
        IsRunning = false;
        Completed?.Invoke(
            this,
            new LauncherMotionCompletedEventArgs(direction, reason, elapsed));
    }

    private void StopAtCurrentValue()
    {
        CaptureCurrentValues();
        StopPropertyAnimations();
        IsRunning = false;
        _generation++;
    }

    private void CaptureCurrentValues()
    {
        if (!IsRunning || _motionProperties is null)
        {
            return;
        }

        double linearProgress = Math.Clamp(
            Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds
                / Math.Max(1, _duration.TotalMilliseconds),
            0,
            1);
        double easedProgress = _direction == LauncherMotionDirection.Entrance
            ? EaseEntrance(linearProgress)
            : EaseExit(linearProgress);
        Vector3 translation = Vector3.Lerp(
            _fromTranslation,
            _toTranslation,
            (float)easedProgress);
        double opacityLinearProgress = Math.Clamp(
            Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds
                / Math.Max(1, _opacityDuration.TotalMilliseconds),
            0,
            1);
        double opacityEasedProgress = _direction == LauncherMotionDirection.Entrance
            ? EaseEntrance(opacityLinearProgress)
            : EaseExit(opacityLinearProgress);
        float opacity = _fromOpacity
            + ((_toOpacity - _fromOpacity) * (float)opacityEasedProgress);
        SetValues(translation, opacity);
    }

    private void StopPropertyAnimations()
    {
        _motionProperties?.StopAnimation("Translation");
        _motionProperties?.StopAnimation("Opacity");
    }

    private void SetValues(Vector3 translation, float opacity)
    {
        _motionProperties?.InsertVector3("Translation", translation);
        _motionProperties?.InsertScalar("Opacity", Math.Clamp(opacity, 0, 1));
    }

    private Vector3 ReadTranslation() =>
        _motionProperties?.TryGetVector3("Translation", out Vector3 value)
            == CompositionGetValueStatus.Succeeded
                ? value
                : Vector3.Zero;

    private float ReadOpacity() =>
        _motionProperties?.TryGetScalar("Opacity", out float value)
            == CompositionGetValueStatus.Succeeded
                ? value
                : 1;

    private static double EaseEntrance(double progress)
    {
        double t = Math.Pow(progress, 1d / 3d);
        return (3 * t * t) - (2 * t * t * t);
    }

    private static double EaseExit(double progress)
    {
        double t = 1 - Math.Pow(1 - progress, 1d / 3d);
        return (3 * t * t) - (2 * t * t * t);
    }
}
