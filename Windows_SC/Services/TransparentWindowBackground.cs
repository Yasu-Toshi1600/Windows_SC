using System;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal static class TransparentWindowBackground
{
    public const uint EraseBackgroundMessage = 0x0014;
    public const uint CompositionChangedMessage = 0x031E;
    public const uint NonClientSizeMessage = 0x0083; // WM_NCCALCSIZE
    private const int CornerPreferenceAttribute = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int BorderColorAttribute = 34; // DWMWA_BORDER_COLOR (Windows 11)
    private const uint NoBorderColor = 0xFFFFFFFE; // DWMWA_COLOR_NONE

    public static int Configure(IntPtr windowHandle)
    {
        // RootBorder owns the only rounded outline. An OS-rounded host would
        // contribute a second contour even with DWMWA_COLOR_NONE applied.
        uint cornerPreference = 1; // DWMWCP_DONOTROUND
        int cornerResult = DwmSetWindowAttribute(windowHandle, CornerPreferenceAttribute,
            ref cornerPreference, sizeof(uint));
        // Honor the client surface's alpha instead of painting an opaque window
        // background. The region lies outside the client area; no desktop blur.
        Margins margins = default;
        int result = DwmExtendFrameIntoClientArea(windowHandle, ref margins);
        if (result < 0) return result;
        IntPtr region = CreateRectRgn(-2, -2, -1, -1);
        if (region == IntPtr.Zero)
            return Marshal.GetHRForLastWin32Error();
        try
        {
            BlurBehind blur = new() { Flags = 3, Enable = 1, Region = region };
            result = DwmEnableBlurBehindWindow(windowHandle, ref blur);
        }
        finally
        {
            DeleteObject(region);
        }

        IntPtr dc = GetDC(windowHandle);
        if (dc != IntPtr.Zero)
        {
            try { Clear(windowHandle, dc); }
            finally { ReleaseDC(windowHandle, dc); }
        }
        uint borderColor = NoBorderColor;
        int borderResult = DwmSetWindowAttribute(windowHandle, BorderColorAttribute,
            ref borderColor, sizeof(uint));
        if (result < 0) return result;
        return cornerResult < 0 ? cornerResult : borderResult;
    }

    // Must run after installing the WM_NCCALCSIZE handler. Changing only the
    // border color does not remove the native frame's reserved client insets.
    public static int RecalculateFrame(IntPtr windowHandle) =>
        SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020)
            ? 0 : Marshal.GetHRForLastWin32Error();

    public static bool Clear(IntPtr windowHandle, IntPtr dc)
    {
        // A black GDI fill clears the backing pixels (including alpha). The
        // Composition brush and XAML content are composed over this surface.
        return dc != IntPtr.Zero && GetClientRect(windowHandle, out Rect rect)
            && FillRect(dc, ref rect, GetStockObject(4 /* BLACK_BRUSH */)) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        public uint Flags;
        public int Enable;
        public IntPtr Region;
        public int TransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute,
        ref uint value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr window, ref BlurBehind blur);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
}
