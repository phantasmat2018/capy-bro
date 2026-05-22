using System.Text.Json.Serialization;

namespace CapyBro.Models;

[JsonSerializable(typeof(HistoryFile))]
[JsonSerializable(typeof(HistoryEntry))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
public partial class HistoryFileJsonContext : JsonSerializerContext
{
}
