using System.Net;
using System.Net.Http;

using CapyBro.Models;
using CapyBro.Services;
using CapyBro.Tests.TestHelpers;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit;

namespace CapyBro.Tests.Services;

public class OllamaClientTests
{
    private const string Model = "llama3.2:latest";
    private const string Prompt = "Improve this text";
    private const string UserText = "Hello world";

    private static (OllamaClient client, MockHttpMessageHandler handler, Translator translator) CreateSut(
        Language language = Language.English,
        string endpoint = "http://localhost:11434")
    {
        var handler = new MockHttpMessageHandler();
        var http = new HttpClient(handler);

        var configStore = new Mock<IConfigStore>(MockBehavior.Strict);
        configStore.Setup(x => x.LoadAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(AppConfig.Default with
                   {
                       Provider = LlmProviderKind.Ollama,
                       OllamaEndpoint = endpoint,
                   });

        var translator = new Translator();
        translator.SetLanguage(language);

        var client = new OllamaClient(http, configStore.Object, translator, NullLogger<OllamaClient>.Instance);
        return (client, handler, translator);
    }

    private static async Task<string> DrainAsync(
        OllamaClient sut,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in sut.ImproveStreamAsync(
            apiKey: string.Empty,
            Model,
            Prompt,
            UserText,
            timeout ?? TimeSpan.FromSeconds(30),
            preserveLanguage: false,
            ct))
        {
            sb.Append(chunk);
        }

        return sb.ToString();
    }

    [Fact]
    public async Task ImproveStreamAsync_ConcatenatesNdjsonDeltas_AndStopsOnDoneTrueAsync()
    {
        var (sut, handler, _) = CreateSut();
        handler.RespondWithNdjson(MockHttpMessageHandler.BuildNdjsonFromContents(
            ["Hello", ", ", "world", "!"]));

        var result = await DrainAsync(sut);

        result.Should().Be(
            "Hello, world!",
            "OllamaClient must concatenate each NDJSON chunk's message.content in order and stop on done:true");
    }

    [Fact]
    public async Task ImproveStreamAsync_HitsApiChatEndpointAtConfiguredHostAsync()
    {
        // The endpoint is dynamic — comes from IConfigStore per call.
        // Pin that the request actually lands on the host+path the user
        // configured, not the hard-coded default.
        var (sut, handler, _) = CreateSut(endpoint: "http://192.168.1.42:11434");
        handler.RespondWithNdjson(MockHttpMessageHandler.BuildNdjsonFromContents(["ok"]));

        await DrainAsync(sut);

        handler.Requests.Should().ContainSingle();
        var uri = handler.Requests[0].RequestUri!;
        uri.Host.Should().Be("192.168.1.42");
        uri.Port.Should().Be(11434);
        uri.AbsolutePath.Should().Be("/api/chat");
    }

    [Fact]
    public async Task ImproveStreamAsync_RequestBody_HasSystemRoleFraming_AndUserTriggerAsync()
    {
        // Same structural defence as OpenRouterClient: task + clipboard
        // text live in the system message; user role is the trivial
        // "go" trigger so the model can't mistake clipboard content
        // for a conversational turn.  Local models echo wrappers a bit
        // more often than hosted ones, so this is just as critical.
        var (sut, handler, _) = CreateSut();
        handler.RespondWithNdjson(MockHttpMessageHandler.BuildNdjsonFromContents(["ok"]));

        await DrainAsync(sut);

        var body = handler.RequestBodies[0];
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;

        root.GetProperty("model").GetString().Should().Be(Model);
        root.GetProperty("stream").GetBoolean().Should().BeTrue(
            "must request NDJSON streaming so progress events + cancellation work");

        var messages = root.GetProperty("messages");
        messages.GetArrayLength().Should().Be(2);

        var systemContent = messages[0].GetProperty("content").GetString()!;
        systemContent.Should().Contain("automated text-transformation function");
        systemContent.Should().Contain(Prompt);
        systemContent.Should().Contain("PROCESSING RULES");
        systemContent.Should().Contain(UserText);

        messages[1].GetProperty("role").GetString().Should().Be("user");
        messages[1].GetProperty("content").GetString().Should().Be(OllamaClient.TrivialUserTrigger);
    }

    [Fact]
    public async Task ImproveStreamAsync_PreserveLanguage_AppendsLanguageLockDirectiveAsync()
    {
        var (sut, handler, _) = CreateSut();
        handler.RespondWithNdjson(MockHttpMessageHandler.BuildNdjsonFromContents(["ok"]));

        await foreach (var ignored in sut.ImproveStreamAsync(
            apiKey: string.Empty,
            Model,
            Prompt,
            UserText,
            TimeSpan.FromSeconds(30),
            preserveLanguage: true))
        {
            _ = ignored;
        }

        var body = handler.RequestBodies[0];
        body.Should().Contain(
            "LANGUAGE LOCK",
            "preserveLanguage=true must add the language-lock paragraph to the system message");
    }

