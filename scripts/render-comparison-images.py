"""
Render the homepage before/after pair by pushing a public-domain SDR photo
through the *actual* Windows-HDR signal path, once without Gloam and once
with Gloam's gamma 2.2 GPU LUT.

Math is ported 1:1 from:
  src/Gloam.Core/TransferFunctions.cs   (PQ, sRGB)
  src/Gloam.Core/LutGenerator.cs        (regrade + shoulder)
  src/Gloam.Core/Calibration/HdrMhc2LutBuilder.cs:104
      (the Windows SDR-in-HDR wire model)

Pipeline, per pixel, per channel:

  s            SDR code value of the source frame (sRGB-encoded 8-bit)
  wire   = PQ^-1( sdrWhite * srgbEotf(s) )        <- what Windows puts on the cable
  wire  := quantize(wire, 10 bit)                 <- real HDMI/DP wire depth
  out    = wire                       (no Gloam)
         | lut1024[wire]              (Gloam gamma 2.2, linear-interpolated as the GPU does)
  nits   = PQ(out)                                <- light the HDR panel emits

The two `nits` fields are then re-encoded for an ordinary SDR web viewer:

  file   = (nits / sdrWhite) ** (1/VIEW_GAMMA)

VIEW_GAMMA = 2.2 because that is the site's (and the app's) stated premise:
SDR content is mastered for, and shown on, ~gamma-2.2 displays. Under that
premise the Gloam branch reduces to the untouched source photo and the
no-Gloam branch is the shadow-lifted one -- which is the honest depiction:
Windows *adds* light to shadows relative to mastering intent.

Usage:
  python render-comparison-images.py [gamma]     # gamma defaults to 2.2

2.2 is GammaMode.Gamma22 and 2.4 is GammaMode.Gamma24. The Windows-branch
output is identical either way, so re-running with a different gamma only
rewrites the Gloam frame.
"""

import sys

import numpy as np
from PIL import Image

# Georges de La Tour, "The Penitent Magdalen" (ca. 1640), Metropolitan Museum
# of Art, public domain / CC0. See site/assets/CREDITS.txt.
# https://images.metmuseum.org/CRDImages/ep/original/DP-27910-001.jpg
SRC = "magdalen.jpg"

# 4:5 crop holding the whole lit figure -- head, hands, skull, candle and
# mirror -- inset from the gold frame edge and the black photographic border
# of the museum scan.
CROP_X0, CROP_X1, CROP_Y0 = 240, 2700, 420

OUT_W, OUT_H = 1200, 1500          # 4:5, matches .comparison aspect-ratio
WEBP_QUALITY = 92                  # 4:4:4; verified to preserve the 8px-integrated
                                   # shadow delta to within 0.17 of 4.45 code values
SDR_WHITE = 200.0                  # MonitorInfo.DefaultSdrWhiteLevel
GAMMA = float(sys.argv[1]) if len(sys.argv) > 1 else 2.2   # GammaMode.Gamma22/24
VIEW_GAMMA = 2.2                   # assumed EOTF of the reader's SDR display
LUT_N = 1024                       # GPU 1D LUT entries
WIRE_BITS = 10

# ---- TransferFunctions.cs constants -------------------------------------
M1 = 2610.0 / 4096.0 / 4.0
M2 = 2523.0 / 4096.0 * 128.0
C1 = 3424.0 / 4096.0
C2 = 2413.0 / 4096.0 * 32.0
C3 = 2392.0 / 4096.0 * 32.0


def pq_eotf(signal):
    """PQ signal -> absolute nits."""
    s = np.clip(np.asarray(signal, dtype=np.float64), 0.0, 1.0)
    n = np.power(s, 1.0 / M2)
    num = np.maximum(0.0, n - C1)
    den = C2 - C3 * n
    out = np.where(den == 0.0, 1.0, np.power(np.maximum(num / np.where(den == 0.0, 1.0, den), 0.0), 1.0 / M1))
    return np.where(den == 0.0, 10000.0, out * 10000.0)


def pq_inverse_eotf(nits):
    """Absolute nits -> PQ signal."""
    l = np.clip(np.asarray(nits, dtype=np.float64), 0.0, 10000.0) / 10000.0
    lm1 = np.power(l, M1)
    return np.power((C1 + C2 * lm1) / (1.0 + C3 * lm1), M2)


def srgb_oetf(linear):
    x = np.clip(np.asarray(linear, dtype=np.float64), 0.0, 1.0)
    return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1.0 / 2.4) - 0.055)


def srgb_eotf(signal):
    x = np.clip(np.asarray(signal, dtype=np.float64), 0.0, 1.0)
    return np.where(x <= 0.04045, x / 12.92, np.power((x + 0.055) / 1.055, 2.4))


def gloam_lut(gamma=GAMMA, sdr_white=SDR_WHITE, n=LUT_N):
    """LutGenerator.GenerateLut, HDR branch, uncalibrated, brightness 100%."""
    v = np.arange(n, dtype=np.float64) / (n - 1)
    nits_in = pq_eotf(v)

    # undo the sRGB curve Windows applied, re-decode with a pure power law
    sig = srgb_oetf(nits_in / sdr_white)
    regraded = pq_inverse_eotf(sdr_white * np.power(sig, gamma))

    # smoothstep shoulder to passthrough above SDR white (LutGenerator.cs:288-315).
    # headroom target == v when brightness == 100 and no boost anchor.
    pq_white = pq_inverse_eotf(sdr_white)
    t = np.clip((v - pq_white) / max(1.0 - pq_white, 1e-9), 0.0, 1.0)
    blend = t * t * (3.0 - 2.0 * t)
    shouldered = regraded + (v - regraded) * blend

    return np.where(nits_in <= sdr_white, regraded, shouldered)


