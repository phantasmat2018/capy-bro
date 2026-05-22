using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CapyBro.Controls;

/// <summary>
/// Sidebar tab-button per project_design_guide.md §7.8. Hosts a
/// Button internally so keyboard navigation + UI Automation come for
/// free; exposes Icon (Geometry), Label (string), IsSelected (bool),
/// Command, and CommandParameter as DPs so callers can bind in XAML.
///
/// The "active tab" indicator (3 px BrandPrimary strip + Layer2
/// background) is driven by IsSelected via a DataTrigger inside the
/// inner Button's ControlTemplate. Hover/focus also tint the row to
/// Layer2 — matching the §7.7 list-item idiom so a selected sidebar
/// row visually rhymes with a selected list row in the content pane.
/// </summary>
public partial class SidebarTabButton : UserControl
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(Geometry),
            typeof(SidebarTabButton),
            new PropertyMetadata(default(Geometry)));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(SidebarTabButton),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(SidebarTabButton),
            new PropertyMetadata(false));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(SidebarTabButton),
            new PropertyMetadata(default(ICommand)));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(
            nameof(CommandParameter),
            typeof(object),
            typeof(SidebarTabButton),
            new PropertyMetadata(default(object)));

    public SidebarTabButton()
    {
        InitializeComponent();
    }

    public Geometry? Icon
    {
        get => (Geometry?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }
}
