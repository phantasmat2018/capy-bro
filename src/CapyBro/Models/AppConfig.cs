namespace CapyBro.Models;

public sealed record AppConfig
{
    // Schema versioning — bump whenever a new field is added whose
    // semantic default differs from default(T). Migration in
    // WithDefaultsApplied detects ConfigVersion < N and back-fills the
    // documented default for fields introduced at version N.
    //   v2 → v3: ExperimentalDiffPreview        (now default false)
    //   v3 → v4: ExperimentalStreaming          (now default false)
    //   v4 → v5: ExperimentalPerPromptModel     (default false)
    //   v5 → v6: ExperimentalCostsAndCredits    (default false)
    //   v6 → v7: ExperimentalPrivacyRedaction   (default false)
    //   v7 → v8: OnboardingCompleted            (default false — first-run wizard gate)
    //   v8 → v9: DefaultPromptOverrides         (per-default text/option overrides)
    //   v9 → v10: DefaultPromptSettings         (slot-level settings shared across locales)
    //   v10 → v11: ExperimentalHistory          (master flag for record + view of improvement history)
    //   v11 → v12: ExperimentalKeepResultSelected (post-paste re-selection master flag)
    //   v12 → v13: DeveloperModeEnabled         (hidden 20-click eye-toggle secret unlocks Beta-features section)
    //   v13 → v14: Timeout semantic change      (`0` now means "no timeout / wait indefinitely" — pre-v14 it was just an invalid value clamped to Default; the bump lets WithDefaultsApplied tell "user explicitly set 0" from "field missing in pre-v14 JSON").
    //   v14 → v15: Provider + OllamaEndpoint + OllamaModel + OllamaModels (Ollama-as-alternative-backend support; existing installs upgrade to Provider=OpenRouter so behaviour is unchanged).
    //   v15 → v16: ExperimentalDiffPreview default flipped to true; the diff-preview-before-paste feature is now ON for everyone by default so each AI-rewritten paragraph passes a verification step before it touches the user's document.  User-feedback driven: pre-v16 a fresh install never saw the modal unless they hunted through General → Experimental, so 90% of users had no idea the safety net existed.  The mass-migration gate (ConfigVersion < 16) overrides any pre-v16 saved value to true; anyone who deliberately disabled it can re-disable via the (now always-visible) per-prompt checkbox or the master toggle.
    // The migration is still a meaningful no-op even when current default
    // equals default(T) — it bumps the stored ConfigVersion so future
    // schema additions can target users by introduction version.
    public const int CurrentConfigVersion = 16;

    public int ConfigVersion { get; init; } = CurrentConfigVersion;

    public required string Model { get; init; }

    public required IReadOnlyList<string> Models { get; init; }

    /// <summary>
    /// Which LLM backend <see cref="Services.TextProcessor"/>
    /// sends the user's text to.  Default <see cref="LlmProviderKind.OpenRouter"/>
    /// preserves behaviour for every install that existed before v15;
    /// privacy-conscious users opt in to <see cref="LlmProviderKind.Ollama"/>
    /// via Settings → General.  When Ollama is selected, the
    /// <see cref="OllamaEndpoint"/> + <see cref="OllamaModel"/> +
    /// <see cref="OllamaModels"/> trio takes the place of the
    /// OpenRouter <see cref="Model"/> / <see cref="Models"/> pair —
    /// the OpenRouter fields are left intact on disk so toggling
    /// back doesn't lose the user's existing model picks.
    /// </summary>
    public LlmProviderKind Provider { get; init; } = LlmProviderKind.OpenRouter;

    /// <summary>
    /// Base URL of the Ollama HTTP API.  Default
    /// <c>http://localhost:11434</c> matches <c>ollama serve</c> out of
    /// the box; a power user can point CapyBro at a different Ollama
    /// instance on their LAN by editing this field in General →
    /// "Local models (Ollama)".  Trailing slash is normalised away by
    /// <see cref="Services.OllamaClient"/>; an empty or
    /// whitespace value falls back to the default at request time.
    /// </summary>
    public string OllamaEndpoint { get; init; } = "http://localhost:11434";

    /// <summary>
    /// Currently selected Ollama model tag (e.g. <c>"llama3.2:latest"</c>).
    /// Kept separate from <see cref="Model"/> so the user's OpenRouter
    /// pick survives a Provider toggle — switching back to OpenRouter
    /// should restore the same model the user had before, not lose it.
    /// Empty string is the new-install state; <see cref="WithDefaultsApplied"/>
    /// does NOT auto-populate this because the available tags depend on
    /// what the user has <c>ollama pull</c>-ed locally — we'd be guessing.
    /// </summary>
    public string OllamaModel { get; init; } = string.Empty;

