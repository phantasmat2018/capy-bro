using System.IO;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

public sealed class AutostartService : IAutostartService
{
    public const string DefaultValueName = "CapyBroV2";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly IRegistryBackend _registry;
    private readonly string _valueName;
    private readonly Func<string> _executablePathProvider;
    private readonly Func<string, bool> _fileExistsProvider;
    private readonly ILogger<AutostartService> _logger;

    internal AutostartService(
        IRegistryBackend registry,
        string valueName,
        Func<string> executablePathProvider,
        ILogger<AutostartService> logger,
        Func<string, bool>? fileExistsProvider = null)
    {
        _registry = registry;
        _valueName = valueName;
        _executablePathProvider = executablePathProvider;
        // FZ6-F2 / H25: injectable so tests can simulate "registered path
        // no longer exists" without depending on the real filesystem.
        // Default is the real File.Exists; tests pass a stub that returns
        // true for known-good paths in the test harness.
        _fileExistsProvider = fileExistsProvider ?? File.Exists;
        _logger = logger;
    }

    public static AutostartService CreateDefault(ILogger<AutostartService> logger)
        => new(
            new WindowsRegistryBackend(),
            DefaultValueName,
            () => Environment.ProcessPath ?? string.Empty,
            logger);

    public bool IsEnabled
    {
        get
        {
            var raw = _registry.GetValue(RunKeyPath, _valueName);
            return !string.IsNullOrEmpty(raw);
        }
    }

    /// <summary>
    /// If autostart is enabled but points at a stale exe path (installer
    /// relocated the binary, user moved CapyBro.exe manually, the
    /// 2026-05-12 binary rename from CapyBro.exe → CapyBro.exe
    /// left a value pointing at the old name, OR the parsed path no
    /// longer exists on disk per FZ6-F2 / H25), rewrite the Run-key
    /// value to the current path.  Idempotent and safe to call on
    /// every launch.
    /// </summary>
    public void RepairIfStale()
    {
        var raw = _registry.GetValue(RunKeyPath, _valueName);
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        var currentExe = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            return;
        }

        // Z4-F4 fix: full-path equality on the parsed exe path, not
        // substring on the raw value. Pre-fix `raw.Contains(currentExe)`
        // returned true when the registry held `..\app.exe.backup` and
        // the current was `..\app.exe`, silently leaving a broken entry.
        var registeredPath = ExtractExePathFromRunKeyValue(raw);
        var pathMatches = registeredPath is not null
            && string.Equals(
                Path.GetFullPath(registeredPath),
                Path.GetFullPath(currentExe),
                StringComparison.OrdinalIgnoreCase);

        // FZ6-F2 / H25: even when the recorded path is "ours by name",
        // verify it actually exists — a user who deleted publish-folder
        // (or any past install location) needs the entry rewritten to
        // the live exe, otherwise Windows boot fails silently with no
        // tray icon ever appearing.
        var registeredFileExists = registeredPath is not null
            && _fileExistsProvider(registeredPath);

        if (pathMatches && registeredFileExists)
        {
            return;
        }

        try
        {
            // FZ5-F2 / M37 — log the DETECTION before Enable() runs.
            // Pre-fix Enable() logged "Enabled autostart with value …"
            // first, then RepairIfStale logged "Detected stale autostart
            // entry" — the chronological narrative read as if a fresh
            // enable preceded a noticing-that-it-was-stale moment, which
            // misled triage ("why did we re-enable AND then detect
            // stale?").  The correct sequence is detect → repair (which
            // = call Enable()), so the log order now matches the
            // causality.
            _logger.LogInformation("Detected stale autostart entry — pointed at {OldValue}, repairing", raw);
            Enable();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.LogWarning(ex, "Could not repair stale autostart entry");
        }
    }

    public void Enable()
    {
        var exePath = _executablePathProvider();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException(
                "Executable path is empty — cannot enable autostart for an unknown process.");
        }

        // Reject paths containing literal quote chars; Windows shell quoting can't escape them
        // safely and the result would either fail to launch or, worse, launch the wrong target.
        if (exePath.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Executable path contains a quote character; refusing to write an ambiguous Run-key value.");
        }

        // The --silent flag tells App.OnStartup that this launch came
        // from Windows boot and the user shouldn't be greeted with the
        // Settings window — the tray icon alone is the right footprint.
        // Manual launches (Start Menu, Desktop shortcut, Explorer
        // double-click) carry no args and open Settings instead.
        var quotedValue = $"\"{exePath}\" --silent";
        _registry.SetValue(RunKeyPath, _valueName, quotedValue);
        _logger.LogInformation("Enabled autostart with value {Value}", quotedValue);
    }

    public void Disable()
    {
        _registry.DeleteValue(RunKeyPath, _valueName);
        _logger.LogInformation("Disabled autostart");
    }

    /// <summary>
    /// Extracts the executable path from a Run-key value, which we always
    /// write as <c>"&lt;exe-path&gt;" --silent</c>. Returns the unquoted
    /// path or, when the value is unquoted (legacy hand-edit), the
    /// substring up to the first whitespace. Z4-F4 / FZ5-F1 fix —
    /// pre-fix <see cref="RepairIfStale"/> used <c>raw.Contains(currentExe)</c>
    /// which produced false-positives for any registry value that was a
    /// superset of the current path (e.g. <c>...CapyBro.exe.backup</c>
    /// silently looked "current" while pointing at a non-existent file).
    /// </summary>
    internal static string? ExtractExePathFromRunKeyValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('"'))
        {
            // string.IndexOf(char, int, StringComparison) doesn't exist;
            // span-search side-steps the CA1307 advisory while keeping
            // explicit Ordinal semantics (char-to-char equality has no
            // culture sensitivity).
            var endQuoteOffset = trimmed.AsSpan(1).IndexOf('"');
            if (endQuoteOffset > 0)
            {
                return trimmed[1..(endQuoteOffset + 1)];
            }

            // Empty quoted value (`""`) or no closing quote — neither
            // shape gives us a usable path, so treat as null rather than
            // returning the empty interior or the malformed remainder.
            return null;
        }

        // Unquoted: take up to first whitespace (cannot reliably parse paths
        // with spaces from an unquoted value — fall back to entire string).
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? trimmed[..space] : trimmed;
    }
}
