using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Windows_SC.Services;

// One instance per window. The XAML panel continues to draw its own background.
internal sealed class TransparentWindowBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        // Windows.UI.Composition needs Windows.System.DispatcherQueue, separate
        // from WinUI's Microsoft.UI.Dispatching queue. Let WinUI own its shutdown.
        DispatcherQueue.EnsureSystemDispatcherQueue();
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _compositor = new Windows.UI.Composition.Compositor();
        _brush = _compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        connectedTarget.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        _brush?.Dispose();
        _brush = null;
        _compositor?.Dispose();
        _compositor = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
