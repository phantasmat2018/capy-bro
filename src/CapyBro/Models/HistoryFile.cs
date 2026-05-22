namespace CapyBro.Models;

/// <summary>
/// On-disk envelope for the history list. Wrapping in a record (instead of
/// serializing a bare List&lt;HistoryEntry&gt;) lets us add metadata fields later
/// (schema version, encryption flag, total-count cap) without breaking
/// forward compatibility.
/// </summary>
public sealed record HistoryFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<HistoryEntry> Entries { get; init; } = [];
}
