using Windows_SC.Models;

namespace Windows_SC.Services;


internal static class ShortcutKeyValidator
{
    public static string? Validate(ShortcutKeyDefinition? definition)
    {
        if (definition is null || definition.VirtualKey == 0)
        {
            return "ショートカットキーを記録してください。";
        }

        if (ShortcutKeyText.IsModifierKey(definition.VirtualKey))
        {
            return "修飾キー以外の本体キーを指定してください。";
        }

        if (!System.Enum.IsDefined(definition.Modifiers)
            && (definition.Modifiers & ~(
                ShortcutKeyModifiers.Control
                | ShortcutKeyModifiers.Alt
                | ShortcutKeyModifiers.Shift
                | ShortcutKeyModifiers.Windows)) != 0)
        {
            return "未対応の修飾キーが含まれています。";
        }

        bool hasControl = definition.Modifiers.HasFlag(ShortcutKeyModifiers.Control);
        bool hasAlt = definition.Modifiers.HasFlag(ShortcutKeyModifiers.Alt);
        if (hasControl && hasAlt && definition.VirtualKey == 0x2E)
        {
            return "Ctrl+Alt+Deleteは通常のアプリから送信できません。";
        }

        if (hasControl && hasAlt && definition.VirtualKey == 0x20)
        {
            return "Ctrl+Alt+SpaceはWindows_SCの固定ホットキーのため登録できません。";
        }

        if (!System.Enum.IsDefined(definition.InputMode))
        {
            return "未対応のショートカットキー送信方式です。";
        }

        if (definition.ScanCode == 0 || definition.ScanCode > ushort.MaxValue)
        {
            return "ショートカットキーのスキャンコードがありません。キーを再記録してください。";
        }

        return null;
    }
}
