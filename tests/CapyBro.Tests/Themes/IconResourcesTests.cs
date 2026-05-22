using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for the Phase A task 2 iconography infrastructure
/// (project_design_guide.md §11 task 2). Loads Themes/Icons.xaml from
/// the source tree and asserts: (a) every documented Lucide icon key
/// exists, (b) every icon is a non-empty Geometry.
///
/// The earlier brand-logomark Logo.AMonogram* keys + their runtime
/// rasteriser were retired when the canonical brand asset moved to a
/// raster PNG (assets/logo.png + logo.ico) consumed via pack URI.
///
/// Adding an icon? Append its key to <see cref="ExpectedLucideKeys"/>
/// AND add the StreamGeometry to Themes/Icons.xaml — this test will
/// fail loudly if either side is missed.
/// </summary>
public class IconResourcesTests
{
    private static readonly string[] ExpectedLucideKeys =
    [
        "Icon.AlertCircle",
        "Icon.AlertTriangle",
        "Icon.Check",
        "Icon.ChevronDown",
        "Icon.ChevronRight",
        "Icon.Copy",
        "Icon.DownloadCloud",
        "Icon.Edit3",
        "Icon.Eye",
        "Icon.EyeOff",
        "Icon.Globe",
        "Icon.HelpCircle",
        "Icon.History",
        "Icon.Info",
        "Icon.Key",
        "Icon.Keyboard",
        "Icon.Loader2",
        "Icon.Play",
        "Icon.Plus",
        "Icon.RefreshCw",
        "Icon.Search",
        "Icon.Settings",
        "Icon.Square",
        "Icon.Trash2",
        "Icon.X",
    ];

    [Fact]
    public void IconsDictionary_Loads_AndContainsEveryLucideKey()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Icons.xaml");

            foreach (var key in ExpectedLucideKeys)
            {
                dict.Contains(key).Should().BeTrue(
                    "because Themes/Icons.xaml must declare `{0}` per design-guide §3.3 canonical icon set",
                    key);
                dict[key].Should().BeAssignableTo<Geometry>(
                    "because `{0}` must resolve to a Geometry usable as Path.Data",
                    key);
            }
        });

    [Fact]
    public void EveryIconGeometry_IsNonEmpty()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Icons.xaml");

            foreach (var key in ExpectedLucideKeys)
            {
                var geometry = (Geometry)dict[key];
                geometry.Bounds.IsEmpty.Should().BeFalse(
                    "because `{0}` should rasterise to a non-empty bitmap; an empty Bounds means the path string parsed but produced no figures",
                    key);
            }
        });

    [Fact]
    public void IconKeySet_IsExactlyLucideAndLogomark_NoStrays()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Icons.xaml");
            var declaredKeys = dict.Keys.Cast<object>().Select(k => k.ToString()!).OrderBy(k => k).ToArray();
            var expectedKeys = ExpectedLucideKeys.OrderBy(k => k).ToArray();

            declaredKeys.Should().Equal(
                expectedKeys,
                "because Themes/Icons.xaml should declare exactly the documented Lucide subset — strays drift the test contract");
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
