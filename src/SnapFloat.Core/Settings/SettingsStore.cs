using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapFloat.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON with atomic replace semantics.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();

    public SettingsStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>
    /// Loads settings. A missing file yields defaults; a corrupt file is renamed to *.corrupt and defaults are used,
    /// so a bad file never prevents startup.
    /// </summary>
    public AppSettings Load(out IReadOnlyList<string> notes)
    {
        lock (_gate)
        {
            var collected = new List<string>();
            AppSettings settings;
            if (!File.Exists(FilePath))
            {
                settings = new AppSettings();
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(FilePath);
                    settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    collected.Add($"Settings file unreadable ({ex.GetType().Name}); defaults used");
                    TryQuarantine();
                    settings = new AppSettings();
                }
            }
            collected.AddRange(Migrate(settings));
            collected.AddRange(SettingsValidator.Normalize(settings));
            notes = collected;
            return settings;
        }
    }

    public AppSettings Load() => Load(out _);

    /// <summary>Upgrades settings written by older versions. Runs before validation stamps the current version.</summary>
    private static IEnumerable<string> Migrate(AppSettings settings)
    {
        if (settings.Version < 2)
        {
            // v1 deleted screenshots after 7 days by default (with a separate on/off toggle). From v2 the
            // default is to keep screenshots forever.
            settings.RetentionDays = AppSettings.KeepForever;
            yield return "Migrated v1 settings: retention set to Forever";
        }
        if (settings.Version < 3
            && settings.RegionHotkey == "Ctrl+Shift+4"
            && settings.FullScreenHotkey == "Ctrl+Shift+3"
            && settings.WindowHotkey == "Ctrl+Shift+5")
        {
            // Up to v2 these were the defaults and blocked the same keys in other apps (Excel, VS Code).
            // Only an untouched set is cleared; shortcuts the user picked are kept.
            settings.RegionHotkey = settings.FullScreenHotkey = settings.WindowHotkey = "None";
            yield return "Migrated v2 settings: default shortcuts switched off";
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    private void TryQuarantine()
    {
        try { File.Move(FilePath, FilePath + ".corrupt", overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
