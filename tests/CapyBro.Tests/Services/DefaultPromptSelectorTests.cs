using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Moq;

using Xunit;

namespace CapyBro.Tests.Services;

[Collection(TranslatorCollection.Name)]
public class DefaultPromptSelectorTests
{
    [Fact]
    public async Task SelectAsync_DefaultKind_ReturnsConfiguredDefaultPromptAsync()
    {
        // Translator singleton seeds to English post-rebrand, so the
        // active preset map is keyed by EN slot names ("Fix errors", etc.)
        // and the harness must reference those.  The matching prompt text
        // contains "grammar" (English copy in PromptRegistry.Defaults).
        var harness = new Harness();
        harness.ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { DefaultPrompt = "Fix errors" });

        var prompt = await harness.Sut.SelectAsync(HotkeyKind.Default);

        prompt.Should().NotBeNull();
        prompt!.Text.Should().Contain("grammar");
    }

    [Fact]
    public async Task SelectAsync_DefaultKind_UnknownConfigKey_FallsBackToFirstActiveAsync()
    {
        var harness = new Harness();
        harness.ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { DefaultPrompt = "Made up key" });

        var prompt = await harness.Sut.SelectAsync(HotkeyKind.Default);

        prompt.Should().NotBeNull();
    }

    [Fact]
    public async Task SelectAsync_MenuKind_DelegatesToPickerAsync()
    {
        var harness = new Harness();
        var expected = new Prompt { Text = "picked", PreserveLanguage = false };
        harness.Picker
            .Setup(x => x.ShowAsync(
                It.IsAny<IReadOnlyDictionary<string, Prompt>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var prompt = await harness.Sut.SelectAsync(HotkeyKind.Menu);

        prompt.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task SelectAsync_MenuKind_UserCancels_ReturnsNullAsync()
    {
        var harness = new Harness();
        harness.Picker
            .Setup(x => x.ShowAsync(
                It.IsAny<IReadOnlyDictionary<string, Prompt>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Prompt?)null);

        var prompt = await harness.Sut.SelectAsync(HotkeyKind.Menu);

        prompt.Should().BeNull();
    }

    [Fact]
    public async Task SelectAsync_DefaultKind_UsesCurrentLanguageForLookupAsync()
    {
        var harness = new Harness();
        harness.Translator.SetLanguage(Language.English);
        harness.ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { DefaultPrompt = "Fix errors" });

        var prompt = await harness.Sut.SelectAsync(HotkeyKind.Default);

        prompt.Should().NotBeNull();
        prompt!.Text.Should().Contain("grammar", because: "English text content for the same prompt index");
    }

    [Fact]
    public async Task SelectAsync_DefaultKind_FallbackIsDeterministicAcrossCallsAsync()
    {
        // Regression: pre-fix, the fallback path used
        // active.Values.FirstOrDefault(), whose iteration order depends on
        // the dictionary's internal hash buckets — in practice "first" was
        // whichever Defaults entry happened to be enumerated first by
        // PromptRegistry.GetActive, but that order shifts whenever the
        // map mutates (custom prompts added/removed).  Now the fallback
        // is alphabetical-by-display-name which is predictable for users
        // and stable across runs / DI lifetimes.
        var harness = new Harness();
        harness.ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { DefaultPrompt = "completely-bogus-pointer" });

        var first = await harness.Sut.SelectAsync(HotkeyKind.Default);
        var second = await harness.Sut.SelectAsync(HotkeyKind.Default);
        var third = await harness.Sut.SelectAsync(HotkeyKind.Default);

        first.Should().NotBeNull();
        second.Should().BeEquivalentTo(
            first,
            "fallback must be deterministic; otherwise stale DefaultPrompt config silently fires a different prompt every run");
        third.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task SelectAsync_DefaultKind_WhitespaceConfigKey_FallsBackInsteadOfThrowingAsync()
    {
        // Pre-fix used IsNullOrEmpty — a whitespace-only DefaultPrompt
        // (e.g. " " saved by a trim-eager UI binding) bypassed the empty
        // guard, hit TryGetValue on a ws-only key (failed), and reached
        // FirstOrDefault.  Tighter contract: whitespace counts as "no
        // pointer set", same as null/empty.
        var harness = new Harness();
        harness.ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { DefaultPrompt = "   " });

        var prompt = await harness.Sut.SelectAsync(HotkeyKind.Default);

        prompt.Should().NotBeNull();
    }

    private sealed class Harness
    {
        public Harness()
        {
            ConfigStore = new Mock<IConfigStore>(MockBehavior.Loose);
            ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppConfig.Default);

            Picker = new Mock<IPromptPicker>(MockBehavior.Loose);

            Translator = new Translator();
            Registry = new PromptRegistry();

            Sut = new DefaultPromptSelector(ConfigStore.Object, Registry, Picker.Object, Translator);
        }

        public DefaultPromptSelector Sut { get; }

        public Mock<IConfigStore> ConfigStore { get; }

        public Mock<IPromptPicker> Picker { get; }

        public Translator Translator { get; }

        public PromptRegistry Registry { get; }
    }
}
