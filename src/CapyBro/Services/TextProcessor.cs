using System.Text;

using CapyBro.Models;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

public sealed class TextProcessor
{
    private static readonly TimeSpan ModifierReleaseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ClipboardSettleDelay = TimeSpan.FromMilliseconds(400);

    // Brief pause between Ctrl+V and the Shift+Left burst that
    // re-selects the pasted text.  Without it, fast text controls
    // (browser inputs, native edits) sometimes receive the Shift+Left
    // events while the paste is still being committed and end up
    // selecting nothing or only part of the original — the Shift+Left
    // arrives before the cursor has moved past the inserted text.
    // 80 ms is empirically enough on Windows 10/11 across Notepad,
    // Word, browsers, and VS-family editors without being noticeable
    // to the user (still well under the perceptual threshold for "the
    // text reappeared instantly").
    private static readonly TimeSpan PostPasteSettleDelay = TimeSpan.FromMilliseconds(80);

    private readonly IConfigStore _configStore;
    private readonly ICredentialStore _credentials;
    private readonly ILlmProviderFactory _providers;
    private readonly IClipboardService _clipboard;
    private readonly IInputSimulator _input;
    private readonly IModifierReleaseWaiter _modifiers;
    private readonly IPromptSelector _prompts;
    private readonly ITranslator _translator;
    private readonly IHistoryStore _history;
    private readonly IDiffPreviewService _diffPreview;
    private readonly ICostEstimator _costEstimator;
    private readonly IPrivacyRedactor _redactor;
    private readonly ITextSelectionExtender _selectionExtender;
    private readonly ILogger<TextProcessor> _logger;

    private int _isProcessing;

    // In-memory record of the latest successful improvement.  Always
    // populated regardless of the v11 ExperimentalHistory master flag —
    // the flag gates persistent journaling (history.json + History tab),
    // not the Undo hotkey, which only ever operates on the most recent
    // run.  Pre-fix flipping ExperimentalHistory off broke Undo because
    // _history.GetMostRecent() returned null (no entries written), so
    // Undo always answered "nothing to undo".  This field gives Undo a
    // store-independent fallback so the hotkey keeps working.
    private HistoryEntry? _lastImprovement;

    public TextProcessor(
        IConfigStore configStore,
        ICredentialStore credentials,
        ILlmProviderFactory providers,
        IClipboardService clipboard,
        IInputSimulator input,
        IModifierReleaseWaiter modifiers,
        IPromptSelector prompts,
        ITranslator translator,
        IHistoryStore history,
        IDiffPreviewService diffPreview,
        ICostEstimator costEstimator,
        IPrivacyRedactor redactor,
        ITextSelectionExtender selectionExtender,
        ILogger<TextProcessor> logger)
    {
        _configStore = configStore;
        _credentials = credentials;
        _providers = providers;
        _clipboard = clipboard;
        _input = input;
        _modifiers = modifiers;
        _prompts = prompts;
        _translator = translator;
        _history = history;
        _diffPreview = diffPreview;
        _costEstimator = costEstimator;
        _redactor = redactor;
        _selectionExtender = selectionExtender;
        _logger = logger;
    }

    public event EventHandler<TextProcessingStartedEventArgs>? ProcessingStarted;

    // H18 (Z10-F4) fix: user cancellation that lands AFTER the paste has
    // already happened leaves the AI text in the document with the user
    // believing the run failed.  This event lets App.xaml.cs surface a
    // dedicated toast ("Cancelled. Result is on the clipboard…") so the
    // outcome is distinguishable from a real failure.
    public event EventHandler? ProcessingCancelledWithResult;

    public event EventHandler? ProcessingCompleted;

    /// <summary>Fires after a successful Undo (history-driven restore).</summary>
    public event EventHandler? ProcessingUndone;

    /// <summary>
    /// Fires when the API call returns and we are about to either commit
    /// or open the diff-preview modal. Lets the host UI hide the
    /// "processing…" toast without showing the "done" toast — that comes
    /// later via <see cref="ProcessingCompleted"/> only after the user
    /// actually accepts the result.
    /// </summary>
    public event EventHandler? ProcessingProgressClosed;

