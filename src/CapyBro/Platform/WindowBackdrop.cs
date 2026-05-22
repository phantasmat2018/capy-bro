using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Serilog;

namespace CapyBro.Platform;

/// <summary>
/// Applies a Win11 system backdrop (Mica / Mica Alt / Acrylic) to a WPF
/// Window via DwmSetWindowAttribute, and silently no-ops on Windows 10
/// or older Windows 11 builds where the API isn't available. Resolves
/// project_design_guide.md §13 Open Question 2 — we go native rather
/// than pull in WPF-UI for ~30 lines of P/Invoke.
///
/// Usage from a Window's OnSourceInitialized:
/// <code>
/// protected override void OnSourceInitialized(EventArgs e)
/// {
///     base.OnSourceInitialized(e);
///     WindowBackdrop.TryApply(this, BackdropType.Mica);
/// }
/// </code>
///
/// Per design-guide §6.2: SettingsWindow uses Mica, ModelsDialog /
/// PromptPickerWindow use Acrylic, ToastWindow uses thin Acrylic.
/// On Win10 the call returns false and the caller's solid background
/// brush (Surface.Canvas) shows through unchanged.
/// </summary>
internal static partial class WindowBackdrop
{
    /// <summary>
    /// First Windows build where DWMWA_SYSTEMBACKDROP_TYPE is documented
    /// (Win11 22H2 / build 22621). Earlier Win11 (21H2 build 22000) had
    /// the undocumented DWMWA_MICA_EFFECT attribute, but we don't
    /// target it — too narrow a window of users to justify maintenance.
    /// </summary>
    internal const int MinSupportedBuild = 22621;

    /// <summary>
    /// True if the current OS supports DWMWA_SYSTEMBACKDROP_TYPE.
    /// </summary>
    public static bool IsSupported => IsSupportedOnBuild(Environment.OSVersion.Version.Build);

