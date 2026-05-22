using System.Windows;
using System.Windows.Media;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Controls;

/// <summary>
/// Tests for TransitioningContentControl: ensure the §8.3 fade+slide
/// motion fires only on actual content swaps (not the initial set),
/// and that the transform seam is set up so the animation has
/// somewhere to start from.
///
/// We can't observe an animation's intermediate frames in a
/// unit test (WPF dispatcher isn't pumped), but we can assert that:
///  - the control exposes a TranslateTransform render-transform
///  - Opacity is 1.0 at rest (so the first frame post-animation is
///    visible)
///  - the y-translation starts at 0 (animation will animate FROM 8 TO 0)
/// </summary>
public class TransitioningContentControlTests
{
    [Fact]
    public void RenderTransform_IsTranslateTransform_FromConstruction()
        => RunOnSta(() =>
        {
            // Reflect into the internal sealed type — kept internal
            // because consumers should use it via XAML, not new it
            // outside the assembly.
            var type = typeof(App).Assembly
                .GetType("CapyBro.Controls.TransitioningContentControl");
            type.Should().NotBeNull("internal TransitioningContentControl must exist");

            var instance = (FrameworkElement)Activator.CreateInstance(type!)!;
            instance.RenderTransform.Should().BeOfType<TranslateTransform>(
                "TransitioningContentControl pre-installs a TranslateTransform so OnContentChanged can animate Y without re-creating it on each swap");
        });

    [Fact]
    public void OpacityAndYTranslation_DefaultRest_AreOneAndZero()
        => RunOnSta(() =>
        {
            var type = typeof(App).Assembly
                .GetType("CapyBro.Controls.TransitioningContentControl");
            var instance = (FrameworkElement)Activator.CreateInstance(type!)!;

            instance.Opacity.Should().Be(1.0, "rest state must be fully visible — animation will dip to 0 only during a swap");

            var transform = (TranslateTransform)instance.RenderTransform;
            transform.Y.Should().Be(0.0, "rest state must sit at Y=0 — animation will jump to 8 then ease back to 0");
        });

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
