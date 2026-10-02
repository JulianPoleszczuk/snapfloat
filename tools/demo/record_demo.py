"""
Records SnapFloat's README demo on a real Windows desktop, driving real mouse and keyboard input.

    python tools/demo/record_demo.py prep      # place the scene window and take a reference screenshot
    python tools/demo/record_demo.py record    # full take -> recording.mkv + events.json (in --out)

What it does during a take (on the configured monitor):
  minimise all windows, hide desktop icons, open the scene app window, start ffmpeg (gdigrab), then:
  click the Terminal taskbar button -> Windows Terminal opens -> type `claude` -> click the app ->
  Win+Shift+S -> select the chart -> SnapFloat preview appears -> drag it into Claude Code -> type a prompt -> Enter.
  Afterwards everything is restored (windows, desktop icons) and the demo windows are closed.

Needs: Windows 11, Windows Terminal, Chrome, Claude Code (`claude`), ffmpeg with NVENC, SnapFloat running with
SNAPFLOAT_CAPTURABLE=1 (otherwise previews are hidden from screen capture by design).
The take is environment specific: monitor and taskbar positions are set in CONFIG below.
"""
import ctypes
import ctypes.wintypes as wt
import json
import math
import os
import random
import subprocess
import sys
import time

CONFIG = dict(
    monitor=(1920, 0, 2560, 1440),          # x, y, w, h of the recorded monitor (physical pixels)
    taskbar_terminal=(3427, 1404),         # centre of the Terminal taskbar button on that monitor
    terminal_pos=(2010, 165),              # where the new Windows Terminal window opens
    terminal_size=(96, 30),                # columns, rows
    app_rect=(3190, 130, 1250, 950),       # scene window x, y, w, h
    demo_dir=os.path.expandvars(r"%USERPROFILE%\northwind-dashboard"),
    prompt="the legend covers the bars, how do I fix it?",
    chrome=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    terminal_title="PowerShell",
    app_title_part="Revenue",
)

user32 = ctypes.windll.user32
user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2: physical pixels everywhere

EVENTS = []


def log(name, **data):
    EVENTS.append(dict(name=name, t=time.time(), **data))
    print(f"{time.strftime('%H:%M:%S')} {name} {data}", flush=True)


# ----------------------------------------------------------------------------------------------- windows
EnumWindowsProc = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)


def find_window(title_part, cls=None, exact=False):
    found = []

    def cb(h, _):
        if not user32.IsWindowVisible(h):
            return True
        n = user32.GetWindowTextLengthW(h)
        buf = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(h, buf, n + 1)
        if (buf.value == title_part) if exact else (title_part in buf.value):
            if cls:
                cb_ = ctypes.create_unicode_buffer(256)
                user32.GetClassNameW(h, cb_, 256)
                if cb_.value != cls:
                    return True
            found.append(h)
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return found[0] if found else None


def window_rect(h):
    r = wt.RECT()
    ctypes.windll.dwmapi.DwmGetWindowAttribute(h, 9, ctypes.byref(r), ctypes.sizeof(r))
    return (r.left, r.top, r.right - r.left, r.bottom - r.top)


def wait_window(title_part, timeout=10, cls=None, exact=False):
    end = time.time() + timeout
    while time.time() < end:
        h = find_window(title_part, cls, exact)
        if h:
            return h
        time.sleep(0.05)
    raise RuntimeError(f"window '{title_part}' did not appear")


def close_window(h):
    if h:
        user32.PostMessageW(h, 0x10, 0, 0)


def desktop_listview():
    def child(parent, cls):
        return user32.FindWindowExW(parent, None, cls, None)
    progman = user32.FindWindowW("Progman", None)
    defview = child(progman, "SHELLDLL_DefView")
    if not defview:
        found = []

        def cb(h, _):
            dv = child(h, "SHELLDLL_DefView")
            if dv:
                found.append(dv)
            return True
        user32.EnumWindows(EnumWindowsProc(cb), 0)
        defview = found[0] if found else None
    return defview, (child(defview, "SysListView32") if defview else None)


def set_desktop_icons(visible):
    """Toggles 'Show desktop icons' only if needed. Returns True if the state was changed."""
    defview, lv = desktop_listview()
    if not lv:
        return False
    if bool(user32.IsWindowVisible(lv)) == visible:
        return False
    user32.SendMessageW(defview, 0x111, 0x7402, 0)
    return True


def shell(method):
    subprocess.run(["powershell", "-NoProfile", "-Command", f"(New-Object -ComObject Shell.Application).{method}()"], check=False)


# ----------------------------------------------------------------------------------------------- input
class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD), ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT)]
    _anonymous_ = ("u",)
    _fields_ = [("type", wt.DWORD), ("u", _U)]


