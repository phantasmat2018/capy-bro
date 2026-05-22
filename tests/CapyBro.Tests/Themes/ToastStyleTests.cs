using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Effects;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for Themes/Toast.xaml (project_design_guide.md
/// §11 task 13 / §7.9). Verifies the Border style key exists, sets
/// the documented elevation-3 shadow, and applies cleanly to a
/// fresh Border via Measure().
/// </summary>
public class ToastStyleTests
{
    [Fact]
    public void ToastDictionary_Loads_AndContainsToastBorderStyle()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Toast.xaml");

            dict.Contains("ToastBorder").Should().BeTrue("Themes/Toast.xaml must declare `ToastBorder` per §7.9");
            ((Style)dict["ToastBorder"]).TargetType.Should().Be(typeof(Border));
        });

    [Fact]
    public void ToastBorder_DeclaresEightPxCornerRadius()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Toast.xaml");
            var style = (Style)dict["ToastBorder"];

            var cornerRadiusSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == Border.CornerRadiusProperty);

            cornerRadiusSetter.Should().NotBeNull("§5.2 Toast row mandates CornerRadius=8");
            cornerRadiusSetter!.Value.Should().Be(new CornerRadius(8.0));
        });

    [Fact]
    public void ToastBorder_DeclaresElevation3DropShadow()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Toast.xaml");
            var style = (Style)dict["ToastBorder"];

            var effectSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == UIElement.EffectProperty);

            effectSetter.Should().NotBeNull("§6.1 elevation-3 mandates a DropShadowEffect");
            effectSetter!.Value.Should().BeOfType<DropShadowEffect>(
                "Toast surface lifts via DropShadowEffect, not OpacityMask or other tricks");
        });

    [Fact]
    public void ToastBorder_AppliesCleanly_WhenAttachedToBorderInstance()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Toast.xaml");
            var style = (Style)dict["ToastBorder"];

            var border = new Border { Style = style };
            border.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Visual contract: setters resolved, no exceptions, the
            // style applied without bind-time fault.
            border.CornerRadius.Should().Be(new CornerRadius(8.0));
            border.BorderThickness.Should().Be(new Thickness(1.0));
            border.Effect.Should().BeOfType<DropShadowEffect>();
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
