using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Moq;

using Xunit;

namespace CapyBro.Tests.Services;

public class LlmProviderFactoryTests
{
    // OllamaClient is sealed so we can't Mock<> it; constructing the
    // real type with null deps is fine because the factory never
    // invokes any method on the instance — it just returns the
    // reference.  Tests that need to verify behaviour go through
    // OllamaClientTests with a real httpHandler.
    private static OllamaClient CreateNullDependencyOllama() =>
        new(null!, null!, null!, null!);

    [Fact]
    public void Resolve_OpenRouter_ReturnsRegisteredOpenRouterInstance()
    {
        var openRouter = new Mock<IOpenRouterClient>(MockBehavior.Strict).Object;
        var ollama = CreateNullDependencyOllama();
        var factory = new LlmProviderFactory(openRouter, ollama);

        factory.Resolve(LlmProviderKind.OpenRouter).Should().BeSameAs(openRouter);
    }

    [Fact]
    public void Resolve_Ollama_ReturnsRegisteredOllamaInstance()
    {
        var openRouter = new Mock<IOpenRouterClient>(MockBehavior.Strict).Object;
        var ollama = CreateNullDependencyOllama();
        var factory = new LlmProviderFactory(openRouter, ollama);

        factory.Resolve(LlmProviderKind.Ollama).Should().BeSameAs(ollama);
    }

    [Fact]
    public void Resolve_UnknownEnum_ThrowsArgumentOutOfRange()
    {
        // L1 regression: a future LlmProviderKind member added to the
        // enum without a matching switch arm + DI registration must
        // surface immediately on first call rather than silently
        // routing the user's requests to OpenRouter (the previous
        // fail-safe behaviour that could mask the missing wiring for
        // an entire release).
        var openRouter = new Mock<IOpenRouterClient>(MockBehavior.Strict).Object;
        var ollama = CreateNullDependencyOllama();
        var factory = new LlmProviderFactory(openRouter, ollama);

        // Cast an out-of-range int to the enum to simulate a future
        // value the switch hasn't been updated for.
        var unknown = (LlmProviderKind)999;
        Action act = () => factory.Resolve(unknown);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("kind");
    }
}
