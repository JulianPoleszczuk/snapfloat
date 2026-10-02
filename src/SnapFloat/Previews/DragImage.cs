using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapFloat.Interop;

namespace SnapFloat.Previews;

/// <summary>Renders the drag image the shell shows under the cursor: the thumbnail with rounded corners.</summary>
internal static class DragImage
{
    private const double Scale = 0.8; // slightly smaller than the card, like a picked-up object

    /// <summary>
    /// Returns a premultiplied 32-bpp bottom-up DIB section (ownership passes to the caller), or IntPtr.Zero.
    /// </summary>
    public static IntPtr Create(BitmapSource? source, Size cardSize, double cornerRadius, double dpiScale, Point grab,
        out System.Drawing.Size pixelSize, out System.Drawing.Point cursorOffset)
    {
        pixelSize = default;
        cursorOffset = default;
        if (source is null) return IntPtr.Zero;

        var w = Math.Max(1, (int)Math.Round(cardSize.Width * Scale * dpiScale));
        var h = Math.Max(1, (int)Math.Round(cardSize.Height * Scale * dpiScale));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, w, h);
            var r = cornerRadius * Scale * dpiScale;
            dc.PushClip(new RectangleGeometry(rect, r, r));
            // Fill like UniformToFill so the image covers the rounded rectangle.
            var scale = Math.Max(w / (double)source.PixelWidth, h / (double)source.PixelHeight);
            var dw = source.PixelWidth * scale;
            var dh = source.PixelHeight * scale;
            dc.DrawImage(source, new Rect((w - dw) / 2, 0, dw, dh));
            dc.Pop();
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1);
            dc.DrawRoundedRectangle(null, pen, new Rect(0.5, 0.5, w - 1, h - 1), r, r);
        }
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var stride = w * 4;
        var pixels = new byte[stride * h];
        rtb.CopyPixels(pixels, stride, 0);

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = h, // bottom-up
            biPlanes = 1,
            biBitCount = 32,
        };
        var hbmp = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
        for (var y = 0; y < h; y++)
            Marshal.Copy(pixels, y * stride, bits + (h - 1 - y) * stride, stride);

        pixelSize = new System.Drawing.Size(w, h);
        cursorOffset = new System.Drawing.Point(
            Math.Clamp((int)Math.Round(grab.X * Scale * dpiScale), 0, w - 1),
            Math.Clamp((int)Math.Round(grab.Y * Scale * dpiScale), 0, h - 1));
        return hbmp;
    }
}
