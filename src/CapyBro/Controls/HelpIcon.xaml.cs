using System.Windows;
using System.Windows.Controls;

namespace CapyBro.Controls;

/// <summary>
/// Inline "?" affordance for a tooltip-discoverable hint.  Pair next
/// to a section heading or field label where the meaning isn't
/// obvious from the label alone — the icon signals "hover for help"
/// the way every modern app's settings pane signals it.
///
/// Single binding surface: set <see cref="HintText"/> from XAML.  The
/// control wires that string straight into <c>ToolTip</c> on the hit
/// area, which our <c>Themes/Tooltips.xaml</c> implicit Style renders
/// as a dark, wrapped, multi-line popup automatically.  No special
/// tooltip plumbing required at the call site.
/// </summary>
public partial class HelpIcon : UserControl
{
    public static readonly DependencyProperty HintTextProperty = DependencyProperty.Register(
        nameof(HintText),
        typeof(string),
        typeof(HelpIcon),
        new PropertyMetadata(string.Empty));

    public HelpIcon()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Hint copy shown in the tooltip when the user hovers the icon.
    /// Bind to a localised string via Translator.Instance — the
    /// content is the visible help text the user reads, so it MUST
    /// flow through the localisation table rather than being a
    /// hard-coded literal.
    /// </summary>
    public string HintText
    {
        get => (string)GetValue(HintTextProperty);
        set => SetValue(HintTextProperty, value);
    }
}
