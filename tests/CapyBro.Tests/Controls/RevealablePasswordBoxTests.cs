using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

using CapyBro.Controls;

using FluentAssertions;

using Xunit;

namespace CapyBro.Tests.Controls;

/// <summary>
/// Tests for <see cref="RevealablePasswordBox"/>: the §7.4 eye-toggle
/// surface. We verify the contract that the API-key consumer relies
/// on:
///  - Two-way Password DP round-trips through both inputs.
///  - Toggling reveal/conceal swaps Visibility on the masked vs
///    plaintext inputs.
///  - Setting Password externally pushes into both inputs (so the
///    value survives a reveal flip mid-edit).
///  - Editing either input pushes back into Password.
/// </summary>
public class RevealablePasswordBoxTests
{
    [Fact]
    public void Initial_StateMaskedInputVisible_PlainHidden_TogglesUnchecked()
        => RunOnSta(() =>
        {
            var control = new RevealablePasswordBox();
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var masked = (PasswordBox)control.FindName("MaskedInput")!;
            var plain = (TextBox)control.FindName("PlainInput")!;
            var toggle = (ToggleButton)control.FindName("RevealToggle")!;

            masked.Visibility.Should().Be(Visibility.Visible, "masked input is the default rest state");
            plain.Visibility.Should().Be(Visibility.Collapsed);
            toggle.IsChecked.Should().BeFalse();
        });

    [Fact]
    public void TogglingChecked_SwapsVisibility_PlainShown_MaskedHidden()
        => RunOnSta(() =>
        {
            var control = new RevealablePasswordBox();
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var masked = (PasswordBox)control.FindName("MaskedInput")!;
            var plain = (TextBox)control.FindName("PlainInput")!;
            var toggle = (ToggleButton)control.FindName("RevealToggle")!;

            toggle.IsChecked = true;

            masked.Visibility.Should().Be(Visibility.Collapsed, "reveal=on hides the masked input");
            plain.Visibility.Should().Be(Visibility.Visible, "reveal=on shows the plaintext input");
        });

    [Fact]
    public void SettingPassword_ProgrammaticallyPushesIntoBothInputs()
        => RunOnSta(() =>
        {
            var control = new RevealablePasswordBox();
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            control.Password = "sk-ant-1234";

            var masked = (PasswordBox)control.FindName("MaskedInput")!;
            var plain = (TextBox)control.FindName("PlainInput")!;

            masked.Password.Should().Be("sk-ant-1234", "Password DP must propagate to the masked input so it survives a future reveal flip");
            plain.Text.Should().Be("sk-ant-1234", "Password DP must propagate to the plaintext input — flipping reveal mid-edit must show the same value");
        });

    [Fact]
    public void EditingMaskedInput_PushesBackIntoPasswordDp()
        => RunOnSta(() =>
        {
            var control = new RevealablePasswordBox();
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var masked = (PasswordBox)control.FindName("MaskedInput")!;
            masked.Password = "typed-while-masked";
            control.Password.Should().Be(
                "typed-while-masked",
                "PasswordBox.PasswordChanged fires synchronously inside .Password setter and must sync into the Password DP");
        });

    [Fact]
    public void EditingPlainInput_PushesBackIntoPasswordDp()
        => RunOnSta(() =>
        {
            var control = new RevealablePasswordBox();
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var plain = (TextBox)control.FindName("PlainInput")!;
            plain.Text = "typed-while-revealed";

            control.Password.Should().Be("typed-while-revealed", "TextBox.TextChanged must sync into the Password DP");
        });

    [Fact]
    public void Password_Binds_TwoWay_ByDefault()
        => RunOnSta(() =>
        {
            // Two-way is the canonical MVVM mode for password fields —
            // catches any future regression where someone removes the
            // BindsTwoWayByDefault flag from the DP metadata.
            var metadata = RevealablePasswordBox.PasswordProperty
                .GetMetadata(typeof(RevealablePasswordBox))
                as FrameworkPropertyMetadata;

            metadata.Should().NotBeNull();
            metadata!.BindsTwoWayByDefault.Should().BeTrue(
                "Password DP must default to two-way binding so callers don't have to spell out Mode=TwoWay");
        });

    [Fact]
    public void EditingDoesNotPingPong_BetweenPasswordDpAndInputs()
        => RunOnSta(() =>
        {
            // Sets Password externally, observe that we don't loop
            // through OnPasswordBoxChanged -> SyncFromInput -> set
            // Password -> OnPasswordPropertyChanged -> set inputs ad
            // infinitum. We verify by counting Password DP changes
            // through a binding.
            var control = new RevealablePasswordBox();
            var source = new TestSource();
            BindingOperations.SetBinding(control, RevealablePasswordBox.PasswordProperty, new Binding(nameof(TestSource.Value))
            {
                Source = source,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
            });

            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var masked = (PasswordBox)control.FindName("MaskedInput")!;
            masked.Password = "abc";

            source.Value.Should().Be("abc");
            source.SetCount.Should().BeLessOrEqualTo(2, "the round-trip must terminate — at most one set on the way down, optionally one normalisation set");
        });

    private sealed class TestSource : System.ComponentModel.INotifyPropertyChanged
    {
        private string _value = string.Empty;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public int SetCount { get; private set; }

        public string Value
        {
            get => _value;
            set
            {
                _value = value;
                SetCount++;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Value)));
            }
        }
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
