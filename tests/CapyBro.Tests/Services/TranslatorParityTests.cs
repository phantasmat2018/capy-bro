using System.IO;
using System.Text.RegularExpressions;

using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Services;

/// <summary>
/// Enforces the locale-parity invariant the prior 432-test suite did not
/// pin: every key in the Ukrainian dictionary must exist in Russian and
/// English (and vice versa), and every <c>Translator.Instance[key]</c>
/// binding in production XAML/CS must resolve to a key that exists in all
/// three dictionaries.  Z7-F1 / Z7-F2 / H13 / C2 regression test —
/// pre-fix the <c>toast_cancel</c> binding shipped without a key and the
/// orphan surfaced as the literal developer token in every UI locale.
/// </summary>
[Collection(TranslatorCollection.Name)]
public partial class TranslatorParityTests
{
    [GeneratedRegex(@"Path\s*=\s*\[(?<key>[A-Za-z][A-Za-z0-9_]*)\]", RegexOptions.Compiled)]
    private static partial Regex BindingPattern();

    [Fact]
    public void AllThreeDictionaries_HaveIdenticalKeySets()
    {
        var ua = Translator.Strings[Language.Ukrainian].Keys.ToHashSet();
        var ru = Translator.Strings[Language.Russian].Keys.ToHashSet();
        var en = Translator.Strings[Language.English].Keys.ToHashSet();

        // Symmetric set differences should be empty in every direction.
        // A non-empty result is the exact failure shape that lets
        // "decorative" locale strings drift between releases.
        ua.Except(ru).Should().BeEmpty(
            "every Ukrainian key must also exist in the Russian dictionary");
        ru.Except(ua).Should().BeEmpty(
            "every Russian key must also exist in the Ukrainian dictionary");
        ua.Except(en).Should().BeEmpty(
            "every Ukrainian key must also exist in the English dictionary");
        en.Except(ua).Should().BeEmpty(
            "every English key must also exist in the Ukrainian dictionary");
        ru.Except(en).Should().BeEmpty(
            "every Russian key must also exist in the English dictionary");
        en.Except(ru).Should().BeEmpty(
            "every English key must also exist in the Russian dictionary");
    }

