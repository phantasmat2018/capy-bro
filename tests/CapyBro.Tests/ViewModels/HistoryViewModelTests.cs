using System.IO;

using CapyBro.Models;
using CapyBro.Services;
using CapyBro.ViewModels;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CapyBro.Tests.ViewModels;

/// <summary>
/// H8 (Z5-F2) regression suite — HistoryViewModelTests did not exist
/// before this file.  Covers the test list from the Z5-F2 finding:
/// snapshot population, store-change refresh, filter rebuild +
/// selection preservation, derived-property fan-out, command
/// CanExecute, clipboard-failure notification (F4), and the store
/// Faulted event toast (F3).
///
/// Dispatcher-cross-thread coverage is out of scope here — the SUT's
/// OnStoreChanged returns early when Application.Current is null
/// (which is always true in a headless xUnit context).  LoadFromStore
/// is public so the same refresh path is exercised directly.
/// </summary>
[Collection(TranslatorCollection.Name)]
public class HistoryViewModelTests
{
    private static HistoryEntry MakeEntry(string original, string improved, string? prompt = null, string? model = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Original = original,
            Improved = improved,
            PromptText = prompt ?? "Improve",
            Model = model ?? "openai/gpt-4o",
            HotkeyKind = (int)HotkeyKind.Default,
        };

    [Fact]
    public void Construct_PopulatesEntriesFromStoreSnapshot()
    {
        // Pre-populate the store so the SUT's constructor-time
        // LoadFromStore picks them up — guarantees the History tab has
        // content on first render rather than flashing an empty state
        // and re-populating on the next dispatcher tick.
        var harness = new Harness();
        harness.Store.Add(MakeEntry("hello", "Hello"));
        harness.Store.Add(MakeEntry("good morning", "Good morning"));

        var sut = harness.BuildSut();

        sut.Entries.Should().HaveCount(2);
        sut.HasEntries.Should().BeTrue();
        sut.SelectedEntry.Should().NotBeNull(
            "auto-selecting the first row keeps the detail pane from showing nothing on tab-open");
    }

    [Fact]
    public void LoadFromStore_AfterStoreAdd_RefreshesEntries()
    {
        var harness = new Harness();
        var sut = harness.BuildSut();
        sut.Entries.Should().BeEmpty();

        harness.Store.Add(MakeEntry("new", "New"));
        sut.LoadFromStore();

        sut.Entries.Should().ContainSingle();
        sut.HasEntries.Should().BeTrue();
    }

    [Fact]
    public void FilterText_MatchingCurrentSelection_PreservesIt()
    {
        var harness = new Harness();
        var apple = MakeEntry("an apple a day", "An apple a day");
        var banana = MakeEntry("banana bread", "Banana bread");
        harness.Store.Add(apple);
        harness.Store.Add(banana);
        var sut = harness.BuildSut();
        sut.SelectedEntry = sut.Entries.Single(e => e.Id == apple.Id);

        sut.FilterText = "apple";

        sut.Entries.Should().HaveCount(1);
        sut.SelectedEntry?.Id.Should().Be(
            apple.Id,
            "selection survives the filter pass when it still matches — user is not yanked off the entry they were reading");
    }

    [Fact]
    public void FilterText_DroppingCurrentSelection_FallsBackToFirstVisibleRow()
    {
        var harness = new Harness();
        var apple = MakeEntry("apple", "Apple");
        var banana = MakeEntry("banana", "Banana");
        harness.Store.Add(apple);
        harness.Store.Add(banana);
        var sut = harness.BuildSut();
        sut.SelectedEntry = sut.Entries.Single(e => e.Id == apple.Id);

        sut.FilterText = "banana";

        sut.Entries.Should().ContainSingle();
        sut.SelectedEntry?.Id.Should().Be(
            banana.Id,
            "when the previously-selected entry no longer matches, fall back to the first visible row so the detail pane never shows stale text");
    }

