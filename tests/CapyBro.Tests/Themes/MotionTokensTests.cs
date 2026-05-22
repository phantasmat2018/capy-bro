using System.IO;
using System.Windows;
using System.Windows.Markup;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for Themes/Motion.xaml (project_design_guide.md
/// §11 task 14 / §8.1). The tokens are stored as String values
/// because Duration has no parameterless ctor that XAML can use; the
/// consumer's Duration property converts via DurationConverter.
/// </summary>
public class MotionTokensTests
{
    private static readonly (string Key, string Expected)[] ExpectedTokens =
    [
        ("Motion.Instant", "0:0:0"),
        ("Motion.Fast", "0:0:0.12"),
        ("Motion.Default", "0:0:0.24"),
        ("Motion.Slow", "0:0:0.4"),
    ];

    [Fact]
    public void MotionDictionary_Loads_AndContainsAllFourTokens()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Motion.xaml");

            foreach (var (key, expected) in ExpectedTokens)
            {
                dict.Contains(key).Should().BeTrue("Themes/Motion.xaml must declare `{0}` per §8.1", key);
                dict[key].Should().BeOfType<string>(
                    "Motion tokens are strings so they can be either token-shadowed by ReduceMotion at startup or parsed as Duration on first read");
                dict[key].Should().Be(expected, "token `{0}` should be `{1}`", key, expected);
            }
        });

    [Fact]
    public void MotionFastToken_ParsesViaDurationConverter()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Motion.xaml");
            var raw = (string)dict["Motion.Fast"];

            var converter = System.ComponentModel.TypeDescriptor.GetConverter(typeof(Duration));
            var duration = (Duration)converter.ConvertFromInvariantString(raw)!;

            duration.HasTimeSpan.Should().BeTrue();
            duration.TimeSpan.Should().Be(TimeSpan.FromMilliseconds(120));
        });

    private static ResourceDictionary LoadDictionary(string relativePath)
    {
        var fullPath = ResolveSourcePath(relativePath);
        using var stream = File.OpenRead(fullPath);
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    private static string ResolveSourcePath(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var probe = Path.Combine(dir, "src", "CapyBro", normalized);
            if (File.Exists(probe))
            {
                return probe;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from {AppContext.BaseDirectory}");
    }

    private static void RunOnSta(Action body)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (captured is not null)
        {
            throw captured;
        }
    }
}
