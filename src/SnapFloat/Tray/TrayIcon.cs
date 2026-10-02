using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Interop;
using SnapFloat.Views;
using WinForms = System.Windows.Forms;

namespace SnapFloat.Tray;

/// <summary>Commands the tray menu can trigger.</summary>
internal interface ITrayCommands
{
    void CaptureRegion();
    void CaptureFullScreen();
    void ShowRecent(string path);
    void OpenScreenshotFolder();
    bool PreviewsPaused { get; set; }
    void OpenSettings();
    void ExitApp();
    string RegionShortcut { get; }
    string FullScreenShortcut { get; }
    IReadOnlyList<FileInfo> RecentScreenshots(int count);
}

/// <summary>
/// Notification-area icon. The icon itself is a WinForms NotifyIcon (which re-adds itself automatically when
/// Explorer restarts); the menu is a themed WPF ContextMenu hosted by an invisible window so it matches the app.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly ITrayCommands _commands;
    private readonly WinForms.NotifyIcon _icon;
    private readonly Window _menuHost;
    private ContextMenu? _menu;

    public TrayIcon(ITrayCommands commands)
    {
        _commands = commands;
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/SnapFloat;component/Assets/SnapFloat.ico")).Stream;
        _icon = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize),
            Text = "SnapFloat",
            Visible = true,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is WinForms.MouseButtons.Left or WinForms.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => _commands.OpenSettings();

        _menuHost = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            Width = 1,
            Height = 1,
            Title = "SnapFloat tray menu",
        };
    }

    public void SetPaused(bool paused) => _icon.Text = paused ? "SnapFloat (previews paused)" : "SnapFloat";

    public void Notify(string title, string text, bool warning = false)
    {
        try
        {
            _icon.ShowBalloonTip(5000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", "Balloon failed", ("error", ex.GetType().Name));
        }
    }

    private void ShowMenu()
    {
        _menu = BuildMenu();
        var cursor = Monitors.CursorPosition();
        // The host must be the foreground window, otherwise the menu won't close when clicking elsewhere.
        _menuHost.Show();
        var hwnd = new WindowInteropHelper(_menuHost).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, cursor.X, cursor.Y, 1, 1, Native.SWP_NOACTIVATE);
        Native.SetForegroundWindow(hwnd);
        _menu.PlacementTarget = _menuHost;
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.Closed += (_, _) => _menuHost.Hide();
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Capture region", "Icon.Scan", _commands.CaptureRegion, _commands.RegionShortcut));
        menu.Items.Add(Item("Capture full screen", "Icon.Monitor", () => DelayThen(_commands.CaptureFullScreen), _commands.FullScreenShortcut));
        menu.Items.Add(new Separator());

        var recent = new MenuItem { Header = "Recent screenshots", Icon = Icon("Icon.Clock") };
        var files = _commands.RecentScreenshots(6);
        if (files.Count == 0)
        {
            recent.Items.Add(new MenuItem { Header = "No screenshots yet", IsEnabled = false });
        }
        else
        {
            foreach (var file in files)
            {
                var item = new MenuItem { Header = Describe(file), Icon = Thumbnail(file.FullName), ToolTip = file.Name };
                var path = file.FullName;
                item.Click += (_, _) => _commands.ShowRecent(path);
                recent.Items.Add(item);
            }
        }
        menu.Items.Add(recent);
        menu.Items.Add(Item("Open screenshots folder", "Icon.Folder", _commands.OpenScreenshotFolder));
        menu.Items.Add(new Separator());

        var pause = Item(_commands.PreviewsPaused ? "Resume previews" : "Pause previews", _commands.PreviewsPaused ? "Icon.Play" : "Icon.Pause",
            () => _commands.PreviewsPaused = !_commands.PreviewsPaused);
        menu.Items.Add(pause);
        menu.Items.Add(Item("Settings…", "Icon.Sliders", _commands.OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit SnapFloat", "Icon.Power", _commands.ExitApp));
        return menu;
    }

    /// <summary>Lets the menu fade away before a full-screen capture so it isn't in the picture.</summary>
    private static void DelayThen(Action action) =>
        _ = Task.Delay(250).ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(action), TaskScheduler.Default);

    private static MenuItem Item(string header, string icon, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, Icon = Icon(icon) };
        if (!string.IsNullOrEmpty(gesture) && gesture != "None") item.InputGestureText = gesture.Replace("+", " + ");
        item.Click += (_, _) => action();
        return item;
    }

    private static IconView Icon(string key) =>
        new() { Geometry = (Geometry)Application.Current.FindResource(key), Size = 16 };

    private static string Describe(FileInfo file)
    {
        var t = file.LastWriteTime;
        var day = t.Date == DateTime.Today ? "Today" : t.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : t.ToString("d MMM");
        return $"{day}, {t:HH:mm:ss}";
    }

    private static UIElement Thumbnail(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 40;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return new Border
            {
                Width = 26,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill },
            };
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FileFormatException)
        {
            return Icon("Icon.Image");
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menuHost.Close();
    }
}
