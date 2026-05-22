using FluentAssertions;

using Xunit;

namespace CapyBro.Tests;

/// <summary>
/// Z8-F5 / M21 regression: the "silent autostart launch stays tray-only"
/// contract is enforced by a single inline boolean in
/// <c>App.OnStartup</c>.  Pre-fix that predicate was unpinned — a
/// future refactor that flipped case-sensitivity, accepted a substring
/// match, or whitespace-tolerated the token would change autostart
/// behaviour on every Windows sign-in for users who installed with the
/// Run-key checkbox and never touched Settings → Autostart.  These tests
/// pin the exact truth table that <c>AutostartService.Enable()</c>
/// writes against.
/// </summary>
public class SilentLaunchDetectionTests
{
    [Fact]
    public void IsSilentLaunch_NullArgs_ReturnsFalse()
    {
        // Defensive: WPF passes StartupEventArgs.Args which is non-null
        // in practice, but App.OnStartup runs the predicate against
        // `e?.Args` so null is the documented no-args path.
        App.IsSilentLaunch(null).Should().BeFalse();
    }

    [Fact]
    public void IsSilentLaunch_EmptyArgs_ReturnsFalse()
    {
        App.IsSilentLaunch([]).Should().BeFalse();
    }

    // Canonical token + shouted/mixed-case variants all match — case-
    // insensitive equality is part of the documented contract because
    // AutostartService writes "--silent" verbatim but a manually-edited
    // registry value might be shouted.
    [Theory]
    [InlineData("--silent")]
    [InlineData("--SILENT")]
    [InlineData("--Silent")]
    public void IsSilentLaunch_ExactTokenCaseInsensitive_ReturnsTrue(string arg)
    {
        App.IsSilentLaunch([arg]).Should().BeTrue(
            "AutostartService writes --silent verbatim and we match it case-insensitively");
    }

    // Anything other than an exact-token match must NOT silence —
    // substring or whitespace tolerance would let stale registry values
    // silence legitimate manual Start-Menu launches.  Cases pinned here:
    //   trailing space, leading space, truncated, substring-suffixed,
    //   single-dash form, naked word, empty string.
    [Theory]
    [InlineData("--silent ")]
    [InlineData(" --silent")]
    [InlineData("--silen")]
    [InlineData("--silently")]
    [InlineData("-silent")]
    [InlineData("silent")]
    [InlineData("")]
    public void IsSilentLaunch_NotExactToken_ReturnsFalse(string arg)
    {
        App.IsSilentLaunch([arg]).Should().BeFalse(
            "exact-token equality is the documented contract; substring or whitespace tolerance would let stale registry values silence legitimate manual launches");
    }

    [Fact]
    public void IsSilentLaunch_FlagAmongOtherArgs_ReturnsTrue()
    {
        // The user may have other args in front of --silent if they hand-
        // launched with a debugger; the predicate must scan the whole
        // array, not just args[0].
        App.IsSilentLaunch(["--debug", "--silent", "--profile"]).Should().BeTrue();
    }

    [Fact]
    public void IsSilentLaunch_OtherArgsButNoSilent_ReturnsFalse()
    {
        // Pin that the predicate doesn't accidentally fire on unrelated
        // args — a regression that changed the .Any predicate body could
        // otherwise treat any non-empty arg list as silent.
        App.IsSilentLaunch(["--debug", "--profile"]).Should().BeFalse();
    }
}
