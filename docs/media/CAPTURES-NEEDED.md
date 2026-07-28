# Screenshots still needed

Six images. The README already references all six by these exact paths — drop the files
in and they light up. Until then GitHub shows a broken-image icon, so this is a launch
blocker, not a nice-to-have.

**Shoot them in one sitting**, on the same display, with the same Windows theme and the
same Gloam theme (dark is the default and reads better against GitHub's page). Consistency
between the six matters more than any one of them being perfect.

## Common settings

- **Format:** PNG. Not JPEG — these are UI screenshots with hard edges and text.
- **Width:** ~1600px. Capture at 100% scaling and let GitHub downscale; do not upscale a
  small capture.
- **Chrome:** window only, not the whole desktop. Include Gloam's own title bar; exclude
  the taskbar and any other application.
- **Content:** real data. A dashboard with one monitor named "Generic PnP Monitor" and a
  calibration report full of zeroes undersells the app more than no screenshot would.
- **Privacy:** these go on a public README. Check for machine names, account names,
  file paths with your username, and anything in a window behind Gloam.

---

## 1. `hero-comparison.png` — the whole pitch

Side-by-side: the same SDR content in HDR mode, uncorrected on the left, Gamma 2.2 on the
right. This is the one image most visitors will actually look at, and the only one that
has to carry an argument rather than just show a window.

The honest way to shoot it: photograph or capture the *same* source image under both
states rather than simulating the difference. A screenshot of an HDR desktop will not
reproduce what the eye sees — the whole problem is that the compositor's output differs
from what gets captured — so a camera photo of the panel under each state, cropped
identically and joined, is more truthful than anything generated. Label the halves.

If you build it from a capture instead, say so in the alt text rather than implying it is
a photograph.

Suggested: 1600×900 or wider, halves of equal size, a thin divider between them.

## 2. `tray-menu.png` — the 30-second path

The tray icon right-clicked, menu open, showing the per-monitor submenu with the gamma
modes and a checkmark on the active one. Ideally with two monitors listed so the
per-monitor nature is visible.

Include enough of the system tray that it reads as a tray menu.

## 3. `dashboard.png` — the main window

The dashboard on its default view: monitor status, night mode state, calibration state.
Best with at least one monitor showing an installed, verified calibration so the status
line has something real in it.

## 4. `game-lab.png` — Game Lab

The Game Lab window with a real game added and a picture intent selected, showing the
active-session receipt with its findings. A game most people recognize helps.

If nothing is running, the dashboard's Game Lab section is an acceptable fallback — but
the dedicated `Win + Shift + G` window is the better shot.

## 5. `calibration-report.png` — the differentiator

A completed calibration report showing measured before/after numbers, the per-patch error
chart, and the uncertainty budget. This is the image that separates Gloam from every other
"fix HDR gamma" utility, so pick a report from a real run with a real meter — ideally one
of the two golden-fixture panels, whose numbers you can stand behind.

Scroll to whichever section shows both the ΔE summary and a chart in one frame.

## 6. `night-mode.png` — night mode

The night mode schedule editor with a schedule configured — sun-position mode is more
interesting than fixed times, since it shows the computed sunrise/sunset. Include the
curve editor if it fits.

---

## After capturing

1. Drop all six into `docs/media/`.
2. Check the README renders: every image resolves, none is sideways or enormous.
3. Delete this file.
