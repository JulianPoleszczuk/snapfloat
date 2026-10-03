# SnapFloat design specification

This is the source of truth for SnapFloat's visual design. It is implemented in `src/SnapFloat/Themes` and the XAML
views. No Figma file was produced for this version. If one is created later, it should mirror the tokens below.

## Principles

1. **The screenshot is the hero.** Chrome around the image is minimal: a 1 px hairline, a soft shadow, and controls
   that appear only on hover or click.
2. **Native first.** Segoe UI Variable, Windows-style settings cards, standard focus behaviour, system theme,
   high contrast.
3. **Calm motion.** The preview slides in from the screen edge (280 ms, cubic ease-out) and fades out (220 ms).
   All motion turns off with the *Animations* setting or when Windows "Animation effects" is off.
4. **Never colour alone.** Every status (copied, error, pinned, conflict) pairs an icon with text.

## Tokens

| Token | Light | Dark |
|---|---|---|
| Window | `#F6F6F8` | `#1E1E22` |
| Sidebar | `#EFEFF3` | `#19191C` |
| Surface (cards) | `#FFFFFF` | `#28282D` |
| Text | `#1B1B1F` | `#F2F2F5` |
| Text secondary | `#5E5E68` (6.3:1) | `#B0B0BA` (8:1) |
| Border | `#E2E2E8` | `#36363D` |
| Accent | `#4F5BD5` (white text 5.3:1) | `#8E98FF` (dark text) |
| Danger | `#C42B1C` | `#FF8A80` |
| Success | `#0F7B3F` | `#6CCB8F` |
| Thumbnail hairline | `#000` at 14 % | `#FFF` at 22 % |
| Shadow | black at 22 %, blur 22, y 5 | black at 50 % |

High-contrast mode maps every token to the corresponding `SystemColors` value.

Typography: Segoe UI Variable Display 22 semibold (page titles), 15 semibold (subtitles), and Segoe UI Variable Text
13 (body) / 12 (captions).
Radii: 6 (controls), 8 (cards and menus), 9 (preview toolbar), and 10 (preview by default, user-adjustable 0–20).
Spacing grid: 4 px. Settings rows are at least 60 px tall with 16 px horizontal padding.

Icons: a 24 px grid, 2 px round strokes in the Lucide style, rendered by `IconView` at 16 px (toolbar and menus) or
18 px (callouts).

## Floating preview

- Width 260 DIP by default (200–320, adjustable). Height follows the aspect ratio, capped at 1.15 × width. Images
  that are very tall or very wide are cropped to fill, anchored at the top, and the card is never narrower than
  168 DIP so the toolbar fits.
- It sits in the chosen corner of the monitor's work area. The card is 18 DIP from the edges, which is also the
  room the shadow needs. Stacked previews have a 10 DIP gap; pinned previews sit nearest the corner.

States:

| State | Treatment |
|---|---|
| Appearing | Opacity 0→1 (200 ms); slides 28 DIP in from the screen edge, and scales 0.96→1 |
| Idle | Image, hairline and shadow only |
| Hovered | A round close button (22 DIP) fades in on the corner facing the screen centre. The countdown pauses. |
| Toolbar visible (click) | A pill floats 8 DIP above the bottom edge with five 28 DIP icon buttons: copy image, copy path, open, pin, delete. Rises 6 DIP as it appears. Hides 450 ms after the pointer leaves. |
| Dragging | The preview drops to 45 % opacity, and the shell shows an 80 % scale rounded drag image under the cursor |
| Pinned | Pin badge (filled pin icon, accent colour) in the top corner; the toolbar pin icon is filled; no auto-dismiss |
| Status | A top-centred pill with ✓ "Image copied" / "Path copied", or ⚠ "Clipboard busy, try again" / "File no longer exists", shown for 1.6 s |
| Fading out | Opacity → 0 and a 16 DIP slide toward the edge, 220 ms ease-in (120 ms after a drop or delete) |
| Error | A 84 DIP card with a ⚠ badge, a bold title ("Couldn't save screenshot"), and a one-line reason; shown for at least 6 s |

## Settings window

860 × 620 DIP (minimum 700 × 480). It has a 220 DIP sidebar containing the app mark, name and tagline, five
navigation items with icons and an accent indicator bar.

Content is grouped into section headers and cards with one setting per row: a title and description on the left and
the control on the right. Controls are a toggle (with an On/Off text label), slider with a value label, combo box,
shortcut recorder, a segmented theme picker, and buttons. Inline errors use a tinted strip with ⚠ and text.

## Onboarding

520 DIP wide, shown on first run only. It shows the app mark and "SnapFloat is ready", then three steps in a card,
each with an icon badge:

1. Take a screenshot, with key caps for Win + Shift + S and, if one is set, the region shortcut.
2. Drag the preview.
3. Click for quick actions.

Below that is the "Start SnapFloat when I sign in" checkbox and the buttons *Open settings* / **Get started**.

## Tray

The icon is a rounded indigo square with white capture-corner brackets and a white "floating card" breaking out of
the bottom-right corner. It has hand-tuned 16/20/24 px frames that keep it legible.

The menu is themed like the app: rounded, with icons and shortcut hints, and a *Recent screenshots* submenu that
shows thumbnails.