    [Theory]
    // Z7-F1 / C2 — orphan binding fixed.
    [InlineData("toast_cancel")]
    // Z9-F2 — caption tooltip localization.
    [InlineData("caption_close")]
    [InlineData("caption_minimize")]
    [InlineData("caption_maximize")]
    [InlineData("caption_restore")]
    // Z2-F2 / C5 — silent save failure surfaced.
    [InlineData("msg_save_settings_failed")]
    // Z2-F1 / C4 — credential persist failure surfaced.
    [InlineData("msg_api_key_persist_failed")]
    // Z5-F3 / H9 — history persist failure surfaced.
    [InlineData("msg_history_save_failed")]
    // Z5-F4 / H10 — history copy failure surfaced.
    [InlineData("msg_history_copy_failed")]
    // Z6-F3 — empty catalogue distinct from loading.
    [InlineData("msg_models_catalogue_empty")]
    // Z10-F3 / H17 — unobserved task surfaced.
    [InlineData("msg_background_task_failed")]
    // Z10-F4 / H18 — cancellation-with-result toast.
    [InlineData("msg_cancelled_with_result")]
    // Z1-F4 / M2 — empty model actionable toast.
    [InlineData("msg_model_not_configured")]
    // Z2-F5 / M4 — Reset failure surfaced.
    [InlineData("msg_reset_failed")]
    // FZ3-F3 / H22 — network unreachable specific.
    [InlineData("api_network_unreachable")]
    // FZ3-F5 / L30 — TLS handshake sub-case of transport failure.
    [InlineData("api_tls_failure")]
    // Z2-F8 / L4 — Additional-features Timeout input label + hint.
    // (Renamed from beta_timeout_* in v14 when the control moved from the
    // developer-mode-gated Beta section into Additional features so all
    // users — not just developer-mode unlockers — could tune the
    // OpenRouter timeout without hand-editing the JSON.)
    [InlineData("timeout_label")]
    [InlineData("timeout_hint")]
    // Z4-F1 / C7 — hotkey registration failure surfaced.
    [InlineData("hotkey_register_failed")]
    // FZ2-F3 — inline collision tooltip.
    [InlineData("tooltip_hotkey_conflict")]
    // Z2-F7 / M6 — inline tooltip when user types an unparseable hotkey.
    [InlineData("tooltip_hotkey_unparseable")]
    // Z9-F1 / M24 — ModelsDialog no-matches empty state.
    [InlineData("models_no_matches")]
    [InlineData("models_no_matches_body")]
    // FZ4-F2 / H23 — language-picker autonyms.
    [InlineData("lang_label_english")]
    [InlineData("lang_label_ukrainian")]
    [InlineData("lang_label_russian")]
    // Z3-F3 / M7 — editor empty-state when no prompt is selected.
    [InlineData("empty_editor_title")]
    [InlineData("empty_editor_body")]
    // v15 — Ollama-as-alternative-provider integration.
    [InlineData("general_section_provider")]
    [InlineData("help_provider")]
    [InlineData("provider_use_ollama")]
    [InlineData("provider_hint")]
    [InlineData("general_section_ollama")]
    [InlineData("help_ollama")]
    [InlineData("ollama_endpoint")]
    [InlineData("ollama_hint_prefix")]
    [InlineData("ollama_hint_suffix")]
    [InlineData("tooltip_refresh_ollama_models")]
    [InlineData("msg_ollama_models_refreshed")]
    [InlineData("msg_ollama_model_not_configured")]
    [InlineData("ollama_unreachable")]
    [InlineData("ollama_switched_to_openrouter")]
    [InlineData("ollama_model_not_pulled")]
    [InlineData("onboarding_apikey_ollama_hint")]
    public void NewQaCampaignKey_ExistsInAllLocales(string key)
    {
        Translator.Strings[Language.Ukrainian].Should().ContainKey(
            key, "Ukrainian dictionary must include {0}", key);
        Translator.Strings[Language.Russian].Should().ContainKey(
            key, "Russian dictionary must include {0}", key);
        Translator.Strings[Language.English].Should().ContainKey(
            key, "English dictionary must include {0}", key);

        // None of the entries should be empty whitespace — empty strings
        // would deliver "successful translation" to a literal blank UI
        // surface, which is the same user-impact as the missing-key bug.
        Translator.Strings[Language.Ukrainian][key].Should().NotBeNullOrWhiteSpace();
        Translator.Strings[Language.Russian][key].Should().NotBeNullOrWhiteSpace();
        Translator.Strings[Language.English][key].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void EveryXamlIndexerBinding_ResolvesToAPopulatedDictionaryEntry()
    {
        // Scan the production XAML tree for `Path=[some_key]` bindings
        // against the Translator and verify each referenced key exists in
        // all three locale dictionaries.  Pre-fix `toast_cancel` shipped
        // without a key and `Translator.Resolve()` returned the literal
        // token; this test would have caught the binding at the moment it
        // was authored.
        var viewsRoot = LocateViewsDirectory();
        viewsRoot.Should().NotBeNull(
            "test must locate the production Views directory to enforce binding parity");

        var ua = Translator.Strings[Language.Ukrainian];
        var ru = Translator.Strings[Language.Russian];
        var en = Translator.Strings[Language.English];

        var orphans = new List<string>();
        foreach (var xaml in Directory.EnumerateFiles(viewsRoot!, "*.xaml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(xaml);
            foreach (Match m in BindingPattern().Matches(content))
            {
                var key = m.Groups["key"].Value;
                if (!ua.ContainsKey(key) || !ru.ContainsKey(key) || !en.ContainsKey(key))
                {
                    orphans.Add($"{Path.GetFileName(xaml)}: Path=[{key}]");
                }
            }
        }

        orphans.Should().BeEmpty(
            "every Translator.Instance Path=[...] binding must resolve to a key present in all three dictionaries; otherwise the literal token leaks to users");
    }

    /// <summary>
    /// Walks up from the test assembly location until it finds the source
    /// tree's Views directory.  Returns null if the layout has changed —
    /// the calling test will fail with a clear message so the maintainer
    /// updates the scan path.
    /// </summary>
    private static string? LocateViewsDirectory()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CapyBro", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
