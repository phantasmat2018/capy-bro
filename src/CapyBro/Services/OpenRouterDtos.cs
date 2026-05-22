namespace CapyBro.Services;

internal sealed record OpenRouterChatRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<OpenRouterMessage> Messages { get; init; }

    /// <summary>
    /// When true, OpenRouter responds with a Server-Sent Events stream of
    /// chat-completion chunks (one delta per token-ish unit) instead of a
    /// single buffered JSON. We always set this true so the UI can show
    /// live progress and the user can cancel mid-generation.
    /// </summary>
    public bool Stream { get; init; }
}

internal sealed record OpenRouterMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

internal sealed record OpenRouterChatResponse
{
    public IReadOnlyList<OpenRouterChoice>? Choices { get; init; }
}

internal sealed record OpenRouterChoice
{
    public OpenRouterMessage? Message { get; init; }
}

internal sealed record OpenRouterModelsResponse
{
    public IReadOnlyList<OpenRouterModelInfo>? Data { get; init; }
}

internal sealed record OpenRouterModelInfo
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    /// <summary>
    /// Per-token USD prices, expressed as JSON strings (OpenRouter's
    /// chosen format — they preserve full precision rather than risking
    /// floating-point round-trip on tiny numbers like 0.000_000_15). Both
    /// fields are optional; free models report null.
    /// </summary>
    public OpenRouterPricing? Pricing { get; init; }
}

internal sealed record OpenRouterPricing
{
    public string? Prompt { get; init; }

    public string? Completion { get; init; }
}

internal sealed record OpenRouterCreditsResponse
{
    public OpenRouterCreditsData? Data { get; init; }
}

internal sealed record OpenRouterCreditsData
{
    public decimal? TotalCredits { get; init; }

    public decimal? TotalUsage { get; init; }
}

// Streaming-mode response chunks. OpenRouter sends one of these per
// "data: {...}" SSE frame; the final frame is a literal "data: [DONE]"
// (no JSON), handled separately by the parser. Schema differs from the
// buffered response: choices have `delta` (incremental piece) instead of
// `message` (full content).
internal sealed record OpenRouterChatStreamChunk
{
    public IReadOnlyList<OpenRouterStreamChoice>? Choices { get; init; }
}

internal sealed record OpenRouterStreamChoice
{
    public OpenRouterStreamDelta? Delta { get; init; }

    public string? FinishReason { get; init; }
}

internal sealed record OpenRouterStreamDelta
{
    public string? Content { get; init; }
}
