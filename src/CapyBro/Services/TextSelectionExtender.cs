using System.Windows.Automation;
using System.Windows.Automation.Text;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

/// <summary>
/// Two-strategy implementation of <see cref="ITextSelectionExtender"/>.
///
/// Strategy 1 (preferred): <b>UI Automation TextPattern</b>.
/// Modern accessibility-aware apps — every browser, Office, WinUI /
/// WPF / WinForms text controls, native Edit / RichEdit, VS / VSCode —
/// expose a <see cref="TextPattern"/> on their focused element.  We
/// query the selection (which after paste is collapsed at the end of
/// the inserted text), <c>Clone</c> it, and move the start endpoint
/// backward by <see cref="TextUnit.Character"/> × <c>charCount</c>.
/// The resulting range is then <c>Select()</c>-ed.  This path operates
/// at the semantic level — line endings, surrogate pairs, IME
/// composition state, and grapheme clusters are all handled by the
/// editor itself — so it is fundamentally immune to the timing /
/// modifier-state glitches that plague the synthesised-key approach.
///
/// Strategy 2 (fallback): <b>synthesised Shift+Left burst</b>.
/// When UIA is unsupported (some custom-drawn controls, Win32 apps
/// with no accessibility surface, full-screen DirectX-rendered
/// editors) we degrade to <see cref="IInputSimulator.SendSelectBackwardAsync"/>.
/// Pre-burst guard: poll <see cref="IModifierReleaseWaiter"/> until
/// the user releases the hotkey modifiers — without this,
/// <c>Ctrl-still-held</c> turns the synthesised <c>Shift+Left</c>
/// into <c>Ctrl+Shift+Left</c> ("select previous WORD") and the
/// selection over- or under-shoots word boundaries unpredictably.
///
/// Both strategies are wrapped in defensive try / catch with logging;
/// failures here are cosmetic (no selection appears) and must never
/// affect the correctness of the paste itself.
/// </summary>
internal sealed class TextSelectionExtender : ITextSelectionExtender
{
    /// <summary>
    /// Hard cap on UIA work.  Cross-process accessibility calls block
    /// the calling thread; an unresponsive target app could otherwise
    /// hang us indefinitely.  500 ms is a comfortable upper bound for
    /// healthy apps (typical UIA round-trip is &lt;100 ms) without
    /// noticeably delaying the post-paste flow.
    /// </summary>
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Same wait the rest of <see cref="TextProcessor"/> uses for
    /// physical-modifier polling.  Re-imported as a constant here so
    /// the fallback path is self-contained and doesn't reach across
    /// component boundaries.
    /// </summary>
    private static readonly TimeSpan ModifierReleaseTimeout = TimeSpan.FromSeconds(2);

    private readonly IInputSimulator _input;
    private readonly IModifierReleaseWaiter _modifiers;
    private readonly ILogger<TextSelectionExtender> _logger;

    public TextSelectionExtender(
        IInputSimulator input,
        IModifierReleaseWaiter modifiers,
        ILogger<TextSelectionExtender> logger)
    {
        _input = input;
        _modifiers = modifiers;
        _logger = logger;
    }

    public async Task ExtendBackwardAsync(int charCount, CancellationToken ct = default)
    {
        if (charCount <= 0)
        {
            return;
        }

        // Try UIA first.  Returns true on a successful semantic
        // selection; false on "not supported" / "timed out" / "threw".
        // Either of the latter two falls through to the keyboard path.
        var uiaSucceeded = await TryExtendViaUiaAsync(charCount, ct).ConfigureAwait(false);
        if (uiaSucceeded)
        {
            _logger.LogDebug(
                "Selection extension via UIA succeeded ({Count} chars)",
                charCount);
            return;
        }

        // Fallback path.  See class-level XML doc for why the modifier
        // wait is critical here — without it, Ctrl held from the user's
        // hotkey reads as Ctrl+Shift+Left in the target app.
        await _modifiers.WaitForReleaseAsync(ModifierReleaseTimeout, ct).ConfigureAwait(false);
        if (_modifiers.IsAnyModifierDown())
        {
            _logger.LogDebug(
                "Skipping fallback selection — user still holds modifiers after {TimeoutMs}ms wait",
                ModifierReleaseTimeout.TotalMilliseconds);
            return;
        }

        _logger.LogDebug(
            "UIA path unavailable — falling back to Shift+Left synthesis ({Count} chars)",
            charCount);
        await _input.SendSelectBackwardAsync(charCount, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps the synchronous UIA call in a thread-pool task with a
    /// hard timeout.  AutomationElement.FocusedElement and TextPattern
    /// methods are blocking RPC calls into the focused app's UI thread;
    /// an unresponsive target would otherwise stall our paste pipeline.
    /// </summary>
    private async Task<bool> TryExtendViaUiaAsync(int charCount, CancellationToken ct)
    {
        // Linked CTS so user cancel + our timeout both abort the work.
        // The hand-off via Task.Run rebinds the continuation to the
        // threadpool — UIA hates being called from a sync-context-
        // captured thread.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UiaTimeout);

        try
        {
            return await Task.Run(() => TryExtendViaUiaSync(charCount), timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own UIA timeout fired — degrade to fallback.
            _logger.LogDebug("UIA selection extension timed out after {TimeoutMs}ms", UiaTimeout.TotalMilliseconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            // External cancel propagates as-is to the caller.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Any UIA exception (ElementNotAvailable, COMException from
            // a torn-down target window, InvalidOperationException from
            // a not-supported pattern) → silently fall back.  These are
            // routine in normal operation and not worth surfacing to
            // the user.
            _logger.LogDebug(ex, "UIA selection extension threw — will fall back");
            return false;
        }
    }

    /// <summary>
    /// Synchronous body of the UIA strategy.  Returns true only when
    /// the selection was actually re-applied; any condition that
    /// prevents that — no focused element, focused element doesn't
    /// support TextPattern, no current selection to anchor on, the
    /// move-endpoint failed to traverse the requested distance —
    /// returns false so the caller can fall back to the keyboard path.
    /// </summary>
    private static bool TryExtendViaUiaSync(int charCount)
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is null)
        {
            return false;
        }

        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var raw)
            || raw is not TextPattern pattern)
        {
            return false;
        }

        var selection = pattern.GetSelection();
        if (selection is null || selection.Length == 0)
        {
            return false;
        }

        // Clone the live selection range — the original is owned by
        // the target app and should not be mutated directly.  After a
        // paste the selection is collapsed (caret at end of pasted
        // text); our job is to move the start endpoint backward by
        // charCount characters, which expands the range to cover the
        // pasted region, then call Select() to apply.
        var range = selection[0].Clone();

        // MoveEndpointByUnit returns the actual unit count moved (may
        // be less than requested if we hit the document start).  We
        // accept any movement greater than zero — selecting "what we
        // could reach" is better than aborting outright when the
        // user pasted near the top of a small document.
        var moved = range.MoveEndpointByUnit(
            TextPatternRangeEndpoint.Start,
            TextUnit.Character,
            -charCount);

        if (moved == 0)
        {
            // Couldn't move at all — TextPattern reported success but
            // the range didn't change.  Possible on read-only views or
            // empty documents; falling back to keys won't help either,
            // but returning false at least keeps the contract clean.
            return false;
        }

        range.Select();
        return true;
    }
}
