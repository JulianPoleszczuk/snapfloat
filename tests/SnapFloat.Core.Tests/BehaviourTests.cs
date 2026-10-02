using SnapFloat.Core.Capture;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Layout;
using SnapFloat.Core.Previews;
using SnapFloat.Core.Settings;

namespace SnapFloat.Core.Tests;

public class LayoutTests
{
    // 1920×1080 monitor with a 48 px taskbar at the bottom.
    private static readonly PixelRect Work = new(0, 0, 1920, 1032);

    [Fact]
    public void Bottom_right_sits_above_the_taskbar()
    {
        var (x, y) = ThumbnailLayout.Place(Work, 300, 200, PreviewCorner.BottomRight, margin: 0);
        Assert.Equal(1620, x);
        Assert.Equal(832, y);
        Assert.True(y + 200 <= Work.Bottom);
    }

    [Fact]
    public void Stacked_previews_grow_away_from_the_corner()
    {
        var (_, y0) = ThumbnailLayout.Place(Work, 300, 200, PreviewCorner.BottomRight, 0, stackOffset: 0);
        var (_, y1) = ThumbnailLayout.Place(Work, 300, 200, PreviewCorner.BottomRight, 0, stackOffset: 180);
        Assert.Equal(y0 - 180, y1);
        var (_, t1) = ThumbnailLayout.Place(Work, 300, 200, PreviewCorner.TopLeft, 0, stackOffset: 180);
        Assert.Equal(180, t1);
    }

    [Fact]
    public void Position_is_clamped_inside_the_work_area()
    {
        var (_, y) = ThumbnailLayout.Place(Work, 300, 200, PreviewCorner.BottomRight, 0, stackOffset: 5000);
        Assert.Equal(Work.Top, y);
    }

    [Fact]
    public void Works_on_secondary_monitors_with_negative_coordinates()
    {
        var left = new PixelRect(-2560, -200, 2560, 1400);
        var (x, y) = ThumbnailLayout.Place(left, 390, 260, PreviewCorner.BottomRight, 0);
        Assert.Equal(-390, x);
        Assert.Equal(940, y);
    }

    [Fact]
    public void Picks_monitor_containing_point_or_falls_back_to_primary()
    {
        var monitors = new[] { new PixelRect(0, 0, 1920, 1080), new PixelRect(1920, 0, 2560, 1440) };
        Assert.Equal(1, ThumbnailLayout.PickMonitor(monitors, 2000, 100));
        Assert.Equal(0, ThumbnailLayout.PickMonitor(monitors, -50, -50, primaryIndex: 0));
    }

    [Fact]
    public void Landscape_screenshot_keeps_aspect_ratio()
    {
        var size = ThumbnailLayout.ComputeImageSize(1920, 1080, 260);
        Assert.Equal(260, size.Width);
        Assert.Equal(146, size.Height);
        Assert.False(size.CropToFill);
    }

    [Fact]
    public void Very_tall_screenshot_is_capped_and_cropped()
    {
        var size = ThumbnailLayout.ComputeImageSize(800, 12000, 260);
        Assert.True(size.Height <= 260 * ThumbnailLayout.MaxAspectHeight + 1);
        Assert.Equal(ThumbnailLayout.MinWidth, size.Width);
        Assert.True(size.CropToFill);
    }

    [Fact]
    public void Very_wide_screenshot_gets_minimum_height()
    {
        var size = ThumbnailLayout.ComputeImageSize(5000, 40, 260);
        Assert.Equal(ThumbnailLayout.MinHeight, size.Height);
        Assert.True(size.CropToFill);
    }

    [Theory]
    [InlineData(260, 1.0, 260)]
    [InlineData(260, 1.5, 390)]
    [InlineData(260, 1.25, 325)]
    public void Converts_dips_to_pixels(double dips, double scale, int expected) =>
        Assert.Equal(expected, ThumbnailLayout.ToPixels(dips, scale));
}

