namespace CapyBro.Services;

public sealed class TextProcessingFailedEventArgs : EventArgs
{
    /// <summary>
    /// Raw-message ctor for failures whose text is NOT a Translator key —
    /// e.g. <see cref="OpenRouterException.Message"/>, which is built from
    /// HTTP status + server body and resolved at throw time by the
    /// OpenRouter client.  The subscriber surfaces the message verbatim.
    /// </summary>
    public TextProcessingFailedEventArgs(string localizedMessage, Exception? exception = null)
    {
        LocalizedMessage = localizedMessage;
        Exception = exception;
    }

    /// <summary>
    /// Key-binding ctor for failures whose text IS a Translator key — every
    /// catch-block in TextProcessor that selects a known message-id (e.g.
    /// "msg_model_not_configured", "toast_no_selection").  The caller passes
    /// the key AND the eagerly-resolved <paramref name="localizedMessage"/>
    /// snapshot; the latter keeps back-compat for tests / log sinks that
    /// read <see cref="LocalizedMessage"/> directly.  Subscribers that want
    /// late-binding (resolve in the active locale at toast-render time
    /// rather than at raise time) should consume <see cref="LocalizationKey"/>
    /// and re-resolve via the translator.
    ///
    /// Z10-F7 / M27 motivation: pre-fix every <c>RaiseFailed</c> call site
    /// passed an already-resolved <c>_translator[key]</c>, so the toast's
    /// language was frozen at raise time.  A future call site that passed
    /// a hard-coded English literal would silently bypass the translator
    /// in EVERY UI locale.  The key-binding path makes the contract
    /// explicit and tools / reviewers can grep for raw <c>RaiseFailed(...)</c>
    /// vs <c>RaiseFailedKey(...)</c>.
    /// </summary>
    public TextProcessingFailedEventArgs(string localizationKey, string localizedMessage, Exception? exception = null)
    {
        LocalizationKey = localizationKey;
        LocalizedMessage = localizedMessage;
        Exception = exception;
    }

    /// <summary>
    /// Translator key (e.g. "msg_model_not_configured") when the failure
    /// reason maps to a known message-id; null when the reason is a
    /// dynamically-built message (server error body, internal exception
    /// detail).  Late-binding subscribers should prefer this and re-resolve
    /// via the active <see cref="ITranslator"/>.
    /// </summary>
    public string? LocalizationKey { get; }

    /// <summary>
    /// Eagerly-resolved message at raise time.  Always populated; safe for
    /// log sinks and tests.  UI subscribers that care about mid-flight
    /// language switches should resolve <see cref="LocalizationKey"/>
    /// (when set) instead.
    /// </summary>
    public string LocalizedMessage { get; }

    public Exception? Exception { get; }
}
