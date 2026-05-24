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

    /// <summary>
    /// Returns the ID of the thread that created the specified window.
    /// Used together with <see cref="AttachThreadInput"/> to inherit
    /// foreground privilege from the target window's thread for a
    /// <see cref="SetForegroundWindow"/> call that would otherwise be
    /// silently rejected by the OS focus rules.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Returns the ID of the calling thread.
    /// </summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    /// <summary>
    /// Attaches or detaches the input processing mechanism of one thread
    /// to that of another.  When attached, the two threads share input
    /// state (keyboard focus, foreground privilege) — used to bypass
    /// the "you didn't receive the last input event" restriction on
    /// <see cref="SetForegroundWindow"/> when restoring the user's
    /// target app after a modal close.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>
    /// Brings the specified window to the top of the Z order without
    /// activating or giving it the focus.  Used in tandem with
    /// <see cref="SetForegroundWindow"/> for reliable foreground
    /// restoration — BringWindowToTop raises the z-order even when
    /// SetForegroundWindow's focus-stealing protection kicks in.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Sets the keyboard focus to the specified window.  Within the
    /// AttachThreadInput sandwich this routes the next keystroke to
    /// the previously-focused child of the target window (e.g. the
    /// Scintilla edit control inside Notepad++), which is what
    /// SendInput's synthesised Ctrl+V needs to land on the right
    /// edit surface.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial IntPtr SetFocus(IntPtr hWnd);

    /// <summary>
    /// Determines whether the specified window is minimized (iconic).
    /// Used to defensively un-minimize the target app via
    /// <see cref="ShowWindowAsync"/> with SW_RESTORE before
    /// SetForegroundWindow, otherwise the foreground call no-ops on
    /// minimized targets.
    /// </summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Asynchronous window-state setter — same surface as ShowWindow but
    /// safe to call across threads/processes.  Used with SW_RESTORE to
    /// un-minimize a target app before we try to bring it to foreground.
    /// Name matches the Win32 export literally; "Async" here refers to
    /// the OS-side post-message semantics, NOT an awaitable C# task,
    /// hence the VSTHRD200 suppression.
    /// </summary>
#pragma warning disable VSTHRD200 // "Async" suffix mirrors the Win32 export name; not a Task-returning method.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
#pragma warning restore VSTHRD200

    public const int SwRestore = 9;

    /// <summary>
    /// Retrieves information about the active window or a specified GUI
    /// thread.  We use it to harvest <see cref="GUITHREADINFO.hwndFocus"/> —
    /// the actual focused CHILD HWND inside the target app (e.g. the
    /// Scintilla edit control inside Notepad++, not its top-level frame).
    /// SetFocus on the focused-child after we restore foreground makes
    /// the subsequent SendInput Ctrl+V land on the edit surface; SetFocus
    /// on the top-level frame instead leaves Ctrl+V echoing into the
    /// non-input frame WindowProc.
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint CbSize;
        public uint Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public RECT RcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
