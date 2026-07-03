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
                           $"final max normalized residual {orchestrator.AdaptiveFinalMaxNormalizedResidual:F2}");
            DumpTopResiduals(result);
            Assert.True(used <= 120, $"must respect budget, used {used}");
            Assert.True(orchestrator.AdaptiveFinalMaxNormalizedResidual <= 1.0,
                $"kinked panel should converge below target within budget, ended at " +
                $"{orchestrator.AdaptiveFinalMaxNormalizedResidual:F2}× target");
        }

        [Fact]
        public async Task KinkedPanel_AdaptiveBeatsEqualCountFixedGrid_OnHeldOutModelError()
        {
            // A panel with a narrow tone defect a coarse uniform grid under-samples.
            var panel = (Func<double, double>)AdaptivePatchPlannerTests.BumpTone;
            var fake = new PanelColorimeterService(panel);
            var orchestrator = Adaptive(fake, budget: 84, batch: 12);
            var result = await orchestrator.StartCalibrationAsync();
            Assert.True(result.Success, result.Message);

            var adaptiveAccuracy = result.Measurements!
                .Where(m => m.IsValid && m.Patch.Nits is null && m.Patch.Category != PatchCategory.DriftCheck)
                .ToList();
            int total = adaptiveAccuracy.Count;

            // Equal-total fixed grid, matched PER MANIFOLD to the adaptive run but placed
            // UNIFORMLY — the fixed-grid baseline the feature replaces, at equal patch count.
            // This isolates PLACEMENT: same budget on each manifold, uniform vs adaptive.
            var fixedMeasurements = BuildEqualCountUniformGrid(adaptiveAccuracy, panel);
            Assert.Equal(total, fixedMeasurements.Count);

            double adaptiveMax = MaxHeldOutResidual(adaptiveAccuracy);
            double fixedMax = MaxHeldOutResidual(fixedMeasurements);

            _out.WriteLine($"equal-total held-out worst-case model residual ({total} patches each):");
            _out.WriteLine($"  adaptive max normalized residual = {adaptiveMax:F2}");
            _out.WriteLine($"  uniform  max normalized residual = {fixedMax:F2}");
            _out.WriteLine($"  adaptive is {fixedMax / adaptiveMax:F2}x better (lower is better)");

            Assert.True(adaptiveMax < fixedMax,
                $"adaptive worst-case model residual ({adaptiveMax:F2}) should beat the equal-total fixed grid ({fixedMax:F2})");
        }

        /// <summary>Worst target-normalized held-out (leave-one-out) model residual of a measurement set.</summary>
        private static double MaxHeldOutResidual(IReadOnlyList<MeasurementResult> measurements)
        {
            var residuals = new Lut3DGenerator(StandardTargets.SrgbGamma22, measurements, 17).ComputeModelResiduals();
            return AdaptivePatchPlanner.MaxNormalizedResidual(residuals);
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
            var fake = new PanelColorimeterService(AdaptivePatchPlannerTests.KinkTone);
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
