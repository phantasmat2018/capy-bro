using System.Windows.Controls;

namespace CapyBro.Views;

/// <summary>
/// In-place history surface inside SettingsWindow's sidebar tabs (next to
/// General + Prompts). Replaces the standalone HistoryWindow so the user
/// has a single, consistent shell for everything app-related — same
/// pattern as Win11 Settings (one window, sidebar tabs).
/// </summary>
public partial class HistoryTab : UserControl
{
    public HistoryTab()
    {
        InitializeComponent();
    }
}
