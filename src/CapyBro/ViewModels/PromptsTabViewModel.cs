using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;

using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Views;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

namespace CapyBro.ViewModels;

public sealed partial class PromptsTabViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Quiet-period before the editor's current state is auto-persisted.
    /// 400 ms balances "user expects changes to stick" against the cost
    /// of writing the config file mid-keystroke. Matches the API-key
    /// debounce in <see cref="GeneralTabViewModel"/>.
    /// </summary>
    private static readonly TimeSpan AutoSaveDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Every locale's value for the "use the global model" ComboBox
    /// sentinel.  Cached once because the set is invariant for the
    /// lifetime of the Translator singleton.  Used by
    /// <see cref="RebuildAvailableModelsForPicker"/> to recognise an
    /// <see cref="EditorModel"/> that holds the sentinel in a PREVIOUS
    /// locale — without this check a UI language switch would surface
    /// the stale label as an extra "leftover model" entry alongside the
    /// new locale's sentinel, e.g. UA picker would show both "Модель за
    /// замовчуванням" AND "Default model" / "Модель по умолчанию".
    /// </summary>
    private static readonly IReadOnlySet<string> KnownDefaultModelSentinels =
        Translator.LocalizedValuesAcrossLocales("prompt_model_default_option");

    private readonly IConfigStore _configStore;
    private readonly IPromptRegistry _registry;
    private readonly ITranslator _translator;
    private readonly GeneralTabViewModel _general;
    private readonly INotificationService? _notifications;
    private readonly ILogger<PromptsTabViewModel> _logger;
    private readonly Timer _autoSaveTimer;

    // Z3-F5 / M9: `_activeMap` is the authoritative pre-save snapshot of
    // active prompts (slot defaults merged with overrides + custom).  It
    // is the sole writer/reader contract for the no-op detection inside
    // `AutoSaveSnapshotAsync` (line 759).  Today the snapshot is safe
    // because there's no cross-VM mutation of the same config — every
    // writer to `CustomPrompts` / `DefaultPromptOverrides` /
    // `DefaultPromptSettings` runs on the same dispatcher through this
    // VM, so reading `_activeMap` is equivalent to reading what is on
    // disk.  IF a future feature ever introduces a second writer (e.g.,
    // a "duplicate prompt" command from a tray-menu context, or a cross-
    // tab move-prompt-to-system pathway), this assumption breaks and
    // the no-op check could silently swallow a real edit.  In that case
    // the fix is to refresh `_activeMap` via
    // `_registry.GetActive(_configStore.LoadAsync().Result, _translator.Language)`
    // at the START of `AutoSaveSnapshotAsync` rather than trusting the
    // cached snapshot.  Tag any new writer to this map so the invariant
    // remains greppable.
    private IReadOnlyDictionary<string, Prompt> _activeMap = new Dictionary<string, Prompt>();
    private bool _suppressEditorSync;
    private bool _autoSavePending;

    /// <summary>
    /// Key under which the editor's current state was last persisted.
    /// Used to detect rename: if the user changes <see cref="EditorName"/>,
    /// the next auto-save removes the old key from CustomPrompts and adds
    /// the new one. Without tracking this, every name keystroke would
    /// leave behind an orphan entry.
    /// </summary>
    private string _previousEditorName = string.Empty;

    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<string> _activeKeys = [];

    [ObservableProperty]
    private string? _selectedKey;

    [ObservableProperty]
    private string _editorName = string.Empty;

    [ObservableProperty]
    private string _editorText = string.Empty;

    [ObservableProperty]
    private bool _editorPreserveLanguage = true;

    [ObservableProperty]
    private bool _editorShowDiffPreview;

    /// <summary>
    /// Per-prompt model override, as displayed in the editor ComboBox.
    /// The localized "Default model" sentinel and the empty string both
    /// mean "use the global model" — we map either to <c>null</c> on save
    /// (see <see cref="ResolveSavedModel"/>). On load, a stored null
    /// shows up as the sentinel so the dropdown always has a meaningful
    /// selection rather than reading as blank.
    /// </summary>
    [ObservableProperty]
    private string _editorModel = string.Empty;

    [ObservableProperty]
    private string? _defaultPromptKey;

    /// <summary>
    /// Items shown in the per-prompt model picker. First entry is the
    /// localized "Default model" sentinel (selecting it means "use the
    /// global model"); the rest mirror the user's pinned models from
    /// <see cref="GeneralTabViewModel.Models"/>. Kept in sync with that
    /// collection's CollectionChanged + the translator's language change.
    /// </summary>
    public ObservableCollection<string> AvailableModelsForPicker { get; } = [];

    public PromptsTabViewModel(
        IConfigStore configStore,
        IPromptRegistry registry,
        ITranslator translator,
        GeneralTabViewModel general,
        ILogger<PromptsTabViewModel> logger,
        INotificationService? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(general);

        _configStore = configStore;
        _registry = registry;
        _translator = translator;
        _general = general;
        _logger = logger;
        _notifications = notifications;
        _autoSaveTimer = new Timer(OnAutoSaveTick);

        translator.PropertyChanged += OnTranslatorPropertyChanged;

        // Forward General.Models additions/removals into the picker
        // collection so the dropdown shows the user's current pinned set
        // without needing to re-open Settings.  v15: also watch the
        // OllamaModels list so a Refresh on the Ollama tags repopulates
        // the per-prompt picker when Provider=Ollama.
        _general.Models.CollectionChanged += OnGeneralModelsChanged;
        _general.OllamaModels.CollectionChanged += OnGeneralModelsChanged;

        // Forward the master "Experimental — Diff preview" / per-prompt
        // model flags so XAML can bind for Visibility on per-prompt UI.
        // Both ViewModels are DI singletons so this subscription lives
        // for the app's lifetime.
        _general.PropertyChanged += OnGeneralPropertyChanged;

        RebuildAvailableModelsForPicker();
    }

    /// <summary>
    /// Mirrors <see cref="GeneralTabViewModel.ExperimentalDiffPreview"/>.
    /// </summary>
    public bool IsDiffPreviewExperimentEnabled => _general.ExperimentalDiffPreview;

    /// <summary>
    /// Mirrors <see cref="GeneralTabViewModel.ExperimentalPerPromptModel"/>.
    /// </summary>
    public bool IsPerPromptModelExperimentEnabled => _general.ExperimentalPerPromptModel;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Z10-F5 fix: flush any pending edits synchronously before the timer
        // is destroyed. HistoryStore.Dispose() does this correctly; we have
        // to match that pattern — otherwise a `_host.Dispose()` (driven by
        // App.OnExit or DispatcherUnhandledException's graceful path)
        // arriving within the 400ms debounce window silently drops the
        // user's last edit.  Hard-cap the wait so a wedged file-lock
        // doesn't block shutdown indefinitely.
