using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for Themes/Buttons.xaml (project_design_guide.md
/// §11 task 4 / §7.1-7.3). Loads the dictionary, asserts that the
/// four documented styles exist, target Button, and declare the
/// expected size + corner-radius shape baked into their Templates.
///
/// We don't assert per-state colours pixel-by-pixel — those are in
/// Triggers and would require a full render pass to verify. Instead
/// we assert the static "shape" contract: TargetType, MinHeight,
/// FontWeight (where it differs from system default), and the
/// presence of the press-scale animation by walking the Template
/// tree for the named ScaleTransform.
/// </summary>
public class ButtonStylesTests
{
    private static readonly string[] ExpectedStyleKeys =
    [
        "ButtonDefault",
        "ButtonPrimary",
        "ButtonDestructive",
        "ButtonIconOnly",
    ];

    [Fact]
    public void ButtonsDictionary_Loads_AndContainsAllExpectedStyles()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Buttons.xaml");

            foreach (var key in ExpectedStyleKeys)
            {
                dict.Contains(key).Should().BeTrue(
                    "because Themes/Buttons.xaml must declare `{0}` per design-guide §7.1-7.3",
                    key);
                dict[key].Should().BeOfType<Style>(
                    "because every Button*.xaml resource is a Style");
                ((Style)dict[key]).TargetType.Should().Be(
                    typeof(Button),
                    "because all four button styles target System.Windows.Controls.Button");
            }
        });

    [Theory]
    [InlineData("ButtonDefault", 32.0)]
    [InlineData("ButtonPrimary", 40.0)]
    [InlineData("ButtonDestructive", 40.0)]
    public void ButtonStyle_DeclaresExpectedMinHeight(string styleKey, double expectedMinHeight)
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Buttons.xaml");
            var style = (Style)dict[styleKey];

            var minHeightSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.MinHeightProperty);

            minHeightSetter.Should().NotBeNull(
                "because `{0}` must explicitly declare a MinHeight per design-guide §5.2 sizing table",
                styleKey);
            minHeightSetter!.Value.Should().Be(
                expectedMinHeight,
                "because `{0}` should be {1}px tall (§5.2)",
                styleKey,
                expectedMinHeight);
        });

    [Theory]
    [InlineData("ButtonIconOnly", 32.0, 32.0)]
    public void IconOnlyStyle_DeclaresFixedWidthAndHeight(string styleKey, double expectedWidth, double expectedHeight)
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Buttons.xaml");
            var style = (Style)dict[styleKey];

            var widthSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.WidthProperty);
            var heightSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.HeightProperty);

            widthSetter.Should().NotBeNull("ButtonIconOnly is a fixed-size square per §5.2");
            heightSetter.Should().NotBeNull();
            widthSetter!.Value.Should().Be(expectedWidth);
            heightSetter!.Value.Should().Be(expectedHeight);
        });

    [Theory]
    [InlineData("ButtonPrimary")]
    [InlineData("ButtonDestructive")]
    public void CtaStyles_DeclareSemiBoldWeight(string styleKey)
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Buttons.xaml");
            var style = (Style)dict[styleKey];

            var fontWeightSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == Control.FontWeightProperty);

            fontWeightSetter.Should().NotBeNull(
                "because Primary/Destructive CTAs should weigh more than body text per §4.2 BodyStrong");
            fontWeightSetter!.Value.Should().Be(FontWeights.SemiBold);
        });

    [Theory]
    [InlineData("ButtonDefault")]
    [InlineData("ButtonPrimary")]
    [InlineData("ButtonDestructive")]
    [InlineData("ButtonIconOnly")]
    public void EveryStyle_HasControlTemplateAndAnimatedPressScale(string styleKey)
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Buttons.xaml");
            var style = (Style)dict[styleKey];

            var templateSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == Control.TemplateProperty);

            templateSetter.Should().NotBeNull(
                "because `{0}` must define a ControlTemplate to render the §7 state contract",
                styleKey);

            var template = (ControlTemplate)templateSetter!.Value;
            template.TargetType.Should().Be(typeof(Button));

            // XamlReader parses templates into the modern (post-WPF 3.5)
            // TemplateContent rather than the legacy FrameworkElementFactory,
            // so template.VisualTree is null and we have to instantiate to
            // inspect named elements. Measure() forces ApplyTemplate.
            var button = new Button { Style = style };
            button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var scale = button.Template?.FindName("PressScale", button) as System.Windows.Media.ScaleTransform;
            scale.Should().NotBeNull(
                "because §8.3 press-scale animation requires an x:Name=\"PressScale\" ScaleTransform inside the template ({0})",
                styleKey);
        });

    [Fact]
    public void EveryStyle_DeclaresHandCursor()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Buttons.xaml");

            foreach (var key in ExpectedStyleKeys)
            {
                var style = (Style)dict[key];
                var cursorSetter = style.Setters
                    .OfType<Setter>()
                    .FirstOrDefault(s => s.Property == FrameworkElement.CursorProperty);

                cursorSetter.Should().NotBeNull(
                    "because every clickable button should hint affordance via Cursor=Hand ({0})",
                    key);
            }
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
