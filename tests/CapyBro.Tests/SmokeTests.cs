using FluentAssertions;

using Xunit;

namespace CapyBro.Tests;

public class SmokeTests
{
    [Fact]
    public void Solution_Builds_And_Tests_Run()
    {
        true.Should().BeTrue();
    }
}
