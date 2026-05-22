using CapyBro.Models;

namespace CapyBro.Services;

/// <summary>
/// Resolves the <see cref="ILlmProvider"/> matching
/// <see cref="AppConfig.Provider"/>. Centralised so call sites
/// (TextProcessor, ModelsDialog, the wizard) don't have to switch on
/// the enum themselves — adding a third provider in the future is a
/// one-file change here, not a hunt across every consumer.
/// </summary>
public interface ILlmProviderFactory
{
    ILlmProvider Resolve(LlmProviderKind kind);
}

internal sealed class LlmProviderFactory : ILlmProviderFactory
{
    private readonly ILlmProvider _openRouter;
    private readonly ILlmProvider _ollama;

    // Constructor takes the OpenRouter-specific interface and the
    // concrete OllamaClient so DI resolves the right registered
    // services (DI registers both as their distinct types).  The
    // fields hold ILlmProvider so unit tests can substitute fakes
    // without subclassing OllamaClient (which is sealed).
    public LlmProviderFactory(IOpenRouterClient openRouter, OllamaClient ollama)
    {
        _openRouter = openRouter;
        _ollama = ollama;
    }

    public ILlmProvider Resolve(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.OpenRouter => _openRouter,
        LlmProviderKind.Ollama => _ollama,
        // L1 fix: throw rather than silent-fallback to OpenRouter so a
        // future provider added to the enum without a matching arm here
        // (and a corresponding DI registration) crashes on first call
        // instead of silently routing the user's requests to the wrong
        // backend.  Cheap insurance — Resolve is invoked once per
        // hotkey run, so the throw fires on the exact next user
        // interaction rather than waiting for a bug report.
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            $"Unknown LlmProviderKind '{kind}'. Add a switch arm + DI registration."),
    };
}
