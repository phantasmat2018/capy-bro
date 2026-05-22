using System.Runtime.InteropServices;

using CapyBro.Platform;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

public sealed class InputSimulator : IInputSimulator
{
    private readonly ILogger<InputSimulator> _logger;

    public InputSimulator(ILogger<InputSimulator> logger)
    {
        _logger = logger;
    }

    public Task SendCopyAsync(CancellationToken ct = default)
    {
        // Honour caller cancellation before we synthesize key events.
        // SendInput itself is synchronous and uncancellable (Win32
        // doesn't expose a cancellation hook), but checking ct here
        // means a cancel that arrived before the call doesn't waste
        // a SendInput round-trip + risk leaving the user's keyboard
        // state perturbed if processing is being torn down.
        ct.ThrowIfCancellationRequested();
        SendKeyCombination(NativeMethods.VkControl, NativeMethods.VkInsert, "copy (Ctrl+Insert)");
        return Task.CompletedTask;
    }

    public Task SendPasteAsync(CancellationToken ct = default)
    {
        // Was Shift+Insert per PROMPT.md §4.2 (chosen to avoid Ctrl+V
        // conflicts with custom shortcuts), but several modern Win11
        // apps (Settings, Mail, some browsers) silently ignore the
        // legacy combo while still honouring Ctrl+V. The clipboard
        // contained the AI result but the selected text never got
        // replaced. Ctrl+V is the modern universal paste — every
        // app from Notepad to Word to WinUI surfaces handles it.
        ct.ThrowIfCancellationRequested();
        SendKeyCombination(NativeMethods.VkControl, NativeMethods.VkV, "paste (Ctrl+V)");
        return Task.CompletedTask;
    }

