using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// UI gateway for the diff-preview modal. Abstracted so TextProcessor can
/// be exercised in unit tests without spinning up a real WPF window — tests
/// inject a fake that returns a canned <see cref="DiffPreviewOutcome"/>.
/// </summary>
public interface IDiffPreviewService
{
    /// <summary>
    /// Opens the side-by-side diff modal and blocks until the user picks
    /// Accept / Reject / Regenerate (or closes the window — equivalent to
    /// Reject). Honours <paramref name="ct"/>: if cancelled the modal is
    /// closed and the call throws <see cref="OperationCanceledException"/>.
    ///
    /// v21: returns a <see cref="DiffPreviewOutcome"/> instead of a bare
    /// verdict so callers can pick up the user-edited "Improved" text
    /// when they used the Edit-view toggle to manually tweak the LLM
    /// output.  On Accept the outcome's FinalImproved holds the edited
    /// (or unedited) result; on Reject/Regenerate it carries the latest
    /// state but is typically ignored by the caller.
    /// </summary>
    Task<DiffPreviewOutcome> ShowAsync(string original, string improved, CancellationToken ct = default);
}

/// <summary>
/// What the user chose AND the text they walked out with.  The verdict
/// drives the TextProcessor flow (paste / cancel / regenerate); the
/// <see cref="FinalImproved"/> is the text to paste on Accept (may differ
/// from the original <c>improved</c> argument if the user edited it
/// in-place via the Diff↔Edit toggle).
/// </summary>
public sealed record DiffPreviewOutcome(DiffPreviewResult Verdict, string FinalImproved);
