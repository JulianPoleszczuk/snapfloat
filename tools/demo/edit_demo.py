"""
Turns the raw take from record_demo.py into the README demo: smooth camera zooms that follow the action, a framed and
shadowed screen on a soft background, click ripples, time-ramped waits and a seamless fade for looping.

    python tools/demo/edit_demo.py <take_dir> [output_dir]

Writes demo.mp4 (2560x1440, 60 fps), demo.webp (1280 px, for the README) and demo-poster.png into output_dir.
"""
import json
import math
import os
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT_W, OUT_H = 2560, 1440
FPS = 60
SRC_W, SRC_H = 2560, 1440
MON_X = 1920                     # left edge of the recorded monitor in desktop coordinates
INSET_MARGIN = (104, 58)         # screen frame margins on the canvas
RADIUS = 22
FADE = 0.45


# ----------------------------------------------------------------------------------------------- helpers
def smooth(x):
    x = max(0.0, min(1.0, x))
    return x * x * x * (x * (6 * x - 15) + 10)   # smootherstep: gentle start and settle


def ffprobe_start(path):
    out = subprocess.check_output(["ffprobe", "-v", "error", "-show_entries", "format=start_time", "-of", "default=nw=1:nk=1", path])
    return float(out.decode().strip())


def background():
    """Deep blue-charcoal gradient with a soft glow, matched to the Windows 11 'Bloom' wallpaper."""
    y, x = np.mgrid[0:OUT_H, 0:OUT_W].astype(np.float32)
    t = (x / OUT_W) * 0.4 + (y / OUT_H) * 0.6
    top = np.array([24, 30, 48], np.float32)
    bottom = np.array([8, 10, 18], np.float32)
    img = top * (1 - t[..., None]) + bottom * t[..., None]
    glow = np.exp(-(((x - OUT_W * 0.32) / (OUT_W * 0.45)) ** 2 + ((y - OUT_H * 0.18) / (OUT_H * 0.55)) ** 2))
    img += glow[..., None] * np.array([30, 44, 96], np.float32) * 0.55
    noise = np.random.default_rng(7).normal(0, 1.4, img.shape[:2]).astype(np.float32)
    img += noise[..., None]
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB")


def frame_layers():
    iw, ih = OUT_W - 2 * INSET_MARGIN[0], OUT_H - 2 * INSET_MARGIN[1]
    mask = Image.new("L", (iw, ih), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, iw - 1, ih - 1), radius=RADIUS, fill=255)
    pad = 120
    sh = Image.new("L", (iw + 2 * pad, ih + 2 * pad), 0)
    ImageDraw.Draw(sh).rounded_rectangle((pad, pad + 24, pad + iw, pad + ih + 24), radius=RADIUS, fill=170)
    sh = sh.filter(ImageFilter.GaussianBlur(46))
    base = background().convert("RGBA")
    shadow = Image.new("RGBA", sh.size, (0, 0, 0, 0))
    shadow.putalpha(sh)
    base.alpha_composite(shadow, (INSET_MARGIN[0] - pad, INSET_MARGIN[1] - pad))
    border = Image.new("RGBA", (iw, ih), (0, 0, 0, 0))
    ImageDraw.Draw(border).rounded_rectangle((0, 0, iw - 1, ih - 1), radius=RADIUS, outline=(255, 255, 255, 34), width=2)
    return base.convert("RGB"), mask, border, (iw, ih)


