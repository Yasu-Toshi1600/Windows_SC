using System;
using System.Collections.Generic;
using Windows_SC.Models;

namespace Windows_SC.Services;


internal static class ShortcutKeyText
{
    public static string Format(ShortcutKeyDefinition definition)
    {
        List<string> parts = [];
        if (definition.Modifiers.HasFlag(ShortcutKeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (definition.Modifiers.HasFlag(ShortcutKeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (definition.Modifiers.HasFlag(ShortcutKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (definition.Modifiers.HasFlag(ShortcutKeyModifiers.Windows))
        {
            parts.Add("Windows");
        }

        parts.Add(FormatVirtualKey(definition.VirtualKey));
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0xBA => ";",
            0xBB => "+",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"VK_{virtualKey:X2}"
        };
    }
}
