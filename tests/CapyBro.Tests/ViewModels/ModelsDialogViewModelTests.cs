using CapyBro.Models;
using CapyBro.Services;
using CapyBro.ViewModels;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CapyBro.Tests.ViewModels;

/// <summary>
/// H11 (Z6-F1) regression suite — ModelsDialogViewModel was entirely
/// untested before this file existed.  Covers every state-machine
/// transition called out in the audit: empty-key / OpenRouter / generic
/// / cancellation error branches, reentrancy guard, ordering invariant,
/// and the filter no-matches state.
/// </summary>
[Collection(TranslatorCollection.Name)]
public class ModelsDialogViewModelTests
{
    [Fact]
    public async Task LoadAsync_EmptyApiKey_SetsUnauthorizedStatusAndLeavesModelsEmptyAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await harness.Sut.LoadAsync();

        harness.Sut.Models.Should().BeEmpty(
            "no key means no catalogue fetch — caller must not see a stale list");
        harness.Sut.StatusMessage.Should().Be(harness.Translator["api_unauthorized"]);
        harness.Sut.IsLoading.Should().BeFalse("finally must reset IsLoading regardless of branch");
    }

    [Fact]
    public async Task LoadAsync_OpenRouterException_SurfacesExceptionMessageAsStatusAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OpenRouterException("Недійсний API-ключ"));

        await harness.Sut.LoadAsync();

        harness.Sut.StatusMessage.Should().Be(
            "Недійсний API-ключ",
            "OpenRouterException carries a pre-localised message — surface it verbatim");
        harness.Sut.Models.Should().BeEmpty();
        harness.Sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_GenericException_SetsApiUnknownErrorStatusAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await harness.Sut.LoadAsync();

        harness.Sut.StatusMessage.Should().Be(
            harness.Translator["api_unknown_error"],
            "unknown exception types must collapse to the generic localised label, never to the raw message");
        harness.Sut.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_OperationCanceledException_DoesNotOverwriteStatusButClearsIsLoadingAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => harness.Sut.LoadAsync();

        // ProcessAsync filter `when ex is not OperationCanceledException`
        // means OCE re-throws past the SUT — confirm both: status stays
        // empty AND finally cleared IsLoading.
        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Sut.StatusMessage.Should().BeEmpty(
            "the catch filter excludes OperationCanceledException — status must NOT be overwritten with api_unknown_error");
        harness.Sut.IsLoading.Should().BeFalse(
            "finally must reset IsLoading even when the exception escapes the SUT");
    }

    [Fact]
    public async Task LoadAsync_SecondCallWhileFirstInFlight_ShortCircuitsAndDoesNotFetchTwiceAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");

        var gate = new TaskCompletionSource<IReadOnlyList<string>>();
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        var first = harness.Sut.LoadAsync();
        // Yield once so the first call's IsLoading=true line executes
        // before we kick off the second one — the reentrancy guard only
        // protects against overlap *after* IsLoading flipped to true.
        await Task.Yield();

        var second = harness.Sut.LoadAsync();

        await second; // second short-circuits and returns immediately.
        harness.OpenRouter.Verify(
            x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the reentrancy guard must skip the second fetch entirely; only the in-flight first call hits OpenRouter");

        // Release the first call so the test can clean up deterministically.
        gate.SetResult(["m1"]);
        await first;
    }

    [Fact]
    public async Task LoadAsync_PopulatesModels_OrderedOrdinalAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["openai/gpt-4o", "anthropic/claude-3.5-sonnet", "openai/gpt-4o-mini"]);

        await harness.Sut.LoadAsync();

        harness.Sut.Models.Should().HaveCount(3);
        harness.Sut.Models.Should().BeInAscendingOrder(
            StringComparer.Ordinal,
            "the SUT sorts the catalogue by ordinal string comparison so the dialog rendering is deterministic across runs");
        harness.Sut.StatusMessage.Should().Contain(
            "3",
            "the success status reports the loaded count via Format(\"msg_models_loaded\", count)");
    }

    [Fact]
    public async Task LoadAsync_EmptyCatalogue_SetsExplicitCatalogueEmptyStatusAsync()
    {
        // M16 (Z6-F3) regression: pre-fix a successful fetch that
        // returned `{"data":[]}` (or null) left StatusMessage as the
        // empty string set at LoadAsync line 63, so the dialog was
        // indistinguishable from a "loading hasn't started" state.
        // ApplyFilter now sets msg_models_catalogue_empty when the
        // underlying catalogue is empty AND no other status arm fired.
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        await harness.Sut.LoadAsync();

        harness.Sut.Models.Should().BeEmpty();
        harness.Sut.StatusMessage.Should().Be(
            harness.Translator["msg_models_catalogue_empty"],
            "an empty catalogue must announce itself — pre-fix the dialog stayed silent and looked broken");
    }

    [Fact]
    public async Task Filter_NarrowingPreservesSelection_DoesNotReassignCollectionAsync()
    {
        // M18 (Z6-F5) regression: ApplyFilter used to do
        // `Models = new ObservableCollection<string>(filtered)` on
        // every keystroke.  That reassignment forced the
        // CollectionViewSource to regenerate the grouped view and
        // re-evaluate every per-item converter, and (under the 150ms
        // Delay binding) caused SelectedItem flicker.  After the fix
        // ApplyFilter mutates Models in place — the assertion below
        // pins the reference-identity invariant so a future refactor
        // back to `new ObservableCollection<...>` triggers a fail.
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["openai/gpt-4o", "openai/gpt-4o-mini", "anthropic/claude-3.5-sonnet"]);
        await harness.Sut.LoadAsync();
        var initialReference = harness.Sut.Models;

        harness.Sut.Filter = "openai";

        ReferenceEquals(harness.Sut.Models, initialReference).Should().BeTrue(
            "Models is mutated in place — reassigning a new collection breaks SelectedItem preservation and CollectionViewSource grouping continuity");
        harness.Sut.Models.Should().HaveCount(2);
    }

    // Z7-F3 / M19 regression: when the user switches UI language while
    // the ModelsDialog is open, the cached StatusMessage must re-resolve
    // through Translator instead of staying frozen in the locale active
    // at LoadAsync time.  Pre-fix the StatusMessage was assigned by
    // `_translator[key]` evaluation at one point in time and never
    // refreshed; an EN-flavoured "Invalid API key" lingered after the
    // user flipped Settings → Language to Ukrainian.
    [Fact]
    public async Task LoadAsync_KeyedStatus_ReResolvesOnMidSessionLanguageSwitchAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        harness.Translator.SetLanguage(Language.English);
        try
        {
            await harness.Sut.LoadAsync();
            harness.Sut.StatusMessage.Should().Be(
                "Invalid API key",
                "the no-key path uses the api_unauthorized key — English resolves to 'Invalid API key'");

            harness.Translator.SetLanguage(Language.Ukrainian);
            harness.Sut.StatusMessage.Should().Be(
                "Недійсний API-ключ",
                "mid-session language switch must re-resolve the cached key through Translator");
        }
        finally
        {
            // Reset for sibling tests in the TranslatorSingleton collection.
            harness.Translator.SetLanguage(Language.English);
            harness.Sut.Dispose();
        }
    }

    [Fact]
    public async Task LoadAsync_RawStatus_StaysFrozenAcrossLanguageSwitchAsync()
    {
        // The OpenRouterException branch sets StatusMessage from
        // ex.Message — a dynamically-built string that has no Translator
        // key.  After a language switch the raw text must stay verbatim;
        // re-resolving an absent key would yield the raw key string and
        // wipe the user-visible error reason.
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OpenRouterException("Server returned HTTP 502"));

        harness.Translator.SetLanguage(Language.English);
        try
        {
            await harness.Sut.LoadAsync();
            harness.Sut.StatusMessage.Should().Be("Server returned HTTP 502");

            harness.Translator.SetLanguage(Language.Ukrainian);
            harness.Sut.StatusMessage.Should().Be(
                "Server returned HTTP 502",
                "raw status (no Translator key behind it) must NOT be touched on language switch");
        }
        finally
        {
            harness.Translator.SetLanguage(Language.English);
            harness.Sut.Dispose();
        }
    }

    [Fact]
    public void Dispose_UnsubscribesFromTranslator_AfterDisposeLanguageSwitchDoesNotMutateStatus()
    {
        // M19 leak guard: ModelsDialogViewModel is DI-registered as
        // Transient and constructed fresh per ModelBrowser.BrowseAsync.
        // Without the Dispose-unsubscribe, every dialog open would leave
        // a strong-ref delegate in Translator's PropertyChanged
        // invocation list, leaking the entire VM (and its captured
        // services) for the rest of the app's lifetime.
        var harness = new Harness();
        harness.Translator.SetLanguage(Language.English);
        try
        {
            // Force the source into the keyed state so OnTranslatorPropertyChanged
            // would have something to do if it were still subscribed.
            harness.Sut.StatusMessage = "stale";
            // Drive ApplyFilter via Filter change to land in a keyed status —
            // empty catalogue path sets msg_models_catalogue_empty.
            harness.Sut.Filter = "x";

            harness.Sut.Dispose();
            var afterDispose = harness.Sut.StatusMessage;

            harness.Translator.SetLanguage(Language.Ukrainian);

            // After Dispose, the handler is detached, so a language switch
            // is a no-op for this VM.  We don't care about the exact value
            // — we only care that it didn't change in response to the
            // language flip.
            harness.Sut.StatusMessage.Should().Be(
                afterDispose,
                "Dispose must unsubscribe from Translator.PropertyChanged; otherwise the disposed VM would still react to language switches and could mutate state after teardown");
        }
        finally
        {
            harness.Translator.SetLanguage(Language.English);
        }
    }

    [Fact]
    public async Task Filter_NoMatches_SetsSearchEmptyStatusAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["openai/gpt-4o", "anthropic/claude-3.5-sonnet"]);
        await harness.Sut.LoadAsync();

        harness.Sut.Filter = "no-such-vendor/imaginary";

        harness.Sut.Models.Should().BeEmpty(
            "the filter substring does not match any catalogue id; the visible list must collapse");
        harness.Sut.StatusMessage.Should().Be(
            harness.Translator["msg_models_search_empty"],
            "the dialog must say so explicitly — pre-fix this was a hollow pane with no signal");
    }

    // 2026-05-12 regression: typing a filter that matched nothing left the
    // "no matches" status pinned even after the user typed a different filter
    // that DID match — the visible list re-populated but the underneath
    // status line still said "Нічого не знайдено за вашим запитом",
    // visually contradicting the rendered rows.  Pre-fix the
    // Models.Count > 0 branch in ApplyFilter was gated on
    // IsNullOrEmpty(StatusMessage); once the no-matches branch had run, that
    // guard blocked every subsequent transition back to a populated state.
    [Fact]
    public async Task Filter_NoMatchThenMatch_StatusUpdatesToLoadedCountAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[
                "openai/gpt-4o",
                "google/gemini-2.0-flash-001",
                "google/gemini-2.5-flash",
                "anthropic/claude-3.5-sonnet"]);
        await harness.Sut.LoadAsync();

        // First filter excludes everything — sets the no-matches status.
        harness.Sut.Filter = "no-such-vendor/imaginary";
        harness.Sut.StatusMessage.Should().Be(
            harness.Translator["msg_models_search_empty"],
            "sanity check: the no-match branch must fire first so we can verify the recovery transition");

        // Second filter matches a subset — the bug surface.
        harness.Sut.Filter = "google";

        harness.Sut.Models.Should().NotBeEmpty(
            "the substring 'google' matches two seeded ids — the visible list must repopulate");
        harness.Sut.StatusMessage.Should().Contain(
            "4",
            "status must re-evaluate to the loaded-count message once results return; staying on the stale 'no matches' string visually contradicts the populated list");
    }

    // Z9-F1 / M24 regression: HasActiveFilter drives the no-matches empty-
    // state overlay's MultiDataTrigger.  The truth table must mirror what
    // ApplyFilter treats as a real filter (IsNullOrWhiteSpace), otherwise
    // a whitespace-only user input would paint the no-matches overlay on
    // top of an unfiltered list — same UX bug shape as M14 on HistoryTab.
    [Theory]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("\t", false)]
    [InlineData("   ", false)]
    [InlineData("a", true)]
    [InlineData("openai", true)]
    [InlineData("  openai  ", true)]
    public void HasActiveFilter_ReflectsIsNullOrWhiteSpaceContract(string filter, bool expected)
    {
        var harness = new Harness();

        harness.Sut.Filter = filter;

        harness.Sut.HasActiveFilter.Should().Be(
            expected,
            "HasActiveFilter must align with ApplyFilter's IsNullOrWhiteSpace check so the no-matches overlay only paints when a real filter is in effect");
    }

    // Z9-F1 / M24 regression: changing Filter must raise PropertyChanged
    // for HasActiveFilter so the XAML MultiDataTrigger picks up the new
    // state.  CommunityToolkit's [ObservableProperty] doesn't auto-notify
    // for derived properties, so the partial OnFilterChanged hook must
    // fire it explicitly.
    [Fact]
    public void OnFilterChanged_RaisesHasActiveFilterPropertyChanged()
    {
        var harness = new Harness();
        var raised = new List<string>();
        harness.Sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        harness.Sut.Filter = "openai";

        raised.Should().Contain(
            nameof(ModelsDialogViewModel.HasActiveFilter),
            "the derived property's notification is required for the XAML overlay's MultiDataTrigger to re-evaluate when the user types into the filter box");
    }

    private sealed class Harness
    {
        public Harness()
        {
            Credentials = new Mock<ICredentialStore>(MockBehavior.Loose);
            Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            OpenRouter = new Mock<IOpenRouterClient>(MockBehavior.Loose);

            Translator = Translator.Instance;

            Sut = new ModelsDialogViewModel(
                OpenRouter.Object,
                Credentials.Object,
                Translator,
                NullLogger<ModelsDialogViewModel>.Instance);
        }

        public ModelsDialogViewModel Sut { get; }

        public Mock<IOpenRouterClient> OpenRouter { get; }

        public Mock<ICredentialStore> Credentials { get; }

        public Translator Translator { get; }
    }
}
