using System;
using System.Collections.Generic;
using Windows_SC.Models;

namespace Windows_SC.Services;


internal readonly record struct ShortcutKeyInputStroke(
    ushort VirtualKey,
    ushort ScanCode,
    bool IsKeyUp,
    bool IsExtended);

internal static class ShortcutKeyInputSequence
{
    public static IReadOnlyList<ShortcutKeyInputStroke> Build(
        ShortcutKeyDefinition shortcutKey)
    {
        List<ShortcutKeyInputStroke> modifierKeys = GetModifierKeys(
            shortcutKey.Modifiers);
        List<ShortcutKeyInputStroke> inputs = new(modifierKeys.Count * 2 + 2);
        inputs.AddRange(modifierKeys);

        ushort bodyVirtualKey = checked((ushort)shortcutKey.VirtualKey);
        ushort bodyScanCode = checked((ushort)shortcutKey.ScanCode);
        bool bodyIsExtended = shortcutKey.IsExtendedKey
            || IsExtendedVirtualKey(shortcutKey.VirtualKey);
        inputs.Add(new(bodyVirtualKey, bodyScanCode, IsKeyUp: false, bodyIsExtended));
        inputs.Add(new(bodyVirtualKey, bodyScanCode, IsKeyUp: true, bodyIsExtended));

        for (int index = modifierKeys.Count - 1; index >= 0; index--)
        {
            ShortcutKeyInputStroke modifier = modifierKeys[index];
            inputs.Add(modifier with { IsKeyUp = true });
        }

        return inputs;
    }

    private static List<ShortcutKeyInputStroke> GetModifierKeys(
        ShortcutKeyModifiers modifiers)
    {
        List<ShortcutKeyInputStroke> keys = [];
        if (modifiers.HasFlag(ShortcutKeyModifiers.Control))
        {
            keys.Add(new(0x11, 0x1D, IsKeyUp: false, IsExtended: false));
        }

        if (modifiers.HasFlag(ShortcutKeyModifiers.Alt))
        {
            keys.Add(new(0x12, 0x38, IsKeyUp: false, IsExtended: false));
        }

        if (modifiers.HasFlag(ShortcutKeyModifiers.Shift))
        {
            keys.Add(new(0x10, 0x2A, IsKeyUp: false, IsExtended: false));
        }

        if (modifiers.HasFlag(ShortcutKeyModifiers.Windows))
        {
            keys.Add(new(0x5B, 0x5B, IsKeyUp: false, IsExtended: true));
        }

        return keys;
    }

    private static bool IsExtendedVirtualKey(uint virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or
        0x25 or 0x26 or 0x27 or 0x28 or
        0x2C or 0x2D or 0x2E or
        0x5B or 0x5C or 0x5D or
        0x6F or 0x90 or
        0xA3 or 0xA5 or
        >= 0xA6 and <= 0xB7;
}
