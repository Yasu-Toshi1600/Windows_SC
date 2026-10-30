using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows_SC.Services;

// Production ownership logic with counted API doubles, not native memory/GUI.
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var dispatcher = new DispatcherQueue();
var surface = new UIElement();
var motion = new CompositionLauncherMotionService(dispatcher, true);
motion.Attach(surface);
int completed = 0;
motion.Completed += (_, _) => completed++;
Check(CompositionObject.Live == 3 && surface.Attached.Count == 2, "Attach ownership mismatch");
for (int i = 0; i < 500; i++)
{
    motion.StartEntrance(24, false);
    var first = Compositor.LastBatch!;
    var stale = first.Callback;
    motion.StartExit(24, "test-reversal");
    Check(first.IsDisposed && first.Callback is null, "Reversed batch retained handler/resources");
    stale?.Invoke(first, new CompositionBatchCompletedEventArgs());
    dispatcher.Drain();
    Check(motion.IsRunning, "Stale completion stopped current animation");
    Compositor.LastBatch!.Complete();
    dispatcher.Drain();
    Check(!motion.IsRunning && CompositionObject.Live == 3, "Completed animation retained resources");
}
Check(completed == 500, "Wrong completion count");
motion.StartEntrance(24, false);
motion.SetHidden(24);
Check(CompositionObject.Live == 3, "Canceled transition retained resources");
motion.StartEntrance(24, false);
motion.AnimationsEnabled = false;
Check(!motion.StartExit(24, "disabled") && CompositionObject.Live == 3,
    "Disabled animation retained previous transition");
motion.AnimationsEnabled = true;
motion.StartEntrance(24, false);
Compositor.LastBatch!.Complete(); // Completion queued when Dispose runs.
motion.Dispose();
dispatcher.Drain();
motion.Dispose();
Check(completed == 500 && CompositionObject.Live == 0 && surface.Attached.Count == 0,
    "Shutdown retained animations or delivered completion");
Check(!Compositor.Shared.Disposed, "Borrowed XAML compositor was disposed");
Console.WriteLine("PASS: reversal/completion x500, cancellation, animation disable, queued completion after disposal, all owned resources released");

namespace Windows.Foundation
{
    public delegate void TypedEventHandler<TSender, TArgs>(TSender sender, TArgs args);
}
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        private readonly Queue<Action> _pending = new();
        public bool TryEnqueue(Action action) { _pending.Enqueue(action); return true; }
        public void Drain() { while (_pending.TryDequeue(out var action)) action(); }
    }
}
namespace Microsoft.UI.Xaml
{
    public sealed class UIElement
    {
        public HashSet<CompositionObject> Attached { get; } = new();
        public void StartAnimation(CompositionObject animation) => Attached.Add(animation);
        public void StopAnimation(CompositionObject animation) => Attached.Remove(animation);
    }
}
namespace Microsoft.UI.Xaml.Hosting { }
namespace Microsoft.UI.Xaml.Media
{
    public static class CompositionTarget
    {
        public static Compositor GetCompositorForCurrentThread() => Compositor.Shared;
    }
}
namespace Microsoft.UI.Composition
{
    using System.Numerics;
    using Windows.Foundation;

    public class CompositionObject : IDisposable
    {
        public static int Live { get; private set; }
        public bool IsDisposed { get; private set; }
        public CompositionObject() => Live++;
        public virtual void Dispose()
        {
            if (!IsDisposed) { IsDisposed = true; Live--; }
        }
    }
    public sealed class Compositor : IDisposable
    {
        public static Compositor Shared { get; } = new();
        public static CompositionScopedBatch? LastBatch { get; private set; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public CompositionPropertySet CreatePropertySet() => new();
        public ExpressionAnimation CreateExpressionAnimation(string expression) => new();
        public CubicBezierEasingFunction CreateCubicBezierEasingFunction(Vector2 a, Vector2 b) => new();
        public Vector3KeyFrameAnimation CreateVector3KeyFrameAnimation() => new();
        public ScalarKeyFrameAnimation CreateScalarKeyFrameAnimation() => new();
        public CompositionScopedBatch CreateScopedBatch(CompositionBatchTypes types) => LastBatch = new();
    }
    public enum CompositionGetValueStatus { Succeeded }
    public enum CompositionBatchTypes { Animation }
    public sealed class CompositionPropertySet : CompositionObject
    {
        private Vector3 _translation;
        private float _opacity;
        public Compositor Compositor => Compositor.Shared;
        public void InsertVector3(string key, Vector3 value) => _translation = value;
        public void InsertScalar(string key, float value) => _opacity = value;
        public CompositionGetValueStatus TryGetVector3(string key, out Vector3 value)
        { value = _translation; return CompositionGetValueStatus.Succeeded; }
        public CompositionGetValueStatus TryGetScalar(string key, out float value)
        { value = _opacity; return CompositionGetValueStatus.Succeeded; }
        public void StartAnimation(string key, CompositionObject animation) { }
        public void StopAnimation(string key) { }
    }
    public sealed class ExpressionAnimation : CompositionObject
    {
        public string Target { get; set; } = "";
        public void SetReferenceParameter(string key, CompositionPropertySet value) { }
    }
    public sealed class CubicBezierEasingFunction : CompositionObject { }
    public sealed class Vector3KeyFrameAnimation : CompositionObject
    {
        public TimeSpan Duration { get; set; }
        public void InsertKeyFrame(float progress, Vector3 value, CubicBezierEasingFunction? easing = null) { }
    }
    public sealed class ScalarKeyFrameAnimation : CompositionObject
    {
        public TimeSpan Duration { get; set; }
        public void InsertKeyFrame(float progress, float value, CubicBezierEasingFunction? easing = null) { }
    }
    public sealed class CompositionBatchCompletedEventArgs : EventArgs { }
    public sealed class CompositionScopedBatch : CompositionObject
    {
        public event TypedEventHandler<object, CompositionBatchCompletedEventArgs>? Completed;
        public TypedEventHandler<object, CompositionBatchCompletedEventArgs>? Callback => Completed;
        public void End() { }
        public void Complete() => Completed?.Invoke(this, new());
    }
}
