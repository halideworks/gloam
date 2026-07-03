using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core.Calibration;
using Xunit;
using Xunit.Abstractions;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// End-to-end tests of the adaptive-mode orchestration (roadmap 1.1) against synthetic
    /// panels: round iteration, early stopping, convergence within budget, and the headline
    /// claim that adaptive placement beats an equal-total-patch fixed grid on held-out error.
    /// </summary>
    public class AdaptiveOrchestratorTests
    {
        private readonly ITestOutputHelper _out;
        public AdaptiveOrchestratorTests(ITestOutputHelper output) => _out = output;

        /// <summary>Fake meter that reports a synthetic panel's response for any patch.</summary>
        private sealed class PanelColorimeterService : ColorimeterService
        {
            private readonly Func<double, double> _tone;
            public PanelColorimeterService(Func<double, double> tone) : base(string.Empty) => _tone = tone;

            public override bool IsReady => true;
            public override Task BeginMeasurementSessionAsync(bool hdrMode, CancellationToken ct = default) => Task.CompletedTask;
            public override Task EndMeasurementSessionAsync() => Task.CompletedTask;

            public override Task<MeasurementResult> MeasureAsync(ColorPatch patch, bool hdrMode = false, CancellationToken ct = default)
                => Task.FromResult(new MeasurementResult
                {
                    Patch = patch,
                    Xyz = AdaptivePatchPlannerTests.PanelXyz(patch.DisplayRgb, _tone),
                    IsValid = true
                });
        }

        private static CalibrationOrchestrator Adaptive(PanelColorimeterService fake, int budget, int batch)
            => new CalibrationOrchestrator(
                fake, StandardTargets.SrgbGamma22, PatchSetGenerator.CalibrationPreset.Adaptive,
                settleTimeMs: 100, maxRetries: 3, hdrMode: false)
            {
                SettleBaseMs = 1,
                SettleScaleFullSwingMs = 0,
                LargeFallSettleFloorMs = 1,
                SettleMaxMs = 1,
                InterReadDelayMs = 1,
                AdaptivePatchBudget = budget,
                AdaptiveBatchSize = batch,
            };

        private void DumpTopResiduals(CalibrationResult r)
        {
            var residuals = new Lut3DGenerator(StandardTargets.SrgbGamma22, r.Measurements!, 17)
                .ComputeModelResiduals()
                .OrderByDescending(AdaptivePatchPlanner.NormalizedResidual)
                .Take(8);
            foreach (var res in residuals)
                _out.WriteLine($"  {res.Kind} {res.Location.Manifold} @ ({res.Location.R:F3},{res.Location.G:F3},{res.Location.B:F3}) " +
                               $"mag={res.Magnitude:F4} norm={AdaptivePatchPlanner.NormalizedResidual(res):F2}");
        }

        private static int AccuracyCount(CalibrationResult r) =>
            r.Measurements!.Count(m => m.IsValid && m.Patch.Nits is null && m.Patch.Category != PatchCategory.DriftCheck);

        [Fact]
        public async Task SmoothPanel_StopsEarly_FarUnderBudget()
        {
            var fake = new PanelColorimeterService(AdaptivePatchPlannerTests.SmoothTone);
            var orchestrator = Adaptive(fake, budget: 120, batch: 12);

            var result = await orchestrator.StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            int used = AccuracyCount(result);
            DumpTopResiduals(result);
            _out.WriteLine($"smooth panel: {orchestrator.AdaptiveRoundsCompleted} adaptive round(s), {used} accuracy patches (budget 120)");
            Assert.True(used < 120 / 2, $"smooth panel should finish well under budget, used {used}");
            Assert.True(orchestrator.AdaptiveRoundsCompleted <= 2,
                $"smooth panel should need very few rounds, took {orchestrator.AdaptiveRoundsCompleted}");
        }

        [Fact]
        public async Task KinkedPanel_ConvergesBelowTarget_WithinBudget()
        {
            var fake = new PanelColorimeterService(AdaptivePatchPlannerTests.ResolvableKinkTone);
            var orchestrator = Adaptive(fake, budget: 120, batch: 12);

            var result = await orchestrator.StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            int used = AccuracyCount(result);
            _out.WriteLine($"kinked panel: {orchestrator.AdaptiveRoundsCompleted} rounds, {used} patches, " +
                           $"final robust normalized residual {orchestrator.AdaptiveFinalRobustNormalizedResidual:F2}, " +
                           $"targetsMet={orchestrator.AdaptiveAccuracyTargetsMet}");
            DumpTopResiduals(result);
            Assert.True(used <= 120, $"must respect budget, used {used}");
            // Stopping is on the robust (90th-pct) summary; a resolvable defect should reach
            // the target and report clean (non-degraded) convergence.
            Assert.True(orchestrator.AdaptiveAccuracyTargetsMet, "resolvable kink should converge, not stop degraded");
            Assert.False(result.AdaptiveDegraded);
            Assert.True(orchestrator.AdaptiveFinalRobustNormalizedResidual <= 1.0,
                $"kinked panel should converge below target within budget, ended at " +
                $"{orchestrator.AdaptiveFinalRobustNormalizedResidual:F2}× target");
        }

        // ---------------------------- Honest held-out benchmark (FIX 2 / FIX 3) ----------------------------
        //
        // The OLD benchmark scored each method on ITS OWN leave-one-out residuals (the exact
        // objective the planner optimizes → circular), at different points per method, on a
        // single hand-picked panel. This replaces it with an INDEPENDENT comparison:
        //   * a COMMON dense held-out gray reference set (256 samples NEITHER method measured),
        //   * each method's forward tone model fitted from its OWN measurements (Lut3DGenerator),
        //   * both models PREDICT the held-out reference and are scored in perceptual ΔL*,
        //   * compared by RMS and 95th-percentile error at the SAME points,
        // across multiple panels. The advantage factor reported is the real, honest number.

        /// <summary>256 held-out gray reference signals, offset off the 8-bit lattice so
        /// NEITHER the adaptive picks nor the uniform grid actually measured them.</summary>
        private static IReadOnlyList<double> HeldOutGraySignals()
        {
            var s = new List<double>(256);
            for (int i = 0; i < 256; i++) s.Add((i + 0.5) / 256.0);
            return s;
        }

        /// <summary>CIE L* of a neutral at relative luminance yr (Y/Yn, white = 1).</summary>
        private static double Lstar(double yr)
        {
            yr = Math.Clamp(yr, 0.0, 1.0);
            return ColorMath.XyzToLab(new CieXyz(yr, yr, yr), new CieXyz(1.0, 1.0, 1.0)).L;
        }

        /// <summary>
        /// Fits the forward tone model from <paramref name="measurements"/> and scores its
        /// held-out gray-tone prediction error (perceptual ΔL*) against the analytic panel at
        /// the common reference signals. Returns RMS + 95th percentile over the whole range
        /// and RMS over the shadow region (signal ≤ 0.2), used to validate FIX 4.
        /// </summary>
        private static (double Rms, double P95, double ShadowRms) HeldOutToneError(
            IReadOnlyList<MeasurementResult> measurements, Func<double, double> panel)
        {
            var characterization = new Lut3DGenerator(StandardTargets.SrgbGamma22, measurements, 17)
                .BuildCharacterizationOnly();
            var tone = characterization.NeutralToneCurve!;

            var errs = new List<double>();
            var shadowErrs = new List<double>();
            foreach (double s in HeldOutGraySignals())
            {
                double trueNorm = panel(s);              // analytic panel's true normalized luminance
                double modelNorm = tone.Lookup(s);       // fitted model's prediction
                double e = Math.Abs(Lstar(modelNorm) - Lstar(trueNorm));
                errs.Add(e);
                if (s <= 0.2) shadowErrs.Add(e);
            }

            double rms = Math.Sqrt(errs.Average(e => e * e));
            var sorted = errs.OrderBy(x => x).ToList();
            double p95 = sorted[(int)Math.Ceiling(0.95 * (sorted.Count - 1))];
            double shadowRms = Math.Sqrt(shadowErrs.Average(e => e * e));
            return (rms, p95, shadowRms);
        }

        private async Task<IReadOnlyList<MeasurementResult>> RunAdaptiveAccuracy(
            Func<double, double> panel, int budget, int batch)
        {
            var fake = new PanelColorimeterService(panel);
            var orchestrator = Adaptive(fake, budget, batch);
            var result = await orchestrator.StartCalibrationAsync();
            Assert.True(result.Success, result.Message);
            return result.Measurements!
                .Where(m => m.IsValid && m.Patch.Nits is null && m.Patch.Category != PatchCategory.DriftCheck)
                .ToList();
        }

        [Theory]
        [InlineData("BumpTone")]
        public async Task HeldOut_AdaptiveBeatsEqualCountUniform_OnLocalizedDefectPanels(string panelName)
        {
            var panel = PanelByName(panelName);
            var adaptive = await RunAdaptiveAccuracy(panel, budget: 84, batch: 12);

            // Equal-total uniform grid, matched per-manifold count (isolates PLACEMENT).
            var uniform = BuildEqualCountUniformGrid(adaptive, panel);
            Assert.Equal(adaptive.Count, uniform.Count);

            var a = HeldOutToneError(adaptive, panel);
            var u = HeldOutToneError(uniform, panel);

            _out.WriteLine($"[{panelName}] common held-out gray ΔL* ({adaptive.Count} patches each):");
            _out.WriteLine($"  adaptive: RMS={a.Rms:F3}  P95={a.P95:F3}  shadowRMS={a.ShadowRms:F3}");
            _out.WriteLine($"  uniform : RMS={u.Rms:F3}  P95={u.P95:F3}  shadowRMS={u.ShadowRms:F3}");
            _out.WriteLine($"  advantage: RMS {u.Rms / a.Rms:F2}x  P95 {u.P95 / a.P95:F2}x (higher = adaptive better)");

            // Honest, quantified margin. On a LOCALIZED defect a uniform grid mispredicts the
            // whole feature, so the tail (P95) is where adaptive's concentration pays off most;
            // it also does not regress the RMS. (On broadband-curvature panels, where uniform's
            // even coverage is already near-optimal, the honest margin is smaller — covered by
            // the parity/no-regression tests below.)
            Assert.True(a.Rms <= u.Rms,
                $"[{panelName}] adaptive held-out RMS ΔL* ({a.Rms:F3}) should not exceed uniform ({u.Rms:F3})");
            Assert.True(a.P95 <= u.P95 * 0.6,
                $"[{panelName}] adaptive held-out P95 ΔL* ({a.P95:F3}) should beat uniform ({u.P95:F3}) with a real margin");
        }

        [Fact]
        public async Task HeldOut_SmoothPanel_AdaptiveParityWithUniform()
        {
            // FIX 3: with no defect to chase, adaptive must not HURT relative to uniform at
            // equal patch count — it stays within a small band of the uniform baseline (and,
            // because it converges from the near-uniform seed, typically reproduces it).
            var panel = (Func<double, double>)AdaptivePatchPlannerTests.SmoothTone;
            var adaptive = await RunAdaptiveAccuracy(panel, budget: 84, batch: 12);
            var uniform = BuildEqualCountUniformGrid(adaptive, panel);

            var a = HeldOutToneError(adaptive, panel);
            var u = HeldOutToneError(uniform, panel);

            double ratio = a.Rms / u.Rms;
            _out.WriteLine($"[SmoothTone] held-out gray ΔL* RMS: adaptive={a.Rms:F4} uniform={u.Rms:F4} ratio={ratio:F2}");
            Assert.InRange(ratio, 0.8, 1.25);
            Assert.True(a.Rms < 0.5, $"adaptive held-out error on a smooth panel should be small ({a.Rms:F3} ΔL*)");
        }

        [Fact]
        public async Task HeldOut_BroadbandPanel_AdaptiveDoesNotPerceptiblyRegress()
        {
            // Honest finding: on a broadband-curvature panel (curvature everywhere plus a
            // kink), uniform's even coverage is already near-optimal, so adaptive's advantage
            // is small-to-none at equal count. Assert it does not PERCEPTIBLY regress: adaptive
            // stays within a modest factor of uniform and in excellent absolute terms.
            var panel = (Func<double, double>)AdaptivePatchPlannerTests.GammaKinkTone;
            var adaptive = await RunAdaptiveAccuracy(panel, budget: 84, batch: 12);
            var uniform = BuildEqualCountUniformGrid(adaptive, panel);

            var a = HeldOutToneError(adaptive, panel);
            var u = HeldOutToneError(uniform, panel);
            _out.WriteLine($"[GammaKinkTone] held-out gray ΔL* RMS: adaptive={a.Rms:F3} uniform={u.Rms:F3} " +
                           $"(P95 adaptive={a.P95:F3} uniform={u.P95:F3})");
            Assert.True(a.Rms <= u.Rms * 1.5, $"adaptive RMS ({a.Rms:F3}) should stay within 1.5x uniform ({u.Rms:F3})");
            Assert.True(a.Rms < 0.6, $"adaptive absolute held-out error stays small ({a.Rms:F3} ΔL*)");
        }

        [Fact]
        public async Task HeldOut_ShadowLocalizedDefect_PerceptualTargetResolvesShadow()
        {
            // FIX 4 validation: a NARROW defect in the shadows has small ΔY but is perceptually
            // visible. The perceptual (|ΔL*|) tone target elevates its priority so adaptive
            // crowds the shadow and predicts it better than the equal-count uniform grid — the
            // shadow held-out error is where the win shows up.
            var panel = (Func<double, double>)AdaptivePatchPlannerTests.ShadowBumpTone;
            var adaptive = await RunAdaptiveAccuracy(panel, budget: 84, batch: 12);
            var uniform = BuildEqualCountUniformGrid(adaptive, panel);

            var a = HeldOutToneError(adaptive, panel);
            var u = HeldOutToneError(uniform, panel);
            _out.WriteLine($"[ShadowBumpTone] shadow(≤0.2) held-out ΔL* RMS: adaptive={a.ShadowRms:F3} uniform={u.ShadowRms:F3}; " +
                           $"overall RMS adaptive={a.Rms:F3} uniform={u.Rms:F3}");
            Assert.True(a.ShadowRms <= u.ShadowRms,
                $"perceptual target should resolve the shadow defect better than uniform (adaptive {a.ShadowRms:F3} vs uniform {u.ShadowRms:F3})");
        }

        private static Func<double, double> PanelByName(string name) => name switch
        {
            "BumpTone" => AdaptivePatchPlannerTests.BumpTone,
            "GammaKinkTone" => AdaptivePatchPlannerTests.GammaKinkTone,
            "SmoothTone" => AdaptivePatchPlannerTests.SmoothTone,
            _ => AdaptivePatchPlannerTests.KinkTone
        };

        [Fact]
        public async Task HardPanel_PlateausAboveTarget_ReportsDegraded_NotCleanSuccess()
        {
            // FIX 1: a genuinely hard panel (sharp BumpTone) on a budget barely above the seed
            // cannot resolve the defect and reach the accuracy target. The run still completes
            // (a profile is produced) but must be flagged DEGRADED rather than reported as
            // unqualified success.
            var fake = new PanelColorimeterService(AdaptivePatchPlannerTests.BumpTone);
            var orchestrator = Adaptive(fake, budget: 30, batch: 12);

            var result = await orchestrator.StartCalibrationAsync();

            _out.WriteLine($"hard panel: rounds={orchestrator.AdaptiveRoundsCompleted}, " +
                           $"worst={orchestrator.AdaptiveFinalMaxNormalizedResidual:F2}, " +
                           $"targetsMet={orchestrator.AdaptiveAccuracyTargetsMet}, degraded={result.AdaptiveDegraded}");
            Assert.True(result.Success, result.Message);   // still a valid measurement pass
            Assert.True(result.AdaptiveDegraded, "hard panel that stops above target must be flagged degraded");
            Assert.False(orchestrator.AdaptiveAccuracyTargetsMet);
            Assert.NotNull(result.AdaptiveDegradedMessage);
            Assert.Contains("WARNING", result.Message);
        }

        [Fact]
        public async Task Adaptive_DriftCadence_ContinuesAcrossSeedToRoundsBoundary()
        {
            // FIX 6: the adaptive rounds must CONTINUE the seed's drift-anchor cadence, not
            // restart it at zero (which produced a redundant back-to-back white and a phase
            // reset at the boundary). Symptom check: no two drift-check WHITE patches are ever
            // adjacent, and white re-reads keep their ~25-ordinary-patch spacing throughout.
            var fake = new PanelColorimeterService(AdaptivePatchPlannerTests.BumpTone);
            var orchestrator = Adaptive(fake, budget: 120, batch: 12);

            var result = await orchestrator.StartCalibrationAsync();
            Assert.True(result.Success, result.Message);

            var seq = result.Measurements!.ToList();
            bool IsWhiteAnchor(MeasurementResult m) =>
                m.Patch.Category == PatchCategory.DriftCheck &&
                m.Patch.DisplayRgb.R >= 0.99 && m.Patch.DisplayRgb.G >= 0.99 && m.Patch.DisplayRgb.B >= 0.99;

            int ordinarySinceWhite = 0;
            int minGap = int.MaxValue, whiteAnchors = 0;
            bool sawFirst = false;
            for (int i = 0; i < seq.Count; i++)
            {
                if (IsWhiteAnchor(seq[i]))
                {
                    whiteAnchors++;
                    Assert.False(i > 0 && IsWhiteAnchor(seq[i - 1]),
                        $"two adjacent drift-check whites at index {i} (double-white / phase reset)");
                    if (sawFirst) minGap = Math.Min(minGap, ordinarySinceWhite);
                    sawFirst = true;
                    ordinarySinceWhite = 0;
                }
                else if (seq[i].Patch.Category != PatchCategory.DriftCheck && seq[i].Patch.Nits is null)
                {
                    ordinarySinceWhite++;
                }
            }

            _out.WriteLine($"white drift anchors={whiteAnchors}, min ordinary-patch gap between whites={minGap}");
            Assert.True(whiteAnchors >= 3, "the run should be long enough to exercise several drift whites");
            // Cadence preserved: consecutive whites stay reasonably spaced (never bunched at
            // the boundary). The interval is 25; allow slack for the seed's closing anchor.
            Assert.True(minGap >= 10, $"drift whites bunched (min gap {minGap}) — cadence restarted at the boundary");
        }

        /// <summary>
        /// Builds a uniform fixed grid measuring the same panel, with the SAME number of
        /// patches on each signal manifold as <paramref name="adaptive"/> used — but placed
        /// uniformly instead of adaptively. Equal total, equal per-manifold budget.
        /// </summary>
        private static List<MeasurementResult> BuildEqualCountUniformGrid(
            IReadOnlyList<MeasurementResult> adaptive, Func<double, double> panel)
        {
            var byManifold = adaptive
                .GroupBy(m => AdaptivePatchPlanner.ClassifySignal(m.Patch.DisplayRgb).Manifold)
                .ToDictionary(g => g.Key, g => g.Count());

            var result = new List<MeasurementResult>();
            void AddUniform(SignalManifold manifold, int count, double lo, double hi,
                Func<double, LinearRgb> toRgb, PatchCategory cat)
            {
                if (count <= 0) return;
                for (int i = 0; i < count; i++)
                {
                    double s = count == 1 ? hi : PatchSetGenerator.Snap8Bit(lo + (hi - lo) * i / (count - 1));
                    var rgb = toRgb(s);
                    result.Add(new MeasurementResult
                    {
                        Patch = new ColorPatch { Name = $"fx {manifold} {s:F3}", DisplayRgb = rgb, Category = cat },
                        Xyz = AdaptivePatchPlannerTests.PanelXyz(rgb, panel),
                        IsValid = true
                    });
                }
            }

            int Count(SignalManifold m) => byManifold.TryGetValue(m, out var c) ? c : 0;
            AddUniform(SignalManifold.Gray, Count(SignalManifold.Gray), 0, 1, s => new LinearRgb(s, s, s), PatchCategory.Grayscale);
            AddUniform(SignalManifold.RedRamp, Count(SignalManifold.RedRamp), 0.25, 1, s => new LinearRgb(s, 0, 0), PatchCategory.Saturated);
            AddUniform(SignalManifold.GreenRamp, Count(SignalManifold.GreenRamp), 0.25, 1, s => new LinearRgb(0, s, 0), PatchCategory.Saturated);
            AddUniform(SignalManifold.BlueRamp, Count(SignalManifold.BlueRamp), 0.25, 1, s => new LinearRgb(0, 0, s), PatchCategory.Saturated);

            // Cube patches (near-neutral / reduced-sat) — reuse the adaptive picks' own
            // locations so the manifold count matches exactly; their placement is not the
            // point of this comparison (the defect is on the 1D manifolds).
            foreach (var m in adaptive)
                if (AdaptivePatchPlanner.ClassifySignal(m.Patch.DisplayRgb).Manifold == SignalManifold.Cube)
                    result.Add(new MeasurementResult
                    {
                        Patch = m.Patch,
                        Xyz = AdaptivePatchPlannerTests.PanelXyz(m.Patch.DisplayRgb, panel),
                        IsValid = true
                    });

            return result;
        }

        [Fact]
        public async Task Adaptive_ReportsPerRoundProgress()
        {
            var fake = new PanelColorimeterService(AdaptivePatchPlannerTests.KinkTone);
            var orchestrator = Adaptive(fake, budget: 48, batch: 12);
            var phases = new List<string>();
            orchestrator.PhaseChanged += (_, p) => phases.Add(p);

            var result = await orchestrator.StartCalibrationAsync();
            Assert.True(result.Success, result.Message);

            Assert.Contains(phases, p => p.StartsWith("Round 1:") && p.Contains("predicted max error"));
        }

        [Fact]
        public async Task Adaptive_CancellationBetweenRounds_IsClean()
        {
            // BumpTone needs several rounds to resolve, so Round 2 reliably fires.
            var fake = new PanelColorimeterService(AdaptivePatchPlannerTests.BumpTone);
            var orchestrator = Adaptive(fake, budget: 120, batch: 12);

            using var cts = new CancellationTokenSource();
            orchestrator.PhaseChanged += (_, p) =>
            {
                if (p.StartsWith("Round 2:")) cts.Cancel();
            };

            var result = await orchestrator.StartCalibrationAsync(cts.Token);
            Assert.True(result.WasCancelled);
            Assert.Equal(CalibrationState.Cancelled, orchestrator.State);
        }
    }
}
