using System.Net;
using System.Net.Http;
using System.Text;

namespace CapyBro.Tests.TestHelpers;

public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _handler;

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> RequestBodies { get; } = [];

    public void RespondWithJson(HttpStatusCode status, string jsonBody)
    {
        _handler = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        });
    }

    /// <summary>
    /// Responds with a text/event-stream body, used by the streaming
    /// OpenRouter API. Pass already-formatted SSE content (data: lines
    /// + blank-line separators); see <see cref="BuildSseFromContents"/>
    /// to compose canonical chunks from raw content strings.
    /// </summary>
    public void RespondWithSse(string sseBody)
    {
        _handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
        });
    }

    /// <summary>
    /// Builds a canonical SSE body for a sequence of content deltas. Each
    /// delta becomes one "data: {...}" frame; a final "data: [DONE]"
    /// sentinel terminates the stream. Use <paramref name="extraLinesBefore"/>
    /// to inject non-data frames (heartbeats, comments) for parser tests.
    /// </summary>
    public static string BuildSseFromContents(
        IEnumerable<string> contents,
        IEnumerable<string>? extraLinesBefore = null)
    {
        var sb = new StringBuilder();
        if (extraLinesBefore is not null)
        {
            foreach (var line in extraLinesBefore)
            {
                sb.Append(line).Append("\n\n");
            }
        }

        foreach (var c in contents)
        {
            // Escape backslash + double-quote so embedded JSON survives.
            var escaped = c.Replace("\\", "\\\\", StringComparison.Ordinal)
                           .Replace("\"", "\\\"", StringComparison.Ordinal)
                           .Replace("\n", "\\n", StringComparison.Ordinal);
            sb.Append("data: {\"choices\":[{\"delta\":{\"content\":\"")
              .Append(escaped)
              .Append("\"}}]}\n\n");
        }

        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// Builds an Ollama-shaped NDJSON body for a sequence of content
    /// deltas.  One JSON object per line — the final line carries
    /// <c>"done": true</c> and an empty content field, mirroring
    /// Ollama's real wire format from
    /// <c>https://github.com/ollama/ollama/blob/main/docs/api.md</c>.
    /// </summary>
    public static string BuildNdjsonFromContents(IEnumerable<string> contents)
    {
        var sb = new StringBuilder();
        foreach (var c in contents)
        {
            var escaped = c.Replace("\\", "\\\\", StringComparison.Ordinal)
                           .Replace("\"", "\\\"", StringComparison.Ordinal)
                           .Replace("\n", "\\n", StringComparison.Ordinal);
            sb.Append("{\"message\":{\"role\":\"assistant\",\"content\":\"")
              .Append(escaped)
              .Append("\"},\"done\":false}\n");
        }

        sb.Append(/*lang=json,strict*/ "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Responds with an Ollama-style NDJSON streaming body.  Same
    /// passthrough behaviour as <see cref="RespondWithSse"/>; the
    /// content-type isn't sniffed by <see cref="CapyBro.Services.OllamaClient"/>
    /// (it just reads lines from the body stream) so we don't need to
    /// match Ollama's exact application/x-ndjson header — the StringContent
    /// default is fine.
    /// </summary>
    public void RespondWithNdjson(string ndjsonBody)
    {
        _handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjsonBody, Encoding.UTF8, "application/x-ndjson"),
        });
    }

    public void RespondWithStatus(HttpStatusCode status, string body = "")
    {
        _handler = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        });
    }

    public void RespondWith(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public void Throw(Exception exception)
    {
        _handler = (_, _) => throw exception;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_handler is null)
        {
            throw new InvalidOperationException("Mock handler not configured. Call RespondWith*(...) first.");
        }

        return await _handler(request, cancellationToken);
    }
}
