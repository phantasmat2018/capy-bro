using CapyBro.Models;

namespace CapyBro.Services;

public sealed class PromptRegistry : IPromptRegistry
{
    private static readonly DefaultPromptDefinition[] Defaults =
    [
        new()
        {
            KeyUk = "Виправити помилки",
            KeyRu = "Исправить ошибки",
            KeyEn = "Fix errors",
            TextUk = "Виправ граматичні, орфографічні та пунктуаційні помилки в тексті, зберігаючи зміст і стиль автора. Поверни ТІЛЬКИ виправлений текст без пояснень.",
            TextRu = "Исправь грамматические, орфографические и пунктуационные ошибки в тексте, сохраняя смысл и авторский стиль. Верни ТОЛЬКО исправленный текст без пояснений.",
            TextEn = "Fix grammar, spelling, and punctuation errors in the text while preserving meaning and the author's style. Return ONLY the corrected text without explanations.",
            PreserveLanguage = true,
            // The canonical "verify before commit" use case — user wants to
            // catch any place where the model "fixed" something it shouldn't
            // have. Other defaults (style improvements, translations, etc.)
            // intentionally rewrite, so a diff would be noise.
            ShowDiffPreview = true,
        },
        new()
        {
            KeyUk = "Покращити стиль",
            KeyRu = "Улучшить стиль",
            KeyEn = "Improve style",
            TextUk = "Покращ стиль написання тексту: зроби його чіткішим, плавнішим і виразнішим, зберігаючи зміст. Поверни ТІЛЬКИ покращений текст.",
            TextRu = "Улучши стиль написания текста: сделай его чётче, плавнее и выразительнее, сохраняя смысл. Верни ТОЛЬКО улучшенный текст.",
            TextEn = "Improve the writing style: make the text clearer, smoother, and more expressive while preserving meaning. Return ONLY the improved text.",
            PreserveLanguage = true,
        },
        new()
        {
            KeyUk = "Офіційний стиль",
            KeyRu = "Официальный стиль",
            KeyEn = "Formal style",
            TextUk = "Перепиши текст у формальному, діловому стилі, зберігаючи зміст. Поверни ТІЛЬКИ переписаний текст.",
            TextRu = "Перепиши текст в формальном, деловом стиле, сохраняя смысл. Верни ТОЛЬКО переписанный текст.",
            TextEn = "Rewrite the text in a formal, business style while preserving meaning. Return ONLY the rewritten text.",
            PreserveLanguage = true,
        },
        new()
        {
            KeyUk = "Скоротити текст",
            KeyRu = "Сократить текст",
            KeyEn = "Shorten text",
            TextUk = "Скороти текст, зберігаючи всі важливі деталі та зміст. Поверни ТІЛЬКИ скорочений текст.",
            TextRu = "Сократи текст, сохраняя все важные детали и смысл. Верни ТОЛЬКО сокращённый текст.",
            TextEn = "Shorten the text while preserving all important details and meaning. Return ONLY the shortened text.",
            PreserveLanguage = true,
        },
        new()
        {
            KeyUk = "Розширити текст",
            KeyRu = "Расширить текст",
            KeyEn = "Expand text",
            TextUk = "Розшир текст, додаючи природні деталі та пояснення без зміни основного змісту. Поверни ТІЛЬКИ розширений текст.",
            TextRu = "Расширь текст, добавив естественные детали и пояснения, не меняя основного смысла. Верни ТОЛЬКО расширенный текст.",
            TextEn = "Expand the text by adding natural details and explanations without changing the core meaning. Return ONLY the expanded text.",
            PreserveLanguage = true,
        },
        new()
        {
            KeyUk = "Перекласти на англійську",
            KeyRu = "Перевести на английский",
            KeyEn = "Translate to English",
            TextUk = "Переклади текст на англійську мову, зберігаючи стиль і нюанси. Поверни ТІЛЬКИ переклад без пояснень.",
            TextRu = "Переведи текст на английский язык, сохраняя стиль и нюансы. Верни ТОЛЬКО перевод без пояснений.",
            TextEn = "Translate the text to English, preserving style and nuance. Return ONLY the translation without explanations.",
            PreserveLanguage = false,
        },
        new()
        {
            KeyUk = "Перекласти на російську",
            KeyRu = "Перевести на русский",
            KeyEn = "Translate to Russian",
            TextUk = "Переклади текст на російську мову, зберігаючи стиль і нюанси. Поверни ТІЛЬКИ переклад без пояснень.",
            TextRu = "Переведи текст на русский язык, сохраняя стиль и нюансы. Верни ТОЛЬКО перевод без пояснений.",
            TextEn = "Translate the text to Russian, preserving style and nuance. Return ONLY the translation without explanations.",
            PreserveLanguage = false,
        },
        new()
        {
            KeyUk = "Перекласти на українську",
            KeyRu = "Перевести на украинский",
            KeyEn = "Translate to Ukrainian",
            TextUk = "Переклади текст на українську мову, зберігаючи стиль і нюанси. Поверни ТІЛЬКИ переклад без пояснень.",
            TextRu = "Переведи текст на украинский язык, сохраняя стиль и нюансы. Верни ТОЛЬКО перевод без пояснений.",
            TextEn = "Translate the text to Ukrainian, preserving style and nuance. Return ONLY the translation without explanations.",
            PreserveLanguage = false,
        },
    ];

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1065:Do not raise exceptions in unexpected locations",
        Justification = "§6.3: throw loudly on misconfiguration of default prompts at startup.")]
    static PromptRegistry()
    {
        ValidateDefaults();
    }

    public static void ValidateDefaults()
    {
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Defaults.Length; i++)
        {
            var def = Defaults[i];
            if (string.IsNullOrEmpty(def.KeyUk) || string.IsNullOrEmpty(def.KeyRu) || string.IsNullOrEmpty(def.KeyEn)
                || string.IsNullOrEmpty(def.TextUk) || string.IsNullOrEmpty(def.TextRu) || string.IsNullOrEmpty(def.TextEn))
            {
                throw new InvalidOperationException(
                    $"Default prompt at index {i} has empty key or text in at least one language.");
            }

            foreach (var key in new[] { def.KeyUk, def.KeyRu, def.KeyEn })
            {
                if (!seenKeys.Add(key))
                {
                    throw new InvalidOperationException(
                        $"Default prompt key '{key}' is duplicated across slots.");
                }
            }
        }
    }

    public IReadOnlyList<string> GetDefaultKeys(Language language) =>
        [.. Defaults.Select(d => d.GetKey(language))];

    public IReadOnlyDictionary<string, Prompt> GetActive(AppConfig config, Language language)
    {
        ArgumentNullException.ThrowIfNull(config);

        var deletedSet = new HashSet<string>(config.DeletedDefaults, StringComparer.Ordinal);
        var result = new Dictionary<string, Prompt>(StringComparer.Ordinal);
        var overrides = config.DefaultPromptOverrides;
        var slotSettings = config.DefaultPromptSettings;

        foreach (var def in Defaults)
        {
            // Delete is GLOBAL across locales: a deletion in any one
            // language hides the slot everywhere (the 8 preset slots are
            // semantically the same prompt in all locales — only the
            // display text/name varies).  DeleteAsync adds all 3
            // language keys to DeletedDefaults; this OR-of-3 check is
            // also forgiving of legacy state where only one might be set.
            if (deletedSet.Contains(def.KeyUk) || deletedSet.Contains(def.KeyRu) || deletedSet.Contains(def.KeyEn))
            {
                continue;
            }

            var langKey = def.GetKey(language);

            // Compose the active prompt by layering, in order:
            //   1. The built-in default (text + per-slot defaults).
            //   2. Slot-level user settings (PreserveLanguage /
            //      ShowDiffPreview / Model) — keyed by EN canonical, so
            //      they apply uniformly across UA/RU/EN of the same slot.
            //      "Preserve source language" is a property of the
            //      prompt's PURPOSE, not of the locale that happened to
            //      be active when the user toggled it.
            //   3. Per-language override (Text / OverrideName) — keyed by
            //      the language-specific name so a UA edit does not
            //      bleed into the EN / RU lists.
            var basePrompt = def.ToPrompt(language);

            if (slotSettings is not null && slotSettings.TryGetValue(def.KeyEn, out var settings))
            {
                basePrompt = basePrompt with
                {
                    PreserveLanguage = settings.PreserveLanguage,
                    ShowDiffPreview = settings.ShowDiffPreview,
                    Model = settings.Model,
                    OllamaModel = settings.OllamaModel,
                };
            }

            var displayName = langKey;
            if (overrides is not null && overrides.TryGetValue(langKey, out var overridePrompt))
            {
                basePrompt = basePrompt with { Text = overridePrompt.Text };
                if (!string.IsNullOrWhiteSpace(overridePrompt.OverrideName))
                {
                    displayName = overridePrompt.OverrideName!;
                }
            }

            result[displayName] = basePrompt;
        }

        foreach (var (k, v) in config.CustomPrompts)
        {
            result[k] = v;
        }

        return result;
    }

    public IReadOnlyList<string> GetAllEquivalentsForDefaultKey(string key)
    {
        var def = Defaults.FirstOrDefault(d => d.MatchesAnyLanguageKey(key));
        return def is null ? [] : [def.KeyUk, def.KeyRu, def.KeyEn];
    }

    public string? GetCanonicalEnglishKeyForDefault(string anyLanguageKey)
    {
        var def = Defaults.FirstOrDefault(d => d.MatchesAnyLanguageKey(anyLanguageKey));
        return def?.KeyEn;
    }

    public string? ResolveOriginalPresetKey(AppConfig config, Language language, string displayName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(displayName);

        // Direct match: the display name is an unedited default OR an
        // override's-key default (i.e. user edited but did not rename).
        var def = Defaults.FirstOrDefault(d => string.Equals(
            d.GetKey(language),
            displayName,
            StringComparison.Ordinal));
        if (def is not null)
        {
            return def.GetKey(language);
        }

        // Renamed match: scan overrides whose key belongs to the CURRENT
        // language and whose OverrideName equals the display name.  The
        // language guard prevents an EN-only rename from claiming a UA
        // editor's selection.
        if (config.DefaultPromptOverrides is null)
        {
            return null;
        }

        foreach (var (key, ov) in config.DefaultPromptOverrides)
        {
            var matchesLang = Defaults.Any(d => string.Equals(
                d.GetKey(language),
                key,
                StringComparison.Ordinal));
            if (!matchesLang)
            {
                continue;
            }

            if (string.Equals(ov.OverrideName, displayName, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }
}
