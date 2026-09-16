# Route Pulse icon — design record

The artwork here is the **design record**. It is not built into the
application. The application icons come from `scripts/build-icons.cs`.

## What the mark shows

Three network endpoints in a triangular route topology, an active red probe on
the right route, and a two-segment motion trail behind the probe where the
resolution permits it. No text, transparent background.

- Light surface: deep navy structure `#082B57`, warm off-white interior
  `#F4F1E8`, vivid red probe `#FF241F`.
- Dark surface: light gray structure `#E8EAED`, charcoal interior `#202124`,
  the same red probe.

Keep this identity unless the user asks for a visual redesign.

## Files

| File | What it is |
|---|---|
| `winmtr-route-pulse-light.svg` | Detailed light master, 1024 unit space |
| `winmtr-route-pulse-dark.svg` | Detailed dark master, 1024 unit space |
| `winmtr-route-pulse-light-small.svg` | First optical master for small light frames |
| `winmtr-route-pulse-dark-small.svg` | First optical master for small dark frames |
| `winmtr-route-pulse-light.png` | 1024 px light render |
| `winmtr-route-pulse-dark.png` | 1024 px dark render |

`scripts/build-icons.cs` carries the geometry of the detailed masters as
constants, and the weights of the small masters as its 16 px optical values.
Change the artwork here and the script does not follow. Change both together.

## Why the hand-made icons were replaced

Three defects, found on 2026-09-10:

1. **The small frames had no antialiasing.** The 16, 20, 24 and 32 px frames
   held four colours and no partial alpha, so every edge was fully on or fully
   off. Circles became octagons and the probe became a square blob. This was
   the defect the user saw, because at 96 dpi Windows shows the 16 px frame in
   the title bar and the 24 px frame on the taskbar.
2. **The artwork changed proportion between 64 px and 128 px.** The small
   master served every size up to 64 px, the detailed master from 128 px up.
   The two had different weights and different margins, so the mark read as two
   different logos. The script now ramps every weight smoothly instead.
3. **The set could not be made again.** No script existed, so the frames could
   drift apart and nobody could correct them.

## How Windows picks a frame

Avalonia does not decode the ICO on Windows. `Avalonia.Win32.IconImpl` keeps
the raw bytes and asks `Win32Icon` for one frame per size:

- title bar: 16 px multiplied by the display scale;
- taskbar: 24 px multiplied by the display scale, on Windows 10 and later.

`Win32Icon.LoadIconFromData` then applies the Windows best-fit rules and calls
`CreateIconFromResourceEx`. Every frame in the ICO is therefore really used, so
each one has to be right on its own.

Linux is different. `X11IconLoader` decodes through Skia, which keeps only the
largest frame. macOS has no window icon.