def apply_lut(x, lut):
    """Linear-interpolated 1D LUT fetch, as the GPU does."""
    idx = np.clip(x, 0.0, 1.0) * (len(lut) - 1)
    lo = np.floor(idx).astype(np.int32)
    hi = np.minimum(lo + 1, len(lut) - 1)
    f = idx - lo
    return lut[lo] * (1.0 - f) + lut[hi] * f


# ---- 1. source frame -> SDR code values ---------------------------------
im = Image.open(SRC).convert("RGB")
assert b"sRGB" in Image.open(SRC).info.get("icc_profile", b""), "expected an sRGB source"

crop_w = CROP_X1 - CROP_X0
crop_h = int(round(crop_w / (OUT_W / OUT_H)))
assert CROP_Y0 + crop_h <= im.size[1], "crop runs off the bottom of the source"
im = im.crop((CROP_X0, CROP_Y0, CROP_X1, CROP_Y0 + crop_h))

# resample in light-linear space; content targets a gamma-2.2 display, so
# linearize with that same power law and re-encode with its exact inverse
# (round-trips to the identity, leaving `s` untouched apart from resampling).
lin = np.power(np.asarray(im, dtype=np.float64) / 255.0, VIEW_GAMMA)
# Pillow can't Lanczos a 3-channel 16-bit image directly; do it per channel.
chans = []
for c in range(3):
    ch = Image.fromarray((np.clip(lin[..., c], 0.0, 1.0) * 65535.0 + 0.5).astype(np.uint16), mode="I;16")
    ch = ch.resize((OUT_W, OUT_H), Image.LANCZOS)
    chans.append(np.asarray(ch, dtype=np.float64) / 65535.0)
lin = np.clip(np.stack(chans, axis=-1), 0.0, 1.0)

s = np.power(lin, 1.0 / VIEW_GAMMA)
s = np.round(s * 255.0) / 255.0          # SDR desktop composition is 8-bit

# ---- 2. Windows puts it on the wire in PQ -------------------------------
wire = pq_inverse_eotf(SDR_WHITE * srgb_eotf(s))
q = (1 << WIRE_BITS) - 1
wire = np.round(wire * q) / q

# ---- 3. two branches ----------------------------------------------------
lut = gloam_lut()
nits_windows = pq_eotf(wire)
nits_gloam = pq_eotf(apply_lut(wire, lut))

# ---- 4. re-encode for an SDR web viewer ---------------------------------
def to_file(nits):
    rel = np.clip(nits / SDR_WHITE, 0.0, 1.0)
    return (np.power(rel, 1.0 / VIEW_GAMMA) * 255.0 + 0.5).astype(np.uint8)


img_windows = to_file(nits_windows)
img_gloam = to_file(nits_gloam)

gamma_tag = f"gamma{GAMMA:.1f}".replace(".", "p")
for arr, name in ((img_windows, "compare_windows_srgb"),
                  (img_gloam, f"compare_gloam_{gamma_tag}")):
    Image.fromarray(arr).save(name + ".webp", quality=WEBP_QUALITY, method=6, subsampling=0)

# ---- 5. QA --------------------------------------------------------------
print(f"output {OUT_W}x{OUT_H}  sdrWhite={SDR_WHITE} nits  gamma={GAMMA}")
print()
print("LUT sanity (should match TransferFunctionTests reference points):")
for nits, expect in [(0.1, 0.0623368657), (100.0, 0.5080784215),
                     (203.0, 0.5806888810), (10000.0, 1.0)]:
    got = float(pq_inverse_eotf(nits))
    print(f"  PQ^-1({nits:>8}) = {got:.10f}  expect {expect:.10f}  "
          f"{'OK' if abs(got - expect) < 1e-9 else 'MISMATCH'}")

print()
print("Shadow lift Windows/Gloam, emitted light ratio (site quotes 2.87 / 1.59 / 1.14 / 1.00):")
for code in (0.05, 0.10, 0.20, 0.40, 1.00):
    a = float(pq_eotf(pq_inverse_eotf(SDR_WHITE * srgb_eotf(code))))
    b = float(pq_eotf(apply_lut(pq_inverse_eotf(SDR_WHITE * srgb_eotf(code)), lut)))
    print(f"  signal {code:.2f}: {a:8.4f} nits vs {b:8.4f} nits  ratio {a / b:.3f}")

print()
d = img_windows.astype(np.int16) - img_gloam.astype(np.int16)
print(f"code-value delta (Windows - Gloam): min {d.min()}  max {d.max()}  mean {d.mean():.2f}")
print(f"pixels lifted by >=4 codes: {(d >= 4).mean() * 100:.1f}%")
y = 0.2126 * lin[..., 0] + 0.7152 * lin[..., 1] + 0.0722 * lin[..., 2]
print(f"source: {(y < 0.01).mean() * 100:.1f}% of pixels below 1% display light")

# does the shipped WebP still carry the difference the page is claiming?
import os
from scipy.ndimage import gaussian_filter

enc = {n: np.asarray(Image.open(n + ".webp").convert("RGB"), dtype=np.float64)
       for n in ("compare_windows_srgb", f"compare_gloam_{gamma_tag}")}
true_d = img_windows.astype(np.float64) - img_gloam.astype(np.float64)
enc_d = enc["compare_windows_srgb"] - enc[f"compare_gloam_{gamma_tag}"]
blurred = gaussian_filter(enc_d - true_d, (8, 8, 0))
print()
print(f"webp q{WEBP_QUALITY}: {sum(os.path.getsize(n + '.webp') for n in enc) / 1e6:.2f} MB for the pair")
print(f"  delta preserved: 8px-integrated error {np.sqrt((blurred ** 2).mean()):.4f} codes "
      f"against a mean delta of {true_d.mean():.2f}")
