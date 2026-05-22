namespace CapyBro.Services;

/// <summary>
/// Carries pre-request data to <see cref="TextProcessor.ProcessingStarted"/>
/// subscribers — currently just the optional cost estimate. Wrapping in a
/// typed args record (instead of a bare EventArgs.Empty) lets the toast
/// host (App.xaml.cs) format the "Обробка..." message with a cost
/// suffix when the user has the credits/cost experiment enabled.
/// </summary>
public sealed class TextProcessingStartedEventArgs : EventArgs
{
    public TextProcessingStartedEventArgs(decimal? estimatedCostUsd)
    {
        EstimatedCostUsd = estimatedCostUsd;
    }

    /// <summary>
    /// Rough USD estimate for this request, or null if estimation is
    /// disabled (master flag off) or unavailable (no pricing for the
    /// model). UI should hide the cost portion of the toast when null.
    /// </summary>
    public decimal? EstimatedCostUsd { get; }
}
