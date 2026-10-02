using SnapFloat.Core.Settings;

namespace SnapFloat.Core.Capture;

/// <summary>Decides whether a new clipboard bitmap should become a SnapFloat preview.</summary>
public static class ClipboardSourcePolicy
{
    /// <summary>
    /// Processes that own the clipboard after a Windows screenshot: the Windows 11 Snipping Tool, the Windows 10
    /// "Screen snip" host (Win+Shift+S) and the legacy Snip &amp; Sketch app.
    /// </summary>
    public static readonly IReadOnlySet<string> ScreenshotProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool",
        "ScreenClippingHost",
        "ScreenSketch",
    };

    /// <param name="mode">User's clipboard watch setting.</param>
    /// <param name="ownerProcessName">
    /// Process name (no extension) owning the clipboard, or null when the clipboard has no owner window, which is
    /// what the classic Print Screen key produces.
    /// </param>
    /// <param name="ownedBySnapFloat">True when SnapFloat itself put the data on the clipboard.</param>
    public static bool ShouldCapture(ClipboardWatchMode mode, string? ownerProcessName, bool ownedBySnapFloat)
    {
        if (ownedBySnapFloat) return false;
        return mode switch
        {
            ClipboardWatchMode.Off => false,
            ClipboardWatchMode.AnyImage => true,
            _ => ownerProcessName is null || ScreenshotProcesses.Contains(ownerProcessName),
        };
    }
}
