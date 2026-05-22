using System.Text.Json.Serialization;

namespace CapyBro.Models;

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(Prompt))]
[JsonSerializable(typeof(DefaultPromptSlotSettings))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
public partial class AppConfigJsonContext : JsonSerializerContext
{
}
