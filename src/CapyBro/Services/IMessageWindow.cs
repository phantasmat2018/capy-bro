using System.Windows.Interop;

namespace CapyBro.Services;

/// <summary>
/// H6 (Z4-F2) seam: thin abstraction over the message-only
/// <see cref="HwndSource"/> that <see cref="HotkeyManager"/> uses to
/// receive WM_HOTKEY messages.  Lets tests inject a fake that captures
/// the WndProc hook and fires synthetic messages, without requiring a
/// WPF dispatcher / window thread.
/// </summary>
public interface IMessageWindow : IDisposable
{
    IntPtr Handle { get; }

    void AddHook(HwndSourceHook hook);

    void RemoveHook(HwndSourceHook hook);
}
