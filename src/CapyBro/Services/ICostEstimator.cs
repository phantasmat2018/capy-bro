namespace CapyBro.Services;

/// <summary>
/// Pre-request rough USD-cost estimator for an OpenRouter chat call.
/// Caches model pricing in memory with a TTL so the toast suffix doesn't
/// add an extra network hop per hotkey press. Used by the experimental
/// credits-and-cost feature only — when the master flag is off, callers
/// should not invoke this at all (don't pay for /models calls until the
/// user opts in).
/// </summary>
public interface ICostEstimator
{
    /// <summary>
    /// Estimates the USD cost of an upcoming chat completion. Returns null
    /// when pricing for <paramref name="modelId"/> is unknown (free model,
    /// fetch failure, or model id absent from /models). Never throws —
    /// the toast just omits the suffix in that case.
    /// </summary>
    Task<decimal?> EstimateAsync(
        string apiKey,
        string modelId,
        string inputText,
        CancellationToken ct = default);

    /// <summary>
    /// Drops any cached pricing snapshot. Called on API key change so a
    /// future estimate goes through a fresh fetch under the new key.
    /// </summary>
    void InvalidateCache();
}
