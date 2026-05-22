using System.IO;

using CapyBro.Services;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CapyBro.Tests.Services;

public class AutostartServiceTests
{
    private const string ValueName = "TestApp";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    [Fact]
    public void IsEnabled_NoValue_ReturnsFalse()
    {
        var registry = new FakeRegistryBackend();
        var sut = CreateSut(registry, () => "C:\\app.exe");

        sut.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_ValuePresent_ReturnsTrue()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\app.exe\"";
        var sut = CreateSut(registry, () => "C:\\app.exe");

        sut.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void IsEnabled_EmptyValue_ReturnsFalse()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = string.Empty;
        var sut = CreateSut(registry, () => "C:\\app.exe");

        sut.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Enable_WritesQuotedExePathWithSilentFlag()
    {
        var registry = new FakeRegistryBackend();
        var sut = CreateSut(registry, () => "C:\\Program Files\\CapyBro\\app.exe");

        sut.Enable();

        // The --silent flag tells App.OnStartup to keep the boot launch
        // tray-only; manual launches (no args) open the Settings window.
        registry.Values[(RunKeyPath, ValueName)]
            .Should().Be("\"C:\\Program Files\\CapyBro\\app.exe\" --silent");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Enable_EmptyExePath_Throws(string? exePath)
    {
        var registry = new FakeRegistryBackend();
        var sut = CreateSut(registry, () => exePath ?? string.Empty);

        var act = sut.Enable;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Enable_PathContainsQuote_RefusesToWrite()
    {
        var registry = new FakeRegistryBackend();
        var sut = CreateSut(registry, () => "C:\\evil\"path\\app.exe");

        var act = sut.Enable;

        act.Should().Throw<InvalidOperationException>();
        registry.Values.Should().NotContainKey((RunKeyPath, ValueName));
    }

    [Fact]
    public void Disable_DeletesValue()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\app.exe\"";
        var sut = CreateSut(registry, () => "C:\\app.exe");

        sut.Disable();

        registry.Values.Should().NotContainKey((RunKeyPath, ValueName));
    }

    [Fact]
    public void Enable_RegistryThrows_PropagatesException()
    {
        var registry = new FakeRegistryBackend
        {
            ThrowOnSet = new IOException("registry locked"),
        };
        var sut = CreateSut(registry, () => "C:\\app.exe");

        var act = sut.Enable;

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Disable_RegistryThrows_PropagatesException()
    {
        var registry = new FakeRegistryBackend
        {
            ThrowOnDelete = new UnauthorizedAccessException("denied"),
        };
        var sut = CreateSut(registry, () => "C:\\app.exe");

        var act = sut.Disable;

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void RepairIfStale_NoEntry_DoesNothing()
    {
        var registry = new FakeRegistryBackend();
        var sut = CreateSut(registry, () => "C:\\app.exe");

        sut.RepairIfStale();

        registry.Values.Should().NotContainKey((RunKeyPath, ValueName));
    }

    [Fact]
    public void RepairIfStale_PathMatches_DoesNotRewrite()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\app.exe\" --silent";
        var sut = CreateSut(registry, () => "C:\\app.exe");

        sut.RepairIfStale();

        // SetValue should not have been called.
        registry.SetValueCallCount.Should().Be(0);
    }

    [Fact]
    public void RepairIfStale_PathStale_RewritesValue()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\old\\app.exe\" --silent";
        var sut = CreateSut(registry, () => "C:\\new\\app.exe");

        sut.RepairIfStale();

        registry.Values[(RunKeyPath, ValueName)]
            .Should().Be("\"C:\\new\\app.exe\" --silent");
    }

    [Fact]
    public void RepairIfStale_EmptyExePath_DoesNothing()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\old\\app.exe\" --silent";
        var sut = CreateSut(registry, () => string.Empty);

        sut.RepairIfStale();

        // Should not rewrite when we don't know our own path.
        registry.Values[(RunKeyPath, ValueName)].Should().Be("\"C:\\old\\app.exe\" --silent");
    }

    // Z4-F4 / FZ5-F1 regression: pre-fix `raw.Contains(currentExe)`
    // returned true when the registered value was a STRICT SUPERSET of the
    // current exe path, so a `..\app.exe.backup` registration silently
    // passed the "looks current" check and was never rewritten — a moved-
    // exe scenario broke autostart with no diagnostic trail.
    [Fact]
    public void RepairIfStale_StoredPathIsSupersetOfCurrentExe_RewritesAnyway()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\app.exe.backup\" --silent";
        var sut = CreateSut(
            registry,
            () => "C:\\app.exe",
            fileExists: path => path == "C:\\app.exe");

        sut.RepairIfStale();

        registry.Values[(RunKeyPath, ValueName)]
            .Should()
            .Be(
                "\"C:\\app.exe\" --silent",
                because: "the substring-trap fix must rewrite when the registered path differs from the current exe path");
        registry.SetValueCallCount.Should().Be(1);
    }

