namespace CapyBro.Models;

/// <summary>
/// Snapshot of an OpenRouter account's credit state, returned by
/// <c>GET /api/v1/credits</c>. Used by the experimental credits/cost
/// feature to surface the remaining balance in the General tab.
/// </summary>
public sealed record CreditsInfo
{
    /// <summary>Total credits ever loaded into the account, in USD.</summary>
    public required decimal TotalCredits { get; init; }

    /// <summary>Total spent so far, in USD.</summary>
    public required decimal TotalUsage { get; init; }

    /// <summary>Remaining balance, computed; never negative.</summary>
    public decimal Remaining => Math.Max(0m, TotalCredits - TotalUsage);
}

/// <summary>
/// Per-token USD pricing for a single OpenRouter model. Returned by
/// <c>GET /api/v1/models</c>'s nested <c>pricing</c> object. We only need
/// prompt + completion rates for the rough cost estimator.
/// </summary>
public sealed record ModelPricing
{
    /// <summary>USD per token for input (system + user messages).</summary>
    public required decimal PromptUsdPerToken { get; init; }

    /// <summary>USD per token for output (the model's response).</summary>
    public required decimal CompletionUsdPerToken { get; init; }
}
