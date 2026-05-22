using System.IO;
using System.Text;
using System.Text.Json;

using CapyBro.Models;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

public sealed class HistoryStore : IHistoryStore, IDisposable
{
    public const int DefaultCapacity = 50;

    // Debounce disk writes: rapid Add calls (or a Clear() during the
    // History window's "Delete each" loop) should produce one final
    // file write, not N. 300ms matches the API-key debounce in
    // GeneralTabViewModel.
    private static readonly TimeSpan PersistDebounce = TimeSpan.FromMilliseconds(300);

    private readonly string _path;
    private readonly int _capacity;
    private readonly ILogger<HistoryStore> _logger;
    private readonly object _gate = new();
    private readonly LinkedList<HistoryEntry> _entries = new();
    private readonly Timer _persistTimer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _loaded;
    private bool _disposed;

    internal HistoryStore(string path, int capacity, ILogger<HistoryStore> logger)
    {
        _path = path;
        _capacity = capacity;
        _logger = logger;
        _persistTimer = new Timer(OnPersistTimerTick);
    }

    public static HistoryStore CreateDefault(ILogger<HistoryStore> logger)
        => new(DefaultPath, DefaultCapacity, logger);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ai_text_improver_v2_history.json");

    public event EventHandler? Changed;

    // Z5-F3 / H9: subscribers (HistoryViewModel) hook this to surface a
    // toast when persist or load fails so the user knows their journal
    // isn't being saved. Pre-fix everything stayed in LogWarning.
    public event EventHandler<HistoryStoreErrorEventArgs>? Faulted;

    public IReadOnlyList<HistoryEntry> Snapshot()
    {
        EnsureLoaded();
        lock (_gate)
        {
            // Newest-first per the doc comment. _entries is maintained
            // newest-at-head (AddFirst), so this enumeration is already
            // in display order.
            return [.. _entries];
        }
    }

    public HistoryEntry? GetMostRecent()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _entries.First?.Value;
        }
    }

    public void Add(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureLoaded();

        lock (_gate)
        {
            _entries.AddFirst(entry);
            EvictBeyondCapacity();
        }

        SchedulePersist();
        RaiseChanged();
    }

    public void Remove(Guid id)
    {
        EnsureLoaded();

        bool removed;
        lock (_gate)
        {
            var node = _entries.First;
            removed = false;
            while (node is not null)
            {
                if (node.Value.Id == id)
                {
                    _entries.Remove(node);
                    removed = true;
                    break;
                }

                node = node.Next;
            }
        }

        if (removed)
        {
            SchedulePersist();
            RaiseChanged();
        }
    }

    public void Clear()
    {
        EnsureLoaded();

        bool hadEntries;
        lock (_gate)
        {
            hadEntries = _entries.Count > 0;
            _entries.Clear();
        }

        if (hadEntries)
        {
            SchedulePersist();
            RaiseChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _persistTimer.Dispose();

        // Best-effort flush: if a debounced save was pending, commit it
        // synchronously so we don't lose the last addition on app exit.
        try
        {
            FlushSync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Final history flush failed during Dispose");
        }

        _writeLock.Dispose();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > 0)
                {
                    var json = File.ReadAllText(_path, Encoding.UTF8);
                    var file = JsonSerializer.Deserialize(json, HistoryFileJsonContext.Default.HistoryFile);
                    if (file?.Entries is { Count: > 0 } loaded)
                    {
                        // Sort defensively in case the file was hand-edited
                        // out of order, then push newest-first into the
                        // linked list.
                        foreach (var entry in loaded.OrderBy(e => e.Timestamp))
                        {
                            _entries.AddFirst(entry);
                        }

                        EvictBeyondCapacity();
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "History file at {Path} is corrupt — starting empty", _path);
                RaiseFaulted(HistoryStoreErrorKind.Load, ex);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read history file at {Path}", _path);
                RaiseFaulted(HistoryStoreErrorKind.Load, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied reading history file at {Path}", _path);
                RaiseFaulted(HistoryStoreErrorKind.Load, ex);
            }

            _loaded = true;
        }
    }

    private void RaiseFaulted(HistoryStoreErrorKind kind, Exception ex)
    {
        try
        {
            Faulted?.Invoke(this, new HistoryStoreErrorEventArgs(kind, ex));
        }
        catch (Exception cbEx) when (cbEx is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogDebug(cbEx, "Faulted subscriber threw — ignored");
        }
    }

    private void EvictBeyondCapacity()
    {
        while (_entries.Count > _capacity)
        {
            _entries.RemoveLast();
        }
    }

    private void SchedulePersist()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _persistTimer.Change(PersistDebounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Race with Dispose — final flush already covered the latest state.
        }
    }

    private void OnPersistTimerTick(object? state)
    {
        // Fire and forget — write lock serializes overlapping ticks.
        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            HistoryFile file;
            lock (_gate)
            {
                file = new HistoryFile
                {
                    SchemaVersion = HistoryFile.CurrentSchemaVersion,
                    Entries = [.. _entries],
                };
            }

            var json = JsonSerializer.Serialize(file, HistoryFileJsonContext.Default.HistoryFile);
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Atomic-ish replace via tmp + rename, same pattern as ConfigStore.
            var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tmp, json, Encoding.UTF8);
                if (File.Exists(_path))
                {
                    File.Replace(tmp, _path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmp, _path);
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (IOException)
                    {
                        // best-effort cleanup
                    }
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not persist history to {Path}", _path);
            RaiseFaulted(HistoryStoreErrorKind.Persist, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied persisting history to {Path}", _path);
            RaiseFaulted(HistoryStoreErrorKind.Persist, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogError(ex, "Unexpected error persisting history");
            RaiseFaulted(HistoryStoreErrorKind.Persist, ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FlushSync()
    {
        // Synchronous variant for Dispose: must not deadlock if a write
        // is already in flight. We just wait briefly for the lock and
        // skip if we can't grab it (the in-flight write is itself
        // committing the latest state anyway).
        if (!_writeLock.Wait(TimeSpan.FromSeconds(2)))
        {
            return;
        }

        try
        {
            HistoryFile file;
            lock (_gate)
            {
                file = new HistoryFile
                {
                    SchemaVersion = HistoryFile.CurrentSchemaVersion,
                    Entries = [.. _entries],
                };
            }

            var json = JsonSerializer.Serialize(file, HistoryFileJsonContext.Default.HistoryFile);
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, json, Encoding.UTF8);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
