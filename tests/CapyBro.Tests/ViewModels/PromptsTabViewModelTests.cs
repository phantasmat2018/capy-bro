using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Tests.TestHelpers;
using CapyBro.ViewModels;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CapyBro.Tests.ViewModels;

/// <summary>
/// Behaviour tests for the prompts editor.  Each test follows the
/// Arrange→Act→Assert pattern via <see cref="Harness"/> which spins up a
/// real <see cref="ConfigStore"/> against a temp directory plus a real
/// <see cref="PromptRegistry"/> — the high-leverage bugs (rename
/// collisions, default forking, lost-update races during NewAsync) are
/// all integration-shaped, so isolating them at the unit level would
/// require replicating half the registry logic in mocks.
/// </summary>
[Collection(TranslatorCollection.Name)]
public class PromptsTabViewModelTests
{
    [Fact]
    public async Task LoadAsync_PopulatesActiveKeysFromDefaults_AndAutoSelectsFirstAsync()
    {
        using var harness = new Harness();

        await harness.Sut.LoadAsync();

        harness.Sut.ActiveKeys.Should().HaveCount(8, "the 8 built-in defaults appear when no overrides exist");
        harness.Sut.SelectedKey.Should().NotBeNullOrEmpty(
            "auto-selecting an entry stops the editor from showing a confusing empty form");
    }

    // Z3-F3 / M7 regression: PromptsTab.xaml gates the editor body
    // versus the empty-state cue on this property.  Pre-fix the editor
    // fields stayed visible (with empty content) when nothing was
    // selected, and typing into them silently went nowhere — the
    // AutoSaveSnapshotAsync path returns early on empty effective name
    // (PromptsTabViewModel.cs:690).  HasSelection=false now flips the
    // pane into a centered "No prompt selected" cue with an icon, so
    // the user knows the next step is to click "New".
    [Fact]
    public void HasSelection_FreshVm_BeforeLoad_IsFalse()
    {
        using var harness = new Harness();

        harness.Sut.HasSelection.Should().BeFalse(
            "a freshly-constructed VM has neither SelectedKey nor EditorName set; the editor empty-state must paint");
    }

    [Fact]
    public async Task HasSelection_AfterLoad_WithDefaults_IsTrueAsync()
    {
        // Reciprocal of the above: once auto-select picks the first
        // preset, the editor body must take over from the empty-state.
        using var harness = new Harness();

        await harness.Sut.LoadAsync();

        harness.Sut.HasSelection.Should().BeTrue(
            "post-load auto-selection should drive the editor visible — flipping back to the empty-state " +
            "while a prompt is selected would render the user's selection invisible");
    }

    [Fact]
    public async Task LoadAsync_WhenSavedDefaultPromptIsMissing_FallsBackAndPersistsCorrectionAsync()
    {
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with { DefaultPrompt = "ghost-key-doesnt-exist" });

        await harness.Sut.LoadAsync();

