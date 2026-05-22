using System.Text.Json.Serialization;

namespace CapyBro.Models;

// H3 (Z2-F3) fix: English declared first so `default(Language) == 0 ==
// English` aligns with the documented post-rebrand default.  Pre-fix the
// order was UA-RU-EN — matching the team's working locale during
// "AI Text Improver" development — but it silently set the language of
// any config (e.g. hand-edited, partial migration) that lacked a
// `language` key to Ukrainian.  Strings on the wire are unaffected
// because `JsonStringEnumConverter<Language>` (de)serializes by name,
// so v2 configs roundtrip cleanly across the flip.
[JsonConverter(typeof(JsonStringEnumConverter<Language>))]
public enum Language
{
    English,
    Ukrainian,
    Russian,
}
