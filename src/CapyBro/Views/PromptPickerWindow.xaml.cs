using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using CapyBro.Models;
using CapyBro.Platform;

namespace CapyBro.Views;

public partial class PromptPickerWindow : Window
{
    private readonly IReadOnlyDictionary<string, Prompt> _options;
    private bool _isClosing;
    private DispatcherTimer? _foregroundPoller;
    private IntPtr _selfHwnd;

    /// <summary>
    /// True when the picker is summoned in Ollama-provider mode.
    /// XAML binds a small "Ollama" pill's Visibility to this so the
    /// user can confirm at a glance which backend will execute the
    /// chosen prompt — same indicator pattern as the Settings sidebar
    /// footer and the tray tooltip.  Defaults to false; passed by the
    /// PromptPicker service which reads AppConfig.Provider from the
    /// store.
    /// </summary>
    public bool IsOllamaProvider { get; }

    // Foreground-poller guard: SetForegroundWindow has Win32 restrictions
    // (only the foreground-eligible thread can grant focus to its own
    // window) and CAN silently fail when the picker is summoned from a
    // hotkey in a context where another app holds activation lock.
    // Without this latch, the very first poller tick (100 ms after Show)
    // would see foreground != _selfHwnd and instantly dismiss the picker.
    // We require the picker to have been the real foreground window at
    // least once before "lost foreground" becomes a meaningful dismiss
    // signal.
    private bool _wasForegroundOnce;

    public PromptPickerWindow(IReadOnlyDictionary<string, Prompt> options, bool isOllamaProvider = false)
    {
        ArgumentNullException.ThrowIfNull(options);

        // IsOllamaProvider MUST be assigned before InitializeComponent
        // so the XAML binding (ElementName=Self) reads the true value
        // when the visual tree is built — the property is one-way and
        // has no INotifyPropertyChanged, so a post-InitializeComponent
        // set leaves the binding stuck on the default false and the
        // pill never appears.
        IsOllamaProvider = isOllamaProvider;
        InitializeComponent();
        _options = options;
        PromptList.ItemsSource = options.Keys.ToList();

        // Single-click commit. Use AddHandler(handledEventsToo:true) so we still receive the
        // bubble even though ListBoxItem marks the event handled while updating selection.
        PromptList.AddHandler(
            MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnListMouseUp),
            handledEventsToo: true);

