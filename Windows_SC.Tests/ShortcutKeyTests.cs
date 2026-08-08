using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows_SC.Models;
using Windows_SC.Services;

namespace Windows_SC.Tests;

[TestClass]
public sealed class ShortcutKeyTests
{
    [TestMethod]
    public void Validate_NormalChord_IsAcceptedAndFormatted()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.Control | ShortcutKeyModifiers.Shift,
            VirtualKey = 0x7B,
            ScanCode = 0x58
        };

        Assert.IsNull(ShortcutKeyValidator.Validate(definition));
        Assert.AreEqual("Ctrl+Shift+F12", ShortcutKeyText.Format(definition));
    }

    [TestMethod]
    public void Validate_ModifierOnly_IsRejected()
    {
        ShortcutKeyDefinition definition = new()
        {
            VirtualKey = 0x11
        };

        Assert.IsNotNull(ShortcutKeyValidator.Validate(definition));
    }

    [TestMethod]
    public void Validate_SecureAttentionSequence_IsRejected()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.Control | ShortcutKeyModifiers.Alt,
            VirtualKey = 0x2E
        };

        StringAssert.Contains(
            ShortcutKeyValidator.Validate(definition),
            "Ctrl+Alt+Delete");
    }

    [TestMethod]
    public void Validate_WindowsScFixedHotKey_IsRejected()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.Control | ShortcutKeyModifiers.Alt,
            VirtualKey = 0x20
        };

        StringAssert.Contains(
            ShortcutKeyValidator.Validate(definition),
            "固定ホットキー");
    }
}