        harness.Sut.DefaultPromptKey.Should().NotBe("ghost-key-doesnt-exist");
        var reloaded = await harness.Store.LoadAsync();
        reloaded.DefaultPrompt.Should().NotBe(
            "ghost-key-doesnt-exist",
            "the corrected default must be written back so subsequent runs do not see the dangling pointer");
    }

    [Fact]
    public async Task NewAsync_CreatesUniqueNamedEntryAndSelectsItAsync()
    {
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        var beforeCount = harness.Sut.ActiveKeys.Count;

        await harness.InvokeNewAsync();

        harness.Sut.ActiveKeys.Count.Should().Be(beforeCount + 1);
        harness.Sut.SelectedKey.Should().NotBeNull();
        harness.Sut.ActiveKeys.Should().Contain(harness.Sut.SelectedKey!);
    }

    [Fact]
    public async Task NewAsync_TwiceProducesNumericallyDistinctNamesAsync()
    {
        using var harness = new Harness();
        await harness.Sut.LoadAsync();

        await harness.InvokeNewAsync();
        var first = harness.Sut.SelectedKey;
        await harness.InvokeNewAsync();
        var second = harness.Sut.SelectedKey;

        first.Should().NotBe(second, "the second new prompt must get a non-colliding name (\"Новий промт 2\")");
    }

    [Fact]
    public async Task EditingDefaultPromptText_StoresPerLanguageOverride_DoesNotAffectOtherLocalesAsync()
    {
        // The 8 preset prompts are independent per-locale units: editing
        // the UA copy must NOT translate the user's text into the EN/RU
        // copies.  After the override is keyed by the language-specific
        // name, only the matching UI language shows the edit.
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";

        harness.Sut.EditorText = "MY CUSTOM TEXT";
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DefaultPromptOverrides.Should().ContainKey(
            "Виправити помилки",
            "the override is keyed by the LANGUAGE-SPECIFIC default name so it only applies in UA");
        stored.DefaultPromptOverrides["Виправити помилки"].Text.Should().Be("MY CUSTOM TEXT");
        stored.DefaultPromptOverrides.Should().NotContainKey(
            "Fix errors",
            "the EN copy must not have inherited the UA edit");
        stored.DefaultPromptOverrides.Should().NotContainKey(
            "Исправить ошибки",
            "the RU copy must not have inherited the UA edit");
        stored.DeletedDefaults.Should().NotContain(
            "Виправити помилки",
            "a same-name text edit must NOT hide the default in other locales");
        stored.CustomPrompts.Should().NotContainKey(
            "Виправити помилки",
            "the entry must not bleed into CustomPrompts where the UA name would leak into other locales' lists");

        // Sanity: EN/RU views must show the ORIGINAL default text, NOT
        // the user's UA edit.
        var registry = new PromptRegistry();
        var enView = registry.GetActive(stored, Language.English);
        enView.Should().ContainKey("Fix errors");
        enView["Fix errors"].Text.Should().NotContain(
            "MY CUSTOM TEXT",
            "the UA-only edit must not leak into the EN view");
        var ruView = registry.GetActive(stored, Language.Russian);
        ruView.Should().ContainKey("Исправить ошибки");
        ruView["Исправить ошибки"].Text.Should().NotContain("MY CUSTOM TEXT");

        // And the UA view DOES show the user's text.
        var uaView = registry.GetActive(stored, Language.Ukrainian);
        uaView["Виправити помилки"].Text.Should().Be("MY CUSTOM TEXT");
    }

    [Fact]
    public async Task RenamingDefaultPrompt_StaysPerLanguage_DoesNotAffectOtherLocalesAsync()
    {
        // Per-language presets: renaming the UA copy must NOT translate
        // the rename into the EN/RU lists.  Stored as a
        // DefaultPromptOverrides entry with OverrideName, NOT as a
        // CustomPrompts entry (which would bleed across locales).
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";

        harness.Sut.EditorName = "My fixer";
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DefaultPromptOverrides.Should().ContainKey(
            "Виправити помилки",
            "the rename lives on the UA slot only — keyed by the original UA name");
        stored.DefaultPromptOverrides["Виправити помилки"].OverrideName.Should().Be(
            "My fixer",
            "OverrideName carries the user's display rename for the UA view");
        stored.CustomPrompts.Should().NotContainKey(
            "My fixer",
            "a per-language preset rename must NOT spill into CustomPrompts");
        stored.DeletedDefaults.Should().NotContain(
            "Fix errors",
            "the EN copy of the slot stays visible — the rename only affects UA");

        var registry = new PromptRegistry();
        var uaView = registry.GetActive(stored, Language.Ukrainian);
        uaView.Should().ContainKey("My fixer");
        uaView.Should().NotContainKey("Виправити помилки");

        var enView = registry.GetActive(stored, Language.English);
        enView.Should().ContainKey("Fix errors");
        enView.Should().NotContainKey("My fixer");

        var ruView = registry.GetActive(stored, Language.Russian);
        ruView.Should().ContainKey("Исправить ошибки");
        ruView.Should().NotContainKey("My fixer");
    }

    [Fact]
    public async Task EditingRenamedPreset_ContinuesPerLanguageOverrideAsync()
    {
        // After a rename, re-selecting the renamed entry and editing its
        // text must still go to the same DefaultPromptOverrides slot —
        // not create a new CustomPrompts entry under the renamed display
        // label (which would defeat the per-language semantics).
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            DefaultPromptOverrides = new Dictionary<string, Prompt>
            {
                ["Виправити помилки"] = new() { Text = "uk text", PreserveLanguage = true, OverrideName = "Renamed" },
            },
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Renamed";

        harness.Sut.EditorText = "uk text v2";
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DefaultPromptOverrides.Should().ContainKey("Виправити помилки");
        stored.DefaultPromptOverrides["Виправити помилки"].Text.Should().Be("uk text v2");
        stored.DefaultPromptOverrides["Виправити помилки"].OverrideName.Should().Be(
            "Renamed",
            "OverrideName must persist when the user only edits text without re-renaming");
        stored.CustomPrompts.Should().NotContainKey("Renamed");
    }

    [Fact]
    public async Task DeletingPresetInOneLanguage_AlsoHidesItInOtherLocalesAsync()
    {
        // Delete is GLOBAL across locales — preset slots are
        // semantically the same prompt regardless of UI language.  A UA
        // delete must therefore drop "Fix errors" / "Исправить ошибки"
        // too (the user wants the slot gone, not just renamed).  Any
        // per-language overrides on the slot are also wiped so a
        // language switch can't resurrect it via leftover override text.
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            DefaultPromptOverrides = new Dictionary<string, Prompt>
            {
                // User had a UA-only edit AND an EN-only edit on the same
                // slot.  Delete must clean both up.
                ["Виправити помилки"] = new() { Text = "uk edit", PreserveLanguage = true },
                ["Fix errors"] = new() { Text = "en edit", PreserveLanguage = true },
            },
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";

        await harness.InvokeDeleteAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DeletedDefaults.Should().Contain("Виправити помилки");
        stored.DeletedDefaults.Should().Contain(
            "Fix errors",
            "delete is global — the EN copy of the same slot is also hidden");
        stored.DeletedDefaults.Should().Contain("Исправить ошибки");
        stored.DefaultPromptOverrides.Should().NotContainKey(
            "Виправити помилки",
            "the UA-only edit must be cleaned up — its slot no longer exists");
        stored.DefaultPromptOverrides.Should().NotContainKey(
            "Fix errors",
            "the EN-only edit on the same slot must also go — otherwise switching to EN would resurrect the prompt with the leftover override text");

        var registry = new PromptRegistry();
        registry.GetActive(stored, Language.Ukrainian).Should().NotContainKey("Виправити помилки");
        registry.GetActive(stored, Language.English).Should().NotContainKey("Fix errors");
        registry.GetActive(stored, Language.Russian).Should().NotContainKey("Исправить ошибки");
    }

    [Fact]
    public async Task DeleteAsync_WhenConfirmCancelled_LeavesEverythingIntactAsync()
    {
        // Confirm-dialog gate: cancelling the prompt must leave the
        // ActiveKeys list unchanged AND the on-disk config untouched.
        // Pre-fix delete was unconditional; this test pins the new
        // affordance — a misclick on "Удалить" is recoverable.
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";
        var beforeKeys = harness.Sut.ActiveKeys.ToList();

        // Override the bypass-true default with a cancel-style false so
        // we exercise the early-return branch.
        harness.Sut.ConfirmDeleteOverride = _ => false;

        await harness.InvokeDeleteAsync();

        harness.Sut.ActiveKeys.Should().BeEquivalentTo(
            beforeKeys,
            "cancel must leave the visible list exactly as the user found it");
        harness.Sut.SelectedKey.Should().Be(
            "Виправити помилки",
            "selection survives a cancelled delete");
        var stored = await harness.Store.LoadAsync();
        stored.DeletedDefaults.Should().NotContain(
            "Виправити помилки",
            "no DeletedDefaults entry is written when the user backs out");
    }

    [Fact]
    public async Task NoOpEdit_OfDefaultPrompt_DoesNotForkAsync()
    {
        // Bug-fix regression: opening a default and clicking elsewhere
        // (or just re-selecting it) used to fork it because every
        // OnEditor*Changed during the selection-bound editor population
        // triggered a save with the original values.  We now skip writes
        // when the snapshot exactly matches the live entry.
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";

        // Round-trip the EditorText to itself by setting it to its own value —
        // the partial OnChanged still fires; the save must be skipped.
        harness.Sut.EditorText = harness.Sut.EditorText;
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DeletedDefaults.Should().NotContain(
            "Виправити помилки",
            "a no-op save must NOT fork the default");
        stored.CustomPrompts.Should().BeEmpty();
    }

    [Fact]
    public async Task RenameToExistingCustomName_IsRefused_AndEditorNameRevertsAsync()
    {
        // Bug-fix regression: typing the name of an OTHER prompt used to
        // silently overwrite that prompt's content.  The rename collision
        // check now refuses the save and reverts the editor name.
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["Custom A"] = new() { Text = "preserve me", PreserveLanguage = true },
                ["Custom B"] = new() { Text = "B body", PreserveLanguage = true },
            },
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Custom B";

        harness.Sut.EditorName = "Custom A";
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.CustomPrompts["Custom A"].Text.Should().Be(
            "preserve me",
            "Custom A must not be silently overwritten by a B-renamed-to-A save");
        stored.CustomPrompts.Should().ContainKey("Custom B");
        harness.Sut.EditorName.Should().Be(
            "Custom B",
            "the editor field must revert so the user notices the collision");
    }

    [Fact]
    public async Task ClearingNameMidEdit_DoesNotDropPendingTextChangesAsync()
    {
        // Bug-fix regression: a transient empty-name state used to make
        // AutoSave return early, dropping all the user's text edits when
        // they later resolved the name.  We now treat empty/whitespace
        // name as "save under the previous name".
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["Edit me"] = new() { Text = "old", PreserveLanguage = true },
            },
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Edit me";

        harness.Sut.EditorText = "new text body";
        harness.Sut.EditorName = "   ";
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.CustomPrompts.Should().ContainKey("Edit me");
        stored.CustomPrompts["Edit me"].Text.Should().Be(
            "new text body",
            "blank name during a typing burst is a transient UI state, not a delete intent");
    }

    [Fact]
    public async Task DeletingDefaultPrompt_AlsoRemovesAnyShadowedCustomEntryAsync()
    {
        // Bug-fix regression: a custom entry that had previously shadowed
        // a default (created by an earlier in-place edit) used to
        // "resurrect" the default after delete because the custom was
        // still merged on top.
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                // Shadow under the UA key (legacy state from before the fork fix).
                ["Виправити помилки"] = new() { Text = "shadow", PreserveLanguage = true },
            },
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";

        await harness.InvokeDeleteAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DeletedDefaults.Should().Contain("Виправити помилки");
        stored.CustomPrompts.Should().NotContainKey(
            "Виправити помилки",
            "the leftover shadow custom must be cleaned up — otherwise the prompt stays visible");
    }

    [Fact]
    public async Task DeletingPromptThatWasGlobalDefault_ClearsTheDefaultPointerAsync()
    {
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["DefaultMe"] = new() { Text = "x", PreserveLanguage = true },
            },
            DefaultPrompt = "DefaultMe",
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "DefaultMe";

        await harness.InvokeDeleteAsync();

        var stored = await harness.Store.LoadAsync();
        stored.CustomPrompts.Should().NotContainKey("DefaultMe");
        stored.DefaultPrompt.Should().NotBe(
            "DefaultMe",
            "deleting the prompt that was the global default must drop the dangling pointer");
    }

    [Fact]
    public async Task SwitchingPromptDuringPendingSave_DoesNotJumpSelectionBackAsync()
    {
        // Bug-fix regression: AutoSave used to set SelectedKey back to
        // the snapshot's name unconditionally, so a fast user-switch
        // sandwiched a still-running save would pull their selection
        // back to the previous prompt and corrupt _previousEditorName
        // for the next snapshot.
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["Alpha"] = new() { Text = "a", PreserveLanguage = true },
                ["Beta"] = new() { Text = "b", PreserveLanguage = true },
            },
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Alpha";

        // Edit Alpha → Switch to Beta → flush completes.
        harness.Sut.EditorText = "edited alpha";
        harness.Sut.SelectedKey = "Beta";
        await harness.Sut.FlushPendingForTestAsync();

        harness.Sut.SelectedKey.Should().Be(
            "Beta",
            "the user already moved focus; the still-running Alpha save must not yank selection back");
        harness.Sut.EditorName.Should().Be("Beta");
    }

    [Fact]
    public async Task EditingDefaultPromptText_DoesNotLeakLanguageNameIntoOtherLocalesAsync()
    {
        // End-to-end check matching the user-reported screenshot:
        // editing the UA "Перекласти на українську" used to show a
        // mixed-language list when the UI was switched to English
        // (the UA-keyed CustomPrompts entry appeared alongside the EN
        // defaults).  After the per-language override fix, the EN view
        // shows "Translate to Ukrainian" with the ORIGINAL EN text —
        // the UA-specific edit does NOT cross over.
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Перекласти на українську";

        harness.Sut.EditorText = "Перекладі text на українську мову, зберігаючи стиль і нюанси. Поверни ТІЛЬКИ переклад без пояснень.1";
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        var registry = new PromptRegistry();

        var enView = registry.GetActive(stored, Language.English);
        enView.Keys.Should().NotContain(
            "Перекласти на українську",
            "the UA name must NOT appear in the EN list — that was the user-reported screenshot bug");
        enView.Should().ContainKey("Translate to Ukrainian");
        enView["Translate to Ukrainian"].Text.Should().StartWith(
            "Translate the text",
            "the EN copy must keep its original built-in text — the UA-specific edit does not bleed across locales");
        enView["Translate to Ukrainian"].Text.Should().NotEndWith(".1");

        var ruView = registry.GetActive(stored, Language.Russian);
        ruView.Keys.Should().NotContain("Перекласти на українську");
        ruView.Should().ContainKey("Перевести на украинский");
        ruView["Перевести на украинский"].Text.Should().NotEndWith(".1");

        // UA view DOES show the edit.
        var uaView = registry.GetActive(stored, Language.Ukrainian);
        uaView["Перекласти на українську"].Text.Should().EndWith(".1");
    }

    [Fact]
    public async Task TogglingPreserveLanguageOnPreset_AppliesAcrossAllLocalesAsync()
    {
        // The 8 preset slots are semantically the same prompt across
        // locales — only their text/name varies.  PreserveLanguage /
        // ShowDiffPreview / Model are properties of the prompt's
        // PURPOSE, so toggling on the UA copy must apply to EN/RU views
        // of the same slot.  Storage: DefaultPromptSettings keyed by EN
        // canonical (slot ID).
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";
        var originalShowDiff = harness.Sut.EditorShowDiffPreview;

        harness.Sut.EditorShowDiffPreview = !originalShowDiff;
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DefaultPromptSettings.Should().ContainKey(
            "Fix errors",
            "slot-level settings are keyed by the EN canonical so they apply uniformly across locales");
        stored.DefaultPromptSettings["Fix errors"].ShowDiffPreview.Should().Be(!originalShowDiff);

        var registry = new PromptRegistry();
        registry.GetActive(stored, Language.English)["Fix errors"].ShowDiffPreview.Should().Be(
            !originalShowDiff,
            "the EN copy of the slot must reflect the UA-side toggle");
        registry.GetActive(stored, Language.Russian)["Исправить ошибки"].ShowDiffPreview.Should().Be(
            !originalShowDiff,
            "the RU copy of the slot must reflect the UA-side toggle");
    }

    [Fact]
    public async Task EditingPresetText_StaysPerLanguageEvenWhenSlotSettingsExistAsync()
    {
        // After we split slot-level settings out of the per-language
        // override, the text edit must STILL stay per-language.  Both
        // layers coexist independently.
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";

        harness.Sut.EditorText = "UA-only text";
        harness.Sut.EditorShowDiffPreview = false;
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DefaultPromptOverrides.Should().ContainKey("Виправити помилки");
        stored.DefaultPromptOverrides["Виправити помилки"].Text.Should().Be("UA-only text");
        stored.DefaultPromptSettings["Fix errors"].ShowDiffPreview.Should().BeFalse();

        var registry = new PromptRegistry();
        // EN view: shared ShowDiffPreview applied; text stays the EN default.
        var enView = registry.GetActive(stored, Language.English);
        enView["Fix errors"].ShowDiffPreview.Should().BeFalse(
            "slot setting carries across locales");
        enView["Fix errors"].Text.Should().NotBe(
            "UA-only text",
            "text override is per-locale — UA edit must NOT leak into EN");
    }

    [Fact]
    public async Task TogglingDefaultCheckboxOffAndOn_DoesNotForkAsync()
    {
        // Bug-fix regression: even when forkingDefault was true, the
        // no-op skip should fire when the resulting state matches what
        // the user already sees in the active map.  Otherwise toggling a
        // checkbox off-then-on (or any UI gesture that schedules a save
        // without changing observable state) would pointlessly fork the
        // default into a custom override.
        using var harness = new Harness();
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "Виправити помилки";

        // Flip ShowDiffPreview off then back to its original (true).
        // Both edits cumulatively schedule one debounced save; the
        // resulting snapshot equals the live default and must skip.
        var originalShowDiff = harness.Sut.EditorShowDiffPreview;
        harness.Sut.EditorShowDiffPreview = !originalShowDiff;
        harness.Sut.EditorShowDiffPreview = originalShowDiff;
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.DeletedDefaults.Should().NotContain(
            "Виправити помилки",
            "an off-then-on toggle leaves the visible state unchanged and must not fork the default");
        stored.CustomPrompts.Should().BeEmpty();
    }

    [Fact]
    public async Task RenamingCustomPrompt_FollowsTheGlobalDefaultPointerAsync()
    {
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["OldName"] = new() { Text = "x", PreserveLanguage = true },
            },
            DefaultPrompt = "OldName",
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "OldName";

        harness.Sut.EditorName = "NewName";
        await harness.Sut.FlushPendingForTestAsync();

        var stored = await harness.Store.LoadAsync();
        stored.CustomPrompts.Should().ContainKey("NewName");
        stored.CustomPrompts.Should().NotContainKey("OldName");
        stored.DefaultPrompt.Should().Be(
            "NewName",
            "the global default pointer must follow a rename so the user's setting does not dangle");
    }

    [Fact]
    public async Task DeleteCommand_CanExecute_TracksSelectionAsync()
    {
        // M8 (Z3-F4) regression: pre-fix the Delete button was always
        // enabled — clicking it with no selection popped a confirm
        // dialog for a no-op, then ran ClearEditor.  CanExecute is now
        // bound to HasSelection so the button dims automatically.
        using var harness = new Harness();
        await harness.Sut.LoadAsync();

        // After LoadAsync the SUT auto-selects the first prompt, so
        // Delete is enabled by default.
        harness.Sut.DeleteCommand.CanExecute(null).Should().BeTrue();

        harness.Sut.SelectedKey = null;
        harness.Sut.EditorName = string.Empty;
        harness.Sut.DeleteCommand.CanExecute(null).Should().BeFalse(
            "no selection and no editor name means there is no key to delete — the button must dim");
    }

    [Fact]
    public async Task SelectPromptWithOffCatalogueModel_FallsBackToSentinelAsync()
    {
        // v15 behaviour change (supersedes the H12 / Z6-F2 "preserve
        // unknown value" rule): a Prompt.Model that no longer matches
        // the active provider's catalogue (e.g. the user just clicked
        // "−" on that model in Settings → Provider) MUST resolve to
        // the localised "Default model" sentinel rather than being
        // re-inserted into the picker as a sibling.  Otherwise the
        // deleted model lingers as an unselectable-from-main-list
        // orphan in every per-prompt dropdown that referenced it.
        // The disk-stored Prompt.Model self-heals on the next
        // prompt-switch via the AutoSaveSnapshotAsync no-op check.
        using var harness = new Harness();
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["P"] = new() { Text = "body", Model = "off-catalogue/v9", PreserveLanguage = true },
            },
        });
        await harness.Sut.LoadAsync();

        harness.Sut.SelectedKey = "P";

        harness.Sut.AvailableModelsForPicker.Should().NotContain(
            "off-catalogue/v9",
            "the off-catalogue id must NOT leak into the dropdown — that's the bug v15 closes");
        harness.Sut.EditorModel.Should().Be(
            harness.Translator["prompt_model_default_option"],
            "EditorModel must show the sentinel label since the stored model is no longer in the catalogue");
    }

    // 2026-05-12 regression: switching the UI language while a prompt with
    // no per-prompt model is selected used to surface BOTH the new locale's
    // "Default model" sentinel AND the previous locale's stale label as
    // separate entries in the picker.  Cause: RebuildAvailableModelsForPicker
    // preserved EditorModel as a "leftover" entry whenever it didn't match
    // the just-computed `label`, and EditorModel still held the OLD locale's
    // sentinel string at the moment the Translator's Language event fired
    // (LoadAsync's async continuation hadn't reset it yet).  The new
    // KnownDefaultModelSentinels guard recognises any locale's sentinel
    // value as the same logical concept and re-binds EditorModel to the
    // current locale's label, dropping the duplicate.
    [Fact]
    public async Task LanguageSwitch_DoesNotDuplicateDefaultModelSentinelInPickerAsync()
    {
        using var harness = new Harness();

        // Seed a single custom prompt whose Model is null — that's the
        // "use global model" sentinel state on disk.  When the user
        // selects this prompt, EditorModel resolves to the LOCALIZED
        // sentinel label.
        await harness.Store.SaveAsync(AppConfig.Default with
        {
            CustomPrompts = new Dictionary<string, Prompt>
            {
                ["P"] = new() { Text = "body", Model = null, PreserveLanguage = true },
            },
        });
        await harness.Sut.LoadAsync();
        harness.Sut.SelectedKey = "P";

        var beforeSentinel = harness.Translator["prompt_model_default_option"];
        harness.Sut.EditorModel.Should().Be(
            beforeSentinel,
            "the no-override prompt resolves to the localized sentinel — sanity check before the switch");

        try
        {
            // Switch UI language — synchronously fires PropertyChanged events
            // that drive RebuildAvailableModelsForPicker.
            harness.Translator.SetLanguage(Language.English);

            var afterSentinel = harness.Translator["prompt_model_default_option"];
            afterSentinel.Should().NotBe(
                beforeSentinel,
                "test invariant: English and Ukrainian sentinels must differ — otherwise the duplication couldn't have been observed in the field");

            // The picker must contain ONLY the current locale's sentinel,
            // never both.  The full picker shape is [current_sentinel,
            // ...global models...].
            harness.Sut.AvailableModelsForPicker.Should().Contain(
                afterSentinel,
                "the picker must contain the NEW locale's sentinel after a language switch");
            harness.Sut.AvailableModelsForPicker.Should().NotContain(
                beforeSentinel,
                "the picker must NOT carry the PREVIOUS locale's sentinel as a leftover — duplication bug surface");

            // EditorModel must also follow the new locale so the ComboBox
            // header binds correctly.
            harness.Sut.EditorModel.Should().Be(
                afterSentinel,
                "EditorModel must be re-bound to the new locale's sentinel so the ComboBox header shows the right string");
        }
        finally
        {
            // Restore the harness default so subsequent tests see Ukrainian.
            harness.Translator.SetLanguage(Language.Ukrainian);
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _dir;

        public Harness()
        {
            _dir = new TempDirectory();
            Store = new ConfigStore(
                _dir.GetPath("config.json"),
                _dir.GetPath("legacy.json"),
                NullLogger<ConfigStore>.Instance);

            Translator = Translator.Instance;
            Translator.SetLanguage(Language.Ukrainian);

            Registry = new PromptRegistry();

            // GeneralTabViewModel is a heavy collaborator we don't actually
            // exercise here — but PromptsTabViewModel takes it for the
            // model-picker forwarding.  A loose mock with the minimum
            // observable surface (Models collection + the two experimental
            // booleans) keeps this test focused.
            General = CreateRealGeneral(Store);

            Notifications = new Mock<INotificationService>(MockBehavior.Loose);

            Sut = new PromptsTabViewModel(
                Store,
                Registry,
                Translator,
                General,
                NullLogger<PromptsTabViewModel>.Instance,
                Notifications.Object)
            {
                // Bypass the ConfirmDialog.Ask added to DeleteAsync — every
                // existing delete-flow test was authored before the
                // confirm step existed and would otherwise hang / throw
                // trying to spin up a modal Window in a headless xUnit
                // context.  Behaviour tests for the confirm gate itself
                // (cancel path) override this per-test.
                ConfirmDeleteOverride = _ => true,
            };
        }

        public PromptsTabViewModel Sut { get; }

        public ConfigStore Store { get; }

        public PromptRegistry Registry { get; }

        public Translator Translator { get; }

        public GeneralTabViewModel General { get; }

        public Mock<INotificationService> Notifications { get; }

        public Task InvokeNewAsync() => Sut.NewCommand.ExecuteAsync(null);

        public Task InvokeDeleteAsync() => Sut.DeleteCommand.ExecuteAsync(null);

        public void Dispose()
        {
            Sut.Dispose();
            General.Dispose();
            Store.Dispose();
            _dir.Dispose();
        }

        private GeneralTabViewModel CreateRealGeneral(IConfigStore store)
        {
            var creds = new Mock<ICredentialStore>(MockBehavior.Loose);
            creds.Setup(x => x.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            creds.Setup(x => x.SetApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var auto = new Mock<IAutostartService>(MockBehavior.Loose);
            var hot = new Mock<IHotkeyManager>(MockBehavior.Loose);
            hot.Setup(x => x.TryRegister(It.IsAny<HotkeyKind>(), It.IsAny<string>())).Returns(true);
            var browse = new Mock<IModelBrowser>(MockBehavior.Loose);
            var open = new Mock<IOpenRouterClient>(MockBehavior.Loose);
            var notif = new Mock<INotificationService>(MockBehavior.Loose);

            return new GeneralTabViewModel(
                store,
                creds.Object,
                auto.Object,
                hot.Object,
                Translator,
                browse.Object,
                open.Object,
                new ScriptedLlmProviderFactory(open.Object),
                notif.Object,
                NullLogger<GeneralTabViewModel>.Instance);
        }
    }
}