    [Fact]
    public void SelectedEntryChanged_FiresPropertyChangedForDerivedSurface()
    {
        var harness = new Harness();
        harness.Store.Add(MakeEntry("a", "A"));
        harness.Store.Add(MakeEntry("b", "B", prompt: "Polish", model: "anthropic/claude-3.5-sonnet"));
        var sut = harness.BuildSut();

        var raised = new HashSet<string?>(StringComparer.Ordinal);
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.SelectedEntry = sut.Entries[1];

        raised.Should().Contain(
            [nameof(sut.OriginalText), nameof(sut.ImprovedText), nameof(sut.PromptText), nameof(sut.ModelName), nameof(sut.FormattedTime)],
            "every detail-pane binding has to be told the source changed; missing one would freeze that field on the previous entry");
    }

    [Fact]
    public void DeleteSelectedCommand_CanExecuteTracksSelection()
    {
        var harness = new Harness();
        harness.Store.Add(MakeEntry("x", "X"));
        var sut = harness.BuildSut();

        sut.SelectedEntry.Should().NotBeNull("constructor auto-selects the first row");
        sut.DeleteSelectedCommand.CanExecute(null).Should().BeTrue();

        sut.SelectedEntry = null;
        sut.DeleteSelectedCommand.CanExecute(null).Should().BeFalse(
            "the Delete button must dim when there is nothing to delete");
    }

    [Fact]
    public async Task CopyOriginal_ClipboardThrows_NotifiesUserAsync()
    {
        // Z5-F4 / H10 regression: pre-fix the clipboard failure path
        // logged at Warning and went silent, so the user blamed the
        // History feature when their target app pasted stale content.
        var harness = new Harness();
        harness.Store.Add(MakeEntry("Привіт", "Hello"));
        harness.Clipboard
            .Setup(x => x.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("clipboard busy"));
        var sut = harness.BuildSut();

        await sut.CopyOriginalCommand.ExecuteAsync(null);

        harness.Notifications.Verify(
            x => x.ShowError(It.Is<string>(s => s == harness.Translator["msg_history_copy_failed"])),
            Times.Once,
            "a silent clipboard failure is the original PreserveLanguage anti-pattern — must reach the user as a toast");
    }

    [Fact]
    public void StoreFaulted_RaisesSaveFailedToast()
    {
        // Z5-F3 / H9 regression: HistoryStore.Faulted is the only
        // signal the user gets that their journal isn't being saved.
        // Without this subscription it sits in Warning logs and the
        // user discovers loss on the next launch.
        var harness = new Harness();
        var sut = harness.BuildSut();

        harness.Store.RaiseFaulted(HistoryStoreErrorKind.Persist, new IOException("disk full"));

        harness.Notifications.Verify(
            x => x.ShowError(It.Is<string>(s => s == harness.Translator["msg_history_save_failed"])),
            Times.Once);

        // Make sure the SUT isn't garbage-collected before the
        // Faulted hook fires (Dispose unsubscribes; we want the
        // subscription live for this assertion).
        GC.KeepAlive(sut);
    }

    // Z5-F10 / L11 regression: HistoryViewModel registers as Singleton in
    // App.xaml.cs:1182 and depends on the also-Singleton IHistoryStore.
    // The DI container disposes singletons it constructed, but if a
    // future refactor changed the lifetime (e.g. AddTransient) or routed
    // disposal through a different path, the `_store.Changed` and
    // `_store.Faulted` event subscriptions could outlive the VM and
    // either (a) leak the VM via the store's event delegate, or (b) fire
    // a notification through a disposed VM.  This test pins the
    // subscription-detach contract directly so the regression is caught
    // at the VM layer rather than only via leak diagnostics.
    [Fact]
    public void Dispose_DetachesChangedAndFaultedSubscriptions()
    {
        var harness = new Harness();
        harness.Store.Add(new HistoryEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Original = "x",
            Improved = "y",
            PromptText = "p",
            Model = "m",
            HotkeyKind = 0,
        });
        var sut = harness.BuildSut();
        var initialCount = sut.Entries.Count;
        initialCount.Should().BeGreaterThan(0, "test prerequisite: store has at least one entry");

