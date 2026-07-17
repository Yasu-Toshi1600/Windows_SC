using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Windows_SC.Services;

internal sealed class LauncherPlacementService(DiagnosticLogger logger) : ILauncherPlacementService
{
    private const int LauncherWidth = 500;
    private const int LauncherHeight = 600;
    private const int LauncherMargin = 12;
    private const double PhonePanelReservedEffectivePixels = 281;
    private const double LauncherBottomMarginEffectivePixels = 12;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public bool TryCalculate(
        StartMenuSnapshot? startMenuSnapshot,
        bool assumePhonePanelVisible,
        out LauncherPlacement placement)
    {
        DisplayArea displayArea;
        PointInt32 dpiPoint;
        int x;
        int y;

        if (startMenuSnapshot is { IsVisible: true, Bounds: not null } snapshot)
        {
            RectInt32 startBounds = snapshot.Bounds.Value;
            int anchorRight = startBounds.X + startBounds.Width;

            if (snapshot.PhonePanelBounds is RectInt32 phoneBounds)
            {
                anchorRight = Math.Max(anchorRight, phoneBounds.X + phoneBounds.Width);
            }

            PointInt32 startCenter = new(
                startBounds.X + (startBounds.Width / 2),
                startBounds.Y + (startBounds.Height / 2));
            displayArea = DisplayArea.GetFromPoint(startCenter, DisplayAreaFallback.Nearest)
                ?? DisplayArea.Primary;
            RectInt32 workArea = displayArea.WorkArea;
            int bottomMargin = ConvertEffectivePixelsToPhysical(
                LauncherBottomMarginEffectivePixels,
                startCenter);

            if (assumePhonePanelVisible && snapshot.PhonePanelBounds is null)
            {
                anchorRight += ConvertEffectivePixelsToPhysical(
                    PhonePanelReservedEffectivePixels,
                    startCenter);
            }

            x = anchorRight + LauncherMargin;
            y = Math.Clamp(
                startBounds.Y + startBounds.Height - LauncherHeight - bottomMargin,
                workArea.Y,
                workArea.Y + workArea.Height - LauncherHeight);

            if (x + LauncherWidth > workArea.X + workArea.Width)
            {
                logger.Write(
                    $"[WindowPlacement] result=failed reason=insufficient-right-space " +
                    $"start=({startBounds.X},{startBounds.Y},{startBounds.Width},{startBounds.Height}) " +
                    $"work-area=({workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}) " +
                    $"required-width={LauncherWidth}");
                placement = default;
                return false;
            }

            dpiPoint = startCenter;
        }
        else
        {
            GetCursorPos(out NativePoint cursorPosition);
            PointInt32 cursorPoint = new(cursorPosition.X, cursorPosition.Y);
            displayArea = DisplayArea.GetFromPoint(cursorPoint, DisplayAreaFallback.Nearest)
                ?? DisplayArea.Primary;
            RectInt32 workArea = displayArea.WorkArea;
            x = workArea.X + ((workArea.Width - LauncherWidth) / 2);
            y = workArea.Y + ((workArea.Height - LauncherHeight) / 2);
            dpiPoint = cursorPoint;
        }

        RectInt32 targetWorkArea = displayArea.WorkArea;
        placement = new LauncherPlacement(
            new RectInt32(x, y, LauncherWidth, LauncherHeight),
            targetWorkArea,
            dpiPoint);
        return true;
    }

    public int ConvertEffectivePixelsToPhysical(double effectivePixels, PointInt32 point)
    {
        NativePoint nativePoint = new() { X = point.X, Y = point.Y };
        IntPtr monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);

        if (monitor != IntPtr.Zero
            && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0)
        {
            return (int)Math.Round(effectivePixels * dpiX / 96d);
        }

        return (int)Math.Round(effectivePixels);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
