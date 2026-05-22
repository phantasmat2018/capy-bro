using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// OpenRouter-specific surface — extends <see cref="ILlmProvider"/> with
/// the cloud-only endpoints (credits balance + per-token pricing) that
/// Ollama has no analogue for.  Code paths that genuinely need OpenRouter
/// (CostEstimator, the General-tab balance widget, the API-key validation
/// probe) keep typing on this interface; everything else (TextProcessor,
/// ModelsDialog, the wizard) talks to <see cref="ILlmProvider"/> via
/// <see cref="ILlmProviderFactory"/> so it works for whichever backend
/// the user picked.
/// </summary>
public interface IOpenRouterClient : ILlmProvider
{
    /// <summary>
    /// Fetches the OpenRouter account's credit balance via
    /// <c>GET /api/v1/credits</c>. Used by the experimental
    /// credits-and-cost feature; not called when that flag is off.
    /// Throws <see cref="OpenRouterException"/> for non-2xx (e.g.
    /// invalid API key); transport errors map to localized messages.
    /// </summary>
    Task<CreditsInfo> GetCreditsAsync(
        string apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches the full models catalogue including per-token pricing.
    /// Returned as a (modelId → pricing) map for fast estimator lookup.
    /// Models that report null pricing (e.g. free tier) are simply absent
    /// from the dictionary; callers must handle missing keys gracefully.
    /// </summary>
    Task<IReadOnlyDictionary<string, ModelPricing>> GetModelsWithPricingAsync(
        string apiKey,
        CancellationToken ct = default);
}
