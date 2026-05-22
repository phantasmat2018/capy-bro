using CapyBro.Models;

namespace CapyBro.Services;

public interface IPromptRegistry
{
    /// <summary>
    /// Returns all 8 default prompt KEYS for the given language, in stable index order.
    /// </summary>
    IReadOnlyList<string> GetDefaultKeys(Language language);

    /// <summary>
    /// Builds the active prompts dictionary for the given config + language: defaults
    /// (minus any user-deleted ones) merged with custom prompts.
    /// </summary>
    IReadOnlyDictionary<string, Prompt> GetActive(AppConfig config, Language language);

    /// <summary>
    /// Returns the all-language equivalents (UK/RU/EN keys) of the default-prompt slot that
    /// matches the given key, so a user-deletion can persist across language switches.
    /// Returns empty list if the key is not a default.
    /// </summary>
    IReadOnlyList<string> GetAllEquivalentsForDefaultKey(string key);

    /// <summary>
    /// Returns the canonical English key of the default-prompt slot that matches the
    /// given key (any language).  Returns <c>null</c> if the key does not match any
    /// default slot.
    /// </summary>
    string? GetCanonicalEnglishKeyForDefault(string anyLanguageKey);

    /// <summary>
    /// Reverse-resolves a name shown in the active list back to the preset slot's
    /// language-specific key (which is what <see cref="AppConfig.DefaultPromptOverrides"/>
    /// is keyed by).  Returns:
    /// <list type="bullet">
    ///   <item>The matching <c>def.GetKey(language)</c> when the display name IS an
    ///   un-renamed default for the current language.</item>
    ///   <item>The override's storage key when the display name comes from a renamed
    ///   override (Prompt.OverrideName) for the current language.</item>
    ///   <item><c>null</c> when the name is a fully-custom prompt or unknown.</item>
    /// </list>
    /// Used by the prompts editor to decide whether a save targets an override entry
    /// or a CustomPrompts entry.
    /// </summary>
    string? ResolveOriginalPresetKey(AppConfig config, Language language, string displayName);
}
