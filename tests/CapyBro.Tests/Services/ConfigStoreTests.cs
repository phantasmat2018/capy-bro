using System.IO;
using System.Text;

using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Tests.TestHelpers;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CapyBro.Tests.Services;

public class ConfigStoreTests
{
    private static ConfigStore CreateStore(TempDirectory dir, string? legacyName = null) =>
        new(
            dir.GetPath("config.json"),
            dir.GetPath(legacyName ?? "legacy.json"),
            NullLogger<ConfigStore>.Instance);

    [Fact]
    public async Task LoadAsync_NoFileExists_ReturnsDefaultAsync()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Should().BeSameAs(AppConfig.Default);
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ReturnsDefaultAsync()
    {
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        await File.WriteAllTextAsync(configPath, "{not valid json", Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Should().BeSameAs(AppConfig.Default);
    }

    [Fact]
    public async Task LoadAsync_OldConfigVersion_MigratesExperimentalDiffPreviewToCurrentDefaultAsync()
    {
        // A v2 config file (pre-experimental-features) has no
        // experimentalDiffPreview field. WithDefaultsApplied applies the
        // documented current default (false — experimental features ship
        // disabled). The migration also bumps the stored schema version.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var v2Json = /*lang=json,strict*/ """
            {
              "configVersion": 2,
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z"
            }
            """;
        await File.WriteAllTextAsync(configPath, v2Json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.ExperimentalDiffPreview.Should().BeFalse(
            "experimental features ship disabled; upgraders inherit the documented default");
        config.ConfigVersion.Should().Be(
            AppConfig.CurrentConfigVersion,
            "loaded config should be upgraded to the current schema version");
    }

    [Fact]
    public async Task LoadAsync_OldConfigVersion_MigratesExperimentalStreamingToCurrentDefaultAsync()
    {
        // v3 config has no experimentalStreaming field. Migration applies
        // the documented current default (false — experimental ships
        // disabled). Already-set fields like experimentalDiffPreview are
        // not touched.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var v3Json = /*lang=json,strict*/ """
            {
              "configVersion": 3,
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "experimentalDiffPreview": true
            }
            """;
        await File.WriteAllTextAsync(configPath, v3Json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.ExperimentalStreaming.Should().BeFalse(
            "experimental features ship disabled; upgraders inherit the documented default");
        config.ExperimentalDiffPreview.Should().BeTrue(
            "previously-set v3 fields survive the v4 upgrade unchanged — user explicitly opted in on v3");
        config.ConfigVersion.Should().Be(
            AppConfig.CurrentConfigVersion,
            "loaded config should be upgraded to the current schema version");
    }

    [Fact]
    public async Task LoadAsync_CurrentVersion_PreservesExperimentalStreamingExplicitFalseAsync()
    {
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var json = $$"""
            {
              "configVersion": {{AppConfig.CurrentConfigVersion}},
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "experimentalDiffPreview": true,
              "experimentalStreaming": false
            }
            """;
        await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.ExperimentalStreaming.Should().BeFalse(
            "user-set false on the current schema must survive a round-trip");
    }

    [Fact]
    public async Task LoadAsync_OldConfigVersion_MigratesExperimentalPerPromptModelToCurrentDefaultAsync()
    {
        // v4 config has no experimentalPerPromptModel field. Migration
        // applies the documented current default (false — experimental
        // ships disabled). Already-set v4 fields are not touched.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var v4Json = /*lang=json,strict*/ """
            {
              "configVersion": 4,
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "experimentalDiffPreview": true,
              "experimentalStreaming": true
            }
            """;
        await File.WriteAllTextAsync(configPath, v4Json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.ExperimentalPerPromptModel.Should().BeFalse(
            "experimental features ship disabled; upgraders inherit the documented default");
        config.ExperimentalDiffPreview.Should().BeTrue(
            "previously-set v4 fields survive the v5 upgrade unchanged");
        config.ExperimentalStreaming.Should().BeTrue(
            "previously-set v4 fields survive the v5 upgrade unchanged");
        config.ConfigVersion.Should().Be(
            AppConfig.CurrentConfigVersion,
            "loaded config should be upgraded to the current schema version");
    }

    [Fact]
    public async Task LoadAsync_OldConfigVersion_MigratesExperimentalCostsAndCreditsToCurrentDefaultAsync()
    {
        // v5 config has no experimentalCostsAndCredits field. v5→v6
        // migration applies the documented default (false — experimental
        // ships disabled).
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var v5Json = /*lang=json,strict*/ """
            {
              "configVersion": 5,
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "experimentalDiffPreview": false,
              "experimentalStreaming": false,
              "experimentalPerPromptModel": true
            }
            """;
        await File.WriteAllTextAsync(configPath, v5Json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.ExperimentalCostsAndCredits.Should().BeFalse(
            "experimental features ship disabled; upgraders inherit the documented default");
        config.ExperimentalPerPromptModel.Should().BeTrue(
            "previously-set v5 fields survive the v6 upgrade unchanged");
        config.ConfigVersion.Should().Be(
            AppConfig.CurrentConfigVersion,
            "loaded config should be upgraded to the current schema version");
    }

    [Fact]
    public async Task LoadAsync_OldConfigVersion_MigratesExperimentalPrivacyRedactionToCurrentDefaultAsync()
    {
        // v6 config has no experimentalPrivacyRedaction field. v6→v7
        // migration applies the documented default (false — experimental
        // ships disabled).
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var v6Json = /*lang=json,strict*/ """
            {
              "configVersion": 6,
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "experimentalDiffPreview": false,
              "experimentalStreaming": false,
              "experimentalPerPromptModel": true,
              "experimentalCostsAndCredits": true
            }
            """;
        await File.WriteAllTextAsync(configPath, v6Json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.ExperimentalPrivacyRedaction.Should().BeFalse(
            "experimental features ship disabled; upgraders inherit the documented default");
        config.ExperimentalPerPromptModel.Should().BeTrue(
            "previously-set v6 fields survive the v7 upgrade unchanged");
        config.ExperimentalCostsAndCredits.Should().BeTrue(
            "previously-set v6 fields survive the v7 upgrade unchanged");
        config.ConfigVersion.Should().Be(
            AppConfig.CurrentConfigVersion,
            "loaded config should be upgraded to the current schema version");
    }

    [Fact]
    public async Task LoadAsync_OldConfigVersion_MigratesOnboardingCompletedToFalseAsync()
    {
        // v7 config has no onboardingCompleted field. v7→v8 migration leaves
        // it at the documented default (false) so a v7 user sees the wizard
        // once on the v8 upgrade — that is the deliberate trade documented
        // on AppConfig.OnboardingCompleted.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var v7Json = /*lang=json,strict*/ """
            {
              "configVersion": 7,
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "experimentalDiffPreview": true,
              "experimentalStreaming": false,
              "experimentalPerPromptModel": false,
              "experimentalCostsAndCredits": false,
              "experimentalPrivacyRedaction": true
            }
            """;
        await File.WriteAllTextAsync(configPath, v7Json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.OnboardingCompleted.Should().BeFalse(
            "v7→v8 leaves OnboardingCompleted=false so the wizard surfaces once on upgrade");
        config.ExperimentalDiffPreview.Should().BeTrue(
            "previously-set v7 fields survive the v8 upgrade unchanged");
        config.ExperimentalPrivacyRedaction.Should().BeTrue(
            "previously-set v7 fields survive the v8 upgrade unchanged");
        config.ConfigVersion.Should().Be(
            AppConfig.CurrentConfigVersion,
            "loaded config should be upgraded to the current schema version");
    }

    [Fact]
    public async Task LoadAsync_CurrentVersion_PreservesOnboardingCompletedTrueAsync()
    {
        // Once the user has completed (or skipped) the wizard, the flag
        // must persist across loads so we do not re-show the wizard.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var json = $$"""
            {
              "configVersion": {{AppConfig.CurrentConfigVersion}},
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "onboardingCompleted": true
            }
            """;
        await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.OnboardingCompleted.Should().BeTrue(
            "user-completed wizard flag must round-trip on the current schema");
    }

    [Fact]
    public async Task LegacyV1Migration_SetsOnboardingCompletedTrueAsync()
    {
        // A legacy (v1) config means the user already had the app fully
        // configured — showing them the wizard would be pure noise.
        // LegacyAppConfig migration explicitly bypasses by stamping
        // OnboardingCompleted=true.
        using var dir = new TempDirectory();
        var legacyPath = dir.GetPath("legacy.json");
        var legacyJson = /*lang=json,strict*/ """
            {
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "language": "uk",
              "hotkey": "Ctrl+Shift+E"
            }
            """;
        await File.WriteAllTextAsync(legacyPath, legacyJson, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.OnboardingCompleted.Should().BeTrue(
            "legacy users already configured the app; the wizard must not re-prompt them");
    }

    [Fact]
    public async Task LoadAsync_CurrentVersion_PreservesExperimentalDiffPreviewExplicitFalseAsync()
    {
        // When the user explicitly turned the flag off and saved, a fresh
        // load must NOT re-default it to true. ConfigVersion is current →
        // migration path skipped → stored value wins.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        var json = $$"""
            {
              "configVersion": {{AppConfig.CurrentConfigVersion}},
              "model": "openai/gpt-4o-mini",
              "models": ["openai/gpt-4o-mini"],
              "timeout": 30,
              "language": 0,
              "customPrompts": {},
              "deletedDefaults": [],
              "defaultPrompt": "",
              "hotkey": "Ctrl+Shift+E",
              "menuHotkey": "Ctrl+Shift+Q",
              "undoHotkey": "Ctrl+Shift+Z",
              "experimentalDiffPreview": false
            }
            """;
        await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.ExperimentalDiffPreview.Should().BeFalse(
            "user-set false on the current schema must survive a round-trip");
    }

    [Fact]
    public async Task LoadAsync_OneFieldOnly_DoesNotTriggerLegacyMigrationAsync()
    {
        // Single-field JSON looks ambiguous — could just as easily be a typo'd v2 file. The
        // tightened heuristic requires ≥2 populated legacy fields before it commits to a
        // migration; otherwise we fall back to Default rather than risk overwriting the user's
        // file with a misinterpretation.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        await File.WriteAllTextAsync(configPath, /*lang=json,strict*/ """{"timeout": 60}""", Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Should().BeSameAs(AppConfig.Default);
    }

    [Fact]
    public async Task LoadAsync_GenuinelyV1Shaped_MigratesInPlaceAsync()
    {
        // A v1-format file with multiple snake_case fields is unambiguous — migrate it.
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        await File.WriteAllTextAsync(
            configPath,
            /*lang=json,strict*/ """{"timeout":60,"model":"x/y","hotkey":"ctrl+shift+e"}""",
            Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Should().NotBeSameAs(AppConfig.Default);
        config.Timeout.Should().Be(60);
        config.Model.Should().Be("x/y");
        config.Hotkey.Should().Be("Ctrl+Shift+E");
    }

    [Fact]
    public async Task LoadAsync_OptionalFieldsMissing_AppliesDefaultsAsync()
    {
        using var dir = new TempDirectory();
        var configPath = dir.GetPath("config.json");
        // Required fields present, optional ones missing
        await File.WriteAllTextAsync(
            configPath,
            /*lang=json,strict*/
            """{"model": "x/y", "models": ["x/y"]}""",
            Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Model.Should().Be("x/y");
        // Bumped from 30 → 60 in v14 (longer-tail models hit the prior
        // ceiling on legitimate first-to-last token windows).  Mirrors
        // AppConfig.Default.Timeout — this test asserts that a JSON
        // missing the `timeout` field falls back to the Default.
        config.Timeout.Should().Be(60);
        config.Hotkey.Should().Be("Ctrl+Shift+E");
        // H3 (Z2-F3) / L5 fix: missing `language` key used to deserialize
        // to default(Language) = Ukrainian under the legacy enum order,
        // contradicting AppConfig.Default.Language = English.  After the
        // flip default(Language) == English, so a hand-edited or partial
        // config gracefully lands on the canonical default.
        config.Language.Should().Be(Language.English);
    }

    [Fact]
    public async Task SaveThenLoad_RoundtripsConfigAsync()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        var original = AppConfig.Default with
        {
            Model = "test/model",
            Timeout = 99,
            DefaultPrompt = "do-the-thing",
        };
        await store.SaveAsync(original);

        File.Exists(dir.GetPath("config.json")).Should().BeTrue();

        var loaded = await store.LoadAsync();
        loaded.Should().NotBeSameAs(AppConfig.Default);
        loaded.Model.Should().Be("test/model");
        loaded.Timeout.Should().Be(99);
        loaded.DefaultPrompt.Should().Be("do-the-thing");
    }

    [Fact]
    public async Task SaveAsync_NullConfig_ThrowsAsync()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        var act = async () => await store.SaveAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFileAsync()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        await store.SaveAsync(AppConfig.Default);

        File.Exists(dir.GetPath("config.json")).Should().BeTrue();
        File.Exists(dir.GetPath("config.json.tmp")).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingFileAsync()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);

        await store.SaveAsync(AppConfig.Default with { Model = "first" });
        await store.SaveAsync(AppConfig.Default with { Model = "second" });

        var loaded = await store.LoadAsync();
        loaded.Model.Should().Be("second");
    }

    [Fact]
    public async Task LoadAsync_LegacyV1FileExists_MigratesAndPersistsV2Async()
    {
        using var dir = new TempDirectory();
        var legacyPath = dir.GetPath("legacy.json");
        var legacyJson = /*lang=json,strict*/ """
        {
          "model": "openai/gpt-4o",
          "models": ["openai/gpt-4o", "anthropic/claude-3.5-sonnet"],
          "timeout": 25,
          "language": "Ukrainian",
          "custom_prompts": {
            "myprompt": { "text": "Some text", "preserve_language": true }
          },
          "deleted_defaults": ["foo"],
          "default_prompt": "myprompt",
          "hotkey": "Ctrl+Shift+E",
          "menu_hotkey": "Ctrl+Shift+Q"
        }
        """;
        await File.WriteAllTextAsync(legacyPath, legacyJson, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Model.Should().Be("openai/gpt-4o");
        config.Models.Should().Contain("anthropic/claude-3.5-sonnet");
        config.Timeout.Should().Be(25);
        config.Language.Should().Be(Language.Ukrainian);
        config.CustomPrompts.Should().ContainKey("myprompt");
        config.CustomPrompts["myprompt"].Text.Should().Be("Some text");
        config.CustomPrompts["myprompt"].PreserveLanguage.Should().BeTrue();
        config.DeletedDefaults.Should().Contain("foo");
        config.DefaultPrompt.Should().Be("myprompt");
        config.ConfigVersion.Should().Be(AppConfig.CurrentConfigVersion);

        File.Exists(dir.GetPath("config.json")).Should().BeTrue("v2 file should be created after migration");
        File.Exists(legacyPath).Should().BeTrue("legacy file should NOT be deleted (rollback safety per §6.18)");
    }

    [Fact]
    public async Task LoadAsync_LegacyMissingFields_FillsWithDefaultsAsync()
    {
        using var dir = new TempDirectory();
        var legacyPath = dir.GetPath("legacy.json");
        await File.WriteAllTextAsync(legacyPath, "{}", Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Should().NotBeSameAs(AppConfig.Default);
        config.Model.Should().Be(AppConfig.Default.Model);
        config.Timeout.Should().Be(AppConfig.Default.Timeout);
        config.Hotkey.Should().Be(AppConfig.Default.Hotkey);
        config.ConfigVersion.Should().Be(AppConfig.CurrentConfigVersion);
    }

    // Z2-F6 / M5 regression: pre-fix the legacy schema deliberately
    // omitted `UndoHotkey` because the feature didn't exist in v1.  A
    // manually-edited or future-rolled-back legacy JSON carrying an
    // `undo_hotkey` value was silently dropped during migration —
    // serving as a tripwire for any future v1 schema growth where the
    // same shape would lose real user data.  Post-fix the legacy reader
    // accepts the field and the migration preserves it through
    // NormalizeHotkey + default-fallback.
    [Fact]
    public async Task LoadAsync_LegacyWithUndoHotkey_PreservesValueThroughMigrationAsync()
    {
        using var dir = new TempDirectory();
        var legacyPath = dir.GetPath("legacy.json");
        var legacyJson = /*lang=json,strict*/ """
        {
          "hotkey": "Ctrl+Shift+E",
          "menu_hotkey": "Ctrl+Shift+Q",
          "undo_hotkey": "Ctrl+Alt+U"
        }
        """;
        await File.WriteAllTextAsync(legacyPath, legacyJson, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.UndoHotkey.Should().Be(
            "Ctrl+Alt+U",
            "the legacy reader must preserve `undo_hotkey` through migration so a manually-edited or rolled-back v1 file does not silently lose state");
        config.Hotkey.Should().Be("Ctrl+Shift+E");
        config.MenuHotkey.Should().Be("Ctrl+Shift+Q");
    }

    // Z2-F6 / M5 companion: a legacy JSON with NO `undo_hotkey` key
    // must still fall through to the v2 default (the documented
    // pre-fix behaviour for the common case where v1 users never
    // configured the field).
    [Fact]
    public async Task LoadAsync_LegacyWithoutUndoHotkey_AdoptsV2DefaultAsync()
    {
        using var dir = new TempDirectory();
        var legacyPath = dir.GetPath("legacy.json");
        var legacyJson = /*lang=json,strict*/ """
        {
          "hotkey": "Ctrl+Shift+E",
          "menu_hotkey": "Ctrl+Shift+Q"
        }
        """;
        await File.WriteAllTextAsync(legacyPath, legacyJson, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.UndoHotkey.Should().Be(
            AppConfig.Default.UndoHotkey,
            "missing legacy `undo_hotkey` keeps the v2 default; the common case for any actual v1 file");
    }

    [Theory]
    [InlineData("Ukrainian", Language.Ukrainian)]
    [InlineData("uk", Language.Ukrainian)]
    [InlineData("Russian", Language.Russian)]
    [InlineData("ru", Language.Russian)]
    [InlineData("English", Language.English)]
    [InlineData("en", Language.English)]
    // Unknown / null values fall back to English post-rebrand (was
    // Ukrainian when the product was branded "AI Text Improver").
    [InlineData("klingon", Language.English)]
    [InlineData(null, Language.English)]
    public async Task LoadAsync_LegacyLanguage_MapsCorrectlyAsync(string? value, Language expected)
    {
        using var dir = new TempDirectory();
        var legacyPath = dir.GetPath("legacy.json");
        var legacyJson = value is null
            ? "{}"
            : $$"""{"language": "{{value}}"}""";
        await File.WriteAllTextAsync(legacyPath, legacyJson, Encoding.UTF8);
        var store = CreateStore(dir);

        var config = await store.LoadAsync();

        config.Language.Should().Be(expected);
    }

    [Fact]
    public async Task ConcurrentSaveAndLoad_NoTornReadsOrErrorsAsync()
    {
        using var dir = new TempDirectory();
        var store = CreateStore(dir);
        await store.SaveAsync(AppConfig.Default with { Model = "seed" });

        const int iterations = 100;
        var fallbackCount = 0;
        var errorCount = 0;

        var tasks = new List<Task>();
        for (var i = 0; i < iterations; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await store.SaveAsync(AppConfig.Default with { Model = $"m{idx}" });
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errorCount);
                }
            }));
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var loaded = await store.LoadAsync();
                    if (ReferenceEquals(loaded, AppConfig.Default))
                    {
                        Interlocked.Increment(ref fallbackCount);
                    }
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref errorCount);
                }
            }));
        }

        await Task.WhenAll(tasks);

        fallbackCount.Should().Be(0, "atomic writes should never produce a torn read that hits the corrupt-fallback path");
        errorCount.Should().Be(0, "save retries should absorb transient lock conflicts on Windows");
    }
}
