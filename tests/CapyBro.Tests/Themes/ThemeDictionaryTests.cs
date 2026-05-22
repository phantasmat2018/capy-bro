using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for the Phase A theming infrastructure
/// (project_design_guide.md §11 task 1). Loads each ResourceDictionary
/// from the source tree and asserts the design-system contract:
/// every documented Color key exists in both Light and Dark, the two
/// dictionaries declare an identical key set (so runtime theme swap
/// doesn't leave dangling DynamicResources), and Tokens.xaml exposes
/// a SolidColorBrush for every Color.
/// </summary>
public class ThemeDictionaryTests
{
    private static readonly string[] ExpectedColorKeys =
    [
        "BrandPrimaryColor",
        "BrandPrimaryHoverColor",
        "BrandPrimaryPressedColor",
        "AccentColor",
        "SurfaceCanvasColor",
        "SurfaceLayer1Color",
        "SurfaceLayer2Color",
        "SurfaceLayer3Color",
        "SurfaceSidebarMicaColor",
        "OnSurfaceStrongColor",
        "OnSurfaceDefaultColor",
        "OnSurfaceMutedColor",
        "OnSurfaceDisabledColor",
        "OnSurfaceInvertedColor",
        "StatusSuccessColor",
        "StatusErrorColor",
        "StatusWarningColor",
        "StatusInfoColor",
        // Diff highlighting tokens (§2.3 - diff preview line tinting).
        // Pre-Z9-F7: present in both palettes + Tokens.xaml but absent
        // from this expected list, so the Color/Brush round-trip checks
        // skipped them.  Adding here closes the gap.
        "DiffInsertBackgroundColor",
        "DiffDeleteBackgroundColor",
        "DiffModifiedBackgroundColor",
        "BorderSubtleColor",
        "BorderDefaultColor",
        "BorderStrongColor",
        "BorderFocusColor",
    ];

    [Theory]
    [InlineData("Themes/Colors.Light.xaml")]
    [InlineData("Themes/Colors.Dark.xaml")]
    public void ColorDictionary_Loads_AndContainsAllExpectedKeys(string relativePath)
        => RunOnSta(() =>
        {
            var dict = LoadDictionary(relativePath);

            foreach (var key in ExpectedColorKeys)
            {
                dict.Contains(key).Should().BeTrue(
                    "because {0} must define `{1}` (design-system §2.3)", relativePath, key);
                dict[key].Should().BeOfType<Color>(
                    "because `{0}` must be a raw Color, not a brush", key);
            }
        });

