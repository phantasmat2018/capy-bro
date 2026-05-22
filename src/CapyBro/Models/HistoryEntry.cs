namespace CapyBro.Models;

/// <summary>
/// One row in the user's text-improvement history. Persisted to
/// ~/.ai_text_improver_v2_history.json and shown in the History window
/// so the user can review, copy back, or Undo a past replacement.
///
/// <para>
/// <b>Immutability contract</b> (Z5-F9 / L10).  This is a
/// <c>sealed record</c> with value-equality semantics on purpose — the
/// VM's `SelectedEntry = entry` setter is an `[ObservableProperty]` which
/// short-circuits the PropertyChanged notification when the new value
/// equals the old one by value.  `IHistoryStore` is therefore designed
/// around append + remove + clear, never in-place mutation.  If a future
/// feature wants to "edit" an entry, the contract is remove-then-add
/// (with a new Id), NOT a record-with-update mutation — that would
/// preserve the Id and let the equality short-circuit suppress the
/// detail-pane refresh (`OriginalText` / `ImprovedText` / etc. all
/// derive from `OnSelectedEntryChanged`).  Adding a "RaisePropertyChanged
/// even on value-equal write" path in the VM would close the door on
/// this trap if the design ever needs to evolve.
/// </para>
/// </summary>
public sealed record HistoryEntry
{
    /// <summary>
    /// Stable identifier so the UI can address entries individually
    /// (delete one, replay one) without depending on list index.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>UTC timestamp when the replacement completed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The selection the user originally had on screen, before the AI ran.</summary>
    public required string Original { get; init; }

    /// <summary>The text that the AI returned and we pasted back into the host app.</summary>
    public required string Improved { get; init; }

    /// <summary>The prompt body that was sent — useful for "what was this?" review.</summary>
    public required string PromptText { get; init; }

    /// <summary>The OpenRouter model id that produced the result.</summary>
    public required string Model { get; init; }

    /// <summary>
    /// Which hotkey kind triggered the run (Default = direct, Menu = picker, Undo).
    /// Stored as int to keep the JSON stable across enum reordering.
    /// Z5-F1 / C8: also exposed as a typed glyph + label below so the
    /// History list-item template can paint a small chip telling the user
    /// which hotkey produced any given entry.  Pre-fix this field was
    /// decorative — written to disk, never read.
    /// </summary>
    public required int HotkeyKind { get; init; }

    /// <summary>
    /// The single-character glyph shown next to the timestamp on each
    /// History row: ⌘ for Default (direct hotkey), ☰ for Menu (picker),
    /// ↶ for Undo. Maps from <see cref="HotkeyKind"/>; safe-fallback to
    /// blank for unknown integer values.
    /// </summary>
    public string HotkeyKindGlyph => HotkeyKind switch
    {
        0 => "⌘", // ⌘ Default
        1 => "☰", // ☰ Menu
        2 => "↶", // ↶ Undo
        _ => string.Empty,
    };
}
