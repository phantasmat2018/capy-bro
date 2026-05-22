using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace CapyBro.Platform;

#pragma warning disable CA1707 // Identifiers should not contain underscores — Win32 constant naming convention
internal static partial class NativeMethods
{
    public const int WmHotkey = 0x0312;

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    // Virtual-key codes
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12; // Alt
    public const ushort VkInsert = 0x2D;
    public const ushort VkLWin = 0x5B;
    public const ushort VkRWin = 0x5C;
    public const ushort VkV = 0x56;
    public const ushort VkLeft = 0x25;

    // SendInput constants
    public const uint InputKeyboard = 1;
    public const uint KeyEventfKeyUp = 0x0002;
    public const uint KeyEventfExtendedKey = 0x0001;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Brings the specified window to the foreground and activates it.
    /// Subject to Windows' restrictions (caller must have received the
    /// last input event, own a foreground window, etc.). For our use it
    /// works because we're handling a hotkey our process registered.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Returns the HWND of the window the user is currently working with
    /// (the foreground window).  Used by <c>PromptPickerWindow</c> to poll
    /// whether the popover is still the active surface — when it isn't,
    /// dismiss as cancelled.  See the comment in
    /// <c>OnForegroundPollerTick</c> for the rationale (cross-process
    /// clicks aren't visible to WPF's input system; polling Win32 is the
    /// only reliable catch-all).
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint Type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(0)]
        public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }
}
#pragma warning restore CA1707
