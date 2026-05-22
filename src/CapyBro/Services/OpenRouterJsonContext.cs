using System.Text.Json.Serialization;

namespace CapyBro.Services;

[JsonSerializable(typeof(OpenRouterChatRequest))]
[JsonSerializable(typeof(OpenRouterChatResponse))]
[JsonSerializable(typeof(OpenRouterChatStreamChunk))]
[JsonSerializable(typeof(OpenRouterModelsResponse))]
[JsonSerializable(typeof(OpenRouterCreditsResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class OpenRouterJsonContext : JsonSerializerContext
{
}
