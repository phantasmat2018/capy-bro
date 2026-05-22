using System.Windows.Interop;

using CapyBro.Models;
using CapyBro.Platform;

using Microsoft.Extensions.Logging;

namespace CapyBro.Services;

public sealed class HotkeyManager : IHotkeyManager, IDisposable
{
    private const int IdBase = 0xB17F;

    private readonly ILogger<HotkeyManager> _logger;
    private readonly IHotkeyApi _api;
    private readonly IMessageWindow _window;
    private readonly Dictionary<HotkeyKind, (int Id, HotkeyAccelerator Accelerator)> _registered = [];
    private readonly HwndSourceHook _wndProcHook;
    private bool _disposed;

    /// <summary>
    /// DI-friendly constructor.  Wires up the Win32-backed implementations
    /// of the H6 (Z4-F2) seams so callers from <c>App.OnStartup</c> need not
    /// know about them.  Must be invoked on the UI thread — the underlying
    /// <see cref="HwndSource"/> initialises on the creating thread.
    /// </summary>
    public HotkeyManager(ILogger<HotkeyManager> logger)
        : this(new Win32HotkeyApi(), new HwndSourceMessageWindow(), logger)
    {
    }

    /// <summary>
    /// Test-only constructor.  Injects the H6 seams so unit tests can
    /// drive Win32 register/unregister responses and synthesise WM_HOTKEY
    /// messages without a real message-only window.
    /// </summary>
    internal HotkeyManager(IHotkeyApi api, IMessageWindow window, ILogger<HotkeyManager> logger)
    {
        _api = api;
        _window = window;
        _logger = logger;
        _wndProcHook = WndProc;
        _window.AddHook(_wndProcHook);
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public bool TryRegister(HotkeyKind kind, string? accelerator)
    {
        ThrowIfDisposed();

        if (_registered.ContainsKey(kind))
        {
            Unregister(kind);
        }

        var parsed = HotkeyAccelerator.Parse(accelerator);
        if (parsed is null)
        {
            _logger.LogWarning("Could not parse accelerator '{Accelerator}' for {Kind}", accelerator, kind);
            return false;
        }

        // Reject duplicate combinations (e.g. menu hotkey === default hotkey) — keep the first.
        if (_registered.Values.Any(v => v.Accelerator == parsed))
        {
            // Z4-F7 / L8: log at Warning, same as the Win32 RegisterHotKey
            // failure branch below.  Both have the same user-impact (silent
            // feature loss — a configured hotkey doesn't fire), so they
            // should surface to an operator log-level scan identically.
            // Pre-fix this was LogInformation, which a "why is the hotkey
            // not working" triage filtered by LogWarning would have missed.
            _logger.LogWarning(
                "Hotkey {Kind} ({Accelerator}) collides with an already-registered hotkey — skipping",
                kind,
                accelerator);
            return false;
        }

        var id = IdBase + (int)kind;
        var modifiers = parsed.Modifiers | NativeMethods.ModNoRepeat;

        if (!_api.RegisterHotKey(_window.Handle, id, modifiers, parsed.VirtualKey))
        {
            _logger.LogWarning(
                "RegisterHotKey failed for {Kind} ({Accelerator}) — may already be taken by another process",
                kind,
                accelerator);
            return false;
        }

        _registered[kind] = (id, parsed);
        _logger.LogInformation("Registered hotkey {Kind} = {Accelerator}", kind, accelerator);
        return true;
    }

    public void Unregister(HotkeyKind kind)
    {
        ThrowIfDisposed();

        if (!_registered.TryGetValue(kind, out var entry))
        {
            return;
        }

        if (!_api.UnregisterHotKey(_window.Handle, entry.Id))
        {
            _logger.LogWarning("UnregisterHotKey failed for {Kind}", kind);
        }

        _registered.Remove(kind);
    }

    public void UnregisterAll()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var kind in _registered.Keys.ToList())
        {
            Unregister(kind);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterAll();
        _window.RemoveHook(_wndProcHook);
        _window.Dispose();
        _disposed = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotkey)
        {
            return IntPtr.Zero;
        }

        // Z4-F5 / M11: direct iteration rather than
        // `_registered.FirstOrDefault(...).Equals(default(KVP<...>))`.  The
        // previous shape relied on default(KeyValuePair<HotkeyKind,
        // (int, HotkeyAccelerator)>) being distinguishable from any real
        // entry — which holds today only because IdBase = 0xB17F and parse
        // failures short-circuit before insertion.  A future refactor that
        // rebases IdBase to 0 (HotkeyKind.Default = 0 is already a real
        // entry) would silently drop the Default-kind dispatch.  Direct
        // iteration with explicit Id comparison makes the dispatch logic
        // independent of which sentinel value KVP/ValueTuple equality
        // happens to expose.
        var id = wParam.ToInt32();
        foreach (var (kind, entry) in _registered)
        {
            if (entry.Id != id)
            {
                continue;
            }

            handled = true;
            try
            {
                HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(kind));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.LogError(ex, "Hotkey handler threw — swallowed to keep message loop healthy");
            }

            break;
        }

        return IntPtr.Zero;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
