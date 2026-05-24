using System.Runtime.InteropServices;

namespace CapyBro.Platform;

/// <summary>
/// Reliably restores a previously-active window to the foreground after
/// our process modal stole it.  The plain <c>SetForegroundWindow</c> call
/// is gated by the OS focus rules (calling thread must own the last input
/// event, no foreground-lock active, target not minimized, etc.) — when
/// any gate fails it returns false and silently no-ops.  We compose it
/// with three other Win32 primitives so the restoration is robust across
/// the common Notepad++ / VS Code / native-edit-control cases where the
/// straight call fails:
/// <list type="number">
/// <item>If the target is minimized, un-minimize via ShowWindowAsync
/// (SW_RESTORE) — SetForegroundWindow no-ops on iconic windows.</item>
/// <item>Attach our thread's input queue to the target's thread.  This
/// makes the OS treat the two threads as sharing input state, which
/// satisfies the "last input event" check inside SetForegroundWindow.</item>
/// <item>Call BringWindowToTop to raise z-order, then SetForegroundWindow.
/// Crucially, when we know the FOCUSED CHILD of the target window (e.g.
/// the Scintilla edit control inside Notepad++) — captured via
/// <see cref="CaptureForegroundFocus"/> before the modal stole focus — we
/// call SetFocus on THAT child rather than the top-level frame.  Without
/// this, the subsequent SendInput Ctrl+V lands on the top-level WindowProc
/// (which doesn't translate to WM_PASTE on a native frame) instead of the
/// edit surface (which does).</item>
/// <item>Detach the input queues in <c>finally</c> so we don't leak the
/// shared-input state and freeze our UI thread on target stalls.</item>
/// </list>
/// Idempotent: returns <c>false</c> on <see cref="IntPtr.Zero"/> or
/// invalid HWNDs; otherwise true even if one of the calls inside no-ops.
/// </summary>
internal static class ForegroundRestorer
{
    /// <summary>
    /// Captures the focused-child HWND of whichever app currently owns
    /// the foreground — the SCINTILLA-inside-NOTEPAD++ case, not just
    /// the top-level Notepad++ frame.  Returns
    /// <c>(topLevel, focusedChild)</c>; the second can be
    /// <see cref="IntPtr.Zero"/> if the target's thread has no focused
    /// window (rare — usually means the app is busy or has no input
    /// surface).  Designed to be called BEFORE the modal steals
    /// foreground; the values are then passed to
    /// <see cref="RestoreToForeground"/> on close.
    /// </summary>
    public static (IntPtr TopLevel, IntPtr FocusedChild) CaptureForegroundFocus()
    {
        var topLevel = NativeMethods.GetForegroundWindow();
        if (topLevel == IntPtr.Zero)
        {
            return (IntPtr.Zero, IntPtr.Zero);
        }

        var targetThreadId = NativeMethods.GetWindowThreadProcessId(topLevel, out _);
        if (targetThreadId == 0)
        {
            return (topLevel, IntPtr.Zero);
        }

        var info = new NativeMethods.GUITHREADINFO
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };

        if (NativeMethods.GetGUIThreadInfo(targetThreadId, ref info))
        {
            return (topLevel, info.HwndFocus);
        }

        return (topLevel, IntPtr.Zero);
    }

    public static bool RestoreToForeground(IntPtr topLevel, IntPtr focusedChild)
    {
        if (topLevel == IntPtr.Zero)
        {
            return false;
        }

        var targetThreadId = NativeMethods.GetWindowThreadProcessId(topLevel, out _);
        if (targetThreadId == 0)
        {
            // Window is dead or belongs to a dying process — best-effort
            // SetForegroundWindow without the attach.
            return NativeMethods.SetForegroundWindow(topLevel);
        }

        if (NativeMethods.IsIconic(topLevel))
        {
            NativeMethods.ShowWindowAsync(topLevel, NativeMethods.SwRestore);
        }

        var ourThreadId = NativeMethods.GetCurrentThreadId();

        // Self-targeting: no need to attach (and AttachThreadInput on
        // same thread is undefined per MSDN).  Just call SetForeground.
        if (ourThreadId == targetThreadId)
        {
            return NativeMethods.SetForegroundWindow(topLevel);
        }

        var attached = NativeMethods.AttachThreadInput(ourThreadId, targetThreadId, true);
        try
        {
            NativeMethods.BringWindowToTop(topLevel);
            var fgOk = NativeMethods.SetForegroundWindow(topLevel);

            // Focus the originally-focused child (e.g. Scintilla edit
            // control inside Notepad++).  When that's null/zero — the
            // target's thread has no focused HWND at the time we captured —
            // fall back to SetFocus on the top-level frame; the OS will
            // re-route to the previously-focused child via WM_ACTIVATE
            // for most apps, but the explicit per-child SetFocus is
            // markedly more reliable for native edit controls.
            var focusTarget = focusedChild != IntPtr.Zero ? focusedChild : topLevel;
            NativeMethods.SetFocus(focusTarget);

            return fgOk;
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(ourThreadId, targetThreadId, false);
            }
        }
    }
}
