using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Settings;
using SnapFloat.Core.Storage;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace SnapFloat.Services;

/// <param name="UsedFallback">True when the chosen folder couldn't be written and the file went to the backup folder.</param>
internal sealed record SavedScreenshot(string Path, int PixelWidth, int PixelHeight, DateTime CreatedUtc, bool UsedFallback = false);

internal readonly record struct StorageUsage(int Count, long Bytes);

/// <summary>
/// Writes screenshots to disk and manages their lifecycle. Only files whose names SnapFloat generated are ever
/// deleted automatically, and never while a preview or drag is still using them.
/// </summary>
internal sealed class ScreenshotStore
{
    private readonly SettingsService _settings;
    private readonly HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shared = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ScreenshotStore(SettingsService settings) => _settings = settings;

    public string Directory => _settings.ScreenshotDirectory;

    /// <summary>
    /// Encodes and writes the bitmap on a worker thread. The file is written under a temporary name and renamed
    /// once complete, so a preview is never shown for a half-written file. Disposes the bitmap.
    /// </summary>
    public Task<SavedScreenshot> SaveAsync(Bitmap bitmap)
    {
        var settings = _settings.Current;
        var directory = Directory;
        return Task.Run(() =>
        {
            using (bitmap)
            {
                try
                {
                    return Write(bitmap, directory, settings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           && !string.Equals(directory, AppPaths.FallbackScreenshotDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    // The folder can disappear or be locked (unplugged drive, network share, OneDrive trouble).
                    // Save to SnapFloat's own data folder instead so the screenshot isn't lost, and log it.
                    Log.Warn("Store", "Screenshot folder unavailable, saving to fallback folder", ("error", ex.GetType().Name));
                    return Write(bitmap, AppPaths.FallbackScreenshotDirectory, settings) with { UsedFallback = true };
                }
            }
        });
    }

    private static SavedScreenshot Write(Bitmap bitmap, string directory, AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(directory);
        var path = ScreenshotFiles.CreateUniquePath(directory, DateTime.Now, settings.ImageFormat);
        var partial = path + ".partial";
        try
        {
            using (var fs = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (settings.ImageFormat == ScreenshotFormat.Jpeg)
                    SaveJpeg(bitmap, fs, settings.JpegQuality);
                else
                    bitmap.Save(fs, ImageFormat.Png);
            }
            File.Move(partial, path, overwrite: false);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        Log.Info("Store", "Screenshot saved", ("file", System.IO.Path.GetFileName(path)), ("width", bitmap.Width), ("height", bitmap.Height));
        return new SavedScreenshot(path, bitmap.Width, bitmap.Height, DateTime.UtcNow);
    }

    private static void SaveJpeg(Bitmap bitmap, Stream stream, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        // JPEG has no alpha; flatten onto white so transparent clipboard images don't turn black.
        using var flat = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flat))
        {
            g.Clear(Color.White);
            g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        }
        flat.Save(stream, codec, parameters);
    }

    /// <summary>Marks a file as in use by a preview/drag so retention never removes it underneath the user.</summary>
    public void Retain(string path) { lock (_gate) _inUse.Add(path); }

    public void Release(string path) { lock (_gate) _inUse.Remove(path); }

    private HashSet<string> SnapshotInUse() { lock (_gate) return new HashSet<string>(_inUse, StringComparer.OrdinalIgnoreCase); }

    /// <summary>
    /// Records that the file left SnapFloat (dragged, copied, opened): another app may now refer to this exact path,
    /// so it must never be removed as a duplicate.
    /// </summary>
    public void MarkShared(string path) { lock (_gate) _shared.Add(path); }

    public bool IsShared(string path) { lock (_gate) return _shared.Contains(path); }

    /// <summary>
    /// Most recent screenshots, newest first: every image in the screenshot folder (including the ones Windows saves
    /// there itself) plus any that went to the backup folder.
    /// </summary>
    public IReadOnlyList<FileInfo> Recent(int count)
    {
        var files = new List<FileInfo>();
        foreach (var directory in new[] { Directory, AppPaths.FallbackScreenshotDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var dir = new DirectoryInfo(directory);
                if (dir.Exists) files.AddRange(dir.EnumerateFiles().Where(f => IsImageFile(f.Name)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Warn("Store", "Could not list screenshots", ("error", ex.GetType().Name));
            }
        }
        return files.OrderByDescending(f => f.LastWriteTimeUtc).Take(count).ToList();
    }

    private static bool IsImageFile(string name) =>
        name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Snipping Tool also saves every snip to the Windows Screenshots folder ("Screenshot 2026-10-03 120000.png") when
    /// its automatic saving is on, which is the default. Waits a few seconds for that file and returns it when its
    /// pixels hash to <paramref name="pixelHash"/> (SHA-256 of the 32-bpp BGRA pixels), or null if none appears.
    /// </summary>
    public async Task<string?> FindWindowsCopyAsync(byte[] pixelHash, int width, int height, DateTime capturedUtc)
    {
        var directory = AppPaths.DefaultScreenshotDirectory;
        var notBefore = capturedUtc - TimeSpan.FromSeconds(5);
        for (var attempt = 0; attempt < 16; attempt++) // about 4 s; Snipping Tool usually writes within 1 s
        {
            await Task.Delay(250);
            var match = await Task.Run(() => FindByPixels(directory, pixelHash, width, height, notBefore));
            if (match is not null) return match;
        }
        return null;
    }

    private static string? FindByPixels(string directory, byte[] pixelHash, int width, int height, DateTime notBeforeUtc)
    {
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) return null;
            foreach (var file in dir.EnumerateFiles("*.png"))
            {
                if (file.LastWriteTimeUtc < notBeforeUtc || ScreenshotFiles.IsManagedFileName(file.Name)) continue;
                if (HashPixels(file.FullName, width, height) is { } hash && hash.AsSpan().SequenceEqual(pixelHash))
                    return file.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    /// <summary>SHA-256 of the image's BGRA32 pixels; null if it can't be read yet (still being written) or has another size.</summary>
    private static byte[]? HashPixels(string path, int width, int height)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth != width || frame.PixelHeight != height) return null;
            var bgra = new FormatConvertedBitmap(frame, WpfPixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);
            return SHA256.HashData(pixels);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Removes SnapFloat's own copy of a screenshot that Windows also saved. Never touches shared or in-use files.</summary>
    public bool DeleteDuplicate(string path)
    {
        if (!ScreenshotFiles.IsManagedFileName(System.IO.Path.GetFileName(path))) return false;
        lock (_gate)
        {
            if (_shared.Contains(path) || _inUse.Contains(path)) return false;
        }
        return TryDelete(path);
    }

    public StorageUsage GetUsage()
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (!dir.Exists) return default;
            var files = dir.EnumerateFiles(ScreenshotFiles.Prefix + "*").Where(f => ScreenshotFiles.IsManagedFileName(f.Name)).ToList();
            return new StorageUsage(files.Count, files.Sum(f => f.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return default;
        }
    }

    /// <summary>Deletes managed screenshots older than the retention period. Safe to call from any thread.</summary>
    public int ApplyRetention()
    {
        var settings = _settings.Current;
        if (settings.RetentionDays == AppSettings.KeepForever) return 0;
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (!dir.Exists) return 0;
            var files = dir.EnumerateFiles(ScreenshotFiles.Prefix + "*")
                .Select(f => new ScreenshotFileInfo(f.FullName, f.LastWriteTimeUtc, f.Length));
            var expired = RetentionPolicy.SelectExpired(files, DateTime.UtcNow, settings.RetentionDays, SnapshotInUse());
            var deleted = expired.Count(TryDelete);
            if (deleted > 0) Log.Info("Store", "Retention cleanup", ("deleted", deleted), ("days", settings.RetentionDays));
            return deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Store", "Retention cleanup failed", ("error", ex.GetType().Name));
            return 0;
        }
    }

    /// <summary>Deletes every managed screenshot except those currently on screen. Caller must confirm with the user first.</summary>
    public int ClearAll()
    {
        try
        {
            var dir = new DirectoryInfo(Directory);
            if (!dir.Exists) return 0;
            var inUse = SnapshotInUse();
            var deleted = dir.EnumerateFiles(ScreenshotFiles.Prefix + "*")
                .Where(f => ScreenshotFiles.IsManagedFileName(f.Name) && !inUse.Contains(f.FullName))
                .Select(f => f.FullName)
                .ToList()
                .Count(TryDelete);
            Log.Info("Store", "Cleared screenshots", ("deleted", deleted));
            return deleted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Store", "Clear failed", ("error", ex.GetType().Name));
            return 0;
        }
    }

    /// <summary>Moves a single screenshot to the Recycle Bin (user-initiated, so it is recoverable).</summary>
    public bool Recycle(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Log.Info("Store", "Screenshot moved to Recycle Bin", ("file", System.IO.Path.GetFileName(path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.Security.SecurityException)
        {
            Log.Warn("Store", "Recycle failed", ("error", ex.GetType().Name));
            return false;
        }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
