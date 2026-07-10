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

**0.1 Hardware validation protocol.** Everything in 1.3.0/1.4.0 is code- and simulation-verified. Run the
full matrix on real panels (SDR + HDR on both dev monitors, all presets incl. Adaptive, Refine HDR,
per-channel ramps, drift comp on a deliberately warm OLED): record before/after reports, archive as
golden fixtures. Any release from now on ships only after this protocol passes.

*v1.4.0 additions needing a real instrument (the software-only tests cannot exercise these because
the spotread process is faked):*
- **Spectrometer capture (1.2):** with `-N` correctly dropped for spectrometers, spotread runs its
  own white/dark calibration and may pause for a keypress under `ARGYLL_NOT_INTERACTIVE=1` — confirm
  the i1Pro/ColorMunki calibration handshake completes without hitting the startup timeout. Confirm
  the real spectral-header wording (the parser accepts steps/bands/samples) and the exact
  calibration-vs-measurement prompt strings against the bundled Argyll (V3.3.0).
- **Meter-offset CCMX (1.5):** run a real spectro+colorimeter pair, confirm the generated `.ccmx`
  measurably improves colorimeter agreement vs. the reference.
- **Uncertainty (1.3):** sanity-check that reported ± intervals bracket the spread of repeated real
  calibrations of the same panel.

**0.2 Cross-tool cross-check.** Calibrate a panel with Gloam, verify with DisplayCAL/ArgyllCMS
(and Calman if available), and vice versa. Publish the comparison — agreement within instrument
repeatability is the strongest credibility statement an open-source tool can make, and nobody
else publishes one.

**0.3 Golden-sample regression rig.** `[DONE — v1.5.0]` Persist a library of real measurement sets (various panel
types: OLED, QD-OLED, VA, IPS, local-dimming LCD) and replay them through the full pipeline in CI.
Today's synthetic-panel tests catch math regressions; replayed real data catches modeling
regressions.

*Shipped in two labeled tiers.* Tier A (exact): the measurement CSVs are round-tripped by a new
importer and replayed through the pure pipeline stages — characterization fit, HDR wire LUT build,
verification metrics, uncertainty budget — against committed `baseline.json` files with explicit
tolerances; regeneration is deliberate via `cli golden-ingest` so accepted numeric changes show up
in git review, never silently. Tier B (model-based, labeled as such): an interpolating panel model
fitted from the recording (self-checked against it first) replays the full iterated HDR loop and
asserts invariants, not exact numbers — recorded data is open-loop and cannot answer counterfactual
corrections exactly. First fixtures: the 2026-06-30 MSI MAG 271QPX and Gigabyte M27Q P hardware
runs. Runs in the existing `dotnet test` CI step.

---

## Tier 1 — Calibration: to instrument-grade

> **Status (v1.4.0):** Tier 1 COMPLETE — all five items implemented, test-verified
> (1009 tests green), and independently audited for math correctness (three expert reviews:
> formats/solver, uncertainty/statistics, adaptive/DOE). The audits confirmed the hard parts
> are correct (CCMX solver direction/scale, genuine leave-one-out, GUM quadrature) and found
> a set of real defects — all since fixed: the drift-residual term was dropping to zero for
> compensated runs (interval too narrow); the small-n median SE used the asymptotic factor;
> the adaptive plateau guard could stop under-target and report success; and the "2.53×"
> benchmark was circular (withdrawn, rebuilt honestly — see 1.1). Hardware validation with
> real instruments remains the gate before any public release (see Tier 0).

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

**2.1 Iterated HDR closed loop.** `[DONE — v1.5.0]` Refine HDR currently does one multiplicative pass. Loop it with
the same keep-best/damping discipline as the SDR loop until the ladder converges (<1% everywhere
or 3 iterations). Cheap: the probe is already mounted; each pass is ~90 seconds.

