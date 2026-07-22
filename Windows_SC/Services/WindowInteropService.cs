using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal sealed class WindowInteropService(
    IGlobalInputService inputService,
    ISystemTrayService systemTrayService) : IWindowInteropService
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmDpiChanged = 0x02E0;
    private const int SpiSetWorkArea = 0x002F;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int VirtualKeyEscape = 0x1B;
    private const int GwlpWndProc = -4;
    private const uint ClsctxAll = 0x17;
    private static readonly Guid VirtualDesktopManagerClassId =
        new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid VirtualDesktopManagerInterfaceId =
        new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");

    private WindowProcedure? _activeWindowProcedure;
    private IntPtr _windowHandle;
    private IntPtr _previousWindowProcedure;
    private bool _isDisposed;

    public event EventHandler? EscapePressed;
    public event EventHandler<DisplayEnvironmentChangedEventArgs>? DisplayEnvironmentChanged;

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

        systemTrayService.Start(windowHandle);
    }

    public bool IsForeground(IntPtr windowHandle) => GetForegroundWindow() == windowHandle;

    public bool TryActivate(IntPtr windowHandle)
    {
        if (IsForeground(windowHandle))
        {
            return true;
        }

        _ = SetForegroundWindow(windowHandle);
        return IsForeground(windowHandle);
    }

    public VirtualDesktopMoveResult MoveToCurrentVirtualDesktop(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return new(VirtualDesktopMoveStatus.Failed, unchecked((int)0x80070057));
        }

        IVirtualDesktopManager? manager = null;
        try
        {
            Guid classId = VirtualDesktopManagerClassId;
            Guid interfaceId = VirtualDesktopManagerInterfaceId;
            int result = CoCreateInstance(
                ref classId,
                IntPtr.Zero,
                ClsctxAll,
                ref interfaceId,
                out manager);
            if (result < 0 || manager is null)
            {
                return new(VirtualDesktopMoveStatus.Failed, result);
            }

            result = manager.IsWindowOnCurrentVirtualDesktop(
                windowHandle,
                out bool isOnCurrentDesktop);
            if (result >= 0 && isOnCurrentDesktop)
            {
                return new(VirtualDesktopMoveStatus.AlreadyCurrent);
            }

            if (!TryGetCurrentDesktopId(manager, windowHandle, out Guid desktopId))
            {
                return new(VirtualDesktopMoveStatus.ReferenceWindowUnavailable, result);
            }

            result = manager.MoveWindowToDesktop(windowHandle, ref desktopId);
            if (result < 0)
            {
                return new(VirtualDesktopMoveStatus.Failed, result);
            }

            result = manager.IsWindowOnCurrentVirtualDesktop(
                windowHandle,
                out isOnCurrentDesktop);
            return result >= 0 && isOnCurrentDesktop
                ? new(VirtualDesktopMoveStatus.Moved)
                : new(VirtualDesktopMoveStatus.Failed, result);
        }
        catch (Exception exception)
        {
            return new(VirtualDesktopMoveStatus.Failed, exception.HResult);
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
            {
                _ = Marshal.ReleaseComObject(manager);
            }
        }
    }

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
        if (systemTrayService.TryHandleWindowMessage(message, wParam, lParam, out IntPtr result))
        {
            return result;
        }

        if (inputService.TryHandleWindowMessage(message, wParam))
        {
            return IntPtr.Zero;
        }

        if (message == WmKeyDown && wParam.ToInt32() == VirtualKeyEscape)
        {
            EscapePressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        string? displayChangeReason = message switch
        {
            WmDisplayChange => "display-change",
            WmDpiChanged => "dpi-change",
            WmSettingChange when wParam.ToInt32() == SpiSetWorkArea => "work-area-change",
            WmPowerBroadcast when wParam.ToInt32() is
                PbtApmResumeSuspend or PbtApmResumeAutomatic => "resume",
            _ => null
        };
        if (displayChangeReason is not null)
        {
            DisplayEnvironmentChanged?.Invoke(
                this,
                new DisplayEnvironmentChangedEventArgs(displayChangeReason));
        }

        return CallWindowProc(_previousWindowProcedure, windowHandle, message, wParam, lParam);
    }

    private static bool TryGetCurrentDesktopId(
        IVirtualDesktopManager manager,
        IntPtr launcherWindow,
        out Guid desktopId)
    {
        desktopId = Guid.Empty;
        IntPtr foregroundWindow = GetForegroundWindow();
        if (TryGetDesktopIdFromCurrentWindow(
            manager,
            foregroundWindow,
            launcherWindow,
            out desktopId))
        {
            return true;
        }

        Guid foundDesktopId = Guid.Empty;
        _ = EnumWindows((candidate, _) =>
        {
            if (!IsWindowVisible(candidate)
                || !TryGetDesktopIdFromCurrentWindow(
                    manager,
                    candidate,
                    launcherWindow,
                    out Guid candidateDesktopId))
            {
                return true;
            }

            foundDesktopId = candidateDesktopId;
            return false;
        }, IntPtr.Zero);
        desktopId = foundDesktopId;
        return foundDesktopId != Guid.Empty;
    }

    private static bool TryGetDesktopIdFromCurrentWindow(
        IVirtualDesktopManager manager,
        IntPtr candidate,
        IntPtr launcherWindow,
        out Guid desktopId)
    {
        desktopId = Guid.Empty;
        if (candidate == IntPtr.Zero || candidate == launcherWindow)
        {
            return false;
        }

        int result = manager.IsWindowOnCurrentVirtualDesktop(
            candidate,
            out bool isOnCurrentDesktop);
        if (result < 0 || !isOnCurrentDesktop)
        {
            return false;
        }

        result = manager.GetWindowDesktopId(candidate, out desktopId);
        return result >= 0 && desktopId != Guid.Empty;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    private delegate bool EnumWindowsProcedure(IntPtr windowHandle, IntPtr lParam);

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(
            IntPtr topLevelWindow,
            [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid classId,
        IntPtr outerUnknown,
        uint context,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IVirtualDesktopManager? instance);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProcedure enumProcedure,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

}
