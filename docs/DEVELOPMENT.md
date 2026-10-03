# Developing SnapFloat

Everything technical about SnapFloat lives here. The user-facing overview is in the [README](../README.md).

## Build from source

Requirements: Windows 10 1809+ / 11 and the .NET 8 SDK.

```powershell
dotnet build SnapFloat.sln                    # debug build
dotnet test  tests\SnapFloat.Core.Tests       # unit tests
powershell -ExecutionPolicy Bypass -File build.ps1   # tests + publish + portable zip + installer
```

`build.ps1` writes the files below to `artifacts\`. It downloads Inno Setup 6.4.3 from NuGet into `tools\.cache`
automatically, so you don't need to install it system-wide.

- `publish\`: self-contained win-x64 build (ReadyToRun)
- `SnapFloat-1.0.0-win-x64-portable.zip`
- `SnapFloat-Setup-1.0.0.exe`

**Releasing:** push a tag such as `v1.0.1`. The GitHub Actions workflow (`.github/workflows/build.yml`) runs the
tests, builds the installer and portable zip with that version number, and attaches them to a GitHub Release.
Every push and pull request runs the same build without publishing.

**Demo video:** the README clip is a real recording. `tools/demo/record_demo.py` drives a take on a real desktop
(Terminal, Claude Code, Win+Shift+S, drag and drop) while ffmpeg records it, and `tools/demo/edit_demo.py` adds the
camera zooms, framing and click ripples and writes `docs/media/demo.mp4` and `demo.webp`. The full-quality video is
[docs/media/demo.mp4](media/demo.mp4).

Developer switches:

- `SnapFloat.exe --background`: start without opening Settings (used by the startup entry).
- `SnapFloat.exe --shutdown`: ask a running instance to exit cleanly (used by the installer and uninstaller).
- Launching a second time opens the running instance's Settings.
- `SNAPFLOAT_CAPTURABLE=1`: let previews appear in screen captures. Normally they are excluded so they never end up
  in your next screenshot. This switch exists for documentation and UI testing.

Regenerate the icon with `dotnet run --project tools/IconGen -- src/SnapFloat/Assets`.

## Architecture

```
src/SnapFloat.Core      net8.0, no UI. Pure logic, fully unit-tested
  Settings/             AppSettings model, JSON store (atomic writes, corrupt-file quarantine), validation
  Input/Hotkey          parse/format shortcuts ("Ctrl+Shift+4") ↔ modifiers + virtual key
  Storage/              file naming, "is this ours?" rule, retention policy, byte formatting
  Layout/               thumbnail sizing and corner/stack placement in physical pixels
  Previews/             DismissSchedule: auto-dismiss state machine (hover/drag/menu/pin holds)
  Capture/              ClipboardSourcePolicy: which clipboard images count as screenshots
  Diagnostics/          small structured file logger with rotation
src/SnapFloat           net8.0-windows, WPF app
  App.xaml.cs           composition root; single instance; capture → save → preview pipeline
  Interop/              Win32 P/Invoke, monitors, message-only window, OLE file drag source
  Services/             hotkeys, clipboard watcher, screen capture, storage, clipboard out, theme, startup
  Previews/             floating ThumbnailWindow, PreviewManager (replace/stack/multi-monitor), drag image
  Views/, ViewModels/   settings window, onboarding, custom controls (IconView, HotkeyBox)
  Tray/                 notification-area icon with a themed WPF menu
  Themes/               Light / Dark / HighContrast palettes, icon geometry, control styles
