using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Data;

using CapyBro.Models;
using CapyBro.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

namespace CapyBro.ViewModels;

public sealed partial class GeneralTabViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ApiKeyDebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly IConfigStore _configStore;
    private readonly ICredentialStore _credentials;
    private readonly IAutostartService _autostart;
    private readonly IHotkeyManager _hotkeys;
    private readonly ITranslator _translator;
    private readonly IModelBrowser _modelBrowser;
    private readonly IOpenRouterClient _openRouter;
    private readonly ILlmProviderFactory _providers;
    private readonly INotificationService _notifications;
    private readonly ILogger<GeneralTabViewModel> _logger;

    private readonly Timer _apiKeyDebounceTimer;
    private string? _pendingApiKey;
    private bool _suppressPersist;

    // M6: superseded by Interlocked.Exchange in RefreshOllamaModelsAsync.
    // Cancel-and-replace on every entry so a Provider toggle (or rapid
    // double-click on the refresh button) cleanly aborts the in-flight
    // request — without this, a slow GetModelsAsync that the user has
    // toggled away from would still post its success toast to the
    // newly-active OpenRouter screen.
    private CancellationTokenSource? _ollamaRefreshCts;

    // Event-driven Ollama-availability machinery.  No background
    // poll — the probe fires from three UI events instead:
    //   • App startup (via the fire-and-forget call in the ctor)
    //   • Settings window opening (SettingsWindow.Show / Loaded)
    //   • Sidebar tab clicks (General / Prompts / History) via
    //     SettingsWindowViewModel's Show*Command relay.
    // This keeps probe traffic to handful-per-session instead of one
    // call every 5s, and the user-perceived latency stays at zero
    // because each probe-firing moment is one the user just
    // initiated.
    //
    // `_ollamaProbeInFlight` is the 0/1 sentinel that collapses
    // overlapping probe requests (rapid tab clicks shouldn't queue
    // multiple parallel HTTP calls).
    // `_ollamaAvailableLastProbe` tracks the previous probe result
    // so the auto-revert toast fires ONLY on a true→false transition
    // (not on every probe when Ollama stays down — that would spam
    // identical toasts as the user clicks around the sidebar).
    // `_ollamaProbeHasRun` distinguishes the first probe from
    // subsequent ones: the first probe-result-was-down case is
    // treated as a "startup trap" if Provider=Ollama on disk
    // (user saved Ollama mid-session, app restart, Ollama no longer
    // running) and triggers the same one-time revert.
    private int _ollamaProbeInFlight;
    private bool _ollamaAvailableLastProbe;
    private bool _ollamaProbeHasRun;

    // Z7-F3 / M19: source-of-truth for BalanceDisplay so a mid-session
    // language switch can re-resolve through _translator.  Null when the
    // current value is either empty or a raw string (e.g.
    // OpenRouterException.Message) that doesn't map to a Translator key.
    private (string Key, object[]? Args)? _balanceSource;

    // Track the most recent fire-and-forget PersistConfigAsync so OnExit
    // (Z10-F1) and the graceful DispatcherUnhandledException path (Z10-F10)
    // can await the latest in-flight write before tearing down the host.
    // Pre-fix, every checkbox/language change scheduled `_ = PersistConfigAsync()`
    // and the user-visible toggle could silently fail to land on disk when
    // shutdown raced the save.
    private Task _lastPersistTask = Task.CompletedTask;
    private readonly object _persistTaskLock = new();

    // Last successfully-applied hotkey values, used to revert the UI when
    // the user tries to assign a combo that's already in use by one of the
    // other hotkey slots. Without these, a conflict + revert would have to
    // round-trip through the config store.
    private string _lastAppliedHotkey = string.Empty;
    private string _lastAppliedMenuHotkey = string.Empty;
    private string _lastAppliedUndoHotkey = string.Empty;

    // FZ2-F3 / M33 — inline-visible conflict message per hotkey slot.
    // Pre-fix the conflict path showed ONLY a transient toast (auto-
    // closes after 3.5 s) plus an immediate UI revert; a user looking
    // away from the toast lost the explanation entirely and just saw
    // their typed value snap back to the old one with no breadcrumb.
    // Non-empty value → a warning glyph appears alongside the ComboBox
    // with this string as its ToolTip; empty value → no glyph.
    [ObservableProperty]
    private string _hotkeyConflictMessage = string.Empty;

    [ObservableProperty]
    private string _menuHotkeyConflictMessage = string.Empty;

    [ObservableProperty]
    private string _undoHotkeyConflictMessage = string.Empty;

    [ObservableProperty]
    private Language _language;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>
    /// Active LLM backend.  Flipped indirectly by the user via
    /// <see cref="UseOllama"/>'s checkbox in General → "Provider";
    /// the boolean shape avoids fighting WPF radio styling so the
    /// section matches the rest of the app's checkbox-driven settings.
    /// Default <see cref="LlmProviderKind.OpenRouter"/> matches new-
    /// install behaviour; LoadFromConfigAsync overwrites this with the
    /// persisted value.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpenRouterProvider))]
    [NotifyPropertyChangedFor(nameof(IsOllamaProvider))]
    [NotifyPropertyChangedFor(nameof(UseOllama))]
    [NotifyPropertyChangedFor(nameof(EffectiveTimeoutSeconds))]
    [NotifyPropertyChangedFor(nameof(IsBalanceRowVisible))]
    private LlmProviderKind _provider = LlmProviderKind.OpenRouter;

    /// <summary>
    /// Boolean facade over <see cref="Provider"/> so the XAML can bind
    /// a single CheckBox (matching every other settings toggle in the
    /// app) instead of a radio pair.  Checked = Ollama (local);
    /// unchecked = OpenRouter (cloud, the default).  Two-way binding —
    /// the setter funnels through <see cref="Provider"/> so the existing
    /// OnProviderChanged persist + visibility-refresh chain runs
    /// unchanged.
    /// </summary>
    public bool UseOllama
    {
        get => Provider == LlmProviderKind.Ollama;
        set => Provider = value ? LlmProviderKind.Ollama : LlmProviderKind.OpenRouter;
    }

    /// <summary>
    /// Base URL of the Ollama HTTP API.  Bound to the endpoint TextBox
    /// in General → "Local models (Ollama)".  Empty value persists as
    /// "" which <see cref="OllamaClient"/> resolves to the default
    /// <c>http://localhost:11434</c> at request time — so a user who
    /// blanks the field doesn't end up sending requests into the void.
    /// </summary>
    [ObservableProperty]
    private string _ollamaEndpoint = OllamaClient.DefaultEndpoint;

    /// <summary>
    /// Currently selected Ollama model tag.  Kept separate from
    /// <see cref="SelectedModel"/> so a Provider switch doesn't clobber
    /// the user's OpenRouter pick — switching back restores it from
    /// <see cref="AppConfig.Model"/>.
    /// </summary>
    [ObservableProperty]
    private string _selectedOllamaModel = string.Empty;

    /// <summary>
    /// Cached set of Ollama model tags from the last refresh.  Populated
    /// by <see cref="RefreshOllamaModelsAsync"/> (calls <c>GET /api/tags</c>);
    /// empty list on a fresh install — the user clicks Refresh after
    /// they've run <c>ollama pull &lt;tag&gt;</c>.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<string> _ollamaModels = [];

    /// <summary>
    /// True while <see cref="RefreshOllamaModelsAsync"/> is in flight.
    /// Same icon-swap idiom as <see cref="IsRefreshingBalance"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isRefreshingOllamaModels;

    /// <summary>
    /// True while <see cref="ProbeOllamaThenPersistAsync"/> is
    /// validating the local Ollama endpoint after the user has just
    /// flipped the Provider checkbox to Ollama.  XAML can bind this
    /// to a busy indicator next to the checkbox so the user sees the
    /// brief network check rather than wondering why the toggle
    /// didn't take immediately.
    /// </summary>
    [ObservableProperty]
    private bool _isCheckingOllamaConnection;

    /// <summary>
    /// True when the local Ollama endpoint responded to <c>/api/tags</c>
    /// at app startup (or the last refresh probe).  Drives the
    /// Provider card's Visibility — the entire toggle UI stays hidden
    /// for users who don't have Ollama running, so they aren't teased
    /// with an option they can't actually use.  Initialised <c>false</c>
    /// so the card is hidden during the startup probe window
    /// (avoids a brief flash where it appears then collapses).
    /// </summary>
    [ObservableProperty]
    private bool _isOllamaAvailable;

    [ObservableProperty]
    private string _selectedModel = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _models = [];

    [ObservableProperty]
    private string _hotkey = string.Empty;

    [ObservableProperty]
    private string _menuHotkey = string.Empty;

    [ObservableProperty]
    private string _undoHotkey = string.Empty;

    [ObservableProperty]
    private bool _autostartEnabled;

    /// <summary>
    /// Master flag for the diff-preview experimental feature. Bound to the
    /// "Experimental features" section in GeneralTab. Independent of any
    /// individual prompt's per-prompt opt-in — both must be true for the
    /// modal to appear. See <see cref="AppConfig.ExperimentalDiffPreview"/>.
    /// Default false — experimental features ship off; LoadFromConfigAsync
    /// overwrites this with the persisted value.
    /// </summary>
    [ObservableProperty]
    private bool _experimentalDiffPreview;

    /// <summary>
    /// Master flag for the streaming-response experimental feature. When
    /// false, the toast stays static ("Обробка...") instead of showing
    /// the live tail of the AI's output. See
    /// <see cref="AppConfig.ExperimentalStreaming"/>. Default false.
    /// </summary>
    [ObservableProperty]
    private bool _experimentalStreaming;

    /// <summary>
    /// Master flag for the per-prompt-model-override experimental feature.
    /// When false, every run uses the global model regardless of any
    /// per-prompt override the user may have saved. See
    /// <see cref="AppConfig.ExperimentalPerPromptModel"/>. Default false.
    /// </summary>
    [ObservableProperty]
    private bool _experimentalPerPromptModel;

    /// <summary>
    /// Master flag for the credits-and-cost experimental feature. When
    /// false, the balance row is hidden and the toast omits the cost
    /// suffix. See <see cref="AppConfig.ExperimentalCostsAndCredits"/>.
    /// Default false.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBalanceRowVisible))]
    private bool _experimentalCostsAndCredits;

    /// <summary>
    /// Display string for the OpenRouter balance row. Empty when the
    /// experiment is off, "..." while a fetch is in flight, the actual
    /// "$X.XX / $Y.YY" once loaded, or a localized error message on
    /// failure. Bound to a read-only TextBlock; no input semantics.
    /// </summary>
    [ObservableProperty]
    private string _balanceDisplay = string.Empty;

    /// <summary>
    /// Master flag for the privacy-redaction experimental feature. When
    /// true, TextProcessor strips emails / URLs / IBANs / phone numbers
    /// from the user's selection before sending to OpenRouter, then
    /// restores the originals in the response. See
    /// <see cref="AppConfig.ExperimentalPrivacyRedaction"/>. Default false.
    /// </summary>
    [ObservableProperty]
    private bool _experimentalPrivacyRedaction;

    /// <summary>
    /// Master flag for the improvement-history experimental feature.
    /// When true, TextProcessor records each successful run in
    /// IHistoryStore and Settings shows the History sidebar tab.  When
    /// false, no entry is recorded and the sidebar tab is hidden.  See
    /// <see cref="AppConfig.ExperimentalHistory"/>.  Default false.
    /// </summary>
    [ObservableProperty]
    private bool _experimentalHistory;

    /// <summary>
    /// Master flag for the post-paste re-selection experimental
    /// feature.  When true, TextProcessor re-selects the just-pasted
    /// text after a successful improvement (and after Undo) via
    /// <see cref="ITextSelectionExtender"/> — UI Automation TextPattern
    /// primary path, Shift+Left synthesis fallback.  When false, the
    /// caret lands at end of pasted text with no selection.  See
    /// <see cref="AppConfig.ExperimentalKeepResultSelected"/>.  Default
    /// false.  v13: relocated from "Additional features" into the
    /// hidden "Beta features" section that appears only when
    /// <see cref="DeveloperModeEnabled"/> is on.
    /// </summary>
    [ObservableProperty]
    private bool _experimentalKeepResultSelected;

    /// <summary>
    /// Z2-F8 / L4: per-request OpenRouter timeout in seconds.  Backs
    /// <see cref="AppConfig.Timeout"/> (default 30).  Pre-fix the field
    /// was persisted in JSON and consumed by TextProcessor's
    /// CancellationTokenSource.CancelAfter, but had NO UI affordance —
    /// power users could only edit it by hand-modifying the config
    /// file.  v14 surfaces it under the developer-mode-gated "Beta
    /// features" section so the UX matches the rest of the hidden
    /// flag tier: visible only when the 20-tap eye-icon gesture has
    /// unlocked dev mode, with a warning glyph + caption framing it
    /// as rougher than "Additional features".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveTimeoutSeconds))]
    private int _timeoutSeconds = 60;

    /// <summary>
    /// Per-request Ollama timeout in seconds.  Stored separately from
    /// <see cref="TimeoutSeconds"/> so the user can tune each provider
    /// independently — local models on user hardware typically need
    /// a higher ceiling (default 120s) than hosted OpenRouter routes
    /// (60s).  Same <c>0 = infinite</c> sentinel as the OpenRouter side.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveTimeoutSeconds))]
    private int _ollamaTimeoutSeconds = 120;

    /// <summary>
    /// Provider-routed view onto whichever timeout backs the active
    /// LLM provider.  The single Timeout input in General →
    /// "Additional features" two-way binds here so the visible field
    /// always reflects the active provider's tuning without the user
    /// having to remember which numeric setting matches which
    /// backend.  Setter funnels through the underlying provider-
    /// scoped property so the existing OnXxxChanged + persist chain
    /// runs unchanged.
    /// </summary>
    public int EffectiveTimeoutSeconds
    {
        get => Provider == LlmProviderKind.Ollama ? OllamaTimeoutSeconds : TimeoutSeconds;
        set
        {
            if (Provider == LlmProviderKind.Ollama)
            {
                OllamaTimeoutSeconds = value;
            }
            else
            {
                TimeoutSeconds = value;
            }
        }
    }

    /// <summary>
    /// Hidden developer-mode flag mirroring
    /// <see cref="AppConfig.DeveloperModeEnabled"/>.  Toggled by the
    /// 20-tap eye-icon gesture (see <see cref="ToggleDeveloperMode"/>).
    /// XAML binds the visibility of the "Beta features" section to
    /// this property — when off, the section (and any flags housed
    /// under it) is collapsed entirely so the surface area stays
    /// minimal for ordinary users.
    /// </summary>
    [ObservableProperty]
    private bool _developerModeEnabled;

    /// <summary>
    /// Result of the last OpenRouter /credits probe for the current
    /// API key.  Drives the inline status indicator under the API-
    /// key input field.  Updated by <see cref="ValidateApiKeyAsync"/>,
    /// which is fired from the debounce tick after the key is
    /// persisted; reset to <see cref="ApiKeyValidationState.None"/>
    /// on every keystroke so the user sees the previous indicator
    /// clear while they re-type rather than stale-Valid lingering
    /// over a freshly broken key.
    ///
    /// XAML binds five computed booleans
    /// (<see cref="IsApiKeyChecking"/> / <see cref="IsApiKeyValid"/> /
    /// <see cref="IsApiKeyInvalid"/> / <see cref="IsApiKeyNetworkError"/> /
    /// <see cref="ApiKeyStatusVisible"/>) rather than the raw enum so
    /// each indicator row's Visibility lights up independently
    /// without a value converter — see the
    /// <c>[NotifyPropertyChangedFor]</c> attributes that keep them
    /// in sync.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeyChecking))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyValid))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyInvalid))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyNetworkError))]
    [NotifyPropertyChangedFor(nameof(ApiKeyStatusVisible))]
    private ApiKeyValidationState _apiKeyState = ApiKeyValidationState.None;

    // Cancellation source for the in-flight /credits probe.  Renewed
    // on every debounce tick that fires validation; the previous
    // CTS is cancelled + disposed so a slow probe that's been
    // superseded by a newer keystroke quietly aborts instead of
    // flipping ApiKeyState back to Valid AFTER the user has
    // already started typing a new (probably-different) key.
    private CancellationTokenSource? _validationCts;

    /// <summary>
    /// True while <see cref="RefreshBalanceCommand"/> is in flight.
    /// XAML binds this to swap the refresh-button icon between
    /// the static <c>Icon.RefreshCw</c> and a spinning
    /// <c>Icon.Loader2</c> so the user sees the request is alive
    /// rather than wondering if the click registered.  Always set
    /// + cleared inside try/finally so an exception path can't
    /// leave the button stuck in busy state.
    /// </summary>
    [ObservableProperty]
    private bool _isRefreshingBalance;

    /// <summary>
    /// True while <see cref="BrowseModelsCommand"/> is fetching the
    /// OpenRouter catalogue / opening the picker.  Same icon-swap
    /// idiom as <see cref="IsRefreshingBalance"/> — the cloud
    /// glyph swaps to a spinning loader so the user gets feedback
    /// during the 1-3s catalogue fetch.
    /// </summary>
    [ObservableProperty]
    private bool _isBrowsingModels;

    public GeneralTabViewModel(
        IConfigStore configStore,
        ICredentialStore credentials,
        IAutostartService autostart,
        IHotkeyManager hotkeys,
        ITranslator translator,
        IModelBrowser modelBrowser,
        IOpenRouterClient openRouter,
        ILlmProviderFactory providers,
        INotificationService notifications,
        ILogger<GeneralTabViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(translator);

        _configStore = configStore;
        _credentials = credentials;
        _autostart = autostart;
        _hotkeys = hotkeys;
        _translator = translator;
        _modelBrowser = modelBrowser;
        _openRouter = openRouter;
        _providers = providers;
        _notifications = notifications;
        _logger = logger;
        _apiKeyDebounceTimer = new Timer(OnApiKeyDebounceTick);

        // Allow ReplaceCollectionInPlace(_models, …) to run on a
        // threadpool thread without WPF's CollectionView throwing
        // NotSupportedException. Without this the second tray-click
        // crashes the app (first click is fine because the binding
        // hasn't yet wrapped _models in a CollectionView).
        BindingOperations.EnableCollectionSynchronization(_models, new object());
        // Same rationale for the Ollama list — RefreshOllamaModelsAsync
        // mutates it from a background continuation post-await.
        BindingOperations.EnableCollectionSynchronization(_ollamaModels, new object());

        AvailableLanguages =
        [
            new LanguageOption(Language.English, translator["lang_label_english"]),
            new LanguageOption(Language.Ukrainian, translator["lang_label_ukrainian"]),
            new LanguageOption(Language.Russian, translator["lang_label_russian"]),
        ];

        // Z7-F3 / M19: keep BalanceDisplay aligned with the live UI locale
        // when the user switches language mid-session.  Singleton lifetime
        // matches Translator's, so direct subscription is safe — but we
        // unsubscribe in Dispose for symmetry and to avoid a Translator-
        // pinned reference if the DI scope is ever torn down for tests.
        _translator.PropertyChanged += OnTranslatorPropertyChanged;

        // Fire-and-forget startup probe so the Provider card visibility
        // reflects the local Ollama endpoint's reachability without
        // requiring the user to open Settings first.  Runs once at
        // first DI resolve (App.xaml.cs.WireRuntimeBehavior pulls the
        // singleton GeneralTabViewModel into scope on startup).
        // Subsequent probes are event-driven — see RefreshOllamaAvailabilityAsync.
        _ = RefreshOllamaAvailabilityAsync();
    }

    /// <summary>
    /// Public entry point for the event-driven probe.  Wraps
    /// <see cref="ProbeOllamaAvailabilityAsync"/> in an in-flight
    /// guard so rapid-fire callers (Settings window opening +
    /// immediately clicking a sidebar tab) collapse overlapping
    /// invocations into a single probe.  External callers:
    /// SettingsWindow.OnSourceInitialized and SettingsWindowViewModel's
    /// Show*Commands for the sidebar tabs.
    /// </summary>
    public async Task RefreshOllamaAvailabilityAsync()
    {
        if (Interlocked.CompareExchange(ref _ollamaProbeInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await ProbeOllamaAvailabilityAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _ollamaProbeInFlight, 0);
        }
    }

    /// <summary>
    /// Read-time pause between the red "Ollama unreachable" warning
    /// and the green "switched to OpenRouter" confirmation.  2.5 s
    /// fits inside the toast's 3.5 s auto-close window so the first
    /// toast is still visible when the second replaces it — the
    /// user sees a smooth red→green hand-off rather than a flash.
    /// Picked from the published "minimum read time for a short
    /// sentence" range; long enough to absorb the warning, short
    /// enough that the success confirmation doesn't feel delayed.
    /// </summary>
    private static readonly TimeSpan OllamaUnreachableToastGap = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Public unified entry-point for every "Ollama is unreachable"
    /// failure surface in the app (toggle-probe, refresh button,
    /// periodic visibility probe, hotkey-time TextProcessor failure
    /// observed by App.xaml.cs).  Reverts Provider→OpenRouter when
    /// the user is currently on Ollama (no-op otherwise), persists
    /// the change, and surfaces TWO sequential toasts separated by
    /// <see cref="OllamaUnreachableToastGap"/> so the user can
    /// actually read both:
    /// 1. ShowError(ollama_unreachable) — red stripe, the warning.
    /// 2. (2.5 s later) ShowInfo(ollama_switched_to_openrouter) —
    ///    green stripe, the confirmation.
    /// Idempotent: a rapid burst of unreachable failures (user
    /// holds the hotkey) coalesces because the second call sees
    /// Provider already == OpenRouter and short-circuits before
    /// either toast fires.
    /// </summary>
    public async Task HandleOllamaUnreachableAsync()
    {
        if (Provider != LlmProviderKind.Ollama)
        {
            // Already on OpenRouter — nothing to revert and no
            // "switched" confirmation to surface.  Caller (or the
            // original failure path) is responsible for any toast.
            return;
        }

        // Warning first — the user sees what failed before they see
        // what we did about it.
        _notifications.ShowError(_translator["ollama_unreachable"]);
        RevertProviderToOpenRouter();

        // Read-time pause.  The first toast keeps rendering during
        // this delay (its own auto-close timer hasn't fired yet);
        // when ShowInfo lands below it replaces the warning in the
        // same window — visually a smooth colour swap.
        await Task.Delay(OllamaUnreachableToastGap);

        // After the delay the user might have manually re-enabled
        // Ollama (toggle is still wired); if so, the original
        // confirmation message would be stale and confusing.  Skip
        // it in that edge case.
        if (Provider == LlmProviderKind.OpenRouter)
        {
            _notifications.ShowInfo(_translator["ollama_switched_to_openrouter"]);
        }
    }

    /// <summary>
    /// Probes the configured Ollama endpoint and flips
    /// <see cref="IsOllamaAvailable"/> to reflect whether the local
    /// backend is reachable.  Called once from the ctor (startup) and
    /// from <see cref="RefreshOllamaAvailabilityAsync"/> on every
    /// user-initiated UI event that should re-check availability
    /// (Settings window opening, sidebar tab clicks).
    ///
    /// <para>
    /// State-change-driven side effects (avoiding toast spam when
    /// Ollama stays down):
    /// 1. Startup trap (first probe + Provider=Ollama saved + probe
    ///    fails): auto-revert to OpenRouter + one localised
    ///    <c>ollama_unreachable</c> toast.  Recovers the user who
    ///    had Ollama selected last session but launched the app with
    ///    Ollama no longer running.
    /// 2. Mid-session transition (was-available → now-unavailable +
    ///    Provider=Ollama): auto-revert + one toast.  Recovers the
    ///    user from a crashed `ollama serve` so the next hotkey
    ///    doesn't trip a wall of identical error toasts.
    /// 3. Steady-state down (subsequent ticks while Ollama remains
    ///    down): IsOllamaAvailable stays false silently, no toast.
    /// </para>
    /// </summary>
    private async Task ProbeOllamaAvailabilityAsync()
    {
        bool nowAvailable;
        try
        {
            var probe = _providers.Resolve(LlmProviderKind.Ollama);
            await probe.GetModelsAsync(apiKey: string.Empty);
            nowAvailable = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                    and not OutOfMemoryException
                                    and not StackOverflowException)
        {
            _logger.LogDebug(ex, "Ollama availability probe failed ({Endpoint})", OllamaEndpoint);
            nowAvailable = false;
        }

        var wasAvailable = _ollamaAvailableLastProbe;
        var firstProbe = !_ollamaProbeHasRun;
        _ollamaAvailableLastProbe = nowAvailable;
        _ollamaProbeHasRun = true;

        // Marshal IsOllamaAvailable + (potential) revert + toast onto
        // the UI thread.  The poll fires from a thread-pool callback
        // so a direct property mutation would crash the WPF dispatcher
        // on the bound Border's Visibility update.
        var dispatcher = Application.Current?.Dispatcher;

        void ApplyOnUi()
        {
            IsOllamaAvailable = nowAvailable;

            // Startup trap: first probe came back down AND user had
            // Provider=Ollama on disk → recover them.
            // Mid-session transition: probe was-up→now-down AND user
            // is still on Ollama → recover them.
            // Both branches fire the same revert+toast, and only fire
            // ONCE per real state-change (steady-state ticks while
            // Ollama remains down silently no-op).
            var startupTrap = firstProbe && !nowAvailable && Provider == LlmProviderKind.Ollama;
            var transitionDown = !firstProbe && wasAvailable && !nowAvailable && Provider == LlmProviderKind.Ollama;

            if (startupTrap || transitionDown)
            {
                // HandleOllamaUnreachable: revert (no-op if already
                // OpenRouter — won't happen here since branch is
                // gated on Provider==Ollama) + combined "switched"
                // toast.  Single notification surface across the
                // four failure paths.
                _ = HandleOllamaUnreachableAsync();
            }
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyOnUi();
        }
        else
        {
            await dispatcher.InvokeAsync(ApplyOnUi);
        }
    }

    // English-first ordering matches the brand decision (default
    // language is English) and the user's request — UA / RU follow as
    // fallback options.  Pre-rebrand the order was UA-RU-EN to match
    // the team's working locale during initial development.
    //
    // H23 (FZ4-F2) fix: items render as autonyms via DisplayName so the
    // dropdown reads "English / Українська / Русский" rather than the
    // raw enum text.  The Translator dictionaries hold identical strings
    // for these three keys in every locale (they ARE autonyms), so this
    // list does NOT need to refresh on language switch.
    public IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    /// <summary>
    /// True when the inline status indicator under the API-key field
    /// should render at all.  False when the field is empty (no
    /// validation has been kicked off yet) — keeps the surface
    /// quiet until the user actually has something to validate.
    /// </summary>
    public bool ApiKeyStatusVisible => ApiKeyState != ApiKeyValidationState.None;

    /// <summary>
    /// True while the /credits probe is in flight.  Drives the
    /// "Loading..." caption + spinning Loader2 glyph row.
    /// </summary>
    public bool IsApiKeyChecking => ApiKeyState == ApiKeyValidationState.Checking;

    /// <summary>
    /// True after a successful /credits probe.  Drives the green
    /// Check glyph + "Ключ дійсний" caption row.
    /// </summary>
    public bool IsApiKeyValid => ApiKeyState == ApiKeyValidationState.Valid;

    /// <summary>
    /// True when the probe came back with HTTP 401 (or the key
    /// shape was rejected before the network call).  Drives the
    /// red X glyph + "Ключ недійсний" row — the user knows the key
    /// itself is the problem.
    /// </summary>
    public bool IsApiKeyInvalid => ApiKeyState == ApiKeyValidationState.Invalid;

    /// <summary>
    /// True when the probe failed for non-key reasons (timeout, 5xx,
    /// DNS).  Drives the muted AlertTriangle + "Не вдалося
    /// перевірити" row — distinct from Invalid so the user doesn't
    /// re-paste a probably-fine key.
    /// </summary>
    public bool IsApiKeyNetworkError => ApiKeyState == ApiKeyValidationState.NetworkError;

    /// <summary>
    /// True when the user has the OpenRouter provider selected.  Drives
    /// the visibility of the API-key field, the OpenRouter model picker,
    /// and the credits/balance row in General.
    /// </summary>
    public bool IsOpenRouterProvider => Provider == LlmProviderKind.OpenRouter;

    /// <summary>
    /// True when the user has the Ollama provider selected.  Drives the
    /// visibility of the Ollama endpoint + local-model picker rows.
    /// Mutually exclusive with <see cref="IsOpenRouterProvider"/>.
    /// </summary>
    public bool IsOllamaProvider => Provider == LlmProviderKind.Ollama;

    /// <summary>
    /// True only when both (a) the user is on OpenRouter AND (b) the
    /// credits/cost experiment is enabled.  Drives the balance row's
    /// Visibility — without the provider gate, toggling to Ollama
    /// would still surface the OpenRouter balance widget below the
    /// (now-hidden) ExperimentalCostsAndCredits checkbox, which is
    /// confusing.  Two NotifyPropertyChangedFor lines on the source
    /// properties keep this in sync.
    /// </summary>
    public bool IsBalanceRowVisible => IsOpenRouterProvider && ExperimentalCostsAndCredits;

    public void Dispose()
    {
        _apiKeyDebounceTimer.Dispose();

        // Cancel any in-flight validation probe so the VM can be
        // collected without leaking the CTS handle or letting a
        // stale continuation mutate ApiKeyState after the view
        // is gone.
        var cts = Interlocked.Exchange(ref _validationCts, null);
        cts?.Cancel();
        cts?.Dispose();

        // M6: same disposal for the Ollama-refresh CTS.
        var ollamaCts = Interlocked.Exchange(ref _ollamaRefreshCts, null);
        ollamaCts?.Cancel();
        ollamaCts?.Dispose();

        // Z7-F3 / M19 — match the ctor's subscription.  Without this a
        // host re-build in tests (or the Settings → Reset path's recreate)
        // would leave the previous VM strong-rooted from Translator.
        _translator.PropertyChanged -= OnTranslatorPropertyChanged;
    }

    public async Task LoadFromConfigAsync(CancellationToken ct = default)
    {
        // Cancel any pending debounce write — otherwise the timer could land AFTER reload
        // and persist a stale api key over the freshly-loaded one (e.g. on Reset Settings).
        _apiKeyDebounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _pendingApiKey = null;

        // Same logic for the in-flight /credits probe — if the user
        // hits Reset, the validation against the soon-to-be-cleared
        // key should not race ahead and flip the indicator to Valid
        // moments after the field is wiped.  CancelAsync (vs sync
        // Cancel) avoids the VSTHRD103/CA1849 analyzer warning
        // about blocking inside async — the token is marked
        // cancelled synchronously regardless, the await just
        // observes when the registered callbacks have all run.
        var oldCts = Interlocked.Exchange(ref _validationCts, null);
        if (oldCts is not null)
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
        }

        ApiKeyState = ApiKeyValidationState.None;

        var config = await _configStore.LoadAsync(ct);
        var existingKey = await _credentials.GetApiKeyAsync(ct);

        _suppressPersist = true;
        try
        {
            Language = config.Language;
            ApiKey = existingKey ?? string.Empty;

            Provider = config.Provider;
            OllamaEndpoint = config.OllamaEndpoint;
            ReplaceCollectionInPlace(OllamaModels, config.OllamaModels);
            SelectedOllamaModel = config.OllamaModel;

            // Update Models in place to avoid re-rendering ComboBox / losing scroll & selection.
            ReplaceCollectionInPlace(Models, config.Models);

            SelectedModel = config.Model;
            Hotkey = config.Hotkey;
            MenuHotkey = config.MenuHotkey;
            UndoHotkey = config.UndoHotkey;
            AutostartEnabled = _autostart.IsEnabled;
            ExperimentalDiffPreview = config.ExperimentalDiffPreview;
            ExperimentalStreaming = config.ExperimentalStreaming;
            ExperimentalHistory = config.ExperimentalHistory;
            ExperimentalKeepResultSelected = config.ExperimentalKeepResultSelected;
            TimeoutSeconds = config.Timeout;
            OllamaTimeoutSeconds = config.OllamaTimeout;
            ExperimentalPerPromptModel = config.ExperimentalPerPromptModel;
            ExperimentalCostsAndCredits = config.ExperimentalCostsAndCredits;
            ExperimentalPrivacyRedaction = config.ExperimentalPrivacyRedaction;
            DeveloperModeEnabled = config.DeveloperModeEnabled;

            // Seed the conflict-revert checkpoints. The values just loaded
            // are by definition consistent (we wrote them earlier through
            // the same conflict guard, or they came from defaults), so they
            // are a safe fallback if a subsequent edit conflicts.
            _lastAppliedHotkey = Hotkey;
            _lastAppliedMenuHotkey = MenuHotkey;
            _lastAppliedUndoHotkey = UndoHotkey;
        }
        finally
        {
            _suppressPersist = false;
        }

        // Kick off a background validation probe for the loaded
        // key so the indicator reflects truth as soon as the user
        // opens Settings — without this they'd have to re-type the
        // field to trigger validation.  Fire-and-forget because the
        // load itself shouldn't block on a network call; if the
        // probe fails, ApiKeyState flips to NetworkError /
        // Invalid and the user sees the relevant indicator after
        // the request resolves.  The OnApiKeyChanged path handled
        // by typing already covers cancellation if the user starts
        // editing before this probe lands.
        if (!string.IsNullOrEmpty(ApiKey))
        {
            _ = ValidateApiKeyAsync();
        }

        // Z2-F4 / H4 fix: also auto-fetch the OpenRouter balance when the
        // costs/credits experiment is ALREADY enabled.  Pre-fix the
        // OnExperimentalCostsAndCreditsChanged handler kicked off
        // RefreshBalanceAsync only on user toggle, and the load path
        // suppressed that change handler via `_suppressPersist`; so the
        // user opening Settings with the flag already on saw the row
        // visible but the balance display blank until they manually
        // clicked refresh. Mirror the ApiKey auto-validate above.
        if (ExperimentalCostsAndCredits && !string.IsNullOrEmpty(ApiKey))
        {
            _ = RefreshBalanceAsync();
        }
    }

    public Task FlushApiKeyAsync(CancellationToken ct = default) => PersistApiKeyAsync(ct);

    /// <summary>
    /// Awaits the most recently-issued <see cref="PersistConfigAsync"/>
    /// task so callers (e.g. <c>App.OnExit</c>) can ensure the latest
    /// fire-and-forget save reached disk before tearing the host down.
    /// Z10-F1 fix — pre-fix only the API-key debounce was flushed.
    /// </summary>
    public Task FlushPendingConfigAsync()
    {
        Task task;
        lock (_persistTaskLock)
        {
            task = _lastPersistTask;
        }

        return task;
    }

    /// <summary>
    /// Flips developer mode on or off and surfaces a confirmation
    /// toast.  Invoked by <see cref="GeneralTab"/> when the
    /// <c>RevealablePasswordBox.SecretSequenceTriggered</c> event
    /// fires (after 20 consecutive eye-icon clicks).  Symmetric:
    /// each completed 20-tap sequence flips the current state, so
    /// an accidental unlock can be reversed by repeating the
    /// gesture rather than requiring a hidden second motion.
    ///
    /// Persists through the regular config-store path so the
    /// unlocked state survives app restarts — without this, a user
    /// who relies on a beta flag would have to re-enter the
    /// gesture every launch, which feels punitive for what is
    /// already a hidden surface.  Disabling clears the beta flags
    /// it gates (see the OnDeveloperModeEnabledChanged partial)
    /// so a re-locked user doesn't keep silently using a feature
    /// whose checkbox they no longer see.
    /// </summary>
    public void ToggleDeveloperMode()
    {
        DeveloperModeEnabled = !DeveloperModeEnabled;
        try
        {
            _notifications.ShowInfo(
                _translator[DeveloperModeEnabled
                    ? "developer_mode_enabled_toast"
                    : "developer_mode_disabled_toast"]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Toast is purely confirmation noise — a notification-
            // service hiccup must not prevent the actual mode flip.
            _logger.LogDebug(ex, "Failed to surface developer-mode toggle toast");
        }
    }

    [RelayCommand]
    private async Task AddModelAsync(string? model)
    {
        var id = model?.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        // Case-insensitive duplicate check. OpenRouter ids are conventionally
        // lowercase ("openai/gpt-4o-mini"), but the catalogue compare is OIC
        // and a user could paste a mixed-case variant of an already-saved id —
        // we should treat them as the same entry, not silently store both.
        var existing = Models.FirstOrDefault(m =>
            string.Equals(m, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _notifications.ShowInfo(_translator.Format("msg_model_already_in_list", existing));
            SelectedModel = existing;
            return;
        }

        // Flush any pending debounced API-key write so a key the user just
        // typed becomes readable by GetApiKeyAsync. Without this, clicking "+"
        // within 400ms of typing the key surfaces api_unauthorized despite the
        // textbox showing a valid key.
        await PersistApiKeyAsync();

        var apiKey = await _credentials.GetApiKeyAsync();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _notifications.ShowError(_translator["api_unauthorized"]);
            return;
        }

        // Validate against OpenRouter's catalogue before adding to the
        // user's pinned list — saves the surprise of the model being
        // rejected later at hotkey-time when there's nothing the user
        // can do about it without re-opening Settings.
        try
        {
            var catalogue = await _openRouter.GetModelsAsync(apiKey);
            if (!catalogue.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                _notifications.ShowError(_translator.Format("msg_model_not_found", id));
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to validate model {Model} against OpenRouter catalogue", id);
            _notifications.ShowError(_translator.Format("msg_model_validate_failed", ex.Message));
            return;
        }

        Models.Add(id);
        SelectedModel = id;
        _notifications.ShowInfo(_translator.Format("msg_model_added", id));
        await PersistConfigAsync();
    }

    /// <summary>
    /// Pulls the user's locally-installed Ollama model tags via
    /// <c>GET /api/tags</c> and surfaces them in
    /// <see cref="OllamaModels"/>.  Distinct from
    /// <see cref="AddModelAsync"/>: OpenRouter has a 300-entry catalogue
    /// where users hand-pick one to add, but Ollama tags are whatever
    /// the user has <c>ollama pull</c>-ed — a one-shot list of usually
    /// 1-5 entries.  Shows toasts on success ("found N models") /
    /// failure (localised endpoint-unreachable hint) instead of silent
    /// no-ops so the user can tell when their Ollama server isn't
    /// running yet.
    /// </summary>
    [RelayCommand]
    private async Task RefreshOllamaModelsAsync()
    {
        // M6 fix: a slow refresh that the user toggles AWAY from
        // (Ollama → OpenRouter) mid-flight would still post a success
        // toast over the OpenRouter screen.  Cancel a prior CTS on
        // entry, then bind the new one so a subsequent Provider toggle
        // can cancel us cleanly.
        var oldCts = Interlocked.Exchange(ref _ollamaRefreshCts, new CancellationTokenSource());
        if (oldCts is not null)
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
        }

        var localCts = _ollamaRefreshCts!;

        IsRefreshingOllamaModels = true;
        try
        {
            var provider = _providers.Resolve(LlmProviderKind.Ollama);
            var tags = await provider.GetModelsAsync(apiKey: string.Empty, localCts.Token);

            ReplaceCollectionInPlace(OllamaModels, tags);

            // If the user's previously-selected tag is no longer present
            // (e.g. they ran `ollama rm`), fall back to the first tag in
            // the list — better than letting them try to run a model
            // that's been deleted.  Empty list leaves SelectedOllamaModel
            // empty too, which TextProcessor surfaces as
            // msg_ollama_model_not_configured at hotkey time.
            if (!string.IsNullOrEmpty(SelectedOllamaModel)
                && !OllamaModels.Contains(SelectedOllamaModel))
            {
                SelectedOllamaModel = OllamaModels.FirstOrDefault() ?? string.Empty;
            }
            else if (string.IsNullOrEmpty(SelectedOllamaModel) && OllamaModels.Count > 0)
            {
                SelectedOllamaModel = OllamaModels[0];
            }

            // M6: re-check cancellation after the await — Provider may
            // have toggled while we were waiting on /api/tags.  Skip
            // the toast and persist in that case so the user doesn't
            // see "Found N local models" overlaying their OpenRouter
            // switch.
            if (localCts.IsCancellationRequested)
            {
                return;
            }

            // Successful /api/tags response also implicitly confirms
            // Ollama-reachability for the startup-probe-driven
            // Provider-card visibility flag.
            IsOllamaAvailable = true;

            await PersistConfigAsync();

            _notifications.ShowInfo(
                _translator.Format("msg_ollama_models_refreshed", OllamaModels.Count));
        }
        catch (OperationCanceledException)
        {
            // Provider toggle (or rapid re-click) superseded this run.
            // Silent — the new run will post its own success/failure
            // toast.
        }
        catch (OpenRouterException ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Ollama models from {Endpoint}", OllamaEndpoint);
            // v15: route the ollama_unreachable case through the
            // centralized handler so refresh-while-Ollama-down also
            // performs the auto-revert + combined toast (matches the
            // user request that every "Couldn't reach Ollama"
            // surface triggers a switch back to OpenRouter).
            if (ex.LocalizationKey == "ollama_unreachable")
            {
                IsOllamaAvailable = false;
                _ = HandleOllamaUnreachableAsync();
            }
            else
            {
                _notifications.ShowError(ex.Message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unexpected error refreshing Ollama models");
            _notifications.ShowError(_translator["api_unknown_error"]);
        }
        finally
        {
            IsRefreshingOllamaModels = false;
        }
    }

    [RelayCommand]
    private async Task RefreshBalanceAsync()
    {
        // Caller (auto-fetch on flag toggle / button click) is the gate;
        // we still defend against being invoked when the flag is off so
        // accidental calls don't surprise-hit OpenRouter.
        if (!ExperimentalCostsAndCredits)
        {
            SetBalanceEmpty();
            return;
        }

        IsRefreshingBalance = true;
        try
        {
            SetBalanceFromKey("balance_loading");

            var apiKey = await _credentials.GetApiKeyAsync();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetBalanceFromKey("balance_no_api_key");
                return;
            }

            var info = await _openRouter.GetCreditsAsync(apiKey);
            SetBalanceFromFormat(
                "balance_format",
                FormatUsd(info.Remaining),
                FormatUsd(info.TotalCredits));
        }
        catch (OpenRouterException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch credit balance");
            SetBalanceRaw(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unexpected error fetching credit balance");
            SetBalanceFromKey("balance_unavailable");
        }
        finally
        {
            // try/finally guarantees the icon flips back to the
            // static refresh glyph regardless of which catch arm
            // ran — without this a transient OpenRouter outage
            // would leave the button stuck spinning forever.
            IsRefreshingBalance = false;
        }
    }

    private static string FormatUsd(decimal usd) =>
        "$" + usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // Z7-F3 / M19 — keyed setter that records the source so a later
    // language switch can re-resolve via OnTranslatorPropertyChanged.
    // Eagerly resolves to the current locale so any caller reading
    // BalanceDisplay immediately sees the expected text.
    private void SetBalanceFromKey(string key)
    {
        _balanceSource = (key, null);
        BalanceDisplay = _translator[key];
    }

    private void SetBalanceFromFormat(string key, params object[] args)
    {
        _balanceSource = (key, args);
        BalanceDisplay = _translator.Format(key, args);
    }

    // Raw-text setter for messages that don't map to a Translator key
    // (e.g. OpenRouterException.Message which is already built from the
    // HTTP status + server body).  Language switches leave these alone.
    private void SetBalanceRaw(string raw)
    {
        _balanceSource = null;
        BalanceDisplay = raw;
    }

    private void SetBalanceEmpty()
    {
        _balanceSource = null;
        BalanceDisplay = string.Empty;
    }

    private void OnTranslatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // WPF binds Translator's Item[] indexer when XAML uses Path=[key];
        // the indexer-change signal is "Item[]".  Language is a regular
        // property change.  Empty / null is the "all properties may have
        // changed" wildcard.  Match all three so a future Translator
        // refactor that batches updates differently still triggers us.
        if (!string.IsNullOrEmpty(e.PropertyName)
            && e.PropertyName != "Item[]"
            && e.PropertyName != nameof(ITranslator.Language))
        {
            return;
        }

        if (_balanceSource is { } src)
        {
            BalanceDisplay = src.Args is null
                ? _translator[src.Key]
                : _translator.Format(src.Key, src.Args);
        }
    }

    [RelayCommand]
    private async Task BrowseModelsAsync()
    {
        IsBrowsingModels = true;
        try
        {
            var picked = await _modelBrowser.BrowseAsync();
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            if (!Models.Contains(picked))
            {
                Models.Add(picked);
            }

            SelectedModel = picked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "BrowseModelsAsync failed");
        }
        finally
        {
            // Same try/finally guarantee as RefreshBalanceAsync —
            // even a thrown OpenRouterException must clear the
            // busy flag, otherwise the cloud-icon button stays
            // stuck on the spinning loader.
            IsBrowsingModels = false;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedModelAsync()
    {
        if (string.IsNullOrEmpty(SelectedModel))
        {
            return;
        }

        var index = Models.IndexOf(SelectedModel);
        if (index < 0)
        {
            return;
        }

        Models.RemoveAt(index);

        // §6.2: ComboBox.SelectedItem after ItemsSource change isn't always reliable — set explicitly.
        SelectedModel = Models.Count > 0 ? Models[Math.Min(index, Models.Count - 1)] : string.Empty;
        await PersistConfigAsync();
    }

    partial void OnLanguageChanged(Language value)
    {
        _translator.SetLanguage(value);
        _ = PersistConfigAsync();
    }

    partial void OnApiKeyChanged(string value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _pendingApiKey = value;

        // Cancel any in-flight validation probe — the new key value
        // supersedes it.  Reset the indicator to None so the user
        // sees the previous Valid/Invalid badge clear immediately
        // while they continue typing, rather than stale state
        // hanging on for the 400-ms debounce window.
        var oldCts = Interlocked.Exchange(ref _validationCts, null);
        oldCts?.Cancel();
        oldCts?.Dispose();
        ApiKeyState = ApiKeyValidationState.None;

        _apiKeyDebounceTimer.Change(ApiKeyDebounceDelay, Timeout.InfiniteTimeSpan);
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (_suppressPersist)
        {
            return;
        }

        // No auto-add to Models here — that step is the user's explicit
        // "+" action which goes through OpenRouter validation. SelectedModel
        // can hold a typed-but-not-in-list id so the active inference
        // request still routes correctly; the "saved" Models list is the
        // user's pinned subset.
        _ = PersistConfigAsync();
    }

    partial void OnProviderChanged(LlmProviderKind value)
    {
        if (_suppressPersist)
        {
            return;
        }

        // M6: cancel any in-flight Ollama refresh so its post-await
        // continuation doesn't fire a stale "Found N models" toast over
        // the now-OpenRouter UI (or write a SelectedOllamaModel that
        // the user no longer cares about).  Cancel() is synchronous;
        // the synchronous-block analyser flags it but OnProviderChanged
        // is itself a synchronous partial — we can't await CancelAsync
        // here without restructuring the change-notify pipeline, and
        // the synchronous variant returns instantly when the token has
        // no expensive registered callbacks (it doesn't — Dispose
        // below releases the timer-style resources separately).
        var inflight = Interlocked.Exchange(ref _ollamaRefreshCts, null);
#pragma warning disable CA1849, VSTHRD103 // OnProviderChanged is sync
        inflight?.Cancel();
#pragma warning restore CA1849, VSTHRD103
        inflight?.Dispose();

        // When the user turns Ollama ON, probe the local endpoint
        // before accepting the toggle.  If `ollama serve` isn't
        // running (or the user mistyped the endpoint), revert to
        // OpenRouter and surface the localised "ollama_unreachable"
        // toast — the user shouldn't be allowed to enter a state
        // where every subsequent hotkey would fail at request time
        // with the same toast.  When turning Ollama OFF (back to
        // OpenRouter), no probe is needed — that path is always
        // safe.
        if (value == LlmProviderKind.Ollama)
        {
            _ = ProbeOllamaThenPersistAsync();
        }
        else
        {
            _ = PersistConfigAsync();
        }
    }

    /// <summary>
    /// Fast health check on toggle-to-Ollama: hits <c>/api/tags</c>
    /// once.  Connection-refused (the canonical "you forgot to start
    /// ollama serve" failure) is immediate; even a bad-host endpoint
    /// resolves in under a second on local LAN.  On success persists
    /// the new Provider state; on failure reverts UseOllama →
    /// unchecked under <c>_suppressPersist</c> so the revert doesn't
    /// loop back through OnProviderChanged.
    /// </summary>
    private async Task ProbeOllamaThenPersistAsync()
    {
        IsCheckingOllamaConnection = true;
        try
        {
            var probe = _providers.Resolve(LlmProviderKind.Ollama);
            // GetModelsAsync surfaces the same OpenRouterException
            // shape (ollama_unreachable / api_server_error /
            // api_unknown_error) the rest of the pipeline already
            // localises — no need for a dedicated probe message.
            await probe.GetModelsAsync(apiKey: string.Empty);

            // Reachable — keep the user's toggle and persist.  Also
            // flip IsOllamaAvailable so the card stays visible past
            // the moment of toggle (the startup probe might have
            // raced ahead with a stale "not running" answer).
            IsOllamaAvailable = true;
            await PersistConfigAsync();
        }
        catch (OpenRouterException ex)
        {
            _logger.LogInformation(
                "Ollama probe failed on toggle ({Endpoint}): {Message}",
                OllamaEndpoint,
                ex.Message);
            _ = HandleOllamaUnreachableAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unexpected error probing Ollama on toggle");
            _ = HandleOllamaUnreachableAsync();
        }
        finally
        {
            IsCheckingOllamaConnection = false;
        }
    }

    private void RevertProviderToOpenRouter()
    {
        // Suppress persist so the revert doesn't re-enter
        // ProbeOllamaThenPersistAsync (and doesn't write the failed
        // toggle to disk through the OnProviderChanged probe path).
        // We rely on the NotifyPropertyChangedFor chain on _provider
        // to push the new state through to the CheckBox binding,
        // IsOpenRouterProvider / IsOllamaProvider / UseOllama /
        // IsBalanceRowVisible all refreshing in one pass.
        _suppressPersist = true;
        try
        {
            Provider = LlmProviderKind.OpenRouter;
        }
        finally
        {
            _suppressPersist = false;
        }

        // Persist the revert explicitly (outside the suppress block,
        // so PersistConfigAsync's `if (_suppressPersist) return` gate
        // doesn't skip the write).  Without this, an in-memory
        // revert from the periodic poll / startup probe never reaches
        // disk — next launch would re-read Provider=Ollama, fail the
        // probe, revert again, show the same toast.  Persisting the
        // revert makes the auto-switch sticky: next launch starts
        // clean on OpenRouter, and the user can re-toggle to Ollama
        // when they bring their local server back up.
        _ = PersistConfigAsync();
    }

    partial void OnOllamaEndpointChanged(string value)
    {
        if (_suppressPersist)
        {
            return;
        }

        // No debounce: endpoint changes are infrequent and the next
        // request will pick up the new value via OllamaClient's per-
        // call IConfigStore.LoadAsync.  Persist directly so a quit
        // immediately after editing doesn't lose the change.
        _ = PersistConfigAsync();
    }

    partial void OnSelectedOllamaModelChanged(string value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnHotkeyChanged(string value)
    {
        HandleHotkeyChange(
            slotName: nameof(Hotkey),
            newValue: value,
            otherValues: [MenuHotkey, UndoHotkey],
            revert: previous =>
            {
                _suppressPersist = true;
                try { Hotkey = previous; }
                finally { _suppressPersist = false; }
            },
            commit: () => _lastAppliedHotkey = value);
    }

    partial void OnMenuHotkeyChanged(string value)
    {
        HandleHotkeyChange(
            slotName: nameof(MenuHotkey),
            newValue: value,
            otherValues: [Hotkey, UndoHotkey],
            revert: previous =>
            {
                _suppressPersist = true;
                try { MenuHotkey = previous; }
                finally { _suppressPersist = false; }
            },
            commit: () => _lastAppliedMenuHotkey = value);
    }

    partial void OnUndoHotkeyChanged(string value)
    {
        HandleHotkeyChange(
            slotName: nameof(UndoHotkey),
            newValue: value,
            otherValues: [Hotkey, MenuHotkey],
            revert: previous =>
            {
                _suppressPersist = true;
                try { UndoHotkey = previous; }
                finally { _suppressPersist = false; }
            },
            commit: () => _lastAppliedUndoHotkey = value);
    }

    /// <summary>
    /// Common path for the three hotkey-slot setters. Shared so the parse
    /// gate, conflict detection, and persist/reregister wiring can't drift
    /// between Hotkey / MenuHotkey / UndoHotkey.
    /// </summary>
    private void HandleHotkeyChange(
        string slotName,
        string newValue,
        IReadOnlyList<string> otherValues,
        Action<string> revert,
        Action commit)
    {
        if (_suppressPersist)
        {
            return;
        }

        // FZ2-F3 / M33 — clear any prior inline conflict message; the
        // user just typed something new so the previous explanation is
        // stale.  If the new value ALSO conflicts (or is unparseable)
        // the message is re-set below.  Doing this at the top means the
        // glyph disappears the moment the user starts editing — visible
        // feedback that the conflict state is being re-evaluated.
        SetHotkeyConflictMessage(slotName, string.Empty);

        // Don't persist garbage — if the user typed something we can't parse,
        // log a warning, surface an inline explanation, and revert the slot
        // back to the last-committed value.  Z2-F7 / M6: pre-fix this branch
        // returned without reverting, leaving the unparseable string visible
        // in the ComboBox AND `_lastApplied*Hotkey` stale relative to it.  A
        // subsequent conflict on a different value would then revert "past"
        // the typo to whatever was committed BEFORE it — UI inconsistency
        // around the snapshot machinery.  Mirroring the conflict-path revert
        // keeps `_lastApplied*Hotkey` always equal to what the user sees.
        if (!string.IsNullOrWhiteSpace(newValue) && HotkeyAccelerator.Parse(newValue) is null)
        {
            _logger.LogWarning("Rejecting unparseable {Slot} '{Hotkey}'", slotName, newValue);
            SetHotkeyConflictMessage(
                slotName,
                _translator.Format("tooltip_hotkey_unparseable", newValue));
            revert(SnapshotForSlot(slotName));
            return;
        }

        // Conflict guard: if the new combo matches another slot's currently
        // set value, refuse the change. Reverting via the supplied callback
        // restores the previously-applied value without re-entering this
        // handler (it sets _suppressPersist) so we don't loop or double-save.
        if (!string.IsNullOrWhiteSpace(newValue)
            && IsHotkeyConflict(newValue, otherValues))
        {
            _logger.LogInformation(
                "Rejecting {Slot}='{Hotkey}' — collides with another configured hotkey",
                slotName,
                newValue);
            _notifications.ShowError(_translator.Format("hotkey_conflict", newValue));

            // FZ2-F3 / M33 — surface the conflict inline alongside the
            // (reverted) ComboBox so the explanation outlives the
            // 3.5 s toast.  The {0} is the value the user attempted —
            // they recognise it even though the field reverted.
            SetHotkeyConflictMessage(
                slotName,
                _translator.Format("tooltip_hotkey_conflict", newValue));

            // Revert UI to the last consistent value so the ComboBox text
            // matches what's actually persisted + registered.
            revert(SnapshotForSlot(slotName));
            return;
        }

        commit();
        _ = PersistConfigAsync();
        ReregisterHotkeys();
    }

    private void SetHotkeyConflictMessage(string slotName, string message)
    {
        switch (slotName)
        {
            case nameof(Hotkey):
                HotkeyConflictMessage = message;
                break;
            case nameof(MenuHotkey):
                MenuHotkeyConflictMessage = message;
                break;
            case nameof(UndoHotkey):
                UndoHotkeyConflictMessage = message;
                break;
            default:
                // Unknown slot — nothing to surface.  This branch is
                // structurally unreachable (HandleHotkeyChange callers
                // pass nameof(Hotkey)/nameof(MenuHotkey)/nameof(UndoHotkey)
                // literals) but kept to satisfy IDE0010 exhaustive-switch.
                break;
        }
    }

    private string SnapshotForSlot(string slotName) => slotName switch
    {
        nameof(Hotkey) => _lastAppliedHotkey,
        nameof(MenuHotkey) => _lastAppliedMenuHotkey,
        nameof(UndoHotkey) => _lastAppliedUndoHotkey,
        _ => string.Empty,
    };

    /// <summary>
    /// True if <paramref name="candidate"/> parses to the same Win32 modifier
    /// + virtual-key combination as any of <paramref name="others"/>. Empty /
    /// unparseable entries are skipped — they can't conflict with anything
    /// real because they won't be registered with RegisterHotKey.
    /// </summary>
    private static bool IsHotkeyConflict(string candidate, IReadOnlyList<string> others)
    {
        var parsed = HotkeyAccelerator.Parse(candidate);
        if (parsed is null)
        {
            return false;
        }

        foreach (var other in others)
        {
            if (string.IsNullOrWhiteSpace(other))
            {
                continue;
            }

            var otherParsed = HotkeyAccelerator.Parse(other);
            if (otherParsed is not null && otherParsed == parsed)
            {
                return true;
            }
        }

        return false;
    }

    partial void OnExperimentalDiffPreviewChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnExperimentalStreamingChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnExperimentalPerPromptModelChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnExperimentalCostsAndCreditsChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();

        // Auto-fetch the balance the first time the user enables the
        // feature so they don't have to hunt for the refresh button. When
        // turned off, clear any stale display so re-enabling shows "..."
        // until the next fetch resolves (avoids flashing an old value).
        if (value)
        {
            _ = RefreshBalanceAsync();
        }
        else
        {
            SetBalanceEmpty();
        }
    }

    partial void OnExperimentalPrivacyRedactionChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnExperimentalHistoryChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnExperimentalKeepResultSelectedChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnTimeoutSecondsChanged(int value)
    {
        if (_suppressPersist)
        {
            return;
        }

        // Z2-F8 / L4: PersistConfigCoreAsync applies the same
        // `<= 0 → default` clamp at write time as AppConfig.cs:307,
        // so a user who types 0 or a negative value here gets the
        // 30s default persisted rather than an invalid Timeout that
        // would fail TextProcessor's CancelAfter contract.
        _ = PersistConfigAsync();
    }

    partial void OnOllamaTimeoutSecondsChanged(int value)
    {
        if (_suppressPersist)
        {
            return;
        }

        _ = PersistConfigAsync();
    }

    partial void OnDeveloperModeEnabledChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        // When the user re-locks dev mode, clear the beta-only flag
        // values too so a future unlock starts from defaults.  Without
        // this, a user could enable a beta feature, lock dev mode,
        // and still have the beta flag silently active in TextProcessor
        // (gating happens on the experiment flag, not on
        // DeveloperModeEnabled).  Clearing on re-lock keeps the user
        // in control: if they don't see the checkbox, the feature
        // isn't running.
        if (!value && ExperimentalKeepResultSelected)
        {
            ExperimentalKeepResultSelected = false;
        }

        _ = PersistConfigAsync();
    }

    partial void OnAutostartEnabledChanged(bool value)
    {
        if (_suppressPersist)
        {
            return;
        }

        // FZ5-F4 / L33: the registry write IS synchronous —
        // `_autostart.Enable()` calls `Microsoft.Win32.Registry.SetValue`
        // which returns only after the value has been committed to the
        // hive.  A live observer (PowerShell + Get-ItemProperty) that
        // saw a "2-second lag" between checkbox flip and the registry
        // read seeing the new value was tripped by PowerShell's own
        // cached view of the hive, NOT an app-level race — the in-
        // process write completed before this method returned.  The
        // OnExit flush plumbed by Z10 C9 covers the (separately
        // concerning) "rapid right-click-Quit" case where the next
        // dispatcher tick disposes the host before any debounced write
        // could land; autostart isn't subject to that flow because the
        // write is inline, not debounced.
        try
        {
            if (value)
            {
                _autostart.Enable();
            }
            else
            {
                _autostart.Disable();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to toggle autostart");

            // §6.16: surface the failure with a localized warning, then revert the checkbox.
            MessageBox.Show(
                _translator.Format("msg_autostart_fail", ex.Message),
                _translator["toast_error"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            _suppressPersist = true;
            try
            {
                AutostartEnabled = !value;
            }
            finally
            {
                _suppressPersist = false;
            }
        }
    }

    private void ReregisterHotkeys()
    {
        if (_suppressPersist)
        {
            return;
        }

        _hotkeys.UnregisterAll();
        TryRegisterHotkeyOrToast(HotkeyKind.Default, Hotkey, "hotkey");
        TryRegisterHotkeyOrToast(HotkeyKind.Menu, MenuHotkey, "menu_hotkey");
        TryRegisterHotkeyOrToast(HotkeyKind.Undo, UndoHotkey, "history_undo_hotkey");
    }

    /// <summary>
    /// Z4-F1 / C7 fix: surface a localized error toast when the Win32
    /// <c>RegisterHotKey</c> call returns false (chord owned by another
    /// app) or the in-manager duplicate-check rejects an intra-app
    /// collision. Pre-fix every caller discarded <see cref="IHotkeyManager.TryRegister"/>'s
    /// bool return, so a failure was visible only in the log file —
    /// the user's hotkey silently didn't fire.
    /// </summary>
    private void TryRegisterHotkeyOrToast(HotkeyKind kind, string accelerator, string labelKey)
    {
        if (string.IsNullOrWhiteSpace(accelerator))
        {
            return;
        }

        var ok = _hotkeys.TryRegister(kind, accelerator);
        if (ok)
        {
            return;
        }

        try
        {
            var slotName = _translator[labelKey];
            _notifications.ShowError(
                _translator.Format("hotkey_register_failed", accelerator, slotName));
        }
        catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogDebug(toastEx, "Failed to surface hotkey-register error toast");
        }
    }

    private void OnApiKeyDebounceTick(object? state)
    {
        // The Timer callback runs on a threadpool thread; marshal to
        // the UI thread before kicking off the persist + validate
        // flow so every ApiKeyState mutation along the way is on the
        // dispatcher (avoids cross-thread INPC weirdness in the bound
        // status indicator).  Application.Current is null in design-
        // time loaders / unit tests where no Application is hosted —
        // fall back to the bare fire-and-forget so the behaviour is
        // unchanged in those contexts.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(PersistAndValidateAsync);
            return;
        }

        _ = PersistAndValidateAsync();
    }

    /// <summary>
    /// Persists the pending API key (existing behaviour) and then,
    /// if the key is non-empty, fires a /credits probe to validate
    /// it against OpenRouter.  Wrapped in a single async method so
    /// the two phases share their cancellation lifetime — if a new
    /// keystroke lands while validation is in flight, we cancel
    /// the probe immediately rather than letting it complete and
    /// flip ApiKeyState back to Valid over a freshly-edited key.
    /// </summary>
    private async Task PersistAndValidateAsync()
    {
        await PersistApiKeyAsync();
        await ValidateApiKeyAsync();
    }

    /// <summary>
    /// Probes /credits with the just-persisted key and updates
    /// <see cref="ApiKeyState"/> based on the outcome.  Renews the
    /// validation CTS so a new keystroke during this call cancels
    /// us cleanly.  Empty key short-circuits to None — no need to
    /// hit the network for an empty string.
    /// </summary>
    private async Task ValidateApiKeyAsync()
    {
        var key = (_pendingApiKey ?? ApiKey)?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            ApiKeyState = ApiKeyValidationState.None;
            return;
        }

        // Renew the CTS atomically: swap in the new one, cancel the
        // old one (if any) AFTER the swap so a concurrent
        // OnApiKeyChanged that observes the field sees the new CTS
        // and cancels it on the next keystroke rather than the
        // already-cancelled one.  CancelAsync over Cancel for the
        // analyzer rule explained at the LoadFromConfigAsync call
        // site.
        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _validationCts, cts);
        if (oldCts is not null)
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
        }

        ApiKeyState = ApiKeyValidationState.Checking;

        try
        {
            // GetCreditsAsync requires authentication and returns
            // 401 for bad keys — exactly the shape we need for a
            // validity probe.  We deliberately ignore the returned
            // CreditsInfo here: this is a "is the key authorized"
            // check, not a balance fetch.  The dedicated
            // RefreshBalanceCommand still owns the balance row.
            await _openRouter.GetCreditsAsync(key, cts.Token);

            // Re-check cancellation: if a newer keystroke fired
            // while the network call was in flight, cts is now
            // cancelled and we shouldn't overwrite the state with
            // a stale Valid result.
            if (!cts.Token.IsCancellationRequested)
            {
                ApiKeyState = ApiKeyValidationState.Valid;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke; do nothing —
            // whichever new validation kicks off next will set the
            // correct state.
        }
        catch (OpenRouterException ex)
        {
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            // 401 is THE signal that the key itself is the problem
            // — surface that distinctly so the user knows to re-
            // paste the key rather than blame their network.
            // BuildRequest also throws OpenRouterException with no
            // StatusCode for keys that have CR/LF/empty after Trim
            // — those are structurally unusable so we treat them
            // as Invalid too (the message matches api_unauthorized
            // already, but the StatusCode null check would push
            // them to NetworkError; explicit fallthrough keeps
            // them in the Invalid bucket where they belong).
            ApiKeyState = ex.StatusCode == HttpStatusCode.Unauthorized
                ? ApiKeyValidationState.Invalid
                : ApiKeyValidationState.NetworkError;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            _logger.LogWarning(ex, "API key validation probe failed unexpectedly");
            ApiKeyState = ApiKeyValidationState.NetworkError;
        }
    }

    private async Task PersistApiKeyAsync(CancellationToken ct = default)
    {
        var key = _pendingApiKey ?? ApiKey;
        try
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                await _credentials.DeleteApiKeyAsync(ct);
            }
            else
            {
                await _credentials.SetApiKeyAsync(key, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist API key");

            // Z2-F1 / C4 fix: surface the persistence failure to the user.
            // Pre-fix the catch swallowed the exception and the validation
            // probe (using the in-memory key) painted the indicator green
            // for a key that never reached Credential Manager.  Next launch
            // `_credentials.GetApiKeyAsync()` returned null and inference
            // failed with `api_unauthorized` from cold start — the user had
            // no diagnostic trail. Also flip the state so the indicator
            // doesn't lie about validity.
            ApiKeyState = ApiKeyValidationState.NetworkError;
            try
            {
                _notifications.ShowError(_translator["msg_api_key_persist_failed"]);
            }
            catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogDebug(toastEx, "Failed to surface API-key persist error toast");
            }
        }
    }

    private static void ReplaceCollectionInPlace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private Task PersistConfigAsync()
    {
        if (_suppressPersist)
        {
            return Task.CompletedTask;
        }

        // Z10-F1 fix: record the task handle so FlushPendingConfigAsync
        // can await it from OnExit. Note that fire-and-forget call sites
        // still don't await this, but at least the work is observable.
        var task = PersistConfigCoreAsync();
        lock (_persistTaskLock)
        {
            _lastPersistTask = task;
        }

        return task;
    }

    private async Task PersistConfigCoreAsync()
    {
        try
        {
            var current = await _configStore.LoadAsync();
            var updated = current with
            {
                Language = Language,
                Provider = Provider,
                OllamaEndpoint = OllamaEndpoint,
                OllamaModel = SelectedOllamaModel,
                OllamaModels = [.. OllamaModels],
                Model = SelectedModel,
                Models = [.. Models],
                Hotkey = Hotkey,
                MenuHotkey = MenuHotkey,
                UndoHotkey = UndoHotkey,
                ExperimentalDiffPreview = ExperimentalDiffPreview,
                ExperimentalStreaming = ExperimentalStreaming,
                ExperimentalPerPromptModel = ExperimentalPerPromptModel,
                ExperimentalCostsAndCredits = ExperimentalCostsAndCredits,
                ExperimentalPrivacyRedaction = ExperimentalPrivacyRedaction,
                ExperimentalHistory = ExperimentalHistory,
                ExperimentalKeepResultSelected = ExperimentalKeepResultSelected,
                DeveloperModeEnabled = DeveloperModeEnabled,
                // Z2-F8 / L4: clamp on persist mirroring the
                // WithDefaultsApplied guard at AppConfig.cs:324.
                // v14: `0` is a SUPPORTED sentinel meaning "no timeout"
                // (wait forever) — TextProcessor translates it to
                // Timeout.InfiniteTimeSpan before handing to
                // OpenRouterClient.  Only negative values are nonsense
                // and fall back to the Default.
                Timeout = TimeoutSeconds < 0 ? AppConfig.Default.Timeout : TimeoutSeconds,
                OllamaTimeout = OllamaTimeoutSeconds < 0
                    ? AppConfig.Default.OllamaTimeout
                    : OllamaTimeoutSeconds,
            };
            await _configStore.SaveAsync(updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Z2-F2 fix: surface persist failure to the user instead of
            // silently dropping every Hotkey / Experimental / language
            // toggle. Pre-fix the user changed a setting, the file write
            // failed (disk full, AV lock, permission denied), the UI kept
            // the in-memory value, and next launch found the old config.
            _logger.LogWarning(ex, "Failed to persist config from settings");
            try
            {
                _notifications.ShowError(_translator["msg_save_settings_failed"]);
            }
            catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogDebug(toastEx, "Failed to surface settings-save error toast");
            }
        }
    }
}
