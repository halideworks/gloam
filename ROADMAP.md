# Gloam Roadmap — from excellent to definitively world-class

Status baseline: **v1.3.0** (2026-07-03). The color math is reference-grade (exact PQ, Sharma-complete
ΔE2000, BT.2124 ΔE ITP, exact Planckian locus + Ohno CCT/Duv, CAT16, Oklab, CIE S 026 tables, one
D65 with fully derived matrices). Night mode is already the most colorimetrically correct
implementation available anywhere. Calibration is best-in-class for Windows-native MHC2 — including
closed-loop HDR EOTF refinement and colored HDR ΔE ITP verification that no free tool offers.
876+ unit tests, hardened updater, resilient settings.

This document is the plan for the remaining distance — the work that turns "best free tool" into
"the reference implementation other tools are judged against." Ordered by tier; each item lists
why it matters and the technical shape of the solution.

---

## Tier 0 — Trust: prove what we built (before any new features)

**0.1 Hardware validation protocol.** Everything in 1.3.0 is code- and simulation-verified. Run the
full matrix on real panels (SDR + HDR on both dev monitors, all presets, Refine HDR, per-channel
ramps, drift comp on a deliberately warm OLED): record before/after reports, archive as golden
fixtures. Any release from now on ships only after this protocol passes.

**0.2 Cross-tool cross-check.** Calibrate a panel with Gloam, verify with DisplayCAL/ArgyllCMS
(and Calman if available), and vice versa. Publish the comparison — agreement within instrument
repeatability is the strongest credibility statement an open-source tool can make, and nobody
else publishes one.

**0.3 Golden-sample regression rig.** Persist a library of real measurement sets (various panel
types: OLED, QD-OLED, VA, IPS, local-dimming LCD) and replay them through the full pipeline in CI.
Today's synthetic-panel tests catch math regressions; replayed real data catches modeling
regressions.

---

## Tier 1 — Calibration: to instrument-grade

> **Status (v1.4.0):** Tier 1 COMPLETE — all five items implemented and test-verified
> (985 tests green). Hardware validation with real instruments remains the gate before any
> public release (see Tier 0). Statuses are updated in place as items land.

**1.1 Adaptive patch placement (the DisplayCAL/Argyll OFPS gap).** `[DONE — v1.4.0]` Fixed grids spend samples where
the display is well-behaved. Instead: fit the display model after an initial coarse pass, compute
model uncertainty across the signal cube (simple leave-one-out error), and place the next batch of
patches where predicted error is highest. Iterate until the predicted worst-case model error is
below target or the patch budget is spent.

*Honest benchmark (held-out, not the planner's own objective).* Both methods fit their model from
their own measurements and predict a common dense reference set of 256 gray samples that NEITHER
measured; error is scored in perceptual ΔL*, at equal patch count. The earlier "2.53× lower
worst-case error" figure was **circular** — it scored each method on its own leave-one-out
residuals (the exact quantity the planner minimizes) — and has been withdrawn. The honest result:
adaptive's advantage is concentrated on **localized defects** a uniform grid under-samples — on a
narrow tone step it cut held-out RMS ~1.4× and 95th-percentile error ~2.8×, and on a narrow
*shadow* defect it cut shadow-region RMS ~3.5× (the perceptual ΔL* target, item 1.4-adjacent,
steers samples into the visually-critical shadows). On smooth or broadband-curvature panels, where
a uniform grid's even coverage is already near-optimal, adaptive is at **parity** (no perceptible
regression), not a win. So the honest claim is "meaningfully better where the display actually
misbehaves, no worse where it doesn't," not a blanket multiplier. Runs that stop before reaching
the accuracy target (budget exhausted, or a genuine plateau) are now reported as a **degraded**
outcome rather than unqualified success.

**1.2 Spectrometer support and CCSS *generation*.** `[DONE — v1.4.0; needs hardware validation with a real i1 Pro/ColorMunki]` Today we consume CCSS; a spectrometer
(i1Pro 2/3, ColorMunki) via spotread's spectral mode lets Gloam *create* the colorimeter
correction for the user's exact panel: measure R/G/B/W spectra, write a CCSS, auto-pair it with
the colorimeter. This removes the single largest accuracy variable for colorimeter owners and is
something only DisplayCAL does today — clumsily.

