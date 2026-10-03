<p align="center">
  <img src="src/SnapFloat/Assets/SnapFloat-256.png" width="96" alt="SnapFloat icon">
</p>

<h1 align="center">SnapFloat</h1>

<p align="center">
  <b>Screenshots you can drag straight into Claude Code.</b><br>
  A tiny Windows app that shows a floating preview after every screenshot,<br>
  ready to drop into your terminal, editor or chat.
</p>

<p align="center">
  <a href="../../releases/latest"><b>Download for Windows</b></a>
  &nbsp;·&nbsp;
  <a href="#how-to-use">How to use</a>
  &nbsp;·&nbsp;
  <a href="#faq">FAQ</a>
</p>

<p align="center">
  <img src="docs/media/demo.webp" width="900" alt="A chart is snipped with Win + Shift + S, the SnapFloat preview appears in the corner and is dragged into Claude Code, where it becomes [Image #1] in the prompt.">
</p>

## Why

Taking a screenshot is easy. Getting it into Claude Code is not: you open the Screenshots folder, look for the right
file and drag it over. SnapFloat skips all of that. Snip like you always do, and the screenshot floats in the corner of
your screen, ready to drag wherever you need it.

## Features

- **Works with <kbd>Win</kbd> + <kbd>Shift</kbd> + <kbd>S</kbd>.** Nothing new to learn. The Windows snipping tool
  keeps working exactly as before.
- **Drag and drop anywhere.** Claude Code, Windows Terminal, VS Code, your browser, Slack, Discord or File Explorer.
- **Quick actions.** Click the preview to copy the image, copy its path, open, pin or delete it.
- **Stays out of your way.** Lives next to the clock, never steals focus and fades out after a few seconds.
- **Feels at home on Windows.** Light and dark mode, multiple monitors and any display scaling.
- **Keeps your screenshots.** Saved as PNG in your usual `Pictures\Screenshots` folder.

## Install

1. Download the **SnapFloat-Setup** installer from the [latest release](../../releases/latest).
2. Run it. No administrator rights needed.
3. If Windows says *"Windows protected your PC"*, click **More info → Run anyway**. The app isn't code-signed yet.

That's it. SnapFloat starts with Windows and waits quietly next to the clock.

Rather not install anything? Grab the **portable zip** from the same page, unzip it anywhere and run `SnapFloat.exe`.

Works on Windows 10 and 11 (64-bit).

## How to use

1. Take a screenshot with <kbd>Win</kbd> + <kbd>Shift</kbd> + <kbd>S</kbd>.
2. A preview appears in the corner of your screen.
3. Drag it into Claude Code. It shows up as `[Image #1]` in your prompt.

| On the preview | What it does |
|---|---|
| **Drag** | Drops the image into any app |
| **Click** | Copy image, copy path, open, pin or delete |
| **Double-click** | Opens the image |
| **Right-click** | More options |

| Shortcut | What it does |
|---|---|
| <kbd>Win</kbd> + <kbd>Shift</kbd> + <kbd>S</kbd> | Snip with Windows, as usual |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>4</kbd> | Snip a region |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>3</kbd> | Capture the whole screen |
| <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>5</kbd> | Capture the active window |

You can change the shortcuts, how long previews stay, which corner they appear in, the theme and where screenshots are
saved. Open **Settings** from the SnapFloat icon next to the clock.

## FAQ

<details>
<summary><b>Dropping into my terminal doesn't work</b></summary>
<br>
Your terminal is probably running as administrator, and Windows blocks dragging from normal apps into admin windows.
Click the preview, choose <b>Copy image</b>, then paste into Claude Code with <kbd>Alt</kbd> + <kbd>V</kbd>.
</details>

<details>
<summary><b>Does it work with Claude Code in WSL?</b></summary>
<br>
Dropping pastes a Windows path, which WSL can't open directly. Use <b>Copy image</b> and paste with
<kbd>Alt</kbd> + <kbd>V</kbd> instead.
</details>

<details>
<summary><b>Where are my screenshots?</b></summary>
<br>
In <code>Pictures\Screenshots</code>. Right-click any preview and choose <b>Show in folder</b>.
</details>

<details>
<summary><b>Will SnapFloat delete my screenshots?</b></summary>
<br>
No. Screenshots are kept forever unless you turn on cleanup in Settings, and even then SnapFloat only removes the files
it created itself.
</details>

<details>
<summary><b>How do I uninstall it?</b></summary>
<br>
Windows Settings → Apps → Installed apps → SnapFloat → Uninstall. Your screenshots stay where they are.
</details>

## For developers

SnapFloat is a native C# / .NET 8 app. To build it yourself, install the .NET 8 SDK and run `build.ps1`. Architecture,
tests and the release process are described in [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## License

[MIT](LICENSE)
