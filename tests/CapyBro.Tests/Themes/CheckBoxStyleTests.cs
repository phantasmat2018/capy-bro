using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

using FluentAssertions;

using Xunit;

using ShapesPath = System.Windows.Shapes.Path;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for Themes/CheckBox.xaml (project_design_guide.md
/// §11 task 7 / §7.6 + §8.3).
/// </summary>
public class CheckBoxStyleTests
{
    [Fact]
    public void CheckBoxDictionary_Loads_AndContainsDefaultCheckBoxStyle()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/CheckBox.xaml");

            dict.Contains("DefaultCheckBox").Should().BeTrue("Themes/CheckBox.xaml must declare `DefaultCheckBox`");
            ((Style)dict["DefaultCheckBox"]).TargetType.Should().Be(typeof(CheckBox));
        });

    [Fact]
    public void DefaultCheckBox_TemplateExposesGlyphAndTickElements()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/CheckBox.xaml");
            var style = (Style)dict["DefaultCheckBox"];

            var checkBox = new CheckBox { Style = style, Content = "label" };
            checkBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // §7.6 16x16 rounded glyph + Icon.Check 12x12 inside.
            var glyph = checkBox.Template?.FindName("Glyph", checkBox) as Border;
            var tick = checkBox.Template?.FindName("Tick", checkBox) as ShapesPath;
            var tickScale = checkBox.Template?.FindName("TickScale", checkBox) as ScaleTransform;

            glyph.Should().NotBeNull("§7.6 mandates a 16x16 rounded-square glyph element");
            glyph!.Width.Should().Be(16.0);
            glyph.Height.Should().Be(16.0);
            glyph.CornerRadius.Should().Be(new CornerRadius(4.0));

            tick.Should().NotBeNull("§7.6 tick is the Icon.Check Path element");
            tickScale.Should().NotBeNull("§8.3 tick scale animation needs an x:Name=\"TickScale\" ScaleTransform target");
        });

    [Fact]
    public void DefaultCheckBox_HasHandCursor()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/CheckBox.xaml");
            var style = (Style)dict["DefaultCheckBox"];

            var cursor = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.CursorProperty);

            cursor.Should().NotBeNull("checkboxes should hint affordance — Cursor=Hand on hover");
        });

    [Fact]
    public void DefaultCheckBox_TickStartsHidden_AndScaledDown()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/CheckBox.xaml");
            var style = (Style)dict["DefaultCheckBox"];

            var checkBox = new CheckBox { Style = style, Content = "label" };
            checkBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var tick = (ShapesPath)checkBox.Template!.FindName("Tick", checkBox)!;
            var scale = (ScaleTransform)checkBox.Template!.FindName("TickScale", checkBox)!;

            // Initial state (Unchecked): tick invisible + scaled down
            // so the §8.3 EaseOut animation has somewhere to grow from.
            tick.Opacity.Should().Be(0.0, "tick must start invisible — IsChecked=True animates Opacity 0->1");
            scale.ScaleX.Should().Be(0.4, "tick must start scaled down so the toggle has a 'pop' to animate");
            scale.ScaleY.Should().Be(0.4);
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
