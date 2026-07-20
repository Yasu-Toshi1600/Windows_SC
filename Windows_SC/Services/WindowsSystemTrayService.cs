using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Windows_SC.Services;

internal sealed class WindowsSystemTrayService(DiagnosticLogger logger) : ISystemTrayService
{
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = 0x8001;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint NotifyIconVersion4 = 4;
    private const uint WmNull = 0x0000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const uint CommandShowLauncher = 1001;
    private const uint CommandSettings = 1002;
    private const uint CommandExit = 1003;
    private const int IdiApplication = 32512;

    private readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private bool _isIconAdded;
    private bool _usesVersion4;
    private bool _isDisposed;

    public event EventHandler? ShowLauncherRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? ExitRequested;

    public void Start(IntPtr windowHandle)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_windowHandle != IntPtr.Zero)
        {
            return;
        }

        _windowHandle = windowHandle;
        _iconHandle = LoadApplicationIcon(out _ownsIcon);
        AddIcon();
    }

    public bool TryHandleWindowMessage(
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result)
    {
        result = IntPtr.Zero;

        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            _isIconAdded = false;
            AddIcon();
            return false;
        }

        if (message != TrayCallbackMessage)
        {
            return false;
        }

        uint notification = _usesVersion4
            ? unchecked((uint)lParam.ToInt64()) & 0xFFFF
            : unchecked((uint)lParam.ToInt64());

        if ((_usesVersion4 && notification is NinSelect or NinKeySelect)
            || (!_usesVersion4 && notification == WmLButtonUp))
        {
            ShowContextMenu(wParam);
            return true;
        }

        if ((_usesVersion4 && notification == WmContextMenu)
            || (!_usesVersion4 && notification == WmRButtonUp))
        {
            ShowContextMenu(wParam);
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        RemoveIcon();

        if (_ownsIcon && _iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }

        _windowHandle = IntPtr.Zero;
        _iconHandle = IntPtr.Zero;
        _ownsIcon = false;
    }

    private void AddIcon()
    {
        if (_isDisposed || _windowHandle == IntPtr.Zero || _isIconAdded)
        {
            return;
        }

        NotifyIconData iconData = CreateIconData();
        if (!ShellNotifyIcon(NimAdd, ref iconData))
        {
            logger.Write(
                $"[SystemTray] action=add result=failed win32-error={Marshal.GetLastWin32Error()}");
            return;
        }

        _isIconAdded = true;
        iconData.VersionOrTimeout = NotifyIconVersion4;
        _usesVersion4 = ShellNotifyIcon(NimSetVersion, ref iconData);
        logger.Write(
            $"[SystemTray] action=add result=success callback-version=" +
            $"{(_usesVersion4 ? NotifyIconVersion4 : 0)}");
    }

    private void RemoveIcon()
    {
        if (!_isIconAdded || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        NotifyIconData iconData = CreateIconData();
        ShellNotifyIcon(NimDelete, ref iconData);
        _isIconAdded = false;
        _usesVersion4 = false;
        logger.Write("[SystemTray] action=remove");
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = TrayIconId,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip,
        CallbackMessage = TrayCallbackMessage,
        IconHandle = _iconHandle,
        Tip = "Windows_SC",
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private void ShowContextMenu(IntPtr anchorPosition)
    {
        IntPtr menuHandle = CreatePopupMenu();
        if (menuHandle == IntPtr.Zero)
        {
            logger.Write(
                $"[SystemTray] action=open-menu result=failed win32-error={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            AppendMenu(
                menuHandle,
                MfString,
                CommandShowLauncher,
                "ランチャーを表示 (Ctrl+Alt+Space)");
            AppendMenu(menuHandle, MfString, CommandSettings, "設定");
            AppendMenu(menuHandle, MfSeparator, 0, null);
            AppendMenu(menuHandle, MfString, CommandExit, "終了");

            SetForegroundWindow(_windowHandle);
            Point cursorPosition;
            if (_usesVersion4)
            {
                long packedPosition = anchorPosition.ToInt64();
                cursorPosition = new Point
                {
                    X = unchecked((short)(packedPosition & 0xFFFF)),
                    Y = unchecked((short)((packedPosition >> 16) & 0xFFFF))
                };
            }
            else if (!GetCursorPos(out cursorPosition))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            uint command = TrackPopupMenu(
                menuHandle,
                TpmRightButton | TpmNonotify | TpmReturnCmd,
                cursorPosition.X,
                cursorPosition.Y,
                0,
                _windowHandle,
                IntPtr.Zero);
            PostMessage(_windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CommandShowLauncher:
                    logger.Write("[SystemTray] action=show-launcher source=menu");
                    ShowLauncherRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandSettings:
                    logger.Write("[SystemTray] action=open-settings");
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandExit:
                    logger.Write("[SystemTray] action=exit");
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Win32Exception exception)
        {
            logger.Write(
                $"[SystemTray] action=open-menu result=failed exception={exception.NativeErrorCode}");
        }
        finally
        {
            DestroyMenu(menuHandle);
        }
    }

    private static IntPtr LoadApplicationIcon(out bool ownsIcon)
    {
        IntPtr[] smallIcons = new IntPtr[1];
        string? executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath)
            && ExtractIconEx(executablePath, 0, null, smallIcons, 1) > 0
            && smallIcons[0] != IntPtr.Zero)
        {
            ownsIcon = true;
            return smallIcons[0];
        }

        ownsIcon = false;
        return LoadIcon(IntPtr.Zero, new IntPtr(IdiApplication));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        [Out] IntPtr[]? largeIcons,
        [Out] IntPtr[]? smallIcons,
        uint iconCount);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(
        IntPtr menuHandle,
        uint flags,
        uint itemId,
        string? itemText);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menuHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenu", SetLastError = true)]
    private static extern uint TrackPopupMenu(
        IntPtr menuHandle,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr windowHandle,
        IntPtr rectangle);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
