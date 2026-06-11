# HDR Gamma Controller

A Windows System Tray application to manage HDR Gamma settings on a per-monitor basis. This tool addresses the "washed out" or incorrect dark levels often experienced when using Windows HDR on OLED and Mini-LED displays.

## Features

- **Per-Monitor Gamma Control**: Apply Gamma 2.2, 2.4, or Windows Default independently for each HDR monitor
- **Real Monitor Names**: Displays actual monitor names from EDID (e.g., "LG OLED TV") instead of generic identifiers
- **Profile Persistence**: Remembers your gamma settings per monitor and restores them automatically on startup
- **Expert Dashboard**: A comprehensive grid view to manage all connected monitors, view their status (HDR/SDR), and quick-access settings.
- **Improved Night Mode**:
  - Now works on **SDR Monitors** too!
  - Automatically adjusts color temperature based on sunset/sunrise at your location
  - Smooth fading transitions (configurable duration)
- **Per-Monitor Color Matching**:
  - **Temperature Offset**: Fine-tune the white point of individual monitors to visually match them to each other, especially effective when Night Mode is active.
- **Native, Self-Healing Gamma Apply**: LUTs load directly into the GPU gamma ramp via the Windows API (sub-millisecond, no helper process), and a ramp guard detects when a fullscreen game or driver event resets the ramp and silently restores the correction within seconds
- **Full Colorimeter Calibration (SDR and HDR)**: Measure your display with a colorimeter (via ArgyllCMS `spotread`) and install a measurement-based correction as a native Windows MHC2 color profile - applied by the compositor itself, persistently, including in HDR:
  - Hands-free flow: measure, apply, and verify automatically, ending on a scored report with measured before/after accuracy (dE2000), corrected-response charts, and an enable/disable A/B toggle
  - Gamut + white point correction via a properly domain-wrapped MHC2 matrix, plus tone correction (PQ-domain in HDR, knee-safe so the panel's own highlight handling is preserved)
  - White-point-only mode for panels that already measure close to target (auto-suggested for OLED)
  - Meter spectral corrections (.ccss/.ccmx) with an in-app browser for the DisplayCAL community corrections database - essential for accurate readings on QD-OLED and wide-gamut panels
  - Per-monitor memory of display type, correction file, and calibration scope
  - Your gamma preference and night mode compose on top of the installed calibration
- **Update Checker**: Automatically notifies you when a new version or auto-build is available.
- **System Tray Integration**: Unobtrusive background operation with dark/light mode support
- **Start with Windows**: Toggle auto-start from the tray menu
- **Auto-Download ArgyllCMS**: Downloads ArgyllCMS automatically when calibration features need it

## Requirements

1. **Windows 10/11** with HDR-capable display(s)
2. **.NET 8.0 Runtime** (for the Lite build) or no dependencies (for the self-contained Full build)
3. **ArgyllCMS** — *optional*. Core gamma correction works without it (the app sets the hardware gamma ramp natively). ArgyllCMS is only needed for colorimeter calibration (`spotread`) and as an automatic fallback if a driver ever rejects the native ramp call:
   - The app will **automatically download** ArgyllCMS when a feature needs it
   - Or if you have **DisplayCAL** installed, the app detects its bundled Argyll binaries

## Installation

### Option 1: Pre-built Releases (Recommended)

**Download the latest release from the Releases page.**

There are two versions available:

1.  **Full Version (`HDRGammaController_Full.zip`)**:
    *   **Recommended for most users.**
    *   **Self-Contained**: Does *not* require .NET Runtime to be installed.
    *   **Bundled Dependencies**: Includes ArgyllCMS (`dispwin.exe`) so it works immediately offline.
    *   **Size**: ~160 MB.

2.  **Lite Version (`HDRGammaController_Lite.zip`)**:
    *   **Requires**: [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0).
    *   **ArgyllCMS**: Not needed for gamma correction; downloaded automatically if you use colorimeter calibration.
    *   **Size**: ~200 KB.

**Instructions:**
1.  Extract the ZIP file to your preferred location (e.g., `C:\Program Files\HDRGammaController`).
2.  Run `HDRGammaController.exe`.
3.  Right-click the tray icon and enable "Start with Windows" for auto-start.

### Option 2: Build from Source

You can build the project manually or use the included packaging script.

#### Using Packaging Script (Recommended)
This script will generate both Lite and Full packages in the root directory.

```powershell
.\package.ps1
```

#### Manual Build
```powershell
# Clone the repository
git clone https://github.com/davidtorcivia/win11hdr-gamma-adjuster.git
cd win11hdr-gamma-adjuster

# Build minimal (requires .NET 8 runtime)
dotnet publish src/HDRGammaController -c Release --self-contained false -o publish-minimal

# OR build self-contained (no dependencies)
dotnet publish src/HDRGammaController -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Usage

1. **Launch** the application - it appears in the system tray
2. **Right-click** the tray icon to see your monitors (numbered with actual names)
3. **Select a gamma mode** for each HDR monitor:
   - **Gamma 2.2**: General PC use, most content
   - **Gamma 2.4**: BT.1886 / dark room / film mastering
   - **Windows Default**: Native piecewise sRGB (bypass)
4. **Enable auto-start** via "Start with Windows" menu option
5. **Open Settings** (via Tray -> Settings) for advanced control:
   - **Night Mode**: Enable "Sunrise/Sunset" for automatic adjustment based on your location (Latitude/Longitude).
   - **Calibration**: Adjust Brightness, Temperature, Tint, and RGB Gains/Offsets for fine-tuning your display's white point and color balance.

   **Note**: All settings are saved automatically to `%LOCALAPPDATA%\HDRGammaController\settings.json`. Diagnostic logs are written to `%LOCALAPPDATA%\HDRGammaController\app.log` — include this file when reporting issues.

Your selections are automatically saved and restored on next launch.

## Display Calibration (Colorimeter)

With a supported colorimeter connected (e.g. X-Rite i1 Display Pro), open **Calibrate Display** from the tray:

1. **Pick the monitor, display type, and target.** For HDR displays use "HDR Desktop PQ (sRGB gamut)". Select a **meter spectral correction** matched to your panel - the "Find online" button searches the DisplayCAL community database and downloads directly (strongly recommended for QD-OLED and wide-gamut panels, where generic corrections misread the white point by several dE).
2. **Run the calibration.** Patches are measured with your existing corrections bypassed; a mute button on the measurement screen silences the capture sounds mid-run. On HDR displays the run also measures an **HDR wire ladder** - true HDR-range patches emitted through a DirectX FP16 (scRGB) surface at exact PQ wire positions, far above SDR white - so the tone correction is built from measured data across the whole HDR range instead of assumptions about Windows' SDR mapping. On completion the report opens, applies the profile, and re-measures through it automatically.
3. **Read the report.** Native vs. calibrated dE2000 (average / max / grayscale / primaries), tone and gamut charts with the corrected response overlaid, and a grade reflecting the corrected display. HDR verifications add a **PQ tracking sweep**: FP16 patches through the applied profile, graded in absolute nits against ST.2084 and dE ITP (BT.2124). Use the toggle button for an eyes-on A/B against the uncorrected panel.
4. **Trust your eyes last.** Instruments and eyes weigh display spectra differently (observer metamerism) - a small final Tint/Temperature trim against a reference display is normal, rides on top of the profile, and never affects the measurements.

If a panel already measures close to target natively, prefer the **white point correction only** option - full gamut correction cannot improve what is already inside measurement noise. Calibrate with the panel warmed up (especially OLEDs).

## Keyboard Shortcuts

Global hotkeys allow you to quickly switch modes without opening the tray menu:

- **Win + Shift + F1**: Apply Gamma 2.2 to focused monitor
- **Win + Shift + F2**: Apply Gamma 2.4 to focused monitor
- **Win + Shift + F3**: Apply Windows Default (Passthrough)
- **Win + Shift + F4**: **Panic Mode** (Clears all gamma tables immediately)
- **Win + Shift + N**: Toggle Night Mode on/off

## How It Works

Windows 11 uses piecewise sRGB for SDR content in HDR mode, but most content is mastered on gamma 2.2/2.4 displays. This causes washed-out shadows and reduced contrast. 

This tool generates corrective 1D LUTs that:
1. Decode the PQ (ST.2084) HDR signal
2. Apply proper gamma correction in the SDR range
3. Preserve HDR highlights above the SDR white level
4. Re-encode back to PQ for the display

The LUTs load directly into the video card's hardware gamma ramp via the Windows `SetDeviceGammaRamp` API (the same mechanism ArgyllCMS `dispwin` uses, without the external process), and identical re-applies are skipped so the ramp is only ever rewritten when the desired output actually changes. A background ramp guard re-asserts the correction if anything external resets it.

For a detailed explanation of the mathematics and engineering behind this tool, please read the **[Technical Whitepaper](whitepaper.md)**.

## Acknowledgements

- **[dylanraga](https://github.com/dylanraga/win11hdr-srgb-to-gamma2.2-icm)**: Original research and Python implementation
- **[ArgyllCMS](https://www.argyllcms.com/)**: Colorimeter measurement (`spotread`) and low-level VCGT access via `dispwin`
- **[MHC2 / MHC2Gen](https://github.com/dantmnf/MHC2)** by dantmnf: Reference for the MHC2 ICC tag format and matrix semantics
- **[DisplayCAL colorimeter corrections database](https://colorimetercorrections.displaycal.net/)**: Community-contributed spectral corrections used by the in-app browser

## License

This project is licensed under the MIT License.

ArgyllCMS is licensed under the AGPL v3 license. This application calls `dispwin` as a separate process and does not link against AGPL code.
