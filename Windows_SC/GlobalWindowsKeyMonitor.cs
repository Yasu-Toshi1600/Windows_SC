using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC;

internal sealed class GlobalWindowsKeyMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint VkLeftWindows = 0x5B;
    private const uint VkRightWindows = 0x5C;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const uint LlkhfInjected = 0x10;
    private const uint LlkhfExtended = 0x01;

    private readonly Action _windowsKeyReleasedAlone;
    private readonly Action<ShortcutKeyDefinition> _shortcutKeyCaptured;
    private readonly Action<string> _diagnosticLog;
    private readonly LowLevelKeyboardProcedure _hookProcedure;
    private readonly object _captureLock = new();
    private readonly HashSet<uint> _capturePressedKeys = [];
    private IntPtr _hookHandle;
    private bool _leftWindowsDown;
    private bool _rightWindowsDown;
    private bool _chordDetected;
    private bool _captureEnabled;
    private bool _captureAwaitingResult;
    private ShortcutKeyDefinition? _pendingShortcutKey;

    public GlobalWindowsKeyMonitor(
        Action windowsKeyReleasedAlone,
        Action<ShortcutKeyDefinition> shortcutKeyCaptured,
        Action<string> diagnosticLog)
    {
        _windowsKeyReleasedAlone = windowsKeyReleasedAlone;
        _shortcutKeyCaptured = shortcutKeyCaptured;
        _diagnosticLog = diagnosticLog;
        _hookProcedure = KeyboardHookCallback;
    }

    public void SetCaptureEnabled(bool enabled)
    {
        lock (_captureLock)
        {
            _captureEnabled = enabled;
            _captureAwaitingResult = false;
            _pendingShortcutKey = null;
            _capturePressedKeys.Clear();
        }
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        IntPtr moduleHandle = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProcedure, moduleHandle, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windowsキーの監視を開始できませんでした。");
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            KeyboardHookData data = Marshal.PtrToStructure<KeyboardHookData>(lParam);

            if ((data.Flags & LlkhfInjected) == 0)
            {
                uint message = unchecked((uint)wParam.ToInt64());
                if (TryCaptureShortcutKey(data, message))
                {
                    return new IntPtr(1);
                }

                bool isKeyDown = message is WmKeyDown or WmSysKeyDown;
                bool isKeyUp = message is WmKeyUp or WmSysKeyUp;
                bool isWindowsKey = data.VirtualKey is VkLeftWindows or VkRightWindows;

                if (isKeyDown && isWindowsKey)
                {
                    HandleWindowsKeyDown(data.VirtualKey);
                }
                else if (isKeyDown && (_leftWindowsDown || _rightWindowsDown))
                {
                    _chordDetected = true;
                }
                else if (isKeyUp && isWindowsKey)
                {
                    HandleWindowsKeyUp(data.VirtualKey);
                }
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private bool TryCaptureShortcutKey(KeyboardHookData data, uint message)
    {
        ShortcutKeyDefinition? capturedShortcutKey = null;
        lock (_captureLock)
        {
            if (!_captureEnabled)
            {
                return false;
            }

            bool isKeyDown = message is WmKeyDown or WmSysKeyDown;
            bool isKeyUp = message is WmKeyUp or WmSysKeyUp;
            if (!isKeyDown && !isKeyUp)
            {
                return true;
            }

            if (_captureAwaitingResult)
            {
                return true;
            }

            if (isKeyDown)
            {
                _capturePressedKeys.Add(data.VirtualKey);
                if (_pendingShortcutKey is null
                    && !ShortcutKeyText.IsModifierKey(data.VirtualKey))
                {
                    _pendingShortcutKey = new ShortcutKeyDefinition
                    {
                        Modifiers = GetCapturedModifiers(),
                        VirtualKey = data.VirtualKey,
                        ScanCode = data.ScanCode,
                        IsExtendedKey = (data.Flags & LlkhfExtended) != 0
                    };
                }
            }
            else
            {
                _capturePressedKeys.Remove(data.VirtualKey);
                if (_pendingShortcutKey is not null && _capturePressedKeys.Count == 0)
                {
                    capturedShortcutKey = _pendingShortcutKey;
                    _pendingShortcutKey = null;
                    _captureAwaitingResult = true;
                }
            }
        }

        if (capturedShortcutKey is not null)
        {
            _shortcutKeyCaptured(capturedShortcutKey);
        }

        return true;
    }

    private ShortcutKeyModifiers GetCapturedModifiers()
    {
        ShortcutKeyModifiers modifiers = ShortcutKeyModifiers.None;
        if (ContainsAny(0x11, 0xA2, 0xA3))
        {
            modifiers |= ShortcutKeyModifiers.Control;
        }

        if (ContainsAny(0x12, 0xA4, 0xA5))
        {
            modifiers |= ShortcutKeyModifiers.Alt;
        }

        if (ContainsAny(0x10, 0xA0, 0xA1))
        {
            modifiers |= ShortcutKeyModifiers.Shift;
        }

        if (ContainsAny(VkLeftWindows, VkRightWindows))
        {
            modifiers |= ShortcutKeyModifiers.Windows;
        }

        return modifiers;
    }

    private bool ContainsAny(params uint[] virtualKeys)
    {
        foreach (uint virtualKey in virtualKeys)
        {
            if (_capturePressedKeys.Contains(virtualKey))
            {
                return true;
            }
        }

        return false;
    }

    private void HandleWindowsKeyDown(uint virtualKey)
    {
        bool wasAnyWindowsKeyDown = _leftWindowsDown || _rightWindowsDown;

        if (virtualKey == VkLeftWindows)
        {
            _leftWindowsDown = true;
        }
        else
        {
            _rightWindowsDown = true;
        }

        if (!wasAnyWindowsKeyDown)
        {
            _chordDetected = IsKeyCurrentlyDown(VkShift)
                || IsKeyCurrentlyDown(VkControl)
                || IsKeyCurrentlyDown(VkMenu);
            _diagnosticLog(_chordDetected
                ? "[InputMonitor] action=windows-key-down result=success " +
                  "preexisting-modifier=true"
                : "[InputMonitor] action=windows-key-down result=success " +
                  "preexisting-modifier=false");
        }
    }

    private void HandleWindowsKeyUp(uint virtualKey)
    {
        if (virtualKey == VkLeftWindows)
        {
            _leftWindowsDown = false;
        }
        else
        {
            _rightWindowsDown = false;
        }

        if (_leftWindowsDown || _rightWindowsDown)
        {
            return;
        }

        bool releasedAlone = !_chordDetected;
        _chordDetected = false;

        if (releasedAlone)
        {
            _diagnosticLog(
                "[InputMonitor] action=classify-windows-key result=success " +
                "classification=standalone");
            _windowsKeyReleasedAlone();
        }
        else
        {
            _diagnosticLog(
                "[InputMonitor] action=classify-windows-key result=skipped " +
                "reason=shortcut launcher-trigger=skipped");
        }
    }

    private static bool IsKeyCurrentlyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KeyboardHookData
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelKeyboardProcedure(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProcedure hookProcedure,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
