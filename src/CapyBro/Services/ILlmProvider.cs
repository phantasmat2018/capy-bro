using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// Provider-neutral surface that <see cref="TextProcessor"/> needs from
/// whichever LLM backend the user has selected. OpenRouter (cloud, paid)
/// and Ollama (local, free) both implement this; <see cref="ILlmProviderFactory"/>
/// resolves the active instance from <see cref="AppConfig.Provider"/>.
///
/// <para>
/// Auth parameter is provider-defined: OpenRouter requires a bearer key,
/// Ollama ignores it (local socket, no auth). The interface keeps the
/// parameter so the factory can hand the call site a single uniform
/// signature; provider implementations decide whether the value is used
/// or discarded.
/// </para>
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// Streams the model's reply as a sequence of deltas. Implementations
    /// MUST yield non-empty fragments only; the caller concatenates and
    /// post-strips via <see cref="ResultStripper"/>. Throws a provider-
    /// specific localized exception (<see cref="OpenRouterException"/>
    /// for either provider — kept as the shared error type because
    /// TextProcessor already translates it into toast keys) for transport
    /// or HTTP failures.
    /// </summary>
    IAsyncEnumerable<string> ImproveStreamAsync(
        string apiKey,
        string model,
        string promptText,
        string userText,
        TimeSpan timeout,
        bool preserveLanguage,
        CancellationToken ct = default);

    /// <summary>
    /// Lists model identifiers the provider exposes. For OpenRouter this
    /// is the global catalogue (~300 entries); for Ollama this is the
    /// set of tags the user has pulled locally via <c>ollama pull</c>
    /// (usually 1-5 entries). Order is alphabetical so the model-picker
    /// dialog can render the list as-is.
    /// </summary>
    Task<IReadOnlyList<string>> GetModelsAsync(
        string apiKey,
        CancellationToken ct = default);

    /// <summary>
    /// True when <see cref="ImproveStreamAsync"/> needs a non-empty
    /// <c>apiKey</c>. Drives the General tab's API-key visibility and
    /// TextProcessor's pre-flight gate that surfaces an actionable toast
    /// instead of round-tripping an empty key for a 401.
    /// </summary>
    bool RequiresApiKey { get; }
}
