namespace CapyBro.Services;

/// <summary>
/// H6 (Z4-F2) seam: thin wrapper over <c>RegisterHotKey</c> /
/// <c>UnregisterHotKey</c> so <see cref="HotkeyManager"/> can be unit-
/// tested without booting the Win32 hotkey machinery (which requires a
/// real message-only window and a running message pump).  Production
/// uses <c>Win32HotkeyApi</c> which forwards to <c>NativeMethods</c>.
/// </summary>
public interface IHotkeyApi
{
    bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    bool UnregisterHotKey(IntPtr hwnd, int id);
}
