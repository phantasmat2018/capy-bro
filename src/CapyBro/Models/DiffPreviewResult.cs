namespace CapyBro.Models;

/// <summary>
/// Outcome of the side-by-side diff modal that opt-in prompts show before
/// committing the AI result to the host application.
/// </summary>
public enum DiffPreviewResult
{
    /// <summary>User accepted the result — proceed to paste + record history.</summary>
    Accept,

    /// <summary>
    /// User rejected the result — restore original clipboard, do not paste,
    /// do not record history. No "Done" toast (this is a user-cancelled run).
    /// </summary>
    Reject,

    /// <summary>
    /// User wants another attempt — re-call the model with the same prompt
    /// and original text, then re-show the preview with the new result.
    /// </summary>
    Regenerate,
}
