using CapyBro.Models;
using CapyBro.Services;
using CapyBro.ViewModels;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CapyBro.Tests.ViewModels;

[Collection(TranslatorCollection.Name)]
public class OnboardingWizardViewModelTests
{
    [Fact]
    public void StartsAtStepZero_AndIsOnWelcomeStep()
    {
        var harness = new Harness();

        harness.Sut.CurrentStep.Should().Be(0);
        harness.Sut.IsWelcomeStep.Should().BeTrue();
        harness.Sut.IsLastStep.Should().BeFalse();
        harness.Sut.CanGoBack.Should().BeFalse(
            "no Back navigation on the very first page");
    }

    [Fact]
    public void NextCommand_AdvancesAcrossAllSteps()
    {
        // Post-restructure the wizard has 4 pages (was 5):
        //   0 Welcome+Language
        //   1 API key
        //   2 Hotkeys
        //   3 Done
        // The dedicated Language step was merged into Welcome at the
        // user's request so the language picker sits on the very first
        // screen.
        var harness = new Harness();

        harness.Sut.NextCommand.Execute(null);
        harness.Sut.IsApiKeyStep.Should().BeTrue();
        harness.Sut.NextCommand.Execute(null);
        harness.Sut.IsHotkeyStep.Should().BeTrue();
        harness.Sut.NextCommand.Execute(null);
        harness.Sut.IsDoneStep.Should().BeTrue();
        harness.Sut.IsLastStep.Should().BeTrue();
        harness.Sut.CanGoNext.Should().BeFalse(
            "Next button is replaced by Finish on the last step");
    }

    [Fact]
    public void BackCommand_StepsBackThroughHistory()
    {
        var harness = new Harness();
        harness.Sut.NextCommand.Execute(null);
        harness.Sut.NextCommand.Execute(null);

        harness.Sut.IsHotkeyStep.Should().BeTrue();
        harness.Sut.BackCommand.Execute(null);
        harness.Sut.IsApiKeyStep.Should().BeTrue();
        harness.Sut.BackCommand.Execute(null);
        harness.Sut.IsWelcomeStep.Should().BeTrue();
    }

    [Fact]
    public void CanGoBack_OnLastStep_IsTrue_SoUserCanRevisitPreviousChoices()
    {
        // Regression: pre-fix CanGoBack required CurrentStep < TotalSteps - 1,
        // which collapsed the Back button on the Done page.  A user who
        // reached Done but realised they wanted to flip a hotkey / API
        // key / language had to Skip and restart the wizard.  Now Back
        // stays available on every non-zero step including the last —
        // user request, no test against the pre-fix behaviour to break.
        var harness = new Harness();

        // Walk to Done.
        harness.Sut.NextCommand.Execute(null);
        harness.Sut.NextCommand.Execute(null);
        harness.Sut.NextCommand.Execute(null);
        harness.Sut.IsDoneStep.Should().BeTrue("test prerequisite — reached the last page");

        harness.Sut.CanGoBack.Should().BeTrue(
            "Back must work on the Done page so the user can revisit and tweak their earlier picks");

        harness.Sut.BackCommand.Execute(null);
        harness.Sut.IsHotkeyStep.Should().BeTrue(
            "BackCommand from Done returns to the Hotkeys step (the previous page)");
    }

