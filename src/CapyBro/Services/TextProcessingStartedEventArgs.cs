namespace CapyBro.Services;

/// <summary>
/// Carries pre-request data to <see cref="TextProcessor.ProcessingStarted"/>
/// subscribers — the optional cost estimate plus the effective model id.
/// Wrapping in a typed args record (instead of a bare EventArgs.Empty)
/// lets the toast host (App.xaml.cs) format the "Обробка..." message with
/// a cost suffix when the user has the credits/cost experiment enabled,
/// and to surface the model id so the user can confirm at a glance which
/// model is currently processing (especially useful when the Pro
/// model-switch hotkey is in play).
/// </summary>
public sealed class TextProcessingStartedEventArgs : EventArgs
{
    public TextProcessingStartedEventArgs(decimal? estimatedCostUsd, string effectiveModel)
    {
        EstimatedCostUsd = estimatedCostUsd;
        EffectiveModel = effectiveModel;
    }

    /// <summary>
    /// Rough USD estimate for this request, or null if estimation is
    /// disabled (master flag off) or unavailable (no pricing for the
    /// model). UI should hide the cost portion of the toast when null.
    /// </summary>
    public decimal? EstimatedCostUsd { get; }

    /// <summary>
    /// The resolved model id for THIS run — OpenRouter slug
    /// (e.g. <c>"openai/gpt-4o"</c>) or Ollama tag
    /// (e.g. <c>"gemma3:latest"</c>) depending on the active provider.
    /// Empty when the model resolution failed upstream (in which case
    /// the toast just omits the suffix; the failure path raises a
    /// separate Failed event with the actionable error).
    /// </summary>
    public string EffectiveModel { get; }
}
