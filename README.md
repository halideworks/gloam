# Gloam

Gloam is a Windows tray app for fixing washed-out SDR content in HDR mode. It applies per-monitor gamma correction, provides a better night mode, and can install verified display calibrations when you connect a colorimeter.

Formerly HDR Gamma Controller.

## What It Does

- Fixes the Windows SDR-in-HDR gamma mismatch with per-monitor Gamma 2.2, Gamma 2.4, or Windows Default modes.
- Restores corrections automatically on startup, display changes, resume, and driver or game ramp resets.
- Provides a perceptual night mode using accurate CIE 1931 color temperature math, schedules, manual mode, fades, and per-app exclusions.
- Supports SDR and HDR colorimeter calibration through ArgyllCMS, including native Windows MHC2 profile installation and verification.
- Remembers calibration setup per monitor, including target, preset, meter correction, display type, and window position.
- Updates silently in the background when installed with the setup package.
- Runs locally. No accounts, no telemetry.

## Requirements

- Windows 10 or 11.
- HDR display for the main SDR-in-HDR gamma fix.
- No .NET runtime install required for official builds.
- Colorimeter optional. Calibration uses ArgyllCMS, which Gloam can download automatically.

## Install

Download from GitHub Releases:

https://github.com/halideworks/gloam/releases

Recommended:

- `Gloam-<version>-Setup.exe`
- Per-user install, no administrator prompt.
- Self-contained.
- Bundles ArgyllCMS.
- Auto-updates silently on app exit or restart.

Portable:

- `Gloam-<version>-Portable.zip`
- Extract and run `Gloam.exe`.
- Does not auto-update. Replace it with a newer zip to upgrade.

If SmartScreen says "Windows protected your PC", click **More info** and confirm the verified publisher. New signed apps can still show reputation warnings until enough users have installed them.

## Use

1. Launch Gloam. It appears in the system tray.
2. Right-click the tray icon.
3. Pick a gamma mode per monitor:
   - Gamma 2.2 for general PC use.
   - Gamma 2.4 for dark-room video or BT.1886-style viewing.
   - Windows Default to bypass correction.
4. Enable **Start with Windows** if you want corrections restored at login.
5. Open the dashboard for monitor status, night mode, calibration, and diagnostics.

Hotkeys:

- `Win + Shift + F1`: Gamma 2.2 on the focused monitor.
- `Win + Shift + F2`: Gamma 2.4 on the focused monitor.
- `Win + Shift + F3`: Windows Default.
- `Win + Shift + F4`: Panic mode, clears gamma ramps immediately.
- `Win + Shift + N`: Toggle night mode.

## Calibration

Connect a supported colorimeter and choose **Calibrate Display** from the tray.

Recommended flow:

1. Warm up the display for at least 30 minutes.
2. Pick the monitor, display type, target, and calibration preset.
3. Use a matching `.ccss` or `.ccmx` correction for QD-OLED, wide-gamut LCD, and other non-standard panels.
4. Run calibration. Gloam bypasses existing corrections while measuring.
5. Review the report. Gloam applies the profile and verifies through it automatically.

For HDR desktop calibration, use **HDR Desktop PQ (sRGB gamut)**. For panels that already measure close to target, use white-point-only correction instead of full gamut correction.

## Build From Source

```powershell
git clone https://github.com/halideworks/gloam.git
cd gloam
dotnet run --project src/HDRGammaController
```

Package locally:

```powershell
.\package.ps1 -Version X.Y.Z
```

## Files

- Settings, logs, reports, corrections: `%LOCALAPPDATA%\Gloam`
- Velopack install root: `%LOCALAPPDATA%\GloamApp`

Use **Export Diagnostics** from the tray when reporting issues.

## License

Gloam is MIT licensed.

ArgyllCMS is AGPL v3. Gloam invokes ArgyllCMS tools as separate processes and does not link against AGPL code. See `THIRD_PARTY_NOTICES.txt`.
