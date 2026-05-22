using System.Text.Json;

using CapyBro.Models;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Models;

public class AppConfigTests
{
    [Fact]
    public void Default_HasSensibleValues()
    {
        var d = AppConfig.Default;

        d.Model.Should().NotBeNullOrEmpty();
        d.Models.Should().NotBeEmpty().And.Contain(d.Model);
        d.Timeout.Should().Be(60);
        d.Language.Should().Be(
            Language.English,
            "post-rebrand the default UI language is English; locale auto-detect was removed from first-run init");
        d.Hotkey.Should().Be("Ctrl+Shift+E");
        d.MenuHotkey.Should().Be("Ctrl+Shift+Q");
        d.ConfigVersion.Should().Be(AppConfig.CurrentConfigVersion);
        d.CustomPrompts.Should().BeEmpty();
        d.DeletedDefaults.Should().BeEmpty();
        d.DefaultPrompt.Should().BeEmpty();
    }

    [Fact]
    public void Roundtrip_ViaJsonSerializer_PreservesAllFields()
    {
        var original = AppConfig.Default with
        {
            Model = "anthropic/claude-3.5-sonnet",
            Hotkey = "Ctrl+Alt+T",
            MenuHotkey = "Ctrl+Alt+M",
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["myPrompt"] = new() { Text = "Make it shine", PreserveLanguage = true },
            },
            DefaultPrompt = "Виправити помилки",
            DeletedDefaults = ["legacy-key"],
            Language = Language.English,
            Timeout = 99,
        };