public class DismissScheduleTests
{
    [Fact]
    public void Starts_with_configured_duration()
    {
        var s = new DismissSchedule(TimeSpan.FromSeconds(5), autoClose: true);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Start());
    }

    [Fact]
    public void Never_schedules_when_auto_close_is_off()
    {
        var s = new DismissSchedule(TimeSpan.FromSeconds(5), autoClose: false);
        Assert.Null(s.Start());
        Assert.Null(s.Release(HoldReason.Hover));
    }

    [Fact]
    public void Hover_pauses_and_leaving_resumes_with_grace()
    {
        var s = new DismissSchedule(TimeSpan.FromSeconds(5), true);
        Assert.Null(s.Hold(HoldReason.Hover));
        Assert.Equal(DismissSchedule.ResumeGrace, s.Release(HoldReason.Hover));
    }

    [Fact]
    public void Drag_keeps_preview_alive_even_after_hover_ends()
    {
        var s = new DismissSchedule(TimeSpan.FromSeconds(5), true);
        s.Hold(HoldReason.Hover);
        s.Hold(HoldReason.Drag);
        Assert.Null(s.Release(HoldReason.Hover)); // drag still active
        Assert.NotNull(s.Release(HoldReason.Drag));
    }

    [Fact]
    public void Pinned_previews_never_auto_dismiss()
    {
        var s = new DismissSchedule(TimeSpan.FromSeconds(5), true);
        s.Hold(HoldReason.Pinned);
        s.Hold(HoldReason.Hover);
        Assert.Null(s.Release(HoldReason.Hover));
        Assert.True(s.IsHeld);
    }

    [Fact]
    public void Short_durations_are_not_extended_by_grace()
    {
        var s = new DismissSchedule(TimeSpan.FromSeconds(2), true);
        s.Hold(HoldReason.Menu);
        Assert.Equal(TimeSpan.FromSeconds(2), s.Release(HoldReason.Menu));
    }
}

public class ClipboardPolicyTests
{
    [Theory]
    [InlineData("SnippingTool", true)]
    [InlineData("ScreenClippingHost", true)]
    [InlineData(null, true)]            // Print Screen: no owner window
    [InlineData("chrome", false)]
    [InlineData("Code", false)]
    public void Screenshot_tools_mode_accepts_only_screenshot_sources(string? owner, bool expected) =>
        Assert.Equal(expected, ClipboardSourcePolicy.ShouldCapture(ClipboardWatchMode.ScreenshotTools, owner, false));

    [Fact]
    public void Own_clipboard_writes_are_always_ignored()
    {
        Assert.False(ClipboardSourcePolicy.ShouldCapture(ClipboardWatchMode.AnyImage, "SnapFloat", true));
        Assert.False(ClipboardSourcePolicy.ShouldCapture(ClipboardWatchMode.ScreenshotTools, null, true));
    }

    [Fact]
    public void Any_image_mode_accepts_other_apps() =>
        Assert.True(ClipboardSourcePolicy.ShouldCapture(ClipboardWatchMode.AnyImage, "chrome", false));

    [Fact]
    public void Off_mode_accepts_nothing() =>
        Assert.False(ClipboardSourcePolicy.ShouldCapture(ClipboardWatchMode.Off, "SnippingTool", false));
}

public sealed class LoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SnapFloatLogTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Writes_structured_lines()
    {
        var logger = new FileLogger(_dir, clock: () => new DateTime(2026, 10, 2, 9, 0, 0));
        logger.Write(LogLevel.Info, "Store", "Screenshot saved", ("file", "a b.png"), ("width", 1920));
        var text = File.ReadAllText(logger.CurrentFilePath);
        Assert.Contains("INFO  Store: Screenshot saved file=\"a b.png\" width=1920", text);
    }

    [Fact]
    public void Respects_minimum_level()
    {
        var logger = new FileLogger(_dir) { MinimumLevel = LogLevel.Warn };
        logger.Write(LogLevel.Info, "X", "ignored");
        Assert.False(File.Exists(logger.CurrentFilePath));
    }

    [Fact]
    public void Rotates_large_files_and_keeps_a_bounded_number()
    {
        var t = new DateTime(2026, 10, 2, 9, 0, 0);
        var logger = new FileLogger(_dir, maxBytesPerFile: 200, keepFiles: 3, clock: () => t = t.AddSeconds(1));
        for (var i = 0; i < 60; i++) logger.Write(LogLevel.Info, "Test", new string('x', 50));
        var files = Directory.GetFiles(_dir, "snapfloat-*.log");
        Assert.InRange(files.Length, 1, 4); // kept files + the active one
    }
}
