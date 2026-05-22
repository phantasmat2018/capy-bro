using System.IO;
using System.Text;
using System.Text.Json;

using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CapyBro.Tests.Services;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _tempPath;

    public HistoryStoreTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"history_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            try
            {
                File.Delete(_tempPath);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
        }
    }

    [Fact]
    public void Add_AppendsEntry_NewestFirstInSnapshot()
    {
        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);

        var first = MakeEntry("first", DateTimeOffset.UtcNow.AddMinutes(-1));
        var second = MakeEntry("second", DateTimeOffset.UtcNow);
        store.Add(first);
        store.Add(second);

        var snapshot = store.Snapshot();
        snapshot.Should().HaveCount(2);
        snapshot[0].Original.Should().Be("second");
        snapshot[1].Original.Should().Be("first");
    }

    [Fact]
    public void GetMostRecent_ReturnsLatestAdded()
    {
        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);

        store.Add(MakeEntry("first"));
        store.Add(MakeEntry("second"));

        store.GetMostRecent()!.Original.Should().Be("second");
    }

    [Fact]
    public void GetMostRecent_EmptyStore_ReturnsNull()
    {
        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);

        store.GetMostRecent().Should().BeNull();
    }

    [Fact]
    public void Add_BeyondCapacity_EvictsOldestFifo()
    {
        using var store = new HistoryStore(_tempPath, capacity: 3, NullLogger<HistoryStore>.Instance);

        store.Add(MakeEntry("a"));
        store.Add(MakeEntry("b"));
        store.Add(MakeEntry("c"));
        store.Add(MakeEntry("d")); // evicts "a"

        var snapshot = store.Snapshot();
        snapshot.Should().HaveCount(3);
        snapshot.Select(e => e.Original).Should().BeEquivalentTo(["d", "c", "b"], opts => opts.WithStrictOrdering());
    }

    [Fact]
    public void Remove_DropsMatchingEntry()
    {
        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);
        var keep = MakeEntry("keep");
        var drop = MakeEntry("drop");
        store.Add(keep);
        store.Add(drop);

        store.Remove(drop.Id);

        var snapshot = store.Snapshot();
        snapshot.Should().HaveCount(1);
        snapshot[0].Original.Should().Be("keep");
    }

    [Fact]
    public void Clear_EmptiesStore()
    {
        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);
        store.Add(MakeEntry("a"));
        store.Add(MakeEntry("b"));

        store.Clear();

        store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Add_RaisesChangedEvent()
    {
        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);
        var fired = 0;
        store.Changed += (_, _) => fired++;

        store.Add(MakeEntry("a"));

        fired.Should().Be(1);
    }

    [Fact]
    public void Persistence_RoundTripsAcrossInstances()
    {
        // Arrange: write through one instance, dispose to flush.
        using (var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance))
        {
            store.Add(MakeEntry("persisted"));
            // Dispose triggers a synchronous flush so the file is on disk.
        }

        File.Exists(_tempPath).Should().BeTrue("Dispose should flush the in-flight history to disk");

        // Act: load via a fresh instance.
        using var second = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);
        var snapshot = second.Snapshot();

        snapshot.Should().HaveCount(1);
        snapshot[0].Original.Should().Be("persisted");
    }

    [Fact]
    public void Persistence_CorruptFile_StartsEmpty_NoThrow()
    {
        File.WriteAllText(_tempPath, "{ this is not valid json", Encoding.UTF8);

        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);

        store.Snapshot().Should().BeEmpty();
    }

    // Z5-F8 / L9: graceful degradation is the right runtime behaviour
    // (don't crash on corrupt JSON), but pre-fix the test approved the
    // silent swallow without checking that the user-visible signal
    // (`Faulted` event → toast via HistoryViewModel, Z5-F3 / H9) fires.
    // This test pins the contract — corrupt JSON must both start empty
    // AND raise Faulted so the H9 toast pathway lights up.
    [Fact]
    public void Persistence_CorruptFile_RaisesFaultedLoadEvent()
    {
        File.WriteAllText(_tempPath, "{ this is not valid json", Encoding.UTF8);

        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);
        HistoryStoreErrorEventArgs? observed = null;
        store.Faulted += (_, e) => observed = e;

        // Touch the store so EnsureLoaded runs (load is lazy on first
        // operation, not in the constructor).  Subscribing BEFORE this
        // ensures we observe the Faulted raised inside EnsureLoaded.
        store.Snapshot();

        observed.Should().NotBeNull(
            "graceful degradation must still raise Faulted — pre-fix the runtime simply logged at Warning and stayed silent, which was the canonical 'fail open' P3 anti-pattern the audit catches");
        observed!.Kind.Should().Be(HistoryStoreErrorKind.Load);
        observed.Exception.Should().BeOfType<JsonException>();
    }

    [Fact]
    public void Persistence_EmptyFile_StartsEmpty_NoThrow()
    {
        File.WriteAllText(_tempPath, string.Empty, Encoding.UTF8);

        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);

        store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Persistence_OutOfOrderTimestamps_AreSortedNewestFirst()
    {
        // Hand-write an envelope with reversed ordering, ensuring the
        // store sorts on load rather than trusting file order.
        var older = MakeEntry("older", DateTimeOffset.UtcNow.AddMinutes(-5));
        var newer = MakeEntry("newer", DateTimeOffset.UtcNow);
        var file = new HistoryFile
        {
            SchemaVersion = HistoryFile.CurrentSchemaVersion,
            Entries = [older, newer],
        };
        File.WriteAllText(
            _tempPath,
            JsonSerializer.Serialize(file, HistoryFileJsonContext.Default.HistoryFile),
            Encoding.UTF8);

        using var store = new HistoryStore(_tempPath, capacity: 50, NullLogger<HistoryStore>.Instance);

        var snapshot = store.Snapshot();
        snapshot[0].Original.Should().Be("newer");
        snapshot[1].Original.Should().Be("older");
    }

    private static HistoryEntry MakeEntry(string text, DateTimeOffset? timestamp = null) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        Original = text,
        Improved = text + " improved",
        PromptText = "test prompt",
        Model = "test/model",
        HotkeyKind = 0,
    };
}