# ----------------------------------------------------------------------------------------------- edit decision list
def build_plan(events, start_time):
    ev = {}
    for e in events:
        ev.setdefault(e["name"], e)
    T = {k: v["t"] - start_time for k, v in ev.items()}

    def m(x, y):   # desktop -> monitor coordinates
        return x - MON_X, y

    term = ev["terminal_open"]["rect"]
    tx, ty, tw, th = term[0] - MON_X, term[1], term[2], term[3]
    sel = ev["select_end"]["rect"]
    sx0, sy0, sx1, sy1 = sel[0] - MON_X, sel[1], sel[2] - MON_X, sel[3]
    pv = ev["preview_shown"]["rect"]
    px, py, pw, ph = pv[0] - MON_X, pv[1], pv[2], pv[3]
    drop = m(ev["drop"]["x"], ev["drop"]["y"])

    # Time ramp: (source_start, source_end, speed). Dead time plays faster.
    t0 = T["click_taskbar"] - 2.4
    ramp = [
        (t0, T["click_taskbar"] + 0.2, 1.15),
        (T["click_taskbar"] + 0.2, T["type_claude_enter"] + 0.6, 1.0),
        (T["type_claude_enter"] + 0.6, T["click_app"] - 0.8, 2.6),
        (T["click_app"] - 0.8, T["snip_key"] + 0.2, 1.0),
        (T["snip_key"] + 0.2, T["select_start"] - 0.4, 1.6),
        (T["select_start"] - 0.4, T["drop"] + 1.1, 1.0),
        (T["drop"] + 1.1, T["type_prompt_start"], 1.4),
        (T["type_prompt_start"], T["enter"] - 0.2, 1.7),
        (T["enter"] - 0.2, T["enter"] + 4.2, 1.0),
    ]

    # Camera targets: (source_time, centre_x, centre_y, zoom, transition_seconds), monitor coordinates.
    full = (SRC_W / 2, SRC_H / 2, 1.0)
    tc = (tx + tw / 2, ty + th / 2)
    cams = [
        (t0, *full, 0.1),
        (T["click_taskbar"] - 1.1, SRC_W * 0.56, SRC_H * 0.62, 1.12, 1.0),
        (T["terminal_open"] + 0.15, tc[0] + 40, tc[1], 1.42, 1.1),
        (T["type_claude_enter"] + 1.0, tc[0] + 40, tc[1] - 60, 1.32, 1.4),
        (T["click_app"] - 1.0, *full, 1.1),
        (T["select_start"] - 0.3, (sx0 + sx1) / 2, (sy0 + sy1) / 2 + 20, 1.55, 1.0),
        (T["preview_shown"] - 0.05, (sx0 + px + pw) / 2 + 40, (sy0 + py + ph) / 2 + 40, 1.32, 0.9),
        (T["drag_start"] - 0.25, (drop[0] + px + pw / 2) / 2, (drop[1] + py) / 2 - 40, 1.12, 0.9),
        (T["drop"] + 0.05, drop[0] + 260, drop[1] - 60, 1.85, 0.9),
        (T["type_prompt_start"] + 0.4, tx + 640, drop[1] - 90, 1.6, 1.2),
        (T["enter"] + 0.5, tc[0] + 20, ty + th * 0.36, 1.55, 1.1),
        (T["enter"] + 3.0, *full, 1.3),
    ]

    clicks = []
    for name in ("click_taskbar", "click_terminal", "click_app", "select_start", "drag_start", "drop"):
        e = ev[name]
        clicks.append((T[name], *m(e["x"], e["y"])))
    return ramp, cams, clicks


def output_to_source(ramp):
    """Builds a function mapping output seconds -> source seconds, plus the total output duration."""
    segs, acc = [], 0.0
    for a, b, speed in ramp:
        if b <= a:
            continue
        dur = (b - a) / speed
        segs.append((acc, acc + dur, a, speed))
        acc += dur

    def f(t):
        for o0, o1, s0, sp in segs:
            if t <= o1:
                return s0 + (t - o0) * sp
        o0, o1, s0, sp = segs[-1]
        return s0 + (o1 - o0) * sp
    return f, acc


def camera_at(cams, t):
    """Eased transitions between camera targets; zoom is interpolated in log space so it feels linear."""
    state = cams[0][1:4]
    for (ts, cx, cy, z, dur) in cams:
        if t < ts:
            break
        k = smooth((t - ts) / dur)
        prev = state
        # Overlapping transitions chain smoothly: each one starts from wherever the previous one is.
        state = (prev[0] + (cx - prev[0]) * k, prev[1] + (cy - prev[1]) * k,
                 math.exp(math.log(prev[2]) + (math.log(z) - math.log(prev[2])) * k))
    return state


def crop_box(cx, cy, z):
    w, h = SRC_W / z, SRC_H / z
    x0 = min(max(cx - w / 2, 0), SRC_W - w)
    y0 = min(max(cy - h / 2, 0), SRC_H - h)
    return x0, y0, w, h


# ----------------------------------------------------------------------------------------------- privacy mask
def mask_usage_warning(arr):
    """Hides Claude Code's usage-limit notice (a yellow line above the prompt) whenever it is on screen."""
    band = arr[930:975, 100:1480]
    yellow = (band[..., 0] > 200) & (band[..., 1] > 150) & (band[..., 2] < 70)
    if yellow.sum() > 60:
        rows = np.where(yellow.any(axis=1))[0]
        y0, y1 = 930 + max(0, rows.min() - 8), 930 + min(45, rows.max() + 10)
        arr[y0:y1, 100:1480] = arr[y0 - 4, 110]   # the terminal's own background colour


