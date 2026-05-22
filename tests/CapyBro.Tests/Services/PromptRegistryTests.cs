using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

public class PromptRegistryTests
{
    [Fact]
    public void ValidateDefaults_DoesNotThrow()
    {
        var act = PromptRegistry.ValidateDefaults;
        act.Should().NotThrow();
    }

    [Fact]
    public void GetDefaultKeys_ReturnsEightKeys()
    {
        var sut = new PromptRegistry();

        var keys = sut.GetDefaultKeys(Language.Ukrainian);

        keys.Should().HaveCount(8);
        keys.Should().Contain("Виправити помилки");
        keys.Should().Contain("Перекласти на англійську");
    }

    [Fact]
    public void GetDefaultKeys_PerLanguage_ReturnsLanguageSpecificKeys()
    {
        var sut = new PromptRegistry();

        sut.GetDefaultKeys(Language.Ukrainian).Should().Contain("Виправити помилки");
        sut.GetDefaultKeys(Language.Russian).Should().Contain("Исправить ошибки");
        sut.GetDefaultKeys(Language.English).Should().Contain("Fix errors");
    }

    [Fact]
    public void GetActive_NoCustomNoDeleted_ReturnsAllDefaults()
    {
        var sut = new PromptRegistry();

        var active = sut.GetActive(AppConfig.Default, Language.Ukrainian);

        active.Should().HaveCount(8);
        active["Виправити помилки"].Text.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetActive_DeletesDefaultsByAnyLanguageEquivalent_HidesInAllLocales()
    {
        // Preset slots are semantically the same prompt across locales —
        // only the display text/name varies.  A delete in any one
        // language must therefore hide the slot everywhere; the OR-of-3
        // check also accepts legacy state where only one key is stored.
        var sut = new PromptRegistry();
        var config = AppConfig.Default with
        {
            DeletedDefaults = ["Fix errors"],
        };

        sut.GetActive(config, Language.English).Should().NotContainKey(
            "Fix errors",
            "the EN copy is hidden directly");
        sut.GetActive(config, Language.Ukrainian).Should().NotContainKey(
            "Виправити помилки",
            "the UA copy of the same slot is hidden too — delete is global across locales");
        sut.GetActive(config, Language.Russian).Should().NotContainKey("Исправить ошибки");
    }

    [Fact]
    public void GetActive_CustomPrompts_AreMerged()
    {
        var sut = new PromptRegistry();
        var config = AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["My custom"] = new() { Text = "do thing", PreserveLanguage = true },
            },
        };

        var active = sut.GetActive(config, Language.Ukrainian);

        active.Should().HaveCount(9);
        active.Should().ContainKey("My custom");
    }

    [Fact]
    public void GetAllEquivalentsForDefaultKey_ReturnsThreeLanguageKeys()
    {
        var sut = new PromptRegistry();

        var equivalents = sut.GetAllEquivalentsForDefaultKey("Виправити помилки");

        equivalents.Should().HaveCount(3);
        equivalents.Should().Contain(["Виправити помилки", "Исправить ошибки", "Fix errors"]);
    }

    [Fact]
    public void GetAllEquivalentsForDefaultKey_UnknownKey_ReturnsEmpty()
    {
        var sut = new PromptRegistry();

        sut.GetAllEquivalentsForDefaultKey("My custom prompt").Should().BeEmpty();
    }

    [Fact]
    public void GetActive_PreservesPromptOrderAndProperties()
    {
        var sut = new PromptRegistry();

        var active = sut.GetActive(AppConfig.Default, Language.English);

        active["Translate to English"].PreserveLanguage.Should().BeFalse();
        active["Fix errors"].PreserveLanguage.Should().BeTrue();
    }
}
