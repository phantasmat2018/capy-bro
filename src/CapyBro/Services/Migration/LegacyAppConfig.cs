using CapyBro.Models;

using AppLanguage = CapyBro.Models.Language;

namespace CapyBro.Services.Migration;

internal sealed record LegacyAppConfig
{
    public string? Model { get; init; }

    public IReadOnlyList<string>? Models { get; init; }

    public int? Timeout { get; init; }

    public string? Language { get; init; }

    public IReadOnlyDictionary<string, LegacyPrompt>? CustomPrompts { get; init; }

    public IReadOnlyList<string>? DeletedDefaults { get; init; }

    public string? DefaultPrompt { get; init; }

    public string? Hotkey { get; init; }

    public string? MenuHotkey { get; init; }

    // Z2-F6 / M5: UndoHotkey was introduced in v11 — pre-v11 users by
    // definition cannot have set it, so the migration falls through to
    // `AppConfig.Default.UndoHotkey` in the common case.  We accept the
    // field anyway so a manually-edited or future-rolled-back legacy JSON
    // carrying `undo_hotkey` is preserved instead of silently dropped.
    // Same pattern as Hotkey/MenuHotkey above; the snake_case naming
    // policy on LegacyConfigJsonContext handles the JSON key mapping.
    public string? UndoHotkey { get; init; }

    public AppConfig ToAppConfig()
    {
        var defaults = AppConfig.Default;

        var prompts = CustomPrompts is null
            ? new Dictionary<string, Prompt>()
            : CustomPrompts.ToDictionary(
                kvp => kvp.Key,
                kvp => new Prompt
                {
                    Text = kvp.Value.Text ?? string.Empty,
                    PreserveLanguage = kvp.Value.PreserveLanguage ?? false,
                    ShowDiffPreview = kvp.Value.ShowDiffPreview ?? false,
                    Model = kvp.Value.Model,
                });

        return new AppConfig
        {
            ConfigVersion = AppConfig.CurrentConfigVersion,
            Model = Model ?? defaults.Model,
            Models = Models ?? defaults.Models,
            Timeout = Timeout ?? defaults.Timeout,
            Language = ParseLanguage(Language),
            CustomPrompts = prompts,
            DeletedDefaults = DeletedDefaults ?? [],
            DefaultPrompt = DefaultPrompt ?? defaults.DefaultPrompt,
            Hotkey = NormalizeHotkey(Hotkey) ?? defaults.Hotkey,
            MenuHotkey = NormalizeHotkey(MenuHotkey) ?? defaults.MenuHotkey,
            UndoHotkey = NormalizeHotkey(UndoHotkey) ?? defaults.UndoHotkey,
            // Legacy v1 had no diff-preview feature; legacy → v3 migration
            // adopts the documented default (on) so users get the new
            // behaviour without manually enabling the master flag.
            ExperimentalDiffPreview = defaults.ExperimentalDiffPreview,
            // Same rationale for streaming — v1 didn't have it; adopt the
            // current default so live toast updates work after upgrade.
            ExperimentalStreaming = defaults.ExperimentalStreaming,
            // Per-prompt model override is also a v5 addition; legacy
            // prompts have no model field, so the global model is used
            // until the user opts into the experiment.
            ExperimentalPerPromptModel = defaults.ExperimentalPerPromptModel,
            // Credits/costs is v6 — legacy adopts the documented default
            // (off) so we don't make /credits or /models pricing calls
            // until the user opts in.
            ExperimentalCostsAndCredits = defaults.ExperimentalCostsAndCredits,
            // Privacy redaction is v7 — also off by default; user opts in
            // through General → Experimental.
            ExperimentalPrivacyRedaction = defaults.ExperimentalPrivacyRedaction,
            // Improvement history is v11 — gated behind the same
            // experimental opt-in pattern.  Legacy v1 had no history
            // concept, so adopting the documented v11 default (off)
            // means migrated users get a clean General → Experimental
            // entry to enable history when they want it.
            ExperimentalHistory = defaults.ExperimentalHistory,
            // Post-paste re-selection is v12 — same experimental opt-in
            // shape: legacy users land on the documented default (off)
            // and can enable via General → Additional features.
            ExperimentalKeepResultSelected = defaults.ExperimentalKeepResultSelected,
            // Developer mode is v13 — hidden 20-tap eye-icon gesture.
            // Legacy users start locked; unlock is the same gesture as
            // for new installs.  Persisted across launches once entered.
            DeveloperModeEnabled = defaults.DeveloperModeEnabled,
            // Onboarding-completed is v8. A legacy v1 user already has all
            // of their settings filled in (they wouldn't have an .ai_text_
            // improver_config.json otherwise), so showing the wizard would
            // be pure noise — bypass it.
            OnboardingCompleted = true,
            // DefaultPromptOverrides is v9 — legacy never had this concept,
            // start empty.
            DefaultPromptOverrides = defaults.DefaultPromptOverrides,
            // DefaultPromptSettings is v10 — slot-level shared settings;
            // empty for legacy users.
            DefaultPromptSettings = defaults.DefaultPromptSettings,
        };
    }

    private static string? NormalizeHotkey(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : HotkeyAccelerator.Normalize(value);

    private static AppLanguage ParseLanguage(string? value) => value?.ToLowerInvariant() switch
    {
        "ukrainian" or "uk" or "ukr" => AppLanguage.Ukrainian,
        "russian" or "ru" or "rus" => AppLanguage.Russian,
        "english" or "en" or "eng" => AppLanguage.English,
        // Unknown / null values fall back to English — the rebranded
        // product's canonical default.  Pre-rebrand this was Ukrainian
        // (the team's working locale during initial development of
        // "AI Text Improver").
        _ => AppLanguage.English,
    };
}
