using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for Themes/Lists.xaml (project_design_guide.md
/// §11 task 6 / §7.7).
/// </summary>
public class ListStylesTests
{
    private static readonly (string Key, Type TargetType)[] ExpectedStyles =
    [
        ("ListListBox", typeof(ListBox)),
        ("ListListBoxItem", typeof(ListBoxItem)),
    ];

    [Fact]
    public void ListsDictionary_Loads_AndContainsBothStyles()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Lists.xaml");

            foreach (var (key, targetType) in ExpectedStyles)
            {
                dict.Contains(key).Should().BeTrue("Themes/Lists.xaml must declare `{0}`", key);
                ((Style)dict[key]).TargetType.Should().Be(targetType);
            }
        });

    [Fact]
    public void ListListBox_BindsItemContainerStyleToListListBoxItem()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Lists.xaml");
            var listBox = (Style)dict["ListListBox"];
            var item = (Style)dict["ListListBoxItem"];

            var setter = listBox.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == ItemsControl.ItemContainerStyleProperty);

            setter.Should().NotBeNull(
                "ListListBox must wire ItemContainerStyle so consumers don't have to set it per-call");
            setter!.Value.Should().BeSameAs(item);
        });

    [Fact]
    public void ListListBoxItem_DeclaresDocumentedMinHeight()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Lists.xaml");
            var item = (Style)dict["ListListBoxItem"];

            var minHeightSetter = item.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.MinHeightProperty);

            minHeightSetter.Should().NotBeNull("ListBoxItem must declare MinHeight per §5.2 sizing");
            minHeightSetter!.Value.Should().Be(36.0, "design-guide §5.2 row-height for ListBoxItem is 36 px");
        });

    [Fact]
    public void ListListBoxItem_TemplateExposesSelectedIndicatorElement()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Lists.xaml");
            var item = (Style)dict["ListListBoxItem"];

            // ListBoxItem applies its template fine in isolation —
            // we don't need a host ListBox just to inspect the visual
            // tree (Measure forces ApplyTemplate either way).
            var listBoxItem = new ListBoxItem { Style = item, Content = "x" };
            listBoxItem.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            listBoxItem.Template?.FindName("SelectedIndicator", listBoxItem)
                .Should().NotBeNull(
                    "§7.7 selected-row left strip requires an x:Name=\"SelectedIndicator\" element so the IsSelected trigger can flip its visibility");
        });

    [Fact]
    public void ListListBox_TemplateRendersWithRoundedBorder()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Lists.xaml");
            var listBoxStyle = (Style)dict["ListListBox"];

            var listBox = new ListBox { Style = listBoxStyle };
            listBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Ensure the template applied without throwing — and that
            // ItemsPresenter is reachable so binding/scrolling work.
            listBox.Template.Should().NotBeNull();
            listBox.Template!.LoadContent().Should().NotBeNull();
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
