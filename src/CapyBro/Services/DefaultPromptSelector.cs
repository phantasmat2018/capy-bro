using CapyBro.Models;

namespace CapyBro.Services;

public sealed class DefaultPromptSelector : IPromptSelector
{
    private readonly IConfigStore _configStore;
    private readonly IPromptRegistry _registry;
    private readonly IPromptPicker _picker;
    private readonly ITranslator _translator;

    public DefaultPromptSelector(
        IConfigStore configStore,
        IPromptRegistry registry,
        IPromptPicker picker,
        ITranslator translator)
    {
        _configStore = configStore;
        _registry = registry;
        _picker = picker;
        _translator = translator;
    }

    public async Task<Prompt?> SelectAsync(HotkeyKind kind, CancellationToken ct = default)
    {
        var config = await _configStore.LoadAsync(ct);
        var active = _registry.GetActive(config, _translator.Language);

        if (kind == HotkeyKind.Default)
        {
            if (!string.IsNullOrWhiteSpace(config.DefaultPrompt)
                && active.TryGetValue(config.DefaultPrompt, out var configured))
            {
                return configured;
            }

            // Fallback to a DETERMINISTIC pick — pre-fix, this used
            // active.Values.FirstOrDefault(), whose iteration order is the
            // dictionary's hash-bucket order: stable within a single
            // process but not guaranteed across runs, and definitely not
            // stable across edits to the active map (insert order shifts
            // when prompts are added/removed).  The user noticed
            // "different prompt fires every time my DefaultPrompt config
            // pointer goes stale".  Sort by display name (Ordinal) so the
            // fallback is always the alphabetically-first entry, which
            // the user can predict and reason about.
            return active.Count == 0
                ? null
                : active
                    .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                    .First()
                    .Value;
        }

        // Menu hotkey: pop the picker (120s timeout per §4.2 step 7).
        return await _picker.ShowAsync(active, TimeSpan.FromSeconds(120), ct);
    }
}
