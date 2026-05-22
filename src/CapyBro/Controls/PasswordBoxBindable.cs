using System.Windows;
using System.Windows.Controls;

namespace CapyBro.Controls;

/// <summary>
/// Attached-property helper that lets MVVM bindings read/write WPF's PasswordBox.Password,
/// which is intentionally non-bindable in the framework. Usage in XAML:
///   &lt;PasswordBox controls:PasswordBoxBindable.BoundPassword="{Binding ApiKey, Mode=TwoWay,
///       UpdateSourceTrigger=PropertyChanged}" controls:PasswordBoxBindable.Attach="True" /&gt;
/// </summary>
public static class PasswordBoxBindable
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxBindable),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    public static readonly DependencyProperty AttachProperty =
        DependencyProperty.RegisterAttached(
            "Attach",
            typeof(bool),
            typeof(PasswordBoxBindable),
            new PropertyMetadata(false, OnAttachChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBindable),
            new PropertyMetadata(false));

    public static string GetBoundPassword(DependencyObject dp)
    {
        ArgumentNullException.ThrowIfNull(dp);
        return (string)dp.GetValue(BoundPasswordProperty);
    }

    public static void SetBoundPassword(DependencyObject dp, string value)
    {
        ArgumentNullException.ThrowIfNull(dp);
        dp.SetValue(BoundPasswordProperty, value);
    }

    public static bool GetAttach(DependencyObject dp)
    {
        ArgumentNullException.ThrowIfNull(dp);
        return (bool)dp.GetValue(AttachProperty);
    }

    public static void SetAttach(DependencyObject dp, bool value)
    {
        ArgumentNullException.ThrowIfNull(dp);
        dp.SetValue(AttachProperty, value);
    }

    private static bool GetIsUpdating(DependencyObject dp) =>
        (bool)dp.GetValue(IsUpdatingProperty);

    private static void SetIsUpdating(DependencyObject dp, bool value) =>
        dp.SetValue(IsUpdatingProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox passwordBox)
        {
            return;
        }

        if (!GetIsUpdating(passwordBox))
        {
            passwordBox.Password = e.NewValue as string ?? string.Empty;
        }
    }

    private static void OnAttachChanged(DependencyObject dp, DependencyPropertyChangedEventArgs e)
    {
        if (dp is not PasswordBox passwordBox)
        {
            return;
        }

        if ((bool)e.OldValue)
        {
            passwordBox.PasswordChanged -= OnPasswordChanged;
        }

        if ((bool)e.NewValue)
        {
            passwordBox.PasswordChanged += OnPasswordChanged;
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        SetIsUpdating(passwordBox, true);
        SetBoundPassword(passwordBox, passwordBox.Password);
        SetIsUpdating(passwordBox, false);
    }
}
