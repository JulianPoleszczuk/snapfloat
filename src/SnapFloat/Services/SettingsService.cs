using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Settings;

namespace SnapFloat.Services;

/// <summary>Owns the live settings instance. Changes apply and broadcast immediately and are saved to disk shortly after.</summary>
internal sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly System.Windows.Threading.DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public SettingsService(SettingsStore store)
    {
        _store = store;
        Current = store.Load(out var notes);
        foreach (var note in notes) Log.Warn("Settings", note);
        _saveTimer.Tick += (_, _) => Flush();
    }

    public AppSettings Current { get; private set; }

    /// <summary>Raised on the UI thread after settings change. Argument is the previous snapshot.</summary>
    public event Action<AppSettings>? Changed;

    public string ScreenshotDirectory => string.IsNullOrWhiteSpace(Current.SaveDirectory)
        ? AppPaths.DefaultScreenshotDirectory
        : Current.SaveDirectory!;

    public bool UsesDefaultDirectory => string.IsNullOrWhiteSpace(Current.SaveDirectory);

    public void Update(Action<AppSettings> mutate)
    {
        var previous = Current.Clone();
        var next = Current.Clone();
        mutate(next);
        SettingsValidator.Normalize(next);
        Current = next;
        // Changes apply immediately; the file write is debounced so dragging a slider doesn't write on every tick.
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke(previous);
    }

    /// <summary>Writes pending changes to disk now. Called by the debounce timer and on exit.</summary>
    public void Flush()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        try
        {
            _store.Save(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Settings", "Could not save settings", ex);
        }
    }
}
