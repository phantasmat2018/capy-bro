using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

public sealed class CredentialStore : ICredentialStore
{
    public const string DefaultTarget = "CapyBroV2";
    public const string DefaultUsername = "default";

    private readonly ICredentialBackend _backend;
    private readonly string _target;
    private readonly string _username;
    private readonly ILogger<CredentialStore> _logger;

    internal CredentialStore(
        ICredentialBackend backend,
        string target,
        string username,
        ILogger<CredentialStore> logger)
    {
        _backend = backend;
        _target = target;
        _username = username;
        _logger = logger;
    }

    public static CredentialStore CreateDefault(ILogger<CredentialStore> logger)
        => new(new WindowsCredentialBackend(), DefaultTarget, DefaultUsername, logger);

    public Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        try
        {
            var secret = _backend.Read(_target);
            return Task.FromResult(secret);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read API key from credential store");
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _backend.Write(_target, _username, apiKey);
        return Task.CompletedTask;
    }

    public Task DeleteApiKeyAsync(CancellationToken ct = default)
    {
        try
        {
            _backend.Delete(_target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete API key from credential store");
        }

        return Task.CompletedTask;
    }
}
