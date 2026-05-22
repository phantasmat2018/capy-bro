using CapyBro.Models;

namespace CapyBro.Services;

public interface IConfigStore
{
    Task<AppConfig> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppConfig config, CancellationToken ct = default);
}
