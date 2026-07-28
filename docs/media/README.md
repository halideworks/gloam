# README media

All six images the README references exist. This file records how each was made, so
they can be reproduced when the UI changes.

Captured 2026-07-28 from an installed **v1.8.1** build on the author's machine, using real
monitors (MSI MAG271QPX28, Gigabyte M27Q-P), real calibration data, real game profiles.
Nothing here is mocked.

| File | What it shows | How to redo it |
| --- | --- | --- |
| `hero-comparison.png` | Windows sRGB decode vs Gloam Gamma 2.2 | Rendered, not photographed (see below) |
| `tray-menu.png` | Tray menu, per-monitor submenu open on the gamma modes | Right-click the tray icon, hover a monitor |
| `dashboard.png` | Active monitors, gamma mode, night temperature, auto-disable list | Tray → Open Dashboard |
| `night-mode.png` | Schedule curve, rendering profile, melanopic ceiling, schedule table | Dashboard → Night mode controls |
| `game-lab.png` | Per-game picture intents and the live session panel | `Win + Shift + G` |
| `calibration-report.png` | Before/after ΔE, uncertainty budget, display characteristics | Calibrate Display → Past reports… → Open |

## The hero image is a render

`hero-comparison.png` is built from `site/assets/compare_windows_srgb.webp` and
`compare_gloam_gamma2p2.webp`, which `scripts/render-comparison-images.py` produces by
pushing a public-domain painting through the actual signal path (the PQ and sRGB
transfer functions, the gamma-2.2 LUT, and the Windows SDR-in-HDR wire model, all ported
1:1 from the app's own source), then re-encoding for an ordinary SDR viewer.

This is deliberate, and the README says so beneath the image. A screenshot genuinely
cannot show this problem: what the compositor puts on the cable is not what a screen
capture records, so a capture of a washed-out HDR desktop looks identical to a capture of
a corrected one. A camera photograph of the panel would also work and would be more
visceral; if you shoot one, replace the file and update the note under the image in the
README.

To rebuild it after regenerating the source pair:

```powershell
python scripts/render-comparison-images.py
# then recompose the labelled side-by-side at 1800px wide
```

## Privacy note

`dashboard.png` and `night-mode.png` come from the same dashboard capture. The night mode
panel shows **latitude and longitude** for sun-position scheduling; those two fields were
blanked before the images were saved. If you recapture, blank them again. They are a
home address to within a few hundred metres.

Check any recapture for machine names, account names, paths containing a username, and
game libraries you would rather not publish.

## Conventions

- PNG, captured at 100% display scaling. Do not upscale a small capture.
- Window only. No desktop, no taskbar, no other application.
- Dark theme (the app default; it also reads better against GitHub's page).
- Real data. A dashboard listing "Generic PnP Monitor" and a report full of zeroes
  undersells the app more than no screenshot would.
