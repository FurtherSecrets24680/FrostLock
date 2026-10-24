using FrostLock.Interop;

namespace FrostLock.Services;

/// <summary>A parsed global-hotkey / hook combo such as "Ctrl+Alt+U".</summary>
public readonly record struct HotkeyBinding(bool Ctrl, bool Alt, bool Shift, bool Win, int VirtualKey, string Display)
{
    public bool HasModifier => Ctrl || Alt || Shift || Win;

    /// <summary>Modifier mask for RegisterHotKey.</summary>
    public uint Win32Modifiers =>
        (Ctrl ? Native.MOD_CONTROL : 0u) |
        (Alt ? Native.MOD_ALT : 0u) |
        (Shift ? Native.MOD_SHIFT : 0u) |
        (Win ? Native.MOD_WIN : 0u);

    public bool Matches(bool ctrl, bool alt, bool shift, bool win, int vk)
        => vk == VirtualKey && ctrl == Ctrl && alt == Alt && shift == Shift && win == Win;

    public static HotkeyBinding None => new(false, false, false, false, 0, "None");
    public bool IsValid => VirtualKey != 0;
}

public static class Hotkeys
{
    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B,
        ["SPACE"] = 0x20, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["RETURN"] = 0x0D,
        ["BACKSPACE"] = 0x08, ["DELETE"] = 0x2E, ["DEL"] = 0x2E, ["INSERT"] = 0x2D,
        ["HOME"] = 0x24, ["END"] = 0x23, ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22,
        ["UP"] = 0x26, ["DOWN"] = 0x28, ["LEFT"] = 0x25, ["RIGHT"] = 0x27,
        ["PRINTSCREEN"] = 0x2C, ["PAUSE"] = 0x13, ["CAPSLOCK"] = 0x14,
        ["NUMLOCK"] = 0x90, ["SCROLLLOCK"] = 0x91,
        ["MINUS"] = 0xBD, ["OEM_MINUS"] = 0xBD, ["PLUS"] = 0xBB, ["EQUAL"] = 0xBB,
        ["COMMA"] = 0xBC, ["PERIOD"] = 0xBE, ["SLASH"] = 0xBF,
        ["SEMICOLON"] = 0xBA, ["QUOTE"] = 0xDE, ["BACKSLASH"] = 0xDC,
        ["TILDE"] = 0xC0, ["BACKTICK"] = 0xC0,
        ["LBRACKET"] = 0xDB, ["RBRACKET"] = 0xDD,
    };

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = HotkeyBinding.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        bool ctrl = false, alt = false, shift = false, win = false;
        int vk = 0;
        string? keyName = null;

        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    ctrl = true; continue;
                case "ALT":
                    alt = true; continue;
                case "SHIFT":
                    shift = true; continue;
                case "WIN":
                case "WINDOWS":
                case "META":
                case "SUPER":
                    win = true; continue;
            }

            keyName = part; // last non-modifier token is the key
        }

        if (keyName is null) return false;

        if (Named.TryGetValue(keyName, out var named))
        {
            vk = named;
        }
        else if (keyName.Length >= 2 && (keyName[0] is 'F' or 'f') && int.TryParse(keyName[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = 0x70 + (fn - 1);
        }
        else if (keyName.Length == 1)
        {
            var c = char.ToUpperInvariant(keyName[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                vk = c;
        }

        if (vk == 0) return false;

        binding = new HotkeyBinding(ctrl, alt, shift, win, vk, Format(ctrl, alt, shift, win, KeyLabel(vk)));
        return true;
    }

    /// <summary>Builds a binding from a recorded key press (used by the settings recorder).</summary>
    public static HotkeyBinding Create(bool ctrl, bool alt, bool shift, bool win, int virtualKey)
        => new(ctrl, alt, shift, win, virtualKey, Format(ctrl, alt, shift, win, KeyLabel(virtualKey)));

    private static string KeyLabel(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        foreach (var kv in Named)
            if (kv.Value == vk && kv.Key.Length <= 8 && kv.Key is not ("ESCAPE" or "RETURN" or "DEL" or "OEM_MINUS" or "BACKTICK" or "EQUAL"))
                return kv.Key;
        return "0x" + vk.ToString("X2");
    }

    private static string Format(bool ctrl, bool alt, bool shift, bool win, string key)
    {
        var parts = new List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }
}