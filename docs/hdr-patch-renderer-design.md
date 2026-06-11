# Design: True HDR-Range Patch Renderer

*Status: designed, not yet implemented. Last updated: June 10, 2026.*

## Problem

All calibration patches are currently rendered by WPF — 8-bit SDR content. In HDR mode,
Windows maps them onto the wire at `PQ(sdrWhiteLevel · sRGB(v))`, so measurements only
cover the wire range up to the SDR white level (~240 nits on the reference system). The
PQ tone LUT therefore corrects only that region and passes everything brighter through
untouched (the knee-safe blend in `HdrMhc2LutBuilder`), and the BT.2020 PQ target cannot
be meaningfully calibrated or verified. Measuring true HDR-range stimuli requires
emitting pixel values above SDR white, which no GDI/WPF surface can do.

## Approach

A dedicated patch window backed by a **DirectX 11 swapchain in scRGB FP16**
(`DXGI_FORMAT_R16G16B16A16_FLOAT` + `DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709`):

- scRGB values are linear with 1.0 = 80 nits; a 1000-nit white patch is rendered as
  `(12.5, 12.5, 12.5)`. The compositor converts to the wire PQ/BT.2020 itself, which is
  exactly the path desktop HDR content takes — so measurements characterize the same
  pipeline users see.
- The renderer is trivial: clear the backbuffer to the patch color each frame; no
  geometry, no shaders beyond the implicit colorspace conversion.
- Host via `SwapChainPanel`-style HWND interop: a borderless Win32 window owning the
  swapchain, positioned with `SetWindowPos` pixels like `PatchDisplayWindow`, with the
  same progress overlay drawn into the frame (D2D interop or a thin WPF overlay window).

## Integration points

1. **Patch type extension**: `ColorPatch` gains `double? Nits` — when set, the renderer
   computes scRGB = `nits / 80` per channel instead of treating `DisplayRgb` as SDR
   signal. Patch sets for HDR targets then include a PQ ramp (e.g. 10 → panel-peak nits
   log-spaced) and HDR-range primaries.
2. **`HdrMhc2LutBuilder`**: with full-range measurements, the wire position of each
   patch is exact (`PQ⁻¹(nits)` — no Windows-mapping assumption) and the LUT can correct
   up to the panel's true rolloff with measured data instead of blending to identity at
   80% of the SDR range. Keep the identity blend *above the measured peak* only.
3. **Verification**: the same renderer runs the verify sweep, enabling honest BT.2020 PQ
   container verification and MaxCLL-style reporting.
4. **Safety**: clamp requested nits to the panel's DXGI-reported `MaxLuminance`; never
   hold >600-nit full-field patches for more than the settle+measure window (OLED ABL
   and panel stress); keep the surround black.

## Risks / unknowns to validate with the probe

- Whether DWM applies the installed MHC2 profile to FP16 scRGB surfaces identically to
  8-bit content (expected: yes — the correction is at scanout — but verify before
  trusting verify).
- HDR brightness-slider interaction: scRGB is referenced to 80-nit units, but Windows'
  SDR-content slider must NOT rescale FP16 values above 1.0 (it should not; verify).
- i1 Display Pro ceiling (~2000 nits standard mode) and low-light floor still apply.

## Effort estimate

D3D11 interop + renderer ~300 lines; patch-set and builder changes ~150; validation runs
on both panels required before enabling the BT.2020 PQ target end-to-end.
