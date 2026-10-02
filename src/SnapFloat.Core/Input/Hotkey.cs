using System.Text;

namespace SnapFloat.Core.Input;

/// <summary>Modifier flags. Values match the Win32 MOD_* constants used by RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global shortcut: modifiers plus a Win32 virtual-key code.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly Hotkey None = new(HotkeyModifiers.None, 0);

    public bool IsEmpty => VirtualKey == 0;

    private static readonly Dictionary<int, string> KeyNames = BuildKeyNames();
    private static readonly Dictionary<string, int> KeysByName = BuildKeysByName();

    private static Dictionary<int, string> BuildKeyNames()
    {
        var map = new Dictionary<int, string>();
        for (var c = 'A'; c <= 'Z'; c++) map[c] = c.ToString();
        for (var c = '0'; c <= '9'; c++) map[c] = c.ToString();
        for (var i = 1; i <= 24; i++) map[0x70 + i - 1] = "F" + i;
        for (var i = 0; i <= 9; i++) map[0x60 + i] = "Num" + i;
        map[0x2C] = "PrintScreen";
        map[0x20] = "Space";
        map[0x0D] = "Enter";
        map[0x09] = "Tab";
        map[0x2D] = "Insert";
        map[0x2E] = "Delete";
        map[0x24] = "Home";
        map[0x23] = "End";
        map[0x21] = "PageUp";
        map[0x22] = "PageDown";
        map[0x25] = "Left";
        map[0x26] = "Up";
        map[0x27] = "Right";
        map[0x28] = "Down";
        map[0x13] = "Pause";
        map[0xC0] = "`";
        map[0xBD] = "-";
        map[0xBB] = "=";
        map[0xDB] = "[";
        map[0xDD] = "]";
        map[0xBA] = ";";
        map[0xDE] = "'";
        map[0xBC] = ",";
        map[0xBE] = ".";
        map[0xBF] = "/";
        map[0xDC] = "\\";
        return map;
    }

    private static Dictionary<string, int> BuildKeysByName()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, name) in KeyNames) map[name] = vk;
        map["PrtSc"] = 0x2C;
        map["PrtScn"] = 0x2C;
        map["Del"] = 0x2E;
        map["Ins"] = 0x2D;
        map["Return"] = 0x0D;
        return map;
    }

    public static bool IsSupportedKey(int virtualKey) => KeyNames.ContainsKey(virtualKey);

    public static string KeyName(int virtualKey) =>
        KeyNames.TryGetValue(virtualKey, out var n) ? n : $"0x{virtualKey:X2}";

    public static bool IsModifierKey(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or // Shift, Ctrl, Alt
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or // L/R variants
        0x5B or 0x5C; // LWin, RWin

    /// <summary>Parses strings such as "Ctrl+Shift+4". Empty / "None" yields <see cref="None"/>.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
            return true;

        var mods = HotkeyModifiers.None;
        int? key = null;
        // Split on '+' but allow the '+'-less punctuation keys; '=' covers the plus key.
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= HotkeyModifiers.Ctrl; continue;
                case "shift": mods |= HotkeyModifiers.Shift; continue;
                case "alt": mods |= HotkeyModifiers.Alt; continue;
                case "win": case "windows": case "meta": mods |= HotkeyModifiers.Win; continue;
            }
            if (key is not null) return false; // two non-modifier keys
            if (!KeysByName.TryGetValue(part, out var vk)) return false;
            key = vk;
        }
        if (key is null) return false;
        hotkey = new Hotkey(mods, key.Value);
        return true;
    }

    public static Hotkey Parse(string? text) =>
        TryParse(text, out var h) ? h : throw new FormatException($"Invalid shortcut: '{text}'");

    public override string ToString()
    {
        if (IsEmpty) return "None";
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) sb.Append("Win+");
        sb.Append(KeyName(VirtualKey));
        return sb.ToString();
    }
}
