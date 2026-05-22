using System.IO;
using System.Text;
using System.Text.Json;

using CapyBro.Models;
using CapyBro.Services.Migration;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

public sealed class ConfigStore : IConfigStore, IDisposable
{
    private const int MaxRetryAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly string _configPath;
    private readonly string _legacyConfigPath;
    private readonly ILogger<ConfigStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(initialCount: 1, maxCount: 1);
    private bool _disposed;

    public ConfigStore(string configPath, string legacyConfigPath, ILogger<ConfigStore> logger)
    {
        _configPath = configPath;
        _legacyConfigPath = legacyConfigPath;
        _logger = logger;
    }

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ai_text_improver_v2_config.json");

    public static string DefaultLegacyConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ai_text_improver_config.json");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _writeLock.Dispose();
        _disposed = true;
    }

    public async Task<AppConfig> LoadAsync(CancellationToken ct = default)
    {
        // We deliberately do NOT guard with File.Exists here. File.Exists
        // silently returns false when the underlying GetFileAttributesEx kernel
        // call fails (e.g. due to transient I/O pressure under concurrent
        // saves), which would incorrectly skip LoadCurrentAsync and return
        // AppConfig.Default. Instead we attempt the open directly: if the file
        // genuinely does not exist, LoadCurrentAsync propagates
        // FileNotFoundException which we catch and route to the legacy fallback
        // below; all other transient I/O errors are retried inside
        // LoadCurrentAsync and therefore never bubble up here.
        try
        {
            return await LoadCurrentAsync(ct);
        }
        catch (FileNotFoundException)
        {
            // File truly absent — fall through to legacy migration / default.
        }

        if (File.Exists(_legacyConfigPath))
        {
            var migrated = await TryMigrateLegacyAsync(ct);
            if (migrated is not null)
            {
                _logger.LogInformation("Migrated legacy v1 config from {Path} to v2 format", _legacyConfigPath);
                var withDefaults = migrated.WithDefaultsApplied();
                await SaveAsync(withDefaults, ct);
                return withDefaults;
            }
        }

        return AppConfig.Default;
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
        var tempPath = $"{_configPath}.{Guid.NewGuid():N}.tmp";
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Serialize writes within this process so concurrent SaveAsync callers do not race
        // each other's File.Replace on the destination. Cross-process safety still relies on
        // ReplaceFileW atomicity + retry.
        await _writeLock.WaitAsync(ct);
        try
        {
            IOException? lastException = null;
            for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await File.WriteAllTextAsync(tempPath, json, Encoding.UTF8, ct);
                    ReplaceFile(tempPath, _configPath);
                    return;
                }
                catch (IOException ex)
                {
                    lastException = ex;
                    _logger.LogWarning(
                        ex,
                        "Config save attempt {Attempt}/{Total} failed for {Path}",
                        attempt,
                        MaxRetryAttempts,
                        _configPath);
                    if (attempt < MaxRetryAttempts)
                    {
                        await Task.Delay(RetryDelay, ct);
                    }
                }
            }

            _logger.LogError(lastException, "Config save failed after {Total} attempts", MaxRetryAttempts);
            throw new IOException(
                $"Failed to save config to {_configPath} after {MaxRetryAttempts} attempts",
                lastException);
        }
        finally
        {
            _writeLock.Release();
            TryDeleteTempFile(tempPath);
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup; orphaned tmp files are harmless
        }
    }

    private static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination))
        {
            File.Replace(source, destination, destinationBackupFileName: null);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private async Task<AppConfig> LoadCurrentAsync(CancellationToken ct)
    {
        IOException? lastIoException = null;
        JsonException? lastJsonException = null;

        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(_configPath);

                if (stream.Length == 0)
                {
                    // A 0-byte file is a transient artifact of File.Replace.
                    // Treat it the same as IOException so the retry loop fires.
                    throw new IOException($"Config file {_configPath} is 0 bytes (attempt {attempt})");
                }

                var config = await JsonSerializer.DeserializeAsync(
                    stream,
                    AppConfigJsonContext.Default.AppConfig,
                    ct);
                return config is null ? AppConfig.Default : config.WithDefaultsApplied();
            }
            catch (JsonException ex)
            {
                // Under concurrent saves, File.Replace (ReplaceFileW) can update
                // the file content while we are mid-read, producing truncated or
                // mixed-generation JSON that fails to parse.  Treat this as a
                // transient IO condition and retry — the next attempt reads a
                // complete, consistent snapshot.  Only on the final attempt do
                // we fall through to the legacy-format heuristic recovery below.
                lastJsonException = ex;
                if (attempt < MaxRetryAttempts)
                {
                    await Task.Delay(RetryDelay, ct);
                    continue;
                }

                // All retries exhausted — try legacy-format recovery as a
                // last resort before returning defaults.
                var rescued = await TryParseAsLegacyAsync(_configPath, ct);
                if (rescued is not null)
                {
                    _logger.LogInformation(
                        "Config at {Path} is in v1 (snake_case) format — migrating in place",
                        _configPath);
                    var rescuedWithDefaults = rescued.WithDefaultsApplied();
                    await SaveAsync(rescuedWithDefaults, ct);
                    return rescuedWithDefaults;
                }

                _logger.LogWarning(
                    ex,
                    "Config file {Path} is corrupt after {Total} attempts — falling back to defaults",
                    _configPath,
                    MaxRetryAttempts);
                return AppConfig.Default;
            }
            catch (FileNotFoundException)
            {
                // Config file genuinely absent — propagate so LoadAsync can
                // check the legacy path and then return AppConfig.Default.
                // Do not retry: "not found" is not a transient I/O error.
                throw;
            }
            catch (IOException ex)
            {
                lastIoException = ex;
                if (attempt < MaxRetryAttempts)
                {
                    await Task.Delay(RetryDelay, ct);
                }
            }
        }

        _logger.LogWarning(
            lastIoException ?? (Exception?)lastJsonException,
            "Could not read config file {Path} after {Total} attempts — falling back to defaults",
            _configPath,
            MaxRetryAttempts);
        return AppConfig.Default;
    }

    private static async Task<AppConfig?> TryParseAsLegacyAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var legacy = await JsonSerializer.DeserializeAsync(
                stream,
                LegacyConfigJsonContext.Default.LegacyAppConfig,
                ct);
            if (legacy is null)
            {
                return null;
            }

            // A v2 file with a typo can sometimes deserialize as legacy with all fields
            // null (snake_case keys won't match camelCase). Don't migrate that — it would
            // overwrite the user's actual config with defaults dressed up as a "migration".
            // Require at least two populated fields to look genuinely v1-shaped.
            var populated = 0;
            if (!string.IsNullOrEmpty(legacy.Model))
            {
                populated++;
            }

            if (legacy.Models is { Count: > 0 })
            {
                populated++;
            }

            if (legacy.Timeout is not null)
            {
                populated++;
            }

            if (!string.IsNullOrEmpty(legacy.Hotkey))
            {
                populated++;
            }

            if (!string.IsNullOrEmpty(legacy.MenuHotkey))
            {
                populated++;
            }

            if (legacy.CustomPrompts is { Count: > 0 })
            {
                populated++;
            }

            return populated >= 2 ? legacy.ToAppConfig() : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task<AppConfig?> TryMigrateLegacyAsync(CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(_legacyConfigPath);
            var legacy = await JsonSerializer.DeserializeAsync(
                stream,
                LegacyConfigJsonContext.Default.LegacyAppConfig,
                ct);
            return legacy?.ToAppConfig();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Legacy config file {Path} could not be parsed", _legacyConfigPath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read legacy config file {Path}", _legacyConfigPath);
            return null;
        }
    }
}
