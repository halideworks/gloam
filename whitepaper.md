# Gloam: A Technical Whitepaper

*Formerly HDR Gamma Controller.*

> *The machine promises fidelity but delivers translation—and every translation is a betrayal.*

---

## Abstract

Windows 11's HDR implementation employs piecewise sRGB as the transfer function for standard dynamic range content displayed within an HDR composition space—a technical decision that, while nominally compliant with the sRGB specification, introduces a fundamental perceptual mismatch with the overwhelming majority of content mastered on gamma 2.2 displays, manifesting as elevated shadow regions, reduced contrast, and the characteristic "washed out" appearance that has plagued HDR adoption on the Windows platform since its inception.

This whitepaper documents the mathematical foundations, engineering decisions, and calibration philosophy underlying Gloam, a tool that corrects this mismatch through the application of per-channel 1D Look-Up Tables within the Windows Advanced Color pipeline; it examines the relevant transfer functions, derives the correction algorithm from first principles, and explores the additional calibration adjustments—color temperature, tint, brightness—that enable fine-grained control over the visual output, with particular attention to why specific mathematical choices were made, grounded always in the physics of light perception and the engineering constraints of the Windows HDR compositor.

---

## Table of Contents

1. [Introduction: The HDR Transition Problem](#1-introduction-the-hdr-transition-problem)
2. [The SDR Transfer Function Mismatch](#2-the-sdr-transfer-function-mismatch)
3. [Mathematical Foundations](#3-mathematical-foundations)
4. [The Transformation Pipeline](#4-the-transformation-pipeline)
5. [HDR Headroom Preservation](#5-hdr-headroom-preservation)
6. [Calibration Adjustments](#6-calibration-adjustments)
7. [Implementation Architecture](#7-implementation-architecture)
8. [Windows HDR Pipeline Context](#8-windows-hdr-pipeline-context)
9. [Verification and Accuracy](#9-verification-and-accuracy)
10. [Colorimeter Calibration and the MHC2 Pipeline](#10-colorimeter-calibration-and-the-mhc2-pipeline)
11. [Acknowledgements](#11-acknowledgements)
12. [References](#12-references)

---

## 1. Introduction: The HDR Transition Problem

The transition to High Dynamic Range displays represents one of the most significant advances in visual reproduction since the shift from standard definition to high definition, expanding the representable luminance range from the few hundred nits of SDR displays to peaks of 1,000, 4,000, or even 10,000 nits while simultaneously lowering the black floor to near-perfect darkness on OLED and Mini-LED panels—an expansion that promises images approximating, for the first time in consumer technology, the dynamic range of human vision itself.

And yet the transition has been anything but smooth, for Windows 11, while making strides in streamlining the HDR experience—finally allowing for workstations where HDR remains always enabled throughout the desktop—has introduced along the way a subtle but pernicious problem in how it handles the vast corpus of SDR content that must coexist with native HDR material, a problem that reveals itself most clearly in the shadows and low mid-tones where the discrepancy between what the system assumes and what the content actually requires becomes impossible to ignore.

The problem is translation: when an SDR application renders its content, Windows must lift that content into the HDR10 composition space for display, a process that requires mapping SDR code values to specific luminance levels within the broader HDR range according to a *transfer function* whose choice determines how the original content appears on the HDR-enabled display. Microsoft chose piecewise sRGB; the industry standard, the curve on which virtually all content has been mastered for the past three decades, is gamma 2.2. The difference between these two curves, concentrated as it is in the shadow and lower mid-tone regions, produces a measurable, reproducible, and—provided one understands the mathematics involved—correctable error that manifests as the very haziness and flatness that has driven countless observers to disable HDR in frustration, wondering why the technology that promised deeper blacks delivers instead a pervasive gray.

---

## 2. The SDR Transfer Function Mismatch

### 2.1 A Brief History of Display Gamma

The concept of "gamma" in display technology originates from the non-linear voltage-to-light relationship inherent in cathode ray tubes, wherein a CRT receiving a voltage signal *V* produces light output *L* following approximately a power law:

$$L = V^\gamma$$

For a typical CRT, γ ≈ 2.5, a non-linearity that was never a design choice but rather a physical characteristic of the phosphor excitation process that content producers quickly adapted to by *pre-compensating* at the source, applying an inverse gamma encoding so that the roundtrip from camera to display preserved perceptual linearity—a system built on accidents and expedience that nonetheless became the foundation upon which all SDR imaging would rest.[^1]

Over decades, through a process of standardization not unlike natural selection, **gamma 2.2** emerged as the de facto standard for computer monitors and general content creation (though cinema is typically gamma 2.4), representing a reasonable approximation for viewing in uncontrolled lighting conditions exceeding 50 lux as specified in various industry documents and adopted by virtually every computer monitor manufactured in the past three decades.[^2]

### 2.2 The sRGB Standard

In 1996, an attempt was made to formalize the color space of computer displays: the sRGB standard (IEC 61966-2-1). Rather than specifying a pure power-law gamma, sRGB defines a *piecewise* transfer function with a linear segment near black:

$$
V_{sRGB} = \begin{cases}
12.92 \times L & \text{if } L \leq 0.0031308 \\
1.055 \times L^{1/2.4} - 0.055 & \text{if } L > 0.0031308
\end{cases}
$$

The linear segment (L ≤ 0.0031308) exists for numerical stability near zero, avoiding the infinite slope that a pure power law exhibits at the origin, while the exponent 1/2.4 in the non-linear segment, combined with the offset terms, produces a curve that approximates gamma 2.2 over most of its range but diverges in precisely the shadow regions where the human eye is most sensitive to error.[^3]

### 2.3 Microsoft's Choice

When Windows 11 renders SDR content within the HDR desktop composition, it employs the piecewise sRGB EOTF—Electro-Optical Transfer Function—to map SDR code values to linear light values under the assumption that SDR content was encoded with the sRGB inverse EOTF and therefore decoding with sRGB produces accurate linear light, an assumption that, while technically correct according to the sRGB specification, proves practically wrong for virtually all content because the monitors used by game developers, web designers, photographers, and colorists overwhelmingly implement pure gamma 2.2 rather than piecewise sRGB, because artists adjust their work until it *looks correct* on their gamma 2.2 display with the encoded values implicitly assuming gamma 2.2 decoding, and because the sRGB piecewise curve produces more light below the mid-tones than gamma 2.2, manifesting as elevated shadows, reduced contrast, and a loss of atmospheric depth when sRGB-decoded content mastered for gamma 2.2 reaches the eye.

The visual discrepancy is not subtle: consider the example cited in Dylan Raga's original research, Diablo IV, a game celebrated for its dark, gritty aesthetic in SDR mode that appears washed out in HDR mode, its rich blacks turning to dull grays, its atmosphere becoming hazy—a ruination caused not by the game's HDR implementation but by Windows' SDR-in-HDR translation layer.[^4]

### 2.4 Quantifying the Difference

The discrepancy admits precise quantification when we plot the luminance output for identical input values under sRGB versus gamma 2.2:

| Input Code Value | sRGB Linear Output | Gamma 2.2 Linear Output | Difference |
|------------------|-------------------|------------------------|------------|
| 0.10 | 0.0100 | 0.0063 | +58.7% |
| 0.20 | 0.0331 | 0.0217 | +52.5% |
| 0.30 | 0.0732 | 0.0545 | +34.3% |
| 0.40 | 0.1329 | 0.1113 | +19.4% |
| 0.50 | 0.2140 | 0.1972 | +8.5% |

The sRGB curve produces significantly more light in the shadow and lower mid-tone regions—a discrepancy exceeding 50% at the 10% input level that constitutes the mathematical origin of the washed-out appearance, diminishing as values approach the mid-tones and converging to near-zero above the 50% threshold where the curves effectively coincide.

---

## 3. Mathematical Foundations

Correcting the transfer function mismatch requires operating within the HDR signal path itself, which in turn requires understanding three distinct transfer functions and their inverses: the gamma power law that governs legacy SDR displays, the piecewise sRGB formulation that Windows assumes, and the ST.2084 Perceptual Quantizer that encodes HDR signals for transmission and display.

### 3.1 The Gamma Power Law

The simplest transfer function relates signal to light via a single exponent:

**EOTF (Electro-Optical Transfer Function):**
$$L = V^\gamma$$

**Inverse EOTF (OETF - Opto-Electronic Transfer Function):**
$$V = L^{1/\gamma}$$

Where:
- *L* = Relative linear luminance [0, 1]
- *V* = Signal value [0, 1]
- *γ* = Gamma exponent (2.2 for general PC use, 2.4 for dark room viewing per BT.1886)

### 3.2 The sRGB Piecewise EOTF

As introduced above, sRGB defines a piecewise function:

**EOTF:**
$$
L = \begin{cases}
V / 12.92 & \text{if } V \leq 0.04045 \\
\left(\frac{V + 0.055}{1.055}\right)^{2.4} & \text{if } V > 0.04045
\end{cases}
$$

**Inverse EOTF:**
$$
V = \begin{cases}
12.92 \times L & \text{if } L \leq 0.0031308 \\
1.055 \times L^{1/2.4} - 0.055 & \text{if } L > 0.0031308
\end{cases}
$$

The threshold 0.04045 (signal domain) corresponds to 0.0031308 (linear domain) at the junction point of the two segments.

### 3.3 The ST.2084 PQ Transfer Function

The Perceptual Quantizer (PQ) defined in SMPTE ST.2084 and adopted by HDR10 is fundamentally different from gamma-based functions. Rather than modeling CRT physics, PQ is designed to match **human perceptual response** to luminance, enabling efficient encoding across the full 0–10,000 nit range of HDR.[^5]

**PQ EOTF (Signal → Nits):**

$$L = 10000 \times \left( \frac{\max(V^{1/m_2} - c_1, 0)}{c_2 - c_3 \times V^{1/m_2}} \right)^{1/m_1}$$

**PQ Inverse EOTF (Nits → Signal):**

$$V = \left( \frac{c_1 + c_2 \times (L/10000)^{m_1}}{1 + c_3 \times (L/10000)^{m_1}} \right)^{m_2}$$

Where the constants are defined as:

| Constant | Fractional Form | Decimal Value |
|----------|-----------------|---------------|
| m₁ | 2610/4096/4 | 0.1593017578125 |
| m₂ | 2523/4096 × 128 | 78.84375 |
| c₁ | 3424/4096 | 0.8359375 |
| c₂ | 2413/4096 × 32 | 18.8515625 |
| c₃ | 2392/4096 × 32 | 18.6875 |

These constants derive from the Barten model of human contrast sensitivity, tuned to produce approximately uniform perceptual steps across 12 bits of precision over the 10,000 nit range.[^6]

What distinguishes PQ from gamma-based functions is its operation in **absolute luminance**—nits, candelas per square meter—rather than relative luminance, so that a PQ signal value corresponds to a specific physical light level regardless of the display's peak brightness, making PQ the lingua franca of HDR signal processing and the domain in which our correction must ultimately operate.

---

## 4. The Transformation Pipeline

### 4.1 The Problem in Signal-Domain Terms

Windows' SDR-in-HDR pipeline receives SDR content assumed to be sRGB-encoded, applies the sRGB EOTF to produce linear light relative to the SDR white level, scales that linear light to absolute nits according to the SDR Content Brightness setting, applies the PQ inverse EOTF to produce the HDR signal, and composites the result into the HDR10 output—a chain in which the error occurs at the sRGB EOTF step, where content mastered for gamma 2.2 decoding encounters Windows' sRGB decoder and emerges with its shadows lifted and its contrast reduced.

### 4.2 The Correction Algorithm

To correct this, we intercept the PQ signal and apply a LUT that effectively replaces sRGB decoding with gamma decoding:

```
For each PQ code value index i in [0, 1023]:
    
    normalized = i / 1023
    
    # Step 1: Decode PQ to get the linear nits Windows produced
    linear_nits = PQ_EOTF(normalized)
    
    # Step 2: If above SDR white level, we're in HDR headroom - handle separately
    if linear_nits > sdr_white_level:
        # See Section 5: HDR Headroom Preservation
        continue
    
    # Step 3: Undo Windows' sRGB assumption
    # Windows assumed: signal → sRGB EOTF → linear_nits_intended
    # So we reverse: linear_nits → sRGB Inverse EOTF → original_signal
    signal_reconstructed = sRGB_InverseEOTF(linear_nits / sdr_white_level)
    
    # Step 4: Apply correct gamma decoding
    # Now decode as the content was actually mastered: signal^γ → linear_intended
    linear_intended = signal_reconstructed^γ
    
    # Step 5: Scale back to absolute nits
    output_nits = black_level + (sdr_white_level - black_level) × linear_intended
    
    # Step 6: Encode back to PQ for the display
    output_signal = PQ_InverseEOTF(output_nits)
    
    LUT[i] = output_signal
```

The essential operation is a remapping that "undoes" the sRGB decoding and "redoes" it as gamma 2.2 (or 2.4), all while preserving the absolute luminance scaling and PQ encoding expected by the HDR display.

### 4.3 The SDR White Level Parameter

The transformation depends critically on knowing the SDR white level—the luminance at which Windows renders SDR code value 1.0. This is controlled by the **"SDR Content Brightness"** slider in Windows Display settings and varies from 80 to 480 nits:

| Slider Position | SDR White Level |
|-----------------|-----------------|
| 0 | 80 nits |
| 5 | 100 nits |
| 30 | 200 nits |
| 55 | 300 nits |
| 80 | 400 nits |
| 100 | 480 nits |

The formula is approximately: **White Level = 80 + (Slider × 4) nits**

If the tool assumes a different white level than Windows is actually using, the correction will be miscalibrated—either crushing shadows (if assumed white level is too low) or elevating them (if too high). This parameter is therefore exposed in the user interface and should match the user's Windows setting.

---

## 5. HDR Headroom Preservation

### 5.1 The Headroom Problem

HDR's defining feature, its expansion of the luminance range, creates a complication for any correction operating in the SDR region: while SDR content occupies roughly 80–480 nits depending on the brightness setting, HDR content can utilize the full range up to the display's peak luminance, commonly 1,000–4,000 nits for modern HDR displays, and some SDR applications exploit this headroom by sending specular highlights and UI elements above the nominal SDR white level, particularly games that use the HDR headroom for bright effects even when rendering ostensibly SDR content.

Blindly applying the gamma correction across the entire signal range would darken these HDR highlights inappropriately—what was intended as a 700-nit specularity would emerge crushed according to the same shadow-adjustment curve designed for the 0–200 nit range, destroying the very highlights that HDR was meant to liberate.

### 5.2 The Shoulder Blend

The correction employs a smooth blend from the fully-corrected curve to a *dim-aware passthrough* as the signal rises above the SDR white level. Two details in that phrase are load-bearing and worth dwelling on, because a naïve implementation gets both of them subtly wrong.

**Blend in PQ-signal space, not linear-nit space.** A blend parameterized by linear nits spends almost all of its range in a region the eye barely perceives — between 4,000 and 10,000 nits — while the region where content actually lives (200–2,000 nits) sees only a tiny fraction of the fade. The result is a curve that stays almost fully corrected across the entire useful HDR range and only lets go of the correction in the extreme peaks. PQ signal values are perceptually uniform by construction, so the blend is instead parameterized by the normalized PQ code value:

$$
t = \frac{N - N_{SDR}}{1 - N_{SDR}}, \quad t \in [0, 1]
$$

where *N* is the normalized PQ signal and *N*<sub>SDR</sub> is the PQ signal corresponding to the SDR white level. Stepping through *t* at uniform intervals traverses equal perceptual steps through the highlight range.

**Smoothstep for C¹ continuity.** A linear blend produces a LUT that is continuous in value but discontinuous in derivative at the SDR/HDR boundary, which shows up as a visible kink in smooth gradients (skies, radial gradients in UI). A smoothstep remedies this at no runtime cost:

$$
\text{blend\_factor} = t^{2}(3 - 2t)
$$

**The blend target must follow the brightness slider.** The correction we fade *from* is the fully-calibrated signal. The question is what we fade *to*. Fading to pure identity passthrough preserves the creative intent of HDR highlights — no re-gamma, no temperature tint on a 2,000-nit specular — which is the right behavior for almost every adjustment in the pipeline. But it is wrong for dimming: a user who slides brightness to 50% expects everything to come down, including specular highlights. Fading to identity leaves those highlights at full luminance and produces the counterintuitive effect of a dimmed UI with undimmed highlights punching through.

The fix is to define the headroom target not as the unchanged input signal, but as the input signal after dimming only — preserving the HDR creative grade while still honoring the brightness setting:

$$
\text{headroom\_target}(L) =
\operatorname{PQ}_{\mathrm{OETF}}\bigl(\text{dim}(L)\bigr)
$$

where the dimming function is the same power+scale defined in §6.4, extended to absolute nits by anchoring at the SDR white level *W*:

$$
\text{dim}(L) = \left(\frac{L}{W}\right)^{1/\gamma(b)} \cdot b \cdot W
$$

The choice of anchor matters. At the SDR/HDR boundary (*L* = *W*) this expression evaluates to *b·W* — exactly the value the dimmed SDR portion of the curve reaches at white — so the headroom target meets the corrected curve continuously. An earlier formulation normalized against the 10,000-nit PQ ceiling instead; because the dimming exponent is applied to *L*/10000 rather than *L*/*W*, the headroom target at the boundary came out ~1.7× brighter than the dimmed SDR white at 50% brightness, leaving a brightness shelf just above SDR white that the smoothstep could soften but not remove. When Brightness = 100%, the expression reduces exactly to PQ<sub>OETF</sub>(L) — the original identity passthrough — so the behavior is unchanged for users who never touch the dimming slider.

The final output is:

$$
\text{output} = \text{corrected} + (\text{headroom\_target} - \text{corrected}) \times \text{blend\_factor}
$$

This produces a LUT that is smooth and monotonic across the full range, preserves creative intent in the HDR region, and responds correctly to the brightness control throughout.

---

## 6. Calibration Adjustments

Beyond the core gamma correction, Gloam provides calibration adjustments—color temperature, tint, brightness—for fine-tuning the display to the user's environment and preferences, adjustments that operate throughout on a philosophy of perceptual accuracy over mathematical simplicity.

### 6.1 Operating in Linear Light Space

All color adjustments are applied after decoding to linear light and before re-encoding, a constraint essential for perceptual accuracy because operations like scaling, color mixing, and temperature adjustment are meaningful only in linear space where physical mixing of light occurs; applying a color temperature shift in gamma-encoded space produces incorrect results because the encoding curve compresses different luminance regions non-uniformly, breaking the proportional relationships that temperature adjustment assumes.

### 6.2 Color Temperature

Color temperature describes the chromaticity of a light source in relation to the Planckian locus—the curve in CIE color space traced by an ideal blackbody radiator as its temperature varies from red-hot (low Kelvin) through white-hot to blue-white (high Kelvin).[^7]

Human circadian rhythm is entrained to the color temperature of natural light, which shifts from the warm ~2700K of candlelight and sunset through the neutral ~5500K of midday sun to the cool ~7500K of overcast sky and twilight. Night mode features that reduce blue light exposure are based on shifting the display toward warmer temperatures.

#### Tanner Helland's Approximation

For computational efficiency, the controller implements Tanner Helland's widely-used approximation of blackbody RGB values,[^8] which provides a visually pleasing, if mathematically simplified, representation of color temperature. For temperature *T* in Kelvin:

**Red channel:**
$$
R = \begin{cases}
255 & \text{if } T \leq 6600 \\
329.698727446 \times (T/100 - 60)^{-0.1332047592} & \text{if } T > 6600
\end{cases}
$$

**Green channel:**
$$
G = \begin{cases}
99.4708025861 \times \ln(T/100) - 161.1195681661 & \text{if } T \leq 6600 \\
288.1221695283 \times (T/100 - 60)^{-0.0755148492} & \text{if } T > 6600
\end{cases}
$$

**Blue channel:**
$$
B = \begin{cases}
0 & \text{if } T \leq 1900 \\
138.5177312231 \times \ln(T/100 - 10) - 305.0447927307 & \text{if } T < 6600 \\
255 & \text{if } T \geq 6600
\end{cases}
$$

The resulting RGB values are normalized so that 6500 K yields exactly (1, 1, 1) and applied as per-channel multipliers in linear space.

#### CIE 1931 Accurate Method

For users demanding higher fidelity, the controller also implements a physically accurate conversion based on the CIE 1931 color space, using the Kang et al. cubic approximations of the Planckian locus[^10] to compute the chromaticity coordinates (x, y) for a given Kelvin temperature, transforming them into the XYZ color space, and finally projecting them into linear sRGB via the D65 matrix before encoding and normalizing to a maximum of 1. This method ensures that the white point shifts precisely along the Planckian locus, preserving the exact chromaticity relationships defined by the laws of physics.

#### Temperature Offset for Night Mode

Rather than specifying an absolute temperature, night mode operates via a **temperature offset** from the base 6500K neutral. A -1500K offset shifts toward warmer 5000K; a +500K offset shifts toward cooler 7000K. This allows the night mode adjustment to layer on top of any existing color profile.

### 6.3 Tint (Green/Magenta Axis)

Color temperature adjustments move along the Planckian locus (blue ↔ orange axis). However, many displays and environments require adjustment along the **orthogonal** green/magenta axis — the tint control familiar from video color correction.

Tint is implemented as a differential scaling of the green channel relative to red and blue. Let *τ* be the slider value in the range [−50, +50] and let *t = τ / 50* be the normalized value in [−1, +1].

For *τ* > 0 (shift toward magenta):

$$
M_R = 1 + 0.08\,t, \qquad M_G = 1 - 0.12\,t, \qquad M_B = 1 + 0.08\,t
$$

For *τ* < 0 (shift toward green), with *t' = −t ≥ 0*:

$$
M_R = 1 - 0.08\,t', \qquad M_G = 1 + 0.10\,t', \qquad M_B = 1 - 0.08\,t'
$$

The asymmetric magenta coefficient (12%) compensates for the greater perceptual weight green carries in luminance; a symmetric multiplier would shift brightness noticeably when moving the slider.

### 6.4 Dimming

Reducing display brightness proves less straightforward than multiplying all values by a single scalar. A pure linear dim preserves the ratio between shadow and highlight but is perceptually *less uniform* than we would like: at low brightness levels the eye's sensitivity to contrast in dark regions rises (Stevens' power law for luminance has an exponent near 0.5),[^9] so what feels like "dim the whole screen" actually collapses the visible shadow detail disproportionately. The controller compensates with a two-parameter curve that mirrors the "exposure + lift" interaction in photo editing:

$$
L_{\mathrm{dimmed}} = L^{1/\gamma(b)} \cdot b
$$

where *b* = brightness / 100 ∈ (0, 1] and γ grows as brightness falls:

$$
\gamma(b) = 1 + 0.3\,(1 - b)
$$

At *b* = 1.0 the function reduces to the identity (γ = 1, factor = 1). At *b* = 0.1 we have γ = 1.27, so a mid-gray input of 0.5 maps to 0.5<sup>0.787</sup>·0.1 ≈ 0.058 — brighter than the 0.05 a linear dim would produce, precisely because the γ term lifts shadows before the multiplicative factor brings everything down to the requested brightness. Near-black values are lifted more than near-white ones, preserving shadow separation even at very low brightness settings. White (L = 1) lands exactly at *b* so the user's "50%" slider maps to true 50% output.

Users who prefer a straight linear dim — for calibration work or color-critical comparisons — can disable the shadow lift via the "Preserve shadow detail" toggle, falling back to *L<sub>dimmed</sub> = L · b*.

### 6.5 Order of Operations

The calibration pipeline applies adjustments in a specific order:

1. **Dimming** — Reduces overall luminance
2. **Temperature** — Shifts white point along Planckian locus
3. **Tint** — Shifts white point orthogonal to Planckian locus
4. **RGB Gain** — Per-channel multiplier (1.0 = unity)
5. **RGB Offset** — Per-channel addition (0.0 = none)

This order ensures that creative adjustments (temperature, tint) operate on the dimmed luminance range, and low-level calibration (RGB gain/offset) can correct for display non-uniformities independent of the artistic intent.

### 6.6 Integrated Night Shift

A persistent challenge in Windows color management is the conflict between hardware gamma tables (VCGT) and the system's native Night Light feature. When an external tool writes a correction to the VCGT, it often overrides or gets overridden by Windows Night Light, leading to a flickering battle for control or the complete disablement of the warming effect just when it is needed.

Gloam solves this by **integrating Night Shift directly into the correction pipeline**. Rather than relying on Windows to apply a toggleable overlay, the application calculates the sun's position based on the user's geolocation, determines the appropriate color temperature shift, and *bakes it mathematically into the gamma LUTs* before they are ever sent to the display.

This approach offers two profound advantages: first, it eliminates the resource contention for the VCGT, ensuring that the gamma correction and the night mode warming coexist in perfect harmony; second, it applies the warming effect in high-precision floating-point space *before* quantization, resulting in a smoother, band-free transition that maintains the integrity of the shadow details even as the screen warms to a deep amber.

The scheduler driving these transitions is event-disciplined: it emits exactly one change notification per effective state change (a kelvin movement beyond a 5 K threshold, or a settings change that alters the output), and its timer adapts its cadence to the schedule—ticking every 4 seconds only while a fade is actively interpolating, and otherwise sleeping until the next scheduled transition (capped at 60 seconds to remain robust against system clock changes). During a fade, each notification produces at most one ramp write per display, and the deduplication described in §7.2 suppresses any redundant ones.

---

## 7. Implementation Architecture

### 7.1 LUT Structure

The correction takes the form of a 1024-point 1D Look-Up Table, a resolution derived from the Windows MHC2 (Monitor Hardware Calibration) profile format which specifies precisely 1024 entries for its calibration LUT and maps input PQ signal values to output PQ signal values:

$$
\text{LUT} : [0, 1023] \rightarrow [0.0, 1.0]
$$

For calibration adjustments that affect color channels differently (temperature, tint, RGB gain), **per-channel LUTs** are generated:

- **LUT_R[1024]** — Red channel transform
- **LUT_G[1024]** — Green channel transform
- **LUT_B[1024]** — Blue channel transform
- **LUT_Grey[1024]** — Reference (average of R, G, B)

### 7.2 Application Methods

The controller loads the generated LUTs into the video card's hardware gamma ramp (VCGT — Video Card Gamma Table).

#### Direct Hardware Loading

The primary path calls the Win32 `SetDeviceGammaRamp` API directly: the 1024-point LUT is resampled onto the 256-entry, 16-bit-per-channel hardware ramp by linear interpolation and handed to the display driver in a single sub-millisecond call. This is precisely what the **ArgyllCMS `dispwin` utility** does internally when loading a `.cal` file, so the resulting hardware state is bit-identical to the dispwin path — but without spawning an external process (~100–500 ms per invocation) or round-tripping the LUT through a temp file. `dispwin` is retained as an automatic fallback should the native call be rejected, and ArgyllCMS remains the measurement engine for colorimeter calibration. This method is:

- **Fast**: Updates apply near-instantly, allowing real-time preview and smooth scheduled transitions.
- **Universal**: Works across most GPU vendors and driver versions.
- **Precise**: Bypasses the Windows compositor's color management quirks by speaking directly to the hardware driver.

While VCGT loading is sometimes criticized for its volatility—it can be reset by system events or fullscreen exclusive games—Gloam counters this with a **ramp guard**: every 10 seconds it reads each display's hardware ramp back via `GetDeviceGammaRamp` (an essentially free call), compares it against the ramp it last applied, and silently restores the correction if anything overwrote it. Displays whose drivers transform ramps on write (so a readback can never match) are detected and excluded after one restore attempt, preventing a restore-flash loop. Combined with re-application on display-configuration changes and resume-from-sleep, the correction is self-healing without user intervention.

#### Apply Hygiene: Why Re-Apply Discipline Matters

Each `dispwin` invocation rewrites the GPU gamma ramp in a single step, and on an HDR display that rewrite is *visible*—a momentary luminance discontinuity the user perceives as flicker. The corollary is that the apply pipeline must treat ramp writes as a scarce resource, not a free idempotent operation. Three mechanisms enforce this:

- **Identical-LUT deduplication.** Before spawning `dispwin`, the generated per-channel LUTs are compared against the last set successfully applied to that display; if nothing changed, the spawn is skipped entirely. This converts the many internal triggers that converge on a re-apply (foreground-app changes, settings touches, night-mode ticks) into no-ops whenever the output would be identical.
- **Cache invalidation on external resets.** Deduplication is only safe while the application's record of the ramp state is accurate. Display configuration changes and resume-from-sleep can reset the ramp behind the application's back, so both events invalidate the dedupe cache before triggering a re-apply—guaranteeing the correction is restored even though the LUT "hasn't changed."
- **Event debouncing.** Windows emits `WM_DISPLAYCHANGE` in bursts during HDR mode transitions. Events are coalesced so that only the final event in a burst, after a settling delay, performs the monitor re-enumeration and re-apply; superseded handlers abandon their work rather than queueing overlapping ramp writes.
- **Ramp guard.** A 10-second readback poll detects external overwrites (fullscreen exclusive games, driver events, other color tools) and restores the applied ramp — writing only when the hardware state has actually diverged.

Together with the night-mode scheduler's single-event-per-state-change contract (§6.6), these ensure the ramp is written exactly once per genuine change in desired output.

### 7.3 SDR vs. HDR Processing Paths

For SDR displays—those without HDR enabled—the transformation pipeline simplifies considerably, decoding input as gamma 2.2 to linear, applying calibration adjustments in linear space, and encoding output as gamma 2.2 back to signal, with no PQ involvement whatsoever because SDR displays expect no such encoding; the calibration adjustments themselves—temperature, tint, dimming—apply identically in both cases, differing only in the encode/decode stages that bracket them.

---

## 8. Windows HDR Pipeline Context

### 8.1 The Desktop Window Manager (DWM)

All visual output in modern Windows flows through the Desktop Window Manager, the compositor responsible for rendering the desktop, windows, and effects, and when HDR is enabled, DWM operates in a **linear scRGB composition space** (Canonical Composition Color Space). This space uses BT.709 primaries but allows for unbounded floating-point values (far exceeding 1.0) to represent high dynamic range and wide color gamut data, converting the traditional sRGB surfaces of SDR applications into this linear space before final output to the display's native format (typically HDR10/BT.2020).

### 8.2 Where LUTs Are Applied

Color transforms apply in a hierarchy: application rendering first (sRGB for SDR apps, scRGB or HDR10 for HDR apps), then DWM conversion from SDR to HDR10 using the sRGB EOTF and SDR white level, and finally the display driver's VCGT gamma ramp where our correction resides.

By injecting the correction at the driver level (VCGT), we effectively intercept the signal *after* the Windows compositor has done its damage but *before* it leaves the GPU, allowing us to untwist the distorted tone curve and restore linearity just before the photons are emitted. The integrated Night Shift ensures that this VCGT injection handles both the gamma correction and the circadian warming in a single, unified mathematical pass.

### 8.3 Night Light Interaction

Because Windows' native Night Light applies its filter at the compositor level, it would normally stack with or be overridden by VCGT loading. However, by disabling the system's Night Light and relying on the controller's **Integrated Night Shift**, we avoid this conflict entirely. The application takes responsibility for the circadian adjustment, rendering the system's native toggle redundant and ensuring a conflict-free color pipeline provided the native feature is disabled.

---

## 9. Verification and Accuracy

### 9.1 Mathematical Validation

The LUT generation algorithm was validated against the reference implementation from Dylan Raga's original project, with the JavaScript implementation using identical transfer function constants and producing bitwise-comparable output when controlled for floating-point precision differences, all calculations employing **double precision** (64-bit IEEE 754) and specifying the ST.2084 constants to 14 decimal places to avoid accumulated rounding error across the 1024-point LUT.

### 9.2 Visual Verification

Correct gamma application manifests visually as rich, defined shadows rather than elevated hazy gray; strong separation between light and dark elements; proper rendering of fog, darkness, and volumetric lighting; and richer color saturation, since elevated shadows desaturate the image. A/B comparison with the tool enabled and disabled on dark content—games, films—reveals the difference immediately to any attentive observer.

### 9.3 Common Failure Modes

| Symptom | Likely Cause |
|---------|--------------|
| Image appears too dark | SDR white level set higher than Windows setting |
| Shadows appear crushed | SDR white level set lower than Windows setting |
| No visible change | LUT failed to apply; check driver compatibility |
| Colors appear tinted | Calibration settings inadvertently modified |

---

## 10. Colorimeter Calibration and the MHC2 Pipeline

The per-channel ramp of Sections 4–7 corrects tone, but it is structurally incapable of correcting color: a 1D LUT per channel cannot move a primary, cannot rotate a white point without disturbing the channels independently, and—decisively—is not honored consistently by the Advanced Color compositor. Measurement-based calibration therefore required a second application mechanism, and Windows offers exactly one OS-native hook for it: an ICC profile carrying the **MHC2 tag**, from which the Desktop Window Manager extracts a 3×4 matrix and three regamma LUTs and applies them at composition—persistently, system-wide, and in both SDR and HDR. The findings in this section were each purchased with a failed on-screen result and a colorimeter; they are documented here because almost none of them are written down anywhere else.

### 10.1 The Matrix Slot Is an XYZ Transform

The single most consequential discovery: Windows does not apply the MHC2 matrix to RGB values. The engine evaluates it **sandwiched between fixed conversions to and from CIE XYZ**, using the wire primaries of the current mode—sRGB in SDR, BT.2020 in HDR:

```
wire = LUT( XYZ→wire_fixed · M_tag · wire→XYZ_fixed · linear_content )
```

The reference generator (MHC2Gen) acknowledges this only in a code comment—left-multiplying its matrix by sRGB→XYZ with the note *"hack: eliminate fixed sRGB to XYZ."* Writing a plain linear-RGB→RGB correction into the slot, as a naive implementation does, produces dramatic damage: on the test display the engine's sandwich turned a gentle warm correction into a white drive of (R 1.39—clipped, G 0.866, B 0.910), a violent magenta cast across the entire tonal range. The correct construction wraps the intended RGB→RGB matrix **M** as `S·M·S⁻¹` (with S the wire RGB→XYZ matrix), whereupon the engine's fixed conversions cancel algebraically and exactly **M** is applied. A useful corollary: because the display is characterized *through* Windows' own content rendering, the wire-format conversion cancels identically in SDR and HDR—the same sRGB-wrapped matrix is correct in both modes, a result verified both algebraically and on-screen.

### 10.2 White Point: Absolute Matrix, Uniform Scale

White-point correction belongs **inside the matrix**, as the textbook absolute mapping (content white → target white), with one refinement: on a panel whose native white differs from the target, some channel demands more than full-scale drive (1.095 on the blue-leaning test LCD), which clips. The resolution is a single **uniform scale** of the entire matrix by the reciprocal of the largest drive value—every chromaticity, primaries included, is preserved exactly, at the cost of a few percent of peak luminance.

The tempting alternative—normalizing per channel and folding the residual gains into the tone LUTs—was implemented, measured, and rejected: per-channel gains form a diagonal matrix applied *after* the gamut matrix and re-tint every non-neutral color the matrix had just placed. Verified consequence: primaries degraded from 1.39 to 2.46 ΔE2000 while neutrals measured fine. Chromatic corrections must never be split across stages that compose multiplicatively per channel.

### 10.3 HDR: PQ-Domain LUTs and the Tone-Mapping Knee

In HDR the MHC2 LUTs operate on **PQ wire signal** (0–1 ≙ 0–10,000 nits via ST.2084), and two physical constraints bound what they may attempt. First, calibration patches are SDR content, mapped by Windows onto the wire at `PQ(sdrWhite · sRGB(v))`—the measurements only cover the wire up to the SDR white level (queried live via `DISPLAYCONFIG_SDR_WHITE_LEVEL`; the assumed 200-nit default was off by 20% on the test system). Second, the upper portion of even that range sits inside the panel's own tone-mapping knee, where the wire-axis model is invalid—and, more subtly, where a strongly curved per-channel LUT distorts the channel ratios of unequal drive values, which is precisely the matrix's corrected white. The first implementation corrected aggressively to the top and measurably destroyed its own white point (189 nits at x = 0.301 instead of ~220 at D65). The fallback builder therefore corrects fully only below 50% of the measured range, fades to identity by 80%, and passes the knee—and all true HDR highlights—through untouched. Microsoft's own HDR Calibration utility, dissected for reference, ships an identity matrix and two-entry identity LUTs: its entire payload is the MHC2 header's min/max luminance metadata, which this tool also writes from the panel's DXGI-reported range.

**Wire-exact measurement (the shipped HDR path).** Both constraints above are artifacts of measuring through Windows' SDR mapping, so the tool now removes the mapping from the loop: HDR calibrations append a **wire ladder** of patches emitted through a DirectX 11 FP16 scRGB swapchain (`R16G16B16A16_FLOAT` + `DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709`, 1.0 ≙ 80 nits), whose PQ wire position is exact—`PQ⁻¹(requested nits)`—and which reaches far above SDR white (0 → min(0.9·panel peak, 1000) nits, log-spaced). A temporary probe harness validated the premises before the path shipped: FP16 surfaces traverse the identical color pipeline as SDR content (Δxy ≤ 0.0005), the SDR-in-HDR mapping is confirmed piecewise-sRGB, FP16 wire positions are self-consistent to within 1.5% across the range—and the OS-reported SDR white level was ~7% below where SDR white actually lands on the wire (240 reported vs ~258 measured), disqualifying it as a calibration anchor. With measured wire-exact data the LUT inverts the panel's response across the entire desktop-plus-HDR range, including the tone-mapping knee (now just part of the measured response), and goes identity only above the panel's *reachable* peak, where no LUT can create luminance. On the QD-OLED test panel—which ran a uniform 86.7% of the PQ spec—this produced the project's first sub-1.0 grayscale result (0.77 ΔE2000 average, 1.12 overall).

### 10.4 Advanced Color Association

HDR displays do not read the classic per-device profile association; they read the **Advanced Color list** (registry `ICMProfileAC`), reachable only through `ColorProfileAddDisplayAssociation(..., associateAsAdvancedColor: true)` with the display's DisplayConfig adapter LUID and source id. The classic WCS association APIs place profiles in a list Windows ignores while HDR is active—a silent failure mode. (A practical interop warning recorded for posterity: `DISPLAYCONFIG_RATIONAL` must be declared as two 32-bit fields; declaring it as a 64-bit integer changes the struct's alignment, inflates `DISPLAYCONFIG_PATH_TARGET_INFO` from 48 to 56 bytes, and `QueryDisplayConfig` rejects every call with `ERROR_INVALID_PARAMETER`—silently breaking both association and SDR-white-level queries.)

### 10.5 Meter Truth: Spectral Corrections

A three-filter colorimeter is only as accurate as the spectral correction matching the panel's emission spectra, and modern panels—KSF/PFS phosphor LCDs, QD-OLED—are pathological cases for generic corrections. Measured consequence on this project's two test panels: opposite-signed white errors of roughly ±11 tint steps against a common WOLED reference, each calibration faithfully reproducing its meter's misplaced D65. With panel-matched spectral samples (`.ccss`, applied via Argyll's `-X`), the KSF LCD's native state measured nearly on-target (0.89 ΔE primaries)—revealing that earlier corrections had been partly chasing a meter artifact. The application integrates the DisplayCAL community corrections database directly (searchable in-app; entries embed the full CGATS content) and remembers the correction per monitor.

### 10.6 Verification and the Noise Floor

Every calibration is verified by re-measuring a patch sweep **through the applied profile**—the compositor applies MHC2 to all content, so a patch window sees the corrected output—using the identical ΔE2000 metric as the native characterization, with the GPU ramp quiesced for the sweep's duration (the user's gamma preference is a deliberate offset, and an early verification pass that measured it produced a phantom 2.3 ΔE grayscale "regression" with untouched full-signal patches—the diagnostic signature of a gamma ramp riding the measurement). When the profile was built from the wire ladder, verification adds a **PQ tracking sweep**: FP16 wire patches through the applied profile, graded in absolute nits against ST.2084 and in ΔE ITP (BT.2124) against D65 gray at each level—the only honest way to grade a correction that claims absolute PQ tracking, and a check the SDR-range sweep is physically incapable of performing.

Two empirical rules emerged. First: **once a panel measures inside the system's noise-plus-nonlinearity floor natively (≲ 2.5 ΔE average), full gamut correction has nothing real to fix and verification reliably comes back worse**—the correct prescriptions are white-point-only correction (the matrix built with target primaries replaced by the measured ones) or no correction at all. Second: measured accuracy and perceptual match are distinct targets. Observer metamerism guarantees that displays of different spectral character can measure identical and look different side by side; the final small visual trim against a reference display is not a failure of calibration but its last legitimate step, and the tool's report says so explicitly.

---

## 11. Acknowledgements

This work builds directly on the research and implementation of **Dylan Raga** ([dylanraga/win11hdr-srgb-to-gamma2.2-icm](https://github.com/dylanraga/win11hdr-srgb-to-gamma2.2-icm)), whose identification of the Windows SDR-in-HDR transfer function problem and creation of the original LUT generator laid the foundation for this project.

Additional acknowledgements:

- **ArgyllCMS** by Graeme Gill — The `dispwin` utility enables low-level LUT loading for testing and validation
- **MHC2Gen** by dantmnf — Reference for the MHC2 ICC profile format
- **Microsoft** — Documentation on Advanced Color and HDR color management APIs

---

## 12. References

[^1]: Poynton, C. (2003). *Digital Video and HDTV: Algorithms and Interfaces*. Morgan Kaufmann. Chapter on gamma and transfer functions.

[^2]: IEC 61966-2-1:1999. *Multimedia systems and equipment - Colour measurement and management - Part 2-1: Colour management - Default RGB colour space - sRGB*.

[^3]: Stokes, M., Anderson, M., Chandrasekar, S., & Motta, R. (1996). *A Standard Default Color Space for the Internet - sRGB*. Hewlett-Packard / Microsoft.

[^4]: Raga, D. (2023). *win11hdr-srgb-to-gamma2.2-icm*. GitHub repository. Retrieved from https://github.com/dylanraga/win11hdr-srgb-to-gamma2.2-icm

[^5]: SMPTE ST 2084:2014. *High Dynamic Range Electro-Optical Transfer Function of Mastering Reference Displays*.

[^6]: Miller, S., Nezamabadi, M., & Daly, S. (2013). *Perceptual Signal Coding for More Efficient Usage of Bit Codes*. SMPTE Motion Imaging Journal, 122(4), 52-59.

[^7]: Wyszecki, G., & Stiles, W. S. (2000). *Color Science: Concepts and Methods, Quantitative Data and Formulae* (2nd ed.). Wiley-Interscience.

[^8]: Helland, T. (2012). *How to Convert Temperature (K) to RGB: Algorithm and Sample Code*. Retrieved from https://tannerhelland.com/2012/09/18/convert-temperature-rgb-algorithm-code.html

[^9]: Stevens, S. S. (1957). On the psychophysical law. *Psychological Review*, 64(3), 153-181.

[^10]: Kang, B., Moon, O., Hong, C., Lee, H., Cho, B., & Kim, Y. (2002). Design of Advanced Color Temperature Control System for HDTV Applications. *Journal of the Korean Physical Society*, 41(6), 865-871.

---

*Last updated: June 10, 2026 — added Section 10 (Colorimeter Calibration and the MHC2 Pipeline), documenting the MHC2 matrix XYZ-sandwich semantics, uniform-scale white handling, knee-safe PQ LUTs, Advanced Color association, spectral corrections, and the verification methodology. Later the same day: wire-exact HDR measurement via FP16 scRGB patches (probe-validated; the OS SDR-white value measured ~7% off the wire), full-range PQ LUT inversion with identity above the reachable peak, and the PQ tracking verification sweep.*
