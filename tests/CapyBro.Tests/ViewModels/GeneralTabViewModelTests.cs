using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Tests.TestHelpers;
using CapyBro.ViewModels;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CapyBro.Tests.ViewModels;

public class GeneralTabViewModelTests
{
    [Fact]
    public async Task LoadFromConfigAsync_PopulatesPropertiesFromConfigAndCredentialsAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Language = Language.English,
                Model = "anthropic/claude-3.5-sonnet",
                Models = ["anthropic/claude-3.5-sonnet", "openai/gpt-4o"],
                Hotkey = "Ctrl+Alt+T",
                MenuHotkey = "Ctrl+Alt+M",
            });
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("stored-key");
        harness.Autostart.Setup(x => x.IsEnabled).Returns(true);

        await harness.Sut.LoadFromConfigAsync();

        harness.Sut.Language.Should().Be(Language.English);
        harness.Sut.SelectedModel.Should().Be("anthropic/claude-3.5-sonnet");
        harness.Sut.Models.Should().HaveCount(2);
        harness.Sut.ApiKey.Should().Be("stored-key");
        harness.Sut.Hotkey.Should().Be("Ctrl+Alt+T");
        harness.Sut.MenuHotkey.Should().Be("Ctrl+Alt+M");
        harness.Sut.AutostartEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task LoadFromConfigAsync_DoesNotPersistOrRegisterHotkeys_DuringInitialLoadAsync()
    {
        var harness = new Harness();

        await harness.Sut.LoadFromConfigAsync();

        harness.ConfigStore.Verify(
            x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "load shouldn't trigger save");
        harness.Hotkeys.Verify(x => x.UnregisterAll(), Times.Never);
        harness.Hotkeys.Verify(
            x => x.TryRegister(It.IsAny<HotkeyKind>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task LanguageChanged_AfterLoad_DelegatesToTranslatorAndPersistsAsync()
    {
        // Default-config language is English post-rebrand, so the test
        // must toggle to a different language to actually trigger the VM's
        // property setter and the debounced persist.  Pre-rebrand the
        // default was Ukrainian and the test set Language=English.
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();

        harness.Sut.Language = Language.Ukrainian;

        harness.Translator.Language.Should().Be(Language.Ukrainian);

        // Allow async persist to flush
        await Task.Delay(50);
        harness.ConfigStore.Verify(
            x => x.SaveAsync(It.Is<AppConfig>(c => c.Language == Language.Ukrainian), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task HotkeyChanged_AfterLoad_RereregistersAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        harness.Hotkeys.Invocations.Clear();

        harness.Sut.Hotkey = "Ctrl+Alt+I";

        harness.Hotkeys.Verify(x => x.UnregisterAll(), Times.AtLeastOnce);
        harness.Hotkeys.Verify(x => x.TryRegister(HotkeyKind.Default, "Ctrl+Alt+I"), Times.Once);
    }

    [Fact]
    public async Task AddModelCommand_ValidModel_AddsAndPersistsAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-test");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["openai/gpt-4o-mini", "custom/model"]);
        await harness.Sut.LoadFromConfigAsync();
        var initialCount = harness.Sut.Models.Count;

        await harness.Sut.AddModelCommand.ExecuteAsync("custom/model");

        harness.Sut.Models.Should().HaveCount(initialCount + 1);
        harness.Sut.Models.Should().Contain("custom/model");
        harness.Sut.SelectedModel.Should().Be("custom/model");
        harness.Notifications.Verify(x => x.ShowInfo(It.Is<string>(s => s.Contains("custom/model"))), Times.Once);
    }

    [Fact]
    public async Task AddModelCommand_ModelNotInCatalog_DoesNotAddAndShowsErrorAsync()
    {
        var harness = new Harness();
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("sk-test");
        harness.OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["openai/gpt-4o-mini"]);
        await harness.Sut.LoadFromConfigAsync();
        var initialCount = harness.Sut.Models.Count;

        await harness.Sut.AddModelCommand.ExecuteAsync("nonsense/imaginary");

        harness.Sut.Models.Should().HaveCount(initialCount);
        harness.Sut.Models.Should().NotContain("nonsense/imaginary");
        harness.Notifications.Verify(x => x.ShowError(It.Is<string>(s => s.Contains("nonsense/imaginary"))), Times.Once);
    }

    [Fact]
    public async Task AddModelCommand_NoApiKey_ShowsUnauthorizedErrorAsync()
    {
        var harness = new Harness();
        // Default Credentials mock returns null for GetApiKeyAsync — no key.
        await harness.Sut.LoadFromConfigAsync();

        await harness.Sut.AddModelCommand.ExecuteAsync("custom/model");

        harness.Sut.Models.Should().NotContain("custom/model");
        harness.OpenRouter.Verify(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Notifications.Verify(x => x.ShowError(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AddModelCommand_DuplicateModel_IsNoOpAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        var beforeCount = harness.Sut.Models.Count;

        await harness.Sut.AddModelCommand.ExecuteAsync(harness.Sut.Models[0]);

        harness.Sut.Models.Should().HaveCount(beforeCount);
        // No catalogue fetch on duplicate — short-circuit before validation.
        harness.OpenRouter.Verify(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoveSelectedModelCommand_RemovesAndAdjustsSelectionAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Models = ["m1", "m2", "m3"],
                Model = "m2",
            });
        await harness.Sut.LoadFromConfigAsync();

        await harness.Sut.RemoveSelectedModelCommand.ExecuteAsync(null);

        harness.Sut.Models.Should().NotContain("m2");
        harness.Sut.Models.Should().HaveCount(2);
        // Per §6.2 we explicitly set SelectedModel after ItemsSource change.
        harness.Sut.SelectedModel.Should().Be("m3", "selection moves to next at same index");
    }

    [Fact]
    public async Task HotkeyChanged_ConflictsWithMenuHotkey_RevertsAndShowsErrorAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Hotkey = "Ctrl+Shift+E",
                MenuHotkey = "Ctrl+Shift+Q",
                UndoHotkey = "Ctrl+Shift+Z",
            });
        await harness.Sut.LoadFromConfigAsync();
        harness.Hotkeys.Invocations.Clear();
        harness.ConfigStore.Invocations.Clear();

        // Try to clobber Hotkey with a value that already lives in MenuHotkey.
        harness.Sut.Hotkey = "Ctrl+Shift+Q";

        // UI reverted back to the last valid value.
        harness.Sut.Hotkey.Should().Be("Ctrl+Shift+E");
        // No save, no re-register.
        harness.ConfigStore.Verify(
            x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Hotkeys.Verify(x => x.UnregisterAll(), Times.Never);
        // User-visible feedback.
        harness.Notifications.Verify(
            x => x.ShowError(It.Is<string>(s => s.Contains("Ctrl+Shift+Q"))),
            Times.Once);
    }

    [Fact]
    public async Task HotkeyChanged_ConflictsWithUndoHotkey_RevertsAndShowsErrorAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Hotkey = "Ctrl+Shift+E",
                MenuHotkey = "Ctrl+Shift+Q",
                UndoHotkey = "Ctrl+Shift+Z",
            });
        await harness.Sut.LoadFromConfigAsync();

        harness.Sut.MenuHotkey = "Ctrl+Shift+Z"; // collides with UndoHotkey

        harness.Sut.MenuHotkey.Should().Be("Ctrl+Shift+Q");
        harness.Notifications.Verify(
            x => x.ShowError(It.Is<string>(s => s.Contains("Ctrl+Shift+Z"))),
            Times.Once);
    }

    [Fact]
    public async Task HotkeyChanged_ConflictDetected_CaseInsensitivelyAsync()
    {
        // "ctrl+shift+e" parses to the same accelerator as "Ctrl+Shift+E";
        // the conflict guard must compare on the parsed Win32 (modifier|VK)
        // tuple, not on raw string equality.
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Hotkey = "Ctrl+Shift+E",
                MenuHotkey = "Ctrl+Shift+Q",
                UndoHotkey = "Ctrl+Shift+Z",
            });
        await harness.Sut.LoadFromConfigAsync();

        harness.Sut.UndoHotkey = "ctrl+shift+e";

        harness.Sut.UndoHotkey.Should().Be("Ctrl+Shift+Z");
        harness.Notifications.Verify(x => x.ShowError(It.IsAny<string>()), Times.Once);
    }

    // FZ2-F3 / M33 regression: pre-fix the only feedback a user got for
    // a hotkey conflict was a 3.5 s toast plus an immediate auto-revert.
    // Looking away from the toast meant losing the explanation entirely.
    // Now the VM exposes per-slot inline conflict messages bound to a
    // warning glyph next to each ComboBox; the message outlives the toast
    // until the user starts editing again.
    [Fact]
    public async Task HotkeyChanged_Conflict_SetsInlineConflictMessage_ClearsOnNextValidEditAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Hotkey = "Ctrl+Shift+E",
                MenuHotkey = "Ctrl+Shift+Q",
                UndoHotkey = "Ctrl+Shift+Z",
            });
        await harness.Sut.LoadFromConfigAsync();
        harness.Sut.HotkeyConflictMessage.Should().BeEmpty(
            "no conflict has happened yet — glyph must stay collapsed");

        // Attempted assignment of MenuHotkey's value into Hotkey → conflict.
        harness.Sut.Hotkey = "Ctrl+Shift+Q";

        harness.Sut.HotkeyConflictMessage.Should().Contain(
            "Ctrl+Shift+Q",
            "the inline message must mention the attempted value so the user " +
            "recognises which keystroke triggered the conflict, even though " +
            "the ComboBox itself reverted to the prior valid value");

        // User retypes a valid combo — the inline message must clear so
        // the glyph disappears.  Pre-fix this branch did not touch the
        // conflict message field; the warning lingered indefinitely.
        harness.Sut.Hotkey = "Ctrl+Alt+T";
        harness.Sut.HotkeyConflictMessage.Should().BeEmpty(
            "valid edit must dismiss the conflict glyph");
    }

    [Fact]
    public async Task HotkeyChanged_MenuConflict_PopulatesMenuHotkeyConflictMessageSlotAsync()
    {
        // Each hotkey slot has its OWN inline conflict message — a conflict
        // on MenuHotkey must not bleed into HotkeyConflictMessage.
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Hotkey = "Ctrl+Shift+E",
                MenuHotkey = "Ctrl+Shift+Q",
                UndoHotkey = "Ctrl+Shift+Z",
            });
        await harness.Sut.LoadFromConfigAsync();

        harness.Sut.MenuHotkey = "Ctrl+Shift+Z";

        harness.Sut.MenuHotkeyConflictMessage.Should().NotBeEmpty();
        harness.Sut.HotkeyConflictMessage.Should().BeEmpty(
            "slot routing: the message must land on the slot the user just edited");
        harness.Sut.UndoHotkeyConflictMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task HotkeyChanged_NoConflict_PersistsNormallyAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        harness.Hotkeys.Invocations.Clear();
        harness.ConfigStore.Invocations.Clear();
        harness.Notifications.Invocations.Clear();

        harness.Sut.Hotkey = "Ctrl+Alt+T"; // not used by other slots

        harness.Sut.Hotkey.Should().Be("Ctrl+Alt+T");
        harness.Hotkeys.Verify(
            x => x.TryRegister(HotkeyKind.Default, "Ctrl+Alt+T"),
            Times.Once);
        harness.Notifications.Verify(x => x.ShowError(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HotkeyChanged_ClearedToEmpty_AllowedNoConflictCheckAsync()
    {
        // An empty / whitespace value means "this hotkey slot is unused";
        // we should let the user clear it without firing a conflict alert
        // even though both other slots are populated.
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        harness.Notifications.Invocations.Clear();

        harness.Sut.MenuHotkey = string.Empty;

        harness.Sut.MenuHotkey.Should().Be(string.Empty);
        harness.Notifications.Verify(x => x.ShowError(It.IsAny<string>()), Times.Never);
    }

    // Z2-F7 / M6 regression: pre-fix an unparseable value (e.g. "asdf") was
    // logged-and-left-in-the-VM — the ComboBox kept showing garbage AND
    // `_lastAppliedHotkey` was stale relative to the visible field.  Now the
    // parse-rejection branch reverts to the last committed value, mirroring
    // the conflict-path UX, and exposes an inline glyph via
    // HotkeyConflictMessage so the user understands why the field snapped.
    [Fact]
    public async Task HotkeyChanged_Unparseable_RevertsToLastValidAndSetsInlineMessageAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Hotkey = "Ctrl+Shift+E",
                MenuHotkey = "Ctrl+Shift+Q",
                UndoHotkey = "Ctrl+Shift+Z",
            });
        await harness.Sut.LoadFromConfigAsync();
        harness.Hotkeys.Invocations.Clear();
        harness.ConfigStore.Invocations.Clear();
        harness.Notifications.Invocations.Clear();

        harness.Sut.Hotkey = "asdf";

        // UI snaps back to the last committed value — same shape as conflict revert.
        harness.Sut.Hotkey.Should().Be("Ctrl+Shift+E");
        // Inline message surfaces the rejected value so the user knows why the field changed.
        harness.Sut.HotkeyConflictMessage.Should().Contain("asdf");
        // Neither persisted nor re-registered: garbage doesn't touch disk or Win32.
        harness.ConfigStore.Verify(
            x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.Hotkeys.Verify(x => x.UnregisterAll(), Times.Never);
        // Toast is reserved for conflicts; an unparseable typo is just a typo,
        // the inline glyph + auto-revert is enough feedback.
        harness.Notifications.Verify(x => x.ShowError(It.IsAny<string>()), Times.Never);
    }

    // Z2-F7 / M6 regression: the snapshot-machinery invariant.  After an
    // unparseable typo followed by a conflict, the revert must land on the
    // value committed BEFORE the typo — not on the typo itself (pre-fix the
    // typo lived in the VM property unchallenged, so the snapshot path was
    // technically still correct only because `_lastAppliedHotkey` was never
    // touched by it).  This test pins the cleaner post-fix flow where the
    // typo is reverted immediately, so the conflict revert never has a
    // chance to mis-fire.
    [Fact]
    public async Task HotkeyChanged_UnparseableThenConflict_RevertsToLastValidValueAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with
            {
                Hotkey = "Ctrl+Shift+E",
                MenuHotkey = "Ctrl+Shift+Q",
                UndoHotkey = "Ctrl+Shift+Z",
            });
        await harness.Sut.LoadFromConfigAsync();

        // Step 1 — unparseable typo: now reverted immediately + inline glyph set.
        harness.Sut.Hotkey = "asdf";
        harness.Sut.Hotkey.Should().Be("Ctrl+Shift+E");
        harness.Sut.HotkeyConflictMessage.Should().Contain("asdf");

        // Step 2 — conflict with MenuHotkey: also reverts to the same baseline.
        // The inline message must update to mention the new attempted value
        // (the prior unparseable explanation is stale).
        harness.Sut.Hotkey = "Ctrl+Shift+Q";
        harness.Sut.Hotkey.Should().Be("Ctrl+Shift+E");
        harness.Sut.HotkeyConflictMessage.Should().Contain("Ctrl+Shift+Q");
        harness.Sut.HotkeyConflictMessage.Should().NotContain("asdf");
    }

    // FZ5-F4 / L33 regression: the autostart-toggle handler must invoke
    // `IAutostartService.Enable` / `Disable` SYNCHRONOUSLY inside the
    // partial-on-changed callback, NOT via a fire-and-forget task.  An
    // earlier audit observation saw a "2-second lag" between the
    // checkbox flip and a Get-ItemProperty registry read picking up the
    // new value — that was PowerShell's cached view of the registry
    // hive, not an app-level race; the in-process write completes
    // before the partial-on-changed returns.  A future refactor that
    // wrapped the call in `_ = Task.Run(...)` would re-introduce a
    // genuine race that the C9 OnExit flush can't catch (autostart is
    // a registry write, not a debounced ConfigStore.SaveAsync).  This
    // test pins the synchronous-write contract.
    [Fact]
    public async Task AutostartEnabledChanged_InvokesAutostartServiceSynchronouslyAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        harness.Autostart.Invocations.Clear();

        harness.Sut.AutostartEnabled = true;
        // Assert immediately — no `await Task.Delay(...)`.  If the
        // production code is refactored to fire-and-forget, this
        // immediate assertion would fail because the threadpool hasn't
        // scheduled the call yet.
        harness.Autostart.Verify(x => x.Enable(), Times.Once);
        harness.Autostart.Verify(x => x.Disable(), Times.Never);

        harness.Autostart.Invocations.Clear();
        harness.Sut.AutostartEnabled = false;
        harness.Autostart.Verify(x => x.Disable(), Times.Once);
        harness.Autostart.Verify(x => x.Enable(), Times.Never);
    }

    // Z2-F8 / L4 regression: the AppConfig.Timeout field used to be
    // persisted with no UI affordance — only hand-editable via the
    // JSON file.  v14 surfaces it under the developer-mode-gated Beta-
    // features section as `TimeoutSeconds`.  Pin the round-trip:
    // (1) Load reads config.Timeout into TimeoutSeconds, (2) a user
    // change persists back to AppConfig.Timeout, (3) the WithDefaultsApplied
    // shape's "<= 0 → restore default" clamp at AppConfig.cs:307 is
    // mirrored at write time so the user can't type 0 or a negative
    // and break TextProcessor's CancelAfter contract.
    [Fact]
    public async Task TimeoutSeconds_LoadsFromConfigAndPersistsBackAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { Timeout = 90 });
        await harness.Sut.LoadFromConfigAsync();

        harness.Sut.TimeoutSeconds.Should().Be(
            90,
            "load must populate the UI from the persisted Timeout field");

        AppConfig? saved = null;
        harness.ConfigStore
            .Setup(x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()))
            .Callback<AppConfig, CancellationToken>((cfg, _) => saved = cfg)
            .Returns(Task.CompletedTask);

        harness.Sut.TimeoutSeconds = 60;
        await Task.Delay(50);

        saved.Should().NotBeNull("a TimeoutSeconds change must trigger PersistConfigAsync");
        saved!.Timeout.Should().Be(
            60,
            "the new TimeoutSeconds value must be written back to AppConfig.Timeout — not silently ignored");
    }

    // v14: only NEGATIVE values clamp.  0 is a SUPPORTED sentinel
    // ("wait indefinitely") that TextProcessor translates to
    // Timeout.InfiniteTimeSpan; OpenRouterClient skips CancelAfter
    // for that case.  Pre-v14 the clamp gated 0 too because there was
    // no infinite-timeout pathway and TimeSpan.FromSeconds(0) cancels
    // immediately.  The L4 / Z2-F8 reasoning still holds for negative
    // values — TimeSpan.FromSeconds(-N) would also cancel immediately.
    [Theory]
    [InlineData(-1)]
    [InlineData(-5)]
    [InlineData(-9999)]
    public async Task TimeoutSeconds_Negative_PersistsAsDefaultAsync(int badValue)
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();

        AppConfig? saved = null;
        harness.ConfigStore
            .Setup(x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()))
            .Callback<AppConfig, CancellationToken>((cfg, _) => saved = cfg)
            .Returns(Task.CompletedTask);

        harness.Sut.TimeoutSeconds = badValue;
        await Task.Delay(50);

        saved.Should().NotBeNull();
        saved!.Timeout.Should().Be(
            AppConfig.Default.Timeout,
            "negative values must clamp to the documented default at persist time so they can't reach TextProcessor's CancellationTokenSource.CancelAfter contract");
    }

    // v14 sentinel: 0 means "wait indefinitely" and must pass through
    // the persist clamp unchanged so TextProcessor sees it and
    // translates to Timeout.InfiniteTimeSpan.  Pre-fix the clamp
    // collapsed 0 to Default.Timeout (60) and there was no way for
    // the user to disable the timeout entirely.
    [Fact]
    public async Task TimeoutSeconds_Zero_PersistsAsZero_AsInfiniteSentinelAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();

        AppConfig? saved = null;
        harness.ConfigStore
            .Setup(x => x.SaveAsync(It.IsAny<AppConfig>(), It.IsAny<CancellationToken>()))
            .Callback<AppConfig, CancellationToken>((cfg, _) => saved = cfg)
            .Returns(Task.CompletedTask);

        harness.Sut.TimeoutSeconds = 0;
        await Task.Delay(50);

        saved.Should().NotBeNull("setting TimeoutSeconds = 0 must still trigger PersistConfigAsync");
        saved!.Timeout.Should().Be(
            0,
            "0 is the documented 'no timeout' sentinel — the persist clamp must NOT collapse it to the default; TextProcessor relies on the literal 0 to switch to Timeout.InfiniteTimeSpan");
    }

    // FZ5-F4 / L33 follow-on: an exception inside `_autostart.Enable()`
    // (e.g. UnauthorizedAccessException on a kiosk-profile machine where
    // the Run-key write is blocked) must revert the checkbox so the
    // UI reflects the actual state.  The existing implementation
    // catches and reverts; this test pins that contract so a future
    // refactor that drops the revert leaves the checkbox in a
    // "claimed-on-but-not-persisted" lie.
    [Fact]
    public async Task AutostartEnabledChanged_WhenAutostartServiceThrows_RevertsCheckboxAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        harness.Sut.AutostartEnabled.Should().BeFalse("baseline: harness defaults to disabled");
        harness.Autostart
            .Setup(x => x.Enable())
            .Throws(new UnauthorizedAccessException("Run key write blocked by policy"));

        // Setting AutostartEnabled=true triggers Enable() which throws;
        // the catch reverts the property back to false.
        // The handler also pops a MessageBox via System.Windows.MessageBox
        // — running these tests headless, MessageBox.Show returns
        // immediately with MessageBoxResult.None and the catch flow
        // proceeds.
        harness.Sut.AutostartEnabled = true;

        harness.Sut.AutostartEnabled.Should().BeFalse(
            "Enable() threw, so the catch path must revert the property and the persisted state must match what the user sees");
    }

    [Fact]
    public async Task ApiKeyChanged_AfterDebounce_PersistsAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        harness.Credentials.Invocations.Clear();

        harness.Sut.ApiKey = "new-key";
        await Task.Delay(700); // wait past 400ms debounce

        harness.Credentials.Verify(
            x => x.SetApiKeyAsync("new-key", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task FlushApiKeyAsync_PersistsImmediatelyAsync()
    {
        var harness = new Harness();
        await harness.Sut.LoadFromConfigAsync();
        harness.Credentials.Invocations.Clear();

        harness.Sut.ApiKey = "lost-focus-key";
        await harness.Sut.FlushApiKeyAsync();

        harness.Credentials.Verify(
            x => x.SetApiKeyAsync("lost-focus-key", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void AvailableLanguages_RendersAutonymDisplayNames()
    {
        // H23 (FZ4-F2) regression: dropdown items must render their
        // autonym ("English / Українська / Русский") so the option is
        // self-identifying regardless of UI locale.  Translator stores
        // identical strings for lang_label_* keys in every dictionary
        // (they ARE autonyms), so this assertion is stable independent
        // of the active language.
        var harness = new Harness();

        var options = harness.Sut.AvailableLanguages;

        options.Should().HaveCount(3);
        options.Should().ContainSingle(o => o.Value == Language.English && o.DisplayName == "English");
        options.Should().ContainSingle(o => o.Value == Language.Ukrainian && o.DisplayName == "Українська");
        options.Should().ContainSingle(o => o.Value == Language.Russian && o.DisplayName == "Русский");
    }

    // Z7-F3 / M19 regression: BalanceDisplay re-resolves when the user
    // changes UI language mid-session.  Pre-fix the value was assigned
    // by `_translator[key]` at one point in time and stayed frozen — a
    // long-running balance fetch could surface its "Loading..." string
    // in the locale active at command-start, then never refresh even if
    // the user flipped Settings → Language while waiting.
    [Fact]
    public async Task RefreshBalance_KeyedDisplay_ReResolvesOnMidSessionLanguageSwitchAsync()
    {
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { ExperimentalCostsAndCredits = true });
        // No API key → RefreshBalanceAsync lands in the balance_no_api_key
        // branch.  Drives a deterministic keyed BalanceDisplay state.
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        await harness.Sut.LoadFromConfigAsync();

        harness.Translator.SetLanguage(Language.English);
        await harness.Sut.RefreshBalanceCommand.ExecuteAsync(null);
        harness.Sut.BalanceDisplay.Should().Be(
            "Enter an API key to see the balance",
            "no-key branch resolves balance_no_api_key in the current locale (English)");

        harness.Translator.SetLanguage(Language.Ukrainian);
        harness.Sut.BalanceDisplay.Should().Be(
            "Введіть API-ключ для перегляду балансу",
            "mid-session language switch must re-resolve the cached key through the translator");

        harness.Sut.Dispose();
    }

    [Fact]
    public async Task RefreshBalance_RawDisplay_StaysFrozenAcrossLanguageSwitchAsync()
    {
        // OpenRouterException.Message branch sets BalanceDisplay from raw
        // text — no Translator key behind it.  After a language switch the
        // raw string must stay verbatim; re-resolving an absent key would
        // yield the raw key string and wipe the actionable error reason.
        var harness = new Harness();
        harness.ConfigStore
            .Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppConfig.Default with { ExperimentalCostsAndCredits = true });
        harness.Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("sk-valid");
        harness.OpenRouter.Setup(x => x.GetCreditsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OpenRouterException("Server returned HTTP 502"));
        await harness.Sut.LoadFromConfigAsync();

        harness.Translator.SetLanguage(Language.English);
        await harness.Sut.RefreshBalanceCommand.ExecuteAsync(null);
        harness.Sut.BalanceDisplay.Should().Be("Server returned HTTP 502");

        harness.Translator.SetLanguage(Language.Ukrainian);
        harness.Sut.BalanceDisplay.Should().Be(
            "Server returned HTTP 502",
            "raw display (no Translator key) must not be touched on language switch");

        harness.Sut.Dispose();
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
            Credentials.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            Credentials.Setup(x => x.SetApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Credentials.Setup(x => x.DeleteApiKeyAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            Autostart = new Mock<IAutostartService>(MockBehavior.Loose);
            Autostart.Setup(x => x.IsEnabled).Returns(false);

            Hotkeys = new Mock<IHotkeyManager>(MockBehavior.Loose);
            Hotkeys.Setup(x => x.TryRegister(It.IsAny<HotkeyKind>(), It.IsAny<string>())).Returns(true);

            Translator = new Translator();

            ModelBrowser = new Mock<IModelBrowser>(MockBehavior.Loose);
            ModelBrowser.Setup(x => x.BrowseAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            OpenRouter = new Mock<IOpenRouterClient>(MockBehavior.Loose);
            OpenRouter.Setup(x => x.GetModelsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)["openai/gpt-4o-mini", "anthropic/claude-3.5-sonnet"]);

            Notifications = new Mock<INotificationService>(MockBehavior.Loose);

            Sut = new GeneralTabViewModel(
                ConfigStore.Object,
                Credentials.Object,
                Autostart.Object,
                Hotkeys.Object,
                Translator,
                ModelBrowser.Object,
                OpenRouter.Object,
                new ScriptedLlmProviderFactory(OpenRouter.Object),
                Notifications.Object,
                NullLogger<GeneralTabViewModel>.Instance);
        }

        public GeneralTabViewModel Sut { get; }

        public Mock<IConfigStore> ConfigStore { get; }

        public Mock<ICredentialStore> Credentials { get; }

        public Mock<IAutostartService> Autostart { get; }

        public Mock<IHotkeyManager> Hotkeys { get; }

        public Mock<IModelBrowser> ModelBrowser { get; }

        public Mock<IOpenRouterClient> OpenRouter { get; }

        public Mock<INotificationService> Notifications { get; }

        public Translator Translator { get; }
    }
}
