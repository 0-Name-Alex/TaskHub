using System.Globalization;

namespace TaskHub.Core.Hotkeys;

/// <summary>Values match the Win32 MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// A parsed global hotkey such as <c>Ctrl+Alt+T</c>. <see cref="Key"/> is a normalised key
/// name (A–Z, 0–9, F1–F24, Space, Enter, …); the platform layer maps it to a virtual key.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    private static readonly Dictionary<string, HotkeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyModifiers.Ctrl,
        ["control"] = HotkeyModifiers.Ctrl,
        ["alt"] = HotkeyModifiers.Alt,
        ["shift"] = HotkeyModifiers.Shift,
        ["win"] = HotkeyModifiers.Win,
        ["windows"] = HotkeyModifiers.Win,
        ["meta"] = HotkeyModifiers.Win,
    };

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = "Space",
        ["spacebar"] = "Space",
        ["enter"] = "Enter",
        ["return"] = "Enter",
        ["tab"] = "Tab",
        ["esc"] = "Escape",
        ["escape"] = "Escape",
        ["backspace"] = "Back",
        ["delete"] = "Delete",
        ["del"] = "Delete",
        ["insert"] = "Insert",
        ["ins"] = "Insert",
        ["home"] = "Home",
        ["end"] = "End",
        ["pageup"] = "PageUp",
        ["pgup"] = "PageUp",
        ["pagedown"] = "PageDown",
        ["pgdn"] = "PageDown",
        ["up"] = "Up",
        ["down"] = "Down",
        ["left"] = "Left",
        ["right"] = "Right",
        ["printscreen"] = "PrintScreen",
        ["prtsc"] = "PrintScreen",
        ["pause"] = "Pause",
    };

    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out var g, out var error) ? g : throw new FormatException(error);

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Hotkey is empty.";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0))
        {
            error = $"'{text}' has an empty part. Use a format like Ctrl+Alt+T.";
            return false;
        }

        var mods = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            if (!ModifierNames.TryGetValue(part, out var m))
            {
                error = $"'{part}' is not a modifier. Use Ctrl, Alt, Shift or Win.";
                return false;
            }
            if ((mods & m) != 0)
            {
                error = $"'{text}' repeats the {m} modifier.";
                return false;
            }
            mods |= m;
        }

        if (!TryNormaliseKey(parts[^1], out var key))
        {
            error = $"'{parts[^1]}' is not a supported key.";
            return false;
        }

        var isFunctionKey = key.Length > 1 && key[0] == 'F' && char.IsDigit(key[1]);
        if (mods == HotkeyModifiers.None && !isFunctionKey)
        {
            error = $"'{text}' needs at least one modifier (Ctrl, Alt, Shift or Win), except for F-keys.";
            return false;
        }

        gesture = new HotkeyGesture(mods, key);
        error = null;
        return true;
    }

    private static bool TryNormaliseKey(string raw, out string key)
    {
        key = "";
        if (raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0]))
        {
            key = raw.ToUpperInvariant();
            return true;
        }
        if (KeyAliases.TryGetValue(raw, out var alias))
        {
            key = alias;
            return true;
        }
        if (raw.Length is 2 or 3 && (raw[0] is 'F' or 'f')
            && int.TryParse(raw.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
        {
            key = "F" + n.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        return false;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}
