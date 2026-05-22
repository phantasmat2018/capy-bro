using CapyBro.Services;

namespace CapyBro.Platform;

/// <summary>
/// Production <see cref="IHotkeyApi"/> implementation — forwards
/// straight to <c>User32.RegisterHotKey</c> / <c>UnregisterHotKey</c>
/// through <see cref="NativeMethods"/>.
/// </summary>
internal sealed class Win32HotkeyApi : IHotkeyApi
{
    public bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey) =>
        NativeMethods.RegisterHotKey(hwnd, id, modifiers, virtualKey);

    public bool UnregisterHotKey(IntPtr hwnd, int id) =>
        NativeMethods.UnregisterHotKey(hwnd, id);
}
