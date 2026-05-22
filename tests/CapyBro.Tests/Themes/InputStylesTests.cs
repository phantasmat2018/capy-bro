using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Snapshot-tests for Themes/Inputs.xaml (project_design_guide.md
/// §11 task 5 / §7.4-7.5). Verifies the four x:Key'd input styles
/// exist, target the right control types, and declare the §5.2 sizing
/// contract. Per-state colour triggers are covered by Buttons-style
/// instantiation pattern: Measure() forces ApplyTemplate so we can
/// assert template parts (PART_ContentHost for text controls,
/// PART_EditableTextBox for the editable ComboBox path).
/// </summary>
public class InputStylesTests
{
    private static readonly (string Key, Type TargetType)[] ExpectedStyles =
    [
        ("InputTextBox", typeof(TextBox)),
        ("InputPasswordBox", typeof(PasswordBox)),
        ("InputComboBox", typeof(ComboBox)),
        ("InputComboBoxItem", typeof(ComboBoxItem)),
    ];

    [Fact]
    public void InputsDictionary_Loads_AndContainsAllExpectedStyles()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Inputs.xaml");

            foreach (var (key, targetType) in ExpectedStyles)
            {
                dict.Contains(key).Should().BeTrue(
                    "because Themes/Inputs.xaml must declare `{0}` per design-guide §7.4-7.5",
                    key);
                dict[key].Should().BeOfType<Style>();
                ((Style)dict[key]).TargetType.Should().Be(
                    targetType,
                    "because `{0}` targets {1}",
                    key,
                    targetType.Name);
            }
        });

    [Theory]
    [InlineData("InputTextBox", 32.0)]
    [InlineData("InputPasswordBox", 32.0)]
    [InlineData("InputComboBox", 32.0)]
    public void InputStyles_DeclareDocumented32pxMinHeight(string styleKey, double expectedMinHeight)
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Inputs.xaml");
            var style = (Style)dict[styleKey];

            var minHeightSetter = style.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == FrameworkElement.MinHeightProperty);

            minHeightSetter.Should().NotBeNull(
                "every input must explicitly declare MinHeight per §5.2 sizing table ({0})",
                styleKey);
            minHeightSetter!.Value.Should().Be(expectedMinHeight);
        });

    [Fact]
    public void InputTextBox_TemplateExposesPartContentHost()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Inputs.xaml");
            var style = (Style)dict["InputTextBox"];

            var textBox = new TextBox { Style = style };
            textBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // PART_ContentHost is the well-known WPF template part that
            // TextBoxBase looks up to wire up text rendering. If it
            // disappears, IME / selection / scrolling all break silently.
            textBox.Template?.FindName("PART_ContentHost", textBox)
                .Should().NotBeNull("TextBox template must expose PART_ContentHost");
        });

    [Fact]
    public void InputPasswordBox_TemplateExposesPartContentHost()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Inputs.xaml");
            var style = (Style)dict["InputPasswordBox"];

            var passwordBox = new PasswordBox { Style = style };
            passwordBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            passwordBox.Template?.FindName("PART_ContentHost", passwordBox)
                .Should().NotBeNull("PasswordBox template must expose PART_ContentHost");
        });

    [Fact]
    public void InputComboBox_TemplateExposesPartEditableTextBoxAndPopup()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Inputs.xaml");
            var style = (Style)dict["InputComboBox"];

            var comboBox = new ComboBox { Style = style };
            comboBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // PART_EditableTextBox is required for IsEditable=True mode;
            // PART_Popup hosts the dropdown content. ComboBox source
            // looks up both by name — missing parts mean the editable
            // mode silently degrades or the popup never opens.
            comboBox.Template?.FindName("PART_EditableTextBox", comboBox)
                .Should().NotBeNull("editable ComboBox needs PART_EditableTextBox");
            comboBox.Template?.FindName("PART_Popup", comboBox)
                .Should().NotBeNull("ComboBox dropdown needs PART_Popup");
        });

    [Fact]
    public void InputComboBox_DeclaresInputComboBoxItemAsItemContainerStyle()
        => RunOnSta(() =>
        {
            var dict = LoadDictionary("Themes/Inputs.xaml");
            var combo = (Style)dict["InputComboBox"];
            var item = (Style)dict["InputComboBoxItem"];

            var setter = combo.Setters
                .OfType<Setter>()
                .FirstOrDefault(s => s.Property == ItemsControl.ItemContainerStyleProperty);

            setter.Should().NotBeNull(
                "InputComboBox should set ItemContainerStyle to InputComboBoxItem so popup items pick up §7.5 hover/selected states");
            setter!.Value.Should().BeSameAs(
                item,
                "ItemContainerStyle should reference the same Style instance as the dictionary's `InputComboBoxItem` entry");
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
