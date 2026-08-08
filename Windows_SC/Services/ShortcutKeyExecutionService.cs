using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class ShortcutKeyExecutionService(DiagnosticLogger logger)
    : IShortcutKeyExecutionService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    public Task<ActionExecutionResult> ExecuteAsync(
        ShortcutKeyDefinition shortcutKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? validationError = ShortcutKeyValidator.Validate(shortcutKey);
        if (validationError is not null)
        {
            logger.Write("[ShortcutKey] action=send result=failed reason=invalid-definition");
            return Task.FromResult(ActionExecutionResult.Failure(validationError));
        }

        List<ushort> pressedKeys = [];
        try
        {
            foreach (ushort modifier in GetModifierKeys(shortcutKey.Modifiers))
            {
                SendKey(modifier, keyUp: false);
                pressedKeys.Add(modifier);
            }

            ushort bodyKey = checked((ushort)shortcutKey.VirtualKey);
            SendKey(bodyKey, keyUp: false);
            pressedKeys.Add(bodyKey);
            SendKey(bodyKey, keyUp: true);
            pressedKeys.RemoveAt(pressedKeys.Count - 1);

            for (int index = pressedKeys.Count - 1; index >= 0; index--)
            {
                SendKey(pressedKeys[index], keyUp: true);
            }

            pressedKeys.Clear();
            logger.Write("[ShortcutKey] action=send result=success");
            logger.WriteDetailed(
                $"[ShortcutKey] action=send result=success " +
                $"keys=\"{LogValue.Normalize(ShortcutKeyText.Format(shortcutKey))}\"");
            return Task.FromResult(ActionExecutionResult.Success);
        }
        catch (Exception exception) when (exception is Win32Exception
            or OverflowException
            or OperationCanceledException)
        {
            for (int index = pressedKeys.Count - 1; index >= 0; index--)
            {
                TryReleaseKey(pressedKeys[index]);
            }

            logger.Write(
                $"[ShortcutKey] action=send result=failed " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            logger.WriteDetailed(
                $"[ShortcutKey] action=send result=failed " +
                $"keys=\"{LogValue.Normalize(ShortcutKeyText.Format(shortcutKey))}\" " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            return Task.FromResult(ActionExecutionResult.Failure(
                $"ショートカットキーを送信できませんでした。\n{exception.Message}"));
        }
    }

    private static IReadOnlyList<ushort> GetModifierKeys(ShortcutKeyModifiers modifiers)
    {
        List<ushort> keys = [];
        if (modifiers.HasFlag(ShortcutKeyModifiers.Control))
        {
            keys.Add(0x11);
        }

        if (modifiers.HasFlag(ShortcutKeyModifiers.Alt))
        {
            keys.Add(0x12);
        }

        if (modifiers.HasFlag(ShortcutKeyModifiers.Shift))
        {
            keys.Add(0x10);
        }

        if (modifiers.HasFlag(ShortcutKeyModifiers.Windows))
        {
            keys.Add(0x5B);
        }

        return keys;
    }

    private static void SendKey(ushort virtualKey, bool keyUp)
    {
        Input input = new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static void TryReleaseKey(ushort virtualKey)
    {
        try
        {
            SendKey(virtualKey, keyUp: true);
        }
        catch (Win32Exception)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] Input[] inputs,
        int inputSize);
}
