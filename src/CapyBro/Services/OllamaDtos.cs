namespace CapyBro.Services;

/// <summary>
/// Wire-format mirrors of Ollama's REST API as documented at
/// https://github.com/ollama/ollama/blob/main/docs/api.md.  We only
/// model the fields we actually read or send; absent fields deserialize
/// to default (null / 0 / false), which is fine because Ollama is
/// open-source and the schema is stable enough that we don't need to
/// defend against surprise shape drift the way we do for OpenRouter
/// mid-deploy.
/// </summary>
internal sealed record OllamaChatRequest
{
    public required string Model { get; init; }

    public required IReadOnlyList<OllamaMessage> Messages { get; init; }

    /// <summary>
    /// Always true — we use the NDJSON streaming path uniformly so the UI
    /// gets per-chunk progress events and cancellation actually stops
    /// server-side generation. If false, Ollama returns a single buffered
    /// JSON; we never set that.
    /// </summary>
    public bool Stream { get; init; }
}

internal sealed record OllamaMessage
{
    public required string Role { get; init; }

    public required string Content { get; init; }
}

// Streaming-mode response chunks. Ollama sends one JSON object per line
// (NDJSON — newline-delimited JSON, *not* SSE).  The "done":true frame
// closes the stream; we don't read the timing/eval-count fields that
// final frame carries.
internal sealed record OllamaChatStreamChunk
{
    public OllamaMessage? Message { get; init; }

    public bool Done { get; init; }
}

internal sealed record OllamaTagsResponse
{
    public IReadOnlyList<OllamaModelInfo>? Models { get; init; }
}

internal sealed record OllamaModelInfo
{
    /// <summary>
    /// Full tag (e.g. <c>"llama3.2:latest"</c>, <c>"mistral:7b-instruct"</c>).
    /// We expose this verbatim as the model identifier — it's what
    /// <c>/api/chat</c> expects in the <c>model</c> field.
    /// </summary>
    public string? Name { get; init; }
}
