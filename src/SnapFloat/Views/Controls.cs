using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnapFloat.Core.Input;

namespace SnapFloat.Views;

/// <summary>Stroke icon drawn from a 24×24 geometry (see Themes/Icons.xaml). Foreground sets the stroke colour.</summary>
public sealed class IconView : Control
{
    public static readonly DependencyProperty GeometryProperty =
        DependencyProperty.Register(nameof(Geometry), typeof(Geometry), typeof(IconView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(IconView), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FillGeometryProperty =
        DependencyProperty.Register(nameof(FillGeometry), typeof(bool), typeof(IconView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    static IconView()
    {
        FocusableProperty.OverrideMetadata(typeof(IconView), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(IconView), new FrameworkPropertyMetadata(false));
    }

    public Geometry? Geometry { get => (Geometry?)GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>Fills the shape as well as stroking it (used for "active" states such as a pinned preview).</summary>
    public bool FillGeometry { get => (bool)GetValue(FillGeometryProperty); set => SetValue(FillGeometryProperty, value); }

    protected override Size MeasureOverride(Size constraint) => new(Size, Size);
    protected override Size ArrangeOverride(Size arrangeBounds) => new(Size, Size);

    protected override void OnRender(DrawingContext dc)
    {
        if (Geometry is null) return;
        var scale = Size / 24.0;
        var pen = new Pen(Foreground, 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(FillGeometry ? Foreground : null, pen, Geometry);
        dc.Pop();
    }
}

/// <summary>
/// Read-only box that records a shortcut when focused: press the keys, Backspace/Delete clears, Esc cancels.
/// Raises <see cref="HotkeyChanged"/> with the canonical text (e.g. "Ctrl+Shift+4").
/// </summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyTextProperty =
        DependencyProperty.Register(nameof(HotkeyText), typeof(string), typeof(HotkeyBox),
            new FrameworkPropertyMetadata("None", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).Render()));

    public static readonly DependencyProperty IsRecordingProperty =
        DependencyProperty.Register(nameof(IsRecording), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        Cursor = Cursors.Arrow;
        Render();
    }

    /// <summary>Raised when recording starts/stops so global shortcuts can be paused and don't fire while typing.</summary>
    public static event Action<bool>? RecordingChanged;

    public string HotkeyText { get => (string)GetValue(HotkeyTextProperty); set => SetValue(HotkeyTextProperty, value); }
    public bool IsRecording { get => (bool)GetValue(IsRecordingProperty); private set => SetValue(IsRecordingProperty, value); }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        IsRecording = true;
        RecordingChanged?.Invoke(true);
        Text = "Press a shortcut…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        IsRecording = false;
        RecordingChanged?.Invoke(false);
        Render();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            base.OnPreviewKeyDown(e); // keep keyboard navigation working
            return;
        }
        e.Handled = true;

        if (key == Key.Escape) { MoveFocusAway(); return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            HotkeyText = Hotkey.None.ToString();
            MoveFocusAway();
            return;
        }

        var vk = KeyInterop.VirtualKeyFromKey(key);
        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;

        if (Hotkey.IsModifierKey(vk))
        {
            Text = mods == HotkeyModifiers.None ? "Press a shortcut…" : Describe(mods) + "…";
            return;
        }
        if (!Hotkey.IsSupportedKey(vk)) return;

        HotkeyText = new Hotkey(mods, vk).ToString();
        MoveFocusAway();
    }

    private static string Describe(HotkeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        return string.Join(" + ", parts) + (parts.Count > 0 ? " + " : string.Empty);
    }

    private void MoveFocusAway()
    {
        var scope = FocusManager.GetFocusScope(this);
        FocusManager.SetFocusedElement(scope, null);
        Keyboard.ClearFocus();
        Render();
    }

    private void Render()
    {
        if (IsRecording) return;
        Text = Hotkey.TryParse(HotkeyText, out var hk) && !hk.IsEmpty ? hk.ToString().Replace("+", " + ") : "Not set";
    }
}
