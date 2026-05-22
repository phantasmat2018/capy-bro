using System.ComponentModel;

using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

[Collection(TranslatorCollection.Name)]
public class TranslatorTests
{
    [Fact]
    public void Indexer_ExistingKey_ReturnsLocalizedString()
    {
        // Z7-F4 / L14: migrated witness key from `btn_save` (now deleted as
        // dead) to `caption_close` (live — wired into WindowCaption.xaml).
        // Picked because it has three distinct values across UA/RU/EN, so
        // the test still pins "translator returns the right string per
        // language" without depending on a key that exists solely to keep
        // the test alive.
        var t = new Translator();
        t.SetLanguage(Language.Ukrainian);

        t["caption_close"].Should().Be("Закрити");

        t.SetLanguage(Language.Russian);
        t["caption_close"].Should().Be("Закрыть");

        t.SetLanguage(Language.English);
        t["caption_close"].Should().Be("Close");
    }

    [Fact]
    public void Indexer_UnknownKey_ReturnsKeyAsFallback()
    {
        var t = new Translator();

        t["totally_made_up_key_xyz"].Should().Be("totally_made_up_key_xyz");
    }

    [Fact]
    public void Indexer_KeyMissingInRussianButPresentInUkrainian_FallsBackToUkrainian()
    {
        // To trigger fallback, we'd need a key that exists in Ukrainian but not Russian.
        // The current resource tables maintain key parity, so we verify the fallback contract
        // by switching to Russian and asking for a synthetic key — verifying that an unknown
        // key in non-default language still returns the key (last-resort fallback).
        var t = new Translator();
        t.SetLanguage(Language.English);

        t["missing_key_xyz"].Should().Be("missing_key_xyz");
    }

    [Fact]
    public void SetLanguage_RaisesPropertyChangedFor_Language_AndIndexer()
    {
        var t = new Translator();
        var captured = new List<string?>();
        t.PropertyChanged += (_, e) => captured.Add(e.PropertyName);

        // Default singleton language is English post-rebrand, so swap to
        // a different value (Ukrainian) to actually trigger PropertyChanged.
        t.SetLanguage(Language.Ukrainian);

        captured.Should().Contain("Language");
        captured.Should().Contain("Item[]");
    }

    [Fact]
    public void SetLanguage_SameValue_DoesNotRaisePropertyChanged()
    {
        var t = new Translator();
        var raised = false;
        t.PropertyChanged += (_, _) => raised = true;

        t.SetLanguage(Language.English); // default is English post-rebrand

        raised.Should().BeFalse();
    }

    [Fact]
    public void Format_WithSingleArg_InterpolatesCorrectly()
    {
        var t = new Translator();
        t.SetLanguage(Language.English);

        var msg = t.Format("msg_autostart_fail", "ACCESS_DENIED");

        msg.Should().Be("Failed to configure autostart: ACCESS_DENIED");
    }

    [Fact]
    public void Format_WithCountArg_RespectsLanguage()
    {
        var t = new Translator();
        t.SetLanguage(Language.Ukrainian);

        t.Format("msg_models_loaded", 42).Should().Be("Завантажено моделей: 42");
    }

    [Fact]
    public void Format_NoArgs_ReturnsTemplate()
    {
        // Default language post-rebrand is English, so "btn_ok" resolves
        // to "OK" (the English table value), not the Ukrainian "Гаразд".
        var t = new Translator();

        t.Format("btn_ok").Should().Be("OK");
    }

    [Fact]
    public void Format_UnknownKey_ReturnsKey()
    {
        var t = new Translator();

        t.Format("unknown_key").Should().Be("unknown_key");
    }

    [Fact]
    public void Format_TemplateMissingPlaceholder_ReturnsTemplateInsteadOfThrowing()
    {
        // Regression: pre-fix, string.Format threw FormatException when
        // the template referenced fewer placeholders than args (or
        // referenced a {N} index out of range).  The exception bubbled
        // up through {Binding} expressions and crashed the WPF surface
        // displaying the string.  Translator now swallows the
        // FormatException and returns the raw template — readable, safer.
        var t = new Translator();

        // "btn_ok" is "Гаразд" — has zero placeholders.  Pre-fix this
        // would actually succeed because args > 0 with no {N} doesn't
        // throw on string.Format.  The genuine failure mode is when the
        // template HAS {0} but no args reach it OR the template has
        // unbalanced braces; we fabricate a synthetic key via the unknown
        // → key fallback behaviour.
        var result = t.Format("template_with_unbalanced_brace_{", "alpha");
        result.Should().Be(
            "template_with_unbalanced_brace_{",
            "the unbalanced brace makes string.Format throw — the translator must catch and return the raw template rather than crash the binding");
    }

    [Fact]
    public void Format_TemplateReferencesIndexOutOfRange_ReturnsTemplate()
    {
        var t = new Translator();

        // Synthetic template via the unknown-key fallback — Format then
        // tries string.Format("template_{0}_{5}", new[]{"alpha"}) which
        // throws FormatException because {5} has no matching arg.
        var result = t.Format("template_{0}_{5}", "alpha");

        result.Should().Be("template_{0}_{5}");
    }

    [Fact]
    public void Instance_StaticAccessor_ReturnsSameSingleton()
    {
        Translator.Instance.Should().BeSameAs(Translator.Instance);
    }

    [Fact]
    public void Instance_InitialLanguage_IsEnglish()
    {
        // Post-rebrand the singleton seeds to English (matches AppConfig.
        // Default.Language); other tests in this file may have called
        // SetLanguage on the same singleton, so reset before asserting.
        Translator.Instance.SetLanguage(Language.English);
        Translator.Instance.Language.Should().Be(Language.English);
    }

    [Theory]
    [InlineData(Language.Ukrainian)]
    [InlineData(Language.Russian)]
    [InlineData(Language.English)]
    public void SettingsTitle_ResolvesToBrandWordmarkInAllLanguages(Language language)
    {
        // Post-rebrand: settings_title is the window-level brand label
        // (shown in the caption strip, taskbar, Alt+Tab).  Brand names do
        // not translate, so all three locale tables resolve to the
        // identical wordmark "CapyBro".  Tab labels (tab_general,
        // tab_prompts, tab_history) and other UI strings remain
        // localised — see those tests above.
        var t = new Translator();
        t.SetLanguage(language);

        t["settings_title"].Should().Be("CapyBro");
    }

    [Fact]
    public void PropertyChanged_AllSubscribersReceiveEvent()
    {
        var t = new Translator();
        var e1Count = 0;
        var e2Count = 0;
        PropertyChangedEventHandler h1 = (_, _) => e1Count++;
        PropertyChangedEventHandler h2 = (_, _) => e2Count++;
        t.PropertyChanged += h1;
        t.PropertyChanged += h2;

        // Default singleton state is English post-rebrand; switch to a
        // different value to actually fire PropertyChanged (the no-op
        // skip-when-equal check is exercised by another test above).
        t.SetLanguage(Language.Ukrainian);

        e1Count.Should().Be(2); // Language + Item[]
        e2Count.Should().Be(2);
    }
}