*Method.* The loop lives in Core (`HdrRefinementLoop`), delegate-driven so the simulated DWM chain
tests it end-to-end: full step on pass 1 (bit-identical to the historical single pass), damping 0.5
on passes 2–3 (`Refine` gained a damping parameter that blends correction factors toward 1 before
the clamp; the convergence/refusal gates always evaluate the undamped error). Keep-best on the
measured post-pass ladder — the display always ends on the best pass, reinstalling it if the last
pass regressed, including on cancellation. Refine's "already converged" refusal counts as success;
an installer LUT rebuild (matrix-scale mismatch) stops the loop honestly rather than iterating on
LUTs it never computed. One spotread session spans all passes; superseded refined profiles from
the same session are cleaned up. Verified to converge on fitted golden panels (see 0.3 Tier B).

**2.2 Colored HDR closed loop.** `[DONE — v1.6.0; needs hardware validation]` We now *measure* R/G/B/CMY error in HDR; the next step is
*correcting* it: refine the MHC2 matrix from measured primaries at multiple luminance levels
(luminance-weighted least squares over the colored rungs), catching panels whose gamut rotates
with brightness (most QD-OLEDs). Nothing on the market closes the loop on HDR color — this would
be a genuine first.

*Method.* The sweep measures R/G/B/C/M/Y **plus White** at each rung (the neutral anchors are
mandatory — an unanchored 3×3 fit can rotate the whole gamut around a drifted white and grade
"better" while looking worse; the fit refuses without them). The residual is modeled as
measured ≈ F·reference in XYZ and fit by luminance-weighted least squares (pairs normalized to
rung luminance, weighted √Y); the installer's new `xyzCorrectionOverride` composes the refined
matrix M′ = D⁻¹·F⁻¹·T. Keep-best loop (`HdrColorMatrixLoop`) caps at 2 passes (a colored sweep
is ~2 min), cumulative across passes (F_total = F₂·F₁), with the same refusal honesty as the
tone loop (converged <2 avg ΔE ITP, wild >25, per-element deviation cap 0.25). A changed matrix
changes the neutral scale, so the HDR tone LUTs are rebuilt open-loop at the new scale and the
UI says to run Refine HDR again for the final tone pass — the coupling is by design, not a leak.

**2.3 Tone-mapping characterization.** `[DONE — v1.6.0; needs hardware validation]` Measure the panel's actual roll-off (dense rungs near peak,
plus APL variants), report the true knee/peak against its EDID/DXGI claims, and emit suggested
HGIG-style calibration values for games plus corrected MaxCLL/MaxFALL metadata. The gap between
claimed and real HDR behavior is the single biggest source of user confusion in HDR.

*Method.* "Characterize HDR" in the report window: a dense ladder at fractions of the claimed
peak (40%…125%, so under-claiming panels are caught too) finds the true knee — first level where
tracking falls below 95%, sub-rung interpolated — and true peak; an APL sweep renders the same
white through FP16 windows covering 1/4/10/25/50/100% of the screen area (fresh renderer per
size; long settles because ABL reacts over hundreds of ms) for the nits-vs-APL curve and
full-frame peak. `ToneMappingAnalyzer` (pure, tested) emits HGIG suggestions (peak/MaxTML = the
measured window peak, MaxCLL = measured peak, MaxFALL = measured full-frame) and the result
persists on the report JSON (`ToneMappingCharacterization`, nullable/backward-compatible). The
sweep runs through whatever chain is active and the summary says which — native-panel truth vs
through-profile tracking are both legitimate questions. Also feeds 2.4's ABL context.

**2.4 ABL/APL characterization.** OLED brightness depends on average picture level. Sweep the same
white rung at 1/4/10/25/50/100% window sizes, chart nits-vs-APL, and annotate all other HDR
measurements with their effective APL context. Also feeds honest uncertainty (1.3).

---

## Tier 3 — Night mode & circadian science (extend the lead)

**3.1 Real melanopic dashboard.** `[DONE — v1.5.0]` We have CIE S 026 tables and CCSS spectra; surface it: live
melanopic EDI (lux-equivalent) of the current screen state, % reduction vs 6500K, and a nightly
"dose" curve. All computed from the *actual* panel spectrum — every competitor's "blue light
filtered" number is marketing fiction.

