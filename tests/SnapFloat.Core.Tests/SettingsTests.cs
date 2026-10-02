using SnapFloat.Core.Settings;

namespace SnapFloat.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SnapFloatTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var s = new SettingsStore(FilePath).Load();
        Assert.Equal(5, s.PreviewDurationSeconds);
        Assert.Equal(PreviewCorner.BottomRight, s.PreviewCorner);
        Assert.True(s.ShowPreviews);
        Assert.False(s.FirstRunCompleted);
        Assert.Equal(AppSettings.KeepForever, s.RetentionDays);
        Assert.Null(s.SaveDirectory);
    }

    [Fact]
    public void Settings_round_trip_through_disk()
    {
        var store = new SettingsStore(FilePath);
        var s = store.Load();
        s.PreviewDurationSeconds = 9;
        s.Theme = ThemePreference.Dark;
        s.PreviewCorner = PreviewCorner.TopLeft;
        s.RegionHotkey = "Ctrl+Alt+R";
        s.ImageFormat = ScreenshotFormat.Jpeg;
        s.RetentionDays = 30;
        s.FirstRunCompleted = true;
        s.SaveDirectory = Path.Combine(_dir, "shots");
        store.Save(s);

        var loaded = new SettingsStore(FilePath).Load();
        Assert.Equal(9, loaded.PreviewDurationSeconds);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(PreviewCorner.TopLeft, loaded.PreviewCorner);
        Assert.Equal("Ctrl+Alt+R", loaded.RegionHotkey);
        Assert.Equal(ScreenshotFormat.Jpeg, loaded.ImageFormat);
        Assert.Equal(30, loaded.RetentionDays);
        Assert.True(loaded.FirstRunCompleted);
        Assert.Equal(Path.Combine(_dir, "shots"), loaded.SaveDirectory);
    }

    [Fact]
    public void Enums_are_stored_as_readable_names()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { Theme = ThemePreference.Light });
        Assert.Contains("\"Theme\": \"Light\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_defaults_used()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ this is not json");
        var s = new SettingsStore(FilePath).Load(out var notes);
        Assert.Equal(new AppSettings().ThumbnailWidth, s.ThumbnailWidth);
        Assert.NotEmpty(notes);
        Assert.True(File.Exists(FilePath + ".corrupt"));
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """
            { "PreviewDurationSeconds": 500, "ThumbnailWidth": 10, "CornerRadius": -4, "RetentionDays": 9000, "JpegQuality": 7 }
            """);
        var s = new SettingsStore(FilePath).Load();
        Assert.Equal(SettingsLimits.MaxDuration, s.PreviewDurationSeconds);
        Assert.Equal(SettingsLimits.MinThumbWidth, s.ThumbnailWidth);
        Assert.Equal(0, s.CornerRadius);
        Assert.Equal(SettingsLimits.MaxRetentionDays, s.RetentionDays);
        Assert.Equal(SettingsLimits.MinJpegQuality, s.JpegQuality);
    }

    [Fact]
    public void Invalid_or_reserved_hotkeys_are_reset_and_duplicates_disabled()
    {
        var s = new AppSettings { RegionHotkey = "Win+Shift+S", FullScreenHotkey = "Ctrl+Shift+5", WindowHotkey = "Ctrl+Shift+5" };
        SettingsValidator.Normalize(s);
        Assert.Equal(new AppSettings().RegionHotkey, s.RegionHotkey);
        Assert.Equal("Ctrl+Shift+5", s.FullScreenHotkey);
        Assert.Equal("None", s.WindowHotkey);
    }

    [Fact]
    public void Unknown_enum_values_fall_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "PreviewCorner": 42 }""");
        Assert.Equal(PreviewCorner.BottomRight, new SettingsStore(FilePath).Load().PreviewCorner);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    [InlineData(14, 14)]
    public void Retention_zero_means_forever_and_negatives_become_forever(int stored, int expected)
    {
        var s = new AppSettings { RetentionDays = stored };
        SettingsValidator.Normalize(s);
        Assert.Equal(expected, s.RetentionDays);
    }

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("C:\\")]
    [InlineData("")]
    public void Rejects_unusable_folders(string path) => Assert.NotNull(SettingsValidator.ValidateDirectoryPath(path));

    [Fact]
    public void Invalid_save_directory_is_reset_to_default()
    {
        var s = new AppSettings { SaveDirectory = "not\\absolute" };
        SettingsValidator.Normalize(s);
        Assert.Null(s.SaveDirectory);
    }

    [Fact]
    public void Version_1_settings_switch_to_keep_forever()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "Version": 1, "AutoDeleteOldScreenshots": true, "RetentionDays": 7, "FirstRunCompleted": true }""");
        var s = new SettingsStore(FilePath).Load();
        Assert.Equal(AppSettings.KeepForever, s.RetentionDays);
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.True(s.FirstRunCompleted);
    }

    [Fact]
    public void Current_version_retention_choice_is_preserved()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { RetentionDays = 30 });
        Assert.Equal(30, new SettingsStore(FilePath).Load().RetentionDays);
    }

    [Fact]
    public void Save_does_not_leave_temp_files()
    {
        new SettingsStore(FilePath).Save(new AppSettings());
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.True(File.Exists(FilePath));
    }
}
