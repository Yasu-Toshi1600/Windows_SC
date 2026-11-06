using System;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

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
    private TypedEventHandler<object, CompositionBatchCompletedEventArgs>? _batchCompleted;
    private ExpressionAnimation? _translationExpression;
    private ExpressionAnimation? _opacityExpression;
    private Vector3KeyFrameAnimation? _translationAnimation;
    private ScalarKeyFrameAnimation? _opacityAnimation;
    private CubicBezierEasingFunction? _easing;
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
        if (_surface is not null) throw new InvalidOperationException("モーション対象は接続済みです。");
        _surface = surface;
        Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
        _motionProperties = compositor.CreatePropertySet();
        _motionProperties.InsertVector3("Translation", Vector3.Zero);
        _motionProperties.InsertScalar("Opacity", 1);

        _translationExpression = compositor.CreateExpressionAnimation(
            "motion.Translation");
        _translationExpression.Target = "Translation";
        _translationExpression.SetReferenceParameter("motion", _motionProperties);
        surface.StartAnimation(_translationExpression);

        _opacityExpression = compositor.CreateExpressionAnimation(
            "motion.Opacity");
        _opacityExpression.Target = "Opacity";
        _opacityExpression.SetReferenceParameter("motion", _motionProperties);
        surface.StartAnimation(_opacityExpression);
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
        IsRunning = false;
        // Attempt every release even if the XAML surface has already closed.
        Exception? failure = null;
        void Release(Action action)
        {
            try { action(); }
            catch (Exception exception) { failure ??= exception; }
        }
        Release(StopPropertyAnimations);
        Release(ReleaseTransitionResources);
        if (_translationExpression is not null)
            Release(() => _surface?.StopAnimation(_translationExpression));
        if (_opacityExpression is not null)
            Release(() => _surface?.StopAnimation(_opacityExpression));
        Release(() => _translationExpression?.Dispose());
        Release(() => _opacityExpression?.Dispose());
        Release(() => _motionProperties?.Dispose());
        _translationExpression = null;
        _opacityExpression = null;
        _motionProperties = null;
        _surface = null;
        Completed = null;
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private bool StartTransition(
        LauncherMotionDirection direction,
        Vector3 targetTranslation,
        float targetOpacity,
        TimeSpan duration,
        string reason)
    {
        if (_isDisposed) return false;
        if (_surface is null || _motionProperties is null)
        {
            throw new InvalidOperationException("モーション対象が設定されていません。");
        }

        CaptureCurrentValues();
        StopPropertyAnimations();
        ReleaseTransitionResources();
        long generation = ++_generation;

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
        _easing = direction == LauncherMotionDirection.Entrance
            ? compositor.CreateCubicBezierEasingFunction(
                new Vector2(0, 0),
                new Vector2(0, 1))
            : compositor.CreateCubicBezierEasingFunction(
                new Vector2(1, 0),
                new Vector2(1, 1));

        _translationAnimation =
            compositor.CreateVector3KeyFrameAnimation();
        _translationAnimation.InsertKeyFrame(0, _fromTranslation);
        _translationAnimation.InsertKeyFrame(1, targetTranslation, _easing);
        _translationAnimation.Duration = duration;

        _opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        _opacityAnimation.InsertKeyFrame(0, _fromOpacity);
        _opacityAnimation.InsertKeyFrame(1, targetOpacity, _easing);
        _opacityAnimation.Duration = _opacityDuration;

        _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batchCompleted = (_, _) =>
        {
            _dispatcherQueue.TryEnqueue(() => CompleteTransition(generation));
        };
        _batch.Completed += _batchCompleted;

        IsRunning = true;
        _motionProperties.StartAnimation("Translation", _translationAnimation);
        _motionProperties.StartAnimation("Opacity", _opacityAnimation);
        _batch.End();
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
        ReleaseTransitionResources();
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
        ReleaseTransitionResources();
    }

    private void ReleaseTransitionResources()
    {
        CompositionScopedBatch? batch = _batch;
        var handler = _batchCompleted;
        var translation = _translationAnimation;
        var opacity = _opacityAnimation;
        var easing = _easing;
        _batch = null;
        _batchCompleted = null;
        _translationAnimation = null;
        _opacityAnimation = null;
        _easing = null;
        try
        {
            if (batch is not null && handler is not null) batch.Completed -= handler;
        }
        finally
        {
            try { batch?.Dispose(); }
            finally
            {
                try { translation?.Dispose(); }
                finally
                {
                    try { opacity?.Dispose(); }
                    finally { easing?.Dispose(); }
                }
            }
        }
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
