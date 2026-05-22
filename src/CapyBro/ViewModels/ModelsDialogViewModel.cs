using System.Collections.ObjectModel;
using System.ComponentModel;

using CapyBro.Services;

using CommunityToolkit.Mvvm.ComponentModel;

using Microsoft.Extensions.Logging;

namespace CapyBro.ViewModels;

public sealed partial class ModelsDialogViewModel : ObservableObject, IDisposable
{
    private readonly IOpenRouterClient _client;
    private readonly ICredentialStore _credentials;
    private readonly ITranslator _translator;
    private readonly ILogger<ModelsDialogViewModel> _logger;
    private readonly List<string> _allModels = [];

    // Z7-F3 / M19: source-of-truth for StatusMessage so a mid-session
    // language switch can re-resolve through _translator.  Null when the
    // current StatusMessage is either empty or a raw string (e.g.
    // OpenRouterException.Message) that doesn't map to a Translator key.
    private (string Key, object[]? Args)? _statusSource;

    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<string> _models = [];

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Z9-F1 / M24: true when the user has typed a non-whitespace filter,
    /// false otherwise.  XAML uses this to choose between the "no
    /// matches — adjust your filter" and the "catalogue is empty"
    /// empty-state overlays.  IsNullOrWhiteSpace mirrors what
    /// <see cref="ApplyFilter"/> uses internally so the two stay in
    /// sync — a whitespace-only filter is treated as no filter and the
    /// overlay reflects that.
    /// </summary>
    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(Filter);

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ModelsDialogViewModel(
        IOpenRouterClient client,
        ICredentialStore credentials,
        ITranslator translator,
        ILogger<ModelsDialogViewModel> logger)
    {
        _client = client;
        _credentials = credentials;
        _translator = translator;
        _logger = logger;

        // Z7-F3 / M19: re-resolve StatusMessage when the user switches the
        // UI language while the dialog is open.  The transient lifetime of
        // this VM (ActivatorUtilities.CreateInstance per BrowseAsync —
        // see ModelBrowser.ShowDialogOnUiThread) means a missed unsubscribe
        // would keep the VM alive through Translator's strong-ref delegate;
        // IDisposable.Dispose detaches the handler in ModelBrowser's
        // finally block.
        _translator.PropertyChanged += OnTranslatorPropertyChanged;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        // Reentrancy guard: the dialog can theoretically be opened twice
        // before the first GetModelsAsync completes (e.g. user double-
        // clicks the "Browse models" button rapidly).  Two concurrent
        // LoadAsync calls would both mutate the plain-List `_allModels`
        // and race ApplyFilter, throwing InvalidOperationException out of
        // the LINQ enumeration ("Collection was modified") at best, or
        // corrupting List internal state at worst.  Cheap early-out:
        // ignore overlapping calls — the in-flight one's results land
        // for both users.
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        SetStatusEmpty();
        try
        {
            var apiKey = await _credentials.GetApiKeyAsync(ct) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetStatusKey("api_unauthorized");
                return;
            }

            var fetched = await _client.GetModelsAsync(apiKey, ct);
            _allModels.Clear();
            _allModels.AddRange(fetched.OrderBy(s => s, StringComparer.Ordinal));
            ApplyFilter();
        }
        catch (OpenRouterException ex)
        {
            SetStatusRaw(ex.Message);
            _logger.LogWarning(ex, "ModelsDialog: load failed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatusKey("api_unknown_error");
            _logger.LogWarning(ex, "ModelsDialog: load failed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _translator.PropertyChanged -= OnTranslatorPropertyChanged;
    }

    partial void OnFilterChanged(string value)
    {
        // Z9-F1 / M24: HasActiveFilter is a derived property; CommunityToolkit
        // [ObservableProperty] doesn't auto-raise PropertyChanged for properties
        // it doesn't know about, so fire it manually whenever Filter mutates.
        OnPropertyChanged(nameof(HasActiveFilter));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(Filter)
            ? _allModels
            : _allModels.Where(m => m.Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToList();

        // M18 (Z6-F5) fix: mutate in place rather than reassigning a new
        // ObservableCollection on every keystroke.  Reassignment forced
        // the CollectionViewSource to regenerate the grouped view from
        // scratch (re-running ProviderPrefixConverter for every entry)
        // and made SelectedItem flicker between rebuilds.  Clear + Add
        // keeps the WPF binding pipeline's incremental change path.
        Models.Clear();
        foreach (var m in filtered)
        {
            Models.Add(m);
        }

        // Empty catalogue: either a successful fetch returned `{"data":[]}`
        // OR LoadAsync threw and already set a meaningful error status
        // (api_unauthorized / api_unknown_error / OpenRouterException
        // message).  In the error case we DO NOT overwrite — the user
        // needs to see why the catalogue is unavailable.  M16 (Z6-F3)
        // fix: when the status is genuinely empty (successful fetch,
        // empty payload) we surface a dedicated message so the dialog
        // is distinguishable from "still loading" / "filter has no matches".
        if (_allModels.Count == 0)
        {
            if (string.IsNullOrEmpty(StatusMessage))
            {
                SetStatusKey("msg_models_catalogue_empty");
            }

            return;
        }

        // Catalogue has data — the status reflects the CURRENT filter
        // state regardless of what was shown before.  Pre-fix the
        // "Models.Count > 0" branch was gated on IsNullOrEmpty(StatusMessage),
        // so once a previous filter had set "no matches", typing a
        // different filter that DID match left the stale "no matches"
        // message visible underneath the populated list — visually
        // contradictory ("nothing found" + visible google/openai rows).
        // Always re-evaluate here.
        if (Models.Count == 0)
        {
            SetStatusKey("msg_models_search_empty");
        }
        else
        {
            SetStatusFormat("msg_models_loaded", _allModels.Count);
        }
    }

    // Z7-F3 / M19 — keyed setter that records the source so a later
    // language switch can re-resolve.  Eagerly resolves to the current
    // locale so any caller reading StatusMessage immediately sees the
    // expected text.
    private void SetStatusKey(string key)
    {
        _statusSource = (key, null);
        StatusMessage = _translator[key];
    }

    private void SetStatusFormat(string key, params object[] args)
    {
        _statusSource = (key, args);
        StatusMessage = _translator.Format(key, args);
    }

    // Raw-text setter for messages that don't map to a Translator key
    // (e.g. OpenRouterException.Message which is already built from the
    // HTTP status + server body).  Language switches leave these alone.
    private void SetStatusRaw(string raw)
    {
        _statusSource = null;
        StatusMessage = raw;
    }

    private void SetStatusEmpty()
    {
        _statusSource = null;
        StatusMessage = string.Empty;
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

        if (_statusSource is { } src)
        {
            StatusMessage = src.Args is null
                ? _translator[src.Key]
                : _translator.Format(src.Key, src.Args);
        }
    }
}
