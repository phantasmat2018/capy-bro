namespace CapyBro.Services;

public interface ICredentialStore
{
    Task<string?> GetApiKeyAsync(CancellationToken ct = default);

    Task SetApiKeyAsync(string apiKey, CancellationToken ct = default);

    Task DeleteApiKeyAsync(CancellationToken ct = default);
}