    [Fact]
    public async Task ImproveStreamAsync_PreserveLanguageFalse_OmitsLanguageLockDirectiveAsync()
    {
        var (sut, handler, _) = CreateSut();
        handler.RespondWithNdjson(MockHttpMessageHandler.BuildNdjsonFromContents(["ok"]));

        await DrainAsync(sut);

        var body = handler.RequestBodies[0];
        body.Should().NotContain(
            "LANGUAGE LOCK",
            "preserveLanguage=false must NOT add the directive — translate-style prompts intentionally change language");
    }

    [Fact]
    public async Task ImproveStreamAsync_EmptyStream_ThrowsApiEmptyResultAsync()
    {
        var (sut, handler, _) = CreateSut();
        // Done frame only, no content — should be treated as empty
        // result, mirroring the OpenRouter "stream ended without a
        // single delta" behaviour.
        handler.RespondWithNdjson(/*lang=json,strict*/ "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n");

        var act = async () => await DrainAsync(sut);

        await act.Should().ThrowAsync<OpenRouterException>()
            .WithMessage("*empty result*", "stream with no content frames is unusable and surfaces as api_empty_result");
    }

    [Fact]
    public async Task ImproveStreamAsync_ConnectionRefused_ThrowsOllamaUnreachableAsync()
    {
        // The canonical failure mode for Ollama: user forgot to start
        // `ollama serve`.  The toast must be the actionable
        // "is Ollama running?" hint, not the generic unknown-error.
        var (sut, handler, _) = CreateSut();
        handler.Throw(new HttpRequestException(
            "Connection refused",
            inner: new System.Net.Sockets.SocketException(),
            statusCode: null));

        var act = async () => await DrainAsync(sut);

        var ex = await act.Should().ThrowAsync<OpenRouterException>();
        ex.Which.Message.Should().Contain(
            "Ollama",
            "connection refused must surface the localized 'Ollama is not running' hint");
    }

    [Fact]
    public async Task ImproveStreamAsync_ModelNotPulled_Throws404WithPullHintAsync()
    {
        // Ollama returns 404 from /api/chat when the model id isn't
        // installed locally.  Toast must tell the user to run
        // `ollama pull <model>`, naming the exact tag they tried.
        var (sut, handler, _) = CreateSut();
        handler.RespondWithStatus(HttpStatusCode.NotFound, body: /*lang=json,strict*/ "{\"error\":\"model not found\"}");

        var act = async () => await DrainAsync(sut);

        var ex = await act.Should().ThrowAsync<OpenRouterException>();
        ex.Which.Message.Should().Contain(
            "ollama pull",
            "404 from /api/chat means model missing locally — toast names the remedy");
        ex.Which.Message.Should().Contain(
            Model,
            "the failing model id must be embedded in the toast so the user knows which tag to pull");
    }

    [Fact]
    public async Task ImproveStreamAsync_ServerError5xx_SurfacesGenericServerToastAsync()
    {
        var (sut, handler, _) = CreateSut();
        handler.RespondWithStatus(HttpStatusCode.InternalServerError);

        var act = async () => await DrainAsync(sut);

        await act.Should().ThrowAsync<OpenRouterException>();
    }

