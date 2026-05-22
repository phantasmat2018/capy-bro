using System.Reflection;

namespace CapyBro.Services;

/// <summary>
/// Static accessor for build-time metadata that XAML / ViewModels need
/// to surface in the UI.  Today: app version for the sidebar footer
/// and About dialogs; future: build channel (stable / beta), commit
/// SHA, etc.
///
/// Static-class-with-static-property idiom (same shape as
/// <see cref="Translator.Instance"/>) so XAML can bind via
/// <c>{x:Static services:AppInfo.DisplayVersion}</c> without going
/// through the DI container — this is purely build-time data, no
/// service lifecycle to manage, and keeping it global avoids
/// threading a singleton through every consumer purely to read a
/// version string.
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Three-part semver string ("2.0.0") sourced from the entry
    /// assembly's AssemblyVersion attribute (which the .csproj
    /// populates from the &lt;Version&gt; element).  We deliberately
    /// drop the four-part version's trailing "0" because the .NET
    /// runtime always produces four parts (major.minor.build.revision)
    /// even when the .csproj only declares three — without the trim
    /// the sidebar would render "v2.0.0.0", which reads as a typo.
    ///
    /// Falls back to "?" when no entry assembly is available
    /// (test hosts, design-time loaders, edge cases): better than
    /// throwing in a property getter that the UI binds at first
    /// render.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>
    /// Bind this from XAML to render a version label.  The "v" prefix
    /// is conventional in app footers (GitHub, Vercel, Discord) and
    /// makes the string read as a version rather than a random number
    /// when the user spots it out of context.
    /// </summary>
    public static string DisplayVersion { get; } = $"v{Version}";

    /// <summary>
    /// Canonical homepage URL for the project.  Single source of truth
    /// referenced from the Settings sidebar footer link.  Typed as
    /// <see cref="Uri"/> rather than <c>string</c> so the XAML
    /// <c>{x:Static services:AppInfo.Homepage}</c> binding hands
    /// <see cref="System.Windows.Documents.Hyperlink.NavigateUri"/>
    /// the type it expects without relying on a markup-time
    /// string→Uri type-converter round-trip.  The installer
    /// (installer/installer.nsi) hardcodes the same string in its
    /// <c>URLInfoAbout</c> registry value — NSIS can't read C#
    /// constants, so a future domain rename has to touch both files.
    /// </summary>
    public static Uri Homepage { get; } = new Uri("https://capybro.app");

    /// <summary>
    /// Visible link text for the sidebar footer.  Host-only form
    /// (<c>capybro.app</c>) — pairs with the current single-line
    /// "<c>v2.0.0 · capybro.app</c>" footer layout where the bullet
    /// separator already cues that the right segment is a destination,
    /// and where the full <c>https://</c> prefix would crowd the row
    /// in the 224-px-wide sidebar.  The actual navigated URI
    /// (<see cref="Homepage"/>) still carries the scheme — this is
    /// the human-facing label only.
    /// </summary>
    public static string HomepageDisplay { get; } = "capybro.app";

    private static string ResolveVersion()
    {
        // GetEntryAssembly is null in WPF design-time loaders and some
        // test runners; fall back to the executing assembly so the
        // property is never null at the binding site.
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version;
        if (version is null)
        {
            return "?";
        }

        // Three-part display: major.minor.build.  Revision is always
        // 0 in our build (we don't auto-increment), so showing it
        // would just add noise.
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