    /// <summary>
    /// Fires once per streamed chunk during <see cref="ProcessAsync"/>.
    /// The event arg carries the FULL accumulated text so far (not the
    /// latest delta) — saves the UI from re-concatenating per chunk and
    /// lets it render whatever slice of the result it wants. Subscribers
    /// MUST marshal to the UI thread before touching WPF bindings.
    /// </summary>
    public event EventHandler<TextProcessingStreamUpdatedEventArgs>? ProcessingStreamUpdated;

    public event EventHandler<TextProcessingFailedEventArgs>? ProcessingFailed;

    public bool IsProcessing => Volatile.Read(ref _isProcessing) == 1;

    /// <summary>
    /// Returns true if processing was started, false if another invocation is already in flight.
    /// </summary>
    public async Task<bool> HandleHotkeyAsync(HotkeyKind kind, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
        {
            _logger.LogDebug("Hotkey {Kind} ignored — another processing run is already in flight", kind);
            return false;
        }

        try
        {
            if (kind == HotkeyKind.Undo)
            {
                await UndoLastAsync(ct);
            }
            else
            {
                await ProcessAsync(kind, ct);
            }

            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    /// <summary>
    /// Restores the original text of the most recent successful improvement
    /// onto the clipboard and pastes it into the focused control. Used by
    /// the Undo hotkey. Returns silently with a "history empty" failure
    /// event if there is nothing to undo.
    /// </summary>
    private async Task UndoLastAsync(CancellationToken ct)
    {
        // Prefer the in-memory record from this session — that survives
        // the user toggling ExperimentalHistory off mid-session.  Fall
        // back to the persistent store's most-recent entry so Undo can
        // still recover from a prior session where the user had history
        // enabled and persisted to disk.  Both being null is the only
        // genuine "nothing to undo" state.
        var entry = _lastImprovement ?? _history.GetMostRecent();
        if (entry is null)
        {
            RaiseFailedKey("history_nothing_to_undo");
            return;
        }

        try
        {
            // Wait for Ctrl+Shift release before sending Ctrl+V — otherwise
            // the still-held Ctrl combines with the synthesized V into a
            // wrong key combo (or nothing at all on apps that filter
            // modifier-down chaff).
            await _modifiers.WaitForReleaseAsync(ModifierReleaseTimeout, ct);
            await _clipboard.SetTextAsync(entry.Original, ct);
            await _input.SendPasteAsync(ct);

            // Re-select the restored original — same UX shape as a
            // regular successful run.  Gated by the same v12 flag as
            // ProcessAsync; see the comment block there for the
            // rationale.  Loading the config inside the Undo path is
            // a fresh I/O round-trip, but Undo is already fast (no API
            // call) and the config-store load is sub-millisecond
            // against a hot disk cache, so it doesn't bother the user.
            var undoConfig = await _configStore.LoadAsync(ct);
            if (undoConfig.ExperimentalKeepResultSelected)
            {
                await Task.Delay(PostPasteSettleDelay, ct);
                await _selectionExtender.ExtendBackwardAsync(entry.Original.Length, ct);
            }

            // The undo itself was a success — surface a brief confirmation
            // so the user knows it worked even though no API call ran.
            ProcessingUndone?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "Undo failed while restoring entry {EntryId}", entry.Id);
            RaiseFailedKey("api_unknown_error", ex);
        }
    }

    private async Task ProcessAsync(HotkeyKind kind, CancellationToken ct)
    {
        string? originalClipboard = await SafeGetClipboardAsync(ct);

        // Once we have written the AI result to the clipboard we MUST NOT
        // restore the original on a later failure — doing so silently
        // wipes the user's improvement. After resultCommitted=true any
        // exception from SendPasteAsync (UIPI-blocked SendInput, cancel
        // fired between paste and Completed, etc.) leaves the AI text on
        // the clipboard so the user can paste manually.
        var resultCommitted = false;

        try
        {
            await _clipboard.ClearAsync(ct);
            await _modifiers.WaitForReleaseAsync(ModifierReleaseTimeout, ct);
            await _input.SendCopyAsync(ct);
            await Task.Delay(ClipboardSettleDelay, ct);

            var selectedText = await _clipboard.GetTextAsync(ct);
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                // Two indistinguishable cases up to here: the user had
                // nothing selected (Ctrl+Insert copied nothing) OR the
                // selection was only whitespace (which the model would
                // reject downstream as "no text provided"). Either way,
                // surface a single "select text and try again" message
                // instead of forwarding garbage to OpenRouter.
                await RestoreClipboardAsync(originalClipboard, ct);
                RaiseFailedKey("toast_no_selection");
                return;
            }

            var prompt = await _prompts.SelectAsync(kind, ct);
            if (prompt is null)
            {
                _logger.LogDebug("Prompt selection cancelled or timed out — restoring clipboard");
                await RestoreClipboardAsync(originalClipboard, ct);
                return;
            }

            var config = await _configStore.LoadAsync(ct);
            var provider = _providers.Resolve(config.Provider);

            // Auth gate is provider-specific: OpenRouter needs a bearer
            // token (no key → actionable toast pointing at Settings →
            // General → API key); Ollama has no auth (the gate would
            // be a dead-end for users on a fresh local-only install).
            // The provider exposes the requirement via
            // ILlmProvider.RequiresApiKey so future providers slot in
            // without TextProcessor having to switch on the enum.
            var apiKey = provider.RequiresApiKey
                ? await _credentials.GetApiKeyAsync(ct) ?? string.Empty
                : string.Empty;
            if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
            {
                await RestoreClipboardAsync(originalClipboard, ct);
                RaiseFailedKey("api_unauthorized");
                return;
            }

            // Effective model: prompt-level override wins, but ONLY when
            // the per-prompt-model master experiment is on. Otherwise the
            // active provider's global model is authoritative regardless
            // of any saved prompt override (kill switch — the user can
            // disable the whole feature and instantly fall back to one
            // consistent model). The same value is recorded into history
            // so the user sees which model actually produced any given run.
            var effectiveModel = ResolveEffectiveModel(config, prompt);

            // Z1-F4 / M2 fix: early-check for an unconfigured model and
            // surface an actionable toast.  Pre-fix an empty model id
            // (corrupt config, user blanked the field, or some flow that
            // bypassed WithDefaultsApplied) flowed straight through to
            // the LLM client, which threw ArgumentException; the broad
            // catch then mapped it to a useless "api_unknown_error" toast.
            // Now the user gets a specific "open Settings → General" cue.
            //
            // v15: Ollama users get the dedicated "no Ollama model
            // picked yet" message — actionable because the remedy is
            // different (refresh tags + pick one, not paste a key).
            if (string.IsNullOrWhiteSpace(effectiveModel))
            {
                await RestoreClipboardAsync(originalClipboard, ct);
                RaiseFailedKey(config.Provider == LlmProviderKind.Ollama
                    ? "msg_ollama_model_not_configured"
                    : "msg_model_not_configured");
                return;
            }

            // Privacy redaction (experimental): replace PII patterns with
            // placeholders BEFORE handing the text to OpenRouter. The mapping
            // is captured once and reused on every call (initial + each
            // regenerate) so placeholder ids stay stable across attempts —
            // critical for the model to recognise repeated entities. After
            // each API call we Restore() so the user-visible result, the
            // diff preview, the paste, and the history all see cleartext.
            // Cost estimate uses the redacted text so the token estimate
            // reflects what's actually sent (placeholders ≈ original length).
            var redacted = config.ExperimentalPrivacyRedaction
                ? _redactor.Redact(selectedText)
                : RedactionResult.Empty(selectedText);

            // Pre-request cost estimate (only when the credits/cost
            // experiment is on). Failure / null => the toast omits the
            // cost suffix; never blocks the actual run.
            var estimatedCost = await EstimateCostAsync(config, apiKey, effectiveModel, redacted.RedactedText, ct);

            RaiseStarted(estimatedCost);

            // v14: `0` is the user's "wait forever" opt-in (documented
            // in the Additional features tooltip and in the AppConfig
            // field comment).  Translate to Timeout.InfiniteTimeSpan
            // here — the provider client checks for the sentinel and
            // skips CancelAfter entirely, so the request can run as
            // long as the backend is willing to stream.
            //
            // v15: timeout is provider-scoped — Timeout for OpenRouter,
            // OllamaTimeout for Ollama.  Defaults differ (60s vs 120s)
            // because local models tend to be slower on cold-start;
            // tuning is independent so the user doesn't have to juggle
            // a single shared value every time they toggle the
            // provider.
            var providerTimeoutSeconds = config.Provider == LlmProviderKind.Ollama
                ? config.OllamaTimeout
                : config.Timeout;
            var requestTimeout = providerTimeoutSeconds == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromSeconds(providerTimeoutSeconds);

            var result = await StreamAndAccumulateAsync(
                provider,
                apiKey,
                effectiveModel,
                prompt.Text,
                redacted.RedactedText,
                prompt.PreserveLanguage,
                requestTimeout,
                config.ExperimentalStreaming,
                ct);

            // Restore originals so downstream (diff preview, paste, history,
            // toast "Done") all see cleartext. The model only ever saw
            // placeholders. Round-trip relies on the model preserving them
            // verbatim — for placeholders the model dropped/mangled, the
            // user will see them as orphans in the output and can Reject
            // via diff preview if it has been enabled for this prompt.
            result = _redactor.Restore(result, redacted.Mapping);

            // Diff-preview opt-in path. Gated on TWO booleans:
            //   • config.ExperimentalDiffPreview — the global master flag
            //     in General → "Experimental features" (kill switch)
            //   • prompt.ShowDiffPreview          — per-prompt opt-in in
            //     Prompts editor (which prompts use the feature)
            // Both must be on for the modal to appear. Either off →
            // straight paste like before.
            if (prompt.ShowDiffPreview && config.ExperimentalDiffPreview)
            {
                while (true)
                {
                    RaiseProgressClosed();
                    var verdict = await _diffPreview.ShowAsync(selectedText, result, ct);

                    if (verdict == DiffPreviewResult.Accept)
                    {
                        break;
                    }

                    if (verdict == DiffPreviewResult.Reject)
                    {
                        // User cancelled — restore the original clipboard,
                        // skip paste, skip history, skip Completed event.
                        await RestoreClipboardAsync(originalClipboard, ct);
                        return;
                    }

                    // Regenerate: re-show progress toast and re-call the API
                    // using the SAME effective model + SAME redacted input
                    // + SAME mapping — placeholder ids stay stable so the
                    // model treats it as a re-roll of the same prompt
                    // rather than a fresh task with renumbered entities.
                    // Reuse the original cost estimate — same input, same
                    // model, no need to re-fetch pricing.
                    RaiseStarted(estimatedCost);
                    // Reuse the same Timeout.InfiniteTimeSpan sentinel
                    // translation as the initial call above — config.Timeout
                    // == 0 means "wait forever" on regenerate too.
                    result = await StreamAndAccumulateAsync(
                        provider,
                        apiKey,
                        effectiveModel,
                        prompt.Text,
                        redacted.RedactedText,
                        prompt.PreserveLanguage,
                        requestTimeout,
                        config.ExperimentalStreaming,
                        ct);
                    result = _redactor.Restore(result, redacted.Mapping);
                }
            }

            await _clipboard.SetTextAsync(result, ct);
            resultCommitted = true;
            await _input.SendPasteAsync(ct);

            // Re-select the just-pasted text so the user immediately
            // sees the new boundary and can copy/extend/delete without
            // re-selecting manually.  Gated by the v12 experimental
            // flag so users opt in via General → "Additional features"
            // — the underlying mechanism (UIA TextPattern primary,
            // Shift+Left fallback) is robust in modern apps but can
            // still glitch in edge cases (custom controls without UIA
            // support, modal dialogs, IME composition state) and we'd
            // rather have unhappy users disable a feature than have
            // them think the whole utility is broken.
            //
            // Brief settle delay before extending: gives the foreground
            // app time to commit the paste before we touch the caret.
            //
            // Best-effort step: a failure here MUST NOT route through
            // the resultCommitted-guarded restore — paste already
            // happened, text is on screen, clobbering it back to the
            // original clipboard would lose the AI output.
            if (config.ExperimentalKeepResultSelected)
            {
                await Task.Delay(PostPasteSettleDelay, ct);
                await _selectionExtender.ExtendBackwardAsync(result.Length, ct);
            }

            // Record after a successful paste — never on failure paths,
            // since the user expects "undo last replacement" to mean
            // "the thing that just appeared in the document," and a
            // failed run never modified the document.
            //
            // Two storage layers, with different gating:
            //   1. _lastImprovement (in-memory) — ALWAYS populated.
            //      Powers the Undo hotkey via UndoLastAsync, which is
            //      decoupled from the v11 ExperimentalHistory flag so
            //      turning history off does not break undo.
            //   2. _history.Add(...) — only when ExperimentalHistory
            //      is on.  Persists to ~/.ai_text_improver_v2_history.json
            //      and feeds the Settings → History tab.
            // Pre-fix the in-memory layer didn't exist, so Undo went
            // through _history.GetMostRecent() unconditionally — when
            // ExperimentalHistory was off the store stayed empty and
            // Undo always answered "nothing to undo".
            var entry = new HistoryEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTimeOffset.UtcNow,
                Original = selectedText,
                Improved = result,
                PromptText = prompt.Text,
                Model = effectiveModel,
                HotkeyKind = (int)kind,
            };
            _lastImprovement = entry;

            if (config.ExperimentalHistory)
            {
                try
                {
                    _history.Add(entry);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    // History is best-effort; never let a store hiccup mask a
                    // successful run.
                    _logger.LogWarning(ex, "Failed to record history entry");
                }
            }

            RaiseCompleted();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !resultCommitted)
        {
            // Timeout-triggered cancellation during the API call (the
            // inner cts in OpenRouterClient.ImproveStreamAsync invoked
            // `CancelAfter` because config.Timeout elapsed before the
            // model finished streaming).  Two `when`-clause conditions
            // discriminate this branch from the others:
            //
            //   • !ct.IsCancellationRequested — external ct is NOT
            //     cancelled, so the user did not press Cancel.
            //   • !resultCommitted — the OCE landed BEFORE clipboard +
            //     paste committed.  H18 (Z10-F4) covers the post-paste
            //     OCE case separately (paste / post-paste hooks throw
            //     OCE with the external ct intact); that path needs
            //     ProcessingCancelledWithResult, not a failure toast.
            //
            // Pre-fix this branch fell through to the user-cancel catch
            // below which silently rethrew without raising any event.
            // The api_request_timeout toast only fired when the timeout
            // hit during SendAsync (request-headers phase), because
            // OpenRouterClient.SendAsync's internal `when (!externalCt.
            // IsCancellationRequested)` catch wrapped that case into an
            // OpenRouterException.  But once SendAsync returned the
            // headers and streaming started, the timeout-vs-cancel
            // discrimination was lost — bug surfaced as "set Timeout to
            // 5 s, hit hotkey on a slow-tier model, watch 5 s elapse
            // with no toast."
            await RestoreClipboardAsync(originalClipboard, CancellationToken.None);
            RaiseFailedKey("api_request_timeout");
            return;
        }
        catch (OperationCanceledException)
        {
            if (!resultCommitted)
            {
                await RestoreClipboardAsync(originalClipboard, CancellationToken.None);
            }
            else
            {
                // H18 fix: paste already landed before cancellation
                // signal observed.  Tell the UI so it can show the
                // dedicated "result is on the clipboard" toast — without
                // this, the cancel path is silent (TextProcessor rethrows
                // and App.xaml.cs's Task.Run catch is a no-op).
                RaiseCancelledWithResult();
            }

            throw;
        }
        catch (OpenRouterException ex)
        {
            if (!resultCommitted)
            {
                await RestoreClipboardAsync(originalClipboard, CancellationToken.None);
            }

            // v15: prefer the keyed path when the underlying client
            // populated `LocalizationKey` (currently OllamaClient does
            // it for `ollama_unreachable` so the App.xaml.cs handler
            // can auto-revert to OpenRouter on hotkey-time failures).
            // Falls back to the raw-message path for OpenRouter errors
            // that were translated at throw-time.
            if (ex.LocalizationKey is { } key)
            {
                RaiseFailedKeyWithMessage(key, ex.Message, ex);
            }
            else
            {
                RaiseFailed(ex.Message, ex);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "Unexpected error during text processing");
            if (!resultCommitted)
            {
                await RestoreClipboardAsync(originalClipboard, CancellationToken.None);
            }

            RaiseFailedKey("api_unknown_error", ex);
        }
    }

    /// <summary>
    /// Returns null if reading clipboard failed (don't restore — we'd risk clearing real content).
    /// Returns "" if clipboard was genuinely empty (safe to clear on restore).
    /// Returns string content otherwise.
    /// </summary>
    private async Task<string?> SafeGetClipboardAsync(CancellationToken ct)
    {
        try
        {
            return await _clipboard.GetTextAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read original clipboard before processing — restore will be skipped");
            return null;
        }
    }

    private async Task RestoreClipboardAsync(string? original, CancellationToken ct)
    {
        // null = initial read failed; we have no idea what was there. Don't touch clipboard
        // (it currently holds either the user's selection or nothing — clearing it would
        // risk wiping data we never saw).
        if (original is null)
        {
            return;
        }

        try
        {
            if (original.Length == 0)
            {
                await _clipboard.ClearAsync(ct);
            }
            else
            {
                await _clipboard.SetTextAsync(original, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to restore clipboard contents after processing");
        }
    }

    /// <summary>
    /// Drains <see cref="IOpenRouterClient.ImproveStreamAsync"/> into a
    /// single string while (optionally) firing <see cref="ProcessingStreamUpdated"/>
    /// for each delta so the UI can render live progress. Result is the
    /// concatenation of all yielded deltas, post-stripped via
    /// <see cref="ResultStripper"/> (mirroring what the buffered path used
    /// to do server-side). Throws <see cref="OpenRouterException"/> if the
    /// stream finished but the stripped result was empty — covers the
    /// "model returned only whitespace / fences" case.
    /// </summary>
    /// <param name="emitStreamEvents">
    /// When false, the per-delta event is suppressed — accumulation still
    /// happens silently. Used by the experimental-streaming master flag:
    /// the HTTP transport keeps streaming (better cancellation), but the
    /// user sees the original static "Обробка..." toast.
    /// </param>
    private async Task<string> StreamAndAccumulateAsync(
        ILlmProvider provider,
        string apiKey,
        string model,
        string promptText,
        string selectedText,
        bool preserveLanguage,
        TimeSpan timeout,
        bool emitStreamEvents,
        CancellationToken ct)
    {
        var sb = new StringBuilder();

        await foreach (var delta in provider.ImproveStreamAsync(
            apiKey, model, promptText, selectedText, timeout, preserveLanguage, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            sb.Append(delta);

            if (!emitStreamEvents)
            {
                continue;
            }

            // Raise on EVERY delta — subscribers (App.xaml.cs) decide
            // whether to throttle / coalesce. Doing it here would mean
            // tying TextProcessor to UI rendering rates.
            try
            {
                ProcessingStreamUpdated?.Invoke(
                    this,
                    new TextProcessingStreamUpdatedEventArgs(sb.ToString()));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A subscriber blowing up must not corrupt the streamed
                // accumulator; log + continue draining the model output.
                _logger.LogWarning(ex, "ProcessingStreamUpdated subscriber threw — continuing stream");
            }
        }

        var raw = sb.ToString();
        var stripped = ResultStripper.Strip(raw);
        if (string.IsNullOrEmpty(stripped))
        {
            throw new OpenRouterException(_translator["api_empty_result"]);
        }

        return stripped;
    }

    /// <summary>
    /// Picks the model id to send to the active LLM provider for this
    /// run.  Per-prompt overrides are PROVIDER-SCOPED — a prompt can
    /// carry one override for OpenRouter (<see cref="Prompt.Model"/>)
    /// AND a separate override for Ollama (<see cref="Prompt.OllamaModel"/>);
    /// the matching one is consulted based on the active
    /// <see cref="AppConfig.Provider"/>.  Both live on disk
    /// simultaneously so toggling the global provider doesn't lose
    /// either side's pick.  Honored only when
    /// <see cref="AppConfig.ExperimentalPerPromptModel"/> is on;
    /// otherwise the active provider's global model is authoritative
    /// regardless of any saved per-prompt value (kill switch).
    /// Whitespace-only overrides are treated as "no override".
    /// </summary>
    private static string ResolveEffectiveModel(AppConfig config, Prompt prompt)
    {
        var promptOverride = config.Provider == LlmProviderKind.Ollama
            ? prompt.OllamaModel
            : prompt.Model;

        if (config.ExperimentalPerPromptModel && !string.IsNullOrWhiteSpace(promptOverride))
        {
            return promptOverride;
        }

        return config.Provider == LlmProviderKind.Ollama
            ? config.OllamaModel
            : config.Model;
    }

    /// <summary>
    /// Computes the per-request cost estimate, but only when the
    /// credits/cost experimental flag is on. Returns null otherwise so
    /// the master flag truly gates ALL the network activity for this
    /// feature — no /models pricing fetch happens for users who never
    /// opted in.
    /// </summary>
    private async Task<decimal?> EstimateCostAsync(
        AppConfig config,
        string apiKey,
        string modelId,
        string inputText,
        CancellationToken ct)
    {
        if (!config.ExperimentalCostsAndCredits)
        {
            return null;
        }

        // Ollama is local-only — there is no per-request billing, so
        // the cost-estimate suffix on the toast would always read $0
        // and the corresponding /models pricing fetch would be a wasted
        // OpenRouter round trip (which would 401 anyway if the user
        // has no OpenRouter key set up because they only use Ollama).
        // Skip outright when the active provider is Ollama.
        if (config.Provider == LlmProviderKind.Ollama)
        {
            return null;
        }

        try
        {
            return await _costEstimator.EstimateAsync(apiKey, modelId, inputText, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                    and not OutOfMemoryException
                                    and not StackOverflowException)
        {
            // Defence-in-depth — the estimator already swallows most
            // errors internally. A bug here must never block the actual
            // user request.
            _logger.LogWarning(ex, "Cost estimate threw — proceeding without toast suffix");
            return null;
        }
    }

    private void RaiseStarted(decimal? estimatedCostUsd) =>
        ProcessingStarted?.Invoke(this, new TextProcessingStartedEventArgs(estimatedCostUsd));

    private void RaiseCompleted() => ProcessingCompleted?.Invoke(this, EventArgs.Empty);

    private void RaiseProgressClosed() => ProcessingProgressClosed?.Invoke(this, EventArgs.Empty);

    // Raw-message path — used for failures whose text is NOT a Translator
    // key (e.g. OpenRouterException.Message, built from HTTP status + server
    // body and already resolved at throw time).  Subscribers surface verbatim.
    private void RaiseFailed(string message, Exception? exception = null) =>
        ProcessingFailed?.Invoke(this, new TextProcessingFailedEventArgs(message, exception));

    // Key-binding path — preferred for catch-blocks whose reason maps to a
    // known message-id.  EventArgs carries both the key (for late-binding
    // at toast-render time, so mid-flight language switches deliver the
    // correct locale) AND an eagerly-resolved snapshot for back-compat.
    // Z10-F7 / M27.
    private void RaiseFailedKey(string localizationKey, Exception? exception = null) =>
        ProcessingFailed?.Invoke(this, new TextProcessingFailedEventArgs(
            localizationKey, _translator[localizationKey], exception));

    // v15: variant for OpenRouterException catches that carry both a
    // pre-translated message AND a LocalizationKey — preserves the
    // exception's exact message text (in case the throw-time locale
    // differs from the current one) while still giving subscribers a
    // key to dispatch on (e.g. App.xaml.cs auto-reverts to
    // OpenRouter when key == "ollama_unreachable").
    private void RaiseFailedKeyWithMessage(string localizationKey, string message, Exception? exception = null) =>
        ProcessingFailed?.Invoke(this, new TextProcessingFailedEventArgs(
            localizationKey, message, exception));

    private void RaiseCancelledWithResult() =>
        ProcessingCancelledWithResult?.Invoke(this, EventArgs.Empty);
}
