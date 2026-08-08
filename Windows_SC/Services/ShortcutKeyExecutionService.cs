using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows_SC.Models;

namespace Windows_SC.Services;

internal sealed class ShortcutKeyExecutionService(
    DiagnosticLogger logger,
    ShortcutKeyExecutionCoordinator executionCoordinator) : IShortcutKeyExecutionService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventScanCode = 0x0008;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ShortcutKeyDefinition shortcutKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? validationError = ShortcutKeyValidator.Validate(shortcutKey);
        if (validationError is not null)
        {
            logger.Write("[ShortcutKey] action=send result=failed reason=invalid-definition");
            return ActionExecutionResult.Failure(validationError);
        }

        try
        {
            await executionCoordinator.PrepareTargetAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<ShortcutKeyInputStroke> strokes =
                ShortcutKeyInputSequence.Build(shortcutKey);
            SendStrokes(strokes, shortcutKey.InputMode);
            string inputMode = FormatInputMode(shortcutKey.InputMode);
            logger.Write(
                $"[ShortcutKey] action=send result=success mode={inputMode}");
            logger.WriteDetailed(
                $"[ShortcutKey] action=send result=success " +
                $"mode={inputMode} batched=true inputs={strokes.Count} " +
                $"keys=\"{LogValue.Normalize(ShortcutKeyText.Format(shortcutKey))}\"");
            return ActionExecutionResult.Success;
        }
        catch (Exception exception) when (exception is Win32Exception
            or OverflowException
            or TimeoutException
            or OperationCanceledException)
        {
            TryReleaseKeys(shortcutKey);

            logger.Write(
                $"[ShortcutKey] action=send result=failed " +
                $"mode={FormatInputMode(shortcutKey.InputMode)} " +
                $"exception={exception.GetType().Name} hresult=0x{exception.HResult:X8}");
            logger.WriteDetailed(
                $"[ShortcutKey] action=send result=failed " +
                $"mode={FormatInputMode(shortcutKey.InputMode)} batched=true " +
                $"keys=\"{LogValue.Normalize(ShortcutKeyText.Format(shortcutKey))}\" " +
                $"message=\"{LogValue.Normalize(exception.Message)}\"");
            return ActionExecutionResult.Failure(
                $"ショートカットキーを送信できませんでした。\n{exception.Message}");
        }
    }

    private static void SendStrokes(
        IReadOnlyList<ShortcutKeyInputStroke> strokes,
        ShortcutKeyInputMode inputMode)
    {
        Input[] inputs = strokes.Select(stroke => CreateInput(stroke, inputMode)).ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static Input CreateInput(
        ShortcutKeyInputStroke stroke,
        ShortcutKeyInputMode inputMode) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = inputMode == ShortcutKeyInputMode.VirtualKey
                    ? stroke.VirtualKey
                    : (ushort)0,
                ScanCode = inputMode == ShortcutKeyInputMode.ScanCode
                    ? stroke.ScanCode
                    : (ushort)0,
                Flags = (inputMode == ShortcutKeyInputMode.ScanCode
                        ? KeyEventScanCode
                            | (stroke.IsExtended ? KeyEventExtendedKey : 0)
                        : 0)
                    | (stroke.IsKeyUp ? KeyEventKeyUp : 0)
            }
        }
    };

    private static void TryReleaseKeys(ShortcutKeyDefinition shortcutKey)
    {
        try
        {
            IReadOnlyList<ShortcutKeyInputStroke> releases =
                ShortcutKeyInputSequence.Build(shortcutKey)
                    .Where(stroke => !stroke.IsKeyUp)
                    .Reverse()
                    .Select(stroke => stroke with { IsKeyUp = true })
                    .ToArray();
            SendStrokes(releases, shortcutKey.InputMode);
        }
        catch (Exception exception) when (exception is Win32Exception or OverflowException)
        {
        }
    }

    private static string FormatInputMode(ShortcutKeyInputMode inputMode) =>
        inputMode == ShortcutKeyInputMode.VirtualKey ? "virtual-key" : "scancode";

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