*Method, and the honest-numbers split.* The headline is the **% reduction vs 6500K** — ratiometric,
so the viewing-geometry assumption, brightness and white level all cancel. Absolute melanopic EDI is
secondary: corneal illuminance is estimated as E_v ≈ L·Ω_eff with a documented nominal solid angle
(0.20 sr ≈ a 27″ panel at 60 cm) and a deliberately large geometry uncertainty term (±35% rel std-u)
that inflates the EDI band and never the reduction band. Spectra come from the loaded CCSS with
provenance-graded uncertainty (own capture 5% < same-model DB 10% < generic synthesized primaries
25% rel std-u on mel-DER), plus the measured residual of the CCSS white row against the additive
R+G+B sum (flags RGBW/overlapping panels instead of hiding them). The apply pipeline publishes
deduped per-monitor state snapshots (white-shape gains, brightness excluded — dimming scales
magnitude once, not spectral shape); a Core service evaluates them plus a 5-minute keepalive into a
per-day JSONL dose store (90-day retention), charted on the dashboard with the multi-monitor sum
labeled as the upper bound it is. HDR states are SDR-white-anchored and annotated (HDR content
luminance is unknowable from the wire).

**3.2 Circadian scheduling by dose, not color.** `[DONE — v1.6.0]` Novel: let the user set a target melanopic-EDI
ceiling for the evening (e.g. <10 melanopic lux after 22:00) and have Gloam solve for the
CCT/brightness trajectory that meets it with the least visible color change (optimize in CAM16-UCS
distance). Turns night mode from an aesthetic into an instrument.

*Method.* Shipped as a **governor**, not a second schedule: a single mel-lux ceiling
(night-mode setting, 0 = off) that rides the existing kelvin schedule. Per monitor and per
apply, the scheduled state's mel-EDI is evaluated from the panel's CCSS spectra; when it
exceeds the ceiling, `CircadianDoseGovernor` scans warmer kelvins (never cooler, never
brighter), computes the minimum dimming per kelvin in closed form (mel-EDI is linear in white
luminance at fixed kelvin), and picks the candidate with minimum **CAM16-UCS ΔE′** from the
scheduled appearance — CAM16-UCS was implemented for this (`Cam16Ucs`, forward model + ΔE′,
pinned to the published Li et al. 2017 test vector; dim surround, L_A = white/5) because ΔE
ITP-class metrics cannot price a luminance change against a chromaticity change across
adaptation. Unreachable ceilings degrade honestly (warm/dim floor, logged "best effort").
Solutions are memoized; the governor can never take down the apply path.

**3.3 Luminance-preserving option.** `[DONE — v1.5.0]` The current normalize-to-max is the right default; add an
optional constant-Y mode (compensate the luminance the warm shift removes, within headroom) for
users who want warmth without dimming.

*Method.* The rescale s = 1/Y(m) uses the Y row of the actual wire-basis RGB→XYZ matrix (sRGB or
Rec.2020 — both white-normalized, so wire-Y is CIE Y), clamped to representable headroom:
s ≤ C/(d·max(m)) with C = min(2, HdrPeakNits/sdrWhite) on active HDR and 1.0 on SDR, where the
256-entry GDI ramp clips — so on SDR at full brightness the option is honestly inert, and user
dimming d cancels in the preserved ratio (constant-Y compensates only the temperature loss, never
fights the brightness slider). Partial preservation is the graceful clamp behavior. The HDR
headroom blend gains an anchored power remap (compress [sdrWhite, 10 kn] onto [boosted anchor,
10 kn]) so the boosted SDR-region white never produces a non-monotonic LUT; the measured-
calibration LUT path caps the boost at 1.0 (the tone-curve inversion cannot soundly extrapolate
above its fit). UltraNight is excluded — its dimming is deliberate melanopic-dose reduction. The
preserved Y is pre-MHC2 (accepted approximation; the compositor correction is near-neutral on the
white axis for a calibrated display). 6500K stays a bit-exact (1,1,1) identity by short-circuit.

