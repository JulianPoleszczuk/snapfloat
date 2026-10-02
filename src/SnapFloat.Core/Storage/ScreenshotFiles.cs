using System.Globalization;
using System.Text.RegularExpressions;
using SnapFloat.Core.Settings;

namespace SnapFloat.Core.Storage;

/// <summary>Naming rules for files SnapFloat creates. Only files matching these rules are ever auto-deleted.</summary>
public static partial class ScreenshotFiles
{
    public const string Prefix = "SnapFloat_";

    [GeneratedRegex(@"^SnapFloat_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}(_\d{3})?(-\d+)?\.(png|jpg)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ManagedNameRegex();

    public static string Extension(ScreenshotFormat format) => format == ScreenshotFormat.Jpeg ? ".jpg" : ".png";

    public static string BaseName(DateTime timestamp) =>
        Prefix + timestamp.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);

    /// <summary>True if the file name was produced by SnapFloat (safe to manage automatically).</summary>
    public static bool IsManagedFileName(string fileName) => ManagedNameRegex().IsMatch(fileName);

    /// <summary>
    /// Returns a path in <paramref name="directory"/> that does not exist yet. Appends -1, -2, … on collision.
    /// </summary>
    public static string CreateUniquePath(string directory, DateTime timestamp, ScreenshotFormat format, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var ext = Extension(format);
        var baseName = BaseName(timestamp);
        var candidate = Path.Combine(directory, baseName + ext);
        for (var i = 1; exists(candidate); i++)
        {
            if (i > 10_000) throw new IOException("Could not find a free file name.");
            candidate = Path.Combine(directory, $"{baseName}-{i}{ext}");
        }
        return candidate;
    }
}

public readonly record struct ScreenshotFileInfo(string Path, DateTime LastWriteTimeUtc, long Length);

public static class RetentionPolicy
{
    /// <summary>
    /// Chooses which managed files are older than the retention window. Files whose names were not produced by
    /// SnapFloat, and files in <paramref name="protectedPaths"/> (visible previews, active drags), are never chosen.
    /// </summary>
    public static IReadOnlyList<string> SelectExpired(
        IEnumerable<ScreenshotFileInfo> files,
        DateTime utcNow,
        int retentionDays,
        IReadOnlySet<string>? protectedPaths = null)
    {
        if (retentionDays < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        var cutoff = utcNow - TimeSpan.FromDays(retentionDays);
        return files
            .Where(f => ScreenshotFiles.IsManagedFileName(Path.GetFileName(f.Path)))
            .Where(f => f.LastWriteTimeUtc < cutoff)
            .Where(f => protectedPaths is null || !protectedPaths.Contains(f.Path))
            .Select(f => f.Path)
            .ToList();
    }
}

public static class ByteSize
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
