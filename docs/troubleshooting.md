# Troubleshooting

Every entry here ends in something to actually do. If none of them fit, jump to
[Getting help](#getting-help) — the diagnostics bundle is what makes a bug report
actionable.

---

## "Windows protected your PC" when installing

**What you are seeing:** a blue SmartScreen dialog with a **Don't run** button and no
obvious way past it.

**Why:** SmartScreen weighs a signature's *reputation*, not just its validity. A correctly
signed application from a publisher Microsoft has not seen at volume still gets flagged
until enough people have installed it. New releases can briefly re-trigger it.

**Do this:** click **More info**, confirm the publisher line reads the expected verified
publisher, then click **Run anyway**. If the publisher line is missing or reads something
else, stop — do not run it, and tell us where you downloaded it from. Official builds come
only from the [releases page](https://github.com/halideworks/gloam/releases).

---

## My profile stopped applying after a Windows update

**What you are seeing:** colors were right, Windows updated, now they are not — and Gloam
still shows the calibration as installed.

**Why:** Windows owns the color-profile association, and feature updates, driver updates,
and display re-detection can all drop or reorder it. Gloam's own record of "this profile
is installed" is a record of what it did, not proof that Windows is still honoring it.
That is exactly the gap the report's activation sentinel is for: it compares the native
and verified measurements on matching patches and warns when an inaccurate panel did not
measurably move toward target after installation.

**Do this, in order:**

1. Open the dashboard and check the display's calibration status. If it no longer shows an
   active profile, re-install it from the calibration report's **Apply** button.
2. If Gloam still claims it is applied but the display looks wrong, do not trust either
   side — measure. Open the report and use **Re-verify** with the meter connected. That
   re-measures through whatever is actually active now and refreshes the numbers.
3. If verification comes back much worse than the report's recorded values, the profile is
   not being applied even though it is associated. Toggle the display's gamma mode to
   **Windows Default** and back, which forces a clean re-apply.
4. If it still disagrees, re-run calibration. A Windows update that changed the HDR
   pipeline has changed the thing you calibrated against.

Note that a *small* regression here is normal and is not proof of anything: display drift
and meter noise both reduce apparent improvement. The sentinel is a signal, not a verdict.

---

## A game reset my gamma

**What you are seeing:** you alt-tab out of a fullscreen game and the desktop is flat,
oversaturated, or washed out.

**Why:** exclusive-fullscreen games write the hardware gamma ramp directly, and many do not
put it back on exit. So do some driver control panels and overlay tools.

**Do this:** nothing, usually. Gloam runs a ramp guard that reads the hardware ramp back on
a ten-second cadence and restores what it applied when something else stomps it, so the
correction should return on its own within a few seconds. Wait for it before intervening.

If it does not come back, or you need it back *now*:

- Press `Win + Shift + F4` — panic mode, which clears the gamma ramps immediately and
  returns the display to an uncorrected state. Then re-select your gamma mode from the tray.
- Press `Win + Shift + F1` (Gamma 2.2) or `Win + Shift + F2` (Gamma 2.4) to re-apply
  directly to the focused monitor.

The ramp guard deliberately stands down for a display that is mid-calibration, so if this
happens while a calibration is running, that is expected — finish or cancel the run first.

---

## Colors look wrong after a driver update

**What you are seeing:** a GPU driver update, and now everything is off — often more
saturated, or with a visible color cast.

**Why:** driver installs commonly reset the hardware gamma ramp, and can also reset or
re-enumerate the display, which changes what Windows thinks the display is and can drop
the profile association with it. Vendor control panels (NVIDIA, AMD, Intel) also apply
their own color settings that compose with Gloam's, and a driver install can re-enable
those.

**Do this, in order:**

1. Check the vendor control panel first. If its own digital vibrance, color enhancement, or
   custom gamma setting got turned back on, turn it off — two corrections stacked will
   never look right, and Gloam cannot see or undo that one.
2. Re-select the gamma mode from the tray to force a re-apply.
3. If you have an installed calibration, follow the "profile stopped applying" steps above;
   a driver update drops profile associations the same way a Windows update does.
4. If the display was re-enumerated, Gloam may see it as a new monitor and have no saved
   settings for it. Re-apply the gamma mode and re-install the calibration profile.

---

## Night mode is not changing anything

**Do this:**

1. Check whether Windows Night Light is also on. Two warming corrections stack, and
   Windows applies its own after Gloam. Turn Windows Night Light off — Gloam replaces it.
2. Check whether the foreground application is on your night-mode exclusion list. Excluded
   applications suppress night mode while they are focused, by design.
3. Check whether Gameplay Lock is holding the output steady for an active game session.
   That is also by design; the manual toggle (`Win + Shift + N`) stays authoritative.

---

## Calibration will not start, or cannot find my meter

**Do this:**

1. Unplug and re-plug the colorimeter, then use **Refresh** in the calibration setup
   window. Meters that were connected through a hub or a KVM are the usual culprits.
2. If Gloam reports the driver is missing, accept the driver-install prompt. ArgyllCMS
   needs its own USB driver for most instruments.
3. Close any other software that talks to the meter — DisplayCAL, the vendor's own
   calibration utility, and Gloam cannot hold the device at the same time.
4. If ArgyllCMS itself is missing, start a calibration once and let Gloam download it. The
   setup build bundles it, but a portable install may need the download.

---

## Where the files are

| What | Where |
| --- | --- |
| Settings, logs, reports, corrections | `%LOCALAPPDATA%\Gloam` |
| Log file | `%LOCALAPPDATA%\Gloam\app.log` |
| Calibration reports | `%LOCALAPPDATA%\Gloam\reports` |
| Measurement CSVs | `%LOCALAPPDATA%\Gloam\reports\measurements` |
| Exported diagnostics bundles | `%LOCALAPPDATA%\Gloam\Diagnostics` |
| Velopack install root | `%LOCALAPPDATA%\GloamApp` |

The two directories are separate on purpose: uninstalling Gloam removes `GloamApp` and
leaves `Gloam`, so an uninstall never destroys your calibration data.

---

## Getting help

Use **Export Diagnostics** from the tray menu. It writes a zip to
`%LOCALAPPDATA%\Gloam\Diagnostics` containing the log, sanitized settings, monitor and
display-configuration state, and — if you choose **Include Reports** — your calibration
report snapshots and verification CSVs.

The bundle is text-only and is sanitized before it is written, but it does describe your
displays and your configuration. Look through it before attaching it to a public issue if
that concerns you.

Then [open an issue](https://github.com/halideworks/gloam/issues/new/choose) and attach it.
A report with a diagnostics bundle can usually be diagnosed; one without it usually turns
into several rounds of questions that the bundle would have answered.
