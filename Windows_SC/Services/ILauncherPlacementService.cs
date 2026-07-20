using Windows.Graphics;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal interface ILauncherPlacementService
{
    bool TryCalculate(
        StartMenuSnapshot? startMenuSnapshot,
        bool assumePhonePanelVisible,
        LauncherLayoutMode layoutMode,
        out LauncherPlacement placement);

    int ConvertEffectivePixelsToPhysical(double effectivePixels, PointInt32 point);
}

internal readonly record struct LauncherPlacement(
    RectInt32 TargetRect,
    RectInt32 WorkArea,
    PointInt32 DpiPoint);