    [Fact]
    public async Task GetModelsAsync_DeserializesTags_AndSortsAsync()
    {
        var (sut, handler, _) = CreateSut();
        // Ollama tag-list shape — names with optional :tag suffixes.
        // Provide in non-sorted order so we can assert the client
        // surfaces them alphabetised (matches OpenRouter behaviour).
        const string body = /*lang=json,strict*/ """
            {
              "models": [
                {"name": "mistral:7b-instruct"},
                {"name": "llama3.2:latest"}
              ]
            }
            """;
        handler.RespondWithJson(HttpStatusCode.OK, body);

        var models = await sut.GetModelsAsync(apiKey: string.Empty);

        models.Should().Equal("llama3.2:latest", "mistral:7b-instruct");
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/tags");
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task GetModelsAsync_NoAuthHeader_SentAsync()
    {
        var (sut, handler, _) = CreateSut();
        handler.RespondWithJson(HttpStatusCode.OK, /*lang=json,strict*/ "{\"models\":[]}");

        await sut.GetModelsAsync(apiKey: "ignored-key");

        handler.Requests[0].Headers.Authorization.Should().BeNull(
            "Ollama runs on a local socket with no auth — sending a bearer token would be either silently ignored or surprising");
    }

    [Fact]
    public async Task GetModelsAsync_EmptyModelsField_ReturnsEmptyListAsync()
    {
        var (sut, handler, _) = CreateSut();
        handler.RespondWithJson(HttpStatusCode.OK, /*lang=json,strict*/ "{\"models\":[]}");

        var models = await sut.GetModelsAsync(apiKey: string.Empty);

        models.Should().BeEmpty();
    }

    [Fact]
    public void RequiresApiKey_IsFalse()
    {
        var (sut, _, _) = CreateSut();
        sut.RequiresApiKey.Should().BeFalse(
            "Ollama has no auth — TextProcessor must skip the api-key gate when this provider is selected");
    }

    // ─── H3 / H4 regression: NormaliseEndpoint sanitises every shape
    // of user-typed endpoint so a copy-paste from curl docs or a
    // typo can't pollute the request URI.
    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434")]
    [InlineData("http://localhost:11434/", "http://localhost:11434")]
    [InlineData("http://localhost:11434/api/chat", "http://localhost:11434")]
    [InlineData("http://localhost:11434/api/tags?stream=false", "http://localhost:11434")]
    [InlineData("  http://localhost:11434  ", "http://localhost:11434")]
    [InlineData("https://ollama.lan:8443", "https://ollama.lan:8443")]
    public void NormaliseEndpoint_SanitisesUserTypedShapes(string input, string expected)
    {
        OllamaClient.NormaliseEndpoint(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("localhost:11434", "http://localhost:11434")]
    [InlineData("192.168.1.42:11434", "http://192.168.1.42:11434")]
    public void NormaliseEndpoint_BareHostWithoutScheme_PrependsHttp(string input, string expected)
    {
        OllamaClient.NormaliseEndpoint(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("://broken")]
    public void NormaliseEndpoint_EmptyOrUnparseable_FallsBackToDefault(string? input)
    {
        OllamaClient.NormaliseEndpoint(input).Should().Be(OllamaClient.DefaultEndpoint);
    }

    // ─── M1 regression: distinguish a stream that ended cleanly with
    // a done:true frame and no content (legitimate empty result) from
    // a stream that was truncated (connection dropped, proxy closed
    // mid-flight) — the toast must surface a server-error hint
    // rather than the misleading "model returned nothing".
    [Fact]
    public async Task ImproveStreamAsync_StreamTruncatedWithoutDoneFrame_ThrowsServerErrorAsync()
    {
        var (sut, handler, _) = CreateSut();
        // No done:true frame, no content — simulates a connection that
        // closed before Ollama could reply.  Single blank line keeps
        // ReadLineAsync from hanging.
        handler.RespondWithNdjson("\n");

        var act = async () => await DrainAsync(sut);

        var ex = await act.Should().ThrowAsync<OpenRouterException>();
        // English server-error key contains "unavailable" (or "недоступний"
        // in UA / "недоступен" in RU).  EN is the default in CreateSut.
        ex.Which.Message.ToLowerInvariant().Should().ContainAny("unavailable", "server");
    }

    // ─── L5 regression: malformed /api/tags JSON surfaces as a
    // localised server-error rather than a generic unknown-error
    // toast, so the user knows to look at the Ollama side rather
    // than blame their network or CapyBro.
    [Fact]
    public async Task GetModelsAsync_MalformedJsonBody_ThrowsServerErrorAsync()
    {
        var (sut, handler, _) = CreateSut();
        // Reverse-proxy HTML page, custom Ollama fork shipping a
        // different shape, truncated response — any of these would
        // hit the JsonException branch.  A trailing junk character
        // after a valid-looking opener is the cheapest repro.
        handler.RespondWithJson(HttpStatusCode.OK, "{\"models\":[]not-json");

        var act = async () => await sut.GetModelsAsync(apiKey: string.Empty);

        var ex = await act.Should().ThrowAsync<OpenRouterException>();
        ex.Which.Message.ToLowerInvariant().Should().ContainAny("unavailable", "server");
    }

    [Fact]
    public async Task ImproveStreamAsync_CleanDoneFrameWithNoContent_StillThrowsEmptyResultAsync()
    {
        // Companion to the truncation test above: a done:true frame
        // with zero accumulated content is the genuine "model said
        // nothing" case and MUST still surface as api_empty_result so
        // the existing user-facing message is preserved.
        var (sut, handler, _) = CreateSut();
        handler.RespondWithNdjson(/*lang=json,strict*/ "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true}\n");

        var act = async () => await DrainAsync(sut);

        await act.Should().ThrowAsync<OpenRouterException>()
            .WithMessage("*empty result*");
    }

    [Fact]
    public async Task ImproveStreamAsync_TimeoutBeforeFirstChunk_SurfacesAsRequestTimeoutAsync()
    {
        // Pre-load a handler that simulates the server never replying
        // by throwing OperationCanceledException once the internal CTS
        // CancelAfter fires.  The discriminator (OpenRouterClient pattern)
        // is `!externalCt.IsCancellationRequested` — only the timeout-
        // driven internal CTS triggers, so the toast key is
        // api_request_timeout.
        var (sut, handler, _) = CreateSut();
        handler.RespondWith(async (req, ct) =>
        {
            // Wait until the per-request timeout fires.
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("should not reach");
        });

        var act = async () => await DrainAsync(sut, timeout: TimeSpan.FromMilliseconds(50));

        var ex = await act.Should().ThrowAsync<OpenRouterException>();
        // English message for `api_request_timeout` reads "Request
        // timed out" — substring assertion uses "timed" so a future
        // word-order tweak that swaps "timed out" for "timeout"
        // doesn't break the test.
        ex.Which.Message.ToLowerInvariant().Should().ContainAny("timed", "timeout");
    }
}
