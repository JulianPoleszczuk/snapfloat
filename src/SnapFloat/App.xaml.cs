using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SnapFloat.Core.Diagnostics;
using SnapFloat.Core.Input;
using SnapFloat.Core.Settings;
using SnapFloat.Interop;
using SnapFloat.Previews;
using SnapFloat.Services;
using SnapFloat.Tray;
using SnapFloat.ViewModels;
using SnapFloat.Views;

namespace SnapFloat;

/// <summary>
/// Composition root. SnapFloat has no main window: it lives in the notification area, listens for screenshots
/// (clipboard + its own shortcuts), saves them, and shows floating previews.
/// </summary>
internal partial class App : Application, ITrayCommands, IAppController
{
    private const string MutexName = @"Local\SnapFloat.SingleInstance";
    private const string ActivateEventName = @"Local\SnapFloat.Activate";
    private const string ShutdownEventName = @"Local\SnapFloat.Shutdown";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent, _shutdownEvent;
    private RegisteredWaitHandle? _activateWait, _shutdownWait;

    private SettingsService _settings = null!;
    private ThemeService _theme = null!;
    private ScreenshotStore _store = null!;
    private MessageWindow _messages = null!;
    private HotkeyService _hotkeys = null!;
    private ClipboardWatcher _clipboard = null!;
    private PreviewManager _previews = null!;
    private TrayIcon _tray = null!;
    private SettingsWindow? _settingsWindow;
    private SettingsViewModel? _settingsVm;
    private DispatcherTimer? _retentionTimer;
    private readonly List<Task> _pendingSaves = [];
    private IReadOnlyDictionary<string, string> _hotkeyFailures = new Dictionary<string, string>();
    private bool _cleanedUp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();
        var background = args.Contains(StartupService.BackgroundArgument);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running: hand the request to the existing instance and quit. A background launch (the
            // sign-in entry) must stay silent, so it doesn't ask the running copy to open Settings.
            if (args.Contains("--shutdown")) SignalExisting(ShutdownEventName);
            else if (!background) SignalExisting(ActivateEventName);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }
        if (args.Contains("--shutdown"))
        {
            Shutdown(); // nothing running; used by the uninstaller
            return;
        }

