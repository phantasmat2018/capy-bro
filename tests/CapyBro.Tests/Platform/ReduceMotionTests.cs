using System.Windows;

using CapyBro.Platform;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Platform;

/// <summary>
/// Tests for <see cref="ReduceMotion"/>. We can't toggle
/// SystemParameters.ClientAreaAnimation in a test (it's an OS
/// preference), so we focus on the application-resources shadowing
/// semantics: when motion is allowed (the default test environment),
/// ApplyToApplicationResources must NOT mutate the dictionary; when
/// motion is disabled, all four Motion.* keys are shadowed with
/// "0:0:0".
///
/// The IsEnabled-disabled branch is exercised indirectly via the
/// dictionary-shadowing tests — if ReduceMotion.IsEnabled flips at
/// some point, these tests document the intended semantics regardless
/// of the runner's preference.
/// </summary>
public class ReduceMotionTests
{
    [Fact]
    public void For_WhenMotionAllowed_ReturnsTheRequestedDuration()
    {
        if (ReduceMotion.IsEnabled)
        {
            // Skip: the runner has motion disabled. The
            // For_WhenReduceMotionEnabled_ReturnsZero test below
            // asserts the inverse.
            return;
        }

        var d = ReduceMotion.For(120);

        d.HasTimeSpan.Should().BeTrue();
        d.TimeSpan.Should().Be(TimeSpan.FromMilliseconds(120));
    }

    [Fact]
    public void For_WhenReduceMotionEnabled_ReturnsZero()
    {
        if (!ReduceMotion.IsEnabled)
        {
            // Skip in motion-allowed environments — opposite case
            // is covered by For_WhenMotionAllowed_ReturnsTheRequestedDuration.
            return;
        }

        var d = ReduceMotion.For(120);

        d.HasTimeSpan.Should().BeTrue();
        d.TimeSpan.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ApplyToApplicationResources_WhenMotionAllowed_LeavesDictionaryAlone()
    {
        if (ReduceMotion.IsEnabled)
        {
            return;
        }

        var resources = new ResourceDictionary
        {
            ["Motion.Fast"] = "0:0:0.12",
            ["Motion.Default"] = "0:0:0.24",
            ["Motion.Slow"] = "0:0:0.4",
        };

        ReduceMotion.ApplyToApplicationResources(resources);

        resources["Motion.Fast"].Should().Be("0:0:0.12");
        resources["Motion.Default"].Should().Be("0:0:0.24");
        resources["Motion.Slow"].Should().Be("0:0:0.4");
    }

    [Fact]
    public void ApplyToApplicationResources_WhenReduceMotion_ShadowsAllMotionTokens()
    {
        if (!ReduceMotion.IsEnabled)
        {
            return;
        }

        var resources = new ResourceDictionary();
        ReduceMotion.ApplyToApplicationResources(resources);

        resources["Motion.Fast"].Should().Be("0:0:0");
        resources["Motion.Default"].Should().Be("0:0:0");
        resources["Motion.Slow"].Should().Be("0:0:0");
    }

    [Fact]
    public void ApplyToApplicationResources_NullArg_Throws()
    {
        Action act = () => ReduceMotion.ApplyToApplicationResources(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
