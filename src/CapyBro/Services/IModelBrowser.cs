namespace CapyBro.Services;

public interface IModelBrowser
{
    /// <summary>
    /// Shows the model-picker dialog (loads OpenRouter catalogue, lets user filter and pick).
    /// Returns the selected model id or null if the user cancelled.
    /// </summary>
    Task<string?> BrowseAsync(CancellationToken ct = default);
}
