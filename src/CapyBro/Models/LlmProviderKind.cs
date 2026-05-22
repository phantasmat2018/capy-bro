using System.Text.Json.Serialization;

namespace CapyBro.Models;

/// <summary>
/// Which LLM backend <see cref="Services.TextProcessor"/> sends
/// the user's text to.  OpenRouter (cloud) is the default for new and
/// upgrading installs — keeps existing behaviour stable through the v14
/// → v15 schema bump.  Ollama (local) is the privacy-first alternative:
/// the user runs <c>ollama serve</c> on their own machine, points
/// CapyBro at it, and no text leaves the box.
///
/// <para>
/// Persisted by name via <see cref="JsonStringEnumConverter{T}"/> (same
/// shape as <see cref="Language"/>) so a hand-edited config is readable
/// and a future rename of these C# identifiers wouldn't silently break
/// existing users — the on-disk string is the source of truth.  Append
/// new providers; do not reorder.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LlmProviderKind>))]
public enum LlmProviderKind
{
    OpenRouter = 0,
    Ollama = 1,
}
