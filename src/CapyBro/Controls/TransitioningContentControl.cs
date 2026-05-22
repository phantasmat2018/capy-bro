using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

using CapyBro.Platform;

namespace CapyBro.Controls;

/// <summary>
/// ContentControl variant that fades + slides the new content into
/// view whenever Content changes — implements project_design_guide.md
/// §8.3 tab-switch motion: fade-in (Opacity 0 → 1) paired with a
/// slide-up (TranslateTransform Y 8 → 0) over 240 ms QuinticEase
/// EaseOut. Used in SettingsWindow to soften the General ↔ Prompts
/// content swap.
///
/// Why a custom subclass rather than a Storyboard resource:
/// ContentControl raises no XAML-targetable event for "content
/// changed" (Loaded fires once; OnContentChanged is the only hook on
/// the change). A Storyboard would also need a way to time-out the
/// out-going content's visual — capturing it via VisualBrush snapshot
/// is brittle on layout changes. Animating the new content's opacity
/// + transform is simpler, and the fade-in alone reads as a
/// transition because the swap itself is sub-frame fast.
/// </summary>
internal sealed class TransitioningContentControl : ContentControl
{
    /// <summary>
    /// Slide distance in DIPs; matches §8.3 ("slide-up 8 px").
    /// </summary>
    private const double SlideDistance = 8.0;

    /// <summary>
    /// Total motion duration; matches §8.1 Motion.Default (240 ms)
    /// per §8.3 spec. Routed through <see cref="ReduceMotion.For"/>
    /// so users with "Show animations in Windows" disabled get an
    /// instant swap (§8.4).
    /// </summary>
    private static readonly Duration MotionDefault = ReduceMotion.For(240);

    private static readonly QuinticEase DecelerateEase = CreateEase();

    public TransitioningContentControl()
    {
        // Pre-install a TranslateTransform so OnContentChanged can
        // animate Y without re-creating the transform on every swap
        // (which would orphan running animations from prior swaps).
        RenderTransform = new TranslateTransform(0, 0);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        // Skip animation on the very first content set — there's no
        // prior visual to transition from, so a fade-in feels like a
        // jarring loading flash. WPF supplies oldContent=null in that
        // case.
        if (oldContent is null)
        {
            return;
        }

        var fadeIn = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = MotionDefault,
            EasingFunction = DecelerateEase,
        };

        var slideUp = new DoubleAnimation
        {
            From = SlideDistance,
            To = 0.0,
            Duration = MotionDefault,
            EasingFunction = DecelerateEase,
        };

        BeginAnimation(OpacityProperty, fadeIn);
        if (RenderTransform is TranslateTransform translate)
        {
            translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }
    }

    private static QuinticEase CreateEase() => new() { EasingMode = EasingMode.EaseOut };
}
