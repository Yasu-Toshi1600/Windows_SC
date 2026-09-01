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

    private readonly DiagnosticLogger _logger;
    private readonly GlobalWindowsKeyMonitor _windowsKeyMonitor;
    private IntPtr _windowHandle;
    private bool _isStarted;
    private bool _isDisposed;
    private bool _isSuppressed;

    public GlobalInputService(DiagnosticLogger logger)
    {
        _logger = logger;
        _windowsKeyMonitor = new GlobalWindowsKeyMonitor(
            () =>
            {
                if (!_isSuppressed)
                {
                    WindowsKeyReleasedAlone?.Invoke(this, EventArgs.Empty);
                }
            },
            shortcutKey => ShortcutKeyCaptured?.Invoke(
                this,
                new ShortcutKeyCapturedEventArgs(shortcutKey)),
            logger.WriteDetailed);
    }

    public event EventHandler? ManualToggleRequested;

    public event EventHandler? WindowsKeyReleasedAlone;

    public event EventHandler<ShortcutKeyCapturedEventArgs>? ShortcutKeyCaptured;

    public void SetSuppressed(bool suppressed)
    {
        _isSuppressed = suppressed;
        _windowsKeyMonitor.SetCaptureEnabled(suppressed);
    }

    public void Start(IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isStarted)
        {
            return;
        }

        _windowHandle = windowHandle;
        try
        {
            StartMonitoring();
        }
        catch
        {
            _windowHandle = IntPtr.Zero;
            throw;
        }
    }

    public bool RecoverAfterResume()
    {
        if (_isDisposed || _windowHandle == IntPtr.Zero)
        {
            _logger.Write(
                "[InputMonitor] action=recover result=skipped reason=not-started");
            return false;
        }

        StopMonitoring();
        try
        {
            StartMonitoring();
            _logger.Write(
                "[InputMonitor] action=recover result=success reason=resume " +
                "components=windows-key-hook,manual-hotkey");
            return true;
        }
        catch (Exception exception)
        {
            _logger.Write(
                $"[InputMonitor] action=recover result=failed reason=resume " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            return false;
        }
    }

    public bool TryHandleWindowMessage(uint message, IntPtr wParam)
    {
        if (message != WmHotKey || wParam.ToInt32() != HotKeyId)
        {
            return false;
        }

        if (_isSuppressed)
        {
            return true;
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
        StopMonitoring();
        _windowHandle = IntPtr.Zero;
    }

    private void StartMonitoring()
    {
        if (!RegisterHotKey(
            _windowHandle,
            HotKeyId,
            ModControl | ModAlt | ModNoRepeat,
            VirtualKeySpace))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Ctrl+Alt+Space のグローバルホットキーを登録できませんでした。");
        }

        try
        {
            _windowsKeyMonitor.Start();
            _isStarted = true;
        }
        catch
        {
            _ = UnregisterHotKey(_windowHandle, HotKeyId);
            throw;
        }
    }

    private void StopMonitoring()
    {
        _windowsKeyMonitor.Dispose();
        if (_isStarted && _windowHandle != IntPtr.Zero)
        {
            _ = UnregisterHotKey(_windowHandle, HotKeyId);
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
