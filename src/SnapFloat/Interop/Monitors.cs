using SnapFloat.Core.Layout;

namespace SnapFloat.Interop;

/// <summary>A display as seen by a per-monitor-DPI-aware process: all rectangles are in physical pixels.</summary>
internal sealed record MonitorInfo(IntPtr Handle, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary, string DeviceName);

internal static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            if (FromHandle(h) is { } m) list.Add(m);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo? FromHandle(IntPtr handle)
    {
        var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(handle, ref info)) return null;
        return new MonitorInfo(
            handle,
            ToPixelRect(info.rcMonitor),
            ToPixelRect(info.rcWork),
            Native.GetMonitorScale(handle),
            (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
            info.szDevice);
    }

    /// <summary>The monitor containing the point, falling back to the nearest one.</summary>
    public static MonitorInfo? FromPoint(int x, int y) =>
        FromHandle(Native.MonitorFromPoint(new Native.POINT(x, y), Native.MONITOR_DEFAULTTONEAREST));

    public static MonitorInfo? Primary() =>
        FromHandle(Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTOPRIMARY));

    public static MonitorInfo? UnderCursor() =>
        Native.GetCursorPos(out var p) ? FromPoint(p.X, p.Y) : Primary();

    public static Native.POINT CursorPosition() => Native.GetCursorPos(out var p) ? p : default;

    public static PixelRect ToPixelRect(Native.RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}
