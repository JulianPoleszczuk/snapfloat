using SnapFloat.Core.Settings;

namespace SnapFloat.Core.Layout;

/// <summary>Integer rectangle in physical pixels (virtual-screen coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

public readonly record struct ThumbnailSize(double Width, double Height, bool CropToFill);

public static class ThumbnailLayout
{
    /// <summary>Narrowest card; wide enough for the five-button quick-action toolbar.</summary>
    public const double MinWidth = 168;
    public const double MinHeight = 64;
    /// <summary>Tallest the thumbnail may get, as a multiple of the configured width.</summary>
    public const double MaxAspectHeight = 1.15;

    /// <summary>
    /// Computes the image area size in device-independent pixels. Very tall or very wide screenshots are clamped and
    /// rendered cropped-to-fill so the thumbnail stays compact.
    /// </summary>
    public static ThumbnailSize ComputeImageSize(int imagePixelWidth, int imagePixelHeight, double targetWidth)
    {
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0) return new ThumbnailSize(targetWidth, targetWidth * 0.5625, true);
        var aspect = (double)imagePixelHeight / imagePixelWidth;
        var width = targetWidth;
        var height = width * aspect;
        var crop = false;
        var maxHeight = targetWidth * MaxAspectHeight;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height / aspect;
            if (width < MinWidth) { width = MinWidth; crop = true; }
        }
        if (height < MinHeight) { height = MinHeight; crop = true; }
        return new ThumbnailSize(Math.Round(width), Math.Round(height), crop);
    }

    /// <summary>
    /// Places a window of <paramref name="windowWidth"/>×<paramref name="windowHeight"/> physical pixels in the given
    /// corner of the work area, offset away from the corner by <paramref name="stackOffset"/> (for stacked previews).
    /// The result is clamped so the window always stays inside the work area.
    /// </summary>
    public static (int X, int Y) Place(PixelRect workArea, int windowWidth, int windowHeight, PreviewCorner corner, int margin, int stackOffset = 0)
    {
        var right = corner is PreviewCorner.BottomRight or PreviewCorner.TopRight;
        var bottom = corner is PreviewCorner.BottomRight or PreviewCorner.BottomLeft;

        var x = right ? workArea.Right - margin - windowWidth : workArea.Left + margin;
        var y = bottom ? workArea.Bottom - margin - windowHeight - stackOffset : workArea.Top + margin + stackOffset;

        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - windowWidth));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - windowHeight));
        return (x, y);
    }

    /// <summary>Picks the monitor (by work area) containing the point, or the primary/first one.</summary>
    public static int PickMonitor(IReadOnlyList<PixelRect> monitorBounds, int x, int y, int primaryIndex = 0)
    {
        for (var i = 0; i < monitorBounds.Count; i++)
            if (monitorBounds[i].Contains(x, y)) return i;
        return monitorBounds.Count == 0 ? -1 : Math.Clamp(primaryIndex, 0, monitorBounds.Count - 1);
    }

    public static int ToPixels(double dips, double dpiScale) => (int)Math.Round(dips * dpiScale);
}
