using CapyBro.Platform;

namespace CapyBro.Services;

public sealed record HotkeyAccelerator(uint Modifiers, uint VirtualKey)
{
    /// <summary>
    /// Parses an accelerator like "Ctrl+Shift+E" into Win32 modifier flags + virtual-key code.
    /// Returns null for empty input or unrecognized keys.
    /// </summary>
    public static HotkeyAccelerator? Parse(string? accelerator)
    {
        if (string.IsNullOrWhiteSpace(accelerator))
        {
            return null;
        }

        uint modifiers = 0;
        uint? virtualKey = null;

        var parts = accelerator.Split(
            '+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var upper = part.ToUpperInvariant();
            switch (upper)
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                case "META":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    var key = ParseKey(upper);
                    if (key is null)
                    {
                        return null;
                    }

                    if (virtualKey is not null)
                    {
                        // accelerator already has a non-modifier key — second key is invalid
                        return null;
                    }

                    virtualKey = key;
                    break;
            }
        }

        return virtualKey is null ? null : new HotkeyAccelerator(modifiers, virtualKey.Value);
    }

    /// <summary>
    /// Re-formats an accelerator string into canonical PascalCase ("ctrl+shift+e" → "Ctrl+Shift+E").
    /// Returns the input unchanged if it can't be parsed.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var parts = raw.Split(
            '+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var output = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var upper = part.ToUpperInvariant();
            output.Add(upper switch
            {
                "CTRL" or "CONTROL" => "Ctrl",
                "ALT" => "Alt",
                "SHIFT" => "Shift",
                "WIN" or "WINDOWS" or "META" => "Win",
                _ => CanonicalKey(upper),
            });
        }

        return string.Join("+", output);
    }

    private static string CanonicalKey(string upper)
    {
        if (upper.Length >= 2 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), out _))
        {
            return upper;
        }

        if (upper.Length == 1)
        {
            return upper;
        }

        return char.ToUpperInvariant(upper[0]) + upper[1..].ToLowerInvariant();
    }

    private static uint? ParseKey(string upper)
    {
        if (upper.Length == 1)
        {
            var c = upper[0];
            if (c is >= 'A' and <= 'Z')
            {
                return c;
            }

            if (c is >= '0' and <= '9')
            {
                return c;
            }

            // Common OEM punctuation keys.  Layout note: these VK codes are
            // assigned by US-QWERTY semantics — the actual character typed on
            // a different layout (e.g. UA, RU) might differ, but
            // RegisterHotKey resolves by VK irrespective of the user's
            // layout, so "Ctrl+/" registered here still fires on the same
            // physical key whatever locale the user types in afterwards.
            // Shifted variants (`?`, `:`, `~` …) intentionally map to the
            // same VK as their unshifted twin — Win32's RegisterHotKey
            // takes one VK and the modifier mask separately, so users who
            // want the shifted glyph just add Shift to the accelerator.
            uint? oem = c switch
            {
                ';' or ':' => 0xBAu,    // VK_OEM_1
                '=' or '+' => 0xBBu,    // VK_OEM_PLUS
                ',' or '<' => 0xBCu,    // VK_OEM_COMMA
                '-' or '_' => 0xBDu,    // VK_OEM_MINUS
                '.' or '>' => 0xBEu,    // VK_OEM_PERIOD
                '/' or '?' => 0xBFu,    // VK_OEM_2
                '`' or '~' => 0xC0u,    // VK_OEM_3
                '[' or '{' => 0xDBu,    // VK_OEM_4
                '\\' or '|' => 0xDCu,   // VK_OEM_5
                ']' or '}' => 0xDDu,    // VK_OEM_6
                '\'' or '"' => 0xDEu,   // VK_OEM_7
                _ => null,
            };
            if (oem is not null)
            {
                return oem;
            }
        }

        if (upper.Length >= 2 && upper[0] == 'F'
            && int.TryParse(upper.AsSpan(1), out var fnum)
            && fnum is >= 1 and <= 24)
        {
            // VK_F1 = 0x70, VK_F2 = 0x71, …, VK_F24 = 0x87
            return 0x70u + (uint)(fnum - 1);
        }

        return upper switch
        {
            "SPACE" => 0x20u,
            "TAB" => 0x09u,
            "ENTER" or "RETURN" => 0x0Du,
            "ESC" or "ESCAPE" => 0x1Bu,
            "BACKSPACE" or "BACK" => 0x08u,
            "DELETE" or "DEL" => 0x2Eu,
            "INSERT" or "INS" => 0x2Du,
            "HOME" => 0x24u,
            "END" => 0x23u,
            "PAGEUP" or "PGUP" => 0x21u,
            "PAGEDOWN" or "PGDN" => 0x22u,
            "LEFT" => 0x25u,
            "UP" => 0x26u,
            "RIGHT" => 0x27u,
            "DOWN" => 0x28u,
            _ => null,
        };
    }
}
