using CapyBro.Models;

namespace CapyBro.Services;

public sealed class HotkeyPressedEventArgs : EventArgs
{
    public HotkeyPressedEventArgs(HotkeyKind kind)
    {
        Kind = kind;
    }

    public HotkeyKind Kind { get; }
}