        // Focus-keeper: a click on the picker's chrome (title TextBlock,
        // keyboard-hint footer, border padding) is inside the window but
        // not on any focusable element, so it drifts focus from the
        // ListBox and breaks arrow-key navigation.  PreviewMouseDown
        // (with handledEventsToo) fires before the click's default focus
        // behaviour; we refocus the ListBox unconditionally for clicks
        // that aren't on an actual ListBoxItem.
        AddHandler(
            PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseDownInsidePicker),
            handledEventsToo: true);

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            // Stop the foreground-window poller so it doesn't keep
            // ticking on a closed window, attempting to call Close()
            // again.  Idempotent via _isClosing but cheaper to just stop.
            if (_foregroundPoller is not null)
            {
                _foregroundPoller.Stop();
                _foregroundPoller = null;
            }
        };
        KeyDown += OnKeyDown;
        Deactivated += (_, _) =>
        {
            // Cross-process focus-loss path: Alt+Tab, Win key, or the OS
            // shell stealing foreground.  The foreground-window poller is
            // the primary dismiss path for "user clicked elsewhere"; this
            // Deactivated branch is the keyboard-shortcut backup that
            // fires earlier than the next 100 ms poll tick.
            //
            // Guard so the closing-cascade (Close → Deactivated → Close → …)
            // doesn't recurse, and so an external close (timeout / ct)
            // doesn't trip a second one.
            if (_isClosing || !IsVisible)
            {
                return;
            }

            _isClosing = true;
            Close();
        };
    }

    public Prompt? SelectedPrompt { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusSelectedItemContainer();

        _selfHwnd = new WindowInteropHelper(this).Handle;

        // Make sure we ARE the foreground window so subsequent clicks
        // elsewhere are detected as "we lost foreground" rather than "we
        // never had it."  SetForegroundWindow has Win32 restrictions
        // around which thread can call it, but the calling context here
        // is the hotkey handler which is allowed by Windows to bring its
        // own UI forward (AttachThreadInput / RegisterHotKey ancestry).
        //
        // The call CAN still fail silently (returns false) in edge cases
        // — see the _wasForegroundOnce guard in OnForegroundPollerTick.
        if (_selfHwnd != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(_selfHwnd);
        }

        Activate();

        // Defensive: if OnLoaded somehow re-fires (theme reload, content
        // template re-instantiation, etc.), stop the previous poller
        // before replacing the field reference — a Tick'd DispatcherTimer
        // keeps itself rooted in the dispatcher's scheduled-events list
        // and would otherwise leak one timer per re-Load.
        if (_foregroundPoller is not null)
        {
            _foregroundPoller.Stop();
            _foregroundPoller.Tick -= OnForegroundPollerTick;
        }

        // Foreground-window poller — the catch-all that handles every
        // cross-process click the WPF input subsystem can't see.  Every
        // 100 ms we ask Win32 "who is the foreground window right now?"
        // If it's no longer ours, the user clicked into some other app
        // (Notepad++, browser, desktop's empty area, OR a same-process
        // sibling window like Settings) — dismiss.
        //
        // 100 ms is well below human perception of "menu lingered" but
        // well above the 16 ms WM_PAINT cadence so we don't burn CPU.
        //
        // Why polling instead of Mouse.Capture: earlier iteration used
        // Mouse.Capture(this, SubTree) + PreviewMouseDownOutsideCapturedElement
        // for same-process dismiss.  That pattern broke ListBox click-
        // commit because ListBox grabs Mouse.Capture internally to track
        // its own click-drag selection — our capture got stolen, the
        // LostMouseCapture handler closed the window BEFORE the user's
        // MouseLeftButtonUp reached OnListMouseUp, and single-click
        // commit silently failed.  Polling sidesteps the capture-
        // ownership tug-of-war entirely.
        _foregroundPoller = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _foregroundPoller.Tick += OnForegroundPollerTick;
        _foregroundPoller.Start();
    }

    /// <summary>
    /// Parks keyboard focus on the SelectedItem's container so the next
    /// ↑/↓ press moves selection without an intermediate "transfer focus
    /// from ListBox shell to selected item" tap.  Called on Loaded AND
    /// from the chrome-click handler so a stray click on the title /
    /// footer / border doesn't sabotage subsequent keyboard navigation.
    /// </summary>
    private void FocusSelectedItemContainer()
    {
        if (PromptList.Items.Count == 0)
        {
            PromptList.Focus();
            return;
        }

        if (PromptList.SelectedIndex < 0)
        {
            PromptList.SelectedIndex = 0;
        }

        // UpdateLayout() forces the ItemContainerGenerator to materialise
        // containers immediately rather than waiting for the next render
        // pass — important because Loaded fires before the first arrange
        // pass on some XAML configurations (depends on whether the
        // ListBox virtualises its items).
        PromptList.UpdateLayout();

        if (PromptList.ItemContainerGenerator.ContainerFromIndex(PromptList.SelectedIndex) is ListBoxItem container)
        {
            container.Focus();
        }
        else
        {
            // Container generation deferred (e.g. virtualising panel
            // not yet materialised) — fall back to the ListBox itself
            // so the user can still type ↓/↑, even if the first press
            // costs them an extra tap.  In practice the picker uses a
            // non-virtualising StackPanel for ~8 prompts so this
            // branch never fires in real use.
            PromptList.Focus();
        }
    }

    private void OnForegroundPollerTick(object? sender, EventArgs e)
    {
        if (_isClosing || !IsVisible || _selfHwnd == IntPtr.Zero)
        {
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == _selfHwnd)
        {
            // Latch: now that we've seen ourselves as the real Win32
            // foreground window at least once, subsequent ticks that
            // observe foreground != self are legitimate dismiss signals.
            _wasForegroundOnce = true;
            return;
        }

        // We're NOT the foreground.  Whether this counts as "user
        // clicked elsewhere" depends on whether we ever WERE the
        // foreground.  If SetForegroundWindow in OnLoaded failed
        // silently (Win32 restrictions around which thread can grant
        // foreground), the picker is on-screen but never had Win32
        // activation.  Dismissing on the first tick in that case would
        // surface as "picker flashes for 100 ms then vanishes" — the
        // worst possible UX.
        //
        // Wait until we've been foreground at least once; only then is
        // "lost foreground" a meaningful dismiss signal.
        if (!_wasForegroundOnce)
        {
            return;
        }

        // Some other window owns foreground — could be a sibling WPF
        // window in this process (Settings, History) or an external app.
        // Either way, the picker isn't where the user is looking anymore;
        // dismiss as cancelled.
        _isClosing = true;
        DialogResult = false;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e is null)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_isClosing)
            {
                return;
            }

            _isClosing = true;
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            CommitSelection();
        }
        else if (e.Key == Key.Tab)
        {
            // Trap Tab / Shift+Tab.  The picker is a leaf popover —
            // there's no "next focusable element" the user could
            // meaningfully cycle to (chrome elements aren't focusable,
            // and Tab inside the ListBox would shove focus into the
            // window's logical-focus chain which can park it on
            // non-list elements that don't react to ↑↓ / Enter).
            // Re-anchor focus on the currently-selected item so the
            // user can keep navigating with the documented keys.
            FocusSelectedItemContainer();
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            // Home / End are common keyboard shortcuts users expect on
            // any list popover (Slack channel picker, VS Code command
            // palette, browser address bar suggestions).  ListBox's
            // built-in handlers cover these BUT only when focus is on
            // a ListBoxItem; routing them here means Home/End work
            // even when the Tab handler above (or some other event)
            // has parked focus on the ListBox shell.
            if (PromptList.Items.Count > 0)
            {
                PromptList.SelectedIndex = 0;
                FocusSelectedItemContainer();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.End)
        {
            if (PromptList.Items.Count > 0)
            {
                PromptList.SelectedIndex = PromptList.Items.Count - 1;
                FocusSelectedItemContainer();
                e.Handled = true;
            }
        }
    }

    private void OnPreviewMouseDownInsidePicker(object sender, MouseButtonEventArgs e)
    {
        // Walk from the originating element up the visual tree.  If we
        // find a ListBoxItem, leave focus alone — the ListBox's own
        // mouse-down logic owns selection.  If we hit the root without
        // finding one, the click was on chrome (title / footer / border).
        // Refocus the SelectedItem container (NOT the ListBox shell) so
        // the next ↑/↓ press moves selection on the first tap — same
        // contract as OnLoaded.  Pre-fix this branch called
        // `PromptList.Focus()` which re-introduced the "first ↓ doesn't
        // move selection" bug after any chrome click.
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not ListBoxItem)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        if (node is null)
        {
            FocusSelectedItemContainer();
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Only commit if the mouse went up over an actual ListBoxItem (not the empty area
        // below the last entry, scrollbar, etc.). Walk the visual tree from the originating
        // element until we find a ListBoxItem.
        var node = e.OriginalSource as DependencyObject;
        while (node is not null and not ListBoxItem)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        if (node is ListBoxItem)
        {
            CommitSelection();
        }
    }

    private void CommitSelection()
    {
        if (_isClosing)
        {
            return;
        }

        if (PromptList.SelectedItem is string key && _options.TryGetValue(key, out var prompt))
        {
            _isClosing = true;
            SelectedPrompt = prompt;
            DialogResult = true;
            Close();
        }
    }
}
