using System.Net;

namespace CapyBro.Services;

public sealed class OpenRouterException : Exception
{
    public OpenRouterException()
    {
    }

    public OpenRouterException(string message)
        : base(message)
    {
    }

    public OpenRouterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OpenRouterException(string message, HttpStatusCode? statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public OpenRouterException(string message, HttpStatusCode? statusCode, string? localizationKey, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        LocalizationKey = localizationKey;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Optional translator key for the failure.  When non-null, the
    /// TextProcessor catch path forwards it through
    /// <c>ProcessingFailed</c> so subscribers can dispatch on the
    /// concrete failure kind without parsing the localised message
    /// (which differs per locale and breaks substring matching).
    /// Currently used to surface the <c>ollama_unreachable</c> case
    /// up to App.xaml.cs so it can trigger an auto-revert to
    /// OpenRouter.
    /// </summary>
    public string? LocalizationKey { get; }
}
