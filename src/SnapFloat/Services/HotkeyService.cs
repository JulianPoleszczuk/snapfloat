using System.Runtime.InteropServices;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Input;
using SnapFloat.Interop;

namespace SnapFloat.Services;

internal enum HotkeyAction { Region = 1, FullScreen = 2, Window = 3 }

/// <summary>
/// Registers SnapFloat's global shortcuts with RegisterHotKey. Win+Shift+S is never registered: it belongs to
/// Windows; SnapFloat observes its result through the clipboard instead.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const uint MOD_NOREPEAT = 0x4000;
    private readonly MessageWindow _window;
    private readonly HashSet<int> _registered = [];

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.HotkeyPressed += id =>
        {
            if (Enum.IsDefined(typeof(HotkeyAction), id)) Pressed?.Invoke((HotkeyAction)id);
        };
    }

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Registers the given shortcuts, replacing earlier ones. Returns the actions that failed (in use elsewhere).</summary>
    public IReadOnlyList<(HotkeyAction Action, Hotkey Hotkey)> Apply(IReadOnlyDictionary<HotkeyAction, Hotkey> map)
    {
        UnregisterAll();
        var failed = new List<(HotkeyAction, Hotkey)>();
        foreach (var (action, hotkey) in map)
        {
            if (hotkey.IsEmpty) continue;
            if (Native.RegisterHotKey(_window.Handle, (int)action, (uint)hotkey.Modifiers | MOD_NOREPEAT, (uint)hotkey.VirtualKey))
            {
                _registered.Add((int)action);
                Log.Info("Hotkeys", "Registered", ("action", action), ("keys", hotkey));
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                failed.Add((action, hotkey));
                Log.Warn("Hotkeys", "Registration failed", ("action", action), ("keys", hotkey), ("win32", err));
            }
        }
        return failed;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered) Native.UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    public void Dispose() => UnregisterAll();
}