    /// <summary>
    /// Saved set of Ollama model tags surfaced in the picker.  Populated
    /// by "Refresh" in General → "Local models (Ollama)" which calls
    /// <c>GET /api/tags</c>.  Empty list on a fresh install — first
    /// refresh seeds it.  Same separation rationale as
    /// <see cref="OllamaModel"/>.
    /// </summary>
    public IReadOnlyList<string> OllamaModels { get; init; } = [];

    // Per-request OpenRouter timeout in seconds.  Bumped from 30 → 60
    // in v14: longer streaming responses on slower-tier models (Claude
    // Opus / Llama 70B / etc.) were hitting the 30 s ceiling for
    // legitimate first-token-to-last-token windows.  60 s gives the
    // long tail headroom without making "user actually wants to cancel"
    // feel like the app froze.  Exposed in Additional features per
    // L4 / Z2-F8 so power users can tune it further.
    //
    // Sentinel: `Timeout = 0` means "no timeout" / "wait indefinitely".
    // TextProcessor translates 0 to Timeout.InfiniteTimeSpan before
    // handing to OpenRouterClient, which then skips the CancelAfter
    // schedule entirely.  External cancellation (user-cancel hotkey,
    // OnExit ShutdownGracefully) still works — only the SCHEDULE-based
    // expiry is disabled.  Useful for long-tail jobs where any
    // non-zero ceiling would false-positive into api_request_timeout
    // toasts.  WithDefaultsApplied gates negative values to the
    // documented Default (60); 0 passes through.
    //
    // v15: timeout is provider-scoped.  This field is the OpenRouter
    // timeout; <see cref="OllamaTimeout"/> is its Ollama counterpart
    // (default 120s — local models tend to be slower than hosted
    // cloud routes so a longer ceiling avoids false-positive
    // cancellations).  TextProcessor picks the matching one at request
    // time based on AppConfig.Provider.
    public int Timeout { get; init; } = 60;

    /// <summary>
    /// Per-request Ollama timeout in seconds.  Default 120s vs. 60s
    /// for OpenRouter — local models on user hardware are typically
    /// slower than cloud-hosted ones, especially for the first run
    /// after a model load.  Same <c>0 = infinite</c> sentinel as
    /// <see cref="Timeout"/>.  Stored separately so the user can
    /// tune each provider independently without juggling settings
    /// every time they toggle the provider.
    /// </summary>
    public int OllamaTimeout { get; init; } = 120;

    // H3 (Z2-F3) fix: init default aligns with AppConfig.Default and with
    // the new enum order (English = 0).  STJ source-gen still overwrites
    // this with default(T) for configs missing the `language` key, but
    // default(Language) is now English — so the silent UA fallback closes
    // without an additional WithDefaultsApplied patch.
    public Language Language { get; init; } = Language.English;

    public IReadOnlyDictionary<string, Prompt> CustomPrompts { get; init; }
        = new Dictionary<string, Prompt>();

    /// <summary>
    /// Per-language overrides for default prompts, keyed by the
    /// LANGUAGE-SPECIFIC default name (e.g. <c>"Перекласти на українську"</c>
    /// is one entry, <c>"Translate to Ukrainian"</c> a separate one — they
    /// can both be present and they don't shadow each other).  Editing a
    /// default's text/options in one UI language only writes the override
    /// for THAT language's key; switching the UI language shows the
    /// original default text for any language the user has not edited.
    /// Renames still move the entry into <see cref="CustomPrompts"/> +
    /// <see cref="DeletedDefaults"/> (a renamed prompt is no longer "the
    /// same default").  Stored independently of the UI language so a
    /// language switch doesn't lose anything.
    /// </summary>
    public IReadOnlyDictionary<string, Prompt> DefaultPromptOverrides { get; init; }
        = new Dictionary<string, Prompt>();

