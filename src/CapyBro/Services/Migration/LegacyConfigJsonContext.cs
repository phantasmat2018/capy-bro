using System.Text.Json.Serialization;

namespace CapyBro.Services.Migration;

[JsonSerializable(typeof(LegacyAppConfig))]
[JsonSerializable(typeof(LegacyPrompt))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class LegacyConfigJsonContext : JsonSerializerContext
{
}
