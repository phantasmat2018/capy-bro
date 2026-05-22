using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// UI gateway for the diff-preview modal. Abstracted so TextProcessor can
/// be exercised in unit tests without spinning up a real WPF window — tests
/// inject a fake that returns a canned <see cref="DiffPreviewResult"/>.
/// </summary>
public interface IDiffPreviewService
{
    /// <summary>
    /// Opens the side-by-side diff modal and blocks until the user picks
    /// Accept / Reject / Regenerate (or closes the window — equivalent to
    /// Reject). Honours <paramref name="ct"/>: if cancelled the modal is
    /// closed and the call throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<DiffPreviewResult> ShowAsync(string original, string improved, CancellationToken ct = default);
}
