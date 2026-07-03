using System;
using System.Collections.Generic;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;
using Xunit.Abstractions;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Pure-logic tests for adaptive patch placement (roadmap 1.1): the planner's scoring,
    /// spread and stopping, plus the leave-one-out / interpolation residuals the planner
    /// consumes. No hardware, no orchestrator, no RNG.
    /// </summary>
    public class AdaptivePatchPlannerTests
    {
        private readonly ITestOutputHelper _out;
        public AdaptivePatchPlannerTests(ITestOutputHelper output) => _out = output;

        // ---- Synthetic panel helpers (shared with the orchestrator tests' intent) ----

        internal const double WhiteY = 100.0;
        internal const double BlackY = 0.1;

        /// <summary>Smooth gamma-2.2 tone response (normalized linear light per channel).</summary>
        internal static double SmoothTone(double s) => Math.Pow(Math.Clamp(s, 0, 1), 2.2);

        /// <summary>
        /// Sharply kinked tone response: piecewise-linear with a slope discontinuity at
        /// 30% signal (steep shadows, shallow highlights). A worst case for a uniform grid
        /// — the C1 discontinuity can't be resolved by evenly-spaced samples — but the
        /// adaptive planner crowds the kink and beats it. Localization target for the
        /// residual tests. f(0)=0, f(1)=1, monotone.
        /// </summary>
        internal static double KinkTone(double s)
        {
            s = Math.Clamp(s, 0, 1);
            const double a = 0.30, fa = 0.55;
            return s <= a ? fa * (s / a) : fa + (1 - fa) * ((s - a) / (1 - a));
        }

        /// <summary>
        /// A RESOLVABLE tone defect: the same steep→shallow transition centered at 30%,
        /// but rounded over a finite window so dense local sampling can drive its fit error
        /// below the tone target within budget (unlike the C1-discontinuous
        /// <see cref="KinkTone"/>). Monotone, normalized f(0)=0, f(1)=1.
        /// </summary>
        internal static double ResolvableKinkTone(double s)
        {
            s = Math.Clamp(s, 0, 1);
            const double a = 0.30, w = 0.06, slopeLow = 1.5, slopeHigh = 0.75;
            double F(double x) => slopeHigh * x +
                (slopeLow - slopeHigh) * (0.5 * x - 0.5 * w * Math.Log(Math.Cosh((x - a) / w)));
            double f0 = F(0.0), f1 = F(1.0);
            return (F(s) - f0) / (f1 - f0);
        }

        /// <summary>
        /// A realistic hard panel: a two-segment gamma (2.6 shadows, 1.5 highlights) with a
        /// slope discontinuity at 30% signal. Curvature everywhere (so no region is "free"
        /// to a uniform grid) plus a localized kink — the setting where adaptive placement,
        /// concentrating where the model is worst, beats an equal-count uniform grid.
        /// f(0)=0, f(1)=1, monotone.
        /// </summary>
        internal static double GammaKinkTone(double s)
        {
            s = Math.Clamp(s, 0, 1);
            const double a = 0.30, g1 = 2.6, g2 = 1.5;
            double raw = s <= a
                ? Math.Pow(s, g1)
                : Math.Pow(a, g1) + (Math.Pow(s, g2) - Math.Pow(a, g2));
            double rawAt1 = Math.Pow(a, g1) + (1.0 - Math.Pow(a, g2));
            return raw / rawAt1;
        }

        /// <summary>
        /// A panel with a NARROW localized tone defect: a rapid ~0.35-height rise over a
        /// ~4% window at 50% signal (a contour/banding step), on an otherwise gentle slope.
        /// The feature is narrower than a coarse uniform grid's spacing, so the fixed grid
        /// under-samples and mispredicts it, while adaptive detects the residual spike and
        /// concentrates there — the canonical "spend samples where the display misbehaves"
        /// win. Monotone (slope stays positive), normalized f(0)=0, f(1)=1.
        /// </summary>
        internal static double BumpTone(double s)
        {
            s = Math.Clamp(s, 0, 1);
            const double c = 1.0, h = 0.35, w = 0.02, center = 0.5;
            double F(double x) => c * x + h * 0.5 * (1 + Math.Tanh((x - center) / w));
            double f0 = F(0.0), f1 = F(1.0);
            return (F(s) - f0) / (f1 - f0);
        }

        /// <summary>Panel forward model: signal RGB → measured absolute XYZ through sRGB primaries.</summary>
        internal static CieXyz PanelXyz(LinearRgb signal, Func<double, double> tone)
        {
            double lr = tone(signal.R), lg = tone(signal.G), lb = tone(signal.B);
            var m = ColorMath.SrgbToXyzMatrix;
            double x = m[0, 0] * lr + m[0, 1] * lg + m[0, 2] * lb;
            double y = m[1, 0] * lr + m[1, 1] * lg + m[1, 2] * lb;
            double z = m[2, 0] * lr + m[2, 1] * lg + m[2, 2] * lb;
            var bx = ColorMath.D65White;
            return new CieXyz(x * WhiteY + bx.X * BlackY, y * WhiteY + bx.Y * BlackY, z * WhiteY + bx.Z * BlackY);
        }

        private static MeasurementResult Meas(double r, double g, double b, Func<double, double> tone, PatchCategory cat)
        {
            var rgb = new LinearRgb(r, g, b);
            return new MeasurementResult
            {
                Patch = new ColorPatch { Name = $"{r:F2},{g:F2},{b:F2}", DisplayRgb = rgb, Category = cat },
                Xyz = PanelXyz(rgb, tone),
                IsValid = true
            };
        }

        /// <summary>A synthetic measurement set: dense gray ramp + primaries, driven by one tone curve.</summary>
        internal static List<MeasurementResult> SyntheticMeasurements(Func<double, double> tone, int grayPoints)
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i < grayPoints; i++)
            {
                double s = i / (double)(grayPoints - 1);
                list.Add(Meas(s, s, s, tone, PatchCategory.Grayscale));
            }
            list.Add(Meas(1, 0, 0, tone, PatchCategory.Primary));
            list.Add(Meas(0, 1, 0, tone, PatchCategory.Primary));
            list.Add(Meas(0, 0, 1, tone, PatchCategory.Primary));
            return list;
        }

        // ---------------------------- Determinism ----------------------------

        [Fact]
        public void PlanNextBatch_IsDeterministic()
        {
            var pool = PatchSetGenerator.BuildAdaptiveCandidatePool();
            var measured = UniformGraySeed(9);
            var residuals = new[] { GrayTone(0.30, 0.08), GrayTone(0.31, 0.07) };

            var a = AdaptivePatchPlanner.PlanNextBatch(pool, measured, residuals, 12);
            var b = AdaptivePatchPlanner.PlanNextBatch(pool, measured, residuals, 12);

            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
                Assert.Equal(a[i], b[i]);
        }

        // ---------------------------- Max-min spread ----------------------------

        [Fact]
        public void PlanNextBatch_NoTwoPicksCloserThanManifoldSeparation()
        {
            var pool = PatchSetGenerator.BuildAdaptiveCandidatePool();
            var measured = UniformGraySeed(5);
            // A single huge residual would otherwise pull every pick onto one spot.
            var residuals = new[] { GrayTone(0.50, 0.5) };

            var batch = AdaptivePatchPlanner.PlanNextBatch(pool, measured, residuals, 12);

            for (int i = 0; i < batch.Count; i++)
                for (int j = i + 1; j < batch.Count; j++)
                {
                    if (batch[i].Manifold != batch[j].Manifold) continue;
                    double d = AdaptivePatchPlanner.Distance(batch[i], batch[j]);
                    Assert.True(d >= AdaptivePatchPlanner.MinPickSeparation(batch[i].Manifold) - 1e-9,
                        $"picks {batch[i]} and {batch[j]} are {d} apart on {batch[i].Manifold}");
                }
        }

        // ---------------------------- Concentration vs spread ----------------------------

        [Fact]
        public void KinkResidual_ConcentratesPicksNearKink_SmoothResidualSpreads()
        {
            var pool = PatchSetGenerator.BuildAdaptiveCandidatePool();
            var measured = UniformGraySeed(9);

            // Kinked model: one large gray residual at 30% dwarfs the rest.
            var kink = new List<ModelResidual> { GrayTone(0.30, 0.12) };
            for (double s = 0.1; s < 1.0; s += 0.1)
                if (Math.Abs(s - 0.30) > 0.05) kink.Add(GrayTone(s, 0.002));

            // Smooth model: uniformly small residuals everywhere.
            var smooth = new List<ModelResidual>();
            for (double s = 0.1; s < 1.0; s += 0.1) smooth.Add(GrayTone(s, 0.004));

            var kinkBatch = AdaptivePatchPlanner.PlanNextBatch(pool, measured, kink, 12);
            var smoothBatch = AdaptivePatchPlanner.PlanNextBatch(pool, measured, smooth, 12);

            int NearKink(IReadOnlyList<SignalPoint> b) => b.Count(p =>
                p.Manifold == SignalManifold.Gray && Math.Abs(p.AxisLevel - 0.30) <= 0.05);

            int kinkNear = NearKink(kinkBatch), smoothNear = NearKink(smoothBatch);
            _out.WriteLine($"gray picks within 5% of the kink: kinked={kinkNear}, smooth={smoothNear}");
            Assert.True(kinkNear > smoothNear,
                $"kink model should crowd the kink more than the smooth model ({kinkNear} vs {smoothNear})");
        }

        // ---------------------------- Stopping guards ----------------------------

        [Fact]
        public void EvaluateStopping_StopsWhenTargetsMet()
        {
            var d = AdaptivePatchPlanner.EvaluateStopping(0.9, previousMaxNormalizedResidual: 5.0,
                measuredPatchCount: 30, patchBudget: 120);
            Assert.True(d.ShouldStop);
            Assert.Contains("target", d.Reason);
        }

        [Fact]
        public void EvaluateStopping_StopsAtBudget()
        {
            var d = AdaptivePatchPlanner.EvaluateStopping(3.0, previousMaxNormalizedResidual: 6.0,
                measuredPatchCount: 120, patchBudget: 120);
            Assert.True(d.ShouldStop);
            Assert.Contains("budget", d.Reason);
        }

        [Fact]
        public void EvaluateStopping_StopsOnPlateau()
        {
            // 5% improvement < 10% plateau floor → stop even though above target and under budget.
            var d = AdaptivePatchPlanner.EvaluateStopping(4.75, previousMaxNormalizedResidual: 5.0,
                measuredPatchCount: 50, patchBudget: 120);
            Assert.True(d.ShouldStop);
            Assert.Contains("plateau", d.Reason);
        }

        [Fact]
        public void EvaluateStopping_ContinuesWhenImprovingAboveTargetUnderBudget()
        {
            var d = AdaptivePatchPlanner.EvaluateStopping(3.0, previousMaxNormalizedResidual: 5.0,
                measuredPatchCount: 50, patchBudget: 120);
            Assert.False(d.ShouldStop);
        }

        // ---------------------------- Leave-one-out localization ----------------------------

        [Fact]
        public void ComputeModelResiduals_LeaveOneOut_LocalizesTheKink()
        {
            var kinked = SyntheticMeasurements(KinkTone, 21);
            var residuals = new Lut3DGenerator(StandardTargets.SrgbGamma22, kinked, 17).ComputeModelResiduals();

            var grayTone = residuals
                .Where(r => r.Location.Manifold == SignalManifold.Gray && r.Kind == ResidualKind.Tone)
                .ToList();
            Assert.NotEmpty(grayTone);

            var worst = grayTone.OrderByDescending(r => r.Magnitude).First();
            _out.WriteLine($"worst gray tone residual at signal {worst.Location.AxisLevel:F3}, |ΔY|/Y={worst.Magnitude:P1}");
            Assert.True(Math.Abs(worst.Location.AxisLevel - 0.30) <= 0.08,
                $"kink should localize near 30%, got {worst.Location.AxisLevel:F3}");
        }

        [Fact]
        public void ComputeModelResiduals_SmoothPanel_HasFarSmallerToneResidualThanKink()
        {
            var smooth = SyntheticMeasurements(SmoothTone, 21);
            var kinked = SyntheticMeasurements(KinkTone, 21);

            double smoothMax = MaxGrayTone(smooth);
            double kinkMax = MaxGrayTone(kinked);
            _out.WriteLine($"max gray tone residual: smooth={smoothMax:P2}, kinked={kinkMax:P2}");
            Assert.True(kinkMax > smoothMax * 3,
                $"kink tone residual ({kinkMax:P2}) should dwarf the smooth panel's ({smoothMax:P2})");
        }

        private static double MaxGrayTone(List<MeasurementResult> measurements)
        {
            var residuals = new Lut3DGenerator(StandardTargets.SrgbGamma22, measurements, 17).ComputeModelResiduals();
            return residuals
                .Where(r => r.Location.Manifold == SignalManifold.Gray && r.Kind == ResidualKind.Tone)
                .Select(r => r.Magnitude)
                .DefaultIfEmpty(0)
                .Max();
        }

        // ---------------------------- Helpers ----------------------------

        private static IReadOnlyList<SignalPoint> UniformGraySeed(int n)
        {
            var list = new List<SignalPoint>();
            for (int i = 0; i < n; i++)
            {
                double s = i / (double)(n - 1);
                list.Add(new SignalPoint(s, s, s, SignalManifold.Gray));
            }
            return list;
        }

        private static ModelResidual GrayTone(double s, double magnitude) =>
            new(new SignalPoint(s, s, s, SignalManifold.Gray), ResidualKind.Tone, magnitude);
    }
}
