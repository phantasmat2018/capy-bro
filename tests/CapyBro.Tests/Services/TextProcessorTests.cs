using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Tests.TestHelpers;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CapyBro.Tests.Services;

[Collection(TranslatorCollection.Name)]
public class TextProcessorTests
{
    private const string Selected = "user-selected text";
    private const string ApiResult = "improved result";
    private const string ApiKey = "k1";

    /// <summary>
    /// Builds an IAsyncEnumerable that yields the given chunks in order.
    /// Used for mocking <see cref="IOpenRouterClient.ImproveStreamAsync"/>.
    /// Concatenated chunks reconstruct what TextProcessor will see as the
    /// final result before stripping.
    /// </summary>
    private static async IAsyncEnumerable<string> AsAsyncStreamAsync(params string[] chunks)
    {
        foreach (var c in chunks)
        {
            yield return c;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// IAsyncEnumerable that throws on first MoveNext — mirrors the
    /// previous .ThrowsAsync() pattern for the buffered API.
    /// </summary>
    private static async IAsyncEnumerable<string> ThrowingAsyncStreamAsync(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162 // unreachable; required so the method qualifies as async iterator
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// Stream that delays for <paramref name="delay"/> before yielding the
    /// payload. Used by the cancellation test — the linked CTS fires
    /// before the delay completes, propagating OperationCanceledException
    /// out through the iterator (mirroring real-world stream cancellation).
    /// </summary>
    private static async IAsyncEnumerable<string> DelayedAsyncStreamAsync(
        TimeSpan delay,
        string payload,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(delay, ct);
        yield return payload;
    }

    [Fact]
    public async Task HandleHotkey_HappyPath_FiresStartedAndCompleted_NoFailureAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        var ok = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        ok.Should().BeTrue();
        harness.StartedCount.Should().Be(1);
        harness.CompletedCount.Should().Be(1);
        harness.FailedEvents.Should().BeEmpty();
        harness.Clipboard.SetCalls.Should().Contain(ApiResult);
        harness.Input.PasteCount.Should().Be(1);

        // Re-select-after-paste: the just-pasted AI result should
        // remain highlighted in the user's editor so they can copy /
        // delete / extend without re-selecting manually.  The SUT
        // delegates to ITextSelectionExtender; the harness's
        // FakeTextSelectionExtender records the call so we can assert
        // both that it happened AND that the right char count was
        // requested (matching ApiResult.Length).
        harness.SelectionExtender.CallCount.Should().Be(1);
        harness.SelectionExtender.LastCharCount.Should().Be(ApiResult.Length);
    }

    // Z1-F1 / H1 regression test — the exact failure shape that hid the
    // original PreserveLanguage bug. The pre-fix Verify used
    // `It.IsAny<bool>()` for the preserveLanguage slot, so a refactor that
    // hard-coded `false` (or otherwise dropped the flag) passed every
    // TextProcessorTest. Assert-by-value catches that drift at the seam.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleHotkey_PreserveLanguage_PassesValueByValueToOpenRouterAsync(bool preserveLanguage)
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.Prompts
            .Setup(x => x.SelectAsync(It.IsAny<HotkeyKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Prompt
            {
                Text = "Improve text",
                PreserveLanguage = preserveLanguage,
            });

        var ok = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        ok.Should().BeTrue();

        // Z1-F1 / H1: the original PreserveLanguage failure shape — a
        // refactor that hard-codes a wrong bool in TextProcessor must
        // break this assertion (pre-fix the slot was It.IsAny<bool>()).
        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                preserveLanguage, // <-- ASSERT BY VALUE, not It.IsAny<bool>()
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // Z1-F2 / H2 regression test — same shape for config.Timeout. Pre-fix
    // every Verify used `It.IsAny<TimeSpan>()` so a hard-coded
    // `TimeSpan.FromSeconds(30)` in TextProcessor (instead of the
    // user-configured value) would have passed silently.
    [Theory]
    [InlineData(7)]
    [InlineData(60)]
    public async Task HandleHotkey_Timeout_PassesConfigValueByValueToOpenRouterAsync(int seconds)
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = seconds,
            });

        var ok = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        ok.Should().BeTrue();

        var expected = TimeSpan.FromSeconds(seconds);
        // Z1-F2 / H2: hard-coding a timeout in TextProcessor must break
        // this assertion (pre-fix the slot was It.IsAny<TimeSpan>()).
        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                expected,             // <-- ASSERT BY VALUE
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // v14: `config.Timeout = 0` is the "wait indefinitely" sentinel.
    // TextProcessor MUST translate it to Timeout.InfiniteTimeSpan
    // before handing to OpenRouterClient, NOT pass TimeSpan.FromSeconds(0)
    // which would cancel the request immediately and surface as an
    // instant api_request_timeout toast — exactly the failure mode the
    // user opted out of by typing 0 into the Additional features input.
    [Fact]
    public async Task HandleHotkey_TimeoutZero_PassesInfiniteTimeSpanToOpenRouterAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 0,
            });

        var ok = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        ok.Should().BeTrue();

        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                Timeout.InfiniteTimeSpan,  // <-- ASSERT BY VALUE
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Timeout=0 is the sentinel for 'no timeout' — TextProcessor must translate to Timeout.InfiniteTimeSpan so OpenRouterClient skips CancelAfter and the request runs as long as OpenRouter is willing to stream");
    }

    [Fact]
    public async Task HandleHotkey_Undo_ReSelectsRestoredOriginalAsync()
    {
        // Symmetry with the regular run: an undo replaces the AI text
        // back with the original, and that ORIGINAL should also end up
        // re-selected — same UX shape, same "selection in, selection
        // out" mental model.  Without this, an accidental Undo would
        // dump unselected text into the document and the user would
        // have to re-select it manually before re-running the hotkey.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        // First run records _lastImprovement — Original = Selected.
        var first = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        first.Should().BeTrue("test prerequisite");

        // Now undo the just-recorded entry.
        var undo = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Undo);

        undo.Should().BeTrue();
        harness.UndoneCount.Should().Be(1);

        // Two paste calls total (improve + undo) and two selection-
        // extender invocations.  The LAST one must use the ORIGINAL
        // text length, not the AI result length — that's what's
        // currently visible to the user after the undo paste.
        harness.Input.PasteCount.Should().Be(2);
        harness.SelectionExtender.CallCount.Should().Be(2);
        harness.SelectionExtender.LastCharCount.Should().Be(Selected.Length);
    }

    [Fact]
    public async Task HandleHotkey_KeepResultSelectedOff_DoesNotCallSelectionExtenderAsync()
    {
        // The v12 ExperimentalKeepResultSelected master flag gates the
        // entire post-paste re-selection step — when it's off,
        // TextProcessor must NOT delegate to ITextSelectionExtender at
        // all.  This is the gate that makes the feature opt-in: users
        // who don't want the highlight (or who hit edge-case glitches
        // in their preferred editor) toggle it off in General →
        // Additional features and the SUT silently goes back to its
        // original "paste-and-forget" behaviour.
        //
        // Crucial assertion: SelectionExtender.CallCount == 0.  Even
        // a single call when the flag is off would mean the gate
        // leaked, the user toggled the feature off, and the broken
        // selection still appeared anyway.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalKeepResultSelected = false,
            });

        var ok = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        ok.Should().BeTrue();
        harness.CompletedCount.Should().Be(1);
        harness.Input.PasteCount.Should().Be(1, "the paste itself is not gated by the flag");
        harness.SelectionExtender.CallCount.Should().Be(
            0,
            "ExperimentalKeepResultSelected was off, so the re-selection step must be skipped entirely");
    }

    [Fact]
    public async Task HandleHotkey_AlreadyProcessing_ReturnsFalse_AndDoesNotProcessAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        // First call: gate copy on a TaskCompletionSource so we can hold processing open.
        var gate = new TaskCompletionSource<string>();
        harness.Clipboard.OnGetText = ct => gate.Task.WaitAsync(ct);

        var inflight = harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        await Task.Yield();

        var second = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        second.Should().BeFalse();
        gate.SetResult(Selected);
        await inflight;
        harness.StartedCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleHotkey_AfterFailure_ReleasesLock_AllowingNextRunAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncStreamAsync(new OpenRouterException("boom")));

        var first = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        first.Should().BeTrue();
        harness.Sut.IsProcessing.Should().BeFalse();
        harness.FailedEvents.Should().HaveCount(1);

        // Now allow success on retry
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsyncStreamAsync(ApiResult));
        var second = await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        second.Should().BeTrue();
        harness.CompletedCount.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n  ")]
    public async Task HandleHotkey_EmptyOrWhitespaceSelection_RaisesNoSelection_NoApiCallAsync(string clipboardText)
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.Clipboard.OnGetText = _ => Task.FromResult(clipboardText);

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.StartedCount.Should().Be(0);
        harness.FailedEvents.Should().HaveCount(1);
        harness.FailedEvents[0].LocalizedMessage.Should().Be("Виділіть текст і спробуйте знову");
        // Z10-F7 / M27: keyed failures carry a LocalizationKey so subscribers
        // can re-resolve in the active locale at toast-render time.  The
        // eagerly-resolved LocalizedMessage above is the back-compat snapshot.
        harness.FailedEvents[0].LocalizationKey.Should().Be("toast_no_selection");
        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // Z10-F7 / M27 — every keyed RaiseFailed path in TextProcessor surfaces
    // its key through the new EventArgs property.  Pre-fix every call site
    // passed `_translator[key]` eagerly, freezing the locale at raise time;
    // this test pins the key contract so a future refactor cannot regress
    // to a hard-coded literal.  The no-API-key path is the cheapest path
    // to exercise — single Setup override on the credentials mock.
    [Fact]
    public async Task HandleHotkey_NoApiKey_FailedEventCarriesLocalizationKeyAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.FailedEvents.Should().HaveCount(1);
        harness.FailedEvents[0].LocalizationKey.Should().Be("api_unauthorized");
        harness.FailedEvents[0].LocalizedMessage.Should().Be("Недійсний API-ключ");
    }

    [Fact]
    public async Task HandleHotkey_PromptSelectionCancelled_RestoresClipboard_NoFailureEventAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.Prompts.Setup(x => x.SelectAsync(HotkeyKind.Menu, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Prompt?)null);

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Menu);

        harness.StartedCount.Should().Be(0);
        harness.FailedEvents.Should().BeEmpty();
        harness.Clipboard.SetCalls.Should().Contain("ORIGINAL");
        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleHotkey_NoApiKey_RaisesUnauthorizedFailureAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.StartedCount.Should().Be(0);
        harness.FailedEvents.Should().HaveCount(1);
        harness.FailedEvents[0].LocalizedMessage.Should().Be("Недійсний API-ключ");
    }

    [Fact]
    public async Task HandleHotkey_OpenRouterException_RaisesFailedWithLocalizedMessage_RestoresClipboardAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncStreamAsync(new OpenRouterException("Не вдалося")));

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.StartedCount.Should().Be(1);
        harness.CompletedCount.Should().Be(0);
        harness.FailedEvents.Should().HaveCount(1);
        harness.FailedEvents[0].LocalizedMessage.Should().Be("Не вдалося");
        // Z10-F7 / M27: OpenRouterException carries a dynamically-built
        // message (server body + status), not a Translator key — so the
        // raw path leaves LocalizationKey null.  Subscribers must surface
        // LocalizedMessage verbatim in this case.
        harness.FailedEvents[0].LocalizationKey.Should().BeNull();
        harness.Clipboard.SetCalls.Last().Should().Be("ORIGINAL");
    }

    [Fact]
    public async Task HandleHotkey_UnexpectedException_RaisesFailedWithUnknownErrorAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncStreamAsync(new InvalidOperationException("oops")));

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.FailedEvents.Should().HaveCount(1);
        harness.FailedEvents[0].LocalizedMessage.Should().Be("Невідома помилка API");
        harness.FailedEvents[0].Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task HandleHotkey_Cancellation_PropagatesAndRestoresClipboardAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        using var cts = new CancellationTokenSource();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, string _, string _, TimeSpan _, bool _, CancellationToken token) =>
                DelayedAsyncStreamAsync(TimeSpan.FromSeconds(10), ApiResult, token));
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = async () => await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Sut.IsProcessing.Should().BeFalse();
        harness.Clipboard.SetCalls.Last().Should().Be("ORIGINAL");
    }

    [Fact]
    public async Task HandleHotkey_TimeoutDuringStream_RaisesApiRequestTimeoutFailureAsync()
    {
        // Regression: when the OpenRouter timeout fires DURING streaming
        // (i.e. after SendAsync returned response headers but the stream
        // hasn't finished), the OperationCanceledException reaches
        // TextProcessor's catch chain WITHOUT being wrapped into
        // OpenRouterException (only the SendAsync-phase wraps via
        // `when (!externalCt.IsCancellationRequested)`).  Pre-fix that
        // OCE hit the generic OperationCanceledException catch which
        // rethrows without firing ProcessingFailed — the user saw the
        // timeout elapse with no toast.
        //
        // Post-fix `catch (OperationCanceledException) when (!ct.
        // IsCancellationRequested)` discriminates: external ct NOT
        // cancelled = timeout (or other internal cancel) → raise the
        // api_request_timeout failure event so the toast fires.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncStreamAsync(new OperationCanceledException("timeout fired mid-stream")));

        // External ct is the default (never cancelled) so the
        // OperationCanceledException came from inside the API call —
        // i.e. the inner CancelAfter, i.e. a timeout.
        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.FailedEvents.Should().HaveCount(
            1,
            "a mid-stream timeout must surface ProcessingFailed so App.xaml.cs's ShowError fires — pre-fix this assertion would fail because the OCE was rethrown silently");
        harness.FailedEvents[0].LocalizationKey.Should().Be(
            "api_request_timeout",
            "the failure event must carry the localised timeout key so the toast resolves correctly across UA/RU/EN — and re-resolves on mid-flight language switch per M27");
        harness.Clipboard.SetCalls.Last().Should().Be(
            "ORIGINAL",
            "clipboard must be restored to the user's original content on timeout — the AI never produced a successful result");
    }

    [Fact]
    public async Task HandleHotkey_HappyPath_RecordsHistoryEntryAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.History.Entries.Should().HaveCount(1);
        var entry = harness.History.Entries[0];
        entry.Original.Should().Be(Selected);
        entry.Improved.Should().Be(ApiResult);
        entry.PromptText.Should().Be("Improve text");
        entry.Model.Should().Be("test/model");
        entry.HotkeyKind.Should().Be((int)HotkeyKind.Default);
        entry.Id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task HandleHotkey_ExperimentalHistoryOff_HappyPath_DoesNotRecordEntryAsync()
    {
        // v11 regression: when the master kill-switch is off, the history
        // log stays empty even on a fully successful improvement run —
        // matches the privacy-preserving behaviour the user opts in via
        // General → Experimental → "Improvement history".  The
        // SettingsWindow sidebar History tab is hidden in this state too
        // (covered by SettingsWindowViewModelTests).
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = false,
            });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.History.Entries.Should().BeEmpty(
            "ExperimentalHistory=false must be a true kill-switch — no entry recorded even on a successful run");

        // The improvement itself still ran — clipboard was set, paste was
        // synthesised — the only difference is no journal entry.
        harness.Input.PasteCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task HandleHotkey_FailureBeforePaste_DoesNotRecordHistoryAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingAsyncStreamAsync(new OpenRouterException("boom")));

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.History.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleHotkey_StreamingMasterFlagOff_DoesNotFireProcessingStreamUpdatedAsync()
    {
        // Master switch off → HTTP transport still streams, accumulator
        // still works, BUT the per-chunk event must be silent. The
        // user-visible effect is the original static "Обробка..." toast.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalStreaming = false,
            });
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsyncStreamAsync("Hello", ", ", "world", "!"));

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.StreamUpdates.Should().BeEmpty(
            "with the streaming master flag off the toast must not animate");
        harness.CompletedCount.Should().Be(1, "the request itself still completes normally");
        harness.History.Entries[0].Improved.Should().Be(
            "Hello, world!",
            "accumulation must work the same way regardless of the UI-event flag");
    }

    [Fact]
    public async Task HandleHotkey_StreamingChunks_FireProcessingStreamUpdatedEventsCumulativelyAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        // Production default has streaming off; opt in for this test so we
        // can verify the live-event firing behavior.
        harness.SetupStreamingExperimentEnabled();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsyncStreamAsync("Hello", ", ", "world", "!"));

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        // One ProcessingStreamUpdated per delta, each carrying the FULL
        // accumulated text — subscribers don't need to do their own
        // concatenation. This is the contract the toast UI relies on.
        harness.StreamUpdates.Should().BeEquivalentTo(
            ["Hello", "Hello, ", "Hello, world", "Hello, world!"],
            opts => opts.WithStrictOrdering());
        harness.CompletedCount.Should().Be(1);
        harness.History.Entries[0].Improved.Should().Be(
            "Hello, world!",
            "history captures the post-strip concatenation of all deltas");
    }

    [Fact]
    public async Task HandleHotkey_PerPromptModelMasterOff_UsesGlobalModelAsync()
    {
        // Master flag off: even if the prompt has a per-prompt model
        // override saved, ResolveEffectiveModel must fall back to the
        // global config.Model. History should also reflect the global.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "global/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalPerPromptModel = false,
            });
        harness.Prompts
            .Setup(x => x.SelectAsync(It.IsAny<HotkeyKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Prompt { Text = "p", Model = "prompt/override" });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                ApiKey,
                "global/model",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.History.Entries[0].Model.Should().Be("global/model");
    }

    [Fact]
    public async Task HandleHotkey_PerPromptModelMasterOn_PromptHasNoModel_UsesGlobalAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "global/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalPerPromptModel = true,
            });
        harness.Prompts
            .Setup(x => x.SelectAsync(It.IsAny<HotkeyKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Prompt { Text = "p", Model = null });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                ApiKey,
                "global/model",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "no per-prompt override → global wins regardless of master flag");
    }

    [Fact]
    public async Task HandleHotkey_PerPromptModelMasterOn_PromptHasModel_UsesOverrideAsync()
    {
        // Both knobs say "use the override" — TextProcessor must call the
        // API with prompt.Model AND record it in history.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "global/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalPerPromptModel = true,
            });
        harness.Prompts
            .Setup(x => x.SelectAsync(It.IsAny<HotkeyKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Prompt { Text = "p", Model = "prompt/override" });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                ApiKey,
                "prompt/override",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        harness.History.Entries[0].Model.Should().Be(
            "prompt/override",
            "history must reflect the model the user actually saw used");
    }

    [Fact]
    public async Task HandleHotkey_PerPromptModelMasterOn_WhitespaceOverride_FallsBackToGlobalAsync()
    {
        // Defensive: a saved prompt with Model="   " is treated as no
        // override. Sending whitespace as a model id would 400 OpenRouter.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "global/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalPerPromptModel = true,
            });
        harness.Prompts
            .Setup(x => x.SelectAsync(It.IsAny<HotkeyKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Prompt { Text = "p", Model = "   " });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                ApiKey,
                "global/model",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleHotkey_CostsAndCreditsMasterOff_NoEstimateInStartedEventAsync()
    {
        // Master flag off → TextProcessor must not even invoke the
        // estimator. Started event fires with EstimatedCostUsd = null so
        // the toast renders without a "(~$X)" suffix.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.CostEstimator.NextEstimateUsd = 0.0042m; // would-be estimate ignored

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.LastEstimatedCostUsd.Should().BeNull(
            "with master flag off TextProcessor must skip estimation entirely");
        harness.CostEstimator.Calls.Should().BeEmpty(
            "no /models pricing fetch should happen when the user hasn't opted in");
    }

    [Fact]
    public async Task HandleHotkey_CostsAndCreditsMasterOn_PassesEstimateThroughStartedEventAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalCostsAndCredits = true,
            });
        harness.CostEstimator.NextEstimateUsd = 0.0042m;

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.LastEstimatedCostUsd.Should().Be(0.0042m);
        harness.CostEstimator.Calls.Should().HaveCount(1);
        harness.CostEstimator.Calls[0].Model.Should().Be("test/model");
        harness.CostEstimator.Calls[0].Input.Should().Be(Selected);
    }

    [Fact]
    public async Task HandleHotkey_PrivacyRedactionMasterOff_SendsRawTextToApiAsync()
    {
        // With the master flag off, no redaction should happen — the
        // API receives the user's selection verbatim. Production default.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.Clipboard.OnGetText = ct =>
        {
            harness.Clipboard.OnGetText = _ => Task.FromResult("Email me at john@a.com");
            return Task.FromResult("ORIGINAL");
        };

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                "Email me at john@a.com",
                It.IsAny<TimeSpan>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "with redaction off the API call should receive the raw text including PII");
    }

    [Fact]
    public async Task HandleHotkey_PrivacyRedactionMasterOn_SendsPlaceholdersToApiAsync()
    {
        // With the master flag on, the API call should NOT contain the
        // user's email — the placeholder goes out instead. The history
        // entry however records the cleartext (we restore on the way
        // back).
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalPrivacyRedaction = true,
            });

        // Selection contains an email; the AI mock returns a sentence
        // that preserves the placeholder verbatim (realistic LLM behaviour).
        harness.Clipboard.OnGetText = ct =>
        {
            harness.Clipboard.OnGetText = _ => Task.FromResult("Email me at john@a.com");
            return Task.FromResult("ORIGINAL");
        };
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsyncStreamAsync("Send the message to <<EMAIL_1>>."));

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        // The API was invoked with a placeholder, NOT with the raw email.
        harness.OpenRouter.Verify(
            x => x.ImproveStreamAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.Is<string>(text => text.Contains("<<EMAIL_1>>") && !text.Contains("john@a.com")),
                It.IsAny<TimeSpan>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // History records the cleartext (restored locally), not the placeholder.
        harness.History.Entries.Should().HaveCount(1);
        harness.History.Entries[0].Improved.Should().Be("Send the message to john@a.com.");
        harness.History.Entries[0].Improved.Should().NotContain(
            "<<EMAIL_1>>",
            "the placeholder must be restored before history is recorded");
    }

    [Fact]
    public async Task HandleHotkey_StreamingMasterFlagDefault_DoesNotFireUpdatesAsync()
    {
        // Default config (master flag off — fresh install). Even when the
        // API streams chunks, no ProcessingStreamUpdated should fire. This
        // is the new expected baseline for users who haven't opted in.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(AsAsyncStreamAsync("Hello", ", ", "world", "!"));

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.StreamUpdates.Should().BeEmpty();
        harness.CompletedCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleHotkey_PromptWithPreview_ButMasterFlagOff_DoesNotInvokeDiffServiceAsync()
    {
        // Master switch in General → Experimental features takes precedence
        // over per-prompt opt-in. Even if the prompt has ShowDiffPreview=true,
        // turning the master flag off must skip the modal entirely (canonical
        // "kill switch" semantics).
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.SetupPromptWithDiffPreview();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalDiffPreview = false,
            });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.DiffPreview.Calls.Should().BeEmpty(
            "master flag off must short-circuit the preview path entirely");
        harness.CompletedCount.Should().Be(1, "happy path completes normally without preview");
        harness.History.Entries.Should().HaveCount(1);
        harness.Input.PasteCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleHotkey_MasterFlagOn_PromptOff_DoesNotInvokeDiffServiceAsync()
    {
        // Symmetrical to the previous test: master flag on, but the
        // selected prompt opted out → no preview. Confirms BOTH flags are
        // required (AND, not OR).
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        // Default prompt in SetupHappyPath has ShowDiffPreview=false.
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = true,
                ExperimentalDiffPreview = true,
            });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.DiffPreview.Calls.Should().BeEmpty();
        harness.CompletedCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleHotkey_PromptWithoutPreview_DoesNotInvokeDiffServiceAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        // Default prompt in SetupHappyPath has ShowDiffPreview=false.
        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.DiffPreview.Calls.Should().BeEmpty();
        harness.CompletedCount.Should().Be(1, "happy path completes normally");
        harness.History.Entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task HandleHotkey_PromptWithPreview_AcceptCommitsResultAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.SetupPromptWithDiffPreview();
        harness.DiffPreview.DefaultResult = DiffPreviewResult.Accept;

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.DiffPreview.Calls.Should().HaveCount(1);
        harness.DiffPreview.Calls[0].Original.Should().Be(Selected);
        harness.DiffPreview.Calls[0].Improved.Should().Be(ApiResult);
        harness.ProgressClosedCount.Should().Be(1, "progress toast hides before showing the modal");
        harness.CompletedCount.Should().Be(1, "Accept proceeds to paste + Completed");
        harness.History.Entries.Should().HaveCount(1, "accepted previews record history");
        harness.Clipboard.SetCalls.Should().Contain(ApiResult);
        harness.Input.PasteCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleHotkey_PromptWithPreview_RejectRestoresOriginalAndSkipsHistoryAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.SetupPromptWithDiffPreview();
        harness.DiffPreview.DefaultResult = DiffPreviewResult.Reject;

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        harness.DiffPreview.Calls.Should().HaveCount(1);
        harness.ProgressClosedCount.Should().Be(1);
        harness.CompletedCount.Should().Be(0, "Reject must NOT raise Completed");
        harness.FailedEvents.Should().BeEmpty("Reject is user cancellation, not failure");
        harness.History.Entries.Should().BeEmpty("rejected runs leave no history trace");
        harness.Input.PasteCount.Should().Be(0, "Reject must not paste anything");
        harness.Clipboard.SetCalls.Last().Should().Be("ORIGINAL", "clipboard must be restored on reject");
    }

    [Fact]
    public async Task HandleHotkey_PromptWithPreview_RegenerateLoopsThenAcceptsAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.SetupPromptWithDiffPreview();
        // First preview → Regenerate, second preview → Accept.
        harness.DiffPreview.EnqueueResults(DiffPreviewResult.Regenerate, DiffPreviewResult.Accept);

        var apiCallCount = 0;
        harness.OpenRouter
            .Setup(x => x.ImproveStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                apiCallCount++;
                return AsAsyncStreamAsync($"{ApiResult}-attempt-{apiCallCount}");
            });

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);

        apiCallCount.Should().Be(2, "regenerate triggers a second API call");
        harness.DiffPreview.Calls.Should().HaveCount(2);
        harness.DiffPreview.Calls[0].Improved.Should().Be($"{ApiResult}-attempt-1");
        harness.DiffPreview.Calls[1].Improved.Should().Be($"{ApiResult}-attempt-2");
        harness.StartedCount.Should().Be(2, "Started fires for the original call AND for the regenerate");
        harness.ProgressClosedCount.Should().Be(2, "progress hides before each preview opens");
        harness.CompletedCount.Should().Be(1);
        harness.History.Entries.Should().HaveCount(1);
        harness.History.Entries[0].Improved.Should().Be(
            $"{ApiResult}-attempt-2",
            "history records the accepted attempt, not earlier regenerated drafts");
    }

    [Fact]
    public async Task HandleHotkey_Undo_RestoresOriginalToClipboardAndPastesAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        // Run once to populate history.
        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        harness.History.Entries.Should().HaveCount(1);

        // Reset paste counter so Undo's paste is observable in isolation.
        var pasteCountBefore = harness.Input.PasteCount;

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Undo);

        harness.UndoneCount.Should().Be(1);
        harness.Clipboard.SetCalls.Last().Should().Be(Selected); // selected == "Original" placeholder == ApiResult input
        harness.Input.PasteCount.Should().Be(pasteCountBefore + 1);
    }

    [Fact]
    public async Task HandleHotkey_Undo_WorksWhenExperimentalHistoryIsOffAsync()
    {
        // Regression: pre-fix flipping the v11 ExperimentalHistory master
        // flag off broke the Undo hotkey.  Undo went through
        // _history.GetMostRecent() which returned null because the
        // gate-on-Add skipped persistence, so users got "Nothing to
        // undo" even after a successful improvement.  Now TextProcessor
        // keeps an in-memory _lastImprovement that the Undo path
        // consults first, decoupling Undo from the journal flag.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Model = "test/model",
                Timeout = 30,
                ExperimentalHistory = false,
            });

        // Run once with history OFF — nothing should land in the store,
        // but the in-memory last-improvement gets populated.
        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Default);
        harness.History.Entries.Should().BeEmpty(
            "ExperimentalHistory=false must remain a true journal kill-switch");

        var pasteCountBefore = harness.Input.PasteCount;

        // Undo: must work despite the empty store.
        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Undo);

        harness.UndoneCount.Should().Be(
            1,
            "Undo must succeed via the in-memory _lastImprovement fallback even when the persistent history is disabled");
        harness.Input.PasteCount.Should().Be(pasteCountBefore + 1);
        harness.FailedEvents.Should().BeEmpty(
            "Undo must not raise 'nothing to undo' when there genuinely is something to undo this session");
    }

    [Fact]
    public async Task HandleHotkey_UndoOnEmptyHistory_RaisesNothingToUndoAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        await harness.Sut.HandleHotkeyAsync(HotkeyKind.Undo);

        harness.UndoneCount.Should().Be(0);
        harness.FailedEvents.Should().HaveCount(1);
        harness.FailedEvents[0].LocalizedMessage.Should().Be("Немає що скасовувати");
    }

    [Fact]
    public async Task HandleHotkey_PasteFailsAfterApiSuccess_LeavesAiResultOnClipboardAsync()
    {
        // Regression: when SendInput failed (UIPI-blocked window, BlockInput
        // race) AFTER SetTextAsync committed the AI result to the clipboard,
        // the catch handler used to call RestoreClipboardAsync which silently
        // wiped the freshly-improved text. The user's improvement was lost.
        // Now resultCommitted gates the restore — the AI text stays on the
        // clipboard so the user can paste manually.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        var pasteFailure = new InvalidOperationException("SendInput blocked");
        var failingInput = new ThrowingInputSimulator(pasteFailure);
        var sut = harness.RebuildSutWithInput(failingInput);

        await sut.HandleHotkeyAsync(HotkeyKind.Default);

        // Clipboard SetCalls trace: ApiResult was written, but the catch
        // path must NOT have appended "ORIGINAL" after that.
        harness.Clipboard.SetCalls.Should().Contain(ApiResult);
        harness.Clipboard.SetCalls.Last().Should().Be(ApiResult);
        harness.FailedEvents.Should().HaveCount(1);
        harness.FailedEvents[0].LocalizedMessage.Should().Be("Невідома помилка API");
    }

    [Fact]
    public async Task HandleHotkey_CancelAfterPasteCommitted_RaisesCancelledWithResultAsync()
    {
        // H18 (Z10-F4) regression: when OperationCanceledException
        // lands AFTER the clipboard already holds the AI result (paste
        // throws or post-paste hooks cancel), the catch path used to
        // be silent — toast closed and the user thought the run
        // failed.  The new ProcessingCancelledWithResult event lets
        // App.xaml.cs distinguish partial-success from outright
        // failure and surface the dedicated translator string.
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();

        var cancelOnPaste = new ThrowingInputSimulator(new OperationCanceledException("user pressed ✕"));
        var sut = harness.RebuildSutWithInput(cancelOnPaste);

        int cancelledWithResultCount = 0;
        sut.ProcessingCancelledWithResult += (_, _) => cancelledWithResultCount++;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.HandleHotkeyAsync(HotkeyKind.Default));

        cancelledWithResultCount.Should().Be(
            1,
            "the cancel landed AFTER resultCommitted, so the partial-success event must fire exactly once");
        harness.Clipboard.SetCalls.Last().Should().Be(
            ApiResult,
            "clipboard must still hold the AI result — the restore path runs only when result was NOT committed");
        harness.FailedEvents.Should().BeEmpty(
            "a cancel-with-result is NOT a failure — ProcessingFailed must NOT fire");
        harness.CompletedCount.Should().Be(
            0,
            "the run was cancelled before RaiseCompleted; Completed must NOT fire");
    }

    [Fact]
    public async Task HandleHotkey_FiftyConcurrentCalls_OnlyOneRunsAsync()
    {
        var harness = new TextProcessorHarness();
        harness.SetupHappyPath();
        var gate = new TaskCompletionSource<string>();
        harness.Clipboard.OnGetText = async ct =>
        {
            // Hold the first invocation open until we release; subsequent calls also hit this
            // but they would never reach here because Interlocked rejects them earlier.
            return await gate.Task.WaitAsync(ct);
        };

        var calls = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => harness.Sut.HandleHotkeyAsync(HotkeyKind.Default)))
            .ToArray();

        // Wait for one call to claim the slot.
        await Task.Delay(50);
        gate.SetResult(Selected);

        var results = await Task.WhenAll(calls);

        results.Count(r => r).Should().Be(1, "only one HandleHotkeyAsync should win the Interlocked race");
        results.Count(r => !r).Should().Be(49);
        harness.StartedCount.Should().Be(1);
        harness.CompletedCount.Should().Be(1);
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public List<string> SetCalls { get; } = [];

        public int ClearCount { get; private set; }

        public Func<CancellationToken, Task<string>> OnGetText { get; set; } =
            _ => Task.FromResult(Selected);

        public Task<string> GetTextAsync(CancellationToken ct = default) => OnGetText(ct);

        public Task SetTextAsync(string text, CancellationToken ct = default)
        {
            SetCalls.Add(text);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInputSimulator : IInputSimulator
    {
        public int CopyCount { get; private set; }

        public int PasteCount { get; private set; }

        // Track the post-paste re-selection burst so tests can assert
        // on (a) "selection was extended at all" and (b) the exact
        // backward-extension length the SUT requested — that latter
        // value is the SUT's promise to the user that the just-pasted
        // text re-selects to its full size.
        public int SelectBackwardCount { get; private set; }

        public int LastSelectBackwardCharCount { get; private set; }

        public Task SendCopyAsync(CancellationToken ct = default)
        {
            CopyCount++;
            return Task.CompletedTask;
        }

        public Task SendPasteAsync(CancellationToken ct = default)
        {
            PasteCount++;
            return Task.CompletedTask;
        }

        public Task SendSelectBackwardAsync(int charCount, CancellationToken ct = default)
        {
            SelectBackwardCount++;
            LastSelectBackwardCharCount = charCount;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingInputSimulator : IInputSimulator
    {
        private readonly Exception _pasteException;

        public ThrowingInputSimulator(Exception pasteException)
        {
            _pasteException = pasteException;
        }

        public Task SendCopyAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendPasteAsync(CancellationToken ct = default) => Task.FromException(_pasteException);

        // Selection burst is post-paste, so any test that wires this
        // class to make paste fail will never reach SendSelectBackwardAsync.
        // Implement as a benign no-op so the interface is satisfied
        // without distorting the failure-mode contract.
        public Task SendSelectBackwardAsync(int charCount, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeModifierReleaseWaiter : IModifierReleaseWaiter
    {
        // Defaults to "modifiers are released" so the post-paste selection
        // step proceeds in happy-path tests.  The dedicated test that
        // exercises the still-held-modifier guard flips this to true and
        // verifies SendSelectBackwardAsync is NOT called.
        public bool ModifiersStillDown { get; set; }

        public Task WaitForReleaseAsync(TimeSpan timeout, CancellationToken ct = default) => Task.CompletedTask;

        public bool IsAnyModifierDown() => ModifiersStillDown;
    }

    /// <summary>
    /// Stub <see cref="ITextSelectionExtender"/> that records the
    /// argument the SUT passed to <c>ExtendBackwardAsync</c>.  Used by
    /// the post-paste re-selection tests to verify that:
    /// <list type="bullet">
    ///   <item>The extender is called with the correct char count
    ///         when <c>ExperimentalKeepResultSelected</c> is on; and</item>
    ///   <item>The extender is NOT called at all when the flag is off
    ///         — i.e. the gate is in TextProcessor and the extender
    ///         doesn't receive a dead-code "do nothing" call.</item>
    /// </list>
    /// The fake intentionally does not simulate UIA-vs-fallback
    /// behaviour — that lives inside the real <c>TextSelectionExtender</c>
    /// and is exercised at a level deeper than these TextProcessor
    /// tests reach.
    /// </summary>
    private sealed class FakeTextSelectionExtender : ITextSelectionExtender
    {
        public int CallCount { get; private set; }

        public int LastCharCount { get; private set; }

        public Task ExtendBackwardAsync(int charCount, CancellationToken ct = default)
        {
            CallCount++;
            LastCharCount = charCount;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stub <see cref="ICostEstimator"/> with a settable next-estimate.
    /// Defaults to null (no estimate available) — most tests aren't
    /// asserting on cost so they don't need to script anything; the few
    /// that do toggle <see cref="NextEstimateUsd"/>.
    /// </summary>
    private sealed class ScriptedCostEstimator : ICostEstimator
    {
        public decimal? NextEstimateUsd { get; set; }

        public List<(string ApiKey, string Model, string Input)> Calls { get; } = [];

        public Task<decimal?> EstimateAsync(string apiKey, string modelId, string inputText, CancellationToken ct = default)
        {
            Calls.Add((apiKey, modelId, inputText));
            return Task.FromResult(NextEstimateUsd);
        }

        public void InvalidateCache()
        {
            // Noop in tests — no real cache to clear.
        }
    }

    /// <summary>
    /// Scripts a sequence of <see cref="DiffPreviewResult"/>s for the diff
    /// preview prompts that fire during a single processor run. Defaults to
    /// Accept so tests that don't care about preview don't have to script.
    ///
    /// v16: the production interface returns DiffPreviewOutcome so the
    /// caller can pick up edited text from the Edit-view toggle.  The
    /// mock echoes <c>improved</c> back as FinalImproved (the "user
    /// didn't edit" case) by default; tests that want to simulate an
    /// edit enqueue a custom <see cref="DiffPreviewOutcome"/> via
    /// <see cref="EnqueueOutcomes"/>.
    /// </summary>
    private sealed class ScriptedDiffPreviewService : IDiffPreviewService
    {
        private readonly Queue<DiffPreviewOutcome> _scripted = new();

        public List<(string Original, string Improved)> Calls { get; } = [];

        public DiffPreviewResult DefaultResult { get; set; } = DiffPreviewResult.Accept;

        public void EnqueueResults(params DiffPreviewResult[] results)
        {
            foreach (var r in results)
            {
                // Echo the LLM output back unchanged — represents the
                // "user didn't enter Edit mode" common case.  Tests that
                // want to simulate manual edits use EnqueueOutcomes.
                _scripted.Enqueue(new DiffPreviewOutcome(r, string.Empty));
            }
        }

        public void EnqueueOutcomes(params DiffPreviewOutcome[] outcomes)
        {
            foreach (var o in outcomes)
            {
                _scripted.Enqueue(o);
            }
        }

        public Task<DiffPreviewOutcome> ShowAsync(string original, string improved, CancellationToken ct = default)
        {
            Calls.Add((original, improved));
            if (_scripted.Count > 0)
            {
                var scripted = _scripted.Dequeue();
                // EnqueueResults stores empty FinalImproved as a sentinel
                // meaning "echo the actual call's improved arg back".
                // EnqueueOutcomes lets a test override that with explicit
                // user-edited text.
                var finalText = string.IsNullOrEmpty(scripted.FinalImproved)
                    ? improved
                    : scripted.FinalImproved;
                return Task.FromResult(new DiffPreviewOutcome(scripted.Verdict, finalText));
            }

            return Task.FromResult(new DiffPreviewOutcome(DefaultResult, improved));
        }
    }

    /// <summary>
    /// Trivial in-memory IHistoryStore for tests — no disk, no debounce timer,
    /// thread-safe via lock. Lets tests inspect the captured entries directly.
    /// </summary>
    private sealed class InMemoryHistoryStore : IHistoryStore
    {
        private readonly object _gate = new();
        private readonly List<HistoryEntry> _entries = [];

        public event EventHandler? Changed;

        // H9 fix added this to the interface — store has no I/O so it
        // never fires the event, but stubs must satisfy the contract.
#pragma warning disable CS0067 // Event is declared but never used.
        public event EventHandler<HistoryStoreErrorEventArgs>? Faulted;
#pragma warning restore CS0067

        public IReadOnlyList<HistoryEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IReadOnlyList<HistoryEntry> Snapshot()
        {
            lock (_gate)
            {
                // newest-first per the interface contract (we AddFirst-style)
                return [.. _entries.AsEnumerable().Reverse()];
            }
        }

        public HistoryEntry? GetMostRecent()
        {
            lock (_gate)
            {
                return _entries.Count == 0 ? null : _entries[^1];
            }
        }

        public void Add(HistoryEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(Guid id)
        {
            bool removed;
            lock (_gate)
            {
                removed = _entries.RemoveAll(e => e.Id == id) > 0;
            }

            if (removed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Clear()
        {
            bool had;
            lock (_gate)
            {
                had = _entries.Count > 0;
                _entries.Clear();
            }

            if (had)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private sealed class TextProcessorHarness
    {
        public TextProcessorHarness()
        {
            Translator = new Translator();
            Translator.SetLanguage(Language.Ukrainian);

            Clipboard = new FakeClipboardService { OnGetText = _ => Task.FromResult("ORIGINAL") };
            Input = new FakeInputSimulator();
            Modifiers = new FakeModifierReleaseWaiter();

            ConfigStore = new Mock<IConfigStore>(MockBehavior.Strict);
            ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(AppConfig.Default with
                {
                    Model = "test/model",
                    Timeout = 30,
                    ExperimentalHistory = true,
                    // Default test config has the post-paste re-selection
                    // experiment ON so the legacy assertions about
                    // SelectionExtender.CallCount keep their meaning;
                    // dedicated flag-off tests build their own config to
                    // verify the gate.
                    ExperimentalKeepResultSelected = true,
                });

            Credentials = new Mock<ICredentialStore>(MockBehavior.Strict);
            Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(ApiKey);

            OpenRouter = new Mock<IOpenRouterClient>(MockBehavior.Strict);
            // Strict mock requires every property + method to be set
            // up; RequiresApiKey is touched by TextProcessor on every
            // run.  Return true (OpenRouter does need a key) — this is
            // the real implementation's behaviour and matches what
            // tests expect downstream.
            OpenRouter.SetupGet(x => x.RequiresApiKey).Returns(true);

            // v15: TextProcessor takes a factory instead of a direct
            // IOpenRouterClient ref so OllamaClient can sit alongside.
            // For OpenRouter-mode tests the factory simply returns the
            // mocked IOpenRouterClient — every test in this suite uses
            // Provider=OpenRouter (the AppConfig.Default).
            Providers = new ScriptedLlmProviderFactory(OpenRouter.Object);
            Prompts = new Mock<IPromptSelector>(MockBehavior.Strict);
            History = new InMemoryHistoryStore();
            DiffPreview = new ScriptedDiffPreviewService();
            CostEstimator = new ScriptedCostEstimator();
            Redactor = new PrivacyRedactor();
            SelectionExtender = new FakeTextSelectionExtender();

            Sut = new TextProcessor(
                ConfigStore.Object,
                Credentials.Object,
                Providers,
                Clipboard,
                Input,
                Modifiers,
                Prompts.Object,
                Translator,
                History,
                DiffPreview,
                CostEstimator,
                Redactor,
                SelectionExtender,
                NullLogger<TextProcessor>.Instance);

            Sut.ProcessingStarted += (_, e) =>
            {
                StartedCount++;
                LastEstimatedCostUsd = e.EstimatedCostUsd;
            };
            Sut.ProcessingCompleted += (_, _) => CompletedCount++;
            Sut.ProcessingUndone += (_, _) => UndoneCount++;
            Sut.ProcessingProgressClosed += (_, _) => ProgressClosedCount++;
            Sut.ProcessingStreamUpdated += (_, e) => StreamUpdates.Add(e.AccumulatedContent);
            Sut.ProcessingFailed += (_, e) => FailedEvents.Add(e);
        }

        public TextProcessor Sut { get; private set; }

        public TextProcessor RebuildSutWithInput(IInputSimulator input)
        {
            Sut = new TextProcessor(
                ConfigStore.Object,
                Credentials.Object,
                Providers,
                Clipboard,
                input,
                Modifiers,
                Prompts.Object,
                Translator,
                History,
                DiffPreview,
                CostEstimator,
                Redactor,
                SelectionExtender,
                NullLogger<TextProcessor>.Instance);
            Sut.ProcessingStarted += (_, e) =>
            {
                StartedCount++;
                LastEstimatedCostUsd = e.EstimatedCostUsd;
            };
            Sut.ProcessingCompleted += (_, _) => CompletedCount++;
            Sut.ProcessingUndone += (_, _) => UndoneCount++;
            Sut.ProcessingProgressClosed += (_, _) => ProgressClosedCount++;
            Sut.ProcessingStreamUpdated += (_, e) => StreamUpdates.Add(e.AccumulatedContent);
            Sut.ProcessingFailed += (_, e) => FailedEvents.Add(e);
            return Sut;
        }

        public Translator Translator { get; }

        public FakeClipboardService Clipboard { get; }

        public FakeInputSimulator Input { get; }

        public FakeModifierReleaseWaiter Modifiers { get; }

        public Mock<IConfigStore> ConfigStore { get; }

        public Mock<ICredentialStore> Credentials { get; }

        public Mock<IOpenRouterClient> OpenRouter { get; }

        public ScriptedLlmProviderFactory Providers { get; }

        public Mock<IPromptSelector> Prompts { get; }

        public InMemoryHistoryStore History { get; }

        public ScriptedDiffPreviewService DiffPreview { get; }

        public ScriptedCostEstimator CostEstimator { get; }

        public PrivacyRedactor Redactor { get; }

        public FakeTextSelectionExtender SelectionExtender { get; }

        public decimal? LastEstimatedCostUsd { get; private set; }

        public int StartedCount { get; private set; }

        public int CompletedCount { get; private set; }

        public int UndoneCount { get; private set; }

        public int ProgressClosedCount { get; private set; }

        public List<TextProcessingFailedEventArgs> FailedEvents { get; } = [];

        public List<string> StreamUpdates { get; } = [];

        public void SetupHappyPath()
        {
            Clipboard.OnGetText = ct =>
            {
                // First call returns ORIGINAL (initial snapshot), all subsequent return Selected.
                Clipboard.OnGetText = _ => Task.FromResult(Selected);
                return Task.FromResult("ORIGINAL");
            };

            var defaultPrompt = new Prompt { Text = "Improve text", PreserveLanguage = false };
            Prompts.Setup(x => x.SelectAsync(It.IsAny<HotkeyKind>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(defaultPrompt);

            OpenRouter
                .Setup(x => x.ImproveStreamAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .Returns(AsAsyncStreamAsync(ApiResult));
        }

        /// <summary>
        /// Replaces the default happy-path prompt with one that opts in to
        /// the diff preview AND flips the master experimental flag on,
        /// since both are required for the modal to fire (see
        /// <see cref="TextProcessor.ProcessAsync"/>). Call after
        /// <see cref="SetupHappyPath"/>.
        /// </summary>
        public void SetupPromptWithDiffPreview()
        {
            var prompt = new Prompt
            {
                Text = "Fix errors",
                PreserveLanguage = true,
                ShowDiffPreview = true,
            };
            Prompts.Setup(x => x.SelectAsync(It.IsAny<HotkeyKind>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(prompt);

            // The default happy-path config has both experimental flags off
            // (production default). Enable diff preview master so the
            // preview path runs; tests that want to explicitly verify the
            // master-off path override this with their own ConfigStore
            // setup before invoking the SUT.
            ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(AppConfig.Default with
                {
                    Model = "test/model",
                    Timeout = 30,
                    ExperimentalHistory = true,
                    ExperimentalDiffPreview = true,
                });
        }

        /// <summary>
        /// Enables the streaming master flag in the ConfigStore mock for
        /// tests that need to verify <c>ProcessingStreamUpdated</c> events
        /// fire. With production defaults (off) those events stay silent.
        /// </summary>
        public void SetupStreamingExperimentEnabled()
        {
            ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(AppConfig.Default with
                {
                    Model = "test/model",
                    Timeout = 30,
                    ExperimentalHistory = true,
                    ExperimentalStreaming = true,
                });
        }
    }
}
