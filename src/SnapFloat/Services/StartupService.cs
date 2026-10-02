using Microsoft.Win32;
using SnapFloat.Core.Diagnostics;

namespace SnapFloat.Services;

/// <summary>Per-user "start at sign-in" via HKCU\...\Run. No admin rights needed; the installer uses the same value.</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "SnapFloat";
    public const string BackgroundArgument = "--background";

    private static string Command => $"\"{AppPaths.ExecutablePath}\" {BackgroundArgument}";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string value || string.IsNullOrWhiteSpace(value)) return false;
            return !IsDisabledInTaskManager();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn("Startup", "Could not read startup state", ("error", ex.GetType().Name));
            return false;
        }
    }

    /// <summary>Returns false (and logs) if the registry could not be written.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);
                ClearTaskManagerDisable();
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            Log.Info("Startup", "Start at sign-in changed", ("enabled", enabled));
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Startup", "Could not change startup state", ex);
            return false;
        }
    }

    /// <summary>Keeps the stored command pointing at the current executable (e.g. after an update moved it).</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string value && !string.Equals(value, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    // Task Manager's "Disable" toggle writes a binary blob whose first byte is odd (3) when disabled.
    private static bool IsDisabledInTaskManager()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }

    private static void ClearTaskManagerDisable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        if (key?.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
