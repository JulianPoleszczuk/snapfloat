using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Layout;
using SnapFloat.Interop;

namespace SnapFloat.Services;

/// <summary>A captured image plus where on the desktop it came from (physical pixels), used to pick the monitor.</summary>
internal sealed record CapturedImage(Bitmap Bitmap, int OriginX, int OriginY, string Source);

/// <summary>
/// SnapFloat's own capture modes. Region capture deliberately delegates to the native Windows snipping overlay
/// (the same UI as Win+Shift+S) rather than reimplementing it; its result arrives through the clipboard.
/// </summary>
internal static class ScreenCapture
{
    /// <summary>Opens the Windows snipping overlay. Returns false if the URI handler is unavailable.</summary>
    public static bool StartRegionSnip()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Capture", "Could not open the Windows snipping overlay", ex);
            return false;
        }
    }

    /// <summary>Captures the whole monitor under the mouse pointer.</summary>
    public static CapturedImage? CaptureMonitorUnderCursor()
    {
        var cursor = Monitors.CursorPosition();
        var monitor = Monitors.FromPoint(cursor.X, cursor.Y) ?? Monitors.Primary();
        if (monitor is null) return null;
        var bmp = CopyScreen(monitor.Bounds);
        return bmp is null ? null : new CapturedImage(bmp, cursor.X, cursor.Y, "fullscreen");
    }

    /// <summary>Captures the foreground window's visible frame. Falls back to null for the desktop/taskbar.</summary>
    public static CapturedImage? CaptureForegroundWindow()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        hwnd = Native.GetAncestor(hwnd, Native.GA_ROOT);
        // SnapFloat's own previews/menus can end up "foreground" (e.g. after Settings closes); use the window below.
        if (IsOwnWindow(hwnd)) hwnd = NextCapturableWindow(hwnd);
        if (hwnd == IntPtr.Zero || IsShellSurface(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return null;

        // DWM's extended frame bounds exclude the invisible resize borders Windows 10/11 add around windows.
        Native.RECT rect;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
        {
            if (!Native.GetWindowRect(hwnd, out rect)) return null;
        }

        // Clip to the monitor the window is on so off-screen parts don't become black bars.
        var monitor = Monitors.FromHandle(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
        var area = Monitors.ToPixelRect(rect);
        if (monitor is not null) area = Intersect(area, monitor.Bounds);
        if (area.Width < 8 || area.Height < 8) return null;

        var bmp = CopyScreen(area);
        return bmp is null ? null : new CapturedImage(bmp, area.Left + area.Width / 2, area.Top + area.Height / 2, "window");
    }

    private static Bitmap? CopyScreen(PixelRect r)
    {
        try
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            // Happens on the secure desktop (UAC prompt, lock screen) or during display mode changes.
            Log.Error("Capture", "Screen copy failed", ex, ("width", r.Width), ("height", r.Height));
            return null;
        }
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>Walks down the z-order to the first visible, non-tool, non-cloaked window of another process.</summary>
    private static IntPtr NextCapturableWindow(IntPtr from)
    {
        for (var h = Native.GetWindow(from, Native.GW_HWNDNEXT); h != IntPtr.Zero; h = Native.GetWindow(h, Native.GW_HWNDNEXT))
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || IsOwnWindow(h)) continue;
            if ((Native.GetExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0) continue;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!Native.GetWindowRect(h, out var r) || r.Width < 50 || r.Height < 50) continue;
            return h;
        }
        return IntPtr.Zero;
    }

    private static bool IsShellSurface(IntPtr hwnd)
    {
        if (hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return true;
        var sb = new StringBuilder(64);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW";
    }

    private static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