**1.3 Instrument uncertainty budgets.** `[DONE — v1.4.0]` Every report number should carry an honest error bar:
combine instrument repeatability (measured live from the median-read spreads we already collect),
CCSS/observer uncertainty, and drift-fit residual into a per-metric confidence interval.
"Grayscale avg ΔE 0.4 ± 0.2" is a fundamentally more trustworthy claim than "0.4" — and no
consumer tool does it.

**1.4 Adaptive integration/settle from live variance.** `[DONE — v1.4.0]` We scale settle time by luminance step;
next: watch the reading-to-reading variance the median logic already computes and extend
integration only when the data is actually noisy (dark VA panels) instead of by fixed rules.

**1.5 Meter-offset workflows.** `[DONE — v1.4.0; needs hardware validation with a real spectro+colorimeter pair]` Four-color matrix correction between a reference spectro and the
user's colorimeter (classic Wyszecki/ASTM E1455 style), stored per display, for labs with two
instruments.

---

## Tier 2 — HDR frontier (where the field is still soft)

**2.1 Iterated HDR closed loop.** Refine HDR currently does one multiplicative pass. Loop it with
the same keep-best/damping discipline as the SDR loop until the ladder converges (<1% everywhere
or 3 iterations). Cheap: the probe is already mounted; each pass is ~90 seconds.

**2.2 Colored HDR closed loop.** We now *measure* R/G/B/CMY error in HDR; the next step is
*correcting* it: refine the MHC2 matrix from measured primaries at multiple luminance levels
(luminance-weighted least squares over the colored rungs), catching panels whose gamut rotates
with brightness (most QD-OLEDs). Nothing on the market closes the loop on HDR color — this would
be a genuine first.

**2.3 Tone-mapping characterization.** Measure the panel's actual roll-off (dense rungs near peak,
plus APL variants), report the true knee/peak against its EDID/DXGI claims, and emit suggested
HGIG-style calibration values for games plus corrected MaxCLL/MaxFALL metadata. The gap between
claimed and real HDR behavior is the single biggest source of user confusion in HDR.

**2.4 ABL/APL characterization.** OLED brightness depends on average picture level. Sweep the same
white rung at 1/4/10/25/50/100% window sizes, chart nits-vs-APL, and annotate all other HDR
measurements with their effective APL context. Also feeds honest uncertainty (1.3).

---

## Tier 3 — Night mode & circadian science (extend the lead)

**3.1 Real melanopic dashboard.** We have CIE S 026 tables and CCSS spectra; surface it: live
melanopic EDI (lux-equivalent) of the current screen state, % reduction vs 6500K, and a nightly
"dose" curve. All computed from the *actual* panel spectrum — every competitor's "blue light
filtered" number is marketing fiction.

**3.2 Circadian scheduling by dose, not color.** Novel: let the user set a target melanopic-EDI
ceiling for the evening (e.g. <10 melanopic lux after 22:00) and have Gloam solve for the
CCT/brightness trajectory that meets it with the least visible color change (optimize in CAM16-UCS
distance). Turns night mode from an aesthetic into an instrument.

**3.3 Luminance-preserving option.** The current normalize-to-max is the right default; add an
optional constant-Y mode (compensate the luminance the warm shift removes, within headroom) for
users who want warmth without dimming.

**3.4 Ambient-adaptive white point.** With an ambient light sensor (many laptops; else a cheap USB
lux meter, else time-of-day model): adapt display white to the room's adaptation state using
CAT16 with D driven by surround luminance (CIE 159 viewing-condition logic). This is what
"TrueTone" gestures at, done with real colorimetry and user-visible math.

**3.5 JND-paced transitions.** Fade steps are mired-uniform; make them *perceptually invisible* by
pacing steps below the MacAdam/JND threshold at the current adaptation state — the transition
should be literally imperceptible, not just smooth.

---

## Tier 4 — Novel & speculative (research-grade differentiators)

