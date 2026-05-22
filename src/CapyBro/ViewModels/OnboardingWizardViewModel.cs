using System.ComponentModel;
using System.Net;

using CapyBro.Models;
using CapyBro.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

namespace CapyBro.ViewModels;

/// <summary>
/// Carries the new language pick from the wizard to the host window so
/// the live-preview can re-render every translator-bound string.
/// CA1003 mandates a real EventArgs subclass on the event's signature
/// (a bare <c>EventHandler&lt;Language&gt;</c> trips the analyzer).
/// </summary>
public sealed class LanguagePreviewEventArgs : EventArgs
{
    public LanguagePreviewEventArgs(Language language)
    {
        Language = language;
    }

    public Language Language { get; }
}

/// <summary>
/// Drives the first-launch onboarding wizard. The flow is a 4-page walk:
/// Welcome+Language → API key → Hotkeys (×3) → Done.
/// All values stay in this VM and are committed to the
/// <see cref="IConfigStore"/> + <see cref="ICredentialStore"/> only when
/// the user clicks Finish — Skip and window-close also commit, but only
/// the <c>OnboardingCompleted</c> flag, leaving the rest of the config at
/// its <see cref="AppConfig.Default"/> values so the user can fill them
/// in later via the Settings window.
/// </summary>
/// <remarks>
/// Every translator string the wizard XAML needs is exposed as a VM
/// property here, instead of bound directly to <c>Translator.Instance</c>
/// via <c>{Binding Source={x:Static}}</c>. Both patterns work in
/// principle, but the indirect path through the VM makes the language
/// live-preview deterministic — the VM subscribes to
/// <see cref="ITranslator.PropertyChanged"/> and replays the change as a
/// catch-all <c>PropertyChanged("")</c> on itself, forcing every wizard
/// binding to re-resolve in lockstep.  Without this, the previous
/// release showed a mixed-language rendering after a mid-wizard language
/// switch (some indexer bindings re-evaluated, some did not — depended
/// on the binding evaluation pass at the moment of the notification).
/// </remarks>
public sealed partial class OnboardingWizardViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Total number of wizard pages.  Layout post-restructure:
    ///   0 Welcome+Language  (heading/body + language ComboBox)
    ///   1 API key
    ///   2 Hotkeys (improvement / menu / undo, with conflict detection)
    ///   3 Done
    /// Pre-restructure this was 5 (separate Welcome and Language
    /// pages); merging them puts language selection on the very first
    /// step so a user whose system speaks a language other than English
    /// can switch the UI before reading any of the welcome copy.
    /// </summary>
    public const int TotalSteps = 4;

    /// <summary>
    /// Delay between the user's last keystroke in the API-key field
    /// and the /credits validation probe.  Same 400 ms cadence as
    /// <see cref="GeneralTabViewModel"/> so the wizard and Settings
    /// surface feel identical when the user is typing.  Implemented
    /// via Task.Delay+CTS rather than a System.Threading.Timer
    /// because the wizard is short-lived (no Dispose-coupled timer
    /// to manage) and we already need a CTS for cancellation.
    /// </summary>
    private static readonly TimeSpan ApiKeyDebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly IConfigStore _configStore;
    private readonly ICredentialStore _credentials;
    private readonly ITranslator _translator;
    private readonly IHotkeyManager _hotkeys;
    private readonly IOpenRouterClient _openRouter;
    private readonly INotificationService? _notifications;
    private readonly ILogger<OnboardingWizardViewModel> _logger;

    // Cancellation source for the in-flight /credits probe.  Same
    // pattern as GeneralTabViewModel — a new keystroke cancels the
    // previous probe so a slow round-trip doesn't flip ApiKeyState
    // back to Valid over a freshly-edited key.  Renewed on every
    // OnApiKeyChanged that schedules a probe.
    private CancellationTokenSource? _validationCts;

    [ObservableProperty]
    private int _currentStep;

    // Brand decision: default to English on the wizard's Language step.
    // Overwritten in the constructor by `_language = _translator.Language`,
    // which itself defaults to English (Translator's static init seeds
    // the singleton to English, and App.OnStartup never auto-detects to
    // a different locale post-rebrand).  This field initializer is the
    // safety net for unit-test harnesses that bypass the constructor's
    // translator coupling.
    [ObservableProperty]
    private Language _language = Language.English;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>
    /// Mirrors <see cref="GeneralTabViewModel.ApiKeyState"/>.  The
    /// wizard uses the same /credits probe to flip a status indicator
    /// under the API-key input on Step 1, so a user typing a key on
    /// the very first launch sees the same "Checking → Valid /
    /// Invalid" feedback they'll see in Settings later.
    ///
    /// XAML binds five computed bools off this enum
    /// (<see cref="IsApiKeyChecking"/> / Valid / Invalid /
    /// NetworkError / <see cref="ApiKeyStatusVisible"/>).
    /// <c>[NotifyPropertyChangedFor]</c> keeps them in sync without
    /// per-state OnPropertyChanged calls.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsApiKeyChecking))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyValid))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyInvalid))]
    [NotifyPropertyChangedFor(nameof(IsApiKeyNetworkError))]
    [NotifyPropertyChangedFor(nameof(ApiKeyStatusVisible))]
    private ApiKeyValidationState _apiKeyState = ApiKeyValidationState.None;

    // Three hotkeys mirror the Settings → General page so the user sees
    // the same trio in onboarding and afterwards.  Defaults match
    // AppConfig.Default — picking different combos here would create a
    // mismatch with the registration that App.OnStartup performed BEFORE
    // the wizard ran (using config defaults).
    [ObservableProperty]
    private string _hotkey = "Ctrl+Shift+E";

    [ObservableProperty]
    private string _menuHotkey = "Ctrl+Shift+Q";

    [ObservableProperty]
    private string _undoHotkey = "Ctrl+Shift+Z";

    /// <summary>
    /// Set to true after the wizard has applied its values (or recorded a
    /// skip). The window code-behind reads this in OnClosing to decide
    /// whether to persist a skip-on-close.
    /// </summary>
    public bool HasCompleted { get; private set; }

    /// <summary>
    /// True only when the wizard closed via <c>FinishCommand</c> — i.e.
    /// the user fully walked through the steps and accepted them.  Skip
    /// and bare-window-close ([×]) leave this false.  The host
    /// (App.xaml.cs) keys "open Settings after the wizard" off this flag
    /// so that Skip / [×] preserve their original "dismiss without
    /// engaging" semantic, while Done explicitly transitions the user
    /// into the running app surface.
    /// </summary>
    public bool WasFinished { get; private set; }

    public OnboardingWizardViewModel(
        IConfigStore configStore,
        ICredentialStore credentials,
        ITranslator translator,
        IHotkeyManager hotkeys,
        IOpenRouterClient openRouter,
        ILogger<OnboardingWizardViewModel> logger,
        INotificationService? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(translator);

        _configStore = configStore;
        _credentials = credentials;
        _translator = translator;
        _hotkeys = hotkeys;
        _openRouter = openRouter;
        _notifications = notifications;
        _logger = logger;

        // Pre-seed the chosen language from the live translator so the VM
        // and the translator agree at wizard-open time.  Without this, on
        // a system whose locale resolved to a non-default value, the
        // translator and the VM's _language field would disagree at
        // first paint.
        _language = _translator.Language;

        // Subscribe to language changes so EVERY translator-derived
        // property exposed below re-publishes.  We forward as a single
        // OnPropertyChanged(string.Empty) — WPF treats that as "every
        // property on this source has changed" and re-evaluates all
        // bindings rooted at the VM in one pass.
        if (_translator is INotifyPropertyChanged translatorNpc)
        {
            translatorNpc.PropertyChanged += OnTranslatorPropertyChanged;
        }

        AvailableLanguages =
        [
            new LanguageOption(Language.English, translator["lang_label_english"]),
            new LanguageOption(Language.Ukrainian, translator["lang_label_ukrainian"]),
            new LanguageOption(Language.Russian, translator["lang_label_russian"]),
        ];
    }

    /// <summary>
    /// M23 (Z8-F7) fix: pre-populate <see cref="ApiKey"/> from the
    /// credential store so a returning user (clean reinstall, prior
    /// key still present in Credential Manager) sees their existing
    /// key in the API-key step instead of an empty field that implies
    /// they have to re-paste it.  Pre-fix the wizard's first-run gate
    /// was purely config-file existence, so the credential surface
    /// stayed silent on the visual return-user case.
    ///
    /// Caller (App.ShowOnboardingWizard) must await this before
    /// surfacing the wizard window — otherwise the user can start
    /// typing into an empty field that the existing key then
    /// overwrites mid-edit.  Logged-and-swallowed on failure: the
    /// wizard remains usable on a fresh install where credential
    /// access throws.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var stored = await _credentials.GetApiKeyAsync(ct);
            if (!string.IsNullOrEmpty(stored))
            {
                ApiKey = stored;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   and not OutOfMemoryException
                                   and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Onboarding wizard: failed to pre-populate API key from credential store");
        }
    }

    /// <summary>
    /// Curated hotkey suggestions surfaced in each of the 3 ComboBoxes on
    /// the Hotkeys step.  Editable so power users can type anything the
    /// HotkeyAccelerator parser accepts — see HotkeyAcceleratorTests for
    /// the full grammar (modifiers + letters / digits / F1-F24 / OEM
    /// punctuation / named keys).  Pre-restructure the list was just three
    /// improvement-action variants; expanded so the user has at least one
    /// reasonable default for each of the three actions without typing.
    /// </summary>
    public IReadOnlyList<string> AvailableHotkeys { get; } =
    [
        "Ctrl+Shift+E",
        "Ctrl+Shift+I",
        "Ctrl+Alt+I",
        "Ctrl+Shift+Q",
        "Ctrl+Shift+M",
        "Ctrl+Alt+M",
        "Ctrl+Shift+Z",
        "Ctrl+Alt+Z",
        "Ctrl+Shift+U",
    ];

    // English-first ordering matches the brand decision (default
    // language is English) and the user's request.  Same list shape as
    // GeneralTabViewModel.AvailableLanguages.
    //
    // H23 (FZ4-F2) fix: items carry a DisplayName so the dropdown
    // shows autonyms.  Populated in the constructor (depends on the
    // injected translator).
    public IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    public bool IsWelcomeStep => CurrentStep == 0;

    public bool IsApiKeyStep => CurrentStep == 1;

    public bool IsHotkeyStep => CurrentStep == 2;

    public bool IsDoneStep => CurrentStep == 3;

    /// <summary>
    /// True when the inline API-key status indicator should render
    /// at all — false on an empty field so the slot stays quiet
    /// until the user has something to validate.
    /// </summary>
    public bool ApiKeyStatusVisible => ApiKeyState != ApiKeyValidationState.None;

    /// <summary>True while the /credits probe is in flight.</summary>
    public bool IsApiKeyChecking => ApiKeyState == ApiKeyValidationState.Checking;

    /// <summary>True after a successful /credits probe.</summary>
    public bool IsApiKeyValid => ApiKeyState == ApiKeyValidationState.Valid;

    /// <summary>True when the probe came back 401 / shape-rejected.</summary>
    public bool IsApiKeyInvalid => ApiKeyState == ApiKeyValidationState.Invalid;

    /// <summary>True when probe failed for non-key reasons (timeout, 5xx, DNS).</summary>
    public bool IsApiKeyNetworkError => ApiKeyState == ApiKeyValidationState.NetworkError;

    /// <summary>
    /// True when at least two of the three hotkey ComboBoxes resolve to
    /// the same accelerator (case-insensitive after normalisation).
    /// Bound to a red error TextBlock on the Hotkeys step and used to
    /// gate <see cref="CanGoNext"/> from advancing past the step — same
    /// pattern as Settings → General's hotkey conflict guard, just
    /// inline-validated rather than via dialog.
    /// </summary>
    public bool HasHotkeyConflict
    {
        get
        {
            var a = NormaliseForCompare(Hotkey);
            var b = NormaliseForCompare(MenuHotkey);
            var c = NormaliseForCompare(UndoHotkey);

            // Empty hotkeys are not "conflicts" — the persist path stays
            // on the existing-config value when the field is blank, so
            // duplicate emptiness is ignored here.
            if (!string.IsNullOrEmpty(a) && (a == b || a == c))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(b) && b == c)
            {
                return true;
            }

            return false;
        }
    }

    public string HotkeyConflictMessage =>
        HasHotkeyConflict ? _translator.Format("hotkey_conflict", DuplicateHotkey()) : string.Empty;

    // Back is enabled on every step EXCEPT the very first one (where
    // there is no previous step to go to).  Pre-fix the predicate also
    // excluded the last step ("CurrentStep < TotalSteps - 1") so the
    // Done page had no way to go back and tweak a previous choice.  At
    // the user's request, the Back button now also appears on the Done
    // page so the user can revisit the Hotkeys / API-key / Welcome
    // pages without restarting the wizard.
    public bool CanGoBack => CurrentStep > 0;

    public bool CanGoNext
    {
        get
        {
            if (CurrentStep >= TotalSteps - 1)
            {
                return false;
            }

            // Block "Next" off the Hotkeys step when there is a duplicate.
            // The user must fix it before moving on; otherwise persisting
            // would either silently overwrite one binding (if we ignored
            // the dup) or RegisterHotKey would fail at runtime and the
            // user would lose access to one of the actions.
            if (IsHotkeyStep && HasHotkeyConflict)
            {
                return false;
            }

            return true;
        }
    }

    public bool IsLastStep => CurrentStep == TotalSteps - 1;

    // Translator-derived strings.  All re-publish via the OnPropertyChanged
    // ("") forwarding in OnTranslatorPropertyChanged so a mid-wizard
    // language switch updates them all atomically.
    public string TitleText => _translator["onboarding_title"];

    public string StepIndicator => _translator.Format(
        "onboarding_step_indicator",
        CurrentStep + 1,
        TotalSteps);

    public string WelcomeHeading => _translator["onboarding_welcome_heading"];

    public string WelcomeBody => _translator["onboarding_welcome_body"];

    public string LanguageHeading => _translator["onboarding_language_heading"];

    public string LanguageBody => _translator["onboarding_language_body"];

    public string ApiKeyHeading => _translator["onboarding_apikey_heading"];

    public string ApiKeyBody => _translator["onboarding_apikey_body"];

    public string ApiKeyHint => _translator["api_key_hint"];

    public string HotkeyHeading => _translator["onboarding_hotkey_heading"];

    public string HotkeyBody => _translator["onboarding_hotkey_body"];

    public string HotkeyLabel => _translator["hotkey"];

    public string MenuHotkeyLabel => _translator["menu_hotkey"];

    public string UndoHotkeyLabel => _translator["history_undo_hotkey"];

    public string DoneHeading => _translator["onboarding_done_heading"];

    public string DoneBody => _translator.Format("onboarding_done_body", Hotkey);

    public string BtnSkip => _translator["onboarding_btn_skip"];

    public string BtnBack => _translator["onboarding_btn_back"];

    public string BtnNext => _translator["onboarding_btn_next"];

    public string BtnFinish => _translator["onboarding_btn_finish"];

    public event EventHandler? RequestedClose;

    /// <summary>
    /// Fires while the wizard runs whenever the language is changed so the
    /// host window can apply the choice live (re-rendering all bound
    /// translator strings on subsequent steps in the chosen language).
    /// </summary>
    public event EventHandler<LanguagePreviewEventArgs>? LanguagePreviewChanged;

    /// <summary>
    /// Called by the window's OnClosing if the user closes the wizard via
    /// the [×] button before clicking Skip / Finish. Treats it as Skip so
    /// the wizard does not reappear next launch (avoids a "stuck wizard"
    /// loop if the user genuinely wants nothing to do with it).
    /// </summary>
    public async Task PersistOnCloseAsync()
    {
        if (HasCompleted)
        {
            return;
        }

        await PersistSkipAsync();
        HasCompleted = true;
    }

    public void Dispose()
    {
        if (_translator is INotifyPropertyChanged translatorNpc)
        {
            translatorNpc.PropertyChanged -= OnTranslatorPropertyChanged;
        }

        CancelInFlightValidation();
    }

    /// <summary>
    /// Z8-F9 / L17: cancels and disposes any in-flight ApiKey-validation
    /// probe.  Called from <see cref="Dispose"/> (window-closed teardown)
    /// AND from the top of <see cref="FinishAsync"/> / <see cref="SkipAsync"/>
    /// so a probe scheduled mid-typing doesn't race the Finish-time
    /// `SaveAsync` sequence and write a stale `ApiKeyState` after the
    /// wizard's PersistChosenValuesAsync has already landed the chosen
    /// values on disk.  Pre-fix only Dispose cancelled — Finish ran first
    /// and could observe `ApiKeyState = Invalid` from a probe whose
    /// HTTP response arrived after PersistChosenValuesAsync but before
    /// the window closed.  No observable user impact today (the UI is
    /// already torn down by then) but the race shape would bite a
    /// future "wait for validation before committing" feature.
    /// </summary>
    private void CancelInFlightValidation()
    {
        // Sync Cancel() (not CancelAsync) because callers include
        // synchronous Dispose; the analyzer's CancelAsync nudge only
        // applies in async-capable contexts.
        var cts = Interlocked.Exchange(ref _validationCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    [RelayCommand]
    private void Next()
    {
        if (!CanGoNext)
        {
            return;
        }

        CurrentStep++;
    }

    [RelayCommand]
    private void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        CurrentStep--;
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        // Skip = commit only OnboardingCompleted + Language (whatever the
        // user picked in the live-preview), leaving the rest at defaults
        // so they can configure later via Settings.  Z8-F2 / H15 fix: if
        // the persist threw, we already surfaced the toast inside
        // PersistSkipAsync — DO NOT flip HasCompleted, so the wizard stays
        // open and the user has a chance to retry rather than landing in
        // the "wizard says done but next launch reopens it" hole.
        //
        // Z8-F9 / L17: mirror FinishAsync — cancel any in-flight ApiKey-
        // validation probe before persisting so a debounced HTTP response
        // can't race the SaveAsync sequence.
        CancelInFlightValidation();

        try
        {
            await PersistSkipAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogDebug(ex, "Skip persist failed — keeping wizard open");
            return;
        }

        HasCompleted = true;
        RequestedClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        // Z8-F9 / L17: cancel any in-flight validation probe BEFORE
        // persisting so a debounced HTTP response can't race the
        // SaveAsync sequence and mutate ApiKeyState mid-Finish.
        CancelInFlightValidation();

        try
        {
            await PersistChosenValuesAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Z8-F2 / H15 fix: same shape as SkipAsync — if persist failed,
            // keep the wizard open so the user can retry.
            _logger.LogDebug(ex, "Finish persist failed — keeping wizard open");
            return;
        }

        HasCompleted = true;
        WasFinished = true;
        RequestedClose?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsApiKeyStep));
        OnPropertyChanged(nameof(IsHotkeyStep));
        OnPropertyChanged(nameof(IsDoneStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(StepIndicator));
    }

    partial void OnLanguageChanged(Language value)
    {
        // Live-preview: as soon as the user picks a language on the
        // welcome step, every subsequent step renders in that language.
        // The host window wires this to ITranslator.SetLanguage(); the
        // Item[] notification from the translator then bounces back into
        // OnTranslatorPropertyChanged, refreshing every string property
        // at once.
        LanguagePreviewChanged?.Invoke(this, new LanguagePreviewEventArgs(value));
    }

    partial void OnHotkeyChanged(string value)
    {
        // Done-page body interpolates Hotkey, so refresh that binding too.
        OnPropertyChanged(nameof(DoneBody));
        OnPropertyChanged(nameof(HasHotkeyConflict));
        OnPropertyChanged(nameof(HotkeyConflictMessage));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnApiKeyChanged(string value)
    {
        // Reset the indicator immediately on every keystroke so the
        // user sees their previous Valid/Invalid badge clear as
        // they edit, rather than stale state hanging on for the
        // 400-ms debounce window.  Cancel any in-flight probe so
        // it can't race ahead and overwrite our None reset.
        var oldCts = Interlocked.Exchange(ref _validationCts, null);
        oldCts?.Cancel();
        oldCts?.Dispose();
        ApiKeyState = ApiKeyValidationState.None;

        // Empty field → leave indicator hidden, no probe to fire.
        // The wizard's API-key step is optional (Skip is allowed),
        // so we don't surface an "Invalid" badge for blank input.
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Fire-and-forget the debounced probe.  Task.Delay-based
        // debounce (vs. System.Threading.Timer) because the wizard
        // VM is short-lived and avoiding a Timer field keeps Dispose
        // simpler.  We deliberately don't capture the keystroke's
        // value here — ScheduleValidationAsync re-reads ApiKey at
        // probe time to honour the latest typed value rather than
        // the captured snapshot.
        _ = ScheduleValidationAsync();
    }

    /// <summary>
    /// Waits 400 ms (the debounce window) and then probes /credits
    /// to validate the key.  Cancellation: a newer keystroke
    /// cancels the linked CTS; the Task.Delay throws
    /// OperationCanceledException, the catch swallows it, and
    /// whichever ScheduleValidationAsync the new keystroke spawned
    /// owns the next state update.
    /// </summary>
    private async Task ScheduleValidationAsync()
    {
        // Renew the CTS atomically so a concurrent OnApiKeyChanged
        // observes the new one (and cancels it) rather than racing
        // with our just-cancelled one.
        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _validationCts, cts);
        if (oldCts is not null)
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
        }

        try
        {
            await Task.Delay(ApiKeyDebounceDelay, cts.Token);

            // Re-check the user's current key.  Between the
            // schedule time and now they might have typed more —
            // honour the latest value rather than the captured one
            // so "user typed an extra character right at the
            // 400-ms mark" doesn't pin us to the half-typed
            // version.
            var currentKey = ApiKey?.Trim();
            if (string.IsNullOrEmpty(currentKey))
            {
                ApiKeyState = ApiKeyValidationState.None;
                return;
            }

            ApiKeyState = ApiKeyValidationState.Checking;

            await _openRouter.GetCreditsAsync(currentKey, cts.Token);

            if (!cts.Token.IsCancellationRequested)
            {
                ApiKeyState = ApiKeyValidationState.Valid;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (OpenRouterException ex)
        {
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            // 401 → key itself is wrong; everything else (timeout,
            // 5xx, DNS) → can't tell.  Same mapping as
            // GeneralTabViewModel so the indicator semantics are
            // identical across the two surfaces.
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

            _logger.LogWarning(ex, "Onboarding API-key validation probe failed unexpectedly");
            ApiKeyState = ApiKeyValidationState.NetworkError;
        }
    }

    partial void OnMenuHotkeyChanged(string value)
    {
        OnPropertyChanged(nameof(HasHotkeyConflict));
        OnPropertyChanged(nameof(HotkeyConflictMessage));
        OnPropertyChanged(nameof(CanGoNext));
    }

    partial void OnUndoHotkeyChanged(string value)
    {
        OnPropertyChanged(nameof(HasHotkeyConflict));
        OnPropertyChanged(nameof(HotkeyConflictMessage));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private void OnTranslatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Translator emits PropertyChanged(nameof(Language)) followed by
        // PropertyChanged("Item[]") when SetLanguage runs.  Either is a
        // valid trigger to refresh every translator-derived property; we
        // listen on Item[] only so we do not double-publish.
        if (e.PropertyName != "Item[]")
        {
            return;
        }

        // Empty string == "every property changed".  WPF's binding engine
        // re-evaluates every binding whose source is this VM in one pass.
        OnPropertyChanged(string.Empty);
    }

    private static string NormaliseForCompare(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        // HotkeyAccelerator.Normalize returns the canonical
        // "Ctrl+Shift+E" form; comparing those Ordinal works.  If
        // Normalize fails to parse, fall back to a trimmed-uppercased
        // variant so a not-yet-valid accelerator still compares
        // consistently with itself across the three fields.
        var normalised = HotkeyAccelerator.Normalize(raw);
        return string.IsNullOrEmpty(normalised)
            ? raw.Trim().ToUpperInvariant()
            : normalised;
    }

    private string DuplicateHotkey()
    {
        var a = NormaliseForCompare(Hotkey);
        var b = NormaliseForCompare(MenuHotkey);
        var c = NormaliseForCompare(UndoHotkey);

        if (!string.IsNullOrEmpty(a) && (a == b || a == c))
        {
            return Hotkey;
        }

        if (!string.IsNullOrEmpty(b) && b == c)
        {
            return MenuHotkey;
        }

        return string.Empty;
    }

    private async Task PersistSkipAsync()
    {
        try
        {
            var existing = await _configStore.LoadAsync();
            // Z8-F1 / Z8-F6 / H14 fix: also propagate Language if the user
            // picked one (the live-preview committed it visually; Skip
            // should not silently revert it on next launch).
            await _configStore.SaveAsync(existing with
            {
                Language = Language,
                OnboardingCompleted = true,
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Z8-F2 / H15 fix: surface to user instead of silently flipping
            // HasCompleted=true and pretending the skip persisted.
            _logger.LogWarning(ex, "Onboarding wizard: failed to persist skip");
            ShowPersistFailureToast();
            throw;
        }
    }

    private async Task PersistChosenValuesAsync()
    {
        try
        {
            var existing = await _configStore.LoadAsync();
            var normalisedHotkey = HotkeyAccelerator.Normalize(Hotkey);
            var normalisedMenuHotkey = HotkeyAccelerator.Normalize(MenuHotkey);
            var normalisedUndoHotkey = HotkeyAccelerator.Normalize(UndoHotkey);

            var updated = existing with
            {
                Language = Language,
                Hotkey = normalisedHotkey,
                MenuHotkey = normalisedMenuHotkey,
                UndoHotkey = normalisedUndoHotkey,
                OnboardingCompleted = true,
            };
            await _configStore.SaveAsync(updated);

            // Z4-F3 / H7 fix: unregister ALL slots before re-registering,
            // matching the pattern in GeneralTabViewModel.ReregisterHotkeys.
            // Pre-fix, an in-session swap (Default<->Menu chord exchange)
            // tripped an ordering race where the second TryRegister's
            // duplicate-check saw the not-yet-unregistered first slot
            // and one of the two ended up silently absent. Unregistering
            // up front clears every slot before any new registration goes
            // in.
            _hotkeys.UnregisterAll();

            if (!string.IsNullOrWhiteSpace(updated.Hotkey))
            {
                _hotkeys.TryRegister(HotkeyKind.Default, updated.Hotkey);
            }

            if (!string.IsNullOrWhiteSpace(updated.MenuHotkey))
            {
                _hotkeys.TryRegister(HotkeyKind.Menu, updated.MenuHotkey);
            }

            if (!string.IsNullOrWhiteSpace(updated.UndoHotkey))
            {
                _hotkeys.TryRegister(HotkeyKind.Undo, updated.UndoHotkey);
            }

            // API key goes to the OS credential vault, not the JSON config.
            // An empty / whitespace value is a legitimate "skip for now" —
            // the user can fill the key in later via Settings → General.
            // Skip the credential write in that case: ICredentialStore's
            // contract rejects whitespace (ArgumentException), and on a
            // first launch there is nothing to clear anyway.  This
            // preserves the existing-key-untouched semantic that an empty
            // submit on the wizard means "leave it as-is".
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                await _credentials.SetApiKeyAsync(ApiKey);
            }

            // Translator is applied live during the wizard via
            // LanguagePreviewChanged, so no extra call here is required —
            // but make it idempotent in case the host wired the event after
            // the user already changed the language.
            _translator.SetLanguage(updated.Language);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "Onboarding wizard: failed to persist chosen values");
            ShowPersistFailureToast();
            throw;
        }
    }

    private void ShowPersistFailureToast()
    {
        if (_notifications is null)
        {
            return;
        }

        try
        {
            _notifications.ShowError(_translator["msg_save_settings_failed"]);
        }
        catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogDebug(toastEx, "Failed to surface onboarding persist-error toast");
        }
    }
}