    /// <summary>
    /// Slot-level user settings for the 8 built-in default prompts —
    /// PreserveLanguage / ShowDiffPreview / Model.  Keyed by the
    /// canonical English slot name (see <see cref="PromptRegistry"/>).
    /// These flags apply uniformly across ALL UI languages of the same
    /// slot: toggling "Preserve source language" on the UA copy of "Fix
    /// errors" has the same effect on its EN / RU copies, because the
    /// behaviour is a property of the prompt's purpose, not of the
    /// language that happened to be active when the user clicked.  Text
    /// and rename are per-locale via
    /// <see cref="DefaultPromptOverrides"/>.
    /// </summary>
    public IReadOnlyDictionary<string, DefaultPromptSlotSettings> DefaultPromptSettings { get; init; }
        = new Dictionary<string, DefaultPromptSlotSettings>();

    public IReadOnlyList<string> DeletedDefaults { get; init; } = [];

    public string DefaultPrompt { get; init; } = string.Empty;

    public string Hotkey { get; init; } = "Ctrl+Shift+E";

    public string MenuHotkey { get; init; } = "Ctrl+Shift+Q";

    public string UndoHotkey { get; init; } = "Ctrl+Shift+Z";

    /// <summary>
    /// Master kill-switch for the diff-preview-before-paste feature. When
    /// false, TextProcessor skips the modal regardless of any prompt's
    /// per-prompt <see cref="Prompt.ShowDiffPreview"/> opt-in.
    ///
    /// v16: default flipped to <c>true</c>.  The feature graduated out of
    /// "experimental" — every fresh install (and every pre-v16 upgrader,
    /// see the v15 → v16 migration in <see cref="WithDefaultsApplied"/>)
    /// now sees the preview by default.  Users who prefer the
    /// instant-paste flow can disable per-prompt via the editor checkbox
    /// (now always visible, no longer gated behind Experimental) or flip
    /// this master switch off in Settings → General.
    /// </summary>
    public bool ExperimentalDiffPreview { get; init; }

    /// <summary>
    /// Master flag for the streaming-response experimental feature. When
    /// false, TextProcessor accumulates the model output silently and
    /// shows a static "Обробка..." toast — the original behaviour before
    /// streaming UI was introduced. The HTTP transport still uses SSE
    /// regardless (better cancellation efficiency — closing the stream
    /// stops server-side generation early). The flag controls only the
    /// per-chunk <c>ProcessingStreamUpdated</c> event firing, i.e. the
    /// user-visible "live updates in the toast" portion of the feature.
    /// Default false (see <see cref="ExperimentalDiffPreview"/>).
    /// </summary>
    public bool ExperimentalStreaming { get; init; }

    /// <summary>
    /// Master flag for the per-prompt model-override experimental feature.
    /// When true, <see cref="TextProcessor"/> uses <see cref="Prompt.Model"/>
    /// for the API call if the prompt sets one; otherwise falls back to
    /// the global <see cref="Model"/>. When false the global model is
    /// always used regardless of any prompt-level override (kill switch).
    /// </summary>
    public bool ExperimentalPerPromptModel { get; init; }

    /// <summary>
    /// Master flag for the credits-and-cost experimental feature. When
    /// true, the General tab shows the OpenRouter account balance and
    /// the "Обробка..." toast is suffixed with a rough per-request cost
    /// estimate. When false, no extra API calls are made (no balance
    /// fetch, no pricing fetch) and the toast stays as before.
    /// </summary>
    public bool ExperimentalCostsAndCredits { get; init; }

    /// <summary>
    /// Master flag for the privacy-redaction experimental feature. When
    /// true, <see cref="TextProcessor"/> redacts emails / URLs / IBANs /
    /// phone numbers in the user's selection BEFORE sending to OpenRouter,
    /// then restores the originals in the AI's response. When false, the
    /// selection is sent verbatim. The flag does not affect the system
    /// prompt content (that's user-defined instructions, not user data).
    /// </summary>
    public bool ExperimentalPrivacyRedaction { get; init; }

    /// <summary>
    /// Master flag for the improvement-history feature.  When true,
    /// <see cref="TextProcessor"/> appends each successful improvement
    /// to <see cref="IHistoryStore"/> and the Settings window's sidebar
    /// exposes the History tab.  When false, no entry is recorded (no
    /// JSON file is written either) and the History tab is hidden.
    ///
    /// v14: default flipped to TRUE.  Early feedback showed users
    /// expected History to be on by default ("where are my previous
    /// improvements?" reaction); the feature has been stable since v11
    /// with no privacy concerns (the file is per-user under
    /// %USERPROFILE% and never leaves the machine).  The kill-switch
    /// shape stays — anyone uncomfortable with any improvement log can
    /// flip it off in General → Additional features and TextProcessor's
    /// IHistoryStore.Add gate honours their choice straight away.
    ///
    /// Pre-v11 users upgrading via WithDefaultsApplied (ConfigVersion
    /// &lt; 11) pick up the new default = true.  v11-v13 users with the
    /// stored field already at false keep their false; we don't silently
    /// flip features on for existing installs.  Fresh v14+ installs see
    /// History enabled from the start.
    /// </summary>
    public bool ExperimentalHistory { get; init; } = true;

