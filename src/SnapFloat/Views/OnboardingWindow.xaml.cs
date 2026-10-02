using System.Windows;
using SnapFloat.Core.Input;
using SnapFloat.Services;

namespace SnapFloat.Views;

internal partial class OnboardingWindow : Window
{
    public OnboardingWindow(ThemeService theme, string regionHotkey)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
        var hk = Hotkey.TryParse(regionHotkey, out var parsed) ? parsed : Hotkey.None;
        RegionKeys.Text = hk.IsEmpty ? "your own shortcut" : hk.ToString().Replace("+", " + ");
    }

    /// <summary>True if the user chose to start with Windows.</summary>
    public bool StartWithWindows => StartupCheck.IsChecked == true;

    public bool OpenSettingsRequested { get; private set; }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        OpenSettingsRequested = true;
        Close();
    }
}
