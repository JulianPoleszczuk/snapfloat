using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Layout;
using SnapFloat.Core.Previews;
using SnapFloat.Core.Settings;
using SnapFloat.Interop;
using SnapFloat.Services;
using SnapFloat.Views;

namespace SnapFloat.Previews;

/// <summary>Display options captured when a preview is created.</summary>
internal sealed record PreviewOptions(double Width, double CornerRadius, bool Animations, TimeSpan Duration, bool AutoClose, PreviewCorner Corner);

/// <summary>Things a preview needs from the rest of the app.</summary>
internal interface IPreviewHost
{
    ScreenshotStore Store { get; }
    void OpenSettings();
}

/// <summary>
/// The floating screenshot thumbnail. Never takes keyboard focus (WS_EX_NOACTIVATE), stays above normal windows,
/// is excluded from screen capture, and starts a real OLE file drag of the saved image.
/// </summary>
internal partial class ThumbnailWindow : Window
{
    public const double ShadowMargin = 18;
    private const double DragThreshold = 6;

    private readonly IPreviewHost _host;
    private readonly DispatcherTimer _dismissTimer = new() { IsEnabled = false };
    private readonly DispatcherTimer _toolbarHideTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private readonly DismissSchedule _schedule;
    private PreviewOptions _options;
    private Point _downPoint;
    private bool _mouseDown;
    private bool _toolbarVisible;
    private bool _closing;
    private DispatcherTimer? _moveTimer;

