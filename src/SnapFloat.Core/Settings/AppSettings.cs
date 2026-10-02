namespace SnapFloat.Core.Settings;

public enum ThemePreference { System, Light, Dark }

public enum PreviewCorner { BottomRight, BottomLeft, TopRight, TopLeft }

public enum ScreenshotFormat { Png, Jpeg }

/// <summary>Which clipboard images should produce a preview.</summary>
public enum ClipboardWatchMode
{
    /// <summary>Only images placed on the clipboard by Snipping Tool / Win+Shift+S / Print Screen.</summary>
    ScreenshotTools,
    /// <summary>Every bitmap that lands on the clipboard (except our own copies).</summary>
    AnyImage,
    /// <summary>Do not watch the clipboard; only SnapFloat's own shortcuts produce previews.</summary>
    Off,
}

/// <summary>
/// Persisted user settings. Plain mutable POCO so System.Text.Json can round-trip it;
/// always run <see cref="SettingsValidator.Normalize"/> after loading.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public bool FirstRunCompleted { get; set; }

    // General
    public bool LaunchAtStartup { get; set; }
    public bool ShowPreviews { get; set; } = true;
    public bool AutoClosePreviews { get; set; } = true;
    public double PreviewDurationSeconds { get; set; } = 5;
    public PreviewCorner PreviewCorner { get; set; } = PreviewCorner.BottomRight;
    public bool StartMinimized { get; set; } = true;
    public ClipboardWatchMode ClipboardWatchMode { get; set; } = ClipboardWatchMode.ScreenshotTools;

    // Capture
    public string RegionHotkey { get; set; } = "Ctrl+Shift+4";
    public string FullScreenHotkey { get; set; } = "Ctrl+Shift+3";
    public string WindowHotkey { get; set; } = "Ctrl+Shift+5";
    public ScreenshotFormat ImageFormat { get; set; } = ScreenshotFormat.Png;
    public int JpegQuality { get; set; } = 92;
    /// <summary>Null or empty means the Windows Screenshots folder (Pictures\Screenshots).</summary>
    public string? SaveDirectory { get; set; }

    // Appearance
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public double ThumbnailWidth { get; set; } = 260;
    public double CornerRadius { get; set; } = 10;
    public bool AnimationsEnabled { get; set; } = true;

    // Storage
    /// <summary>Days to keep SnapFloat's screenshots. <see cref="KeepForever"/> (the default) never deletes.</summary>
    public int RetentionDays { get; set; } = KeepForever;

    public const int KeepForever = 0;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
