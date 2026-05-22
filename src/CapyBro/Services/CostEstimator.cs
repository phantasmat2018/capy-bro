using CapyBro.Models;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

internal sealed class CostEstimator : ICostEstimator
{
    /// <summary>
    /// Heuristic: tokens ≈ chars / 3. Tighter than the common English
    /// "chars/4" because the app's primary audience writes Cyrillic, where
    /// UTF-8 uses 2 bytes per char and the OpenAI tokenizer averages
    /// fewer chars per token. Conservative — likely overestimates slightly
    /// on pure-Latin input, which is fine for "rough estimate" UX.
    /// </summary>
    private const int CharsPerTokenHeuristic = 3;

    /// <summary>
    /// Output tokens assumed proportional to input — most prompts in this
    /// app rephrase / fix / translate, so output tracks input length
    /// roughly. 2× is a defensive overestimate — better to surprise the
    /// user with a smaller actual bill than the reverse.
    /// </summary>
    private const decimal OutputTokenMultiplier = 2m;

    /// <summary>
    /// Pricing snapshot lives this long before we re-fetch. OpenRouter
    /// pricing changes rarely (days/weeks); 1 h gives the user fresh
    /// numbers within a session without hammering /models on every
    /// hotkey press.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly IOpenRouterClient _client;
    private readonly ILogger<CostEstimator> _logger;
    private readonly object _gate = new();
    private readonly TimeProvider _time;

    private IReadOnlyDictionary<string, ModelPricing>? _cachedPricing;
    private DateTimeOffset _cachedAt;

    public CostEstimator(
        IOpenRouterClient client,
        ILogger<CostEstimator> logger)
        : this(client, logger, TimeProvider.System)
    {
    }

    // Test ctor — lets tests advance time without thread.sleep'ing.
    internal CostEstimator(
        IOpenRouterClient client,
        ILogger<CostEstimator> logger,
        TimeProvider time)
    {
        _client = client;
        _logger = logger;
        _time = time;
    }

    public async Task<decimal?> EstimateAsync(
        string apiKey,
        string modelId,
        string inputText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(modelId)
            || string.IsNullOrWhiteSpace(inputText))
        {
            return null;
        }

        var pricing = await GetPricingAsync(apiKey, ct);
        if (pricing is null || !pricing.TryGetValue(modelId, out var modelPricing))
        {
            return null;
        }

        // Token estimate: char count / heuristic divisor.
        var inputTokens = Math.Max(1m, inputText.Length / (decimal)CharsPerTokenHeuristic);
        var outputTokens = inputTokens * OutputTokenMultiplier;

        return (inputTokens * modelPricing.PromptUsdPerToken)
             + (outputTokens * modelPricing.CompletionUsdPerToken);
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cachedPricing = null;
            _cachedAt = default;
        }
    }

    private async Task<IReadOnlyDictionary<string, ModelPricing>?> GetPricingAsync(
        string apiKey,
        CancellationToken ct)
    {
        // Hot path: cache hit, no lock needed for the read because
        // assignment of the dictionary reference is atomic on .NET.
        var now = _time.GetUtcNow();
        var cached = _cachedPricing;
        if (cached is not null && now - _cachedAt < CacheTtl)
        {
            return cached;
        }

        try
        {
            var fresh = await _client.GetModelsWithPricingAsync(apiKey, ct);
            lock (_gate)
            {
                _cachedPricing = fresh;
                _cachedAt = now;
            }

            return fresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Estimator must never fail user-visible flows. Log + return
            // null so the toast just omits the suffix.
            _logger.LogWarning(ex, "Cost estimator failed to fetch pricing — suppressing cost suffix");
            return null;
        }
    }
}
