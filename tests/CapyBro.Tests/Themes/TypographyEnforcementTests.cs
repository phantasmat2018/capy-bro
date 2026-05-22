using System.IO;
using System.Text.RegularExpressions;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Themes;

/// <summary>
/// Z9-F5 / L19 enforcement test for Typography.xaml §4.2.
///
/// The design guide states "every TextBlock has a Style" (Style="{DynamicResource
/// TypeXxx}").  Typography.xaml itself documents the invariant but does not
/// enforce it — pre-fix, a new free-standing TextBlock could ship without a
/// font directive and render with the WPF default 12-pt Segoe UI black,
/// invisible against the dark canvas, in any new view.
///
/// Implementation: scan every production XAML opener for &lt;TextBlock ...&gt;
/// and assert it declares one of Style / FontFamily / FontSize / FontWeight /
/// Foreground.  Foreground counts as a font directive because a TextBlock
/// with an explicit Foreground has explicitly opted into a styling decision
/// even if it inherits other font properties from a parent template.
///
/// Known exceptions are documented inline below — these are TextBlocks
/// nested inside DataTemplate / ItemTemplate / Button content where the
/// font is inherited from the styled parent (ComboBox style, Button style,
/// etc.) via WPF property-value inheritance.  Adding a Style there would
/// override the parent's intended typography.  The test is BIDIRECTIONAL:
/// stale exceptions (TextBlocks that since got an explicit Style) also fail
/// the test, forcing the exception list to track the production tree.
/// </summary>
public partial class TypographyEnforcementTests
{
    // Match <TextBlock ...> or <TextBlock ... />.  Negative-context after
    // TextBlock excludes <TextBlock.Style> property-element openers; those
    // are NOT instances of TextBlock, they're property setters on a parent
    // TextBlock declared earlier.
    [GeneratedRegex(@"<TextBlock(?=[\s/>])[^>]*>", RegexOptions.Compiled)]
    private static partial Regex TextBlockOpenerPattern();

    private static readonly string[] FontDirectives =
    [
        "Style=",
        "FontFamily=",
        "FontSize=",
        "FontWeight=",
        "Foreground=",
    ];

    // Exception list — each entry must be paired with a comment explaining
    // why the TextBlock at that location legitimately inherits font from a
    // styled parent rather than declaring its own.  Adding an entry is a
    // design decision; reviewers should push back ("can this just use a
    // Style?") before approving.
    private static readonly HashSet<string> KnownTemplateInheritedExceptions =
    [
        // ComboBox.ItemTemplate — language picker.  Font inherits from the
        // ComboBox style (Themes/Inputs.xaml InputComboBox).
        // OnboardingWizard line shifted 176→205 when the per-step footer
        // homepage hyperlink (capybro.app) was added to the wizard's
        // bottom Border above — pushed the ComboBox.ItemTemplate further
        // down in the file.  Same TextBlock, new line.
        // GeneralTab line shifted across v15 iterations (84→132→124→161→167→178)
        // as the Provider card + Ollama section + Additional features
        // refactor + Ollama-probe spinner StackPanel + visibility-bound
        // Provider card + Provider-card-bottom-margin polish moved the
        // Language ComboBox.ItemTemplate around.
        "GeneralTab.xaml:178",
        "OnboardingWizard.xaml:205",
        // ListBox.ItemTemplate — prompt-picker per-prompt row.  Font
        // inherits from the parent ListBoxItem.
        "PromptsTab.xaml:121",
        // Button content — caption / action buttons in History + Prompts
        // tabs.  Font inherits from ButtonDefault / ButtonPrimary style.
        // Z3-F3 / M7 — PromptsTab line numbers shifted (327→221, 372→266)
        // when the editor pane was restructured into a DockPanel +
        // empty-state overlay; the button TextBlocks themselves are
        // unchanged but the surrounding XAML grew an empty-state block
        // above them.
        "HistoryTab.xaml:104",
        "PromptsTab.xaml:221",
        "PromptsTab.xaml:266",
    ];

    [Fact]
    public void EveryTextBlockOpener_DeclaresStyleOrExplicitFontProperty()
    {
        var srcRoot = LocateSourceRoot();
        srcRoot.Should().NotBeNull("test must locate the production source tree");

        var violations = new List<string>();
        foreach (var xaml in Directory.EnumerateFiles(srcRoot!, "*.xaml", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(xaml);
            foreach (Match m in TextBlockOpenerPattern().Matches(content))
            {
                var opener = m.Value;
                if (FontDirectives.Any(d => opener.Contains(d, StringComparison.Ordinal)))
                {
                    continue;
                }

                // Line number: count newlines BEFORE the match start, +1
                // (1-indexed).  Robust against CRLF since LF is part of CRLF.
                var lineNumber = 1;
                for (var i = 0; i < m.Index; i++)
                {
                    if (content[i] == '\n')
                    {
                        lineNumber++;
                    }
                }

                violations.Add($"{Path.GetFileName(xaml)}:{lineNumber}");
            }
        }

        var unaccounted = violations.Where(v => !KnownTemplateInheritedExceptions.Contains(v)).ToList();
        unaccounted.Should().BeEmpty(
            "every TextBlock outside a styled-parent template must declare Style, FontFamily, FontSize, FontWeight, or Foreground (Typography.xaml §4.2). Add the missing attribute or, if the TextBlock genuinely inherits from a styled parent template, append it to KnownTemplateInheritedExceptions with justification.");

        var staleExceptions = KnownTemplateInheritedExceptions.Where(e => !violations.Contains(e)).ToList();
        staleExceptions.Should().BeEmpty(
            "every entry in KnownTemplateInheritedExceptions must still correspond to a violation; otherwise the exception is stale and should be removed");
    }

    private static string? LocateSourceRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CapyBro");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
