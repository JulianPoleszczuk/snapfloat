using System.Globalization;
using System.Text;

namespace SnapFloat.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Minimal structured file logger with daily files, size cap and retention. Thread-safe.
/// Never pass image data or clipboard contents to it, only metadata (sizes, paths, durations).
/// </summary>
public sealed class FileLogger
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytesPerFile;
    private readonly int _keepFiles;
    private readonly Func<DateTime> _clock;

    public FileLogger(string directory, long maxBytesPerFile = 1024 * 1024, int keepFiles = 7, Func<DateTime>? clock = null)
    {
        _directory = directory;
        _maxBytesPerFile = maxBytesPerFile;
        _keepFiles = keepFiles;
        _clock = clock ?? (() => DateTime.Now);
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public string CurrentFilePath => Path.Combine(_directory, $"snapfloat-{_clock():yyyyMMdd}.log");

    public void Write(LogLevel level, string category, string message, params (string Key, object? Value)[] fields)
    {
        if (level < MinimumLevel) return;
        var sb = new StringBuilder(128);
        sb.Append(_clock().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(' ').Append(level.ToString().ToUpperInvariant().PadRight(5))
          .Append(' ').Append(category).Append(": ").Append(message);
        foreach (var (key, value) in fields)
        {
            sb.Append(' ').Append(key).Append('=');
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
            if (text.Contains(' ') || text.Contains('"')) sb.Append('"').Append(text.Replace("\"", "\\\"")).Append('"');
            else sb.Append(text);
        }
        sb.AppendLine();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = CurrentFilePath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > _maxBytesPerFile)
                {
                    File.Move(path, Path.Combine(_directory, $"snapfloat-{_clock():yyyyMMdd-HHmmss}.log"), overwrite: true);
                    Prune();
                }
                else if (!info.Exists)
                {
                    Prune();
                }
                File.AppendAllText(path, sb.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_directory).GetFiles("snapfloat-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(_keepFiles);
        foreach (var f in files)
        {
            try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Static facade so any component can log without plumbing.</summary>
public static class Log
{
    private static FileLogger? _logger;

    public static void Initialize(FileLogger logger) => _logger = logger;

    public static void Debug(string category, string message, params (string, object?)[] fields) => _logger?.Write(LogLevel.Debug, category, message, fields);
    public static void Info(string category, string message, params (string, object?)[] fields) => _logger?.Write(LogLevel.Info, category, message, fields);
    public static void Warn(string category, string message, params (string, object?)[] fields) => _logger?.Write(LogLevel.Warn, category, message, fields);

    public static void Error(string category, string message, Exception? ex = null, params (string, object?)[] fields)
    {
        if (ex is null) { _logger?.Write(LogLevel.Error, category, message, fields); return; }
        var all = fields.Concat([("error", (object?)ex.GetType().Name), ("detail", ex.Message)]).ToArray();
        _logger?.Write(LogLevel.Error, category, message, all);
    }
}
