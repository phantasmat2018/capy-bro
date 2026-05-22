using System.Windows.Interop;

using CapyBro.Services;

namespace CapyBro.Platform;

/// <summary>
/// Production <see cref="IMessageWindow"/> implementation — wraps a
/// message-only <see cref="HwndSource"/> at <c>HWND_MESSAGE</c> so it
/// receives WM_HOTKEY without ever being visible on the desktop.
/// </summary>
internal sealed class HwndSourceMessageWindow : IMessageWindow
{
    private readonly HwndSource _source;
    private bool _disposed;

    public HwndSourceMessageWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("CapyBroHotkeyHost")
        {
            Width = 0,
            Height = 0,
            PositionX = -10000,
            PositionY = -10000,
            ParentWindow = -3, // HWND_MESSAGE
        });
    }

    public IntPtr Handle => _source.Handle;

    public void AddHook(HwndSourceHook hook) => _source.AddHook(hook);

    public void RemoveHook(HwndSourceHook hook) => _source.RemoveHook(hook);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _source.Dispose();
        _disposed = true;
    }
}