def send(*inputs):
    arr = (INPUT * len(inputs))(*inputs)
    user32.SendInput(len(inputs), arr, ctypes.sizeof(INPUT))


def mouse_button(down):
    i = INPUT(type=0)
    i.mi = MOUSEINPUT(0, 0, 0, 0x2 if down else 0x4, 0, 0)
    send(i)


def key(vk, up=False):
    i = INPUT(type=1)
    i.ki = KEYBDINPUT(vk, 0, 0x2 if up else 0, 0, 0)
    send(i)


def tap(vk):
    key(vk)
    time.sleep(0.03)
    key(vk, up=True)


def chord(*vks):
    for v in vks:
        key(v)
        time.sleep(0.02)
    time.sleep(0.05)
    for v in reversed(vks):
        key(v, up=True)
        time.sleep(0.02)


def type_text(text, cps=11.0):
    for ch in text:
        down = INPUT(type=1)
        down.ki = KEYBDINPUT(0, ord(ch), 0x4, 0, 0)
        up = INPUT(type=1)
        up.ki = KEYBDINPUT(0, ord(ch), 0x4 | 0x2, 0, 0)
        send(down, up)
        pause = 1 / cps * random.uniform(0.6, 1.5)
        if ch == " ":
            pause *= 1.4
        time.sleep(pause)


def cursor():
    p = wt.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def glide(x, y, dur, arc=0.12):
    """Moves the real cursor along a gentle curve with ease-in-out timing, like a person would."""
    x0, y0 = cursor()
    dx, dy = x - x0, y - y0
    dist = math.hypot(dx, dy) or 1
    cx = x0 + dx / 2 - dy / dist * dist * arc
    cy = y0 + dy / 2 + dx / dist * dist * arc
    start = time.perf_counter()
    while True:
        t = min(1.0, (time.perf_counter() - start) / dur)
        e = 4 * t ** 3 if t < 0.5 else 1 - (-2 * t + 2) ** 3 / 2
        px = (1 - e) ** 2 * x0 + 2 * (1 - e) * e * cx + e * e * x
        py = (1 - e) ** 2 * y0 + 2 * (1 - e) * e * cy + e * e * y
        user32.SetCursorPos(int(round(px)), int(round(py)))
        if t >= 1:
            break
        time.sleep(1 / 240)


def click(name):
    x, y = cursor()
    mouse_button(True)
    time.sleep(0.07)
    mouse_button(False)
    log(name, x=x, y=y)


# ----------------------------------------------------------------------------------------------- scene
def open_app_window():
    url = "file:///" + os.path.abspath(os.path.join(os.path.dirname(__file__), "scene.html")).replace("\\", "/")
    subprocess.Popen([CONFIG["chrome"], f"--app={url}", "--new-window"])
    h = wait_window(CONFIG["app_title_part"], timeout=15)
    x, y, w, hgt = CONFIG["app_rect"]
    user32.ShowWindow(h, 9)
    # Twice: moving onto a monitor with another DPI makes Chrome rescale itself after the first move.
    for _ in range(2):
        user32.SetWindowPos(h, 0, x, y, w, hgt, 0x40)
        time.sleep(0.6)
    time.sleep(1.0)
    return h


def clean_env():
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("CLAUDE", "SNAPFLOAT"))}
    return env


def launch_terminal():
    pos = "{},{}".format(*CONFIG["terminal_pos"])
    size = "{},{}".format(*CONFIG["terminal_size"])
    subprocess.Popen(["wt.exe", "-w", "new", "--pos", pos, "--size", size, "new-tab", "--title", CONFIG["terminal_title"],
                      "--suppressApplicationTitle", "-d", CONFIG["demo_dir"], "powershell", "-NoLogo"], env=clean_env())


def screenshot(path):
    mx, my, mw, mh = CONFIG["monitor"]
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "gdigrab", "-offset_x", str(mx), "-offset_y", str(my),
                    "-video_size", f"{mw}x{mh}", "-i", "desktop", "-frames:v", "1", path], check=True)