**3.4 Ambient-adaptive white point.** With an ambient light sensor (many laptops; else a cheap USB
lux meter, else time-of-day model): adapt display white to the room's adaptation state using
CAT16 with D driven by surround luminance (CIE 159 viewing-condition logic). This is what
"TrueTone" gestures at, done with real colorimetry and user-visible math.

**3.5 JND-paced transitions.** `[DONE — v1.5.0]` Fade steps are mired-uniform; make them *perceptually invisible* by
pacing steps below the MacAdam/JND threshold at the current adaptation state — the transition
should be literally imperceptible, not just smooth.

*Method.* The fixed 0.05-mired heuristic became the fallback; the live per-step ceiling is
0.5 ΔE ITP per hardware write (BT.2124, ~1 unit ≈ 1 JND — a conservative pre-adaptation bound,
documented as such), derived from a central finite difference of ΔE ITP per mired between
consecutive adapted whites under the ACTIVE algorithm at a 100 cd/m² sRGB reference. That prices in
the luminance dimension the mired heuristic ignored: UltraNight's deliberate dimming automatically
gets tighter steps, and with constant-Y on, pacing takes the max of the preserved and unpreserved
variants (one global fade drives both SDR and HDR paths). The fade trajectory is untouched —
duration is a user promise and wins over imperceptibility when a short fade over a long span cannot
stay sub-JND at the ~4 writes/sec coalescer floor (logged once per fade window). The integer-Kelvin
dedupe floor was *measured*, not assumed: a 1 K step stays under the ceiling across 1900–6500K for
every algorithm (regression-tested tripwire; the mired-grid dedupe contingency stays documented).

---

## Tier 4 — Novel & speculative (research-grade differentiators)

**4.1 Temporal dithering above the ramp's bit depth.** The GDI ramp path is 256×16-bit input-limited;
investigate alternating between two adjacent quantized ramps at compositor cadence to synthesize
intermediate values (spatio-temporal dithering for gradients). Risky (DWM behavior, flicker
sensitivity) — prototype behind a flag, measure with the probe, publish findings either way.

**4.2 Bayesian display model.** `[PARTIAL — v1.7.0: drift prediction shipped; the rest stays research]`
Treat the characterization as a posterior (priors from the golden
panel library per panel type; measurements as evidence). Yields: principled uncertainty (1.3),
adaptive sampling acquisition (1.1), and drift *prediction* — "your panel has drifted an estimated
1.2 ΔE since calibration; re-verify?" from thermal/aging trend models over the stored history.

*What shipped.* `DriftPredictor` fits the trust-check trend (per installed profile): slope with
standard error and a significance gate (no statement below 3 points / 7 days of span — failing
the gate means saying LESS, never more), predictive interval combining regression SE with the
checks' own U95, and a projected recalibration-threshold crossing date. Surfaced in the trust
check window. *What deliberately did not:* the Bayesian characterization prior and acquisition —
they need a golden-panel library per panel type, and we have two panels; revisit when the
community correction database (Tier 5) exists.

**4.3 Micro-verification. ** `[DONE — v1.5.0]` A 6-patch, 20-second "trust check" runnable any time (or scheduled
monthly): white/gray/black plus primaries, trended over months in a dashboard. Display aging
becomes visible, and recalibration becomes data-driven instead of superstition.

*Method.* White / Gray 40% / Black / R / G / B through the installed profile (same ramp bypass as
the verify sweep — a check made through night mode would poison the trend), graded by the shared
metrics path with an uncertainty budget; black by nits only (ΔE at black is metrologically
meaningless). Runs refuse without an active calibration — a trust check without a reference is
noise. Per-monitor append-only JSONL trend under `reports\trend`, charted (avg ΔE with ±U95 band,
white Duv) in a dedicated window with CSV export. Drift alerts are honest by construction: the
baseline resets when the installed profile changes, and no alert fires unless the drift exceeds
BOTH the RSS-combined U95 of the two runs and a practical floor (1.0 avg ΔE / 0.003 Duv). Monthly
cadence is an opt-in reminder toast — never an auto-run, which cannot assume the probe is attached
and aimed.

