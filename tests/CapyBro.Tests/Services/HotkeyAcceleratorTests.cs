using CapyBro.Platform;
using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

public class HotkeyAcceleratorTests
{
    [Fact]
    public void Parse_CtrlShiftE_YieldsCorrectModifiersAndKey()
    {
        var result = HotkeyAccelerator.Parse("Ctrl+Shift+E");

        result.Should().NotBeNull();
        result!.Modifiers.Should().Be(NativeMethods.ModControl | NativeMethods.ModShift);
        result.VirtualKey.Should().Be('E');
    }

    [Theory]
    [InlineData("Ctrl+Q")]
    [InlineData("ctrl+q")]
    [InlineData("CTRL+Q")]
    [InlineData("Control+Q")]
    public void Parse_CaseInsensitive(string accelerator)
    {
        var result = HotkeyAccelerator.Parse(accelerator);

        result.Should().NotBeNull();
        result!.Modifiers.Should().Be(NativeMethods.ModControl);
        result.VirtualKey.Should().Be('Q');
    }

    [Theory]
    [InlineData("Alt+F1", 0x70u)]
    [InlineData("Alt+F12", 0x7Bu)]
    [InlineData("Alt+F24", 0x87u)]
    public void Parse_FunctionKeys(string accelerator, uint expectedVk)
    {
        var result = HotkeyAccelerator.Parse(accelerator);

        result.Should().NotBeNull();
        result!.VirtualKey.Should().Be(expectedVk);
    }

    [Theory]
    [InlineData("Win+R", "WINDOWS")]
    [InlineData("Windows+R", "WINDOWS")]
    [InlineData("Meta+R", "META")]
    public void Parse_WinModifier(string accelerator, string _)
    {
        var result = HotkeyAccelerator.Parse(accelerator);

        result.Should().NotBeNull();
        result!.Modifiers.Should().Be(NativeMethods.ModWin);
        result.VirtualKey.Should().Be('R');
    }

    [Theory]
    [InlineData("Ctrl+Space", 0x20u)]
    [InlineData("Ctrl+Tab", 0x09u)]
    [InlineData("Ctrl+Enter", 0x0Du)]
    [InlineData("Ctrl+Esc", 0x1Bu)]
    [InlineData("Ctrl+Delete", 0x2Eu)]
    [InlineData("Ctrl+Insert", 0x2Du)]
    [InlineData("Ctrl+Home", 0x24u)]
    [InlineData("Ctrl+End", 0x23u)]
    public void Parse_NamedKeys(string accelerator, uint expectedVk)
    {
        var result = HotkeyAccelerator.Parse(accelerator);

        result.Should().NotBeNull();
        result!.VirtualKey.Should().Be(expectedVk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+UnknownKey")]
    [InlineData("Ctrl+Shift+E+F")]
    public void Parse_InvalidInputs_ReturnsNull(string? accelerator)
    {
        HotkeyAccelerator.Parse(accelerator).Should().BeNull();
    }

    [Fact]
    public void Parse_AllModifiersStacked()
    {
        var result = HotkeyAccelerator.Parse("Ctrl+Alt+Shift+Win+F5");

        result.Should().NotBeNull();
        result!.Modifiers.Should().Be(
            NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModWin);
        result.VirtualKey.Should().Be(0x74u); // VK_F5
    }

    // Regression: pre-fix, single-character punctuation keys returned null
    // from ParseKey, so users who tried to register "Ctrl+/" (a very common
    // hotkey for opening Settings/Search across modern apps) silently saw
    // their preference rejected — the field accepted the text but
    // RegisterHotKey never fired.  These cover the OEM-VK punctuation set.
    [Theory]
    [InlineData("Ctrl+/", 0xBFu)]
    [InlineData("Ctrl+?", 0xBFu)]
    [InlineData("Ctrl+,", 0xBCu)]
    [InlineData("Ctrl+.", 0xBEu)]
    [InlineData("Ctrl+;", 0xBAu)]
    [InlineData("Ctrl+=", 0xBBu)]
    [InlineData("Ctrl+-", 0xBDu)]
    [InlineData("Ctrl+[", 0xDBu)]
    [InlineData("Ctrl+]", 0xDDu)]
    [InlineData("Ctrl+\\", 0xDCu)]
    [InlineData("Ctrl+'", 0xDEu)]
    [InlineData("Ctrl+`", 0xC0u)]
    public void Parse_OemPunctuationKeys_RecognizedAsValidHotkeys(string accelerator, uint expectedVk)
    {
        var result = HotkeyAccelerator.Parse(accelerator);

        result.Should().NotBeNull(
            "{0} is a common cross-app hotkey (e.g. Settings/Search) — must be parseable",
            accelerator);
        result!.Modifiers.Should().Be(NativeMethods.ModControl);
        result.VirtualKey.Should().Be(expectedVk);
    }

    [Fact]
    public void Parse_ShiftedPunctuationProducesSameVk_AsUnshifted()
    {
        // The Win32 RegisterHotKey API accepts a (modifier mask, VK) pair —
        // not a character. "?" and "/" share VK_OEM_2; "Shift" goes into
        // the modifier mask separately.  This guarantees that whatever the
        // user types in the accelerator field, the resulting registration
        // is layout-independent.
        var slash = HotkeyAccelerator.Parse("Ctrl+/");
        var question = HotkeyAccelerator.Parse("Ctrl+Shift+?");

        slash.Should().NotBeNull();
        question.Should().NotBeNull();
        slash!.VirtualKey.Should().Be(question!.VirtualKey);
    }
}
