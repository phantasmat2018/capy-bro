using System.IO;
using System.Xml.Linq;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Views;

/// <summary>
/// Static-text scan over the view XAML files for the §10 accessibility
/// contract — every icon-only button must declare
/// AutomationProperties.Name so screen-readers read meaningful prose
/// rather than the default "Button". We grep the raw XAML rather than
/// instantiate views (which would require a host Window + visual
/// tree) — the contract is purely structural.
///
/// Per design-guide §11 task 16 / §10.
/// </summary>
public class AccessibilityTests
{
    [Theory]
    [InlineData("Views/GeneralTab.xaml")]
    [InlineData("Views/ToastWindow.xaml")]
    public void EveryButtonIconOnly_DeclaresAutomationPropertiesName(string relativePath)
    {
        var fullPath = ResolveSourcePath(relativePath);
        var doc = XDocument.Load(fullPath);

        // ButtonIconOnly is the design-guide marker for "icon-only" —
        // any element with Style="{StaticResource ButtonIconOnly}" must
        // carry an AutomationProperties.Name so non-sighted users have
        // a label.
        var iconOnlyButtons = doc.Descendants()
            .Where(e => e.Attribute("Style")?.Value.Contains("ButtonIconOnly", StringComparison.Ordinal) == true)
            .ToArray();

        iconOnlyButtons.Should().NotBeEmpty(
            "every view scanned should host at least one icon-only button — if this fires, the test data needs updating");

        foreach (var button in iconOnlyButtons)
        {
            var hasName = button.Attribute("AutomationProperties.Name") != null
                || button.Attribute(XName.Get("AutomationProperties.Name", string.Empty)) != null;
            hasName.Should().BeTrue(
                "icon-only button at line {0} of `{1}` must declare AutomationProperties.Name (§10 screen-reader hints)",
                ((System.Xml.IXmlLineInfo)button).LineNumber,
                relativePath);
        }
    }

    [Fact]
    public void RevealablePasswordBoxEyeToggle_HasAccessibleName()
    {
        // The eye-toggle is a ToggleButton (not a ButtonIconOnly
        // styled Button), so the parametric scan above misses it.
        // Asserting it directly here keeps the §10 contract complete.
        var fullPath = ResolveSourcePath("Controls/RevealablePasswordBox.xaml");
        var doc = XDocument.Load(fullPath);

        var toggle = doc.Descendants()
            .First(e => e.Name.LocalName == "ToggleButton");

        toggle.Attribute("AutomationProperties.Name").Should().NotBeNull(
            "the password reveal/conceal toggle must announce itself to screen-readers");
    }

    [Fact]
    public void ToastWindowCancelButton_HasAccessibleName()
    {
        // Spot check — ToastWindow's cancel is the only icon-only
        // button outside GeneralTab today; this test ensures we don't
        // accidentally drop it from the parametric scan above.
        var fullPath = ResolveSourcePath("Views/ToastWindow.xaml");
        var doc = XDocument.Load(fullPath);

        var cancel = doc.Descendants()
            .First(e => e.Attribute("Name")?.Value == "CancelButton"
                || (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "CancelButton");

        cancel.Attribute("AutomationProperties.Name").Should().NotBeNull(
            "ToastWindow.CancelButton must announce itself to screen-readers");
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
}
