namespace CapyBro.Services;

/// <summary>
/// Carries the live streaming-AI accumulator state to subscribers of
/// <see cref="TextProcessor.ProcessingStreamUpdated"/>. Wraps a single
/// string instead of using <see cref="EventHandler{T}"/> with bare string
/// because CA1003 wants every event payload to derive from EventArgs (so
/// the event signature is forwards-compatible if we add fields later —
/// e.g. token count, finish reason — without breaking subscribers).
/// </summary>
public sealed class TextProcessingStreamUpdatedEventArgs : EventArgs
{
    public TextProcessingStreamUpdatedEventArgs(string accumulatedContent)
    {
        AccumulatedContent = accumulatedContent;
    }

    /// <summary>Full accumulated text so far (concatenation of all deltas).</summary>
    public string AccumulatedContent { get; }
}