    /// <summary>
    /// Master flag for the post-paste re-selection experimental feature.
    /// When true, <see cref="TextProcessor"/> re-selects the just-pasted
    /// text after a successful improvement (and after Undo) so the
    /// boundary stays highlighted — the user can immediately copy /
    /// extend / delete without re-selecting manually.  When false, the
    /// caret lands at the end of the pasted text with no selection,
    /// matching the legacy "paste-and-forget" behaviour.  Default false:
    /// the underlying mechanism (UI Automation TextPattern primary +
    /// Shift+Left synthesis fallback) is robust in modern apps but can
    /// still glitch in edge cases (custom controls without UIA support,
    /// modal dialogs, IME composition state), so users opt in via
    /// General → "Additional features".
    /// </summary>
    public bool ExperimentalKeepResultSelected { get; init; }

    /// <summary>
    /// Hidden "developer mode" master switch.  Toggled by tapping the
    /// eye icon next to the API-key field exactly 20 times in
    /// succession — same gesture for unlock and re-lock so a user who
    /// accidentally enters dev mode can leave it via the same path.
    /// When true, an additional "Beta features" section appears under
    /// Settings → General → "Additional features", visually separated
    /// from regular experimental flags by a divider; the section
    /// surfaces flags that aren't yet ready for opt-in by mainstream
    /// users (currently <see cref="ExperimentalKeepResultSelected"/>).
    /// Persisted to disk so once unlocked the user doesn't have to
    /// repeat the gesture every launch — exit is intentional via the
    /// same 20-tap motion.  Default <c>false</c>: the gesture is
    /// undiscoverable without external knowledge.
    /// </summary>
    public bool DeveloperModeEnabled { get; init; }

    /// <summary>
    /// Set to <c>true</c> after the user has completed (or explicitly
    /// skipped) the first-launch onboarding wizard. The wizard reads this
    /// flag at startup and only opens when it is <c>false</c> — flipping
    /// it permanently silences the wizard on every subsequent launch.
    /// Default <c>false</c> on a fresh install: even an account that
    /// upgraded from v7 (which had no such field) gets the wizard once,
    /// since v7 → v8 migration leaves the default <c>false</c>; that is
    /// the deliberate trade — slightly noisier upgrade UX in exchange for
    /// new-feature discovery on existing installs.
    /// </summary>
    public bool OnboardingCompleted { get; init; }

    public static AppConfig Default { get; } = new()
    {
        ConfigVersion = CurrentConfigVersion,
        // Default model and the model-picker shortlist.  Pre-rebrand the
        // shortlist included "anthropic/claude-3.5-sonnet" as a second
        // option, but smaller / cheaper Claude routes occasionally
        // refuse straightforward text-transformation tasks (especially
        // around translation / rewriting), and we got user reports of
        // the model returning conversational fall-backs.  Trimmed to a
        // single, dependable default — users can still add any other
        // OpenRouter model via Settings → General → "+", they just
        // don't get Claude pre-selected for them.  Bumped from
        // "openai/gpt-4o-mini" to the full "openai/gpt-4o" because the
        // mini variant exhibited the same instruction-shaped-input
        // confusion the structural-defence change in OpenRouterClient
        // was designed to fix; gpt-4o handles the structural defence
        // more reliably.
        Model = "openai/gpt-4o",
        Models = ["openai/gpt-4o"],
        Provider = LlmProviderKind.OpenRouter,
        OllamaEndpoint = "http://localhost:11434",
        OllamaModel = string.Empty,
        OllamaModels = [],
        Timeout = 60,
        OllamaTimeout = 120,
        // Brand decision: default to English on every fresh install.
        // Pre-rebrand the default was Ukrainian (matched the team's
        // primary locale during initial development), but the product
        // now ships internationally as "CapyBro" with English as the
        // canonical interface language.  System-locale auto-detection
        // is also disabled at startup (App.DetectLocale was removed
        // from the first-run path) so a Ukrainian-locale Windows boot
        // doesn't silently flip the wizard back to Ukrainian — the
        // user explicitly opts in via the wizard's Language step or
        // Settings → General.
        Language = Language.English,
        CustomPrompts = new Dictionary<string, Prompt>(),
        DefaultPromptOverrides = new Dictionary<string, Prompt>(),
        DefaultPromptSettings = new Dictionary<string, DefaultPromptSlotSettings>(),
        DeletedDefaults = [],
        DefaultPrompt = string.Empty,
        Hotkey = "Ctrl+Shift+E",
        MenuHotkey = "Ctrl+Shift+Q",
        UndoHotkey = "Ctrl+Shift+Z",
        // v16: was false in v15.  Flipped to true so the diff-preview
        // safety net is on for every fresh install + every upgrader (the
        // mass migration in WithDefaultsApplied overrides any pre-v16
        // saved value).  See the v15 → v16 migration entry above.
        ExperimentalDiffPreview = true,
        ExperimentalStreaming = false,
        ExperimentalPerPromptModel = false,
        ExperimentalCostsAndCredits = false,
        ExperimentalPrivacyRedaction = false,
        // v14: enabled by default — see field-level comment above.
        ExperimentalHistory = true,
        ExperimentalKeepResultSelected = false,
        DeveloperModeEnabled = false,
        OnboardingCompleted = false,
    };

