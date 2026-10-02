using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Services;
using SnapFloat.ViewModels;

namespace SnapFloat.Views;

internal partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly IAppController _app;

    public SettingsWindow(SettingsViewModel vm, IAppController app, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _app = app;
        DataContext = vm;
        SourceInitialized += (_, _) => theme.ApplyTitleBar(this);
        IsVisibleChanged += (_, _) => { if (IsVisible) _vm.RefreshUsage(); };
    }

    /// <summary>Set by the app on exit so closing really closes instead of hiding.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps SnapFloat running in the notification area.
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void ShowPage(string page)
    {
        var target = page switch
        {
            "capture" => NavCapture,
            "appearance" => NavAppearance,
            "storage" => NavStorage,
            "about" => NavAbout,
            _ => NavGeneral,
        };
        target.IsChecked = true;
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (PageGeneral is null) return; // during InitializeComponent
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageCapture.Visibility = NavCapture.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageStorage.Visibility = NavStorage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
        if (NavStorage.IsChecked == true) _vm.RefreshUsage();
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where SnapFloat saves screenshots",
            InitialDirectory = _vm.ScreenshotFolder,
        };
        if (dialog.ShowDialog(this) == true) _vm.SetFolder(dialog.FolderName);
    }

    private void OnDefaultFolder(object sender, RoutedEventArgs e) => _vm.SetFolder(null);

    private void OnShowPreview(object sender, RoutedEventArgs e) => _app.ShowLatestPreview();

    private void OnOpenFolder(object sender, RoutedEventArgs e) => _app.OpenScreenshotFolder();

    private void OnRefreshUsage(object sender, RoutedEventArgs e) => _vm.RefreshUsage();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            $"Permanently delete all SnapFloat screenshots in\n{_vm.ScreenshotFolder}?\n\nOnly files named SnapFloat_… are removed. Screenshots currently shown as previews are kept.",
            "Clear screenshots", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) _vm.ClearScreenshots();
    }

    private void OnLicense(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        if (File.Exists(path)) Launch(path);
        else MessageBox.Show(this, "SnapFloat is released under the MIT License.", "License");
    }

    private void OnRepository(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(AppPaths.RepositoryUrl)) Launch(AppPaths.RepositoryUrl);
    }

    private void OnLogs(object sender, RoutedEventArgs e) => _app.OpenLogFolder();

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn("Settings", "Launch failed", ("error", ex.GetType().Name));
        }
    }
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InvertBool : IValueConverter
{
    public static readonly InvertBool Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