**4.1 Temporal dithering above the ramp's bit depth.** The GDI ramp path is 256×16-bit input-limited;
investigate alternating between two adjacent quantized ramps at compositor cadence to synthesize
intermediate values (spatio-temporal dithering for gradients). Risky (DWM behavior, flicker
sensitivity) — prototype behind a flag, measure with the probe, publish findings either way.

**4.2 Bayesian display model.** Treat the characterization as a posterior (priors from the golden
panel library per panel type; measurements as evidence). Yields: principled uncertainty (1.3),
adaptive sampling acquisition (1.1), and drift *prediction* — "your panel has drifted an estimated
1.2 ΔE since calibration; re-verify?" from thermal/aging trend models over the stored history.

**4.3 Micro-verification. ** A 6-patch, 20-second "trust check" runnable any time (or scheduled
monthly): white/gray/black plus primaries, trended over months in a dashboard. Display aging
becomes visible, and recalibration becomes data-driven instead of superstition.

**4.4 Observer metamerism correction.** CIE 2006 physiological observer (age/field-size
parameterized) instead of the fixed 2° observer, applied through the CCSS spectral path: a
60-year-old's D65 is not a 20-year-old's. Expose as an advanced "observer age" setting with the
math documented. No shipping tool does this.

**4.5 Multi-display matching solver.** Minimize the *maximum* perceptual difference (CAM16-UCS)
across all connected panels jointly — common white AND matched grays within each panel's gamut —
rather than calibrating each panel to an absolute target it may not reach. The dual-monitor color
mismatch is the most common real-world complaint calibration tools ignore.

**4.6 Per-app color intent.** The app-exclusion machinery already exists; extend it to per-app
*rendering intent* (e.g. games get contrast-preserving gamut clip, photo apps get colorimetric)
by swapping MHC2 LUT sets on foreground-app change. Windows can't do this; Gloam's architecture
almost already can.

---

## Tier 5 — Ecosystem & credibility

- **Docs site with the science.** Publish the color-math rationale (tone-target semantics, wire-basis
  night math, MHC2 sandwich discovery, CAT16 choice) as readable articles. The documentation *is*
  the moat: make Gloam the place people learn Windows color management.
- **CLI/scripting parity** for automation (calibrate/verify/apply headless; JSON reports) — labs and
  reviewers will script it, and every scripted review is marketing.
- **Community correction database**: opt-in sharing of CCSS files and anonymized verification
  results per panel model — a public map of how real panels behave.
- **Parser hardening**: fuzz CGATS/ICC/CCSS/cube readers (they ingest untrusted files).
- **iccMAX (ICC v5) output** alongside v4 once consumers exist; keep spec-watch on Windows ACM.
- **Localization** once strings stabilize.

---

## Suggested order of attack

| Phase | Items | Status | Rationale |
|---|---|---|---|
| v1.4.0 | **Tier 1 (1.1–1.5)** | **DONE (code+tests)** | All five calibration-to-instrument-grade items landed together; instrument-grade calibration is the foundation everything else builds on |
| Gate | 0.1, 0.2 | Pending hardware | Prove 1.3.0 **and** the 1.4.0 calibration work on real instruments before any public release |
| v1.5.0 | 2.1, 3.1, 4.3, 0.3 | Queued | Cheap, high-visibility wins on existing infrastructure (iterated HDR loop, melanopic dashboard, micro-verification, golden-sample rig) |
| v1.6.0 | 2.2, 2.3, 3.2 | Queued | The HDR-color closed loop and dose-based circadian scheduling — genuine firsts |
| v1.7.0+ | 4.5, 4.2, 4.4 | Research | Multi-display matching, Bayesian model, observer metamerism |
| Ongoing | Tier 5 | Ongoing | Docs, CLI, community DB, parser hardening compound across releases |

> Tier 1 was pulled forward and completed as a single v1.4.0 push rather than spread across
> 1.5/1.6 as originally sketched — the five items share infrastructure (uncertainty feeds
> adaptive placement; spectral capture feeds meter-offset) and were cheaper to land together.

The through-line: every tier either *measures more honestly*, *corrects more precisely*, or
*proves it publicly*. That combination — not feature count — is what makes a tool the world's
reference.
