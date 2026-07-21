using System;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Windows_SC;

internal static class StartMenuBoundsValidator
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MaximumMonitorCoveragePercent = 90;

    internal static bool CoversMostOfMonitor(RectInt32 bounds)
    {
        NativePoint center = new()
        {
            X = bounds.X + (bounds.Width / 2),
            Y = bounds.Y + (bounds.Height / 2)
        };
        nint monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return false;
        }

        MonitorInfo monitorInfo = new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        return CoversMostOf(bounds, monitorInfo.Monitor)
            || CoversMostOf(bounds, monitorInfo.WorkArea);
    }

    private static bool CoversMostOf(RectInt32 bounds, NativeRectangle area)
    {
        int areaWidth = area.Right - area.Left;
        int areaHeight = area.Bottom - area.Top;
        return areaWidth > 0
            && areaHeight > 0
            && (long)bounds.Width * 100 >= (long)areaWidth * MaximumMonitorCoveragePercent
            && (long)bounds.Height * 100 >= (long)areaHeight * MaximumMonitorCoveragePercent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);
}
