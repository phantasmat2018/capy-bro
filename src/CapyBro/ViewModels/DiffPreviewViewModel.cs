using System.Collections.ObjectModel;

using CapyBro.Models;

using CommunityToolkit.Mvvm.ComponentModel;

using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace CapyBro.ViewModels;

/// <summary>
/// Backs the side-by-side diff preview modal. Computes the line-level diff
/// once at construction (the user can't edit the texts), exposes two
/// per-line collections for binding, and stores the user's verdict for the
/// DiffPreviewService to read after ShowDialog returns.
/// </summary>
public sealed partial class DiffPreviewViewModel : ObservableObject
{
    public DiffPreviewViewModel(string original, string improved)
    {
        Original = original ?? string.Empty;
        Improved = improved ?? string.Empty;

        // SideBySideDiffBuilder pads each side with "Imaginary" lines so
        // OldLines[i] and NewLines[i] always describe the same visual row.
        // We render those padding lines as empty placeholders below; without
        // them, equal-length but content-shifted texts wouldn't align.
        var diff = SideBySideDiffBuilder.Diff(Original, Improved);

        // Number rows on each side independently.  An Imaginary row
        // (padding inserted by the differ to keep the two panes
        // aligned) gets no line number — the gutter renders empty
        // for that row, but the row still occupies a layout slot
        // so the side-by-side correlation is preserved.  Real rows
        // are numbered 1..N within their own pane.
        OriginalLines = new ObservableCollection<DiffLineRow>(
            NumberRows(diff.OldText.Lines));
        ImprovedLines = new ObservableCollection<DiffLineRow>(
            NumberRows(diff.NewText.Lines));

        // Stats: count Inserted/Deleted/Modified for the footer
        // chip strip.  Inserted only exists on the New side,
        // Deleted only on the Old side, Modified appears in both
        // (paired) — count once on the New side to avoid doubling.
        InsertedCount = ImprovedLines.Count(r => r.Kind == DiffLineKind.Inserted);
        DeletedCount = OriginalLines.Count(r => r.Kind == DiffLineKind.Deleted);
        ModifiedCount = ImprovedLines.Count(r => r.Kind == DiffLineKind.Modified);
    }

    public string Original { get; }

    public string Improved { get; }

    public ObservableCollection<DiffLineRow> OriginalLines { get; }

    public ObservableCollection<DiffLineRow> ImprovedLines { get; }

    /// <summary>
    /// Number of lines that exist only in the improved pane —
    /// surfaced as the green "+ N" chip in the diff stats footer.
    /// Computed once at construction; the diff is immutable post-
    /// construction so a one-shot count is safe.
    /// </summary>
    public int InsertedCount { get; }

    /// <summary>
    /// Number of lines that exist only in the original pane —
    /// surfaced as the red "- N" chip.
    /// </summary>
    public int DeletedCount { get; }

    /// <summary>
    /// Number of lines paired as "modified" between the two panes
    /// (same row index, different content) — surfaced as the
    /// amber "~ N" chip.
    /// </summary>
    public int ModifiedCount { get; }

    /// <summary>
    /// User's choice — set by the window code-behind when one of the three
    /// action buttons is clicked, or via window-close (treated as Reject).
    /// Default Reject means "if the modal is dismissed without an explicit
    /// choice we cancel the run rather than silently committing".
    /// </summary>
    public DiffPreviewResult Result { get; set; } = DiffPreviewResult.Reject;

    /// <summary>
    /// Yields rows in source order, assigning a 1-based line number
    /// to every non-Imaginary entry on this side.  Imaginary rows
    /// (alignment padding) get LineNumber=null so the XAML gutter
    /// renders an empty cell for them.  Counter is per-side, so the
    /// Original pane shows numbers 1..N where N is the line count
    /// of the user's clipboard text, and the Improved pane shows
    /// 1..M where M is the line count of the AI's response —
    /// independent of which rows are paired by the diff.
    /// </summary>
    private static IEnumerable<DiffLineRow> NumberRows(IEnumerable<DiffPiece> pieces)
    {
        var line = 0;
        foreach (var piece in pieces)
        {
            var isImaginary = piece.Type == ChangeType.Imaginary;
            int? lineNumber = null;
            if (!isImaginary)
            {
                line++;
                lineNumber = line;
            }

            yield return new DiffLineRow(
                Text: piece.Text ?? string.Empty,
                Kind: MapKind(piece.Type),
                IsImaginary: isImaginary,
                LineNumber: lineNumber);
        }
    }

    private static DiffLineKind MapKind(ChangeType type) => type switch
    {
        ChangeType.Inserted => DiffLineKind.Inserted,
        ChangeType.Deleted => DiffLineKind.Deleted,
        ChangeType.Modified => DiffLineKind.Modified,
        ChangeType.Imaginary => DiffLineKind.Imaginary,
        _ => DiffLineKind.Unchanged,
    };
}

public enum DiffLineKind
{
    Unchanged,
    Inserted,
    Deleted,
    Modified,
    Imaginary,
}

/// <summary>
/// One row in the diff side-by-side rendering. A null/empty Imaginary row
/// is the alignment padding DiffPlex inserts to keep the two columns
/// row-correlated.  <see cref="LineNumber"/> is 1-based within its
/// own pane (Original or Improved) for non-imaginary rows; null
/// for the alignment padding so the gutter renders blank for those.
/// </summary>
public sealed record DiffLineRow(
    string Text,
    DiffLineKind Kind,
    bool IsImaginary,
    int? LineNumber);
