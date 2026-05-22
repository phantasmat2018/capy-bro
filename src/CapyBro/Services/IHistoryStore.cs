using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// Append-only ring buffer of past text-improvement runs. Latest entries
/// come first. Mutations fire <see cref="Changed"/> so UI can refresh.
/// Thread-safe; mutations may come from a threadpool thread (TextProcessor)
/// while reads happen on the UI thread (History window).
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Snapshot of current entries, newest first. Returning a snapshot
    /// (not the live collection) keeps callers from racing against a
    /// concurrent <see cref="Add"/> on a different thread.
    /// </summary>
    IReadOnlyList<HistoryEntry> Snapshot();

    /// <summary>
    /// Most recently added entry, or null if history is empty.
    /// Used by the Undo hotkey path which needs only the latest.
    /// </summary>
    HistoryEntry? GetMostRecent();

    /// <summary>
    /// Records a completed improvement. Older entries beyond the
    /// configured cap (default 50) are evicted FIFO.
    /// </summary>
    void Add(HistoryEntry entry);

    void Remove(Guid id);

    void Clear();

    /// <summary>
    /// Fires after every mutation. Subscribers should marshal to the UI
    /// thread before touching WPF bindings.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Fires when persist or load fails — Z5-F3 / H9 fix. Pre-fix the
    /// HistoryStore swallowed I/O failures into Warning logs and the user
    /// had no signal that their history journal was silently broken.
    /// Subscribers should marshal to the UI thread and surface via toast.
    /// </summary>
    event EventHandler<HistoryStoreErrorEventArgs>? Faulted;
}

/// <summary>
/// Payload for <see cref="IHistoryStore.Faulted"/>. Describes which path
/// failed (persist vs load) and carries the underlying exception so the
/// subscriber can log richer diagnostics if needed.
/// </summary>
public sealed class HistoryStoreErrorEventArgs(HistoryStoreErrorKind kind, Exception exception) : EventArgs
{
    public HistoryStoreErrorKind Kind { get; } = kind;

    public Exception Exception { get; } = exception;
}

public enum HistoryStoreErrorKind
{
    Load,
    Persist,
}
