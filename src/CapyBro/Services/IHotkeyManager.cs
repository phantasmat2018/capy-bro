using CapyBro.Models;

namespace CapyBro.Services;

public interface IHotkeyManager
{
    event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    /// <summary>
    /// Registers a hotkey for the given kind. Returns false if the accelerator could not be parsed
    /// or the OS rejected the registration (e.g. duplicate combination already taken).
    /// </summary>
    bool TryRegister(HotkeyKind kind, string? accelerator);

    void Unregister(HotkeyKind kind);

    void UnregisterAll();
}
