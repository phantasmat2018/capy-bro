using System.Windows;

namespace CapyBro.Platform;

/// <summary>
/// Reduce-motion fallback per project_design_guide.md §8.4. When the
/// user has disabled "Show animations in Windows" (Settings →
/// Accessibility → Visual effects), we collapse all motion durations
/// to zero so storyboards play instantly.
///
/// Two seams:
///  1. Code-behind animations (TransitioningContentControl,
///     RevealablePasswordBox toggles) call <see cref="For(double)"/>
///     to derive a Duration that's either the requested timing or
///     <see cref="Duration.Automatic"/>-equivalent zero.
///  2. XAML Storyboard triggers (Buttons.xaml press scale, CheckBox
///     tick) reference Motion.* tokens via DynamicResource.
///     <see cref="ApplyToApplicationResources"/> is called once at
///     startup; if the OS preference is off, it shadows the four
///     Motion.* resources in App.Resources with "0:0:0" string values
///     so subsequent DynamicResource lookups resolve to the no-op
///     duration without re-walking the visual tree.
///
/// We don't subscribe to a system preference change because Windows
/// doesn't broadcast SystemParameters.ClientAreaAnimation changes
/// usefully at runtime; the canonical UX expectation is that the app
/// is restarted after a Settings change anyway.
/// </summary>
internal static class ReduceMotion
{
    /// <summary>
    /// True when the OS has animations disabled. Captured once on
    /// first read; stable for the lifetime of the process so
    /// everywhere that branches on it stays consistent (no half-way
    /// state where some animations honour the setting and others
    /// don't).
    /// </summary>
    public static bool IsEnabled { get; } = !SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Returns a <see cref="Duration"/> of <paramref name="milliseconds"/>
    /// when motion is allowed, else zero.
    /// </summary>
    public static Duration For(double milliseconds) =>
        IsEnabled
            ? new Duration(TimeSpan.Zero)
            : new Duration(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// Idempotent: shadows Motion.Fast / Default / Slow with "0:0:0"
    /// in the supplied <paramref name="resources"/> dictionary if the
    /// OS preference is off. App.OnStartup calls this once with
    /// <c>Application.Current.Resources</c>; tests can pass a fresh
    /// ResourceDictionary to verify the override semantics without
    /// touching the live app.
    /// </summary>
    public static void ApplyToApplicationResources(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!IsEnabled)
        {
            return;
        }

        // Top-level dictionary entries shadow merged-dict entries with
        // the same key, so DynamicResource consumers resolve to these
        // overrides without us needing to walk merged dicts.
        resources["Motion.Fast"] = "0:0:0";
        resources["Motion.Default"] = "0:0:0";
        resources["Motion.Slow"] = "0:0:0";
    }
}