#pragma warning disable VSTHRD002 // Sync wait is intentional — we are in Dispose
        try
        {
            Task.Run(FlushPendingSaveAsync).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Pending auto-save flush failed during Dispose");
        }
#pragma warning restore VSTHRD002

        _autoSaveTimer.Dispose();
        _general.Models.CollectionChanged -= OnGeneralModelsChanged;
        _general.OllamaModels.CollectionChanged -= OnGeneralModelsChanged;
        _general.PropertyChanged -= OnGeneralPropertyChanged;
        _translator.PropertyChanged -= OnTranslatorPropertyChanged;
    }

    /// <summary>
    /// Synchronously flushes any pending auto-save and awaits the resulting
    /// disk write.  Called by <c>App.OnExit</c> (or the graceful shutdown
    /// path triggered by <c>DispatcherUnhandledException</c>) so the user's
    /// last edit reaches disk before the host is disposed.  Z10-F1 fix —
    /// pre-fix only the API-key debounce was flushed.
    /// </summary>
    public Task FlushPendingAsync() => FlushPendingSaveAsync();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var config = await _configStore.LoadAsync(ct);
            _activeMap = _registry.GetActive(config, _translator.Language);

            // Decide what to show as the global "default prompt" before we
            // touch any observable property — we may need to write back a
            // correction if config.DefaultPrompt points at a now-missing
            // entry (e.g. user deleted the prompt that was set as default).
            var resolvedDefaultKey = _activeMap.ContainsKey(config.DefaultPrompt)
                ? config.DefaultPrompt
                : _activeMap.Keys.FirstOrDefault();

            _suppressEditorSync = true;
            try
            {
                ActiveKeys = new ObservableCollection<string>(_activeMap.Keys);
                DefaultPromptKey = resolvedDefaultKey;
            }
            finally
            {
                _suppressEditorSync = false;
            }

            // Persist the correction OUTSIDE the suppress block so the
            // OnDefaultPromptKeyChanged path is bypassed (we already did
            // the work) but the saved JSON catches up with reality.
            if (!string.Equals(config.DefaultPrompt, resolvedDefaultKey ?? string.Empty, StringComparison.Ordinal))
            {
                try
                {
                    await _configStore.SaveAsync(config with { DefaultPrompt = resolvedDefaultKey ?? string.Empty }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to persist corrected DefaultPrompt during load");
                }
            }

            // Auto-select the first prompt so the user lands on something
            // editable instead of an empty editor (less guesswork in the
            // new save-button-less workflow).
            var firstKey = ActiveKeys.FirstOrDefault();
            if (firstKey is not null)
            {
                SelectedKey = firstKey;
            }
            else
            {
                ClearEditor();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to load prompts");
        }
    }

    /// <summary>
    /// Test-only helper: forces an immediate flush of any pending edits
    /// without waiting on the debounce timer, so behaviour tests can
    /// observe the post-save config state deterministically.  Internal
    /// because callers outside the test assembly should not bypass the
    /// debouncer — the production paths (selection change, NewAsync,
    /// DeleteAsync) already flush via <see cref="FlushPendingSaveAsync"/>.
    /// </summary>
    internal Task FlushPendingForTestAsync() => FlushPendingSaveAsync();

    partial void OnSelectedKeyChanged(string? value)
    {
        // M8 (Z3-F4) fix: refresh Delete's CanExecute regardless of the
        // suppression flag — the button's enabled state must always
        // reflect the current selection, even during programmatic
        // selection swaps (LoadAsync, post-delete reselect, etc.).
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));

        if (_suppressEditorSync)
        {
            return;
        }

        // Commit pending edits BEFORE swapping editor content. We snapshot
        // the current editor state (which still belongs to the previously
        // selected prompt) and fire-and-forget the save — by the time the
        // save runs, EditorName/EditorText etc. will have been replaced
        // with the new selection's content, so we MUST capture before.
        if (_autoSavePending)
        {
            _ = FlushPendingSaveAsync();
        }

        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (_activeMap.TryGetValue(value, out var prompt))
        {
            // v15: per-prompt model is provider-scoped — read the
            // matching field for the active provider so toggling
            // OpenRouter↔Ollama doesn't bleed an id from one backend
            // into the picker that's listing the other backend's
            // catalogue.  Both Prompt.Model AND Prompt.OllamaModel
            // coexist on disk so each toggle restores the user's
            // last-known pick for that provider.
            //
            // Defence-in-depth: if the stored value appears in the
            // OTHER provider's catalogue (e.g. a legacy save from
            // before the v15 split where the OpenRouter id leaked
            // into the Ollama field via mid-build auto-save), treat
            // it as corrupt and fall through to the default sentinel.
            // Without this, RebuildAvailableModelsForPicker's H12
            // "preserve unknown model" branch would surface the bad
            // id alongside the active provider's catalogue and the
            // user would see both backends' models in one dropdown.
            var storedProviderModel = _general.Provider == LlmProviderKind.Ollama
                ? prompt.OllamaModel
                : prompt.Model;
            var otherProviderCatalogue = _general.Provider == LlmProviderKind.Ollama
                ? _general.Models
                : _general.OllamaModels;
            var activeProviderCatalogue = _general.Provider == LlmProviderKind.Ollama
                ? _general.OllamaModels
                : _general.Models;
            if (!string.IsNullOrEmpty(storedProviderModel)
                && otherProviderCatalogue.Contains(storedProviderModel))
            {
                storedProviderModel = null;
            }
            else if (!string.IsNullOrEmpty(storedProviderModel)
                     && !activeProviderCatalogue.Contains(storedProviderModel))
            {
                // The model was removed from the active catalogue at
                // some point between save and reopen (user clicked
                // "−" in Settings → Provider model list).  Fall back
                // to the sentinel so the dropdown doesn't display
                // an orphan id alongside the user's currently-pinned
                // models.  Auto-save flushes the now-null
                // Prompt.Model on the next prompt-switch (snapshot's
                // no-op check at AutoSaveSnapshotAsync naturally
                // detects the difference and persists).
                storedProviderModel = null;
            }

            // Map stored null → the localized "Default model" sentinel
            // so the ComboBox displays it instead of empty (and the
            // user can clearly see the prompt opts into the global).
            var storedModel = string.IsNullOrEmpty(storedProviderModel)
                ? _translator["prompt_model_default_option"]
                : storedProviderModel;

            // H12 companion: ensure the stored model is reachable in
            // AvailableModelsForPicker BEFORE assigning EditorModel.  With
            // IsEditable=False the SelectedItem binding would otherwise
            // bounce back to null and silently overwrite the stored value
            // when the model is not in the curated list (legacy v1 prompt
            // or hand-edited JSON).
            if (!AvailableModelsForPicker.Contains(storedModel))
            {
                AvailableModelsForPicker.Add(storedModel);
            }

            _suppressEditorSync = true;
            try
            {
                EditorName = value;
                EditorText = prompt.Text;
                EditorPreserveLanguage = prompt.PreserveLanguage;
                EditorShowDiffPreview = prompt.ShowDiffPreview;
                EditorModel = storedModel;
                _previousEditorName = value;
            }
            finally
            {
                _suppressEditorSync = false;
            }
        }
    }

    partial void OnEditorNameChanged(string value)
    {
        // M8 (Z3-F4) companion: HasSelection falls back to EditorName
        // when SelectedKey is null (e.g. just after ClearEditor), so the
        // Delete button's enablement tracks both inputs.
        DeleteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
        ScheduleAutoSave();
    }

    partial void OnEditorTextChanged(string value) => ScheduleAutoSave();

    partial void OnEditorPreserveLanguageChanged(bool value) => ScheduleAutoSave();

    partial void OnEditorShowDiffPreviewChanged(bool value) => ScheduleAutoSave();

    partial void OnEditorModelChanged(string value) => ScheduleAutoSave();

    partial void OnDefaultPromptKeyChanged(string? value)
    {
        if (_suppressEditorSync || string.IsNullOrEmpty(value))
        {
            return;
        }

        _ = PersistDefaultPromptAsync(value);
    }

    /// <summary>
    /// Creates a fresh prompt entry in CustomPrompts with a unique
    /// auto-generated name and selects it. Replaces the old "clear the
    /// editor" behaviour: after auto-save replaced the explicit Save
    /// button, an empty editor with no entry would be confusing — there
    /// would be no "thing" to save into.
    /// </summary>
    /// <remarks>
    /// L7 (Z3-F7) note: <c>NewAsync</c> intentionally has no confirmation
    /// dialog, unlike <see cref="DeleteAsync"/>.  Creating a prompt is
    /// reversible (one-click Delete of the new entry) and high-frequency;
    /// adding a confirm here would degrade the UX without removing any
    /// real risk.  Documented so a future contributor reviewing the
    /// pattern doesn't add one out of consistency with Delete.
    /// </remarks>
    [RelayCommand]
    private async Task NewAsync()
    {
        // Commit any in-flight edit before creating a new prompt — the
        // user's last keystroke on the previous one shouldn't get lost
        // just because they started a new one.  AWAIT is critical: a
        // fire-and-forget here would race with our own LoadAsync below
        // (lost-update on the config file).
        if (_autoSavePending)
        {
            await FlushPendingSaveAsync();
        }

        try
        {
            var config = await _configStore.LoadAsync();
            var custom = config.CustomPrompts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            var name = GenerateUniqueNewPromptName(custom);
            custom[name] = new Prompt
            {
                Text = string.Empty,
                PreserveLanguage = true,
                ShowDiffPreview = false,
                Model = null,
            };

            await _configStore.SaveAsync(config with { CustomPrompts = custom });
            await LoadAsync();

            // LoadAsync auto-selects the first item; override with the
            // newly-created prompt so the user lands on it ready to edit.
            SelectedKey = name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to create a new prompt");
        }
    }

    /// <summary>
    /// M8 (Z3-F4) fix companion: predicate for <see cref="DeleteCommand"/>'s
    /// CanExecute.  Pre-fix the Delete button stayed fully active even
    /// when no prompt was selected — clicking it popped a confirmation
    /// dialog for a no-op delete (and then ran <c>ClearEditor</c>),
    /// which felt like a bug.  CanExecute=<c>HasSelection</c> makes the
    /// button dim automatically while there is nothing to act on.
    /// </summary>
    public bool HasSelection => !string.IsNullOrWhiteSpace(SelectedKey ?? EditorName);

    /// <summary>
    /// Test seam.  When non-null, replaces the production
    /// <see cref="ConfirmDialog.Ask"/> call inside <see cref="DeleteAsync"/>
    /// — receives the prompt name being deleted and returns true to
    /// proceed, false to abort.  The behaviour test harness sets this to
    /// <c>_ =&gt; true</c> so the existing delete-flow assertions still
    /// run; production code never touches it and falls back to the real
    /// modal dialog.
    /// </summary>
    internal Func<string, bool>? ConfirmDeleteOverride { get; set; }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        var keyToDelete = SelectedKey ?? EditorName;
        if (string.IsNullOrWhiteSpace(keyToDelete))
        {
            return;
        }

        // Z3-F1 / C6 fix: flush any pending auto-save BEFORE the modal so
        // the dispatcher pump inside ShowDialog can't fire the 400ms
        // debounce mid-confirm and resurrect the prompt we're about to
        // delete. NewAsync already does this; DeleteAsync was the missing
        // sibling — the race window was small but reproducible under
        // typing-then-immediately-clicking-Delete.
        if (_autoSavePending)
        {
            await FlushPendingSaveAsync();
        }

        // Confirm before deleting — same affordance as "Очистить историю" /
        // "Сбросить настройки".  Pre-fix the click was instantaneous and
        // irreversible, which felt jarringly different from every other
        // destructive action in the app.  We surface the prompt name in
        // the body so the user can verify they're about to remove the
        // intended one.  Test harness bypasses this via
        // ConfirmDeleteOverride to keep the existing delete-flow tests
        // headless (no WPF Application available in xUnit context).
        bool confirmed;
        if (ConfirmDeleteOverride is { } overrideFn)
        {
            confirmed = overrideFn(keyToDelete);
        }
        else
        {
            var result = ConfirmDialog.Ask(
                title: _translator["prompt_confirm_delete_title"],
                body: _translator.Format("prompt_confirm_delete_body", keyToDelete),
                confirmText: _translator["btn_delete"],
                owner: Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive));
            confirmed = result == true;
        }

        if (!confirmed)
        {
            return;
        }

        // Capture the neighbour we want to land on AFTER the delete so the
        // selection moves to the next prompt down (or the previous one if
        // the deleted entry was last) instead of LoadAsync's default
        // "snap to ActiveKeys[0]" behaviour.  Computed up-front from the
        // current list because the index of `keyToDelete` becomes
        // meaningless once ActiveKeys is rebuilt by the post-save LoadAsync.
        //   • IndexOf == -1 means the user deleted via EditorName fallback
        //     for a name that isn't in the list (brand-new unsaved
        //     prompt) — leave nextSelectionKey null and let the default
        //     first-row fallback take over.
        //   • Index N < Count-1 means there's a row after this one — that
        //     row will shift up into slot N after delete, but we capture
        //     its KEY now so the lookup survives the rebuild.
        //   • Index == Count-1 means the deleted row was last — fall back
        //     to its predecessor.
        var deletedIndex = ActiveKeys.IndexOf(keyToDelete);
        string? nextSelectionKey = null;
        if (deletedIndex >= 0)
        {
            if (deletedIndex + 1 < ActiveKeys.Count)
            {
                nextSelectionKey = ActiveKeys[deletedIndex + 1];
            }
            else if (deletedIndex - 1 >= 0)
            {
                nextSelectionKey = ActiveKeys[deletedIndex - 1];
            }
        }

        // Cancel pending auto-save so we don't recreate the entry we're
        // about to delete (race: timer fires after Delete starts).
        _autoSaveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _autoSavePending = false;

        try
        {
            var config = await _configStore.LoadAsync();

            // The display name the user clicked may be a preset slot, a
            // CustomPrompts entry, or BOTH (legacy shadow state).  Whatever
            // it is, "Delete" means "make this name disappear from the
            // list", so we attack every layer that could re-surface it.
            var custom = config.CustomPrompts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var defaultOverrides = config.DefaultPromptOverrides.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var slotSettings = config.DefaultPromptSettings.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var deleted = config.DeletedDefaults.ToHashSet(StringComparer.Ordinal);

            var customRemoved = custom.Remove(keyToDelete);

            // Preset hide is GLOBAL across locales — the 8 preset slots
            // are semantically the same prompt regardless of UI language
            // (only the display text/name differs), so a delete in UA
            // also hides "Fix errors" / "Исправить ошибки".  We add ALL
            // three language keys to DeletedDefaults and drop any
            // per-language overrides + slot-level settings on this slot
            // so a language switch can't resurrect the prompt via
            // leftover override text or stale settings.
            var presetOriginalKey = _registry.ResolveOriginalPresetKey(config, _translator.Language, keyToDelete);
            var presetHidden = presetOriginalKey is not null;
            if (presetHidden)
            {
                var equivalents = _registry.GetAllEquivalentsForDefaultKey(presetOriginalKey!);
                foreach (var k in equivalents)
                {
                    deleted.Add(k);
                    defaultOverrides.Remove(k);
                }

                var enKey = _registry.GetCanonicalEnglishKeyForDefault(presetOriginalKey!);
                if (enKey is not null)
                {
                    slotSettings.Remove(enKey);
                }
            }

            if (!customRemoved && !presetHidden)
            {
                // Unknown name: nothing to delete; just clear the editor
                // back to a usable state.
                ClearEditor();
                _previousEditorName = string.Empty;
                return;
            }

            var updated = config with
            {
                CustomPrompts = custom,
                DefaultPromptOverrides = defaultOverrides,
                DefaultPromptSettings = slotSettings,
                DeletedDefaults = [.. deleted],
            };

            // If the deleted key was the configured global default, clear
            // it so LoadAsync's fallback picks a real, currently-active
            // entry instead of leaving a dangling pointer.  For preset
            // deletes we check ALL 3 language keys, not just the current
            // one — the user may have set DefaultPrompt while a different
            // UI language was active.
            var defaultPointerOrphaned =
                string.Equals(updated.DefaultPrompt, keyToDelete, StringComparison.Ordinal)
                || (presetOriginalKey is not null
                    && _registry.GetAllEquivalentsForDefaultKey(presetOriginalKey)
                        .Any(k => string.Equals(updated.DefaultPrompt, k, StringComparison.Ordinal)));
            if (defaultPointerOrphaned)
            {
                updated = updated with { DefaultPrompt = string.Empty };
            }

            await _configStore.SaveAsync(updated);
            await LoadAsync();

            // LoadAsync auto-selects ActiveKeys[0] which would yank focus
            // up to the very first prompt — user-reported as "перескакує
            // на найвищий промт".  Override to the neighbour we captured
            // before the mutation so the selection lands on the natural
            // next entry (or the previous one when deleting the last row).
            // Guarded by Contains because a preset-delete could in theory
            // remove a row that we picked as the neighbour, in which case
            // we leave LoadAsync's fallback alone.
            if (nextSelectionKey is not null && ActiveKeys.Contains(nextSelectionKey))
            {
                SelectedKey = nextSelectionKey;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete prompt");
        }
    }

    private void ScheduleAutoSave()
    {
        if (_suppressEditorSync || _disposed)
        {
            return;
        }

        _autoSavePending = true;
        try
        {
            _autoSaveTimer.Change(AutoSaveDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Race with Dispose — fine, the VM is going away.
        }
    }

    private void OnAutoSaveTick(object? state)
    {
        // The Timer callback runs on the threadpool.  AutoSaveSnapshotAsync
        // mutates ObservableCollection ActiveKeys + several
        // [ObservableProperty] fields; ObservableCollection's
        // CollectionChanged handler in WPF's CollectionView throws
        // InvalidOperationException when the change comes from a thread
        // other than the dispatcher's.  Without marshalling, the auto-save
        // path blew up silently (caught by the broad catch) and left the
        // bound list visibly empty even though the underlying _activeMap
        // had every entry — exactly the symptom the user reported (list
        // empty, editor still populated).  Dispatch the entire snapshot +
        // save to the UI thread so every observable mutation happens with
        // the right thread affinity; the awaitable I/O inside still
        // releases the UI thread between continuations.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ExecutePendingAutoSave();
            return;
        }

        // Fire-and-forget — BeginInvoke returns a DispatcherOperation we
        // intentionally do not await; observation happens via the inner
        // AutoSaveSnapshotAsync's own try/catch.
        _ = dispatcher.BeginInvoke(ExecutePendingAutoSave);
    }

    private void ExecutePendingAutoSave()
    {
        if (!_autoSavePending)
        {
            return;
        }

        _autoSavePending = false;
        var snapshot = SnapshotEditorState();
        _ = AutoSaveSnapshotAsync(snapshot);
    }

    /// <summary>
    /// Synchronously cancels the pending timer and runs the save so the
    /// caller can <c>await</c> it before performing the next config
    /// mutation.  Returns a completed task when there's nothing to flush.
    /// </summary>
    private Task FlushPendingSaveAsync()
    {
        _autoSaveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (!_autoSavePending)
        {
            return Task.CompletedTask;
        }

        _autoSavePending = false;
        var snapshot = SnapshotEditorState();
        return AutoSaveSnapshotAsync(snapshot);
    }

    private EditorSnapshot SnapshotEditorState() => new(
        PreviousName: _previousEditorName,
        Name: EditorName,
        Text: EditorText,
        PreserveLanguage: EditorPreserveLanguage,
        ShowDiffPreview: EditorShowDiffPreview,
        Model: ResolveSavedModel(EditorModel));

    private async Task AutoSaveSnapshotAsync(EditorSnapshot snapshot)
    {
        // Empty / whitespace name does NOT mean "delete this prompt" — it
        // is almost always a transient state while the user is mid-edit.
        // Treat it as "save under the previous name" so we never silently
        // lose a body the user has been typing into.
        var effectiveName = string.IsNullOrWhiteSpace(snapshot.Name)
            ? snapshot.PreviousName
            : snapshot.Name.Trim();

        if (string.IsNullOrWhiteSpace(effectiveName))
        {
            // Nothing to key the prompt by — first-time entry with no name
            // typed yet.  Wait until the user provides one.
            return;
        }

        try
        {
            var config = await _configStore.LoadAsync();
            var custom = config.CustomPrompts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var defaultOverrides = config.DefaultPromptOverrides.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var slotSettings = config.DefaultPromptSettings.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            var prevName = snapshot.PreviousName;
            var hasPreviousName = !string.IsNullOrWhiteSpace(prevName);
            var nameChanged = hasPreviousName
                              && !string.Equals(prevName, effectiveName, StringComparison.Ordinal);

            // Resolve the previous display name to its preset slot key in
            // the CURRENT language.  When non-null, the edit is a per-
            // language preset edit (or rename) — every change is stored
            // in DefaultPromptOverrides under that language-specific key
            // so other UI languages remain untouched.  When null, the
            // edit targets a fully-custom prompt and goes to CustomPrompts.
            var presetOriginalKey = hasPreviousName
                ? _registry.ResolveOriginalPresetKey(config, _translator.Language, prevName)
                : null;
            var editingPreset = presetOriginalKey is not null;

            // Rename collision check.  If the user typed a name that's
            // ALREADY in the active map (and is not the prompt we are
            // currently saving), refuse the save — it would silently
            // overwrite an unrelated prompt.  The editor name is
            // reverted to the previous so the user notices.
            var collidesWithActive =
                nameChanged
                && _activeMap.TryGetValue(effectiveName, out _)
                && !string.Equals(effectiveName, prevName, StringComparison.Ordinal);
            var collidesWithCustom =
                custom.ContainsKey(effectiveName)
                && !string.Equals(effectiveName, prevName, StringComparison.Ordinal)
                && !editingPreset;

            if (collidesWithActive || collidesWithCustom)
            {
                _logger.LogInformation(
                    "Refusing to save '{Prev}' under '{New}': name already taken",
                    prevName,
                    effectiveName);

                // Revert the editor name on the UI thread so the user
                // sees their typed-name didn't stick.
                NotifyRenameRefused(prevName);
                return;
            }

            // Standard rename path for a custom prompt: drop the old key.
            if (nameChanged && !editingPreset && custom.ContainsKey(prevName))
            {
                custom.Remove(prevName);
            }

            // No-op detection.  If every observable field already matches
            // what the user currently sees in the list, there is nothing
            // to write.  Suppresses spurious writes when the user toggles
            // a checkbox off-then-on or just opens-and-closes a prompt
            // without changing anything.  v15: compare the active
            // provider's slot (Model vs OllamaModel) — the other slot
            // is invisible to the user and unchanged by this snapshot.
            var existingActiveProviderModel = _activeMap.TryGetValue(effectiveName, out var existingActive)
                ? (_general.Provider == LlmProviderKind.Ollama
                    ? existingActive.OllamaModel
                    : existingActive.Model)
                : null;
            if (!nameChanged
                && existingActive is not null
                && string.Equals(existingActive.Text, snapshot.Text, StringComparison.Ordinal)
                && existingActive.PreserveLanguage == snapshot.PreserveLanguage
                && existingActive.ShowDiffPreview == snapshot.ShowDiffPreview
                && string.Equals(existingActiveProviderModel ?? string.Empty, snapshot.Model ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            if (editingPreset)
            {
                // Preset save splits across two storage layers:
                //   • DefaultPromptOverrides[langKey]: per-locale state
                //     (text + optional rename via OverrideName).  Other
                //     UI languages remain unaffected.
                //   • DefaultPromptSettings[KeyEn]: slot-level settings
                //     (PreserveLanguage, ShowDiffPreview, Model) shared
                //     across UA/RU/EN — toggling "Preserve source
                //     language" on the UA copy applies in EN/RU too,
                //     because that flag is a property of the prompt's
                //     purpose, not of the active UI locale.
                var overrideName = string.Equals(presetOriginalKey, effectiveName, StringComparison.Ordinal)
                    ? null
                    : effectiveName;

                defaultOverrides[presetOriginalKey!] = new Prompt
                {
                    Text = snapshot.Text,
                    OverrideName = overrideName,
                };

                var enKey = _registry.GetCanonicalEnglishKeyForDefault(presetOriginalKey!);
                if (enKey is not null)
                {
                    // v15: only the active provider's Model slot is
                    // refreshed from the snapshot; the OTHER provider's
                    // slot is preserved verbatim from disk so a save
                    // while on Ollama doesn't clobber a prior OpenRouter
                    // override (and vice versa).  Both fields coexist on
                    // disk; TextProcessor picks the matching one at
                    // request time.
                    var existingSlot = slotSettings.TryGetValue(enKey, out var prior)
                        ? prior
                        : null;
                    var openRouterModel = _general.Provider == LlmProviderKind.Ollama
                        ? existingSlot?.Model
                        : snapshot.Model;
                    var ollamaModel = _general.Provider == LlmProviderKind.Ollama
                        ? snapshot.Model
                        : existingSlot?.OllamaModel;

                    slotSettings[enKey] = new DefaultPromptSlotSettings
                    {
                        PreserveLanguage = snapshot.PreserveLanguage,
                        ShowDiffPreview = snapshot.ShowDiffPreview,
                        Model = openRouterModel,
                        OllamaModel = ollamaModel,
                    };
                }
            }
            else
            {
                // Genuinely custom prompt: live in CustomPrompts (shared
                // across all UI languages, since the user typed the name
                // themselves and there is no language-pair to anchor it).
                // v15: same provider-scoped merge rule as the preset
                // branch above.
                var existingCustom = custom.TryGetValue(effectiveName, out var priorCustom)
                    ? priorCustom
                    : null;
                var openRouterModel = _general.Provider == LlmProviderKind.Ollama
                    ? existingCustom?.Model
                    : snapshot.Model;
                var ollamaModel = _general.Provider == LlmProviderKind.Ollama
                    ? snapshot.Model
                    : existingCustom?.OllamaModel;

                custom[effectiveName] = new Prompt
                {
                    Text = snapshot.Text,
                    PreserveLanguage = snapshot.PreserveLanguage,
                    ShowDiffPreview = snapshot.ShowDiffPreview,
                    Model = openRouterModel,
                    OllamaModel = ollamaModel,
                };
            }

            var updated = config with
            {
                CustomPrompts = custom,
                DefaultPromptOverrides = defaultOverrides,
                DefaultPromptSettings = slotSettings,
            };

            // If the global "default prompt" pointer was on the old name,
            // follow the rename so the user's setting doesn't dangle.
            if (nameChanged && string.Equals(updated.DefaultPrompt, prevName, StringComparison.Ordinal))
            {
                updated = updated with { DefaultPrompt = effectiveName };
            }

            await _configStore.SaveAsync(updated);

            // Refresh derived state on the UI thread without resetting
            // the editor (the user may already be typing the next char).
            _activeMap = _registry.GetActive(updated, _translator.Language);
            _suppressEditorSync = true;
            try
            {
                ReplaceCollectionInPlace(ActiveKeys, _activeMap.Keys);

                // CRITICAL: only re-sync SelectedKey / _previousEditorName
                // if the editor is STILL on the prompt this snapshot
                // belonged to.  When the user has already moved on (e.g.
                // they clicked a different list item while we were saving)
                // their selection MUST be left alone — bouncing it back
                // would be a user-hostile selection-jump and would also
                // corrupt the next snapshot's PreviousName.
                if (string.Equals(EditorName, effectiveName, StringComparison.Ordinal))
                {
                    if (!string.Equals(SelectedKey, effectiveName, StringComparison.Ordinal))
                    {
                        SelectedKey = effectiveName;
                    }

                    _previousEditorName = effectiveName;
                }

                // If the saved prompt was the global default, refresh the
                // ComboBox so its label reflects the (possibly renamed)
                // entry.
                if (string.Equals(updated.DefaultPrompt, effectiveName, StringComparison.Ordinal)
                    && !string.Equals(DefaultPromptKey, effectiveName, StringComparison.Ordinal))
                {
                    DefaultPromptKey = effectiveName;
                }
            }
            finally
            {
                _suppressEditorSync = false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Z3-F2 / H5 fix: pre-fix the auto-save catch only logged at
            // Warning and the user's typed prompt body could silently
            // disappear on next session. Surface the failure via the
            // notification service so the user knows to retry rather than
            // discovering on the next launch that their edit reverted.
            _logger.LogWarning(ex, "Auto-save failed for prompt '{Name}'", effectiveName);
            if (_notifications is not null)
            {
                try
                {
                    _notifications.ShowError(_translator["msg_save_settings_failed"]);
                }
                catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
                {
                    _logger.LogDebug(toastEx, "Failed to surface auto-save error toast");
                }
            }
        }
    }

    private void NotifyRenameRefused(string previousName)
    {
        // Marshalled back through _suppressEditorSync so reverting the
        // EditorName does not re-trigger another auto-save round.
        _suppressEditorSync = true;
        try
        {
            EditorName = previousName;
        }
        finally
        {
            _suppressEditorSync = false;
        }

        if (_notifications is not null)
        {
            try
            {
                _notifications.ShowError(_translator.Format("prompt_name_collision", previousName));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to surface rename-collision toast");
            }
        }
    }

    private string? ResolveSavedModel(string editorModelText)
    {
        // Treat both "" and the localized sentinel as "no override".
        // Stored as null in JSON; resolver in TextProcessor picks the
        // global model when null/whitespace.
        if (string.IsNullOrWhiteSpace(editorModelText))
        {
            return null;
        }

        if (string.Equals(
                editorModelText,
                _translator["prompt_model_default_option"],
                StringComparison.Ordinal))
        {
            return null;
        }

        return editorModelText.Trim();
    }

    private string GenerateUniqueNewPromptName(Dictionary<string, Prompt> existingCustom)
    {
        var baseName = _translator["new_prompt_default_name"];
        if (!_activeMap.ContainsKey(baseName) && !existingCustom.ContainsKey(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!_activeMap.ContainsKey(candidate) && !existingCustom.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        // Pathological — user has 1000 "Новий промт N"; fall back to GUID.
        return baseName + " " + Guid.NewGuid().ToString("N")[..8];
    }

    private void ClearEditor()
    {
        _suppressEditorSync = true;
        try
        {
            SelectedKey = null;
            EditorName = string.Empty;
            EditorText = string.Empty;
            EditorPreserveLanguage = true;
            EditorShowDiffPreview = false;
            EditorModel = _translator["prompt_model_default_option"];
            _previousEditorName = string.Empty;
        }
        finally
        {
            _suppressEditorSync = false;
        }
    }

    private async Task PersistDefaultPromptAsync(string key)
    {
        try
        {
            var config = await _configStore.LoadAsync();
            await _configStore.SaveAsync(config with { DefaultPrompt = key });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to persist default prompt key");
        }
    }

    private void OnGeneralPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Defensive marshal — OnPropertyChanged itself can drive binding
        // updates, and the producer (GeneralTabViewModel) may fire change
        // events from threadpool continuations of PersistConfigAsync.
        if (e.PropertyName == nameof(GeneralTabViewModel.ExperimentalDiffPreview))
        {
            DispatchToUi(() => OnPropertyChanged(nameof(IsDiffPreviewExperimentEnabled)));
        }
        else if (e.PropertyName == nameof(GeneralTabViewModel.ExperimentalPerPromptModel))
        {
            DispatchToUi(() => OnPropertyChanged(nameof(IsPerPromptModelExperimentEnabled)));
        }
        else if (e.PropertyName == nameof(GeneralTabViewModel.Provider))
        {
            // v15: Provider flip swaps the picker source between
            // OpenRouter Models and Ollama OllamaModels.  Beyond
            // rebuilding the dropdown's catalogue, we also need to
            // re-read the active prompt's provider-scoped Model field
            // (Prompt.Model vs Prompt.OllamaModel) because the
            // currently-displayed EditorModel still reflects the OLD
            // provider's slot.
            //
            // H1 fix: flush any pending auto-save FIRST.  Without this,
            // the user could type a per-prompt model value, toggle the
            // Provider checkbox within the 400 ms debounce window, and
            // have OnSelectedKeyChanged below reset EditorModel to the
            // pre-typed value before the debounced save fired — the
            // typed value would be lost.
            DispatchToUi(() => _ = RefreshEditorForProviderChangeAsync());
        }
    }

    /// <summary>
    /// H1 + M4 helper — flushes the pending auto-save snapshot,
    /// re-binds EditorModel from the new provider's stored slot, and
    /// rebuilds the per-prompt model picker so the dropdown shows the
    /// active provider's catalogue.  Sequencing matters:
    /// 1) flush captures the user's just-typed values into the OLD
    ///    provider's slot before anything else touches EditorModel.
    /// 2) OnSelectedKeyChanged reads the NEW provider's slot off
    ///    disk and assigns it to EditorModel (using _suppressEditorSync
    ///    so the assignment doesn't schedule a fresh auto-save).
    /// 3) Rebuild produces the picker contents with the now-correct
    ///    EditorModel, so the cross-provider filter at the tail of
    ///    RebuildAvailableModelsForPicker never has to fire — the
    ///    sentinel/legacy preserve branches just work.
    /// </summary>
    private async Task RefreshEditorForProviderChangeAsync()
    {
        if (_autoSavePending)
        {
            await FlushPendingSaveAsync();
        }

        if (!string.IsNullOrEmpty(SelectedKey))
        {
            OnSelectedKeyChanged(SelectedKey);
        }

        RebuildAvailableModelsForPicker();
    }

    private void OnTranslatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ITranslator.Language))
        {
            // Language change rebuilds the localized "Default model"
            // sentinel and the new-prompt placeholder; LoadAsync also
            // re-keys all prompts to the new language's strings.
            //
            // Translator.SetLanguage is called only from the UI thread
            // today (App.OnStartup, GeneralTabViewModel, OnboardingWizard).
            // Marshal anyway so a future caller from threadpool doesn't
            // crash the dispatcher — this event handler runs synchronously
            // on the producer's thread.
            DispatchToUi(() =>
            {
                RebuildAvailableModelsForPicker();
                _ = LoadAsync();
            });
        }
        else if (e.PropertyName == "Item[]")
        {
            // Pure indexer-change (no language flip) — rebuild the picker
            // so the localized sentinel keeps up.
            DispatchToUi(RebuildAvailableModelsForPicker);
        }
    }

    private void OnGeneralModelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Z10-F3 / FZ3-F1 fix: this event fires synchronously on whatever
        // thread mutated GeneralTabViewModel.Models. Historically a
        // threadpool-thread mutator (e.g. an awaited LoadAsync without a
        // UI sync context) would land us here off-UI, then mutating the
        // bound AvailableModelsForPicker would raise the
        // "CollectionView does not support changes from a thread other
        // than the dispatcher thread" exception that crashed the app 7+
        // times in production logs. Always marshal.
        //
        // v15 extension: if the user explicitly removed the model that
        // is currently selected for the active prompt's per-prompt
        // override, fall back to the sentinel so the picker doesn't
        // keep displaying the deleted id as a sibling entry (which
        // RebuildAvailableModelsForPicker's H12 "preserve unknown"
        // branch would otherwise do).  Not suppressed — we want
        // OnEditorModelChanged → ScheduleAutoSave to fire so the
        // disk-stored Prompt.Model updates to null too.
        var removed = e.Action == NotifyCollectionChangedAction.Remove
            ? e.OldItems
            : null;
        DispatchToUi(() =>
        {
            if (removed is not null
                && !string.IsNullOrEmpty(EditorModel)
                && !KnownDefaultModelSentinels.Contains(EditorModel))
            {
                foreach (var item in removed)
                {
                    if (string.Equals(item as string, EditorModel, StringComparison.Ordinal))
                    {
                        EditorModel = _translator["prompt_model_default_option"];
                        break;
                    }
                }
            }

            RebuildAvailableModelsForPicker();
        });
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the WPF UI thread. When called
    /// from a non-UI thread, posts via <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(Action)"/>;
    /// otherwise executes synchronously. Returns a no-op when the
    /// application's dispatcher is unavailable (test harness without WPF).
    /// </summary>
    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _ = dispatcher.BeginInvoke(action);
        }
    }

    private void RebuildAvailableModelsForPicker()
    {
        var label = _translator["prompt_model_default_option"];
        AvailableModelsForPicker.Clear();
        AvailableModelsForPicker.Add(label);

        // v15: pull from whichever provider's catalogue is active.
        // Ollama tags (e.g. "llama3.2:latest") and OpenRouter ids
        // (e.g. "openai/gpt-4o") live in separate collections on
        // GeneralTabViewModel so a Provider toggle doesn't lose
        // either side's pinned set.
        var source = _general.Provider == LlmProviderKind.Ollama
            ? _general.OllamaModels
            : _general.Models;
        foreach (var m in source)
        {
            AvailableModelsForPicker.Add(m);
        }

        if (string.IsNullOrEmpty(EditorModel))
        {
            return;
        }

        // 2026-05-12 fix: EditorModel may still hold the PREVIOUS locale's
        // sentinel label after a UI language switch (Translator's Language
        // event fires before LoadAsync's continuation has had a chance to
        // re-read prompts and reset EditorModel to the new locale's label).
        // Recognise the cross-locale sentinel set and re-bind EditorModel
        // to the current label so the ComboBox header shows the right
        // string AND the leftover-preservation branch below does not push
        // the stale label into the picker as a sibling entry.  Pre-fix the
        // dropdown showed both "Default model" (or "Модель по умолчанию")
        // AND "Модель за замовчуванням" simultaneously on every switch.
        if (KnownDefaultModelSentinels.Contains(EditorModel))
        {
            if (!string.Equals(EditorModel, label, StringComparison.Ordinal))
            {
                _suppressEditorSync = true;
                try
                {
                    EditorModel = label;
                }
                finally
                {
                    _suppressEditorSync = false;
                }
            }

            return;
        }

        // v15 cross-provider guard: if EditorModel happens to belong
        // to the OTHER provider's catalogue (e.g. user just toggled
        // Provider — this rebuild fires from the Provider-change
        // handler before OnSelectedKeyChanged has re-read the
        // provider-scoped Prompt slot), reset it to the sentinel
        // label instead of preserving as a sibling.  Suppress the
        // resulting auto-save burst — the next user edit will write
        // the corrected provider-scoped fields naturally.
        var otherCatalogue = _general.Provider == LlmProviderKind.Ollama
            ? _general.Models
            : _general.OllamaModels;
        if (otherCatalogue.Contains(EditorModel))
        {
            _suppressEditorSync = true;
            try
            {
                EditorModel = label;
            }
            finally
            {
                _suppressEditorSync = false;
            }

            return;
        }

        // H12 companion: keep the currently-selected EditorModel reachable
        // even when it is not in the active provider's pinned catalogue
        // (legacy v1 prompt, hand-edited JSON, or a tag the user has
        // since `ollama rm`-ed).  Without this insert, SelectedItem
        // binding would land on null and the user would see a blank
        // ComboBox with no way to inspect the stored value before
        // replacing it.
        if (!AvailableModelsForPicker.Contains(EditorModel))
        {
            AvailableModelsForPicker.Add(EditorModel);
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

    private sealed record EditorSnapshot(
        string PreviousName,
        string Name,
        string Text,
        bool PreserveLanguage,
        bool ShowDiffPreview,
        string? Model);
}
