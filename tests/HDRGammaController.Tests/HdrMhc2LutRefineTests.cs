using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Closed-loop refinement of the HDR PQ tone LUT (HdrMhc2LutBuilder.Refine): a LUT built
    /// open-loop is re-measured THROUGH the installed correction and multiplicatively
    /// corrected once. These tests simulate the full DWM chain — content nits → matrix
    /// neutral scale → PQ encode → LUT → panel — with a panel that has drifted since the
    /// calibration (dark overshoot, bright undershoot) and assert the refined LUT drives the
    /// simulated response onto ST.2084 while preserving every Build invariant.
    /// </summary>
    public class HdrMhc2LutRefineTests
    {
        private const double SdrWhite = 200.0;
        private static readonly double[] Ladder = { 0, 2, 4, 8, 16, 32, 64, 100, 150, 220, 320, 450, 650, 1000 };

        // The rung set the report window measures through the installed correction
        // (below the identity blend: ≤ 0.85 × reachable peak of 867 → up to 650).
        private static readonly double[] RefineRungs = { 2, 4, 8, 16, 32, 64, 100, 150, 220, 320, 450, 650 };

        /// <summary>Wire-ladder calibration measurements: requested nits sets the exact wire position.</summary>
        private static List<MeasurementResult> SimulateWireLadder(Func<double, double> panelNitsOfPq)
        {
            return Ladder.Select((n, i) => MakeWireMeasurement(n, panelNitsOfPq(TransferFunctions.PqInverseEotf(n)), i)).ToList();
        }

        private static MeasurementResult MakeWireMeasurement(double requestedNits, double measuredNits, int index = 0) => new()
        {
            Patch = new ColorPatch
            {
                Name = $"PQ {requestedNits:F0} nits",
                DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                Nits = requestedNits,
                Category = PatchCategory.General,
                Index = index,
            },
            Xyz = new CieXyz(measuredNits * 0.95, measuredNits, measuredNits * 1.08),
        };

        /// <summary>Linear interpolation of the 1024-sample LUT at wire position p.</summary>
        private static double SampleLut(double[] lut, double p)
        {
            double x = Math.Clamp(p, 0.0, 1.0) * (lut.Length - 1);
            int i = (int)Math.Floor(x);
            if (i >= lut.Length - 1) return lut[^1];
            double t = x - i;
            return lut[i] + (lut[i + 1] - lut[i]) * t;
        }

        /// <summary>
        /// The DWM pipeline on the neutral axis: content at <paramref name="contentNits"/> is
        /// scaled by the matrix's uniform neutral scale s, PQ-encoded onto the wire, run
        /// through the tone LUT, and emitted by the panel.
        /// </summary>
        private static double Chain(double[] lut, Func<double, double> panelNitsOfPq, double contentNits, double s = 1.0)
        {
            double x = TransferFunctions.PqInverseEotf(s * contentNits);
            return panelNitsOfPq(SampleLut(lut, x));
        }

        /// <summary>
        /// Post-calibration drift as a multiplicative luminance error on the panel output:
        /// +8% in the shadows sliding smoothly (in log-nits) to −3% in the highlights.
        /// </summary>
        private static double Drift(double nits)
        {
            double t = Math.Clamp(
                (Math.Log10(Math.Max(nits, 0.01)) - 1.0) / (Math.Log10(300) - 1.0), 0, 1);
            double sm = t * t * (3 - 2 * t);
            return 1.08 + (0.97 - 1.08) * sm;
        }

        private const double Gain = 0.867; // the calibrated-out flat panel gain (MAG 271QPX probe)

        private static double CleanPanel(double p) => Math.Max(TransferFunctions.PqEotf(p) * Gain, 0.02);

        private static double DriftedPanel(double p)
        {
            double baseNits = CleanPanel(p);
            return baseNits * Drift(baseNits);
        }

        /// <summary>Measures the refinement rungs through the current LUT on the drifted panel.</summary>
        private static List<MeasurementResult> MeasureThroughChain(
            double[] lut, Func<double, double> panel, IEnumerable<double> rungs, double s = 1.0)
        {
            return rungs.Select((n, i) => MakeWireMeasurement(n, Chain(lut, panel, n, s), i)).ToList();
        }

        // ---- The headline case: drift corrected to <1.5% at every measured rung ----------

        [Fact]
        public void Refine_DrivesDriftedPanel_WithinOnePointFivePercent_AtEveryRung()
        {
            // Calibrate open-loop against the clean panel, then let the panel drift (+8%
            // dark / −3% bright). The refined LUT must bring the through-chain response to
            // within 1.5% of the requested nits at every measured rung.
            var existing = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite);
            Assert.True(existing.WireExact);

            var before = MeasureThroughChain(existing.LutR, DriftedPanel, RefineRungs);
            double beforeWorst = before.Max(m => Math.Abs(m.Xyz.Y / m.Patch.Nits!.Value - 1.0));
            Assert.True(beforeWorst > 0.02, "test setup: drift should produce a visible pre-refinement error");

            var outcome = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits);
            Assert.Null(outcome.RefusalReason);
            Assert.NotNull(outcome.Refined);
            var refined = outcome.Refined!;

            foreach (double n in RefineRungs)
            {
                double y = Chain(refined.LutR, DriftedPanel, n);
                Assert.True(Math.Abs(y / n - 1.0) < 0.015,
                    $"at {n:F0} nits refined chain emitted {y:F2} ({y / n - 1.0:+0.0%;-0.0%}); expected <1.5%");
            }
        }

        [Fact]
        public void Refine_PreservesMonotonicity_IdentityBlend_AndEndpoints()
        {
            var existing = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite);
            var before = MeasureThroughChain(existing.LutR, DriftedPanel, RefineRungs);
            var refined = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits).Refined!;

            // Neutral (identical channels), like Build.
            Assert.Equal(refined.LutR, refined.LutG);
            Assert.Equal(refined.LutR, refined.LutB);

            // Monotone and in the s15Fixed16-representable [0, 1] range.
            for (int i = 0; i < refined.LutR.Length; i++)
            {
                Assert.InRange(refined.LutR[i], 0.0, 1.0);
                if (i > 0)
                    Assert.True(refined.LutR[i] >= refined.LutR[i - 1], $"refined LUT not monotonic at {i}");
            }

            // Endpoints exactly preserved.
            Assert.Equal(existing.LutR[0], refined.LutR[0]);
            Assert.Equal(existing.LutR[^1], refined.LutR[^1]);

            // Build's identity blend starts at 0.9 × the reachable peak: at and above that
            // input position the refinement must leave every entry bit-for-bit untouched.
            double xFadeEnd = TransferFunctions.PqInverseEotf(existing.MeasuredPeakNits * 0.90);
            int untouched = 0;
            for (int i = 0; i < refined.LutR.Length; i++)
            {
                double x = i / (double)(refined.LutR.Length - 1);
                if (x < xFadeEnd) continue;
                Assert.Equal(existing.LutR[i], refined.LutR[i]);
                untouched++;
            }
            Assert.True(untouched > 50, "test did not cover the identity-blend region");
        }

        // ---- Refusals --------------------------------------------------------------------

        [Fact]
        public void Refine_Refuses_FewerThanFourRungs()
        {
            var existing = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite);
            var before = MeasureThroughChain(existing.LutR, DriftedPanel, new[] { 16.0, 64, 150 });

            var outcome = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits);

            Assert.Null(outcome.Refined);
            Assert.Contains("rung", outcome.RefusalReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Refine_Refuses_WildErrorAtAnyRung()
        {
            // +50% at one rung means the profile isn't active / night mode is on / the panel
            // moved massively — a multiplicative touch-up would just bake the fault in.
            var existing = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite);
            var before = MeasureThroughChain(existing.LutR, DriftedPanel, new[] { 16.0, 64, 150, 320 });
            before.Add(MakeWireMeasurement(100, 150.0)); // e = 1.5

            var outcome = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits);

            Assert.Null(outcome.Refined);
            Assert.Contains("re-run the calibration", outcome.RefusalReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Refine_Refuses_WhenAlreadyConverged()
        {
            // Sub-1% average error: a reinstall would churn the profile for nothing.
            var existing = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite);
            var before = RefineRungs.Select((n, i) => MakeWireMeasurement(n, n * 1.005, i)).ToList();

            var outcome = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits);

            Assert.Null(outcome.Refined);
            Assert.Contains("converged", outcome.RefusalReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(outcome.AverageAbsErrorBefore < 0.01);
        }

        [Fact]
        public void Refine_Ignores_RungsInsideTheIdentityBlend()
        {
            // Rungs at/above 90% of the reachable peak measure the panel's own rolloff, which
            // the LUT deliberately passes through - they must not count as refinable data.
            var existing = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite);
            var blendRungs = new[] { 0.92, 0.95, 0.99, 1.05 }
                .Select(f => f * existing.MeasuredPeakNits);
            var before = blendRungs.Select((n, i) => MakeWireMeasurement(n, n * 1.10, i)).ToList();

            var outcome = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits);

            Assert.Null(outcome.Refined);
            Assert.Contains("rung", outcome.RefusalReason, StringComparison.OrdinalIgnoreCase);
        }

        // ---- Clamping --------------------------------------------------------------------

        [Fact]
        public void Refine_ClampsCorrectionFactor_AtTheLowerBound()
        {
            // A uniform e = 0.66 passes the ±35% gate (|e−1| = 0.34) but sits below the 0.7
            // per-point clamp: the correction must divide by 0.7, not 0.66, bounding runaway.
            var ideal = HdrMhc2LutBuilder.Build(
                SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p), 0.02)), SdrWhite);
            var before = RefineRungs.Select((n, i) => MakeWireMeasurement(n, n * 0.66, i)).ToList();

            var refined = HdrMhc2LutBuilder.Refine(ideal, before, ideal.MeasuredPeakNits).Refined;
            Assert.NotNull(refined);

            // At a mid rung (100 nits, well inside the flat-0.66 region) the refined output
            // must decode to exactly oldNits / 0.7.
            int idx = (int)Math.Round(TransferFunctions.PqInverseEotf(100.0) * 1023);
            double oldNits = TransferFunctions.PqEotf(ideal.LutR[idx]);
            double newNits = TransferFunctions.PqEotf(refined!.LutR[idx]);
            double appliedDivisor = oldNits / newNits;
            Assert.True(Math.Abs(appliedDivisor - 0.7) < 0.005,
                $"expected the clamped divisor 0.7, got {appliedDivisor:F4}");
        }

        // ---- Matrix neutral-scale composition --------------------------------------------

        [Fact]
        public void Build_CarriesMatrixNeutralScale_IntoTheResult()
        {
            var r1 = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite);
            var r2 = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite, matrixNeutralScale: 0.8);
            Assert.Equal(1.0, r1.MatrixNeutralScale);
            Assert.Equal(0.8, r2.MatrixNeutralScale);
        }

        [Fact]
        public void Refine_OnScaleComposedLut_FixesResidual_WithoutDisturbingTheComposedScale()
        {
            // The M5 scenario plus drift: LUT built with the matrix's neutral scale s = 0.8
            // composed in, then the panel drifts. Refinement must correct ONLY the residual
            // drift, in the same post-matrix input domain, so the chain still tracks ABSOLUTE
            // nits (not s·nits) afterwards.
            const double s = 0.8;
            var existing = HdrMhc2LutBuilder.Build(SimulateWireLadder(CleanPanel), SdrWhite, matrixNeutralScale: s);
            Assert.Equal(s, existing.MatrixNeutralScale);

            // Sanity: pre-drift, the composed chain already tracks absolute nits. Targets on
            // the measured knots (gain × ladder) keep the piecewise-linear inversion exact,
            // as in the M5 composition test.
            foreach (double ladderNits in new[] { 32.0, 64, 150, 320 })
            {
                double n = Gain * ladderNits;
                double y = Chain(existing.LutR, CleanPanel, n, s);
                Assert.True(Math.Abs(y / n - 1.0) < 0.02,
                    $"pre-drift composed chain should track {n:F0} nits, got {y:F1}");
            }

            var before = MeasureThroughChain(existing.LutR, DriftedPanel, RefineRungs, s);
            var outcome = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits);
            Assert.Null(outcome.RefusalReason);
            var refined = outcome.Refined!;
            Assert.Equal(s, refined.MatrixNeutralScale);

            foreach (double n in RefineRungs)
            {
                double y = Chain(refined.LutR, DriftedPanel, n, s);
                Assert.True(Math.Abs(y / n - 1.0) < 0.015,
                    $"at {n:F0} nits the refined composed chain emitted {y:F2} ({y / n - 1.0:+0.0%;-0.0%}); expected <1.5% (absolute tracking)");
            }
        }

        // ---- Shared error metric -----------------------------------------------------------

        [Fact]
        public void AverageAbsLuminanceError_MatchesHandComputedMean()
        {
            var ms = new List<MeasurementResult>
            {
                MakeWireMeasurement(100, 108), // +8%
                MakeWireMeasurement(200, 194), // -3%
            };
            Assert.Equal((0.08 + 0.03) / 2, HdrMhc2LutBuilder.AverageAbsLuminanceError(ms), 6);
            Assert.True(double.IsNaN(HdrMhc2LutBuilder.AverageAbsLuminanceError(Array.Empty<MeasurementResult>())));
        }
    }
}
