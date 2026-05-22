using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;

using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Views;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

namespace CapyBro.ViewModels;

/// <summary>
/// Backs the in-window History tab in SettingsWindow's sidebar. Lives as a
/// singleton for the app lifetime — the same instance is bound every time
/// the user clicks the History sidebar tab, so subscriptions to
/// <see cref="IHistoryStore.Changed"/> stay armed and the bound collection
/// always reflects the latest state.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryStore _store;
    private readonly IClipboardService _clipboard;
    private readonly ITranslator _translator;
    private readonly INotificationService? _notifications;
    private readonly ILogger<HistoryViewModel> _logger;
    private bool _disposed;

    /// <summary>
    /// Full unfiltered snapshot mirroring <see cref="IHistoryStore.Snapshot"/>.
    /// <see cref="Entries"/> is the filtered view that XAML binds to;
    /// when <see cref="FilterText"/> is empty the two are identical.
    /// Kept as a plain List (not ObservableCollection) because XAML
    /// never binds to it directly — every consumer goes through
    /// Entries which we mutate in place to drive the
    /// CollectionViewSource grouping.
    /// </summary>
    private readonly List<HistoryEntry> _allEntries = [];

    [ObservableProperty]
    private ObservableCollection<HistoryEntry> _entries = [];

    [ObservableProperty]
    private HistoryEntry? _selectedEntry;

    /// <summary>
    /// Free-text filter applied to <see cref="Entries"/>.  Case-
    /// insensitive substring match against Original / Improved /
    /// PromptText / Model on each entry.  Empty string disables
    /// filtering and shows the full snapshot.  Bound to the
    /// search TextBox above the list on HistoryTab.
    /// </summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    public HistoryViewModel(
        IHistoryStore store,
        IClipboardService clipboard,
        ITranslator translator,
        ILogger<HistoryViewModel> logger,
        INotificationService? notifications = null)
    {
        _store = store;
        _clipboard = clipboard;
        _translator = translator;
        _notifications = notifications;
        _logger = logger;

        // M13 (Z5-F5) note: EnableCollectionSynchronization is defence-in-
        // depth, NOT the primary thread-safety mechanism.  OnStoreChanged
        // marshals to the dispatcher BEFORE touching Entries (and
        // LoadFromStore / ApplyFilter only ever run on the UI thread as a
        // result), so this collection's mutations are already UI-thread-
        // confined.  We keep the registration so any future contributor
        // who adds a fast-path Entries.Add(...) bypassing OnStoreChanged
        // does not immediately crash WPF's binding pipeline — the lock
        // is a guard rail, not a guarantee.  Production paths must keep
        // mutating Entries on the dispatcher; do not assume this call
        // makes off-thread mutation safe.
        BindingOperations.EnableCollectionSynchronization(_entries, new object());

        _store.Changed += OnStoreChanged;
        _store.Faulted += OnStoreFaulted;

        // Pre-populate so the tab shows current entries the very first
        // time the user clicks the sidebar — no flash of empty state.
        LoadFromStore();
    }

    /// <summary>
    /// Detaches the store-event subscriptions so the singleton VM doesn't
    /// keep the singleton store's `Changed`/`Faulted` event delegates alive
    /// past container shutdown.  Z5-F10 / L11: registered as
    /// <c>AddSingleton&lt;HistoryViewModel&gt;()</c> in App.xaml.cs:1182 —
    /// the DI container owns disposal via <c>_host.Dispose()</c> in
    /// <c>App.OnExit</c>, so this Dispose is invoked exactly once at
    /// process shutdown by the .NET DI container, not by client code.
    /// Idempotent so a hypothetical double-dispose (e.g. a test harness
    /// disposing the container twice) is safe.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _store.Changed -= OnStoreChanged;
        _store.Faulted -= OnStoreFaulted;
        _disposed = true;
    }

    public bool HasEntries => Entries.Count > 0;

    /// <summary>
    /// M14 (Z5-F6) fix: lets XAML triggers tell "no entries because the
    /// store is empty" from "no entries because the filter excluded
    /// everything".  Pre-fix the no-history trigger compared
    /// <c>FilterText == ""</c> literally — a whitespace-only filter
    /// matched the no-matches arm instead even though
    /// <see cref="ApplyFilter"/> trims and treats it as an empty filter.
    /// IsNullOrWhiteSpace closes that gap so the user gets the right
    /// advice ("add some entries" vs. "try a different query").
    /// </summary>
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(FilterText);

    public string? OriginalText => SelectedEntry?.Original;

    public string? ImprovedText => SelectedEntry?.Improved;

    public string? PromptText => SelectedEntry?.PromptText;

    public string? ModelName => SelectedEntry?.Model;

    public string? FormattedTime =>
        SelectedEntry?.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public void LoadFromStore()
    {
        // Capture the user's current selection BEFORE we tear down the
        // collection — without this, every store change (a new hotkey
        // run, a delete from another window) snaps the highlight back to
        // entry [0].  If the user is reading entry #5 and a new
        // improvement lands, their reading position is yanked away.
        // We compare by Id (HistoryEntry record equality is by-value but
        // Id is the durable handle the store also uses for Remove).
        var previousSelectionId = SelectedEntry?.Id;

        // Refresh the unfiltered cache from the store, then re-apply
        // the current filter (which may be empty — in that case
        // ApplyFilter just copies _allEntries verbatim into Entries).
        _allEntries.Clear();
        _allEntries.AddRange(_store.Snapshot());
        ApplyFilter(previousSelectionId);

        OnPropertyChanged(nameof(HasEntries));
        ClearAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Rebuilds <see cref="Entries"/> from <see cref="_allEntries"/>
    /// using the current <see cref="FilterText"/>, then re-selects
    /// the entry with <paramref name="previousSelectionId"/> if it's
    /// still in the visible set (otherwise falls back to the first
    /// row, mirroring the LoadFromStore selection-preservation
    /// contract).
    ///
    /// Match logic: case-insensitive substring across Original /
    /// Improved / PromptText / Model.  We deliberately don't match
    /// on Timestamp or HotkeyKind — those don't read as searchable
    /// text from a user perspective.
    /// </summary>
    private void ApplyFilter(Guid? previousSelectionId = null)
    {
        var filter = FilterText?.Trim() ?? string.Empty;

        Entries.Clear();
        foreach (var entry in _allEntries)
        {
            if (filter.Length == 0 || EntryMatchesFilter(entry, filter))
            {
                Entries.Add(entry);
            }
        }

        // Re-select the previous entry if it survived the filter pass;
        // otherwise pick the first visible row so the detail pane is
        // never empty when there is something to display.  When we
        // haven't been given a hint (e.g. called from
        // OnFilterTextChanged), use the current SelectedEntry as the
        // anchor so a re-typed filter that still includes the
        // selected entry doesn't blow away the user's selection.
        var anchorId = previousSelectionId ?? SelectedEntry?.Id;
        HistoryEntry? toSelect = null;
        if (anchorId is { } id)
        {
            toSelect = Entries.FirstOrDefault(e => e.Id == id);
        }

        SelectedEntry = toSelect ?? (Entries.Count > 0 ? Entries[0] : null);
    }

    private static bool EntryMatchesFilter(HistoryEntry entry, string filter)
    {
        return Contains(entry.Original, filter)
            || Contains(entry.Improved, filter)
            || Contains(entry.PromptText, filter)
            || Contains(entry.Model, filter);

        // IDE0061 prefers block bodies for local functions over
        // expression bodies — keep the noisy null-or-empty +
        // contains check explicit.
        static bool Contains(string? haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        // Filter changed: re-evaluate Entries against _allEntries.
        // ApplyFilter takes care of re-selecting the user's current
        // entry if it survives the new filter, or falling back to the
        // first visible row if it doesn't.
        ApplyFilter();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasActiveFilter));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyOriginalAsync()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(SelectedEntry.Original);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Z5-F4 / H10 fix: surface to user instead of silently
            // leaving them with the old clipboard contents.
            _logger.LogWarning(ex, "Failed to copy original text from history");
            ShowCopyFailureToast();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyImprovedAsync()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        try
        {
            await _clipboard.SetTextAsync(SelectedEntry.Improved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to copy improved text from history");
            ShowCopyFailureToast();
        }
    }

    private void ShowCopyFailureToast()
    {
        if (_notifications is null)
        {
            return;
        }

        try
        {
            _notifications.ShowError(_translator["msg_history_copy_failed"]);
        }
        catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogDebug(toastEx, "Failed to surface copy-failure toast");
        }
    }

    private void OnStoreFaulted(object? sender, HistoryStoreErrorEventArgs e)
    {
        // Z5-F3 / H9: surface persist/load failures via toast — pre-fix
        // they sat in Warning logs and the user had no idea their history
        // wasn't being saved. Marshal to UI thread because the Faulted
        // event may fire from the HistoryStore's persist timer thread.
        if (_notifications is null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        void Show()
        {
            try
            {
                _notifications.ShowError(_translator["msg_history_save_failed"]);
            }
            catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogDebug(toastEx, "Failed to surface history-save error toast");
            }
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Show();
        }
        else
        {
            _ = dispatcher.BeginInvoke(Show);
        }
    }

    /// <remarks>
    /// M15 (Z5-F7) note: single-row delete intentionally has NO
    /// confirmation dialog.  Modern mail / messaging clients
    /// (Gmail, Outlook, Slack) treat single-item deletes as low-cost
    /// reversible-via-Undo actions and skip the confirm to keep the
    /// hot path snappy.  The bulk <see cref="ClearAllCommand"/> uses
    /// <see cref="ConfirmDialog.Ask"/> because that's the truly
    /// destructive variant.  Documented so a future contributor
    /// reviewing the pattern doesn't add a confirm for consistency.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelected()
    {
        if (SelectedEntry is null)
        {
            return;
        }

        _store.Remove(SelectedEntry.Id);
        // Store change event will refresh the collection.
    }

    [RelayCommand(CanExecute = nameof(HasEntries))]
    private void ClearAll()
    {
        var confirmed = ConfirmDialog.Ask(
            title: _translator["history_confirm_clear_title"],
            body: _translator["history_confirm_clear_body"],
            confirmText: _translator["history_btn_clear_all"],
            owner: Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive));

        if (confirmed == true)
        {
            _store.Clear();
        }
    }

    private bool HasSelection => SelectedEntry is not null;

    partial void OnSelectedEntryChanged(HistoryEntry? value)
    {
        OnPropertyChanged(nameof(OriginalText));
        OnPropertyChanged(nameof(ImprovedText));
        OnPropertyChanged(nameof(PromptText));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(FormattedTime));

        CopyOriginalCommand.NotifyCanExecuteChanged();
        CopyImprovedCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        // The store is the authoritative source; it can be mutated from
        // a TextProcessor running on a threadpool thread. Marshal back
        // to the UI thread before touching the bound collection.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(LoadFromStore);
        }
        else
        {
            LoadFromStore();
        }
    }
}