        sut.Dispose();
        // After Dispose, a Changed event must NOT cause the VM to
        // re-snapshot the store — Entries should remain at its pre-
        // dispose count.
        harness.Store.Add(new HistoryEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Original = "post-dispose",
            Improved = "y",
            PromptText = "p",
            Model = "m",
            HotkeyKind = 0,
        });

        sut.Entries.Count.Should().Be(
            initialCount,
            "the VM must have unsubscribed from Changed during Dispose; a post-dispose store mutation must not fan out into the VM");

        // Same contract for Faulted: post-dispose, the SUT must not
        // route store faults into ShowError on the notifications mock.
        harness.Notifications.Invocations.Clear();
        harness.Store.RaiseFaulted(HistoryStoreErrorKind.Persist, new IOException("post-dispose"));
        harness.Notifications.Verify(
            x => x.ShowError(It.IsAny<string>()),
            Times.Never,
            "Faulted must also be detached during Dispose so a post-dispose store fault doesn't pop a toast");
    }

    // Z5-F10 / L11 follow-on: Dispose is idempotent — the container might
    // dispose the singleton twice under abnormal teardown paths.
    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var harness = new Harness();
        var sut = harness.BuildSut();

        sut.Dispose();
        var act = () => sut.Dispose();

        act.Should().NotThrow(
            "Dispose guards via the `_disposed` flag so a second call short-circuits cleanly");
    }

    [Fact]
    public void HasActiveFilter_FollowsFilterTextThroughTrimmedWhitespace()
    {
        // M14 (Z5-F6) regression: HistoryTab.xaml routes between
        // "no-history" and "no-matches" empty states via HasActiveFilter.
        // A whitespace-only FilterText must NOT count as an active
        // filter — ApplyFilter trims and treats it as empty, so the
        // empty-state surface must match.
        var harness = new Harness();
        var sut = harness.BuildSut();

        sut.HasActiveFilter.Should().BeFalse("empty filter on construct");

        sut.FilterText = "   ";
        sut.HasActiveFilter.Should().BeFalse(
            "whitespace-only filter is treated as no filter by ApplyFilter; the empty-state binding must agree");

        sut.FilterText = "apple";
        sut.HasActiveFilter.Should().BeTrue();

        sut.FilterText = string.Empty;
        sut.HasActiveFilter.Should().BeFalse();
    }

    private sealed class Harness
    {
        public Harness()
        {
            Store = new InMemoryHistoryStoreForTests();
            Clipboard = new Mock<IClipboardService>(MockBehavior.Loose);
            Clipboard.Setup(x => x.SetTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Notifications = new Mock<INotificationService>(MockBehavior.Loose);
            Translator = Translator.Instance;
        }

        public InMemoryHistoryStoreForTests Store { get; }

        public Mock<IClipboardService> Clipboard { get; }

        public Mock<INotificationService> Notifications { get; }

        public Translator Translator { get; }

        public HistoryViewModel BuildSut() => new(
            Store,
            Clipboard.Object,
            Translator,
            NullLogger<HistoryViewModel>.Instance,
            Notifications.Object);
    }

    /// <summary>
    /// Trivial in-memory <see cref="IHistoryStore"/> for unit tests —
    /// no disk, no debounce timer, no thread marshalling.  Exposes a
    /// public hook to fire <see cref="IHistoryStore.Faulted"/> directly
    /// because the production store only raises it from I/O paths that
    /// are hard to provoke under xUnit.
    /// </summary>
    private sealed class InMemoryHistoryStoreForTests : IHistoryStore
    {
        private readonly List<HistoryEntry> _entries = [];

        public event EventHandler? Changed;

        public event EventHandler<HistoryStoreErrorEventArgs>? Faulted;

        public IReadOnlyList<HistoryEntry> Snapshot() => _entries.AsReadOnly();

        public HistoryEntry? GetMostRecent() => _entries.Count == 0 ? null : _entries[^1];

        public void Add(HistoryEntry entry)
        {
            _entries.Insert(0, entry);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(Guid id)
        {
            if (_entries.RemoveAll(e => e.Id == id) > 0)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Clear()
        {
            if (_entries.Count > 0)
            {
                _entries.Clear();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RaiseFaulted(HistoryStoreErrorKind kind, Exception exception) =>
            Faulted?.Invoke(this, new HistoryStoreErrorEventArgs(kind, exception));
    }
}
