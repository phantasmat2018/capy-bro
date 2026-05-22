using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

public class ResultStripperTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("\n\n", "")]
    public void Strip_EmptyOrWhitespace_ReturnsEmpty(string? input, string expected)
    {
        ResultStripper.Strip(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Hello world", "Hello world")]
    [InlineData("  Hello  ", "Hello")]
    public void Strip_PlainText_TrimsOnly(string input, string expected)
    {
        ResultStripper.Strip(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Translation: Hello", "Hello")]
    [InlineData("Text: Hello", "Hello")]
    [InlineData("Output: Hello", "Hello")]
    [InlineData("Result: Hello world", "Hello world")]
    [InlineData("Answer: 42", "42")]
    [InlineData("Reply: ok", "ok")]
    [InlineData("Response: foo", "foo")]
    [InlineData("Текст: привіт", "привіт")]
    [InlineData("Переклад: hello", "hello")]
    [InlineData("Результат: 42", "42")]
    [InlineData("Відповідь: yes", "yes")]
    [InlineData("Перевод: привет", "привет")]
    [InlineData("Ответ: да", "да")]
    public void Strip_LeadPrefix_Removed(string input, string expected)
    {
        ResultStripper.Strip(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("translation: hello", "hello")]
    [InlineData("TRANSLATION: hello", "hello")]
    public void Strip_LeadPrefix_CaseInsensitive(string input, string expected)
    {
        ResultStripper.Strip(input).Should().Be(expected);
    }

    [Fact]
    public void Strip_CodeFence_NoLanguageTag_ExtractsContent()
    {
        var input = "```\nHello world\n```";
        ResultStripper.Strip(input).Should().Be("Hello world");
    }

    [Fact]
    public void Strip_CodeFence_WithLanguageTag_ExtractsContent()
    {
        var input = "```python\ndef foo(): pass\n```";
        ResultStripper.Strip(input).Should().Be("def foo(): pass");
    }

    [Fact]
    public void Strip_CodeFence_MultiLine_ExtractsContent()
    {
        var input = "```\nLine one\nLine two\nLine three\n```";
        ResultStripper.Strip(input).Should().Be("Line one\nLine two\nLine three");
    }

    [Fact]
    public void Strip_PrefixThenCodeFence_BothRemoved()
    {
        var input = "Translation:\n```\nHello\n```";
        ResultStripper.Strip(input).Should().Be("Hello");
    }

    [Theory]
    [InlineData("\"\"\"Hello\"\"\"", "Hello")]
    [InlineData("'''Hello'''", "Hello")]
    [InlineData("\"\"\"Hello", "Hello")]
    [InlineData("Hello\"\"\"", "Hello")]
    public void Strip_TripleQuotes_Removed(string input, string expected)
    {
        ResultStripper.Strip(input).Should().Be(expected);
    }

    [Fact]
    public void Strip_PrefixAndCodeFenceAndQuotes_AllRemoved()
    {
        var input = "Output:\n```\n\"\"\"Hello\"\"\"\n```";
        ResultStripper.Strip(input).Should().Be("Hello");
    }

    [Fact]
    public void Strip_OnlyPrefix_ReturnsEmpty()
    {
        ResultStripper.Strip("Translation:").Should().Be("");
    }

    [Fact]
    public void Strip_EmptyCodeFence_ReturnsEmpty()
    {
        ResultStripper.Strip("```\n```").Should().Be("");
    }

    // Anti-injection wrapping echo: when a model dutifully includes the
    // <text_to_process> tags around its response (despite the system
    // postscript instructing it not to), strip them so the user's pasted
    // result is clean.  Pre-fix this would surface as "<text_to_process>
    // Hello </text_to_process>" landing back on their clipboard.
    [Theory]
    [InlineData(
        "<text_to_process>Hello world</text_to_process>",
        "Hello world")]
    [InlineData(
        "<text_to_process>\nПривіт, як справи?\n</text_to_process>",
        "Привіт, як справи?")]
    [InlineData(
        "<text_to_process>Translation: Bonjour</text_to_process>",
        "Bonjour")]
    // Mixed-case echoes (some models lowercase / uppercase tag names).
    [InlineData(
        "<TEXT_TO_PROCESS>Hi</TEXT_TO_PROCESS>",
        "Hi")]
    public void Strip_EchoedAntiInjectionWrapper_RemovesTags(string input, string expected)
    {
        ResultStripper.Strip(input).Should().Be(expected);
    }

    [Fact]
    public void Strip_OnlyOpeningTagEchoed_StillStripped()
    {
        // Some models echo just the open tag (or only the close) when
        // they trail off mid-response.  Each side strips independently.
        ResultStripper.Strip("<text_to_process>foo bar").Should().Be("foo bar");
        ResultStripper.Strip("baz</text_to_process>").Should().Be("baz");
    }
}