        var json = JsonSerializer.Serialize(original, AppConfigJsonContext.Default.AppConfig);
        var deserialized = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);

        deserialized.Should().NotBeNull();
        deserialized!.Model.Should().Be(original.Model);
        deserialized.Hotkey.Should().Be(original.Hotkey);
        deserialized.MenuHotkey.Should().Be(original.MenuHotkey);
        deserialized.Timeout.Should().Be(99);
        deserialized.Language.Should().Be(Language.English);
        deserialized.CustomPrompts.Should().HaveCount(1);
        deserialized.CustomPrompts["myPrompt"].Should()
            .Be(new Prompt { Text = "Make it shine", PreserveLanguage = true });
        deserialized.DefaultPrompt.Should().Be("Виправити помилки");
        deserialized.DeletedDefaults.Should().ContainSingle().Which.Should().Be("legacy-key");
        deserialized.ConfigVersion.Should().Be(AppConfig.CurrentConfigVersion);
    }

    [Fact]
    public void Json_UsesCamelCase_AndStringEnumForLanguage()
    {
        var json = JsonSerializer.Serialize(AppConfig.Default, AppConfigJsonContext.Default.AppConfig);

        json.Should().Contain("\"configVersion\"");
        json.Should().Contain("\"defaultPrompt\"");
        json.Should().Contain("\"menuHotkey\"");
        json.Should().Contain("\"language\": \"English\"");
    }

    [Fact]
    public void Deserialize_JsonWithoutLanguageKey_DefaultsToEnglish()
    {
        // H3 (Z2-F3) / L5 regression: pre-fix the enum order was
        // UA-RU-EN, so `default(Language)` was Ukrainian — every config
        // missing a `language` key (hand-edited, partial migration,
        // older test fixture) silently fell back to Ukrainian even
        // though AppConfig.Default.Language is English.  The enum was
        // flipped to put English first; this test pins the new
        // alignment so the buggy fallback cannot drift back in.
        const string jsonWithoutLanguage = /*lang=json,strict*/ """
            {
              "model": "openai/gpt-4o",
              "models": ["openai/gpt-4o"],
              "timeout": 30,
              "configVersion": 13
            }
            """;

        var config = JsonSerializer.Deserialize(jsonWithoutLanguage, AppConfigJsonContext.Default.AppConfig);

        config.Should().NotBeNull();
        config!.Language.Should().Be(
            Language.English,
            "default(Language) must align with AppConfig.Default.Language post-rebrand");
    }

    [Fact]
    public void Deserialize_ExplicitLanguageUkrainian_PreservesValue()
    {
        // Companion to the missing-key test: a config that does carry
        // "language": "Ukrainian" must NOT be coerced to English just
        // because the enum order changed.  Strings on the wire are
        // unaffected by the flip — this guards against an accidental
        // converter regression.
        const string jsonWithLanguage = /*lang=json,strict*/ """
            {
              "model": "openai/gpt-4o",
              "models": ["openai/gpt-4o"],
              "timeout": 30,
              "language": "Ukrainian",
              "configVersion": 13
            }
            """;

        var config = JsonSerializer.Deserialize(jsonWithLanguage, AppConfigJsonContext.Default.AppConfig);

        config.Should().NotBeNull();
        config!.Language.Should().Be(Language.Ukrainian);
    }

    [Fact]
    public void Default_HasV15OllamaFields()
    {
        var d = AppConfig.Default;

        d.Provider.Should().Be(
            LlmProviderKind.OpenRouter,
            "OpenRouter is the default for new and upgrading installs — Ollama is opt-in");
        d.OllamaEndpoint.Should().Be(
            "http://localhost:11434",
            "must match `ollama serve` default bind address out of the box");
        d.OllamaModel.Should().BeEmpty(
            "fresh installs have no tag picked; user runs Refresh after `ollama pull`");
        d.OllamaModels.Should().BeEmpty();
    }

    [Fact]
    public void Upgrade_FromV14_DefaultsProviderToOpenRouter_AndOllamaEndpointToLocalhost()
    {
        // Pre-v15 JSON lacks every Ollama field.  STJ source-gen
        // deserializes them all to default(T): Provider=OpenRouter (the
        // zero-value of the enum, which intentionally matches the
        // documented upgrade default), OllamaEndpoint=null (empty
        // string after WithDefaultsApplied fallback), OllamaModels=null
        // (replaced with empty list).  The migration must surface the
        // canonical Default values so existing users see a Provider
        // toggle on the right setting without any visible disruption.
        const string v14Json = /*lang=json,strict*/ """
            {
              "model": "openai/gpt-4o",
              "models": ["openai/gpt-4o"],
              "timeout": 60,
              "language": "English",
              "configVersion": 14
            }
            """;

        var loaded = JsonSerializer.Deserialize(v14Json, AppConfigJsonContext.Default.AppConfig)!;
        var migrated = loaded.WithDefaultsApplied();

        migrated.Provider.Should().Be(LlmProviderKind.OpenRouter);
        migrated.OllamaEndpoint.Should().Be("http://localhost:11434");
        migrated.OllamaModel.Should().BeEmpty();
        migrated.OllamaModels.Should().BeEmpty();
        migrated.ConfigVersion.Should().Be(AppConfig.CurrentConfigVersion);

        // Existing fields must be preserved verbatim — the upgrade does
        // not touch the user's existing OpenRouter model pick.
        migrated.Model.Should().Be("openai/gpt-4o");
        migrated.Timeout.Should().Be(60);
    }

    [Fact]
    public void V15_PreservesExplicitOllamaSelection()
    {
        // A v15 user who has picked Ollama as their provider and saved
        // a local tag must roundtrip those values unchanged — no silent
        // reversion to OpenRouter on load.
        var original = AppConfig.Default with
        {
            Provider = LlmProviderKind.Ollama,
            OllamaEndpoint = "http://192.168.1.42:11434",
            OllamaModel = "llama3.2:latest",
            OllamaModels = ["llama3.2:latest", "mistral:7b-instruct"],
        };

        var json = JsonSerializer.Serialize(original, AppConfigJsonContext.Default.AppConfig);
        var deserialized = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig)!;
        var migrated = deserialized.WithDefaultsApplied();

        migrated.Provider.Should().Be(LlmProviderKind.Ollama);
        migrated.OllamaEndpoint.Should().Be("http://192.168.1.42:11434");
        migrated.OllamaModel.Should().Be("llama3.2:latest");
        migrated.OllamaModels.Should().BeEquivalentTo(["llama3.2:latest", "mistral:7b-instruct"]);
    }

    [Fact]
    public void V15_BlankOllamaEndpoint_FallsBackToLocalhost()
    {
        // Defence against the user clearing the Endpoint field and
        // saving.  Empty string is meaningless to OllamaClient (it
        // can't build a URI); WithDefaultsApplied normalises to the
        // documented default so requests still resolve.
        var loaded = AppConfig.Default with
        {
            Provider = LlmProviderKind.Ollama,
            OllamaEndpoint = "   ",
        };

        var migrated = loaded.WithDefaultsApplied();

        migrated.OllamaEndpoint.Should().Be("http://localhost:11434");
    }

    [Fact]
    public void V15_Json_UsesStringEnumForProvider()
    {
        // Mirror the Language-enum string-name policy so a hand-edited
        // config or a future C# rename doesn't silently break loads.
        var withOllama = AppConfig.Default with { Provider = LlmProviderKind.Ollama };
        var json = JsonSerializer.Serialize(withOllama, AppConfigJsonContext.Default.AppConfig);

        json.Should().Contain("\"provider\": \"Ollama\"");
    }
}