tests/SnapFloat.Core.Tests  xUnit (79 tests)
installer/SnapFloat.iss     Inno Setup script (per-user, no admin)
docs/DESIGN.md              visual design specification
docs/media/                 README demo video (recorded and edited with tools/demo)
.github/workflows/build.yml CI build, tests and tag-based releases
```

Key decisions:

- **Integrating with `Win + Shift + S` instead of reimplementing it.** That shortcut belongs to Windows and is never
  registered by SnapFloat. SnapFloat listens with `AddClipboardFormatListener`, which is event-driven with no polling.
  When the clipboard owner is Snipping Tool / ScreenClippingHost (or there is no owner, as with classic Print Screen),
  SnapFloat reads the bitmap, preferring the lossless `PNG` clipboard format, saves it, and only then shows the preview.
  SnapFloat's own region shortcut opens the same native overlay through `ms-screenclip:`.
- **Drag-and-drop uses the Shell's own data object** (`IShellItem` → `BHID_DataObject`), passed to `ole32!DoDragDrop`
  with a custom `IDropSource`, plus a rendered drag image via `IDragSourceHelper`. That's byte-for-byte what Explorer
  offers, so every target that accepts a file from Explorer accepts SnapFloat's. Only `DROPEFFECT_COPY` is offered,
  so a drop can never move the screenshot out of its folder.
- **Previews never steal focus.** They use `WS_EX_NOACTIVATE` and `MA_NOACTIVATE`, are topmost, and are excluded from
  capture with `WDA_EXCLUDEFROMCAPTURE`. Previews are positioned in physical pixels per monitor
  (per-monitor-v2 DPI aware), inside the work area so the taskbar is respected.
- **Clipboard output** sets CF_BITMAP/CF_DIB, `PNG` and CF_HDROP, and retries when another app holds the clipboard.
- **Footprint:** idle CPU is 0 (no timers except a 6-hourly retention sweep). After previews close, memory is
  compacted and the working set is trimmed.

Logs are written to `%LOCALAPPDATA%\SnapFloat\logs`, one file per day, capped at 1 MB each, with the last 7 kept.
They contain events, sizes and file names only, never image data or clipboard contents.

## Testing

### Automated

`dotnet test` runs 79 xUnit tests. All passed at the time of writing. They cover:

- shortcut parsing, formatting and validation, including reserved keys, duplicates and AltGr warnings
- settings persistence: round trip, corrupt file, clamping, enum fallback, invalid paths
- file naming and collisions
- the "managed file" rule and retention selection
- layout across multiple monitors, negative coordinates and DPI scales
- the dismiss state machine
- the clipboard source policy
- logger formatting and rotation

### Manual verification

Run on Windows 11 (build 26200) with two monitors: 1920×1080 at 125 % and 2560×1440 at 150 %.

| # | Check | Result |
|---|---|---|
| 1 | App launches; first-run onboarding shows | ✅ Passed |
| 2 | Region screenshot through the native snipping overlay (`Ctrl+Shift+4` → `ms-screenclip:`) | ✅ Passed: 800×498 PNG saved, preview shown |
| 2b | Literal `Win + Shift + S` keypress | ⚠️ Not verified. A synthetic keypress started a cold Snipping Tool, but the scripted drag missed the overlay. Uses the same overlay and clipboard path as 2. |
| 3 | Thumbnail in the correct corner of the correct monitor, above the taskbar | ✅ Passed on both monitors / both DPI scales |
| 4/5 | Drag into File Explorer, correct PNG copied | ⚠️ Not completed. The automated Explorer target window never painted during the test. Needs a manual check. |
| 4b | Drag into Chrome | ✅ Passed: Chrome received the file and opened the PNG |
| 6 | Drag into Windows Terminal running Claude Code | ✅ Passed: the file arrives as `[Image #1]` in Claude Code's prompt (also recorded in the demo video) |
| 7 | Copy image / copy path | ✅ Passed: Bitmap + PNG + FileDrop formats; `Get-Clipboard -Format Image` returns the full image; path text correct |
| 8 | Preview disappears after the timeout | ✅ Passed |
| 9 | Dragging pauses the timeout | ✅ Covered by unit tests and the drag code path. Not timed manually beyond the 5 s window. |
| 10 | Rapid screenshots | ✅ Passed: 4 files saved, 1 preview (replaced) |
| 11 | Multi-monitor positioning | ✅ Passed |
| 12 | Light / dark themes, live switching | ✅ Passed |
| 13 | Start after restarting Windows | ⚠️ Not executed (no reboot). The installer's Run-key entry was created, and the app was launched with the same command: started in about 0.8 s, silently, in the tray. |
| 14 | Uninstall | ✅ Silent uninstall stopped the app and removed the files and startup entry. ⚠️ The interactive "remove my data?" prompt was not exercised. |
| 15 | Tray menu opens, *Pause previews* works | ✅ Passed |
| 16 | Pin + stacking | ✅ Passed |
| 17 | Active-window capture | ✅ Passed after a fix (it previously could capture SnapFloat's own preview) |
| 18 | Idle CPU / memory | 0.000 CPU-s over 10 s idle; about 59 MB private |
| 19 | Settings changed right before exit are saved | ✅ Passed (debounced write is flushed on exit) |
| 20 | Second launch with `--background` while running | ✅ Passed: stays silent, single process |

## Known limitations

- **Elevated (administrator) terminals reject the drop.** Windows UIPI blocks drag-and-drop from a normal-integrity
  app into an elevated one. Use *Copy image* (`Alt + V` in Claude Code) or *Copy file path* instead. Don't run
  SnapFloat itself as administrator: it would then be unable to drop into normal apps.
- **WSL / SSH sessions** receive a Windows path (`C:\Users\…`). Claude Code running inside WSL needs the `/mnt/c/…`
  form, so paste the path and adjust it, or use the clipboard.
- **Targets that don't accept file drops** won't work; nothing SnapFloat can do changes that. VS Code's editor opens
  dropped files, while its terminal and chat panels handle drops according to VS Code's own settings. That wasn't
  tested here.
- **Monitor for `Win + Shift + S`:** Windows doesn't say where a snip was taken, so the preview goes to the monitor
  under the mouse pointer when the snip finishes. That is almost always the right one.
- **Full-screen and window capture use GDI.** On HDR displays colours can look washed out, and window capture copies
  what's on screen, including anything overlapping the window. Region snips (`Win + Shift + S`) are unaffected.
- **Snipping Tool must copy to the clipboard.** That is the Windows default. If its auto-copy is turned off,
  SnapFloat won't see Snipping Tool snips.
- **Previews are excluded from screen captures** on Windows 10 2004+. On older builds they may appear in later
  screenshots.
- **Opening the tray menu from the keyboard** (`Win + B`) shows it at the mouse pointer, not at the icon.
- **Not code-signed and no auto-update.** Check the [releases](https://github.com/JulianPoleszczuk/snapfloat/releases) page for new versions.

## Settings file

Settings are saved to `%LOCALAPPDATA%\SnapFloat\settings.json` shortly after every change. Screenshots go to the
Windows Screenshots folder (`Pictures\Screenshots`, resolved through the known-folder API so OneDrive redirection is
honoured). If that folder can't be written, SnapFloat falls back to `%LOCALAPPDATA%\SnapFloat\Screenshots`, shows a
tray notification, and lists those files under *Recent screenshots*. The uninstaller's "remove settings and logs"
option never deletes that folder.

Snipping Tool saves its own copy of every snip to `Pictures\Screenshots` when its automatic saving is on (the
default). For snips from Snipping Tool / `Win + Shift + S`, SnapFloat waits up to about 4 s for that file, matches it
by a SHA-256 of the pixels, switches the preview to it and deletes its own `SnapFloat_…` copy. The copy is kept if it
was already dragged, copied or opened, because another app may refer to its path. This only happens while
SnapFloat uses the default folder.

Global shortcuts are off by default (`"None"`): a combination registered with `RegisterHotKey` stops working in every
other app. Settings from v2 that still had the old defaults (`Ctrl+Shift+4/3/5`) are migrated to `"None"`; shortcuts
the user changed are kept.

The installer offers "start with Windows" only on the first install. On upgrades the app's own setting is kept. The
welcome window's checkbox starts from what the installer set (the portable copy suggests "on"). Closing the welcome
window with its X leaves the startup entry unchanged.
Automatic cleanup only ever touches files named `SnapFloat_YYYY-MM-DD_HH-mm-ss_fff.png` and never files that are
currently shown as previews.
