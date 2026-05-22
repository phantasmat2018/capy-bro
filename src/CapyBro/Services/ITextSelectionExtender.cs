namespace CapyBro.Services;

/// <summary>
/// Re-selects the just-pasted text in the foreground editor so the
/// user sees what changed and can immediately copy / extend / delete
/// without re-selecting manually.  Implementations layer multiple
/// strategies (UI Automation TextPattern primary, synthesised
/// <c>Shift+Left</c> fallback) to maximise coverage across the broad
/// app surface this utility runs against — modern accessibility-aware
/// apps typically support TextPattern; legacy / custom controls fall
/// back to the keyboard path.
/// </summary>
public interface ITextSelectionExtender
{
    /// <summary>
    /// Extends the current selection backwards by <paramref name="charCount"/>
    /// UTF-16 code units, treating the caret's current position as the
    /// anchor's end.  No-op when the count is zero or negative.  Best
    /// effort: implementations log and return without raising on any
    /// failure mode — the calling pipeline (see
    /// <see cref="TextProcessor"/>) treats an unsuccessful selection as
    /// a cosmetic regression, never a correctness one.
    /// </summary>
    Task ExtendBackwardAsync(int charCount, CancellationToken ct = default);
}