    // System.Text.Json source-gen for records with `required` members invokes the parameterized-
    // ctor creator path, which overwrites every init property with default(T) for fields absent
    // from JSON — bypassing C# initializer values. This restores defaults for the non-required
    // fields after deserialization. See: dotnet/runtime#92877.
    //
    // The C# `required` modifier is NOT enforced by source-gen JSON, so a hand-edited file with
    // `"model": null` or no `models` field will deserialize Model=null / Models=null, then NRE
    // when handed to OpenRouter or rendered in the ComboBox. We patch both here.
    public AppConfig WithDefaultsApplied()
    {
        var d = Default;

        // Per-version migration: when loading a config written by an older
        // version of the app, fields introduced AFTER that version are
        // missing from the JSON and STJ source-gen assigns default(T) to
        // them. For most types default is harmless (e.g. empty string), but
        // a bool field whose semantic default is true (like
        // ExperimentalDiffPreview) would silently flip OFF for upgraders.
        // Restore the documented default whenever the loaded version is
        // older than the field's introduction version.
        //
        // v16: mass-flip ExperimentalDiffPreview to true for everyone on
        // pre-v16 configs (not just pre-v3 where the field didn't yet
        // exist).  The feature was effectively invisible to ~90% of
        // users — those who never thought to enable it under General →
        // Experimental — even though the per-prompt opt-in existed.
        // Surfacing it by default is the safer baseline (verify-before-
        // commit on every AI rewrite); anyone who explicitly turned it
        // off on v15 gets it re-enabled and can turn it off again via
        // the (now always-visible) per-prompt checkbox or the master
        // toggle.  v16+ JSON keeps the user's saved value.
        var experimentalDiffPreview = ConfigVersion < 16
            ? d.ExperimentalDiffPreview
            : ExperimentalDiffPreview;

        var experimentalStreaming = ConfigVersion < 4
            ? d.ExperimentalStreaming
            : ExperimentalStreaming;

        var experimentalPerPromptModel = ConfigVersion < 5
            ? d.ExperimentalPerPromptModel
            : ExperimentalPerPromptModel;

        var experimentalCostsAndCredits = ConfigVersion < 6
            ? d.ExperimentalCostsAndCredits
            : ExperimentalCostsAndCredits;

        var experimentalPrivacyRedaction = ConfigVersion < 7
            ? d.ExperimentalPrivacyRedaction
            : ExperimentalPrivacyRedaction;

        var onboardingCompleted = ConfigVersion < 8
            ? d.OnboardingCompleted
            : OnboardingCompleted;

        var experimentalHistory = ConfigVersion < 11
            ? d.ExperimentalHistory
            : ExperimentalHistory;

        var experimentalKeepResultSelected = ConfigVersion < 12
            ? d.ExperimentalKeepResultSelected
            : ExperimentalKeepResultSelected;

        var developerModeEnabled = ConfigVersion < 13
            ? d.DeveloperModeEnabled
            : DeveloperModeEnabled;

        // v15: Provider + Ollama* fields.  Pre-v15 configs have no
        // Provider key in JSON, which STJ source-gen deserializes as
        // default(LlmProviderKind) == LlmProviderKind.OpenRouter — that
        // happens to match the documented default for upgraders, so the
        // gate here is a no-op in practice, but kept explicit for
        // symmetry with the other version gates and to document the
        // intentional behaviour ("existing installs keep OpenRouter").
        var provider = ConfigVersion < 15
            ? d.Provider
            : Provider;

        // Ollama fields: pre-v15 JSON lacks them, so they deserialize
        // to default(T) (empty string / empty list).  Replace empty/null
        // collection refs with the canonical Default sentinels for v15
        // consumers; the endpoint string falls back to the documented
        // localhost:11434 default when the user has cleared the field.
        var ollamaEndpoint = string.IsNullOrWhiteSpace(OllamaEndpoint)
            ? d.OllamaEndpoint
            : OllamaEndpoint;

        var ollamaModels = OllamaModels ?? d.OllamaModels;

        return this with
        {
            ConfigVersion = ConfigVersion <= 0 ? d.ConfigVersion : Math.Max(ConfigVersion, CurrentConfigVersion),
            Model = string.IsNullOrWhiteSpace(Model) ? d.Model : Model,
            Models = Models is { Count: > 0 } ? Models : d.Models,
            Provider = provider,
            OllamaEndpoint = ollamaEndpoint,
            // OllamaModel is intentionally NOT defaulted — empty string
            // is the legitimate "user hasn't picked a local model yet"
            // state, and we'd be guessing if we filled in a tag they
            // may not have pulled.  TextProcessor surfaces an actionable
            // "open Settings → Local models" toast when this is empty
            // and Provider=Ollama.
            OllamaModel = OllamaModel ?? string.Empty,
            OllamaModels = ollamaModels,
            // v14: `Timeout = 0` is a SUPPORTED sentinel meaning "no
            // timeout" / "wait indefinitely" — see TextProcessor's
            // Timeout.InfiniteTimeSpan translation.  Negative values
            // are nonsense (TimeSpan.FromSeconds(-N) would cancel
            // immediately) and fall back to the Default.
            //
            // ConfigVersion gate: pre-v14 JSON files that LACK the
            // `timeout` field deserialize to Timeout = 0 (default(int)).
            // For those (ConfigVersion < 14) we treat 0 as "missing
            // field" and apply the documented Default — the user never
            // had the chance to set it explicitly.  For v14+ JSON, 0
            // is the user's explicit "wait indefinitely" choice and
            // passes through.
            Timeout =
                Timeout < 0 || (ConfigVersion < 14 && Timeout == 0)
                    ? d.Timeout
                    : Timeout,
            // v15: same sentinel rules as Timeout — `0` means infinite,
            // negative is invalid and falls back to default, pre-v15
            // JSON has no field at all (deserializes to 0) so the
            // ConfigVersion gate substitutes the documented default
            // 120s.  v15+ users with an explicit 0 get the "wait
            // forever" semantics; v15+ users with a positive value
            // keep their tuning.
            OllamaTimeout =
                OllamaTimeout < 0 || (ConfigVersion < 15 && OllamaTimeout == 0)
                    ? d.OllamaTimeout
                    : OllamaTimeout,
            CustomPrompts = CustomPrompts ?? d.CustomPrompts,
            DefaultPromptOverrides = DefaultPromptOverrides ?? d.DefaultPromptOverrides,
            DefaultPromptSettings = DefaultPromptSettings ?? d.DefaultPromptSettings,
            DeletedDefaults = DeletedDefaults ?? d.DeletedDefaults,
            DefaultPrompt = DefaultPrompt ?? d.DefaultPrompt,
            Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? d.Hotkey : Hotkey,
            MenuHotkey = string.IsNullOrWhiteSpace(MenuHotkey) ? d.MenuHotkey : MenuHotkey,
            UndoHotkey = string.IsNullOrWhiteSpace(UndoHotkey) ? d.UndoHotkey : UndoHotkey,
            ExperimentalDiffPreview = experimentalDiffPreview,
            ExperimentalStreaming = experimentalStreaming,
            ExperimentalPerPromptModel = experimentalPerPromptModel,
            ExperimentalCostsAndCredits = experimentalCostsAndCredits,
            ExperimentalPrivacyRedaction = experimentalPrivacyRedaction,
            ExperimentalHistory = experimentalHistory,
            ExperimentalKeepResultSelected = experimentalKeepResultSelected,
            DeveloperModeEnabled = developerModeEnabled,
            OnboardingCompleted = onboardingCompleted,
        };
    }
}