    private ThumbnailWindow(IPreviewHost host, PreviewOptions options, TimeSpan duration, bool autoClose)
    {
        InitializeComponent();
        _host = host;
        _options = options;
        _schedule = new DismissSchedule(duration, autoClose);
        _dismissTimer.Tick += (_, _) => { _dismissTimer.Stop(); Dismiss(); };
        _toolbarHideTimer.Tick += (_, _) => { _toolbarHideTimer.Stop(); SetToolbarVisible(false); };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); Fade(StatusPill, 0, 180); };

        Root.Margin = new Thickness(ShadowMargin);
        Root.MouseEnter += OnHoverEnter;
        Root.MouseLeave += OnHoverLeave;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => StopTimers();
    }

    /// <summary>The saved screenshot this preview represents (null for an error card).</summary>
    public SavedScreenshot? Screenshot { get; private set; }

    public bool IsPinned { get; private set; }
    public bool IsError { get; private set; }
    public bool IsClosing => _closing;

    /// <summary>Monitor this preview is laid out on.</summary>
    public IntPtr MonitorHandle { get; set; }

    /// <summary>Card size in DIPs (without shadow margin).</summary>
    public Size CardSize { get; private set; }

    public event Action<ThumbnailWindow>? PinnedChanged;
    public event Action<ThumbnailWindow>? Dismissed;

    public static ThumbnailWindow ForScreenshot(IPreviewHost host, SavedScreenshot shot, BitmapSource thumbnail, PreviewOptions options)
    {
        var w = new ThumbnailWindow(host, options, options.Duration, options.AutoClose) { Screenshot = shot };
        w.Picture.Source = thumbnail;
        var size = ThumbnailLayout.ComputeImageSize(shot.PixelWidth, shot.PixelHeight, options.Width);
        if (size.CropToFill)
        {
            w.Picture.Stretch = Stretch.UniformToFill;
            w.Picture.VerticalAlignment = VerticalAlignment.Top;
        }
        w.SetCardSize(new Size(size.Width, size.Height));
        w.ContextMenu = w.BuildContextMenu();
        AutomationPropertiesHelper.SetName(w.Card, $"Screenshot {System.IO.Path.GetFileName(shot.Path)}. Drag to drop the file, click for actions, double-click to open.");
        host.Store.Retain(shot.Path);
        return w;
    }

    public static ThumbnailWindow ForError(IPreviewHost host, string title, string detail, PreviewOptions options)
    {
        var w = new ThumbnailWindow(host, options, TimeSpan.FromSeconds(Math.Max(6, options.Duration.TotalSeconds)), autoClose: true) { IsError = true };
        w.ErrorPanel.Visibility = Visibility.Visible;
        w.ErrorTitle.Text = title;
        w.ErrorDetail.Text = detail;
        w.ErrorDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        w.Card.Cursor = Cursors.Arrow;
        w.Picture.Visibility = Visibility.Collapsed;
        w.SetCardSize(new Size(Math.Max(options.Width, 260), 84));
        AutomationPropertiesHelper.SetName(w.Card, $"{title}. {detail}");
        return w;
    }

    private void SetCardSize(Size size)
    {
        CardSize = size;
        Width = size.Width + 2 * ShadowMargin;
        Height = size.Height + 2 * ShadowMargin;
        ApplyCornerRadius(_options.CornerRadius);
    }

    private void ApplyCornerRadius(double radius)
    {
        var r = new CornerRadius(radius);
        ShadowHost.CornerRadius = r;
        Outline.CornerRadius = r;
        Card.Clip = new RectangleGeometry(new Rect(0, 0, CardSize.Width, CardSize.Height), radius, radius);
    }

    // ---------------------------------------------------------------- window plumbing

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(hwnd, Native.GetExStyle(hwnd) | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        // Keep previews out of later screenshots (Windows 10 2004+). Harmless no-op where unsupported.
        // SNAPFLOAT_CAPTURABLE=1 turns this off for documentation screenshots and UI testing.
        if (Environment.GetEnvironmentVariable("SNAPFLOAT_CAPTURABLE") != "1")
            Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Physical-pixel size of the whole window on a monitor with the given scale.</summary>
    public (int Width, int Height) PixelSize(double scale) =>
        (ThumbnailLayout.ToPixels(Width, scale), ThumbnailLayout.ToPixels(Height, scale));

    /// <summary>Positions the window in physical pixels, handling a DPI change when it lands on another monitor.</summary>
    public void PlaceAt(int x, int y, double scale, bool animate)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var (w, h) = PixelSize(scale);
        _moveTimer?.Stop();
        if (!animate || !_options.Animations || !IsVisible)
        {
            // Twice: the first move may trigger WM_DPICHANGED, after which WPF re-sizes using its suggested rect.
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
            return;
        }

        Native.GetWindowRect(hwnd, out var from);
        var start = DateTime.UtcNow;
        const double durationMs = 180;
        _moveTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _moveTimer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / durationMs);
            var eased = 1 - Math.Pow(1 - t, 3);
            var cx = (int)Math.Round(from.Left + (x - from.Left) * eased);
            var cy = (int)Math.Round(from.Top + (y - from.Top) * eased);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, cx, cy, w, h, Native.SWP_NOACTIVATE);
            if (t >= 1) _moveTimer?.Stop();
        };
        _moveTimer.Start();
    }

    public void UpdateOptions(PreviewOptions options)
    {
        _options = options;
        AlignCloseButton();
    }

    // ---------------------------------------------------------------- appear / dismiss

    public void Present()
    {
        AlignCloseButton();
        var fromRight = _options.Corner is PreviewCorner.BottomRight or PreviewCorner.TopRight;
        if (_options.Animations)
        {
            RootShift.X = fromRight ? 28 : -28;
            RootScale.ScaleX = RootScale.ScaleY = 0.96;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(280);
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            RootShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, d) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        }
        else
        {
            Root.Opacity = 1;
        }
        ScheduleDismiss(_schedule.Start());
    }

    /// <summary>Fades the preview out and closes it. The screenshot file is kept.</summary>
    public void Dismiss(bool fast = false)
    {
        if (_closing) return;
        _closing = true;
        StopTimers();
        if (Screenshot is not null) _host.Store.Release(Screenshot.Path);
        Dismissed?.Invoke(this);

        if (!_options.Animations || !IsVisible)
        {
            Close();
            return;
        }
        var fromRight = _options.Corner is PreviewCorner.BottomRight or PreviewCorner.TopRight;
        var d = TimeSpan.FromMilliseconds(fast ? 120 : 220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, d) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        Root.IsHitTestVisible = false;
        Root.BeginAnimation(OpacityProperty, fade);
        RootShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromRight ? 16 : -16, d) { EasingFunction = ease });
    }

    private void ScheduleDismiss(TimeSpan? delay)
    {
        _dismissTimer.Stop();
        if (delay is not { } d || _closing) return;
        _dismissTimer.Interval = d;
        _dismissTimer.Start();
    }

    private void StopTimers()
    {
        _dismissTimer.Stop();
        _toolbarHideTimer.Stop();
        _statusTimer.Stop();
        _moveTimer?.Stop();
    }

    // ---------------------------------------------------------------- hover & toolbar

    private void OnHoverEnter(object sender, MouseEventArgs e)
    {
        ScheduleDismiss(_schedule.Hold(HoldReason.Hover));
        _toolbarHideTimer.Stop();
        CloseButton.Visibility = Visibility.Visible;
        Fade(CloseButton, 1, 140);
    }

    private void OnHoverLeave(object sender, MouseEventArgs e)
    {
        if (_mouseDown) return; // still pressing; mouse capture will bring us back
        ScheduleDismiss(_schedule.Release(HoldReason.Hover));
        Fade(CloseButton, 0, 160);
        if (_toolbarVisible) _toolbarHideTimer.Start();
    }

    private void SetToolbarVisible(bool visible)
    {
        if (IsError || _toolbarVisible == visible) return;
        _toolbarVisible = visible;
        if (visible)
        {
            Toolbar.Visibility = Visibility.Visible;
            if (_options.Animations)
            {
                ToolbarShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            Fade(Toolbar, 1, 140);
        }
        else
        {
            Fade(Toolbar, 0, 140, () => { if (!_toolbarVisible) Toolbar.Visibility = Visibility.Collapsed; });
        }
    }

    private void AlignCloseButton()
    {
        // Close button sits on the corner facing the screen centre so it never hugs the screen edge.
        var right = _options.Corner is PreviewCorner.BottomRight or PreviewCorner.TopRight;
        CloseButton.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        CloseButton.Margin = right ? new Thickness(-9, -9, 0, 0) : new Thickness(0, -9, -9, 0);
        PinBadge.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        PinBadge.Margin = right ? new Thickness(0, 7, 7, 0) : new Thickness(7, 7, 0, 0);
    }

    // ---------------------------------------------------------------- mouse: click / double-click / drag

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled || IsError || _closing || !IsOnCardSurface(e)) return;
        if (e.ClickCount == 2)
        {
            _mouseDown = false;
            Card.ReleaseMouseCapture();
            OpenFile();
            e.Handled = true;
            return;
        }
        _downPoint = e.GetPosition(Card);
        _mouseDown = true;
        Card.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_mouseDown) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPress(); return; }
        var p = e.GetPosition(Card);
        if (Math.Abs(p.X - _downPoint.X) < DragThreshold && Math.Abs(p.Y - _downPoint.Y) < DragThreshold) return;
        _mouseDown = false;
        Card.ReleaseMouseCapture();
        BeginFileDrag(_downPoint);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_mouseDown) return;
        EndPress();
        SetToolbarVisible(!_toolbarVisible);
        e.Handled = true;
    }

    /// <summary>
    /// Ends a press that didn't become a drag. Hover-leave is ignored while pressed, so if the pointer was released
    /// outside the preview the hover hold must be released here, or the preview would never auto-dismiss.
    /// </summary>
    private void EndPress()
    {
        _mouseDown = false;
        Card.ReleaseMouseCapture();
        if (!Root.IsMouseOver) OnHoverLeave(this, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
    }

    private bool IsOnCardSurface(MouseEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return false;
        var p = e.GetPosition(Card);
        return p.X >= 0 && p.Y >= 0 && p.X <= Card.ActualWidth && p.Y <= Card.ActualHeight;
    }

    private static bool IsInsideButton(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ButtonBase) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void BeginFileDrag(Point grabPoint)
    {
        if (Screenshot is null || !EnsureFileExists()) return;

        ScheduleDismiss(_schedule.Hold(HoldReason.Drag));
        SetToolbarVisible(false);
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0.45;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var image = DragImage.Create(Picture.Source as BitmapSource, CardSize, _options.CornerRadius, scale, grabPoint, out var imageSize, out var offset);

        FileDragSource.DragResult result;
        try
        {
            result = FileDragSource.DoFileDrag(Screenshot.Path, image, imageSize, offset);
        }
        catch (Exception ex)
        {
            Log.Error("Drag", "Drag failed", ex);
            result = default;
        }

        Root.Opacity = 1;
        if (result.Dropped && !IsPinned)
        {
            Dismiss(fast: true);
            return;
        }
        ScheduleDismiss(_schedule.Release(HoldReason.Drag));
        if (!Root.IsMouseOver) ScheduleDismiss(_schedule.Release(HoldReason.Hover));
    }

    // ---------------------------------------------------------------- actions

    private bool EnsureFileExists()
    {
        if (Screenshot is not null && System.IO.File.Exists(Screenshot.Path)) return true;
        ShowStatus("File no longer exists", error: true);
        return false;
    }

    private async void OnCopyImage(object sender, RoutedEventArgs e) => await CopyImageAsync();

    private async Task CopyImageAsync()
    {
        if (Screenshot is null || !EnsureFileExists()) return;
        var ok = await ClipboardService.CopyImageAsync(Screenshot.Path);
        ShowStatus(ok ? "Image copied" : "Clipboard busy, try again", error: !ok);
    }

    private async void OnCopyPath(object sender, RoutedEventArgs e) => await CopyPathAsync();

    private async Task CopyPathAsync()
    {
        if (Screenshot is null || !EnsureFileExists()) return;
        var ok = await ClipboardService.CopyTextAsync(Screenshot.Path);
        ShowStatus(ok ? "Path copied" : "Clipboard busy, try again", error: !ok);
    }

    private void OnOpen(object sender, RoutedEventArgs e) => OpenFile();

    private void OpenFile()
    {
        if (Screenshot is null || !EnsureFileExists()) return;
        try
        {
            Process.Start(new ProcessStartInfo(Screenshot.Path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Preview", "Open failed", ("error", ex.GetType().Name));
            ShowStatus("No app to open this image", error: true);
        }
    }

    private void OpenFolder()
    {
        if (Screenshot is null || !EnsureFileExists()) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Screenshot.Path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Preview", "Open folder failed", ("error", ex.GetType().Name));
        }
    }

    private void OnTogglePin(object sender, RoutedEventArgs e) => SetPinned(!IsPinned);

    public void SetPinned(bool pinned)
    {
        if (IsError || IsPinned == pinned) return;
        IsPinned = pinned;
        PinBadge.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        PinIcon.FillGeometry = pinned;
        PinButton.ToolTip = pinned ? "Unpin" : "Pin";
        AutomationPropertiesHelper.SetName(PinButton, pinned ? "Unpin screenshot" : "Pin screenshot");
        ScheduleDismiss(pinned ? _schedule.Hold(HoldReason.Pinned) : _schedule.Release(HoldReason.Pinned));
        if (!pinned && !Root.IsMouseOver) ScheduleDismiss(_schedule.Release(HoldReason.Hover));
        PinnedChanged?.Invoke(this);
    }

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteScreenshot();

    private void DeleteScreenshot()
    {
        if (Screenshot is null) return;
        if (_host.Store.Recycle(Screenshot.Path)) Dismiss(fast: true);
        else ShowStatus("Couldn't delete the file", error: true);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Dismiss();

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusIcon.Geometry = (Geometry)FindResource(error ? "Icon.Alert" : "Icon.Check");
        StatusIcon.SetResourceReference(ForegroundProperty, error ? "Brush.Danger" : "Brush.Success");
        Fade(StatusPill, 1, 120);
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ---------------------------------------------------------------- context menu

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Copy image", "Icon.Copy", async () => await CopyImageAsync()));
        menu.Items.Add(Item("Copy file path", "Icon.Link", async () => await CopyPathAsync()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open", "Icon.Open", OpenFile));
        menu.Items.Add(Item("Show in folder", "Icon.Folder", OpenFolder));
        menu.Items.Add(new Separator());
        var pin = Item("Pin", "Icon.Pin", () => SetPinned(!IsPinned));
        menu.Items.Add(pin);
        menu.Items.Add(Item("Move to Recycle Bin", "Icon.Trash", DeleteScreenshot));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Settings…", "Icon.Sliders", _host.OpenSettings));
        menu.Items.Add(Item("Close preview", "Icon.Close", () => Dismiss()));

        menu.Opened += (_, _) =>
        {
            pin.Header = IsPinned ? "Unpin" : "Pin";
            ScheduleDismiss(_schedule.Hold(HoldReason.Menu));
            SetToolbarVisible(false);
        };
        menu.Closed += (_, _) =>
        {
            ScheduleDismiss(_schedule.Release(HoldReason.Menu));
            if (!Root.IsMouseOver) ScheduleDismiss(_schedule.Release(HoldReason.Hover));
        };
        return menu;

        MenuItem Item(string header, string icon, Action action)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = new IconView { Geometry = (Geometry)FindResource(icon), Size = 16 },
            };
            item.Click += (_, _) => action();
            return item;
        }
    }

    // ---------------------------------------------------------------- helpers

    private void Fade(UIElement element, double to, int ms, Action? completed = null)
    {
        if (!_options.Animations)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = to;
            completed?.Invoke();
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
        if (completed is not null) anim.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, anim);
    }
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}