**4.4 Observer metamerism correction.** `[DEFERRED — dropped from v1.7.0]` CIE 2006 physiological observer (age/field-size
parameterized) instead of the fixed 2° observer, applied through the CCSS spectral path: a
60-year-old's D65 is not a 20-year-old's. Expose as an advanced "observer age" setting with the
math documented. No shipping tool does this.

*Why deferred:* the CIE 2006/CIE 170 model needs its published reference tables (lens and
macular-pigment optical densities, low-density cone absorbance spectra) vendored and verified
against the standard's own worked values. Implementing it from memory would mean fabricating
spectral data — a violation of the project's honesty bar worse than the feature's absence.
Pick it back up when the tables can be vendored from CVRL with checksums and a verification
test against published CMFs.

**4.5 Multi-display matching solver.** `[DONE — v1.7.0 (model-based tier)]` Minimize the *maximum* perceptual difference (CAM16-UCS)
across all connected panels jointly — common white AND matched grays within each panel's gamut —
rather than calibrating each panel to an absolute target it may not reach. The dual-monitor color
mismatch is the most common real-world complaint calibration tools ignore.

*Method.* Tray → "Match Displays…": `DisplayMatchSolver` grid-searches white chromaticity over
the calibrated whites' neighborhood; per candidate, each panel's drive comes from its measured
RGB→XYZ inverse (infeasible when any channel goes non-positive), the common luminance is the
dimmest panel's reach at that chromaticity, and the winner minimizes the worst-case CAM16-UCS
ΔE′ from each panel's current state. The solution applies as per-monitor channel gains
(normalize-to-max, nothing clips) + a brightness cap — pure existing plumbing, reversible via
Reset Trims. Honestly labeled MODEL-BASED: it runs on stored characterizations, so residual
per-panel calibration error and inter-panel instrument metamerism are invisible to it; a
probe-assisted measured matching pass is the natural second tier.

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
| Gate | 0.1, 0.2 | Partially done | Real hardware run completed 2026-06-30/07-09 on the MSI/M27Q panels with the i1 Display Pro (its recordings seed the 0.3 golden fixtures); the spectro-dependent validations (1.2 capture handshake, 1.5 CCMX pair) remain pending until a spectrometer is available, as does the 0.2 cross-tool comparison |
| v1.5.0 | **2.1, 3.1, 4.3, 0.3 + 3.3, 3.5** | **DONE (code+tests)** | The queued quartet plus two night-mode backlog items pulled in (constant-Y warmth, JND-paced fades); 1116 tests green incl. golden replay of real panel data. Interactive verification on hardware (multi-pass Refine HDR, a live trust check, the melanopic card) is the release gate |
| v1.6.0 | **2.2, 2.3, 3.2** | **DONE (code+tests)** | The HDR-color closed loop and dose-based circadian scheduling — genuine firsts; 1150 tests green. 2.2/2.3 need a real HDR-panel run before release |
| v1.7.0 | **4.5, 4.2 (drift prediction)** | **DONE (code+tests)** | Model-based multi-display matching + trend-fitted drift prediction; 1162 tests green. 4.4 observer metamerism DEFERRED (needs vendored CIE 2006 reference tables); full Bayesian prior deferred pending a golden-panel library |
| Ongoing | Tier 5 | Ongoing | Docs, CLI, community DB, parser hardening compound across releases |

> Tier 1 was pulled forward and completed as a single v1.4.0 push rather than spread across
> 1.5/1.6 as originally sketched — the five items share infrastructure (uncertainty feeds
> adaptive placement; spectral capture feeds meter-offset) and were cheaper to land together.
> v1.5.0 likewise absorbed 3.3 and 3.5 from the backlog: constant-Y and JND pacing share the
> multiplier/luminance math, and the pacing metric must see the constant-Y rescale to be correct.

The through-line: every tier either *measures more honestly*, *corrects more precisely*, or
*proves it publicly*. That combination — not feature count — is what makes a tool the world's
reference.
