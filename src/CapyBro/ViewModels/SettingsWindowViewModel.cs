using System.ComponentModel;
using System.Windows;

using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Views;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

namespace CapyBro.ViewModels;

public sealed partial class SettingsWindowViewModel : ObservableObject
{
    private readonly IConfigStore _configStore;
    private readonly ICredentialStore _credentials;
    private readonly ITranslator _translator;
    private readonly INotificationService _notifications;
    private readonly ILogger<SettingsWindowViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralSelected))]
    [NotifyPropertyChangedFor(nameof(IsPromptsSelected))]
    [NotifyPropertyChangedFor(nameof(IsHistorySelected))]
    private object _selectedTab;

    public SettingsWindowViewModel(
        GeneralTabViewModel generalTab,
        PromptsTabViewModel promptsTab,
        HistoryViewModel historyTab,
        IConfigStore configStore,
        ICredentialStore credentials,
        ITranslator translator,
        INotificationService notifications,
        ILogger<SettingsWindowViewModel> logger)
    {
        GeneralTab = generalTab;
        PromptsTab = promptsTab;
        HistoryTab = historyTab;
        _configStore = configStore;
        _credentials = credentials;
        _translator = translator;
        _notifications = notifications;
        _logger = logger;
        _selectedTab = generalTab;

        // History sidebar visibility tracks the v11 ExperimentalHistory
        // flag.  GeneralTab is the authoritative source for the flag —
        // it owns the bound CheckBox and persists the change.  When the
        // user toggles it OFF while the History tab is currently
        // selected, snap back to General so the content area is not
        // showing a tab whose sidebar entry just disappeared.
        GeneralTab.PropertyChanged += OnGeneralTabPropertyChanged;
    }

    public GeneralTabViewModel GeneralTab { get; }

    public PromptsTabViewModel PromptsTab { get; }

    public HistoryViewModel HistoryTab { get; }

    /// <summary>
    /// True when the general tab is the active content. Drives the
    /// SidebarTabButton.IsSelected indicator strip on the General tab
    /// row in the SettingsWindow sidebar.
    /// </summary>
    public bool IsGeneralSelected => ReferenceEquals(SelectedTab, GeneralTab);

    /// <summary>
    /// True when the prompts tab is the active content. Drives the
    /// SidebarTabButton.IsSelected indicator strip on the Prompts tab
    /// row in the SettingsWindow sidebar.
    /// </summary>
    public bool IsPromptsSelected => ReferenceEquals(SelectedTab, PromptsTab);

    /// <summary>
    /// True when the history tab is the active content. Drives the
    /// SidebarTabButton.IsSelected indicator strip on the History tab.
    /// </summary>
    public bool IsHistorySelected => ReferenceEquals(SelectedTab, HistoryTab);

    /// <summary>
    /// Mirrors <see cref="GeneralTabViewModel.ExperimentalHistory"/> so
    /// the SettingsWindow XAML can bind the History
    /// <c>SidebarTabButton.Visibility</c> to it.  When false, the
    /// sidebar row is hidden and the only way to re-enable it is via
    /// General → Experimental → "History" toggle, mirroring the rollout
    /// pattern of the other Experimental flags.
    /// </summary>
    public bool IsHistoryAvailable => GeneralTab.ExperimentalHistory;

    // v15: every sidebar-tab click triggers a fresh Ollama probe so
    // the Provider card's visibility reflects the live `ollama serve`
    // state without a background poll.  Fire-and-forget — the probe's
    // in-flight guard collapses overlap with any in-progress call.
    [RelayCommand]
    private void ShowGeneral()
    {
        SelectedTab = GeneralTab;
        _ = GeneralTab.RefreshOllamaAvailabilityAsync();
    }

    [RelayCommand]
    private void ShowPrompts()
    {
        SelectedTab = PromptsTab;
        _ = GeneralTab.RefreshOllamaAvailabilityAsync();
    }

    [RelayCommand]
    private void ShowHistory()
    {
        SelectedTab = HistoryTab;
        _ = GeneralTab.RefreshOllamaAvailabilityAsync();
    }

    private void OnGeneralTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GeneralTabViewModel.ExperimentalHistory))
        {
            return;
        }

        OnPropertyChanged(nameof(IsHistoryAvailable));

        // If the user just turned History off while looking at it,
        // bounce them to General so the content pane isn't orphaned.
        if (!GeneralTab.ExperimentalHistory && ReferenceEquals(SelectedTab, HistoryTab))
        {
            SelectedTab = GeneralTab;
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var confirmed = ConfirmDialog.Ask(
            _translator["confirm_reset_title"],
            _translator["confirm_reset_body"],
            _translator["reset_settings"],
            owner);

        if (confirmed != true)
        {
            return;
        }

        try
        {
            // §4.4: clear config + credential, but DO NOT touch the Run-key autostart entry.
            await _configStore.SaveAsync(AppConfig.Default);
            await _credentials.DeleteApiKeyAsync();

            await GeneralTab.LoadFromConfigAsync();
            await PromptsTab.LoadAsync();

            _logger.LogInformation("Settings reset to defaults (autostart registry left untouched)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Z2-F5 / M4 fix: surface the failure to the user. Pre-fix
            // ResetAsync just LogWarning'd and the user saw nothing — the
            // UI reloaded from the unchanged on-disk state and they
            // assumed Reset "didn't do anything" rather than "Reset failed".
            _logger.LogWarning(ex, "Reset failed");
            try
            {
                _notifications.ShowError(_translator["msg_reset_failed"]);
            }
            catch (Exception toastEx) when (toastEx is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogDebug(toastEx, "Failed to surface reset error toast");
            }
        }
    }
}
