using System.Collections.ObjectModel;
using System.Windows;

using CapyBro.Models;

using CommunityToolkit.Mvvm.ComponentModel;

using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace CapyBro.ViewModels;

/// <summary>
/// Backs the side-by-side diff preview modal. Computes the line-level diff
/// at construction and on every Edit-mode commit, exposes two per-line
/// collections for binding, and stores the user's verdict for the
/// DiffPreviewService to read after ShowDialog returns.
///
/// v21: the right (Improved) side is editable.  The user can flip
/// <see cref="IsEditMode"/> on, edit <see cref="EditableImproved"/> in a
/// TextBox, and flip back — the diff is recomputed against the edited
/// text so the side-by-side view stays accurate.  Accept commits the
/// edited version (not the original LLM output) so the user's manual
/// fixes ship to the document.
/// </summary>
public sealed partial class DiffPreviewViewModel : ObservableObject
{
    public DiffPreviewViewModel(string original, string improved)
    {
        Original = original ?? string.Empty;
        Improved = improved ?? string.Empty;
        _editableImproved = Improved;

        OriginalLines = [];
        ImprovedLines = [];

        RecomputeDiff();
    }

    /// <summary>The user's clipboard text — immutable for the lifetime of
    /// the modal (no UI to edit the original side).</summary>
    public string Original { get; }

    /// <summary>
    /// The "improved" text — initially the raw LLM result, becomes
    /// whatever the user committed via Edit mode after a CommitEditableImproved.
    /// This is the value DiffPreviewService.ShowOnUiThread hands back to
    /// TextProcessor for the paste step (via <see cref="FinalImproved"/>).
    /// </summary>
    public string Improved { get; private set; }

    /// <summary>
    /// Authoritative result text the diff-preview service returns to
    /// TextProcessor on Accept.  After every CommitEditableImproved
    /// (called explicitly on Accept and on the IsEditMode→false transition)
    /// this stays in sync with <see cref="Improved"/>; reading it before
    /// the commit would return the pre-edit value.
    /// </summary>
    public string FinalImproved => Improved;

    public ObservableCollection<DiffLineRow> OriginalLines { get; }