# ----------------------------------------------------------------------------------------------- the take
def take(out_dir, select_rect):
    mx, my, mw, mh = CONFIG["monitor"]
    rec = os.path.join(out_dir, "recording.mkv")
    ff = subprocess.Popen(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                           "-f", "gdigrab", "-framerate", "60", "-offset_x", str(mx), "-offset_y", str(my),
                           "-video_size", f"{mw}x{mh}", "-draw_mouse", "1", "-use_wallclock_as_timestamps", "1", "-i", "desktop",
                           "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "constqp", "-qp", "10", "-pix_fmt", "yuv444p",
                           "-copyts", rec], stdin=subprocess.PIPE)
    time.sleep(1.5)
    log("start")
    term = None
    try:
        time.sleep(0.6)
        glide(mx + 860, my + 760, 0.9)
        time.sleep(0.25)

        # Open Windows Terminal from the taskbar.
        tx, ty = CONFIG["taskbar_terminal"]
        glide(tx, ty, 1.0, arc=0.06)
        time.sleep(0.35)
        log("click_taskbar", x=tx, y=ty)   # the window is launched directly; see module docstring
        launch_terminal()
        time.sleep(0.15)
        glide(tx - 120, ty - 260, 0.6)
        term = wait_window(CONFIG["terminal_title"], cls="CASCADIA_HOSTING_WINDOW_CLASS", exact=True)
        log("terminal_open", rect=window_rect(term))
        time.sleep(1.3)

        tr = window_rect(term)
        glide(tr[0] + int(tr[2] * 0.45), tr[1] + int(tr[3] * 0.45), 0.7)
        click("click_terminal")
        time.sleep(0.45)
        log("type_claude_start")
        type_text("claude", cps=8)
        time.sleep(0.3)
        tap(0x0D)
        log("type_claude_enter")
        time.sleep(4.2)

        # A normal click in the app window, then the native snip.
        sx0, sy0, sx1, sy1 = select_rect
        glide(sx0 + (sx1 - sx0) * 0.55, sy0 - 120, 1.0)
        click("click_app")
        time.sleep(0.6)
        log("snip_key")
        chord(0x5B, 0x10, ord("S"))
        time.sleep(1.7)
        glide(sx0, sy0, 0.7)
        time.sleep(0.15)
        mouse_button(True)
        log("select_start", x=sx0, y=sy0)
        glide(sx1, sy1, 1.05, arc=0.02)
        time.sleep(0.12)
        mouse_button(False)
        log("select_end", rect=select_rect)

        preview = wait_window("SnapFloat screenshot preview", timeout=5, exact=True)
        log("preview_shown", rect=window_rect(preview))
        time.sleep(0.8)

        # Drag the preview into Claude Code's prompt.
        px, py, pw, ph = window_rect(preview)
        gx, gy = px + pw // 2, py + ph // 2
        glide(gx, gy, 0.9)
        time.sleep(0.35)
        mouse_button(True)
        log("drag_start", x=gx, y=gy)
        time.sleep(0.05)
        tr = window_rect(term)
        dropx, dropy = tr[0] + 330, tr[1] + int(tr[3] * 0.91)
        glide(dropx, dropy, 1.4, arc=-0.18)
        time.sleep(0.3)
        mouse_button(False)
        log("drop", x=dropx, y=dropy)
        time.sleep(1.0)

        glide(dropx + 380, dropy - 220, 0.8)
        time.sleep(0.2)
        log("type_prompt_start")
        type_text(" " + CONFIG["prompt"], cps=13)
        time.sleep(0.5)
        tap(0x0D)
        log("enter")
        time.sleep(1.5)
        glide(tr[0] + tr[2] + 260, tr[1] + tr[3] - 60, 1.4)
        time.sleep(13)
        log("end")
    finally:
        try:
            ff.stdin.write(b"q")
            ff.stdin.flush()
            ff.wait(timeout=20)
        except Exception:
            ff.kill()
        close_window(term)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "prep"
    out_dir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "take")
    os.makedirs(out_dir, exist_ok=True)
    select_rect = json.loads(sys.argv[3]) if len(sys.argv) > 3 else None

    shell("MinimizeAll")
    time.sleep(0.8)
    icons_hidden = set_desktop_icons(False)
    app = None
    try:
        app = open_app_window()
        mx, my, mw, mh = CONFIG["monitor"]
        user32.SetCursorPos(mx + mw // 2 + 200, my + 640)
        if mode == "prep":
            time.sleep(0.5)
            screenshot(os.path.join(out_dir, "prep.png"))
            print("app rect", window_rect(app))
        else:
            # Warm up the snipping overlay so it opens instantly during the take.
            chord(0x5B, 0x10, ord("S"))
            time.sleep(2.5)
            tap(0x1B)
            time.sleep(1.0)
            take(out_dir, select_rect)
            with open(os.path.join(out_dir, "events.json"), "w") as f:
                json.dump(dict(config=CONFIG, events=EVENTS), f, indent=1)
    finally:
        close_window(app)
        time.sleep(0.5)
        if icons_hidden:
            set_desktop_icons(True)
        shell("UndoMinimizeALL")


if __name__ == "__main__":
    main()