        Log.Initialize(new FileLogger(AppPaths.LogDirectory));
        Log.Info("App", "Starting", ("version", AppPaths.Version), ("os", Environment.OSVersion.VersionString), ("background", background));
        try
        {
            Initialize(background);
        }
        catch (Exception ex)
        {
            // Without this the process could linger with no tray icon and no way to quit it.
            Log.Error("App", "Startup failed", ex);
            MessageBox.Show($"SnapFloat couldn't start:\n\n{ex.Message}\n\nDetails are in {AppPaths.LogDirectory}.",
                "SnapFloat", MessageBoxButton.OK, MessageBoxImage.Error);
            Cleanup();
            Shutdown(1);
        }
    }

    private void Initialize(bool background)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("App", "Unhandled exception", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("App", "Unobserved task exception", ev.Exception); ev.SetObserved(); };

        _settings = new SettingsService(new SettingsStore(AppPaths.SettingsFile));
        _theme = new ThemeService(_settings, Resources);
        _theme.Apply();
        _store = new ScreenshotStore(_settings);
        _messages = new MessageWindow();

        _hotkeys = new HotkeyService(_messages);
        _hotkeys.Pressed += OnHotkey;
        _hotkeyFailures = ApplyHotkeys();
        HotkeyBox.RecordingChanged += recording =>
        {
            // Don't fire captures while the user is typing a new shortcut.
            if (recording) _hotkeys.Apply(new Dictionary<HotkeyAction, Hotkey>());
            else ApplyHotkeys();
        };

        _clipboard = new ClipboardWatcher(_messages, () => _settings.Current.ClipboardWatchMode);
        _clipboard.ImageCaptured += image => _ = ProcessCaptureAsync(image);
        _clipboard.Start();

        _previews = new PreviewManager(_settings, _theme, _store, OpenSettings);
        _tray = new TrayIcon(this);
        _settings.Changed += _ => _theme.Apply();

        StartupService.RefreshPathIfEnabled();
        StartRetention();
        ListenForOtherInstances();
        SessionEnding += (_, _) => Cleanup();

        if (!_settings.Current.FirstRunCompleted)
        {
            ShowOnboarding();
        }
        else
        {
            if (!background && !_settings.Current.StartMinimized) OpenSettings();
            else if (!background) _tray.Notify("SnapFloat is running", "Take a screenshot with Win + Shift + S. Settings are in the notification area icon.");
            if (_hotkeyFailures.Count > 0)
                _tray.Notify("Shortcut unavailable", "Another app is using one of SnapFloat's shortcuts. Click to choose a different one.", warning: true);
        }
        MemoryTrimmer.ScheduleTrim();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    // ------------------------------------------------------------------ capture pipeline

    private void OnHotkey(HotkeyAction action)
    {
        Log.Info("App", "Shortcut pressed", ("action", action));
        switch (action)
        {
            case HotkeyAction.Region:
                CaptureRegion();
                break;
            case HotkeyAction.FullScreen:
                _ = CaptureAsync(ScreenCapture.CaptureMonitorUnderCursor, "Couldn't capture the screen", "Windows blocked the capture. Try again.");
                break;
            case HotkeyAction.Window:
                _ = CaptureAsync(ScreenCapture.CaptureForegroundWindow, "No window to capture", "Click the window you want first, then press the shortcut.");
                break;
        }
    }

    public void CaptureRegion()
    {
        if (ScreenCapture.StartRegionSnip())
        {
            _clipboard.ExpectSnip();
            return;
        }
        var c = Monitors.CursorPosition();
        _previews.ShowError("Couldn't open the snipping overlay", "Use Win + Shift + S instead.", c.X, c.Y);
    }

    public void CaptureFullScreen() => OnHotkey(HotkeyAction.FullScreen);

    private async Task CaptureAsync(Func<CapturedImage?> capture, string errorTitle, string errorDetail)
    {
        var image = await Task.Run(capture);
        if (image is null)
        {
            var c = Monitors.CursorPosition();
            _previews.ShowError(errorTitle, errorDetail, c.X, c.Y);
            return;
        }
        await ProcessCaptureAsync(image);
    }

    /// <summary>Save first; only a successfully written file gets a preview.</summary>
    private async Task ProcessCaptureAsync(CapturedImage image)
    {
        var save = _store.SaveAsync(image.Bitmap);
        lock (_pendingSaves) _pendingSaves.Add(save);
        try
        {
            var saved = await save;
            await _previews.ShowAsync(saved, image.OriginX, image.OriginY);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            Log.Error("App", "Saving screenshot failed", ex, ("source", image.Source));
            _previews.ShowError("Couldn't save screenshot", DescribeSaveError(ex), image.OriginX, image.OriginY);
        }
        finally
        {
            lock (_pendingSaves) _pendingSaves.Remove(save);
        }
    }

    private static string DescribeSaveError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "No permission to write to the screenshot folder.",
        DirectoryNotFoundException => "The screenshot folder is unavailable.",
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => "The disk is full.",
        IOException => "The file couldn't be written. Check the folder in Settings.",
        _ => "The image couldn't be encoded.",
    };

    // ------------------------------------------------------------------ hotkeys

    public IReadOnlyDictionary<string, string> ApplyHotkeys()
    {
        var s = _settings.Current;
        var map = new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.Region] = Hotkey.TryParse(s.RegionHotkey, out var r) ? r : Hotkey.None,
            [HotkeyAction.FullScreen] = Hotkey.TryParse(s.FullScreenHotkey, out var f) ? f : Hotkey.None,
            [HotkeyAction.Window] = Hotkey.TryParse(s.WindowHotkey, out var w) ? w : Hotkey.None,
        };
        var errors = new Dictionary<string, string>();
        foreach (var (action, hotkey) in _hotkeys.Apply(map))
        {
            var name = action switch
            {
                HotkeyAction.Region => nameof(AppSettings.RegionHotkey),
                HotkeyAction.FullScreen => nameof(AppSettings.FullScreenHotkey),
                _ => nameof(AppSettings.WindowHotkey),
            };
            errors[name] = $"{hotkey} is already used by another app. Choose a different shortcut.";
        }
        _hotkeyFailures = errors;
        return errors;
    }

    // ------------------------------------------------------------------ windows

    public void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsVm = new SettingsViewModel(_settings, this);
            _settingsVm.ShowHotkeyFailures(_hotkeyFailures);
            _settingsWindow = new SettingsWindow(_settingsVm, this, _theme);
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ShowOnboarding()
    {
        var onboarding = new OnboardingWindow(_theme, _settings.Current.RegionHotkey);
        onboarding.Closed += (_, _) =>
        {
            var start = onboarding.StartWithWindows;
            StartupService.SetEnabled(start);
            _settings.Update(s =>
            {
                s.FirstRunCompleted = true;
                s.LaunchAtStartup = start;
            });
            Log.Info("App", "Onboarding completed", ("startWithWindows", start));
            if (onboarding.OpenSettingsRequested) OpenSettings();
        };
        onboarding.Show();
        onboarding.Activate();
    }

    // ------------------------------------------------------------------ tray / controller commands

    public bool PreviewsPaused
    {
        get => _previews.Paused;
        set
        {
            _previews.Paused = value;
            _tray.SetPaused(value);
            Log.Info("App", value ? "Previews paused" : "Previews resumed");
        }
    }

    public string RegionShortcut => _settings.Current.RegionHotkey;
    public string FullScreenShortcut => _settings.Current.FullScreenHotkey;
    public ScreenshotStore Store => _store;

    public IReadOnlyList<FileInfo> RecentScreenshots(int count) => _store.Recent(count);

    public void ShowRecent(string path) => _ = _previews.ShowExistingAsync(path);

    public void ShowLatestPreview()
    {
        var latest = _store.Recent(1);
        if (latest.Count > 0)
        {
            _ = _previews.ShowExistingAsync(latest[0].FullName);
            return;
        }
        var c = Monitors.CursorPosition();
        _previews.ShowError("No screenshots yet", "Press Win + Shift + S to take one.", c.X, c.Y);
    }

    public void OpenScreenshotFolder() => OpenFolder(_store.Directory);

    public void OpenLogFolder() => OpenFolder(AppPaths.LogDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("App", "Could not open folder", ("error", ex.GetType().Name));
        }
    }

    public void ExitApp()
    {
        Log.Info("App", "Exit requested");
        Cleanup();
        Shutdown();
    }

    // ------------------------------------------------------------------ lifecycle helpers

    private void StartRetention()
    {
        _ = Task.Run(_store.ApplyRetention);
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _retentionTimer.Tick += (_, _) => _ = Task.Run(_store.ApplyRetention);
        _retentionTimer.Start();
    }

    private void ListenForOtherInstances()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateEvent, (_, _) => Dispatcher.BeginInvoke(OpenSettings), null, Timeout.Infinite, false);
        _shutdownWait = ThreadPool.RegisterWaitForSingleObject(_shutdownEvent, (_, _) => Dispatcher.BeginInvoke(ExitApp), null, Timeout.Infinite, true);
    }

    private static void SignalExisting(string eventName)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(eventName);
            handle.Set();
            if (eventName == ShutdownEventName)
            {
                // Give the running instance a moment to exit (the uninstaller waits on this process).
                using var mutex = new Mutex(false, MutexName);
                try { if (mutex.WaitOne(TimeSpan.FromSeconds(5))) mutex.ReleaseMutex(); }
                catch (AbandonedMutexException) { }
            }
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("App", "Unhandled UI exception", e.Exception);
        e.Handled = true; // keep the background utility alive; the failure is logged
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        try
        {
            Task[] pending;
            lock (_pendingSaves) pending = _pendingSaves.ToArray();
            if (pending.Length > 0)
            {
                Log.Info("App", "Waiting for screenshots to finish saving", ("count", pending.Length));
                try { Task.WaitAll(pending, TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
            }
            _settings?.Flush();
            _retentionTimer?.Stop();
            _activateWait?.Unregister(null);
            _shutdownWait?.Unregister(null);
            _previews?.Dispose();
            _clipboard?.Dispose();
            _hotkeys?.Dispose();
            _messages?.Dispose();
            _tray?.Dispose();
            _theme?.Dispose();
            if (_settingsWindow is not null)
            {
                _settingsWindow.AllowClose = true;
                _settingsWindow.Close();
            }
            _activateEvent?.Dispose();
            _shutdownEvent?.Dispose();
            Log.Info("App", "Stopped");
        }
        finally
        {
            if (_mutex is not null)
            {
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
                _mutex.Dispose();
                _mutex = null;
            }
        }
    }
}
