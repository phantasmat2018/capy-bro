using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace CapyBro.Controls;

/// <summary>
/// PasswordBox + plaintext TextBox swap with an eye-toggle, per
/// project_design_guide.md §7.4. Exposes a single
/// <see cref="Password"/> DP that callers bind two-way.
///
/// Sync model: both inputs write through the same <see cref="Password"/>
/// DP. PasswordBox.PasswordChanged + TextBox.TextChanged each push
/// into Password; OnPasswordPropertyChanged pushes back into whichever
/// input isn't currently authoring. An <see cref="_isUpdating"/> flag
/// breaks the round-trip so a programmatic write doesn't ping-pong.
///
/// The toggle is intentionally not focus-stealing (it's its own tab
/// stop), and the masked input keeps focus when toggling — cmd+A,
/// home/end, paste behave the same regardless of reveal state.
/// </summary>
public partial class RevealablePasswordBox : UserControl
{
    public static readonly DependencyProperty PasswordProperty =
        DependencyProperty.Register(
            nameof(Password),
            typeof(string),
            typeof(RevealablePasswordBox),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnPasswordPropertyChanged));

    /// <summary>
    /// Number of consecutive eye-toggle clicks the user must make to
    /// fire <see cref="SecretSequenceTriggered"/>.  Hidden gesture for
    /// the developer-mode unlock, intentionally undiscoverable without
    /// external knowledge.  Symmetric: the same gesture also re-locks
    /// dev mode, so an accidental unlock can be undone the same way.
    /// </summary>
    private const int SecretSequenceLength = 20;

    /// <summary>
    /// Maximum gap between consecutive clicks while the sequence is
    /// being entered.  Five seconds is short enough that ordinary
    /// password-revealing usage (one click to peek, one click to
    /// re-mask) doesn't accidentally accumulate, but long enough that
    /// a deliberate user can complete the sequence comfortably.
    /// Stopwatch is monotonic, so an NTP step or system-clock change
    /// can't break the counter.
    /// </summary>
    private static readonly TimeSpan SecretSequenceWindow = TimeSpan.FromSeconds(5);

    private readonly Stopwatch _sinceLastClick = new();
    private bool _isUpdating;
    private int _consecutiveClicks;

    /// <summary>
    /// Fires after the user clicks the eye toggle exactly
    /// <see cref="SecretSequenceLength"/> times in succession with
    /// gaps no longer than <see cref="SecretSequenceWindow"/>.  The
    /// counter resets on emit so a follow-up sequence can fire the
    /// event again — used by the GeneralTab to flip developer mode
    /// on / off symmetrically.
    /// </summary>
    public event EventHandler? SecretSequenceTriggered;

    public RevealablePasswordBox()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the current password value. Two-way binding
    /// suitable for MVVM (PasswordBox itself is intentionally not
    /// bindable in WPF; this DP wraps the sync between masked and
    /// plain inputs).
    /// </summary>
    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (RevealablePasswordBox)d;
        if (control._isUpdating)
        {
            return;
        }

        var newValue = (e.NewValue as string) ?? string.Empty;

        control._isUpdating = true;
        try
        {
            // Push into both inputs so flipping reveal mid-edit shows
            // the same value either way. The hidden one's text is
            // never displayed; we update it lazily so binding stays
            // consistent without triggering a focus-loss reformat.
            if (!string.Equals(control.MaskedInput.Password, newValue, StringComparison.Ordinal))
            {
                control.MaskedInput.Password = newValue;
            }

            if (!string.Equals(control.PlainInput.Text, newValue, StringComparison.Ordinal))
            {
                control.PlainInput.Text = newValue;
            }
        }
        finally
        {
            control._isUpdating = false;
        }
    }

    private void OnPasswordBoxChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        SyncFromInput(MaskedInput.Password);
    }

    private void OnTextBoxChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        SyncFromInput(PlainInput.Text);
    }

    private void SyncFromInput(string value)
    {
        _isUpdating = true;
        try
        {
            // Reflect into Password DP so two-way bindings see the
            // change. The other input's value will be brought into
            // sync the next time it becomes visible (or immediately
            // if a binding callback fires OnPasswordPropertyChanged).
            if (!string.Equals(Password, value, StringComparison.Ordinal))
            {
                Password = value;
            }

            // Mirror across to keep both inputs current — covers the
            // case where reveal flips before any external binding has
            // a chance to round-trip.
            if (!string.Equals(MaskedInput.Password, value, StringComparison.Ordinal))
            {
                MaskedInput.Password = value;
            }

            if (!string.Equals(PlainInput.Text, value, StringComparison.Ordinal))
            {
                PlainInput.Text = value;
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void OnRevealChanged(object sender, RoutedEventArgs e)
    {
        // RevealToggle.IsChecked drives glyph swap via the template's
        // IsChecked trigger; here we just flip Visibility.
        if (RevealToggle.IsChecked == true)
        {
            MaskedInput.Visibility = Visibility.Collapsed;
            PlainInput.Visibility = Visibility.Visible;
            PlainInput.Focus();
            PlainInput.CaretIndex = PlainInput.Text.Length;
        }
        else
        {
            PlainInput.Visibility = Visibility.Collapsed;
            MaskedInput.Visibility = Visibility.Visible;
            MaskedInput.Focus();
        }

        TrackSecretSequence();
    }

    /// <summary>
    /// Counts consecutive eye-toggle clicks that arrive within
    /// <see cref="SecretSequenceWindow"/> of each other.  When the
    /// count reaches <see cref="SecretSequenceLength"/> the
    /// <see cref="SecretSequenceTriggered"/> event fires and the
    /// counter resets; any pause longer than the window also resets,
    /// so ordinary peek-and-hide usage (which never reaches 20 in a
    /// row) cannot accidentally trigger the gesture.
    /// </summary>
    private void TrackSecretSequence()
    {
        // Reset the counter if the user paused too long between clicks
        // — typical password-reveal interactions are well under 5 s
        // apart for a deliberate user, so a longer gap is a strong
        // signal that this isn't an in-progress sequence.
        if (_sinceLastClick.IsRunning && _sinceLastClick.Elapsed > SecretSequenceWindow)
        {
            _consecutiveClicks = 0;
        }

        _consecutiveClicks++;
        _sinceLastClick.Restart();

        if (_consecutiveClicks >= SecretSequenceLength)
        {
            // Reset before raising so a re-entrant subscriber
            // (unlikely, but possible if a handler chains another
            // toggle) starts from a clean count rather than firing
            // twice on the same click stream.
            _consecutiveClicks = 0;
            _sinceLastClick.Reset();
            SecretSequenceTriggered?.Invoke(this, EventArgs.Empty);
        }
    }
}
