using CapyBro.Models;
using CapyBro.Services;

namespace CapyBro.Tests.TestHelpers;

/// <summary>
/// Tiny <see cref="ILlmProviderFactory"/> stand-in for tests.  The test
/// configures whichever provider the SUT is exercising (usually
/// OpenRouter via a Mock&lt;IOpenRouterClient&gt;) and the factory just
/// returns it.  An optional Ollama slot lets a focused provider-switch
/// test exercise the dual-provider routing path; tests that don't care
/// leave it null and any LlmProviderKind.Ollama resolve throws so the
/// failure is obvious instead of silently routing to OpenRouter.
/// </summary>
public sealed class ScriptedLlmProviderFactory : ILlmProviderFactory
{
    private readonly ILlmProvider _openRouter;
    private readonly ILlmProvider? _ollama;

    public ScriptedLlmProviderFactory(ILlmProvider openRouter, ILlmProvider? ollama = null)
    {
        _openRouter = openRouter;
        _ollama = ollama;
    }

    public ILlmProvider Resolve(LlmProviderKind kind) => kind switch
    {
        LlmProviderKind.OpenRouter => _openRouter,
        LlmProviderKind.Ollama => _ollama
            ?? throw new InvalidOperationException(
                "Test did not configure an Ollama provider — pass one to the constructor if exercising the Ollama path."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown provider in test factory."),
    };
}
