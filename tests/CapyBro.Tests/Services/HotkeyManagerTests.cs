using System.Windows.Interop;

using CapyBro.Models;
using CapyBro.Services;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CapyBro.Tests.Services;

/// <summary>
/// H6 (Z4-F2) regression suite — HotkeyManager had zero unit tests
/// before the seam refactor.  Tests drive the SUT through fake
/// <see cref="IHotkeyApi"/> and <see cref="IMessageWindow"/>
/// implementations so the Win32 message pump never runs.
/// </summary>
public class HotkeyManagerTests
{
    private const int WmHotkey = 0x0312;

    [Fact]
    public void TryRegister_AcceleratorUnparseable_ReturnsFalseAndDoesNotCallApi()
    {
        var harness = new Harness();

        var ok = harness.Sut.TryRegister(HotkeyKind.Default, "Garbage+");

        ok.Should().BeFalse("an unparseable accelerator must fail fast with no side effects");
        harness.Api.RegisterCalls.Should().BeEmpty(
            "production used to call into User32 regardless — the SUT must short-circuit BEFORE Win32");
    }

    [Fact]
    public void TryRegister_HappyPath_StoresEntryAndPassesParsedModifiersToApi()
    {
        var harness = new Harness();
        harness.Api.NextRegisterResult = true;

        var ok = harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E");

        ok.Should().BeTrue();
        harness.Api.RegisterCalls.Should().ContainSingle();
        var call = harness.Api.RegisterCalls[0];
        (call.Modifiers & 0x4000u).Should().NotBe(
            0u,
            "MOD_NOREPEAT is OR'd into the parsed modifiers — without it, hold-down generates a hotkey storm");
    }

    [Fact]
    public void TryRegister_IntraAppCollision_RejectsDuplicateAcceleratorWithoutSecondApiCall()
    {
        var harness = new Harness();
        harness.Api.NextRegisterResult = true;
        harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E").Should().BeTrue();

        var ok = harness.Sut.TryRegister(HotkeyKind.Menu, "Ctrl+Shift+E");

        ok.Should().BeFalse(
            "two distinct HotkeyKinds cannot share the same accelerator — the SUT must guard against the hand-edited-JSON case");
        harness.Api.RegisterCalls.Should().ContainSingle(
            "the duplicate guard fires BEFORE we touch Win32, so RegisterHotKey is only called once");
    }

    [Fact]
    public void TryRegister_Win32RegisterReturnsFalse_NotStoredAndReturnsFalse()
    {
        // Simulates "Spotify / OS already owns that chord" — Win32
        // returns false and the SUT must surface that to the caller.
        var harness = new Harness();
        harness.Api.NextRegisterResult = false;

        var ok = harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E");

        ok.Should().BeFalse();
        harness.Api.RegisterCalls.Should().ContainSingle();
        // A failed Win32 register must NOT leave a stale entry in the
        // dictionary — Unregister(Default) would otherwise call
        // UnregisterHotKey for an id that was never registered.
        harness.Sut.Unregister(HotkeyKind.Default);
        harness.Api.UnregisterCalls.Should().BeEmpty(
            "no entry stored => nothing to unregister; calling Unregister must be a silent no-op");
    }

    [Fact]
    public void TryRegister_SameKindTwice_UnregistersFirstThenRegistersSecond()
    {
        var harness = new Harness();
        harness.Api.NextRegisterResult = true;
        harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E").Should().BeTrue();

        harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Alt+I").Should().BeTrue();

        harness.Api.UnregisterCalls.Should().ContainSingle(
            "the SUT unregisters the previous accelerator before storing the new one — otherwise the OS would hold both");
        harness.Api.RegisterCalls.Should().HaveCount(2);
    }

    [Fact]
    public void UnregisterAll_AfterTwoRegistrations_RemovesBoth()
    {
        var harness = new Harness();
        harness.Api.NextRegisterResult = true;
        harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E");
        harness.Sut.TryRegister(HotkeyKind.Menu, "Ctrl+Shift+Q");

        harness.Sut.UnregisterAll();

        harness.Api.UnregisterCalls.Should().HaveCount(2);
    }

    [Fact]
    public void WndProc_KnownHotkeyId_FiresHotkeyPressedEvent()
    {
        var harness = new Harness();
        harness.Api.NextRegisterResult = true;
        harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E").Should().BeTrue();

        HotkeyKind? observed = null;
        harness.Sut.HotkeyPressed += (_, e) => observed = e.Kind;

        // IdBase + (int)HotkeyKind.Default — must match the constant in
        // HotkeyManager.IdBase.  The seam exposes the registered id via
        // FakeHotkeyApi.RegisterCalls so we don't have to hard-code it.
        var registeredId = harness.Api.RegisterCalls[0].Id;
        var handled = false;
        harness.Window.FireHook(IntPtr.Zero, WmHotkey, new IntPtr(registeredId), IntPtr.Zero, ref handled);

        handled.Should().BeTrue("the SUT marks WM_HOTKEY messages as handled so the host shell does not also process them");
        observed.Should().Be(HotkeyKind.Default);
    }

