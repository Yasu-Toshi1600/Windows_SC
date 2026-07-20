using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal sealed class GlobalInputService : IGlobalInputService
{
    private const int HotKeyId = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VirtualKeySpace = 0x20;
    private const uint WmHotKey = 0x0312;

    private readonly GlobalWindowsKeyMonitor _windowsKeyMonitor;
    private IntPtr _windowHandle;
    private bool _isStarted;
    private bool _isDisposed;

    public GlobalInputService(DiagnosticLogger logger)
    {
        _windowsKeyMonitor = new GlobalWindowsKeyMonitor(
            () => WindowsKeyReleasedAlone?.Invoke(this, EventArgs.Empty),
            logger.WriteDetailed);
    }

    public event EventHandler? ManualToggleRequested;

    public event EventHandler? WindowsKeyReleasedAlone;

    public void Start(IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isStarted)
        {
            return;
        }

        if (!RegisterHotKey(
            windowHandle,
            HotKeyId,
            ModControl | ModAlt | ModNoRepeat,
            VirtualKeySpace))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Ctrl+Alt+Space のグローバルホットキーを登録できませんでした。");
        }

        _windowHandle = windowHandle;
        try
        {
            _windowsKeyMonitor.Start();
            _isStarted = true;
        }
        catch
        {
            UnregisterHotKey(_windowHandle, HotKeyId);
            _windowHandle = IntPtr.Zero;
            throw;
        }
    }

    public bool TryHandleWindowMessage(uint message, IntPtr wParam)
    {
        if (message != WmHotKey || wParam.ToInt32() != HotKeyId)
        {
            return false;
        }

        ManualToggleRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _windowsKeyMonitor.Dispose();

        if (_windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HotKeyId);
            _windowHandle = IntPtr.Zero;
        }

        _isStarted = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        IntPtr windowHandle,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
