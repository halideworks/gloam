# Design: True HDR-Range Patch Renderer

*Status: implemented and integrated. Last updated: July 4, 2026.*

## Problem

The original calibration path rendered patches through WPF, which meant 8-bit SDR
content. In HDR mode, Windows maps those pixels onto the wire at
`PQ(sdrWhiteLevel · sRGB(v))`, so measurements only cover the range up to the SDR white
level. True HDR-range calibration and verification require emitting pixel values above
SDR white, which no GDI/WPF surface can do.

## Approach

Gloam uses a dedicated patch window backed by a **DirectX 11 swapchain in scRGB FP16**
(`DXGI_FORMAT_R16G16B16A16_FLOAT` + `DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709`):

- scRGB values are linear with 1.0 = 80 nits; a 1000-nit white patch is rendered as
  `(12.5, 12.5, 12.5)`. The compositor converts to the wire PQ/BT.2020 itself, which is
  exactly the path desktop HDR content takes — so measurements characterize the same
  pipeline users see.
- The renderer is trivial: clear the backbuffer to the patch color each frame; no
  geometry, no shaders beyond the implicit colorspace conversion.
- Host via HWND interop: a borderless Win32 window owns the swapchain, is positioned in
  device pixels, and presents patch colors directly through DWM's HDR path. The WPF
  calibration flow chooses this renderer for HDR/nit-addressed patches and keeps the
  existing SDR patch window for ordinary SDR stimuli.

## Integration points

1. **Patch type extension**: `ColorPatch` carries `double? Nits` — when set, the renderer
   computes scRGB = `nits / 80` per channel instead of treating `DisplayRgb` as SDR
   signal. Patch sets for HDR targets include a PQ ramp (e.g. 10 → panel-peak nits
   log-spaced) and HDR-range primaries.
2. **`HdrMhc2LutBuilder`**: with full-range measurements, the wire position of each
   patch is exact (`PQ⁻¹(nits)` — no Windows-mapping assumption) and the LUT can correct
   up to the panel's true rolloff with measured data. The identity blend is retained
   above the measured peak instead of at the SDR/HDR boundary.
3. **Verification**: the same renderer runs the verify sweep, enabling honest BT.2020 PQ
   container verification and MaxCLL-style reporting.
4. **Safety**: clamp requested nits to the panel's DXGI-reported `MaxLuminance`; never
   hold >600-nit full-field patches for more than the settle+measure window (OLED ABL
   and panel stress); keep the surround black.

## Validation notes

- DWM/MHC2 behavior must be checked whenever this path changes: installed MHC2 profiles
  should affect FP16 scRGB surfaces at scanout the same way they affect ordinary desktop
  content.
- HDR brightness-slider interaction remains a regression point: scRGB is referenced to
  80-nit units, but Windows' SDR-content slider must not rescale FP16 values above 1.0.
- i1 Display Pro ceiling (~2000 nits standard mode) and low-light floor still apply.
