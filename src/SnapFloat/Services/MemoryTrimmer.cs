using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SnapFloat.Services;

/// <summary>
/// SnapFloat sits idle most of the day. After a burst of activity (decoding multi-megapixel screenshots) this
/// returns the freed memory to Windows so the background footprint stays small.
/// </summary>
internal static class MemoryTrimmer
{
    private static DispatcherTimer? _timer;

    /// <summary>Trims once the app has been quiet for a couple of seconds. Repeated calls restart the delay.</summary>
    public static void ScheduleTrim()
    {
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        };
        return timer;
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
