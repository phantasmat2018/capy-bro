namespace CapyBro.Services;

public interface IClipboardService
{
    Task<string> GetTextAsync(CancellationToken ct = default);

    Task SetTextAsync(string text, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}
