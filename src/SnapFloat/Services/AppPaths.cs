using System.Runtime.InteropServices;

namespace SnapFloat.Services;

internal static class AppPaths
{
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapFloat");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string LogDirectory => Path.Combine(DataRoot, "logs");
    /// <summary>
    /// The Windows Screenshots folder (Pictures\Screenshots), where Win+PrintScreen and Snipping Tool save too.
    /// Resolved through the known-folder API so OneDrive and redirected Pictures folders are honoured.
    /// </summary>
    public static string DefaultScreenshotDirectory { get; } = ResolveScreenshotsFolder();

    /// <summary>Last-resort folder, used only when the chosen folder can't be written.</summary>
    public static string FallbackScreenshotDirectory => Path.Combine(DataRoot, "Screenshots");

    private static readonly Guid FOLDERID_Screenshots = new("B7BEDE81-DF94-4682-A7D8-57A52620B86F");

    private static string ResolveScreenshotsFolder()
    {
        try
        {
            // KF_FLAG_DONT_VERIFY: return the path even if the folder doesn't exist yet; it is created on first save.
            if (SHGetKnownFolderPath(FOLDERID_Screenshots, 0x4000, IntPtr.Zero, out var ptr) == 0)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(ptr);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SnapFloat.exe");

    /// <summary>True when running from an installer-made copy (Inno Setup puts unins000.exe next to the app), not the portable zip.</summary>
    public static bool InstalledBySetup
    {
        get
        {
            try { return Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe").Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public static string Version
    {
        get
        {
            var v = typeof(AppPaths).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Repository URL shown in About.</summary>
    public const string? RepositoryUrl = "https://github.com/JulianPoleszczuk/snapfloat";
}