# ----------------------------------------------------------------------------------------------- render
def main():
    take = sys.argv[1]
    out_dir = os.path.abspath(sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "..", "docs", "media"))
    os.makedirs(out_dir, exist_ok=True)
    rec = os.path.join(take, "recording.mkv")
    data = json.load(open(os.path.join(take, "events.json")))
    start_time = ffprobe_start(rec)
    ramp, cams, clicks = build_plan(data["events"], start_time)
    src_time, total = output_to_source(ramp)
    n_out = int(total * FPS)
    print(f"output {total:.1f}s, {n_out} frames")

    base, mask, border, (iw, ih) = frame_layers()
    ox, oy = INSET_MARGIN

    # Source frames at a constant 60 fps, read sequentially.
    reader = subprocess.Popen(["ffmpeg", "-v", "error", "-i", rec, "-vf", "fps=60", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
                              stdout=subprocess.PIPE, bufsize=SRC_W * SRC_H * 3 * 2)
    frame_bytes = SRC_W * SRC_H * 3
    mp4 = os.path.join(out_dir, "demo.mp4")
    writer = subprocess.Popen(["ffmpeg", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{OUT_W}x{OUT_H}", "-r", str(FPS),
                               "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "17", "-pix_fmt", "yuv420p",
                               "-movflags", "+faststart", mp4], stdin=subprocess.PIPE)
    src_index, current = -1, None
    try:
        for i in range(n_out):
            t_out = i / FPS
            st = src_time(t_out)
            want = int(round(st * FPS))
            while src_index < want:
                buf = reader.stdout.read(frame_bytes)
                if len(buf) < frame_bytes:
                    break
                current = np.frombuffer(buf, np.uint8).reshape(SRC_H, SRC_W, 3).copy()
                src_index += 1
                mask_usage_warning(current)

            cx, cy, z = camera_at(cams, st)
            x0, y0, w, h = crop_box(cx, cy, z)
            src = Image.fromarray(current)
            view = src.resize((iw, ih), Image.BICUBIC, box=(x0, y0, x0 + w, y0 + h), reducing_gap=2.0)

            # click ripples
            d = ImageDraw.Draw(view, "RGBA")
            scale = iw / w
            for (tc, mx, my) in clicks:
                age = st - tc
                if 0 <= age < 0.55:
                    k = age / 0.55
                    r = (10 + 38 * (1 - (1 - k) ** 3)) * scale ** 0.5 * 1.15
                    a = int(200 * (1 - k))
                    vx, vy = (mx - x0) * scale, (my - y0) * scale
                    d.ellipse((vx - r, vy - r, vx + r, vy + r), outline=(255, 255, 255, a), width=4)
                    d.ellipse((vx - r * 0.55, vy - r * 0.55, vx + r * 0.55, vy + r * 0.55), fill=(255, 255, 255, int(a * 0.25)))

            frame = base.copy()
            frame.paste(view, (ox, oy), mask)
            frame.paste(border, (ox, oy), border)

            fade = min(1.0, t_out / FADE, (total - t_out) / FADE)
            if fade < 1.0:
                frame = Image.blend(base, frame, smooth(max(0.0, fade)))
            writer.stdin.write(frame.tobytes())
            if i % 120 == 0:
                print(f"frame {i}/{n_out}", flush=True)
    finally:
        writer.stdin.close()
        writer.wait()
        reader.kill()

    # README animation: animated WebP is far smaller than GIF at the same quality, and GitHub renders it inline.
    webp = os.path.join(out_dir, "demo.webp")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", mp4, "-vf", "fps=30,scale=1280:-1:flags=lanczos",
                    "-c:v", "libwebp_anim", "-lossless", "0", "-quality", "82", "-compression_level", "6", "-loop", "0", webp], check=True)
    poster = os.path.join(out_dir, "demo-poster.png")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", f"{total * 0.62:.2f}", "-i", mp4, "-frames:v", "1", poster], check=True)
    for p in (mp4, webp, poster):
        print(f"{os.path.basename(p)}: {os.path.getsize(p) / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
