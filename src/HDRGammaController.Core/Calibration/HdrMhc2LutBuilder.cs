using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Builds the per-channel MHC2 tone LUTs for a display running in HDR.
    ///
    /// In HDR the wire format is BT.2100 PQ, and the MHC2 LUTs operate on PQ SIGNAL
    /// (0..1 ≙ 0..10000 nits via ST.2084) — not on the gamma-encoded SDR signal the SDR
    /// path uses. The goal is PQ tracking: CONTENT signal v should produce ST2084(v) nits.
    /// Because the DWM applies the (uniformly scaled) gamut matrix BEFORE these LUTs, the
    /// builder composes that scale into the inversion (see Build's matrixNeutralScale) so
    /// the matrix+LUT chain — not the LUT in isolation — tracks PQ.
    ///
    /// Two measurement sources, by preference:
    ///  - HDR wire-ladder patches (ColorPatch.Nits, FP16 scRGB rendering): wire positions
    ///    are exact and span the full desktop+HDR range, so the LUT inverts MEASURED
    ///    response everywhere up to the panel's reachable peak, going identity only above.
    ///  - SDR-window grayscale fallback: Windows maps SDR content onto the wire as
    ///    PQ(sdrWhiteNits · srgbEotf(v)) — an assumption, and coverage stops at SDR white,
    ///    so correction is knee-safe-limited and blends to identity inside the SDR range.
    /// </summary>
    public static class HdrMhc2LutBuilder
    {
        public sealed record Result(
            double[] LutR, double[] LutG, double[] LutB,
            double MeasuredBlackNits, double MeasuredPeakNits,
            bool WireExact = false,
            double MatrixNeutralScale = 1.0);

        /// <summary>
        /// Outcome of <see cref="Refine"/>: either a refined LUT, or null with the reason the
        /// refinement was refused. <see cref="AverageAbsErrorBefore"/> is the mean |Y/req − 1|
        /// of the post-install measurements that drove (or refused) the refinement.
        /// </summary>
        public sealed record RefinementResult(
            Result? Refined, string? RefusalReason,
            double AverageAbsErrorBefore, int RungCount);

        private const int LutSamples = 1024;

        // Closed-loop refinement policy (see Refine).
        private const int RefineMinRungs = 4;
        private const double RefineMaxAbsError = 0.35;   // any rung beyond ±35% → something else is wrong
        private const double RefineConvergedError = 0.01; // avg |e−1| under 1% → not worth a reinstall
        private const double RefineClampMin = 0.7;       // per-point correction factor bounds
        private const double RefineClampMax = 1.4;

        /// <remarks>
        /// The LUT is NEUTRAL (identical per channel): white-point correction lives entirely
        /// in the (uniformly scaled, absolute) gamut matrix. Per-channel gains here would
        /// re-tint saturated colors the matrix already placed.
        /// </remarks>
        /// <param name="matrixNeutralScale">
        /// The uniform scale s (0 &lt; s ≤ 1) baked into the MHC2 gamut matrix that Windows
        /// applies BEFORE these LUTs. The LUT model composes it in (see the MATRIX
        /// COMPOSITION comment in the sample loop); 1.0 (identity/no matrix dimming)
        /// reproduces the uncomposed build bit-for-bit.
        /// </param>
        public static Result Build(
            IEnumerable<MeasurementResult> measurements,
            double sdrWhiteNits,
            double matrixNeutralScale = 1.0)
        {
            if (!double.IsFinite(sdrWhiteNits) || sdrWhiteNits < 40 || sdrWhiteNits > 1000)
                throw new ArgumentOutOfRangeException(nameof(sdrWhiteNits),
                    $"SDR white level {sdrWhiteNits:F0} nits is implausible.");

            if (!double.IsFinite(matrixNeutralScale) || matrixNeutralScale <= 0.0 || matrixNeutralScale > 1.0 + 1e-9)
                throw new ArgumentOutOfRangeException(nameof(matrixNeutralScale),
                    $"Matrix neutral-axis scale {matrixNeutralScale} must be in (0, 1] — the uniform-scale policy only ever dims.");

            // PREFERRED: HDR wire-ladder patches (ColorPatch.Nits set) rendered through the
            // FP16 scRGB path. Their wire position is EXACT - PQ⁻¹(requested nits) - with no
            // SDR-mapping assumption, and they reach far above SDR white, so the LUT can
            // correct with measured data across the whole desktop+HDR range. (Validated on
            // the MAG 271QPX: probe confirmed FP16 wire positions are exact while the OS
            // SDR-white value was ~7% off.)
            var wirePoints = measurements
                .Where(IsFiniteWireMeasurement)
                .Select(m => (
                    P: TransferFunctions.PqInverseEotf(m.Patch.Nits!.Value),
                    Nits: m.Xyz.Y))
                .OrderBy(t => t.P)
                .ToList();
            var points = wirePoints;
            bool wireExact = IsUsableWireLadder(wirePoints);

            // FALLBACK: SDR-window grayscale patches. Windows maps SDR content onto the wire
            // as PQ(sdrWhiteNits · srgbEotf(v)) - an ASSUMPTION (the OS-reported white level
            // can be off), and coverage stops at SDR white.
            if (!wireExact)
                points = measurements
                    .Where(IsFiniteGrayscaleMeasurement)
                    .Select(m => (
                        P: TransferFunctions.PqInverseEotf(sdrWhiteNits * TransferFunctions.SrgbEotf(m.Patch.DisplayRgb.R)),
                        Nits: m.Xyz.Y))
                    .OrderBy(t => t.P)
                    .ToList();

            if (points.Count < 5)
                throw new InvalidOperationException(
                    $"Need at least 5 valid grayscale measurements for an HDR LUT (have {points.Count}).");

            // Enforce a monotonic response (measurement noise can produce tiny inversions).
            for (int i = 1; i < points.Count; i++)
                if (points[i].Nits < points[i - 1].Nits)
                    points[i] = (points[i].P, points[i - 1].Nits);

            double blackNits = points[0].Nits;
            double peakNits = points[^1].Nits;
            if (peakNits <= blackNits * 1.5)
                throw new InvalidOperationException(
                    $"Measured HDR grayscale has almost no range ({blackNits:F2}–{peakNits:F2} nits) - readings look invalid.");

            var lut = new double[LutSamples];
            double blendStartNits, blendEndNits;
            if (wireExact)
            {
                // Wire positions are measured, not assumed, so the inversion is valid across
                // the entire measured span - including the panel's tone-mapping knee, which
                // is now just part of the measured response. Identity only ABOVE the top
                // measured point: beyond the panel's reachable output the LUT must leave the
                // panel's own rolloff alone (it cannot create luminance that isn't there).
                blendStartNits = peakNits * 0.90;
                blendEndNits = peakNits;
            }
            else
            {
                // Correct fully only in the lower half of the measured range; fade to IDENTITY
                // between 50% and 80% of it. Two hard-won reasons (verified on the M27Q):
                //  1. The upper range sits inside the panel's HDR tone-mapping knee, where the
                //     ASSUMED wire-axis model breaks down - "correcting" the knee overshoots
                //     and just dims highlights (verified white 189 nits instead of ~220).
                //  2. The LUT is applied per channel AFTER the gamut matrix. Strong curvature
                //     near the top distorts the channel ratios of unequal drive values — which
                //     is exactly the matrix's D65-corrected white (it measured x=0.301, blue-
                //     green, with the aggressive top-end correction). Identity near the top
                //     keeps the white point the matrix worked for.
                blendStartNits = blackNits + (peakNits - blackNits) * 0.50;
                blendEndNits = blackNits + (peakNits - blackNits) * 0.80;
            }

            for (int i = 0; i < LutSamples; i++)
            {
                double p = i / (double)(LutSamples - 1);

                // MATRIX COMPOSITION (the M5 fix). Windows evaluates the MHC2 pipeline as
                //     wire → PQ-decode → gamut matrix (uniformly scaled by s) → PQ-encode → LUT → panel
                // but the panel response in `points` was measured with NO matrix installed.
                // At apply time this LUT therefore never sees raw content wire positions:
                // on the NEUTRAL axis the matrix scales linear luminance by exactly s —
                // both the display and target RGB→XYZ matrices are white-normalized to
                // Y=1, so the absolute matrix maps content white to a display drive of
                // relative luminance 1 (chromaticity changes, luminance does not) and the
                // uniform scale is the only neutral-axis luminance change. Content at PQ
                // position v thus reaches this LUT at w'(v) = PQ⁻¹(s·PQ(v)).
                //
                // Building the LUT against raw positions (the old behavior) made the
                // installed matrix+LUT chain output s·d nits instead of the absolute
                // target d for content d, and — because PQ is nonlinear and the LUT is
                // per-channel over unequal post-matrix drives — re-tinted near-neutrals
                // (fallback reason #2 below describes that failure). Composing s into the
                // model: LUT input p corresponds to content luminance PQ(p)/s, so invert
                // the measured response at THAT target. Then LUT(w'(v)) = f⁻¹(PQ(v)):
                // absolute PQ tracking through matrix+LUT up to the panel's reachable
                // peak, identity above it, exactly as designed. With s = 1 the division
                // is exact and the output is bit-identical to the pre-composition build.
                double desired = TransferFunctions.PqEotf(p) / matrixNeutralScale;

                // Analytic passthrough: identity — preserves the panel's own behavior.
                double analytic = p;

                double v;
                if (desired >= blendEndNits)
                {
                    v = analytic;
                }
                else
                {
                    double corrected = InverseResponse(points, Math.Max(desired, blackNits));
                    if (desired <= blendStartNits)
                    {
                        v = corrected;
                    }
                    else
                    {
                        double t = (desired - blendStartNits) / Math.Max(blendEndNits - blendStartNits, 1e-9);
                        t = Math.Clamp(t, 0, 1);
                        double s = t * t * (3 - 2 * t);
                        v = corrected + (analytic - corrected) * s;
                    }
                }
                lut[i] = Math.Clamp(v, 0.0, 1.0);
            }

            // Monotonic cleanup — the regamma LUT must never invert.
            for (int i = 1; i < LutSamples; i++)
                if (lut[i] < lut[i - 1]) lut[i] = lut[i - 1];

            return new Result(lut, (double[])lut.Clone(), (double[])lut.Clone(), blackNits, peakNits, wireExact,
                matrixNeutralScale);
        }

        // ---- Closed-loop refinement -------------------------------------------------------

        /// <summary>
        /// One multiplicative closed-loop refinement of an installed HDR tone LUT — the
        /// Calman/ColourSpace-style EOTF iteration the open-loop build cannot do alone.
        ///
        /// <paramref name="postInstallMeasurements"/> are wire-ladder patches (ColorPatch.Nits
        /// = requested nits) measured THROUGH the active matrix+LUT correction. For each rung
        /// the multiplicative luminance error is e = measuredY / requestedNits in linear
        /// light. e is interpolated across LUT input positions in PQ-signal space (each rung's
        /// input position is PQ⁻¹(s·requested), because the matrix's neutral scale s runs
        /// BEFORE the LUT — same domain Build inverts in), clamped per point to
        /// [0.7, 1.4], and each LUT entry's output is divided by it in linear light:
        /// out' = PQ⁻¹(PQ(out) / e). Under a locally-smooth panel gain this lands the chain on
        /// target: if the chain currently emits e·d for target d, driving the wire that decodes
        /// to PQ(out)/e emits (e·d)/e = d.
        ///
        /// Preserved invariants: monotonicity (enforced), both endpoints, and the identity
        /// blend above the measured range — the correction factor fades smoothly to 1 between
        /// the top measured rung and the start of Build's identity blend
        /// (0.9 × <paramref name="targetPeakNits"/> in content nits), and entries at or above
        /// it are copied bit-for-bit.
        ///
        /// Refuses (Refined = null + reason) when: fewer than 4 valid rungs, any |e−1| &gt; 0.35
        /// (a mis-applied profile or drifted panel a multiplicative touch-up can't fix), or
        /// the average |e−1| &lt; 1% (already converged — a reinstall would churn for nothing).
        /// </summary>
        /// <param name="existing">The LUT currently installed (as returned by <see cref="Build"/>).</param>
        /// <param name="postInstallMeasurements">Wire-ladder measurements taken through the installed correction.</param>
        /// <param name="targetPeakNits">
        /// The panel's reachable peak the existing LUT was built against (its
        /// <see cref="Result.MeasuredPeakNits"/>): anchors where the identity blend begins.
        /// </param>
        public static RefinementResult Refine(
            Result existing,
            IEnumerable<MeasurementResult> postInstallMeasurements,
            double targetPeakNits)
        {
            if (existing == null) throw new ArgumentNullException(nameof(existing));
            if (existing.LutR is not { Length: >= 2 })
                throw new ArgumentException("Existing LUT is empty.", nameof(existing));
            if (!double.IsFinite(targetPeakNits) || targetPeakNits <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetPeakNits));

            double s = existing.MatrixNeutralScale;

            // Valid rungs, restricted to the region the LUT actually corrects: at and above
            // Build's identity blend (≥ 90% of the reachable peak) the measured error is the
            // panel's own rolloff, which refinement must not chase. Duplicate requests
            // (repeat reads) are averaged.
            var rungs = postInstallMeasurements
                .Where(IsFiniteWireMeasurement)
                .Where(m => m.Patch.Nits!.Value > 0 && m.Xyz.Y > 0)
                .Where(m => m.Patch.Nits!.Value < targetPeakNits * 0.90)
                .GroupBy(m => m.Patch.Nits!.Value)
                .Select(g => (Req: g.Key, Y: g.Average(m => m.Xyz.Y)))
                .OrderBy(t => t.Req)
                .ToList();

            double avgAbsError = rungs.Count > 0
                ? rungs.Average(r => Math.Abs(r.Y / r.Req - 1.0))
                : 0.0;

            if (rungs.Count < RefineMinRungs)
                return new RefinementResult(null,
                    $"only {rungs.Count} valid rung(s) inside the corrected range (need at least {RefineMinRungs})",
                    avgAbsError, rungs.Count);

            foreach (var (req, y) in rungs)
            {
                double err = y / req - 1.0;
                if (Math.Abs(err) > RefineMaxAbsError)
                    return new RefinementResult(null,
                        $"luminance error at {req:F0} nits is {err:+0.0%;-0.0%} — beyond the ±{RefineMaxAbsError:P0} " +
                        "refinement window, so something else is wrong (profile not active, night mode on, or panel " +
                        "drift); re-run the calibration instead",
                        avgAbsError, rungs.Count);
            }

            if (avgAbsError < RefineConvergedError)
                return new RefinementResult(null,
                    $"already converged (average luminance error {avgAbsError:P2}) — a refinement pass would not improve it",
                    avgAbsError, rungs.Count);

            // Correction-factor knots over LUT INPUT positions. Content requesting d nits
            // reaches the LUT at PQ⁻¹(s·d) (the matrix's neutral scale runs first), so that is
            // where its measured error lives.
            var knots = rungs
                .Select(r => (
                    X: TransferFunctions.PqInverseEotf(s * r.Req),
                    E: Math.Clamp(r.Y / r.Req, RefineClampMin, RefineClampMax)))
                .ToList();

            // Fade region: from the top measured rung to where Build's identity blend begins
            // (content nits 0.9 × peak ⇒ input position PQ⁻¹(s·0.9·peak)). At and above the
            // fade end the factor is exactly 1 and entries are copied untouched, so the
            // identity blend and top endpoint survive bit-for-bit.
            double xTop = knots[^1].X;
            double xFadeEnd = TransferFunctions.PqInverseEotf(s * targetPeakNits * 0.90);
            if (xFadeEnd <= xTop)
                xFadeEnd = Math.Min(1.0, xTop + 1e-3);

            int n = existing.LutR.Length;
            var refined = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = i / (double)(n - 1);
                double e = CorrectionFactorAt(knots, x, xFadeEnd);
                if (e == 1.0)
                {
                    refined[i] = existing.LutR[i];
                    continue;
                }
                double outNits = TransferFunctions.PqEotf(existing.LutR[i]);
                refined[i] = Math.Clamp(TransferFunctions.PqInverseEotf(outNits / e), 0.0, 1.0);
            }

            // Endpoints preserved exactly: black stays at the measured black inversion point,
            // and the top entry is inside the untouched identity region anyway.
            refined[0] = existing.LutR[0];
            refined[n - 1] = existing.LutR[n - 1];

            // Monotonic cleanup — the regamma LUT must never invert.
            for (int i = 1; i < n; i++)
                if (refined[i] < refined[i - 1]) refined[i] = refined[i - 1];

            return new RefinementResult(
                new Result(refined, (double[])refined.Clone(), (double[])refined.Clone(),
                    existing.MeasuredBlackNits, existing.MeasuredPeakNits,
                    existing.WireExact, existing.MatrixNeutralScale),
                null, avgAbsError, rungs.Count);
        }

        /// <summary>
        /// Mean |measuredY / requestedNits − 1| over valid wire-ladder measurements — the
        /// figure Refine gates on, shared so callers can report before/after consistently.
        /// Returns NaN when no valid rungs exist.
        /// </summary>
        public static double AverageAbsLuminanceError(IEnumerable<MeasurementResult> measurements)
        {
            var errors = measurements
                .Where(IsFiniteWireMeasurement)
                .Where(m => m.Patch.Nits!.Value > 0 && m.Xyz.Y > 0)
                .Select(m => Math.Abs(m.Xyz.Y / m.Patch.Nits!.Value - 1.0))
                .ToList();
            return errors.Count > 0 ? errors.Average() : double.NaN;
        }

        /// <summary>
        /// The interpolated multiplicative error at LUT input position <paramref name="x"/>:
        /// flat extension below the first knot (the toe inherits the darkest rung's error),
        /// monotone-safe linear interpolation between knots (each segment stays inside its
        /// endpoints' clamped values, so no PCHIP-style overshoot is possible), and a
        /// smoothstep fade from the last knot's error to exactly 1 by <paramref name="xFadeEnd"/>.
        /// </summary>
        private static double CorrectionFactorAt(
            IReadOnlyList<(double X, double E)> knots, double x, double xFadeEnd)
        {
            if (x >= xFadeEnd) return 1.0;
            if (x <= knots[0].X) return knots[0].E;

            var (xTop, eTop) = knots[^1];
            if (x > xTop)
            {
                double t = (x - xTop) / Math.Max(xFadeEnd - xTop, 1e-9);
                t = Math.Clamp(t, 0, 1);
                double sm = t * t * (3 - 2 * t);
                return eTop + (1.0 - eTop) * sm;
            }

            for (int i = 1; i < knots.Count; i++)
            {
                if (x <= knots[i].X)
                {
                    double span = knots[i].X - knots[i - 1].X;
                    double t = span <= 1e-12 ? 0 : (x - knots[i - 1].X) / span;
                    return knots[i - 1].E + (knots[i].E - knots[i - 1].E) * t;
                }
            }
            return eTop;
        }

        private static bool IsFiniteWireMeasurement(MeasurementResult m) =>
            m.IsValid &&
            m.Patch.Nits is double nits &&
            double.IsFinite(nits) &&
            nits >= 0.0 &&
            double.IsFinite(m.Xyz.Y) &&
            m.Xyz.Y >= 0.0;

        private static bool IsFiniteGrayscaleMeasurement(MeasurementResult m) =>
            m.IsValid &&
            m.Patch.Category == PatchCategory.Grayscale &&
            double.IsFinite(m.Patch.DisplayRgb.R) &&
            m.Patch.DisplayRgb.R is >= 0.0 and <= 1.0 &&
            double.IsFinite(m.Xyz.Y) &&
            m.Xyz.Y >= 0.0;

        private static bool IsUsableWireLadder(IReadOnlyList<(double P, double Nits)> points)
        {
            int distinctWirePositions = points
                .Select(p => Math.Round(p.P, 12))
                .Distinct()
                .Count();
            if (distinctWirePositions < 5)
                return false;

            double maxWireNits = TransferFunctions.PqEotf(points[^1].P);
            return maxWireNits >= 100.0;
        }

        /// <summary>The wire PQ signal that produced <paramref name="nits"/> on the measured panel.</summary>
        private static double InverseResponse(List<(double P, double Nits)> points, double nits)
        {
            if (nits <= points[0].Nits) return points[0].P;
            for (int i = 1; i < points.Count; i++)
            {
                if (nits <= points[i].Nits)
                {
                    double span = points[i].Nits - points[i - 1].Nits;
                    double t = span <= 1e-9 ? 0 : (nits - points[i - 1].Nits) / span;
                    return points[i - 1].P + (points[i].P - points[i - 1].P) * t;
                }
            }
            return points[^1].P;
        }
    }
}
