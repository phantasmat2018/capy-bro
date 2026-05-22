using System.Text.Json.Serialization;

namespace CapyBro.Services;

[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatStreamChunk))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class OllamaJsonContext : JsonSerializerContext
{
}
