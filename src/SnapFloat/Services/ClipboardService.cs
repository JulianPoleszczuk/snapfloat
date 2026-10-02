using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using SnapFloat.Core.Diagnostics;

namespace SnapFloat.Services;

/// <summary>
/// Clipboard writes with the formats Windows apps expect:
/// CF_BITMAP/CF_DIB (Paint, Office, Claude Code's Alt+V paste), "PNG" (browsers, chat apps, lossless) and
/// CF_HDROP (paste as a file in Explorer). Retries on contention, because another app may hold the clipboard briefly.
/// </summary>
internal static class ClipboardService
{
    public static async Task<bool> CopyImageAsync(string path)
    {
        if (!File.Exists(path)) return false;
        BitmapSource image;
        byte[] pngBytes;
        try
        {
            (image, pngBytes) = await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();

                byte[] png;
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    png = File.ReadAllBytes(path);
                }
                else
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using var ms = new MemoryStream();
                    enc.Save(ms);
                    png = ms.ToArray();
                }
                return ((BitmapSource)bmp, png);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            Log.Warn("ClipboardOut", "Could not load image for copy", ("error", ex.GetType().Name));
            return false;
        }

        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", new MemoryStream(pngBytes), autoConvert: false);
        data.SetFileDropList(new StringCollection { path });
        return await SetWithRetryAsync(data, "image");
    }

    public static Task<bool> CopyTextAsync(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        return SetWithRetryAsync(data, "path");
    }

    private static async Task<bool> SetWithRetryAsync(DataObject data, string kind)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                Log.Info("ClipboardOut", "Copied", ("kind", kind));
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                Log.Debug("ClipboardOut", "Clipboard busy, retrying", ("attempt", attempt));
                await Task.Delay(80 * attempt);
            }
        }
        Log.Warn("ClipboardOut", "Clipboard stayed busy", ("kind", kind));
        return false;
    }
}