    public ObservableCollection<DiffLineRow> ImprovedLines { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InsertedChipVisibility))]
    [NotifyPropertyChangedFor(nameof(HasAnyChanges))]
    [NotifyPropertyChangedFor(nameof(IsIdentical))]
    private int _insertedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletedChipVisibility))]
    [NotifyPropertyChangedFor(nameof(HasAnyChanges))]
    [NotifyPropertyChangedFor(nameof(IsIdentical))]
    private int _deletedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModifiedChipVisibility))]
    [NotifyPropertyChangedFor(nameof(HasAnyChanges))]
    [NotifyPropertyChangedFor(nameof(IsIdentical))]
    private int _modifiedCount;

    /// <summary>
    /// Per-chip visibility: hide the chip when its count is zero so the
    /// stats strip shows only the change kinds that actually occurred.
    /// "+0 -0 ~0" was visual clutter that drew the eye to nothing — for
    /// a pure-translation prompt with only modified lines we now show
    /// just the amber "~N" chip, not three chips with two zeros.
    /// </summary>
    public Visibility InsertedChipVisibility => InsertedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeletedChipVisibility => DeletedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ModifiedChipVisibility => ModifiedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>True if at least one line differs between the two sides.</summary>
    public bool HasAnyChanges => InsertedCount > 0 || DeletedCount > 0 || ModifiedCount > 0;

    /// <summary>
    /// Inverse of <see cref="HasAnyChanges"/>, exposed as Visibility so the
    /// "тексти ідентичні" hint badge in the XAML can bind directly.
    /// </summary>
    public Visibility IsIdentical => HasAnyChanges ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// View-mode flag: <c>false</c> shows the side-by-side diff (read-only,
    /// default), <c>true</c> swaps the right pane to an editable TextBox so
    /// the user can hand-fix the LLM result before accepting.  Toggling
    /// back to false commits the edits and recomputes the diff via the
    /// OnIsEditModeChanged partial hook.
    /// </summary>
    [ObservableProperty]
    private bool _isEditMode;

    /// <summary>
    /// Visibility of the read-only diff scroller in the right pane.  We
    /// expose a computed Visibility property (instead of binding through
    /// a BoolToVisibility converter in XAML) because StaticResource
    /// converter lookups inside a deeply-nested visual tree have
    /// occasionally failed to resolve in this window — likely an
    /// ordering interaction with the merged Application.Resources
    /// dictionaries.  A direct property binding sidesteps that.
    /// </summary>
    public Visibility DiffViewVisibility => IsEditMode ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Visibility of the editable TextBox in the right pane — the
    /// counterpart to <see cref="DiffViewVisibility"/>.  See the comment
    /// there for why we avoid the converter route.
    /// </summary>
    public Visibility EditViewVisibility => IsEditMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The in-edit-mode buffer.  Bound to a TextBox via TwoWay so every
    /// keystroke updates it; we deliberately do NOT recompute the diff
    /// per-keystroke (too jarring with line reflow + virtualizing scroll
    /// jumps).  Recompute fires once when the user flips back to diff
    /// view, or when OnAccept calls <see cref="CommitEditableImproved"/>.
    /// </summary>
    [ObservableProperty]
    private string _editableImproved;

    /// <summary>
    /// User's choice — set by the window code-behind when one of the three
    /// action buttons is clicked, or via window-close (treated as Reject).
    /// Default Reject means "if the modal is dismissed without an explicit
    /// choice we cancel the run rather than silently committing".
    /// </summary>
    public DiffPreviewResult Result { get; set; } = DiffPreviewResult.Reject;

    /// <summary>
    /// Snapshot the current EditableImproved buffer into <see cref="Improved"/>
    /// + <see cref="FinalImproved"/> and rebuild the per-row diff
    /// collections against it.  Called from two places:
    /// <list type="bullet">
    /// <item>the OnIsEditModeChanged partial hook when the user flips
    /// back from Edit view to Diff view (so the freshly-shown diff
    /// reflects their edits);</item>
    /// <item>the Accept handler in <see cref="Views.DiffPreviewWindow"/>
    /// — guarantees Accept always ships the latest edited text, even if
    /// the user clicked Accept while still in Edit view (didn't toggle
    /// back to Diff first).</item>
    /// </list>
    /// No-op when EditableImproved equals the current Improved so repeated
    /// commits (e.g. Accept after a back-to-diff toggle) don't churn the
    /// observable collections needlessly.
    /// </summary>
    public void CommitEditableImproved()
    {
        if (string.Equals(EditableImproved, Improved, StringComparison.Ordinal))
        {
            return;
        }

        Improved = EditableImproved ?? string.Empty;
        RecomputeDiff();
        OnPropertyChanged(nameof(Improved));
        OnPropertyChanged(nameof(FinalImproved));
    }

    partial void OnIsEditModeChanged(bool value)
    {
        // Notify the computed visibility properties so the XAML
        // bindings flip the two panes synchronously with the toggle.
        OnPropertyChanged(nameof(DiffViewVisibility));
        OnPropertyChanged(nameof(EditViewVisibility));

        // false = "back to diff view" → commit the buffer so the diff
        // the user is about to see reflects their edits.
        // true = "entering edit view" → buffer is already in sync (we
        // last committed on the previous false→true→false cycle, or
        // it's still the initial LLM output).
        if (!value)
        {
            CommitEditableImproved();
        }
    }

    /// <summary>
    /// Rebuilds <see cref="OriginalLines"/> + <see cref="ImprovedLines"/>
    /// + the three stat counts from the current Original + Improved
    /// pair.  Clears-and-refills in place so the existing collection
    /// references stay valid for XAML bindings (replacing the reference
    /// would require INotifyPropertyChanged on the property itself, and
    /// {Binding OriginalLines} on an ItemsControl doesn't re-subscribe
    /// to a swapped collection without a property-change notification).
    /// </summary>
    private void RecomputeDiff()
    {
        // SideBySideDiffBuilder pads each side with "Imaginary" lines so
        // OldLines[i] and NewLines[i] always describe the same visual row.
        // We render those padding lines as empty placeholders below; without
        // them, equal-length but content-shifted texts wouldn't align.
        var diff = SideBySideDiffBuilder.Diff(Original, Improved);

        var newOriginal = NumberRows(diff.OldText.Lines).ToList();
        var newImproved = NumberRows(diff.NewText.Lines).ToList();

        OriginalLines.Clear();
        foreach (var row in newOriginal)
        {
            OriginalLines.Add(row);
        }

        ImprovedLines.Clear();
        foreach (var row in newImproved)
        {
            ImprovedLines.Add(row);
        }

        InsertedCount = newImproved.Count(r => r.Kind == DiffLineKind.Inserted);
        DeletedCount = newOriginal.Count(r => r.Kind == DiffLineKind.Deleted);
        ModifiedCount = newImproved.Count(r => r.Kind == DiffLineKind.Modified);
    }

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
