using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using SnapFloat.Core.Capture;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Settings;
using SnapFloat.Interop;
using WinFormsClipboard = System.Windows.Forms.Clipboard;

namespace SnapFloat.Services;

/// <summary>
/// Observes the clipboard with AddClipboardFormatListener (event-driven, no polling). When Snipping Tool,
/// Win+Shift+S or Print Screen put a bitmap on the clipboard, the image is handed to <see cref="ImageCaptured"/>.
/// This is how SnapFloat integrates with the native screenshot flow without hijacking its shortcut.
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private static readonly uint PngFormat = Native.RegisterClipboardFormat("PNG");
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);

    private readonly MessageWindow _window;
    private readonly Func<ClipboardWatchMode> _mode;
    private readonly int _ownPid = Environment.ProcessId;
    private bool _listening;
    private uint _lastSequence;
    private byte[]? _lastHash;
    private DateTime _lastHashTime;
    private DateTime _expectSnipUntil;

    public ClipboardWatcher(MessageWindow window, Func<ClipboardWatchMode> mode)
    {
        _window = window;
        _mode = mode;
        _window.ClipboardUpdated += OnClipboardUpdated;
    }

    public event Action<CapturedImage>? ImageCaptured;

    public void Start()
    {
        if (_listening) return;
        _listening = Native.AddClipboardFormatListener(_window.Handle);
        _lastSequence = Native.GetClipboardSequenceNumber();
        Log.Info("Clipboard", _listening ? "Listening" : "Listener registration failed");
    }

    /// <summary>
    /// Called when SnapFloat opened the snipping overlay itself: the next screenshot-tool image is accepted even if
    /// clipboard watching is switched off.
    /// </summary>
    public void ExpectSnip() => _expectSnipUntil = DateTime.UtcNow.AddMinutes(2);

    private void OnClipboardUpdated()
    {
        var sequence = Native.GetClipboardSequenceNumber();
        if (sequence == _lastSequence) return;
        _lastSequence = sequence;

        if (!HasBitmap()) return;

        var ownerName = OwnerProcessName(out var ownedByUs);
        var expecting = DateTime.UtcNow < _expectSnipUntil;
        var mode = _mode();
        var accept = ClipboardSourcePolicy.ShouldCapture(mode, ownerName, ownedByUs)
                     || (expecting && !ownedByUs && ClipboardSourcePolicy.ShouldCapture(ClipboardWatchMode.ScreenshotTools, ownerName, false));
        Log.Debug("Clipboard", "Bitmap on clipboard", ("owner", ownerName ?? "(none)"), ("accepted", accept));
        if (!accept) return;
        if (expecting) _expectSnipUntil = default;

        Native.GetCursorPos(out var cursor);
        _ = ReadWithRetryAsync(sequence, cursor);
    }

    private static bool HasBitmap() =>
        Native.IsClipboardFormatAvailable(Native.CF_DIB) ||
        Native.IsClipboardFormatAvailable(Native.CF_DIBV5) ||
        Native.IsClipboardFormatAvailable(Native.CF_BITMAP) ||
        (PngFormat != 0 && Native.IsClipboardFormatAvailable(PngFormat));

    /// <summary>Process name of the clipboard owner; null when there is no owner window (Print Screen).</summary>
    private string? OwnerProcessName(out bool ownedByUs)
    {
        ownedByUs = false;
        var owner = Native.GetClipboardOwner();
        if (owner == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(owner, out var pid);
        if (pid == _ownPid) { ownedByUs = true; return "SnapFloat"; }
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    private async Task ReadWithRetryAsync(uint sequence, Native.POINT cursor)
    {
        // The owner may still hold the clipboard open right after notifying; back off briefly.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (Native.GetClipboardSequenceNumber() != sequence) return; // superseded by a newer copy
            var bitmap = TryReadBitmap();
            if (bitmap is not null)
            {
                if (await IsDuplicateAsync(bitmap))
                {
                    bitmap.Dispose();
                    Log.Debug("Clipboard", "Duplicate clipboard image ignored");
                    return;
                }
                ImageCaptured?.Invoke(new CapturedImage(bitmap, cursor.X, cursor.Y, "clipboard"));
                return;
            }
            await Task.Delay(60 + attempt * 60);
        }
        Log.Warn("Clipboard", "Clipboard image could not be read after retries");
    }

    private static Bitmap? TryReadBitmap()
    {
        try
        {
            var data = WinFormsClipboard.GetDataObject();
            if (data is null) return null;

            // Prefer the lossless PNG stream that Snipping Tool also publishes.
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                using var fromPng = new Bitmap(png);
                return Normalize(fromPng);
            }
            if (WinFormsClipboard.GetImage() is { } image)
            {
                using (image) return Normalize(image);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException or OutOfMemoryException or ThreadStateException)
        {
            Log.Debug("Clipboard", "Clipboard read failed", ("error", ex.GetType().Name));
        }
        return null;
    }

    /// <summary>Copies into an independent 32-bpp bitmap that is safe to hand to a worker thread.</summary>
    private static Bitmap Normalize(Image source)
    {
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        return copy;
    }

    /// <summary>Snipping Tool sometimes rewrites the clipboard twice for a single snip; drop identical images.</summary>
    private async Task<bool> IsDuplicateAsync(Bitmap bitmap)
    {
        var hash = await Task.Run(() => HashPixels(bitmap));
        var now = DateTime.UtcNow;
        var duplicate = _lastHash is not null && now - _lastHashTime < DuplicateWindow && hash.AsSpan().SequenceEqual(_lastHash);
        _lastHash = hash;
        _lastHashTime = now;
        return duplicate;
    }

    private static byte[] HashPixels(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var span = new ReadOnlySpan<byte>((void*)data.Scan0, Math.Abs(data.Stride) * data.Height);
                return SHA256.HashData(span);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _window.ClipboardUpdated -= OnClipboardUpdated;
        if (_listening) Native.RemoveClipboardFormatListener(_window.Handle);
        _listening = false;
    }
}