    [Fact]
    public async Task FinishCommand_PersistsLanguageHotkeyAndCompletionFlagAsync()
    {
        var harness = new Harness();
        harness.Sut.Language = Language.English;
        harness.Sut.Hotkey = "Ctrl+Alt+I";
        harness.Sut.ApiKey = "sk-test-123";

        await harness.Sut.FinishCommand.ExecuteAsync(null);

        harness.Sut.HasCompleted.Should().BeTrue();
        harness.ConfigStore.Verify(
            x => x.SaveAsync(
                It.Is<AppConfig>(c =>
                    c.Language == Language.English
                    && c.Hotkey == "Ctrl+Alt+I"
                    && c.OnboardingCompleted),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Finish must persist all three pieces in a single SaveAsync call");
        harness.Credentials.Verify(
            x => x.SetApiKeyAsync("sk-test-123", It.IsAny<CancellationToken>()),
            Times.Once,
            "API key must be written to the OS credential vault, not the JSON config");
    }

    [Fact]
    public async Task FinishCommand_ReregistersHotkey_WhenChosenHotkeyDiffersFromExistingAsync()
    {
        var harness = new Harness();
        harness.Sut.Hotkey = "Ctrl+Alt+I";

        await harness.Sut.FinishCommand.ExecuteAsync(null);

        harness.Hotkeys.Verify(
            x => x.TryRegister(HotkeyKind.Default, "Ctrl+Alt+I"),
            Times.Once,
            "user picked a non-default hotkey, so the OS-level registration must swap");
    }

    [Fact]
    public async Task FinishCommand_DoesNotCallCredentialStore_WhenApiKeyIsEmptyAsync()
    {
        // Regression: ICredentialStore.SetApiKeyAsync rejects whitespace via
        // ArgumentException, and the wizard previously called it
        // unconditionally with `ApiKey ?? string.Empty`.  When the user
        // clicked Finish without typing a key, the throw was swallowed but
        // polluted the log and short-circuited the post-credentials work
        // (translator.SetLanguage idempotent call).
        var harness = new Harness();
        harness.Sut.Language = Language.English;
        harness.Sut.Hotkey = "Ctrl+Alt+I";
        harness.Sut.ApiKey = string.Empty;

        await harness.Sut.FinishCommand.ExecuteAsync(null);

        harness.Sut.HasCompleted.Should().BeTrue();
        harness.Credentials.Verify(
            x => x.SetApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an empty API-key field on Finish must not call into the credential store");
        harness.ConfigStore.Verify(
            x => x.SaveAsync(
                It.Is<AppConfig>(c =>
                    c.Language == Language.English
                    && c.Hotkey == "Ctrl+Alt+I"
                    && c.OnboardingCompleted),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "the rest of the config must still be persisted on Finish even when the API key is blank");
    }

    [Fact]
    public async Task FinishCommand_DoesNotCallCredentialStore_WhenApiKeyIsWhitespaceAsync()
    {
        // Same regression as the empty-string case but the user has
        // accidentally pasted spaces or pressed Space in the field.  The
        // wizard treats whitespace as "no key entered".
        var harness = new Harness();
        harness.Sut.ApiKey = "   ";

        await harness.Sut.FinishCommand.ExecuteAsync(null);

        harness.Credentials.Verify(
            x => x.SetApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "whitespace-only ApiKey must be treated as 'no key entered'");
    }

    [Fact]
    public async Task FinishCommand_SetsWasFinishedFlag_SoHostCanOpenSettingsAsync()
    {
        // Regression: pre-fix, the wizard closed without any signal that
        // the user had clicked Done vs Skip vs the [×] close button — so
        // the host (App.xaml.cs) couldn't tell when to show Settings as
        // the next step.  WasFinished is true ONLY when FinishAsync ran
        // its happy path; the App's wizard.Closed handler keys "open
        // Settings" off this flag.
        var harness = new Harness();

        await harness.Sut.FinishCommand.ExecuteAsync(null);

        harness.Sut.WasFinished.Should().BeTrue(
            "Done button means the user explicitly opted into the app, so the host should transition them straight to Settings");
        harness.Sut.HasCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task SkipCommand_DoesNotSetWasFinished_LeavesAppInTrayOnlyModeAsync()
    {
        // Skip is the explicit "I don't want to engage" path.  HasCompleted
        // flips so the wizard doesn't reappear next launch, but WasFinished
        // stays false so App.xaml.cs leaves the app in tray-only mode
        // (the original pre-finish-fix behaviour).
        var harness = new Harness();

        await harness.Sut.SkipCommand.ExecuteAsync(null);

        harness.Sut.WasFinished.Should().BeFalse(
            "Skip preserves the dismiss-without-engaging semantic; only Done auto-opens Settings");
        harness.Sut.HasCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task PersistOnCloseAsync_FromBareWindowClose_DoesNotSetWasFinishedAsync()
    {
        // Closing the wizard via [×] before clicking any action button is
        // also a dismiss path — same contract as Skip.
        var harness = new Harness();

        await harness.Sut.PersistOnCloseAsync();

        harness.Sut.WasFinished.Should().BeFalse();
        harness.Sut.HasCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task SkipCommand_PersistsCompletionFlagOnly_LeavesOtherFieldsAtDefaultsAsync()
    {
        var harness = new Harness();
        harness.Sut.Language = Language.English;
        harness.Sut.Hotkey = "Ctrl+Alt+I";

        await harness.Sut.SkipCommand.ExecuteAsync(null);

        harness.Sut.HasCompleted.Should().BeTrue();
        harness.ConfigStore.Verify(
            x => x.SaveAsync(
                It.Is<AppConfig>(c =>
                    c.OnboardingCompleted
                    && c.Hotkey == AppConfig.Default.Hotkey
                    && c.Language == AppConfig.Default.Language),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Skip must save ONLY OnboardingCompleted; other fields stay at their existing values");
        harness.Credentials.Verify(
            x => x.SetApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Skip must not touch the credential store");
    }

    [Fact]
    public async Task PersistOnCloseAsync_TreatsBareWindowCloseAsSkipAsync()
    {
        var harness = new Harness();

        await harness.Sut.PersistOnCloseAsync();

        harness.Sut.HasCompleted.Should().BeTrue();
        harness.ConfigStore.Verify(
            x => x.SaveAsync(
                It.Is<AppConfig>(c => c.OnboardingCompleted),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "[×] before any wizard action must still mark the wizard complete so it does not reappear");
    }

    [Fact]
    public async Task PersistOnCloseAsync_IsNoOp_AfterFinishAsync()
    {
        var harness = new Harness();
        await harness.Sut.FinishCommand.ExecuteAsync(null);
        harness.ConfigStore.Reset();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { OnboardingCompleted = true });

        await harness.Sut.PersistOnCloseAsync();

        harness.ConfigStore.Verify(
            x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Finish already saved; the implicit close-handler must not double-write");
    }

    // Z8-F9 / L17 regression: an in-flight ApiKey validation probe at
    // Finish-time used to race the SaveAsync sequence — the probe's
    // continuation could mutate `ApiKeyState` after PersistChosenValuesAsync
    // had landed and immediately before the window tore down.  Post-fix
    // FinishAsync (and SkipAsync) cancel the in-flight probe via
    // `CancelInFlightValidation()` BEFORE persisting, so the probe's
    // GetCreditsAsync is never invoked when Finish runs inside the
    // debounce window.
    //
    // The debounce is 400ms; this test sets ApiKey and immediately calls
    // Finish, well before the Task.Delay elapses, so the validation never
    // reaches the HTTP call.  The assertion is that `GetCreditsAsync` was
    // not invoked AND `FinishAsync` still completed normally — pre-fix
    // these two outcomes were not jointly guaranteed under the race
    // window.
    [Fact]
    public async Task FinishAsync_WithDebouncedValidationInFlight_CancelsProbeBeforePersistingAsync()
    {
        var harness = new Harness();
        harness.OpenRouter.Invocations.Clear();

        // Trigger validation by typing into ApiKey — schedules the
        // debounced probe via ScheduleValidationAsync.
        harness.Sut.ApiKey = "sk-or-test-key-pending-validation";

        // Finish immediately, well inside the 400ms debounce window.
        await harness.Sut.FinishCommand.ExecuteAsync(null);

        // The cancellation closed the debounce delay; GetCreditsAsync
        // must NOT have been called.  Pre-fix the probe could still
        // fire (its CTS wasn't cancelled until window close), and any
        // response — success or failure — would mutate ApiKeyState
        // after the Finish path had committed.
        harness.OpenRouter.Verify(
            x => x.GetCreditsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "FinishAsync must cancel the in-flight probe before PersistChosenValuesAsync so the post-Finish HTTP response can't race the save");

        // Finish itself still completed: the wizard committed and asked
        // to close.
        harness.Sut.HasCompleted.Should().BeTrue();
        harness.Sut.WasFinished.Should().BeTrue();

        // Allow any background continuation (the cancelled Task.Delay
        // throw + catch) a moment to retire before test scope unwinds.
        await Task.Yield();
    }

    [Fact]
    public async Task SkipAsync_WithDebouncedValidationInFlight_AlsoCancelsProbeAsync()
    {
        // Mirror of the Finish test for the Skip path — both Persist
        // entry points must cancel the in-flight probe.
        var harness = new Harness();
        harness.OpenRouter.Invocations.Clear();

        harness.Sut.ApiKey = "sk-or-test-key-pending-validation";

        await harness.Sut.SkipCommand.ExecuteAsync(null);

        harness.OpenRouter.Verify(
            x => x.GetCreditsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Sut.HasCompleted.Should().BeTrue();
        harness.Sut.WasFinished.Should().BeFalse("Skip records Skip — not Finish");
        await Task.Yield();
    }

    [Fact]
    public void LanguageChange_FiresLanguagePreviewEvent()
    {
        var harness = new Harness();
        Language? observed = null;
        harness.Sut.LanguagePreviewChanged += (_, e) => observed = e.Language;

        harness.Sut.Language = Language.English;

        observed.Should().Be(
            Language.English,
            "the host wires this event to ITranslator.SetLanguage so subsequent steps render in the chosen language");
    }

    [Fact]
    public void StepIndicator_FormatsAsCurrentOfTotal()
    {
        // Post-restructure TotalSteps == 4 (was 5).  The harness sets the
        // translator back to Ukrainian for the assertion text — the
        // singleton's default is English now, but the test cares about
        // the format-string interpolation, not the locale.
        var harness = new Harness();
        harness.Translator.SetLanguage(Language.Ukrainian);
        harness.Sut.StepIndicator.Should().Be("Крок 1 з 4");
        harness.Sut.NextCommand.Execute(null);
        harness.Sut.StepIndicator.Should().Be("Крок 2 з 4");
    }

    [Fact]
    public void HotkeyConflict_AnyTwoMatch_BlocksAdvanceFromHotkeyStep()
    {
        // Post-restructure the wizard surfaces the same 3-hotkey
        // configuration as Settings → General with inline conflict
        // detection.  Pre-restructure only one hotkey was bindable in
        // the wizard, so a duplicate against MenuHotkey / UndoHotkey
        // could only be discovered after Finish (and would silently
        // overwrite one binding).  Now the user must resolve up front.
        var harness = new Harness();

        // Walk to the Hotkey step (index 2 post-restructure).
        harness.Sut.NextCommand.Execute(null); // Welcome → API key
        harness.Sut.NextCommand.Execute(null); // API key → Hotkeys
        harness.Sut.IsHotkeyStep.Should().BeTrue();

        // No conflict at defaults.
        harness.Sut.HasHotkeyConflict.Should().BeFalse();
        harness.Sut.CanGoNext.Should().BeTrue();

        // Pick the same combo for menu — instant conflict.
        harness.Sut.MenuHotkey = harness.Sut.Hotkey;
        harness.Sut.HasHotkeyConflict.Should().BeTrue();
        harness.Sut.HotkeyConflictMessage.Should().NotBeEmpty();
        harness.Sut.CanGoNext.Should().BeFalse(
            "the user must resolve duplicates before moving past the Hotkeys step — otherwise RegisterHotKey would silently fail at runtime for one of the bindings");

        // Resolving the conflict re-opens Next.
        harness.Sut.MenuHotkey = "Ctrl+Shift+Q";
        harness.Sut.HasHotkeyConflict.Should().BeFalse();
        harness.Sut.CanGoNext.Should().BeTrue();
    }

    [Fact]
    public async Task FinishCommand_PersistsAllThreeHotkeysAsync()
    {
        // Regression: pre-restructure only the improvement hotkey was
        // bindable + persisted.  Now MenuHotkey and UndoHotkey also flow
        // through to AppConfig — same shape as Settings → General.
        var harness = new Harness();
        harness.Sut.Hotkey = "Ctrl+Shift+E";
        harness.Sut.MenuHotkey = "Ctrl+Shift+M";
        harness.Sut.UndoHotkey = "Ctrl+Alt+Z";

        await harness.Sut.FinishCommand.ExecuteAsync(null);

        harness.ConfigStore.Verify(
            x => x.SaveAsync(
                It.Is<AppConfig>(c =>
                    c.Hotkey == "Ctrl+Shift+E"
                    && c.MenuHotkey == "Ctrl+Shift+M"
                    && c.UndoHotkey == "Ctrl+Alt+Z"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_StoredCredential_PrePopulatesApiKeyAsync()
    {
        // M23 (Z8-F7) regression: pre-fix a returning user (saved key
        // in Credential Manager, no v2 config — e.g. clean reinstall)
        // saw an empty API-key field and either re-typed (overwriting)
        // or left it blank thinking the wizard already had it.  The
        // VM's InitializeAsync now fetches the existing key so the
        // first-run gate is honest about what's still on the system.
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("sk-existing-key-from-prior-install");

        await harness.Sut.InitializeAsync();

        harness.Sut.ApiKey.Should().Be(
            "sk-existing-key-from-prior-install",
            "a returning user must see their stored key so they can simply click Next");
    }

    [Fact]
    public async Task InitializeAsync_NoStoredCredential_LeavesApiKeyEmptyAsync()
    {
        // Sanity counterpart: a fresh-install path with no stored key
        // must leave ApiKey unchanged (typically empty) so the
        // validation indicator stays in its quiet-until-typed state.
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        harness.Sut.ApiKey.Should().BeEmpty("pre-init baseline");

        await harness.Sut.InitializeAsync();

        harness.Sut.ApiKey.Should().BeEmpty(
            "no stored credential => no pre-populate; field stays in its quiet ApiKeyState=None state");
    }

    private sealed class Harness
    {
        public Harness()
        {
            ConfigStore = new Mock<IConfigStore>(MockBehavior.Loose);
            ConfigStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AppConfig.Default);
            ConfigStore.Setup(x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Credentials = new Mock<ICredentialStore>(MockBehavior.Loose);
            Credentials.Setup(x => x.SetApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Hotkeys = new Mock<IHotkeyManager>(MockBehavior.Loose);
            Hotkeys.Setup(x => x.TryRegister(It.IsAny<HotkeyKind>(), It.IsAny<string>())).Returns(true);

            // OpenRouter mock for the API-key validation probe added in
            // Phase E iteration 2.  Default behaviour: GetCreditsAsync
            // throws an OperationCanceledException so an unexpected call
            // (e.g. a Task.Delay debounce that raced past test teardown)
            // is silently dropped rather than flipping ApiKeyState in the
            // middle of an unrelated assertion.  Tests that exercise the
            // validation path can override the setup directly.
            OpenRouter = new Mock<IOpenRouterClient>(MockBehavior.Loose);
            OpenRouter.Setup(x => x.GetCreditsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            Translator = Translator.Instance;
            Translator.SetLanguage(Language.Ukrainian);

            Sut = new OnboardingWizardViewModel(
                ConfigStore.Object,
                Credentials.Object,
                Translator,
                Hotkeys.Object,
                OpenRouter.Object,
                NullLogger<OnboardingWizardViewModel>.Instance);
        }

        public OnboardingWizardViewModel Sut { get; }

        public Mock<IConfigStore> ConfigStore { get; }

        public Mock<ICredentialStore> Credentials { get; }

        public Mock<IHotkeyManager> Hotkeys { get; }

        public Mock<IOpenRouterClient> OpenRouter { get; }

        public Translator Translator { get; }
    }
}
