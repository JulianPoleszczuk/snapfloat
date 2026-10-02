using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using SnapFloat.Core.Settings;
using SnapFloat.Interop;

namespace SnapFloat.Services;

internal enum EffectiveTheme { Light, Dark, HighContrast }

/// <summary>Resolves System/Light/Dark (and Windows high contrast) and swaps the app's colour dictionary at runtime.</summary>
internal sealed class ThemeService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ResourceDictionary _host;
    private ResourceDictionary? _current;

    public ThemeService(SettingsService settings, ResourceDictionary appResources)
    {
        _settings = settings;
        _host = appResources;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public EffectiveTheme Effective { get; private set; }

    public event Action? ThemeChanged;

    /// <summary>True when motion should play: user setting AND Windows "Animation effects".</summary>
    public bool AnimationsEnabled => _settings.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation;

    public void Apply()
    {
        var effective = Resolve(_settings.Current.Theme);
        if (_current is not null && effective == Effective) return;
        Effective = effective;

        var uri = effective switch
        {
            EffectiveTheme.Dark => "Themes/Dark.xaml",
            EffectiveTheme.HighContrast => "Themes/HighContrast.xaml",
            _ => "Themes/Light.xaml",
        };
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SnapFloat;component/{uri}") };
        // Slot 0 holds the colour theme (App.xaml seeds it with Light). Replace in place: later merged
        // dictionaries win lookups, so inserting in front of the seed would leave the seed in effect.
        _host.MergedDictionaries[0] = dict;
        _current = dict;

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
        ThemeChanged?.Invoke();
    }

    public static EffectiveTheme Resolve(ThemePreference preference)
    {
        if (SystemParameters.HighContrast) return EffectiveTheme.HighContrast;
        return preference switch
        {
            ThemePreference.Light => EffectiveTheme.Light,
            ThemePreference.Dark => EffectiveTheme.Dark,
            _ => SystemUsesLightTheme() ? EffectiveTheme.Light : EffectiveTheme.Dark,
        };
    }

    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>Matches the native title bar to the theme (dark title bar on Windows 10 20H1+ and 11).</summary>
    public void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = Effective == EffectiveTheme.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
            Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }
}
