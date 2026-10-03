using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Layout;
using SnapFloat.Interop;
using SnapFloat.Services;

namespace SnapFloat.Previews;

/// <summary>
/// Owns the floating previews. A new screenshot replaces the current (unpinned) preview, while the old file stays on
/// disk. Pinned previews stack in the chosen corner of their monitor.
/// </summary>
internal sealed class PreviewManager : IPreviewHost, IDisposable
{
    private const double StackGap = 10;

    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly Action _openSettings;
    private readonly List<ThumbnailWindow> _windows = [];

    public PreviewManager(SettingsService settings, ThemeService theme, ScreenshotStore store, Action openSettings)
    {
        _settings = settings;
        _theme = theme;
        Store = store;
        _openSettings = openSettings;
        _settings.Changed += _ => OnSettingsChanged();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public ScreenshotStore Store { get; }

    /// <summary>Temporarily suppress previews (tray "Pause previews"). Screenshots are still saved.</summary>
    public bool Paused { get; set; }

    public void OpenSettings() => _openSettings();

    private PreviewOptions Options()
    {
        var s = _settings.Current;
        return new PreviewOptions(s.ThumbnailWidth, s.CornerRadius, _theme.AnimationsEnabled,
            TimeSpan.FromSeconds(s.PreviewDurationSeconds), s.AutoClosePreviews, s.PreviewCorner);
    }

    /// <summary>Shows a preview for a saved screenshot on the monitor containing the origin point.</summary>
    /// <param name="force">Show even when previews are disabled or paused (e.g. re-opened from the tray).</param>
    public async Task ShowAsync(SavedScreenshot shot, int originX, int originY, bool force = false)
    {
        if (!force && (!_settings.Current.ShowPreviews || Paused))
        {
            Log.Info("Preview", "Preview suppressed", ("paused", Paused));
            return;
        }

        var monitor = Monitors.FromPoint(originX, originY) ?? Monitors.Primary();
        if (monitor is null) return;
        var options = Options();

        var decodeWidth = (int)Math.Ceiling(options.Width * monitor.Scale * 2);
        var thumbnail = await LoadThumbnailAsync(shot.Path, Math.Min(decodeWidth, Math.Max(1, shot.PixelWidth)));
        if (thumbnail is null)
        {
            ShowError("Couldn't open screenshot", "The image file is missing or unreadable.", originX, originY);
            return;
        }

        ReplaceUnpinned();
        var window = ThumbnailWindow.ForScreenshot(this, shot, thumbnail, options);
        Add(window, monitor);
    }

    public void ShowError(string title, string detail, int originX, int originY)
    {
        var monitor = Monitors.FromPoint(originX, originY) ?? Monitors.Primary();
        if (monitor is null) return;
        ReplaceUnpinned();
        Add(ThumbnailWindow.ForError(this, title, detail, Options()), monitor);
    }

    /// <summary>Shows an existing screenshot file again (from the tray's recent list) on the monitor under the cursor.</summary>
    public async Task ShowExistingAsync(string path)
    {
        var cursor = Monitors.CursorPosition();
        var size = await Task.Run(() => ReadPixelSize(path));
        if (size is null)
        {
            ShowError("Couldn't open screenshot", "The image file is missing or unreadable.", cursor.X, cursor.Y);
            return;
        }
        var shot = new SavedScreenshot(path, size.Value.Width, size.Value.Height, File.GetLastWriteTimeUtc(path));
        await ShowAsync(shot, cursor.X, cursor.Y, force: true);
    }

    /// <summary>Moves open previews of <paramref name="oldPath"/> over to <paramref name="newPath"/> (same image, other file).</summary>
    public void Retarget(string oldPath, string newPath)
    {
        foreach (var w in _windows.Where(w => !w.IsClosing && string.Equals(w.Screenshot?.Path, oldPath, StringComparison.OrdinalIgnoreCase)))
            w.Retarget(newPath);
    }

    public void DismissAll()
    {
        foreach (var w in _windows.ToList()) w.Dismiss(fast: true);
    }

    private void ReplaceUnpinned()
    {
        foreach (var w in _windows.Where(w => !w.IsPinned && !w.IsClosing).ToList())
            w.Dismiss(fast: true);
    }

    private void Add(ThumbnailWindow window, MonitorInfo monitor)
    {
        window.MonitorHandle = monitor.Handle;
        window.Dismissed += w =>
        {
            _windows.Remove(w);
            Layout(animate: true);
            if (_windows.Count == 0) MemoryTrimmer.ScheduleTrim();
        };
        window.PinnedChanged += _ => Layout(animate: true);
        _windows.Add(window);

        // Position before showing so the first visible frame is already in the right place and at the right DPI.
        Layout(animate: false, only: window);
        window.Show();
        Layout(animate: false, only: window);
        window.Present();
        Log.Info("Preview", "Preview shown", ("monitor", monitor.DeviceName), ("scale", monitor.Scale), ("stack", _windows.Count));
    }

    /// <summary>Stacks previews per monitor: pinned ones nearest the corner, the live preview after them.</summary>
    private void Layout(bool animate, ThumbnailWindow? only = null)
    {
        var corner = _settings.Current.PreviewCorner;
        foreach (var group in _windows.Where(w => !w.IsClosing).GroupBy(w => w.MonitorHandle))
        {
            var monitor = Monitors.FromHandle(group.Key) ?? Monitors.Primary();
            if (monitor is null) continue;
            var offset = 0;
            foreach (var w in group.OrderByDescending(w => w.IsPinned))
            {
                var (pw, ph) = w.PixelSize(monitor.Scale);
                var (x, y) = ThumbnailLayout.Place(monitor.WorkArea, pw, ph, corner, margin: 0, stackOffset: offset);
                if (only is null || ReferenceEquals(only, w)) w.PlaceAt(x, y, monitor.Scale, animate);
                offset += ThumbnailLayout.ToPixels(w.CardSize.Height + StackGap, monitor.Scale);
            }
        }
    }

    private void OnSettingsChanged()
    {
        var options = Options();
        foreach (var w in _windows) w.UpdateOptions(options);
        Layout(animate: true);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Monitors may have been removed or re-scaled: move orphaned previews to the primary display.
            var live = Monitors.All().Select(m => m.Handle).ToHashSet();
            var primary = Monitors.Primary();
            foreach (var w in _windows.Where(w => !live.Contains(w.MonitorHandle)))
                if (primary is not null) w.MonitorHandle = primary.Handle;
            Layout(animate: false);
            Log.Info("Preview", "Display configuration changed; previews re-laid out");
        });
    }

    private static Task<BitmapSource?> LoadThumbnailAsync(string path, int decodeWidth) => Task.Run(() =>
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // read fully, don't keep the file locked
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return (BitmapSource?)bmp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            Log.Warn("Preview", "Thumbnail load failed", ("error", ex.GetType().Name));
            return null;
        }
    });

    private static (int Width, int Height)? ReadPixelSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (var w in _windows.ToList()) w.Close();
        _windows.Clear();
    }
}
