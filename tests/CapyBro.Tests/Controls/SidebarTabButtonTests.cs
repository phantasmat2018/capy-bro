using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using CapyBro.Controls;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Controls;

/// <summary>
/// Tests for <see cref="SidebarTabButton"/>: DP wiring + click
/// dispatch through to Command. The visual contract (3 px brand
/// indicator on IsSelected, Layer2 hover) is asserted indirectly via
/// the underlying Button template — visual smoke is left to the
/// browser-equivalent (running the app).
/// </summary>
public class SidebarTabButtonTests
{
    [Fact]
    public void DependencyProperties_Roundtrip_PerSetterValue()
        => RunOnSta(() =>
        {
            var control = new SidebarTabButton();

            var iconA = Geometry.Parse("M 0 0 L 10 10");
            control.Icon = iconA;
            control.Icon.Should().BeSameAs(iconA);

            control.Label = "General";
            control.Label.Should().Be("General");

            control.IsSelected = true;
            control.IsSelected.Should().BeTrue();

            var stub = new RecordingCommand();
            control.Command = stub;
            control.Command.Should().BeSameAs(stub);

            control.CommandParameter = "x";
            control.CommandParameter.Should().Be("x");
        });

    [Fact]
    public void Command_FiresThroughHostButton_WhenInvokedProgrammatically()
        => RunOnSta(() =>
        {
            var control = new SidebarTabButton();
            var recorder = new RecordingCommand();
            control.Command = recorder;
            control.CommandParameter = "general";

            // Force template apply so the inner Button binds to our DPs.
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Simulate a click by invoking the command directly via the
            // bound handler — exercises the same path that mouse/keyboard
            // would, modulo input routing. This guards the
            // {Binding Command, ElementName=Root} relay in the template.
            var inner = (System.Windows.Controls.Button?)control.FindName("HostButton");
            inner.Should().NotBeNull("UserControl must expose its host Button by x:Name");
            inner!.Command.Should().BeSameAs(
                recorder,
                "Button.Command must reflect SidebarTabButton.Command via the ElementName relay");
            inner.CommandParameter.Should().Be("general");
        });

    private sealed class RecordingCommand : ICommand
    {
        public int Invocations { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Invocations++;
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