    /// <summary>
    /// Applies the requested backdrop. Returns true if the DWM call
    /// succeeded; false on Win10 / unsupported builds, or if the window
    /// has no HWND yet (caller invoked us before OnSourceInitialized).
    /// </summary>
    public static bool TryApply(Window window, BackdropType type)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!IsSupported)
        {
            return false;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            // Window source not yet initialised; can't set DWM attributes.
            return false;
        }

        var value = (int)type;
        var hresult = DwmSetWindowAttribute(
            hwnd,
            DwmwaSystemBackdropType,
            ref value,
            sizeof(int));

        return hresult == 0;
    }

    /// <summary>
    /// Switches the window's native title bar (caption + min/max/close
    /// buttons) between light and dark rendering. Independent of
    /// <see cref="TryApply"/> — works on Win10 build 19041+ even
    /// without Mica support, and on Win11 it pairs with a Mica backdrop
    /// so the chrome doesn't read white-against-dark.
    ///
    /// Returns <c>true</c> if DWM accepted the attribute; <c>false</c>
    /// on null HWND, older Win10 (build &lt; 19041), or DWM failure.
    /// Callers should invoke from OnSourceInitialized — HWND must
    /// already exist.
    /// </summary>
    public static bool TryApplyTitleBarTheme(Window window, bool useDark)
    {
        ArgumentNullException.ThrowIfNull(window);

        var build = Environment.OSVersion.Version.Build;
        var winType = window.GetType().Name;

        // DWMWA_USE_IMMERSIVE_DARK_MODE was 19 on Win10 build
        // 18985-19044, then renumbered to 20 on 19041+ and Win11.
        // Anyone on a maintained Windows version today has 19041+
        // (Win10 2004, May 2020), so we only target attribute 20.
        if (build < 19041)
        {
            Log.Debug("TitleBarTheme[{Win}]: skipped — build {Build} < 19041", winType, build);
            return false;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Log.Debug("TitleBarTheme[{Win}]: skipped — HWND is zero", winType);
            return false;
        }

        var value = useDark ? 1 : 0;

        // Both attribute IDs unconditionally — Win11 22H2 (build 22621)
        // sometimes returns S_OK for attribute 20 without actually
        // applying the visual change; the older 19 sometimes flips
        // when 20 silently no-ops. Belt-and-suspenders, no harm in
        // setting both.
        var hr20 = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        var hr19 = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBeforeBuild19041, ref value, sizeof(int));

        Log.Information(
            "TitleBarTheme[{Win}]: build={Build} hwnd={Hwnd:X} useDark={UseDark} hr20={Hr20:X} hr19={Hr19:X}",
            winType,
            build,
            hwnd.ToInt64(),
            useDark,
            hr20,
            hr19);

        if (hr20 != 0 && hr19 != 0)
        {
            return false;
        }

        // Force the non-client area to repaint. Two complementary nudges:
        //   1. SetWindowPos(SWP_FRAMECHANGED) — re-evaluates the frame.
        //   2. SendMessage(WM_THEMECHANGED) — primes the theme cache so
        //      the new caption colour is picked up on the next paint.
        // Some Win11 22H2 builds need both; one alone is unreliable.
        const uint NoSize = 0x0001;
        const uint NoMove = 0x0002;
        const uint NoZOrder = 0x0004;
        const uint NoActivate = 0x0010;
        const uint FrameChanged = 0x0020;
        var flags = NoSize | NoMove | NoZOrder | NoActivate | FrameChanged;
        var swpResult = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, flags);
        SendMessage(hwnd, WmThemeChanged, IntPtr.Zero, IntPtr.Zero);
        // Win11 22H2+ also accepts direct caption + text colour overrides.
        // On builds where DWMWA_USE_IMMERSIVE_DARK_MODE is silently ignored
        // (some 22621 servicing branches), these stick where the toggle
        // doesn't. We always set them — value DwmWaColorDefault (-1) on
        // light theme means "follow system", which restores the default.
        if (build >= 22000)
        {
            // COLORREF is 0x00BBGGRR — careful with byte order vs CSS hex.
            int captionColor = useDark ? 0x00261B1A : DwmWaColorDefault;
            int textColor = useDark ? 0x00F5F4F4 : DwmWaColorDefault;
            var hrCaption = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
            var hrText = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
            Log.Information(
                "TitleBarTheme[{Win}]: caption=#{Caption:X8} text=#{Text:X8} hrCaption={HrC:X} hrText={HrT:X}",
                winType,
                captionColor,
                textColor,
                hrCaption,
                hrText);
        }

        Log.Information("TitleBarTheme[{Win}]: SetWindowPos={Result} + WM_THEMECHANGED sent", winType, swpResult);

        return true;
    }

    /// <summary>
    /// Convenience wrapper — picks <c>useDark</c> by probing
    /// <c>OnSurfaceStrongBrush</c> from the window's resource scope.
    /// Light theme keeps that brush near-black, dark theme near-white,
    /// so a luminance threshold gives an accurate read without
    /// threading IThemeService through every consumer.
    /// </summary>
    public static bool TryApplyTitleBarThemeFromPalette(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.TryFindResource("OnSurfaceStrongBrush") is not SolidColorBrush probe)
        {
            Log.Warning("TitleBarTheme[{Win}]: OnSurfaceStrongBrush not found in window resources", window.GetType().Name);
            return false;
        }

        var c = probe.Color;
        var luminance = ((c.R * 299) + (c.G * 587) + (c.B * 114)) / 1000;
        Log.Information(
            "TitleBarTheme[{Win}]: probe color=#{R:X2}{G:X2}{B:X2} luminance={Lum} -> useDark={UseDark}",
            window.GetType().Name,
            c.R,
            c.G,
            c.B,
            luminance,
            luminance >= 128);
        return TryApplyTitleBarTheme(window, useDark: luminance >= 128);
    }

    /// <summary>
    /// Internal seam for tests: lets us assert the build-number gate
    /// without invoking the real DWM (which would fail on CI runners
    /// older than 22621).
    /// </summary>
    internal static bool IsSupportedOnBuild(int buildNumber) => buildNumber >= MinSupportedBuild;

    /// <summary>
    /// DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE per dwmapi.h.
    /// </summary>
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>
    /// DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE on Win10
    /// build 19041+ and Win11.
    /// </summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>
    /// Pre-19041 numbering of the same attribute. Some Win10
    /// servicing branches still advertise this one — try-fallback in
    /// <see cref="TryApplyTitleBarTheme"/>.
    /// </summary>
    private const int DwmwaUseImmersiveDarkModeBeforeBuild19041 = 19;

    /// <summary>
    /// DWMWA_CAPTION_COLOR — direct caption background colour
    /// (Win11 22000+). COLORREF format 0x00BBGGRR. Used when
    /// DWMWA_USE_IMMERSIVE_DARK_MODE silently no-ops on a particular
    /// 22H2 servicing branch.
    /// </summary>
    private const int DwmwaCaptionColor = 35;

    /// <summary>
    /// DWMWA_TEXT_COLOR — caption text colour (Win11 22000+),
    /// paired with DWMWA_CAPTION_COLOR.
    /// </summary>
    private const int DwmwaTextColor = 36;

    /// <summary>
    /// Sentinel value passed to colour-attribute setters meaning
    /// "restore the default (system) colour".
    /// </summary>
    private const int DwmWaColorDefault = unchecked((int)0xFFFFFFFE);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int pvAttribute,
        int cbAttribute);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    /// <summary>WM_THEMECHANGED — broadcast by the OS when the user
    /// changes the system theme; sending it manually nudges the
    /// caption to re-read DWM attributes.</summary>
    private const int WmThemeChanged = 0x031A;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam);
}

/// <summary>
/// DWM_SYSTEMBACKDROP_TYPE — the DWM enum that
/// DWMWA_SYSTEMBACKDROP_TYPE accepts. Naming follows the design-guide
/// §6.2 vocabulary (Mica / MicaAlt / Acrylic) rather than the verbose
/// Win32 constants (DWMSBT_MAINWINDOW etc.).
/// </summary>
public enum BackdropType
{
    /// <summary>Auto: DWM picks based on window properties (default).</summary>
    Auto = 0,

    /// <summary>None: explicitly opaque, falls through to Window.Background.</summary>
    None = 1,

    /// <summary>Mica: tinted blur for primary, long-lived windows (SettingsWindow).</summary>
    Mica = 2,

    /// <summary>Acrylic: richer blur for transient surfaces (ModelsDialog, PromptPickerWindow).</summary>
    Acrylic = 3,

    /// <summary>Mica Alt: thinner Mica variant for sub-surfaces (sidebar pane within SettingsWindow).</summary>
    MicaAlt = 4,
}
