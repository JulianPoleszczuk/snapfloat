using System.Windows;
using SnapFloat.Core.Input;
using SnapFloat.Services;

namespace SnapFloat.Views;

internal partial class OnboardingWindow : Window
{
    /// <param name="startWithWindows">Initial state of the startup checkbox.</param>
    public OnboardingWindow(ThemeService theme, string regionHotkey, bool startWithWindows)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
        StartupCheck.IsChecked = startWithWindows;

        var hk = Hotkey.TryParse(regionHotkey, out var parsed) ? parsed : Hotkey.None;
        if (hk.IsEmpty)
        {
            RegionOr.Visibility = Visibility.Collapsed;
            RegionKeysCap.Visibility = Visibility.Collapsed;
        }
        else
        {
            RegionKeys.Text = hk.ToString().Replace("+", " + ");
        }
    }

    /// <summary>True if the user chose to start with Windows.</summary>
    public bool StartWithWindows => StartupCheck.IsChecked == true;

    /// <summary>
    /// True when the window was closed with one of its buttons. Closing it with the title bar's X is not a choice,
    /// so the startup setting is then left as it was.
    /// </summary>
    public bool Confirmed { get; private set; }

    public bool OpenSettingsRequested { get; private set; }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        OpenSettingsRequested = true;
        Close();
    }
}
