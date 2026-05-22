using CapyBro.Models;

namespace CapyBro.Services;

public interface IPromptPicker
{
    Task<Prompt?> ShowAsync(
        IReadOnlyDictionary<string, Prompt> options,
        TimeSpan timeout,
        CancellationToken ct = default);
}
