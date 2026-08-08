using System.Collections.Generic;
using System.Linq;
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
    public void Validate_SingleNonModifierKey_IsAcceptedAndFormatted()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.None,
            VirtualKey = 0x7A,
            ScanCode = 0x57
        };

        Assert.IsNull(ShortcutKeyValidator.Validate(definition));
        Assert.AreEqual("F11", ShortcutKeyText.Format(definition));
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

    [TestMethod]
    public void BuildInputSequence_UsesScanCodesInSingleOrderedSequence()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.Control | ShortcutKeyModifiers.Shift,
            VirtualKey = 0x4D,
            ScanCode = 0x32
        };

        IReadOnlyList<ShortcutKeyInputStroke> sequence =
            ShortcutKeyInputSequence.Build(definition);

        CollectionAssert.AreEqual(
            new[]
            {
                new ShortcutKeyInputStroke(0x11, 0x1D, false, false),
                new ShortcutKeyInputStroke(0x10, 0x2A, false, false),
                new ShortcutKeyInputStroke(0x4D, 0x32, false, false),
                new ShortcutKeyInputStroke(0x4D, 0x32, true, false),
                new ShortcutKeyInputStroke(0x10, 0x2A, true, false),
                new ShortcutKeyInputStroke(0x11, 0x1D, true, false)
            },
            sequence.ToArray());
    }

    [TestMethod]
    public void BuildInputSequence_SingleKey_ContainsOnlyPressAndRelease()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.None,
            VirtualKey = 0x4D,
            ScanCode = 0x32
        };

        IReadOnlyList<ShortcutKeyInputStroke> sequence =
            ShortcutKeyInputSequence.Build(definition);

        CollectionAssert.AreEqual(
            new[]
            {
                new ShortcutKeyInputStroke(0x4D, 0x32, false, false),
                new ShortcutKeyInputStroke(0x4D, 0x32, true, false)
            },
            sequence.ToArray());
    }

    [TestMethod]
    public void BuildInputSequence_MarksExtendedKeys()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.Windows,
            VirtualKey = 0x27,
            ScanCode = 0x4D
        };

        IReadOnlyList<ShortcutKeyInputStroke> sequence =
            ShortcutKeyInputSequence.Build(definition);

        Assert.IsTrue(sequence[0].IsExtended);
        Assert.IsTrue(sequence[1].IsExtended);
        Assert.IsTrue(sequence[2].IsExtended);
        Assert.IsTrue(sequence[3].IsExtended);
    }

    [TestMethod]
    public void Validate_BothInputModes_AreAccepted()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.Control,
            VirtualKey = 0x4D,
            ScanCode = 0x32,
            InputMode = ShortcutKeyInputMode.ScanCode
        };

        Assert.IsNull(ShortcutKeyValidator.Validate(definition));
        definition.InputMode = ShortcutKeyInputMode.VirtualKey;
        Assert.IsNull(ShortcutKeyValidator.Validate(definition));
    }

    [TestMethod]
    public void Validate_UnknownInputMode_IsRejected()
    {
        ShortcutKeyDefinition definition = new()
        {
            Modifiers = ShortcutKeyModifiers.Control,
            VirtualKey = 0x4D,
            ScanCode = 0x32,
            InputMode = (ShortcutKeyInputMode)99
        };

        StringAssert.Contains(
            ShortcutKeyValidator.Validate(definition),
            "送信方式");
    }
}
