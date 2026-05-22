using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CapyBro.Controls;

/// <summary>
/// WPF-rendered title bar strip used in tandem with
/// <see cref="System.Windows.Shell.WindowChrome"/> on the host Window.
/// We render the chrome ourselves because Win11 22H2 (build 22621)
/// silently ignores DwmSetWindowAttribute caption-theming requests
/// from WPF apps (verified via diagnostic logging — DWM returns
/// S_OK but the caption stays in OS default light).
///
/// DPs:
///   <see cref="Title"/>      — text on the left of the strip
///   <see cref="ShowMinMax"/> — minimise + maximise/restore buttons.
///                              Defaults to true; set false on
///                              fixed-size dialogs (Confirm, Models,
///                              PromptPicker).
///   <see cref="ShowTitle"/>  — hides the title TextBlock when the
///                              host paints its own wordmark
///                              elsewhere (SettingsWindow has the
///                              logo + wordmark in its sidebar).
///
/// Click handlers route through <see cref="SystemCommands"/> so the
/// usual Win32 minimise / maximise / restore semantics apply
/// (animation, AeroSnap, etc.).
/// </summary>
public partial class WindowCaption : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(WindowCaption),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowMinMaxProperty =
        DependencyProperty.Register(
            nameof(ShowMinMax),
            typeof(bool),
            typeof(WindowCaption),
            new PropertyMetadata(true, OnShowMinMaxChanged));

    public static readonly DependencyProperty ShowTitleProperty =
        DependencyProperty.Register(
            nameof(ShowTitle),
            typeof(bool),
            typeof(WindowCaption),
            new PropertyMetadata(true, OnShowTitleChanged));

    public WindowCaption()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ShowMinMax
    {
        get => (bool)GetValue(ShowMinMaxProperty);
        set => SetValue(ShowMinMaxProperty, value);
    }

    public bool ShowTitle
    {
        get => (bool)GetValue(ShowTitleProperty);
        set => SetValue(ShowTitleProperty, value);
    }

    private static void OnShowMinMaxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowCaption caption)
        {
            var visible = (bool)e.NewValue;
            caption.MinimizeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            caption.MaximizeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void OnShowTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowCaption caption)
        {
            caption.TitleText.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to host Window state changes so the maximise glyph
        // flips to the "restore" double-square when the user maximises
        // via AeroSnap or system menu.
        var window = Window.GetWindow(this);
        if (window is not null)
        {
            window.StateChanged += OnHostWindowStateChanged;
            UpdateMaximizeGlyph(window);
        }
    }

    private void OnHostWindowStateChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            UpdateMaximizeGlyph(window);
        }
    }

    private void UpdateMaximizeGlyph(Window window)
    {
        // Maximised: two overlapping squares (restore icon).
        // Otherwise: single square (maximise icon).
        MaximizeGlyph.Data = window.WindowState == WindowState.Maximized
            ? Geometry.Parse("M 2 0 L 10 0 L 10 8 L 8 8 M 0 2 L 8 2 L 8 10 L 0 10 Z")
            : Geometry.Parse("M 0 0 L 10 0 L 10 10 L 0 10 Z");
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is not null)
        {
            SystemCommands.MinimizeWindow(window);
        }
    }

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(window);
        }
        else
        {
            SystemCommands.MaximizeWindow(window);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is not null)
        {
            SystemCommands.CloseWindow(window);
        }
    }
}
