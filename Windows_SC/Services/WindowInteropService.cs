using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal sealed class WindowInteropService(IGlobalInputService inputService) : IWindowInteropService
{
    private const uint WmKeyDown = 0x0100;
    private const int VirtualKeyEscape = 0x1B;
    private const int GwlpWndProc = -4;

    private WindowProcedure? _activeWindowProcedure;
    private IntPtr _windowHandle;
    private IntPtr _previousWindowProcedure;
    private bool _isDisposed;

    public event EventHandler? EscapePressed;

    public void Start(IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_windowHandle != IntPtr.Zero)
        {
            return;
        }

        _windowHandle = windowHandle;
        _activeWindowProcedure = WindowMessageHandler;
        IntPtr procedurePointer = Marshal.GetFunctionPointerForDelegate(_activeWindowProcedure);
        _previousWindowProcedure = SetWindowLongPtr(windowHandle, GwlpWndProc, procedurePointer);

        if (_previousWindowProcedure == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                _windowHandle = IntPtr.Zero;
                throw new Win32Exception(error, "ウィンドウメッセージの監視を開始できませんでした。");
            }
        }
    }

    public bool IsForeground(IntPtr windowHandle) => GetForegroundWindow() == windowHandle;

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_windowHandle != IntPtr.Zero && _previousWindowProcedure != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowHandle, GwlpWndProc, _previousWindowProcedure);
        }

        _windowHandle = IntPtr.Zero;
        _previousWindowProcedure = IntPtr.Zero;
        _activeWindowProcedure = null;
    }

    private IntPtr WindowMessageHandler(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (inputService.TryHandleWindowMessage(message, wParam))
        {
            return IntPtr.Zero;
        }

        if (message == WmKeyDown && wParam.ToInt32() == VirtualKeyEscape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return CallWindowProc(_previousWindowProcedure, windowHandle, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr windowHandle, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr previousWindowProcedure,
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

}