    [Fact]
    public void WndProc_UnknownId_DoesNotFireEvent()
    {
        // A stray hotkey id that we never registered (e.g. a stale OS
        // entry from a previous run or a race during Unregister) must
        // be ignored, not crash and not fire the wrong kind.
        var harness = new Harness();
        harness.Api.NextRegisterResult = true;
        harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E").Should().BeTrue();

        var fired = false;
        harness.Sut.HotkeyPressed += (_, _) => fired = true;

        var handled = false;
        harness.Window.FireHook(IntPtr.Zero, WmHotkey, new IntPtr(0x9999), IntPtr.Zero, ref handled);

        fired.Should().BeFalse();
        handled.Should().BeFalse("unknown ids fall through unmodified so the host pump can decide");
    }

    // Z4-F5 / M11 regression: WndProc dispatch must look up the kind by
    // *Id equality alone*, independent of which KeyValuePair / ValueTuple
    // sentinel happens to satisfy `default()`.  Pre-fix the handler used
    // `FirstOrDefault(...).Equals(default(KVP<HotkeyKind,(int,HA)>))` —
    // works today only because IdBase = 0xB17F means no real entry has
    // Id=0 AND parse failures short-circuit (so no entry has a null HA).
    // A refactor that rebased IdBase to 0 would silently drop Default
    // dispatches.  This test pins the post-fix invariant: each registered
    // kind dispatches uniquely on its own id, even when all three live
    // simultaneously.
    [Fact]
    public void WndProc_AllThreeKindsRegistered_DispatchesEachToTheCorrectKind()
    {
        var harness = new Harness();
        harness.Api.NextRegisterResult = true;
        harness.Sut.TryRegister(HotkeyKind.Default, "Ctrl+Shift+E").Should().BeTrue();
        harness.Sut.TryRegister(HotkeyKind.Menu, "Ctrl+Shift+Q").Should().BeTrue();
        harness.Sut.TryRegister(HotkeyKind.Undo, "Ctrl+Shift+Z").Should().BeTrue();

        var observed = new List<HotkeyKind>();
        harness.Sut.HotkeyPressed += (_, e) => observed.Add(e.Kind);

        var defaultId = harness.Api.RegisterCalls[0].Id;
        var menuId = harness.Api.RegisterCalls[1].Id;
        var undoId = harness.Api.RegisterCalls[2].Id;

        // Fire in non-registration order to exercise the iteration order
        // explicitly — a regression that returned the first dictionary
        // entry regardless of Id would silently mis-route.
        var handled = false;
        harness.Window.FireHook(IntPtr.Zero, WmHotkey, new IntPtr(undoId), IntPtr.Zero, ref handled);
        harness.Window.FireHook(IntPtr.Zero, WmHotkey, new IntPtr(defaultId), IntPtr.Zero, ref handled);
        harness.Window.FireHook(IntPtr.Zero, WmHotkey, new IntPtr(menuId), IntPtr.Zero, ref handled);

        observed.Should().Equal(HotkeyKind.Undo, HotkeyKind.Default, HotkeyKind.Menu);
    }

    [Fact]
    public void Dispose_RemovesHookAndDisposesWindow()
    {
        var harness = new Harness();

        harness.Sut.Dispose();

        harness.Window.WasDisposed.Should().BeTrue("the SUT owns the message window via the seam — must dispose it");
        harness.Window.HookCount.Should().Be(0, "the WndProc hook must be removed during Dispose");
    }

    private sealed class Harness
    {
        public Harness()
        {
            Api = new FakeHotkeyApi();
            Window = new FakeMessageWindow();
            Sut = new HotkeyManager(Api, Window, NullLogger<HotkeyManager>.Instance);
        }

        public HotkeyManager Sut { get; }

        public FakeHotkeyApi Api { get; }

        public FakeMessageWindow Window { get; }
    }

    private sealed class FakeHotkeyApi : IHotkeyApi
    {
        public bool NextRegisterResult { get; set; } = true;

        public List<RegisterCall> RegisterCalls { get; } = [];

        public List<UnregisterCall> UnregisterCalls { get; } = [];

        public bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey)
        {
            RegisterCalls.Add(new RegisterCall(hwnd, id, modifiers, virtualKey));
            return NextRegisterResult;
        }

        public bool UnregisterHotKey(IntPtr hwnd, int id)
        {
            UnregisterCalls.Add(new UnregisterCall(hwnd, id));
            return true;
        }

        public sealed record RegisterCall(IntPtr Hwnd, int Id, uint Modifiers, uint VirtualKey);

        public sealed record UnregisterCall(IntPtr Hwnd, int Id);
    }

    private sealed class FakeMessageWindow : IMessageWindow
    {
        private readonly List<HwndSourceHook> _hooks = [];

        public IntPtr Handle => new(0x1234);

        public int HookCount => _hooks.Count;

        public bool WasDisposed { get; private set; }

        public void AddHook(HwndSourceHook hook) => _hooks.Add(hook);

        public void RemoveHook(HwndSourceHook hook) => _hooks.Remove(hook);

        public void Dispose() => WasDisposed = true;

        public void FireHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            foreach (var hook in _hooks.ToList())
            {
                hook(hwnd, msg, wParam, lParam, ref handled);
            }
        }
    }
}
