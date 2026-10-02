using System.Windows.Interop;

namespace SnapFloat.Interop;

/// <summary>
/// Hidden message-only window that receives WM_HOTKEY and WM_CLIPBOARDUPDATE. Lives on the UI thread, so handlers
/// run on the dispatcher without polling.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private readonly HwndSource _source;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("SnapFloat.MessageWindow")
        {
            ParentWindow = HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    public event Action<int>? HotkeyPressed;
    public event Action? ClipboardUpdated;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY:
                HotkeyPressed?.Invoke(wParam.ToInt32());
                handled = true;
                break;
            case Native.WM_CLIPBOARDUPDATE:
                ClipboardUpdated?.Invoke();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