    [Fact]
    public void LightAndDark_DeclareIdenticalKeySets()
        => RunOnSta(() =>
        {
            var light = LoadDictionary("Themes/Colors.Light.xaml");
            var dark = LoadDictionary("Themes/Colors.Dark.xaml");

            var lightKeys = light.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k).ToArray();
            var darkKeys = dark.Keys.Cast<object>().Select(k => k.ToString()).OrderBy(k => k).ToArray();

            darkKeys.Should().Equal(
                lightKeys,
                "because runtime theme swap re-resolves DynamicResource against MergedDictionaries[0]; mismatched keys would leave dangling brushes");
        });

    [Fact]
    public void TokensDictionary_ExposesABrushForEveryColor()
        => RunOnSta(() =>
        {
            var tokens = LoadDictionary("Themes/Tokens.xaml");

            foreach (var colorKey in ExpectedColorKeys)
            {
                var brushKey = colorKey[..^"Color".Length] + "Brush";
                tokens.Contains(brushKey).Should().BeTrue(
                    "because every Color token needs a corresponding Brush token in Tokens.xaml ({0} → {1})",
                    colorKey, brushKey);
                tokens[brushKey].Should().BeOfType<SolidColorBrush>(
                    "because semantic tokens are SolidColorBrush instances bound via DynamicResource");
            }
        });

    // Z9-F7 / L21 — Round-trip palette parity that does NOT depend on the
    // hard-coded ExpectedColorKeys list.  Catches the failure mode where a
    // future contributor adds a new Color to Colors.Light + Colors.Dark
    // and Tokens.xaml but forgets to update ExpectedColorKeys (which would
    // silently skip the new key in every other test in this class).  The
    // existing `LightAndDark_DeclareIdenticalKeySets` covers Light↔Dark
    // parity; this covers Light/Dark → Tokens → Light/Dark.
    [Fact]
    public void EveryPaletteColor_HasBrushInTokens_AndEveryBrush_HasPaletteColor()
        => RunOnSta(() =>
        {
            var light = LoadDictionary("Themes/Colors.Light.xaml");
            var tokens = LoadDictionary("Themes/Tokens.xaml");

            var paletteKeys = light.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
            var brushKeys = tokens.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();

            // Forward: every *Color in the palette has a sibling *Brush in Tokens.
            var orphanColors = new List<string>();
            foreach (var colorKey in paletteKeys)
            {
                colorKey.Should().EndWith(
                    "Color",
                    "because palette entries are raw Colors named *Color (found `{0}`)", colorKey);
                var expectedBrush = colorKey[..^"Color".Length] + "Brush";
                if (!brushKeys.Contains(expectedBrush))
                {
                    orphanColors.Add($"{colorKey} → {expectedBrush}");
                }
            }

            orphanColors.Should().BeEmpty(
                "every palette Color must have a corresponding Brush in Tokens.xaml; otherwise DynamicResource consumers of the Brush would get nothing");

            // Reverse: every *Brush in Tokens references a *Color the palette declares.
            var orphanBrushes = new List<string>();
            foreach (var brushKey in brushKeys)
            {
                brushKey.Should().EndWith(
                    "Brush",
                    "because Tokens.xaml entries are SolidColorBrush named *Brush (found `{0}`)", brushKey);
                var expectedColor = brushKey[..^"Brush".Length] + "Color";
                if (!paletteKeys.Contains(expectedColor))
                {
                    orphanBrushes.Add($"{brushKey} → {expectedColor}");
                }
            }

            orphanBrushes.Should().BeEmpty(
                "every Tokens.xaml Brush must reference a Color the palette declares; otherwise the brush resolves to a default-black SolidColorBrush at runtime");
        });

    [Fact]
    public void TypographyDictionary_LoadsAndDefinesTypeScale()
        => RunOnSta(() =>
        {
            var typography = LoadDictionary("Themes/Typography.xaml");

            string[] expected =
            [
                "FontPrimary",
                "TypeDisplay",
                "TypeH1",
                "TypeH2",
                "TypeH3",
                "TypeBodyStrong",
                "TypeBody",
                "TypeCaption",
                "TypeSmall",
            ];

            foreach (var key in expected)
            {
                typography.Contains(key).Should().BeTrue(
                    "because Typography.xaml must declare `{0}` (design-system §4.2)", key);
            }
        });

    [Fact]
    public void SpacingDictionary_LoadsAndDefinesScale()
        => RunOnSta(() =>
        {
            var spacing = LoadDictionary("Themes/Spacing.xaml");

            string[] expectedDoubles =
            [
                "Space0",
                "Space2",
                "Space4",
                "Space8",
                "Space12",
                "Space16",
                "Space20",
                "Space24",
                "Space32",
                "Space48",
                "Space64",
            ];

            foreach (var key in expectedDoubles)
            {
                spacing.Contains(key).Should().BeTrue(
                    "because Spacing.xaml must declare `{0}` (design-system §5.1)", key);
                spacing[key].Should().BeOfType<double>();
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
        // Walk up from the test bin directory until we find the source folder.
        // bin/Debug/net8.0-windows/ → CapyBro.Tests/ → tests/ → repo-root → src/CapyBro/Themes/...
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

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh STA thread. WPF's XAML loader
    /// instantiates Freezable types (SolidColorBrush) which are most reliably
    /// constructed on STA; xUnit's default runner is MTA. Re-throws any
    /// captured exception so xUnit reporting works normally.
    /// </summary>
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