    // FZ6-F2 / H25: the registered path may parse identically to the current
    // exe but if the underlying file no longer exists (publish folder
    // deleted, AppData install removed), the Run-key entry is broken even
    // though the substring check would say "ours". File-exists guard fixes.
    [Fact]
    public void RepairIfStale_RegisteredPathDoesNotExist_RewritesAnyway()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\publish\\app.exe\" --silent";
        var sut = CreateSut(
            registry,
            () => "C:\\install\\app.exe",
            fileExists: path => path == "C:\\install\\app.exe"); // publish path no longer exists

        sut.RepairIfStale();

        registry.Values[(RunKeyPath, ValueName)]
            .Should().Be("\"C:\\install\\app.exe\" --silent");
    }

    // FZ5-F2 / M37 regression: pre-fix the chronological log narrative for
    // a stale-entry repair was misleading — Enable() logged "Enabled
    // autostart with value …" first, then RepairIfStale logged "Detected
    // stale autostart entry".  Reading the log file looked like the app
    // re-enabled autostart and THEN noticed the previous value was stale,
    // when really the detection IS what drove the repair.  The fix swaps
    // the order so the file reads detect → enable.
    [Fact]
    public void RepairIfStale_StaleEntry_LogsDetectionBeforeEnableMessage()
    {
        var registry = new FakeRegistryBackend();
        registry.Values[(RunKeyPath, ValueName)] = "\"C:\\old\\app.exe\" --silent";
        var capture = new CapturingLogger();
        var sut = new AutostartService(
            registry,
            ValueName,
            () => "C:\\new\\app.exe",
            capture,
            _ => true);

        sut.RepairIfStale();

        var detectIndex = capture.Messages.FindIndex(
            m => m.Contains("Detected stale", StringComparison.Ordinal));
        var enableIndex = capture.Messages.FindIndex(
            m => m.Contains("Enabled autostart", StringComparison.Ordinal));

        detectIndex.Should().BeGreaterThanOrEqualTo(
            0,
            "the detection log line must appear");
        enableIndex.Should().BeGreaterThanOrEqualTo(
            0,
            "Enable() must still log its own message");
        detectIndex.Should().BeLessThan(
            enableIndex,
            "detect → enable is the causal order; pre-fix Enable() logged first which misled triage");
    }

    // Parser-level regression: quoted, unquoted, and the well-formed
    // canonical form should all extract correctly.
    [Theory]
    [InlineData("\"C:\\app.exe\" --silent", "C:\\app.exe")]
    [InlineData("\"C:\\Program Files\\CapyBro\\app.exe\" --silent", "C:\\Program Files\\CapyBro\\app.exe")]
    [InlineData("C:\\app.exe --silent", "C:\\app.exe")]
    [InlineData("C:\\app.exe", "C:\\app.exe")]
    [InlineData("\"\"", null)]
    [InlineData("", null)]
    public void ExtractExePathFromRunKeyValue_ParsesExpectedShape(string raw, string? expected)
    {
        var actual = AutostartService.ExtractExePathFromRunKeyValue(raw);
        if (expected is null)
        {
            actual.Should().BeNullOrEmpty();
        }
        else
        {
            actual.Should().Be(expected);
        }
    }

    private static AutostartService CreateSut(
        IRegistryBackend registry,
        Func<string> exePathProvider,
        Func<string, bool>? fileExists = null) =>
        new(
            registry,
            ValueName,
            exePathProvider,
            NullLogger<AutostartService>.Instance,
            fileExists ?? (_ => true));

    // Minimal capturing logger for log-order assertions.  Records each
    // formatted message in arrival order; tests inspect the list.
    private sealed class CapturingLogger : ILogger<AutostartService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FakeRegistryBackend : IRegistryBackend
    {
        public Dictionary<(string KeyPath, string ValueName), string> Values { get; } = [];

        public Exception? ThrowOnSet { get; set; }

        public Exception? ThrowOnDelete { get; set; }

        public int SetValueCallCount { get; private set; }

        public string? GetValue(string keyPath, string valueName)
            => Values.TryGetValue((keyPath, valueName), out var v) ? v : null;

        public void SetValue(string keyPath, string valueName, string value)
        {
            SetValueCallCount++;
            if (ThrowOnSet is not null)
            {
                throw ThrowOnSet;
            }

            Values[(keyPath, valueName)] = value;
        }

        public void DeleteValue(string keyPath, string valueName)
        {
            if (ThrowOnDelete is not null)
            {
                throw ThrowOnDelete;
            }

            Values.Remove((keyPath, valueName));
        }
    }
}
