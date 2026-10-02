// Generates SnapFloat's application/tray icon (.ico with 16–256 px frames) and a 256 px PNG.
// Usage: dotnet run --project tools/IconGen -- <output-dir>
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
var frames = sizes.Select(s => (Size: s, Bitmap: Render(s))).ToList();

frames.Last().Bitmap.Save(Path.Combine(outDir, "SnapFloat-256.png"), ImageFormat.Png);
WriteIco(Path.Combine(outDir, "SnapFloat.ico"), frames);
Console.WriteLine($"Wrote icon to {Path.GetFullPath(outDir)}");

static Bitmap Render(int size)
{
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);

    // Everything is authored on a 32-unit grid and scaled.
    var u = size / 32f;
    var small = size <= 24;
    var inset = small ? 0f : 1f;

    using (var bg = RoundedRect(new RectangleF(inset * u, inset * u, (32 - 2 * inset) * u, (32 - 2 * inset) * u), (small ? 6.5f : 7.5f) * u))
    using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Color.FromArgb(0x5E, 0x6A, 0xE6), Color.FromArgb(0x42, 0x4D, 0xC6)))
        g.FillPath(brush, bg);

    var stroke = (size <= 16 ? 4f : small ? 3f : 2.4f) * u;
    using var pen = new Pen(Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    // Capture-region corner brackets (top-left, top-right, bottom-left).
    var a = small ? 7f : 8f;     // outer edge of the frame
    var b = small ? 25f : 24f;   // opposite outer edge
    var len = small ? 6f : 5.5f;   // bracket arm length
    g.DrawLines(pen, [P(a, a + len), P(a, a), P(a + len, a)]);
    {
        g.DrawLines(pen, [P(b - len, a), P(b, a), P(b, a + len)]);
        g.DrawLines(pen, [P(a, b - len), P(a, b), P(a + len, b)]);
    }

    // The floating preview card, breaking out of the bottom-right corner.
    var card = small ? new RectangleF(15f * u, 16f * u, 12f * u, 10f * u) : new RectangleF(15f * u, 16f * u, 12.5f * u, 9.5f * u);
    using (var shadow = RoundedRect(new RectangleF(card.X, card.Y + 0.8f * u, card.Width, card.Height), 2.2f * u))
    using (var sb = new SolidBrush(Color.FromArgb(70, 0x1A, 0x1F, 0x5C)))
        g.FillPath(sb, shadow);
    using (var cardPath = RoundedRect(card, 2.2f * u))
        g.FillPath(Brushes.White, cardPath);

    if (size >= 32)
    {
        // Tiny landscape glyph inside the card, only where it stays legible.
        using var accent = new SolidBrush(Color.FromArgb(0x4F, 0x5B, 0xD5));
        var m = card;
        g.FillPolygon(accent, [new PointF(m.X + 2f * u, m.Bottom - 2f * u), new PointF(m.X + 5.5f * u, m.Y + 4f * u), new PointF(m.X + 8f * u, m.Bottom - 4f * u), new PointF(m.X + 9.5f * u, m.Y + 5.5f * u), new PointF(m.Right - 2f * u, m.Bottom - 2f * u)]);
    }
    return bmp;

    PointF P(float x, float y) => new(x * u, y * u);
}

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

// Small frames are stored as classic 32-bit DIBs (maximum compatibility with System.Drawing.Icon and the
// notification area); the 256 px frame is PNG-compressed as Windows expects.
static void WriteIco(string path, List<(int Size, Bitmap Bitmap)> frames)
{
    var images = frames.Select(f => f.Size >= 256 ? Png(f.Bitmap) : Dib(f.Bitmap)).ToList();
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
    var offset = 6 + 16 * frames.Count;
    for (var i = 0; i < frames.Count; i++)
    {
        var s = frames[i].Size;
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(images[i].Length); w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}

static byte[] Png(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] Dib(Bitmap bmp)
{
    int n = bmp.Width;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(n * n * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (var y = n - 1; y >= 0; y--)
        for (var x = 0; x < n; x++)
        {
            var c = bmp.GetPixel(x, y);
            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
        }
    var maskStride = ((n + 31) / 32) * 4;
    w.Write(new byte[maskStride * n]); // all-zero AND mask: alpha channel decides transparency
    return ms.ToArray();
}
