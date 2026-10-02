using SnapFloat.Core.Input;

namespace SnapFloat.Core.Settings;

public static class SettingsLimits
{
    public const double MinDuration = 2, MaxDuration = 30;
    public const double MinThumbWidth = 200, MaxThumbWidth = 320;
    public const double MinCornerRadius = 0, MaxCornerRadius = 20;
    public const int MinRetentionDays = 1, MaxRetentionDays = 365;
    public const int MinJpegQuality = 50, MaxJpegQuality = 100;
}

public static class SettingsValidator
{
    /// <summary>Shortcuts owned by Windows that must never be registered.</summary>
    private static readonly HashSet<Hotkey> Reserved =
    [
        new(HotkeyModifiers.Win | HotkeyModifiers.Shift, 'S'),
        new(HotkeyModifiers.Win, 'L'),
        new(HotkeyModifiers.Win, 'D'),
        new(HotkeyModifiers.Win, 'E'),
        new(HotkeyModifiers.Win, 'R'),
        new(HotkeyModifiers.Win, 'V'),
        new(HotkeyModifiers.Win, 0x2C), // Win+PrintScreen
        new(HotkeyModifiers.Alt, 0x09), // Alt+Tab
        new(HotkeyModifiers.Alt, 0x73), // Alt+F4
        new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x2E), // Ctrl+Alt+Del
        new(HotkeyModifiers.Ctrl, 'C'),
        new(HotkeyModifiers.Ctrl, 'V'),
        new(HotkeyModifiers.Ctrl, 'X'),
        new(HotkeyModifiers.Ctrl, 'Z'),
        new(HotkeyModifiers.Ctrl, 'A'),
        new(HotkeyModifiers.Ctrl, 'S'),
    ];

    /// <summary>
    /// Clamps numeric values into range and repairs anything unparseable so the app can always start.
    /// Returns human-readable notes about what was repaired (for logging).
    /// </summary>
    public static IReadOnlyList<string> Normalize(AppSettings s)
    {
        var notes = new List<string>();
        var defaults = new AppSettings();

        s.PreviewDurationSeconds = Clamp(s.PreviewDurationSeconds, SettingsLimits.MinDuration, SettingsLimits.MaxDuration, defaults.PreviewDurationSeconds, nameof(s.PreviewDurationSeconds), notes);
        s.ThumbnailWidth = Clamp(s.ThumbnailWidth, SettingsLimits.MinThumbWidth, SettingsLimits.MaxThumbWidth, defaults.ThumbnailWidth, nameof(s.ThumbnailWidth), notes);
        s.CornerRadius = Clamp(s.CornerRadius, SettingsLimits.MinCornerRadius, SettingsLimits.MaxCornerRadius, defaults.CornerRadius, nameof(s.CornerRadius), notes);
        if (s.RetentionDays != AppSettings.KeepForever)
            s.RetentionDays = s.RetentionDays < 0
                ? AppSettings.KeepForever
                : (int)Clamp(s.RetentionDays, SettingsLimits.MinRetentionDays, SettingsLimits.MaxRetentionDays, defaults.RetentionDays, nameof(s.RetentionDays), notes);
        s.JpegQuality = (int)Clamp(s.JpegQuality, SettingsLimits.MinJpegQuality, SettingsLimits.MaxJpegQuality, defaults.JpegQuality, nameof(s.JpegQuality), notes);

        if (!Enum.IsDefined(s.Theme)) { s.Theme = defaults.Theme; notes.Add("Theme reset"); }
        if (!Enum.IsDefined(s.PreviewCorner)) { s.PreviewCorner = defaults.PreviewCorner; notes.Add("PreviewCorner reset"); }
        if (!Enum.IsDefined(s.ImageFormat)) { s.ImageFormat = defaults.ImageFormat; notes.Add("ImageFormat reset"); }
        if (!Enum.IsDefined(s.ClipboardWatchMode)) { s.ClipboardWatchMode = defaults.ClipboardWatchMode; notes.Add("ClipboardWatchMode reset"); }

        s.RegionHotkey = NormalizeHotkey(s.RegionHotkey, defaults.RegionHotkey, nameof(s.RegionHotkey), notes);
        s.FullScreenHotkey = NormalizeHotkey(s.FullScreenHotkey, defaults.FullScreenHotkey, nameof(s.FullScreenHotkey), notes);
        s.WindowHotkey = NormalizeHotkey(s.WindowHotkey, defaults.WindowHotkey, nameof(s.WindowHotkey), notes);

        // Duplicate shortcuts: keep the first, disable later ones.
        var seen = new HashSet<Hotkey>();
        s.RegionHotkey = Dedupe(s.RegionHotkey, seen, nameof(s.RegionHotkey), notes);
        s.FullScreenHotkey = Dedupe(s.FullScreenHotkey, seen, nameof(s.FullScreenHotkey), notes);
        s.WindowHotkey = Dedupe(s.WindowHotkey, seen, nameof(s.WindowHotkey), notes);

        if (!string.IsNullOrWhiteSpace(s.SaveDirectory) && ValidateDirectoryPath(s.SaveDirectory) is { } err)
        {
            notes.Add($"SaveDirectory reset ({err})");
            s.SaveDirectory = null;
        }
        if (string.IsNullOrWhiteSpace(s.SaveDirectory)) s.SaveDirectory = null;

        s.Version = AppSettings.CurrentVersion;
        return notes;
    }

    /// <summary>Validates a single shortcut on its own. Returns an error message, or null if acceptable.</summary>
    public static string? ValidateHotkey(Hotkey hotkey)
    {
        if (hotkey.IsEmpty) return null;
        if (!Hotkey.IsSupportedKey(hotkey.VirtualKey)) return "This key can't be used for a shortcut.";
        var isFunctionKeyAlone = hotkey.VirtualKey is >= 0x7C and <= 0x87; // F13–F24
        if (hotkey.Modifiers == HotkeyModifiers.None && !isFunctionKeyAlone)
            return "Add at least one modifier (Ctrl, Shift, Alt or Win).";
        if (hotkey.Modifiers == HotkeyModifiers.Shift && !isFunctionKeyAlone)
            return "Shift alone would block typing. Add Ctrl, Alt or Win.";
        if (Reserved.Contains(hotkey))
            return $"{hotkey} is reserved by Windows or common apps.";
        return null;
    }

    /// <summary>Returns a non-blocking advisory for a shortcut, e.g. AltGr clashes.</summary>
    public static string? HotkeyWarning(Hotkey hotkey)
    {
        if (hotkey.IsEmpty) return null;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl) && hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
            return "Ctrl+Alt is the same as AltGr on many keyboard layouts and may block typing characters.";
        return null;
    }

    /// <summary>Validates the three capture shortcuts together. Returns errors keyed by setting name.</summary>
    public static Dictionary<string, string> ValidateHotkeys(string region, string fullScreen, string window)
    {
        var errors = new Dictionary<string, string>();
        var parsed = new List<(string Name, Hotkey Key)>();
        foreach (var (name, text) in new[] { (nameof(AppSettings.RegionHotkey), region), (nameof(AppSettings.FullScreenHotkey), fullScreen), (nameof(AppSettings.WindowHotkey), window) })
        {
            if (!Hotkey.TryParse(text, out var hk)) { errors[name] = "Unrecognised shortcut."; continue; }
            if (ValidateHotkey(hk) is { } e) { errors[name] = e; continue; }
            parsed.Add((name, hk));
        }
        foreach (var group in parsed.Where(p => !p.Key.IsEmpty).GroupBy(p => p.Key).Where(g => g.Count() > 1))
            foreach (var item in group.Skip(1))
                errors[item.Name] = $"{item.Key} is already used by another SnapFloat shortcut.";
        return errors;
    }

    /// <summary>Returns an error message if the path cannot be used as a screenshot folder, otherwise null.</summary>
    public static string? ValidateDirectoryPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "Path is empty.";
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "Path contains invalid characters.";
            if (!Path.IsPathFullyQualified(path)) return "Use an absolute path such as C:\\Screenshots.";
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.Equals(full.TrimEnd('\\', '/'), root?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return "Choose a folder, not the root of a drive.";
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "Path is not valid.";
        }
    }

    private static double Clamp(double value, double min, double max, double fallback, string name, List<string> notes)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) { notes.Add($"{name} reset"); return fallback; }
        if (value < min) { notes.Add($"{name} clamped"); return min; }
        if (value > max) { notes.Add($"{name} clamped"); return max; }
        return value;
    }

    private static string NormalizeHotkey(string? text, string fallback, string name, List<string> notes)
    {
        if (!Hotkey.TryParse(text, out var hk)) { notes.Add($"{name} unparseable, reset"); return fallback; }
        if (ValidateHotkey(hk) is not null) { notes.Add($"{name} invalid, reset"); return fallback; }
        return hk.ToString();
    }

    private static string Dedupe(string text, HashSet<Hotkey> seen, string name, List<string> notes)
    {
        var hk = Hotkey.Parse(text);
        if (hk.IsEmpty) return text;
        if (seen.Add(hk)) return text;
        notes.Add($"{name} duplicated another shortcut, disabled");
        return Hotkey.None.ToString();
    }
}
