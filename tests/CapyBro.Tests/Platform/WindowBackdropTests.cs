using System.Windows;

using CapyBro.Platform;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Platform;

/// <summary>
/// Tests for <see cref="WindowBackdrop"/>: build-number gate logic
/// (deterministic across CI runners) and behavioural smoke-tests on
/// real Window instances. We don't assert that DWM produces visible
/// Mica — that depends on the test runner's OS — only that the seam
/// behaves correctly: never throws, returns false on unsupported, and
/// rejects null arguments.
///
/// Resolves project_design_guide.md §13 Open Question 2 — the call to
/// DwmSetWindowAttribute is wrapped in TryApply, so unsupported OSes
/// degrade silently instead of throwing PlatformNotSupportedException.
/// </summary>
public class WindowBackdropTests
{
    // Build-number references: Win10 22H2 = 19045 (final); Win11 21H2 = 22000 (has only
    // the undocumented DWMWA_MICA_EFFECT, which we don't target); Win11 22H2 GA = 22621
    // (first documented DWMWA_SYSTEMBACKDROP_TYPE); Win11 23H2 = 22631; Win11 24H2 = 26100.
    [Theory]
    [InlineData(0, false)]
    [InlineData(19045, false)]
    [InlineData(22000, false)]
    [InlineData(22620, false)]
    [InlineData(22621, true)]
    [InlineData(22631, true)]
    [InlineData(26100, true)]
    public void IsSupportedOnBuild_GatesAtMinSupportedBuild(int buildNumber, bool expected)
    {
        WindowBackdrop.IsSupportedOnBuild(buildNumber)
            .Should().Be(expected, "build {0} should map to IsSupported={1}", buildNumber, expected);
    }

    [Fact]
    public void IsSupportedOnBuild_ExactlyAtThreshold_ReturnsTrue()
    {
        WindowBackdrop.IsSupportedOnBuild(WindowBackdrop.MinSupportedBuild).Should().BeTrue();
        WindowBackdrop.IsSupportedOnBuild(WindowBackdrop.MinSupportedBuild - 1).Should().BeFalse();
    }

    [Fact]
    public void TryApply_WithNullWindow_Throws()
    {
        Action act = () => WindowBackdrop.TryApply(null!, BackdropType.Mica);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void TryApply_OnUnsourcedWindow_ReturnsFalse_AndDoesNotThrow()
    {
        // A Window that's been instantiated but never Show()n has no
        // HWND yet. WindowBackdrop must detect this and bail rather
        // than passing IntPtr.Zero to the DWM (which would otherwise
        // succeed silently and leak the call).
        Exception? captured = null;
        bool? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window();
                result = WindowBackdrop.TryApply(window, BackdropType.Mica);
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        captured.Should().BeNull("TryApply must never throw — caller relies on graceful degradation");

        // On a supported OS, IsSupported is true but the HWND is zero, so we expect false.
        // On an unsupported OS, we also expect false. So in both worlds: false.
        result.Should().BeFalse();
    }

    [Fact]
    public void TryApply_OnSupportedHostMatchesIsSupported_OtherwiseFalse()
    {
        // Sanity check that the public IsSupported lines up with the
        // build-number gate when reading the real OSVersion. This
        // doesn't run DwmSetWindowAttribute (no Window) — just confirms
        // the static seam.
        var expected = WindowBackdrop.IsSupportedOnBuild(Environment.OSVersion.Version.Build);
        WindowBackdrop.IsSupported.Should().Be(expected);
    }
}