    public async Task SendSelectBackwardAsync(int charCount, CancellationToken ct = default)
    {
        // Negative or zero is a legitimate "nothing was pasted, nothing
        // to select" state — most importantly the Undo path can hand us
        // an empty entry.Original.Length when the user undid an edit
        // that had no preceding text.  Just no-op.
        if (charCount <= 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();
        await SendShiftLeftBurstAsync(charCount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Pumps <c>Shift+Left × count</c> through <c>SendInput</c> in small
    /// batches with brief inter-batch yields so the just-pasted text
    /// re-selects in the foreground editor without overwhelming the
    /// app's input handler.
    ///
    /// Three things this method tries hard to NOT do, learned from
    /// user-reported glitches on the previous oversized-burst version:
    ///
    ///   1. NOT slam the input queue.  Old version sent 200 keypresses
    ///      (≈400 INPUT events) in one SendInput call.  Apps with
    ///      synchronous text-input handlers (browsers, RichEdit
    ///      surfaces in Office, native VS code editor) couldn't
    ///      service that fast enough and either dropped half the
    ///      events or coalesced them into auto-repeats — selection
    ///      ended up arbitrary.  New batch is 25 keypresses with a
    ///      5 ms yield between batches; that's still well under
    ///      1 ms/char total wall time but gives the target app
    ///      multiple message-pump cycles per batch.
    ///
    ///   2. NOT over-extend.  Old cap was 10 000 chars; lowered to
    ///      2 000.  Above 2 000 the visual benefit is minimal — the
    ///      user can't see most of the selection anyway in a normal
    ///      window — and the cumulative synthesised-input load
    ///      starts to interact badly with auto-correct / spell-
    ///      check / IME handlers in heavy editors.  Above the cap
    ///      we silently skip (better than partial-selection
    ///      glitches).
    ///
    ///   3. NOT leave Shift stuck.  If a SendInput call delivers
    ///      fewer events than requested (UIPI block, BlockInput,
    ///      kiosk lock screen…) we forcibly send a Shift-up so the
    ///      user's next typed key isn't interpreted as Shifted.
    /// </summary>
    private async Task SendShiftLeftBurstAsync(int count, CancellationToken ct)
    {
        const int MaxChars = 2_000;
        const int BatchSize = 25;
        var interBatchDelay = TimeSpan.FromMilliseconds(5);

        // Above the cap we skip entirely rather than partially
        // selecting — a half-selected document-scale rewrite is
        // more confusing than no selection at all.  The user
        // typically reaches for Ctrl+A / mouse drag for that scale
        // anyway, so we're not blocking any reasonable workflow.
        if (count > MaxChars)
        {
            _logger.LogDebug(
                "Skipping post-paste re-selection — pasted text {Count} chars exceeds {Cap}-char cap",
                count,
                MaxChars);
            return;
        }

        var size = Marshal.SizeOf<NativeMethods.INPUT>();

        for (var sent = 0; sent < count; sent += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var thisBatch = Math.Min(BatchSize, count - sent);

            // Layout per batch:
            //   [0]            Shift down
            //   [1..1+2N-1]    (Left down, Left up) × thisBatch
            //   [^1]           Shift up
            // The single Shift down/up around the burst (instead of one
            // Shift down/up per Left key) is the standard "modifier
            // pre-applied to a stream" pattern — it cuts SendInput
            // events by ~half and the OS reliably re-asserts the Shift
            // state for every Left key while it's held down.
            var inputs = new NativeMethods.INPUT[2 + (thisBatch * 2)];
            inputs[0] = MakeKeyInput(NativeMethods.VkShift, keyDown: true);
            for (var i = 0; i < thisBatch; i++)
            {
                inputs[1 + (i * 2)] = MakeKeyInput(NativeMethods.VkLeft, keyDown: true);
                inputs[1 + (i * 2) + 1] = MakeKeyInput(NativeMethods.VkLeft, keyDown: false);
            }

            inputs[^1] = MakeKeyInput(NativeMethods.VkShift, keyDown: false);

            var actuallySent = NativeMethods.SendInput((uint)inputs.Length, inputs, size);
            if (actuallySent != inputs.Length)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "SendInput selection delivered {Sent}/{Expected} events; aborting burst (Win32 error {Error})",
                    actuallySent,
                    inputs.Length,
                    error);

                // Stuck-modifier recovery: try a Shift up so the user's
                // next typed key isn't interpreted as Shifted.
                if (actuallySent >= 1)
                {
                    var release = new[] { MakeKeyInput(NativeMethods.VkShift, keyDown: false) };
                    NativeMethods.SendInput((uint)release.Length, release, size);
                }

                return;
            }

            // Yield to the OS message pump between batches.  Without
            // this, even apps with healthy input handlers drop events
            // when ~10 batches arrive in <1 ms — Win32 doesn't queue
            // much beyond ~250 events per app message-pump cycle.
            // The yield adds bounded latency: at 25 chars/batch +
            // 5 ms/yield, a 100-char selection takes 4×5=20 ms total,
            // imperceptible to the user but visible to the OS as
            // separate input bursts.
            if (sent + thisBatch < count)
            {
                await Task.Delay(interBatchDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private void SendKeyCombination(ushort modifierVk, ushort keyVk, string label)
    {
        var inputs = new NativeMethods.INPUT[4];

        inputs[0] = MakeKeyInput(modifierVk, keyDown: true);
        inputs[1] = MakeKeyInput(keyVk, keyDown: true);
        inputs[2] = MakeKeyInput(keyVk, keyDown: false);
        inputs[3] = MakeKeyInput(modifierVk, keyDown: false);

        var size = Marshal.SizeOf<NativeMethods.INPUT>();
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, size);
        if (sent != inputs.Length)
        {
            // Common causes: UIPI blocking input to a higher-integrity window, another
            // process holding BlockInput, or kiosk/lockscreen state. Result text is in
            // clipboard but won't paste — surface as a warning so the user sees something
            // is wrong instead of a silent no-op.
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning(
                "SendInput delivered {Sent}/{Expected} events for {Label}; Win32 error {Error}",
                sent,
                inputs.Length,
                label,
                error);

            // Stuck-modifier recovery: if SendInput succeeded for the
            // modifier-down (sent ≥ 1) but failed before the modifier-up
            // (sent < 4), the OS sees Ctrl as held and the user's next
            // letter key is interpreted as a shortcut.  Defensively force
            // a key-up for the modifier alone so the keyboard state
            // returns to neutral.  Worst-case this fires a redundant
            // "Ctrl up" against an already-released Ctrl, which is a no-op
            // for every app we tested.  Best-case it unsticks the modifier
            // before the user even notices.
            if (sent >= 1 && sent < 4)
            {
                var release = new[] { MakeKeyInput(modifierVk, keyDown: false) };
                var releaseSent = NativeMethods.SendInput((uint)release.Length, release, size);
                if (releaseSent != release.Length)
                {
                    _logger.LogWarning(
                        "Stuck-modifier recovery for {Label} could not release modifier (sent {Sent}/1)",
                        label,
                        releaseSent);
                }
            }
        }
    }

    private static NativeMethods.INPUT MakeKeyInput(ushort vk, bool keyDown) => new()
    {
        Type = NativeMethods.InputKeyboard,
        U = new NativeMethods.INPUTUNION
        {
            Keyboard = new NativeMethods.KEYBDINPUT
            {
                Vk = vk,
                Scan = 0,
                Flags = keyDown ? 0u : NativeMethods.KeyEventfKeyUp,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };
}
