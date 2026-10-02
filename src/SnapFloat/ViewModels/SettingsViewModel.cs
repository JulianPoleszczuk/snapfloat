using System.ComponentModel;
using System.Runtime.CompilerServices;
using SnapFloat.Core.Input;
using SnapFloat.Core.Settings;
using SnapFloat.Core.Storage;
using SnapFloat.Services;

namespace SnapFloat.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What the settings screen needs from the running app.</summary>
internal interface IAppController
{
    /// <summary>Re-registers shortcuts from current settings; returns a user-facing error per failing setting name.</summary>
    IReadOnlyDictionary<string, string> ApplyHotkeys();
    ScreenshotStore Store { get; }
    void OpenScreenshotFolder();
    void OpenLogFolder();
    void ShowLatestPreview();
}

/// <summary>Binds the settings window. Every change is validated, applied live and persisted immediately.</summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly SettingsService _settings;
    private readonly IAppController _app;
    private string? _regionError, _fullScreenError, _windowError, _folderError, _startupError;
    private string? _regionDraft, _fullScreenDraft, _windowDraft;
    private string _usage = "";

    public SettingsViewModel(SettingsService settings, IAppController app)
    {
        _settings = settings;
        _app = app;
        _settings.Changed += _ => OnPropertyChanged(string.Empty);
        RefreshUsage();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private AppSettings S => _settings.Current;

    // ------------------------------------------------------------------ options

    public IReadOnlyList<Option<PreviewCorner>> CornerOptions { get; } =
    [
        new(PreviewCorner.BottomRight, "Bottom right"),
        new(PreviewCorner.BottomLeft, "Bottom left"),
        new(PreviewCorner.TopRight, "Top right"),
        new(PreviewCorner.TopLeft, "Top left"),
    ];

    public IReadOnlyList<Option<ClipboardWatchMode>> WatchOptions { get; } =
    [
        new(ClipboardWatchMode.ScreenshotTools, "Windows screenshots"),
        new(ClipboardWatchMode.AnyImage, "Any copied image"),
        new(ClipboardWatchMode.Off, "SnapFloat shortcuts only"),
    ];

    public IReadOnlyList<Option<ScreenshotFormat>> FormatOptions { get; } =
    [
        new(ScreenshotFormat.Png, "PNG (lossless)"),
        new(ScreenshotFormat.Jpeg, "JPEG (smaller files)"),
    ];

    public IReadOnlyList<Option<int>> RetentionOptions { get; } =
    [
        new(AppSettings.KeepForever, "Forever"), new(1, "1 day"), new(3, "3 days"), new(7, "1 week"), new(14, "2 weeks"), new(30, "30 days"), new(90, "90 days"), new(365, "1 year"),
    ];

    // ------------------------------------------------------------------ general

    public bool LaunchAtStartup
    {
        get => StartupService.IsEnabled();
        set
        {
            StartupError = StartupService.SetEnabled(value) ? null : "Windows didn't allow changing the startup entry. See the log for details.";
            _settings.Update(s => s.LaunchAtStartup = value);
        }
    }

    public string? StartupError { get => _startupError; private set => Set(ref _startupError, value); }

    public bool StartMinimized { get => S.StartMinimized; set => _settings.Update(s => s.StartMinimized = value); }
    public bool ShowPreviews { get => S.ShowPreviews; set => _settings.Update(s => s.ShowPreviews = value); }
    public bool AutoClosePreviews { get => S.AutoClosePreviews; set => _settings.Update(s => s.AutoClosePreviews = value); }

    public double PreviewDuration
    {
        get => S.PreviewDurationSeconds;
        set => _settings.Update(s => s.PreviewDurationSeconds = Math.Round(value));
    }

    public string PreviewDurationText => $"{S.PreviewDurationSeconds:0} s";

    public PreviewCorner PreviewCorner { get => S.PreviewCorner; set => _settings.Update(s => s.PreviewCorner = value); }
    public ClipboardWatchMode WatchMode { get => S.ClipboardWatchMode; set => _settings.Update(s => s.ClipboardWatchMode = value); }

    // ------------------------------------------------------------------ capture

    public string RegionHotkey { get => _regionDraft ?? S.RegionHotkey; set => SetHotkey(nameof(AppSettings.RegionHotkey), value); }
    public string FullScreenHotkey { get => _fullScreenDraft ?? S.FullScreenHotkey; set => SetHotkey(nameof(AppSettings.FullScreenHotkey), value); }
    public string WindowHotkey { get => _windowDraft ?? S.WindowHotkey; set => SetHotkey(nameof(AppSettings.WindowHotkey), value); }

    public string? RegionError { get => _regionError; private set => Set(ref _regionError, value); }
    public string? FullScreenError { get => _fullScreenError; private set => Set(ref _fullScreenError, value); }
    public string? WindowError { get => _windowError; private set => Set(ref _windowError, value); }

    private void SetHotkey(string name, string value)
    {
        var region = name == nameof(AppSettings.RegionHotkey) ? value : S.RegionHotkey;
        var full = name == nameof(AppSettings.FullScreenHotkey) ? value : S.FullScreenHotkey;
        var window = name == nameof(AppSettings.WindowHotkey) ? value : S.WindowHotkey;

        var errors = SettingsValidator.ValidateHotkeys(region, full, window);
        if (errors.TryGetValue(name, out var error))
        {
            // Keep the rejected keys visible next to the message so the user sees what was refused.
            SetDraft(name, value);
            SetError(name, error);
            return;
        }

        SetDraft(name, null);
        SetError(name, null);
        _settings.Update(s =>
        {
            s.RegionHotkey = region;
            s.FullScreenHotkey = full;
            s.WindowHotkey = window;
        });
        foreach (var (failedName, message) in _app.ApplyHotkeys()) SetError(failedName, message);
        var warning = Hotkey.TryParse(value, out var hk) ? SettingsValidator.HotkeyWarning(hk) : null;
        if (warning is not null && GetError(name) is null) SetError(name, warning);
    }

    private void SetDraft(string name, string? value)
    {
        switch (name)
        {
            case nameof(AppSettings.RegionHotkey): _regionDraft = value; OnPropertyChanged(nameof(RegionHotkey)); break;
            case nameof(AppSettings.FullScreenHotkey): _fullScreenDraft = value; OnPropertyChanged(nameof(FullScreenHotkey)); break;
            case nameof(AppSettings.WindowHotkey): _windowDraft = value; OnPropertyChanged(nameof(WindowHotkey)); break;
        }
    }

    private void SetError(string name, string? message)
    {
        switch (name)
        {
            case nameof(AppSettings.RegionHotkey): RegionError = message; break;
            case nameof(AppSettings.FullScreenHotkey): FullScreenError = message; break;
            case nameof(AppSettings.WindowHotkey): WindowError = message; break;
        }
    }

    private string? GetError(string name) => name switch
    {
        nameof(AppSettings.RegionHotkey) => RegionError,
        nameof(AppSettings.FullScreenHotkey) => FullScreenError,
        _ => WindowError,
    };

    /// <summary>Shows registration failures detected at startup.</summary>
    public void ShowHotkeyFailures(IReadOnlyDictionary<string, string> failures)
    {
        foreach (var (name, message) in failures) SetError(name, message);
    }

    public ScreenshotFormat ImageFormat
    {
        get => S.ImageFormat;
        set { _settings.Update(s => s.ImageFormat = value); OnPropertyChanged(nameof(IsJpeg)); }
    }

    public bool IsJpeg => S.ImageFormat == ScreenshotFormat.Jpeg;

    public double JpegQuality { get => S.JpegQuality; set => _settings.Update(s => s.JpegQuality = (int)Math.Round(value)); }
    public string JpegQualityText => $"{S.JpegQuality}%";

    public string ScreenshotFolder => _settings.ScreenshotDirectory;
    public bool UsesDefaultFolder => _settings.UsesDefaultDirectory;
    public string? FolderError { get => _folderError; private set => Set(ref _folderError, value); }

    public void SetFolder(string? path)
    {
        if (path is not null)
        {
            if (SettingsValidator.ValidateDirectoryPath(path) is { } error) { FolderError = error; return; }
            try
            {
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, $".snapfloat-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                FolderError = "SnapFloat can't write to this folder. Choose another one.";
                return;
            }
        }
        FolderError = null;
        _settings.Update(s => s.SaveDirectory = path);
        RefreshUsage();
    }

    // ------------------------------------------------------------------ appearance

    public bool ThemeSystem { get => S.Theme == ThemePreference.System; set { if (value) _settings.Update(s => s.Theme = ThemePreference.System); } }
    public bool ThemeLight { get => S.Theme == ThemePreference.Light; set { if (value) _settings.Update(s => s.Theme = ThemePreference.Light); } }
    public bool ThemeDark { get => S.Theme == ThemePreference.Dark; set { if (value) _settings.Update(s => s.Theme = ThemePreference.Dark); } }

    public double ThumbnailWidth { get => S.ThumbnailWidth; set => _settings.Update(s => s.ThumbnailWidth = Math.Round(value / 10) * 10); }
    public string ThumbnailWidthText => $"{S.ThumbnailWidth:0} px";

    public double CornerRadius { get => S.CornerRadius; set => _settings.Update(s => s.CornerRadius = Math.Round(value)); }
    public string CornerRadiusText => $"{S.CornerRadius:0} px";

    public bool AnimationsEnabled { get => S.AnimationsEnabled; set => _settings.Update(s => s.AnimationsEnabled = value); }
    public bool SystemAnimationsOff => !System.Windows.SystemParameters.ClientAreaAnimation;

    public double MinDuration => SettingsLimits.MinDuration;
    public double MaxDuration => SettingsLimits.MaxDuration;
    public double MinWidth => SettingsLimits.MinThumbWidth;
    public double MaxWidth => SettingsLimits.MaxThumbWidth;
    public double MaxRadius => SettingsLimits.MaxCornerRadius;

    // ------------------------------------------------------------------ storage

    public int RetentionDays
    {
        get => S.RetentionDays;
        set { _settings.Update(s => s.RetentionDays = value); ApplyRetentionInBackground(); }
    }

    public string UsageText { get => _usage; private set => Set(ref _usage, value); }

    public void RefreshUsage()
    {
        _ = Task.Run(() => _app.Store.GetUsage()).ContinueWith(t =>
        {
            var u = t.Result;
            UsageText = u.Count == 0 ? "No screenshots stored" : $"{u.Count} screenshot{(u.Count == 1 ? "" : "s")} · {ByteSize.Format(u.Bytes)}";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void ClearScreenshots()
    {
        _app.Store.ClearAll();
        RefreshUsage();
    }

    private void ApplyRetentionInBackground() =>
        _ = Task.Run(() => _app.Store.ApplyRetention()).ContinueWith(_ => RefreshUsage(), TaskScheduler.FromCurrentSynchronizationContext());

    // ------------------------------------------------------------------ about

    public string Version => $"Version {AppPaths.Version}";
    public string? RepositoryUrl => AppPaths.RepositoryUrl;
    public bool HasRepository => !string.IsNullOrEmpty(AppPaths.RepositoryUrl);

    // ------------------------------------------------------------------ plumbing

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
