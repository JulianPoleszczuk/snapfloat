using SnapFloat.Core.Settings;
using SnapFloat.Core.Storage;

namespace SnapFloat.Core.Tests;

public class StorageTests
{
    private static readonly DateTime Stamp = new(2026, 10, 2, 14, 5, 9, 123);

    [Fact]
    public void File_names_are_sortable_and_recognised_as_managed()
    {
        var path = ScreenshotFiles.CreateUniquePath(@"C:\shots", Stamp, ScreenshotFormat.Png, _ => false);
        Assert.Equal(@"C:\shots\SnapFloat_2026-10-02_14-05-09_123.png", path);
        Assert.True(ScreenshotFiles.IsManagedFileName(Path.GetFileName(path)));
    }

    [Fact]
    public void Collisions_get_a_numeric_suffix()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\shots\SnapFloat_2026-10-02_14-05-09_123.png",
            @"C:\shots\SnapFloat_2026-10-02_14-05-09_123-1.png",
        };
        var path = ScreenshotFiles.CreateUniquePath(@"C:\shots", Stamp, ScreenshotFormat.Png, taken.Contains);
        Assert.Equal(@"C:\shots\SnapFloat_2026-10-02_14-05-09_123-2.png", path);
        Assert.True(ScreenshotFiles.IsManagedFileName(Path.GetFileName(path)));
    }

    [Fact]
    public void Jpeg_uses_jpg_extension() =>
        Assert.EndsWith(".jpg", ScreenshotFiles.CreateUniquePath(@"C:\s", Stamp, ScreenshotFormat.Jpeg, _ => false));

    [Theory]
    [InlineData("holiday.png")]
    [InlineData("SnapFloat_notes.png")]
    [InlineData("SnapFloat_2026-10-02_14-05-09_123.png.partial")]
    [InlineData("SnapFloat_2026-10-02_14-05-09_123.exe")]
    [InlineData("Screenshot 2026-10-02 140509.png")]
    public void Foreign_files_are_never_managed(string name) => Assert.False(ScreenshotFiles.IsManagedFileName(name));

    [Fact]
    public void Retention_only_selects_old_managed_unprotected_files()
    {
        var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        var old = now.AddDays(-8);
        var files = new[]
        {
            new ScreenshotFileInfo(@"C:\s\SnapFloat_2026-10-01_10-00-00_000.png", old, 10),
            new ScreenshotFileInfo(@"C:\s\SnapFloat_2026-10-01_10-00-01_000.png", old, 10),   // protected (on screen)
            new ScreenshotFileInfo(@"C:\s\SnapFloat_2026-10-09_10-00-00_000.png", now.AddDays(-1), 10), // too new
            new ScreenshotFileInfo(@"C:\s\my-own-file.png", old, 10),                          // not ours
        };
        var expired = RetentionPolicy.SelectExpired(files, now, 7,
            new HashSet<string> { @"C:\s\SnapFloat_2026-10-01_10-00-01_000.png" });
        Assert.Equal([@"C:\s\SnapFloat_2026-10-01_10-00-00_000.png"], expired);
    }

    [Fact]
    public void Retention_rejects_non_positive_periods() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RetentionPolicy.SelectExpired([], DateTime.UtcNow, 0));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    public void Formats_byte_sizes(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));
}
