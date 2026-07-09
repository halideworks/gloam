using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// The iterated HDR closed loop (HdrRefinementLoop): keep-best/damped iteration of
    /// HdrMhc2LutBuilder.Refine through injected measure/install delegates. Control-flow
    /// tests use scripted ladders (deterministic uniform errors); the convergence test drives
    /// the same simulated DWM chain as HdrMhc2LutRefineTests.
    /// </summary>
    public class HdrRefinementLoopTests
    {
        private const double SdrWhite = 200.0;
        private static readonly double[] BuildLadder = { 0, 2, 4, 8, 16, 32, 64, 100, 150, 220, 320, 450, 650, 1000 };
        private static readonly double[] RefineRungs = { 2, 4, 8, 16, 32, 64, 100, 150, 220, 320, 450, 650 };
        private static readonly double[] ScriptRungs = { 16, 64, 150, 320 };

        private const double Gain = 0.867;

        private static double CleanPanel(double p) => Math.Max(TransferFunctions.PqEotf(p) * Gain, 0.02);

        private static double Drift(double nits)
        {
            double t = Math.Clamp(
                (Math.Log10(Math.Max(nits, 0.01)) - 1.0) / (Math.Log10(300) - 1.0), 0, 1);
            double sm = t * t * (3 - 2 * t);
            return 1.08 + (0.97 - 1.08) * sm;
        }

        private static double DriftedPanel(double p)
        {
            double baseNits = CleanPanel(p);
            return baseNits * Drift(baseNits);
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

        private static List<MeasurementResult> SimulateWireLadder(Func<double, double> panelNitsOfPq)
            => BuildLadder.Select((n, i) =>
                MakeWireMeasurement(n, panelNitsOfPq(TransferFunctions.PqInverseEotf(n)), i)).ToList();

        private static double SampleLut(double[] lut, double p)
        {
            double x = Math.Clamp(p, 0.0, 1.0) * (lut.Length - 1);
            int i = (int)Math.Floor(x);
            if (i >= lut.Length - 1) return lut[^1];
            double t = x - i;
            return lut[i] + (lut[i + 1] - lut[i]) * t;
        }

        private static double Chain(double[] lut, Func<double, double> panel, double contentNits)
            => panel(SampleLut(lut, TransferFunctions.PqInverseEotf(contentNits)));

        /// <summary>
        /// A display whose installed LUT the loop's delegates mutate: ladders are measured
        /// through the currently installed LUT on the given panel.
        /// </summary>
        private sealed class SimulatedDisplay
        {
            public double[] InstalledLut;
            public Func<double, double> Panel;
            public int InstallCount;
            public readonly List<HdrMhc2LutBuilder.Result> Installs = new();

            public SimulatedDisplay(HdrMhc2LutBuilder.Result initial, Func<double, double> panel)
            {
                InstalledLut = initial.LutR;
                Panel = panel;
            }

            public Task<IReadOnlyList<MeasurementResult>> MeasureAsync(
                IReadOnlyList<double> rungs, int sequenceOffset, CancellationToken ct)
            {
                IReadOnlyList<MeasurementResult> result = rungs
                    .Select((n, i) => MakeWireMeasurement(n, Chain(InstalledLut, Panel, n), sequenceOffset + i))
                    .ToList();
                return Task.FromResult(result);
            }

            public Task<(HdrMhc2LutBuilder.Result, string)> InstallAsync(
                HdrMhc2LutBuilder.Result candidate, CancellationToken ct)
            {
                InstalledLut = candidate.LutR;
                Installs.Add(candidate);
                InstallCount++;
                return Task.FromResult((candidate, $"profile-{InstallCount}"));
            }
        }

        /// <summary>Scripted ladders for deterministic control-flow tests.</summary>
        private sealed class ScriptedLadders
        {
            private readonly Queue<double[]> _uniformErrors;
            public readonly List<HdrMhc2LutBuilder.Result> Installs = new();

            public ScriptedLadders(params double[][] uniformErrorsPerCall)
                => _uniformErrors = new Queue<double[]>(uniformErrorsPerCall);

            public Task<IReadOnlyList<MeasurementResult>> MeasureAsync(
                IReadOnlyList<double> rungs, int sequenceOffset, CancellationToken ct)
            {
                Assert.True(_uniformErrors.Count > 0, "test script exhausted: unexpected extra ladder measurement");
                double[] errors = _uniformErrors.Dequeue();
                IReadOnlyList<MeasurementResult> result = rungs
                    .Select((n, i) => MakeWireMeasurement(
                        n, n * errors[Math.Min(i, errors.Length - 1)], sequenceOffset + i))
                    .ToList();
                return Task.FromResult(result);
            }

            public Task<(HdrMhc2LutBuilder.Result, string)> InstallAsync(
                HdrMhc2LutBuilder.Result candidate, CancellationToken ct)
            {
                Installs.Add(candidate);
                return Task.FromResult((candidate, $"profile-{Installs.Count}"));
            }
        }

        private static double[] Uniform(double e) => new[] { e, e, e, e };

        private static HdrMhc2LutBuilder.Result BuildInitial(Func<double, double>? panel = null)
            => HdrMhc2LutBuilder.Build(SimulateWireLadder(panel ?? CleanPanel), SdrWhite);

        // ---- Damping math ------------------------------------------------------------------

        [Fact]
        public void Refine_DampingOne_IsBitIdenticalToTheDefault()
        {
            var existing = BuildInitial();
            var before = RefineRungs.Select((n, i) =>
                MakeWireMeasurement(n, Chain(existing.LutR, DriftedPanel, n), i)).ToList();

            var byDefault = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits);
            var explicitOne = HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits, damping: 1.0);

            Assert.NotNull(byDefault.Refined);
            Assert.Equal(byDefault.Refined!.LutR, explicitOne.Refined!.LutR);
        }

        [Fact]
        public void Refine_DampingHalf_HalvesTheFactorDeviation()
        {
            // Uniform e = 1.2 with damping 0.5 must apply the divisor 1.1, not 1.2.
            var ideal = HdrMhc2LutBuilder.Build(
                SimulateWireLadder(p => Math.Max(TransferFunctions.PqEotf(p), 0.02)), SdrWhite);
            var before = RefineRungs.Select((n, i) => MakeWireMeasurement(n, n * 1.2, i)).ToList();

            var refined = HdrMhc2LutBuilder.Refine(ideal, before, ideal.MeasuredPeakNits, damping: 0.5).Refined;
            Assert.NotNull(refined);

            int idx = (int)Math.Round(TransferFunctions.PqInverseEotf(100.0) * 1023);
            double oldNits = TransferFunctions.PqEotf(ideal.LutR[idx]);
            double newNits = TransferFunctions.PqEotf(refined!.LutR[idx]);
            double appliedDivisor = oldNits / newNits;
            Assert.True(Math.Abs(appliedDivisor - 1.1) < 0.005,
                $"expected the damped divisor 1.1, got {appliedDivisor:F4}");
        }

        [Fact]
        public void Refine_RejectsOutOfRangeDamping()
        {
            var existing = BuildInitial();
            var before = RefineRungs.Select((n, i) => MakeWireMeasurement(n, n * 1.05, i)).ToList();

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits, damping: 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HdrMhc2LutBuilder.Refine(existing, before, existing.MeasuredPeakNits, damping: 1.5));
        }

        // ---- Convergence on the simulated chain ---------------------------------------------

        [Fact]
        public async Task Loop_DriftedPanel_ConvergesWithinThreePasses()
        {
            var initial = BuildInitial();
            var display = new SimulatedDisplay(initial, DriftedPanel);

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = RefineRungs,
                MeasureLadderAsync = display.MeasureAsync,
                InstallAsync = display.InstallAsync,
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall);
            Assert.True(outcome.Converged, $"loop did not converge: {outcome.StopReason}");
            Assert.True(outcome.FinalAvgAbsError < 0.01,
                $"final avg error {outcome.FinalAvgAbsError:P2} should be under 1%");
            Assert.True(outcome.Passes.Count <= 3);
            Assert.True(outcome.FinalAvgAbsError < outcome.InitialAvgAbsError);

            // The display must end on the best (= final converged) LUT.
            Assert.Same(outcome.FinalLuts.LutR, display.InstalledLut);
        }

        [Fact]
        public async Task Loop_AlreadyConvergedPanel_NoInstall_ReportsSuccess()
        {
            var initial = BuildInitial();
            var script = new ScriptedLadders(Uniform(1.005)); // 0.5% — under the 1% gate

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = ScriptRungs,
                MeasureLadderAsync = script.MeasureAsync,
                InstallAsync = script.InstallAsync,
            }, CancellationToken.None);

            Assert.False(outcome.AnyInstall);
            Assert.True(outcome.Converged);
            Assert.Empty(script.Installs);
            Assert.Same(initial, outcome.FinalLuts);
            Assert.Contains("converged", outcome.StopReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Loop_ConvergedRefusalOnPassTwo_TreatedAsSuccess()
        {
            // A loop configured with a stricter target than Refine's 1% floor: pass 1 lands at
            // 0.5%, the loop asks for another pass, Refine refuses "already converged" — the
            // loop must report success, not a failed refusal.
            var initial = BuildInitial();
            var script = new ScriptedLadders(Uniform(1.05), Uniform(1.005));

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = ScriptRungs,
                MeasureLadderAsync = script.MeasureAsync,
                InstallAsync = script.InstallAsync,
                ConvergedAvgError = 0.002,
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall);
            Assert.True(outcome.Converged);
            Assert.Single(script.Installs);
            Assert.Contains("converged", outcome.StopReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Loop_RegressingPasses_KeepsBest_ReinstallsInitialAtEnd()
        {
            // Every pass makes things worse; the best state is the initial install, so the
            // loop must end by putting the initial LUTs back on the display.
            var initial = BuildInitial();
            var script = new ScriptedLadders(
                Uniform(1.05),   // initial ladder: 5%
                Uniform(1.08),   // after pass 1: 8% (worse)
                Uniform(1.10),   // after pass 2: 10% (worse)
                Uniform(1.12));  // after pass 3: 12% (worse)

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = ScriptRungs,
                MeasureLadderAsync = script.MeasureAsync,
                InstallAsync = script.InstallAsync,
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall);
            Assert.False(outcome.Converged);
            Assert.True(outcome.EndedOnBest);
            Assert.Same(initial, outcome.FinalLuts);
            Assert.Equal(0.05, outcome.FinalAvgAbsError, 6);
            Assert.Equal(4, script.Installs.Count); // 3 passes + the best-reinstall
            Assert.Same(initial, script.Installs[^1]);
        }

        [Fact]
        public async Task Loop_ImprovingPasses_StopsAtMaxPasses_NoReinstallNeeded()
        {
            var initial = BuildInitial();
            var script = new ScriptedLadders(
                Uniform(1.10), Uniform(1.06), Uniform(1.04), Uniform(1.02));

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = ScriptRungs,
                MeasureLadderAsync = script.MeasureAsync,
                InstallAsync = script.InstallAsync,
            }, CancellationToken.None);

            Assert.False(outcome.Converged);
            Assert.False(outcome.EndedOnBest); // last pass is best; nothing to reinstall
            Assert.Equal(3, script.Installs.Count);
            Assert.Equal(0.02, outcome.FinalAvgAbsError, 6);
            Assert.Equal(3, outcome.Passes.Count);
            Assert.Contains("max passes", outcome.StopReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Loop_WildErrorMidLoop_StopsAndRestoresBest()
        {
            // Pass 1's verification shows a rung at +50% (profile knocked out mid-run, night
            // mode kicked in, …) — Refine refuses on pass 2 and the loop restores the best
            // measured state (the initial install).
            var initial = BuildInitial();
            var script = new ScriptedLadders(
                Uniform(1.05),
                new[] { 1.05, 1.5, 1.05, 1.05 });

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = ScriptRungs,
                MeasureLadderAsync = script.MeasureAsync,
                InstallAsync = script.InstallAsync,
            }, CancellationToken.None);

            Assert.False(outcome.Converged);
            Assert.Contains("refused", outcome.StopReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(outcome.EndedOnBest);
            Assert.Same(initial, outcome.FinalLuts);
            Assert.Same(initial, script.Installs[^1]);
        }

        [Fact]
        public async Task Loop_InstallerRebuildsLuts_StopsHonestly()
        {
            // The installer rejecting the override (matrix-scale mismatch) surfaces as a
            // different Result instance; the loop must not iterate on LUTs it never computed.
            var initial = BuildInitial();
            var script = new ScriptedLadders(Uniform(1.05));
            var rebuilt = BuildInitial(DriftedPanel);

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = ScriptRungs,
                MeasureLadderAsync = script.MeasureAsync,
                InstallAsync = (_, _) => Task.FromResult((rebuilt, "rebuilt-profile")),
            }, CancellationToken.None);

            Assert.Contains("rebuilt", outcome.StopReason, StringComparison.OrdinalIgnoreCase);
            Assert.False(outcome.Converged);
            // The best (initial) LUTs are restored over the rebuilt install at the end.
            Assert.True(outcome.EndedOnBest);
            Assert.Same(initial, outcome.FinalLuts);
        }

        [Fact]
        public async Task Loop_CancellationMidLoop_AttemptsBestReinstall_ThenPropagates()
        {
            var initial = BuildInitial();
            var installs = new List<HdrMhc2LutBuilder.Result>();
            using var cts = new CancellationTokenSource();
            int ladderCalls = 0;

            Task<IReadOnlyList<MeasurementResult>> Measure(
                IReadOnlyList<double> rungs, int offset, CancellationToken ct)
            {
                ladderCalls++;
                if (ladderCalls == 1)
                {
                    IReadOnlyList<MeasurementResult> initialLadder = rungs
                        .Select((n, i) => MakeWireMeasurement(n, n * 1.05, offset + i)).ToList();
                    return Task.FromResult(initialLadder);
                }
                // Cancelled while measuring the post-install ladder of pass 1.
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }

            Task<(HdrMhc2LutBuilder.Result, string)> Install(
                HdrMhc2LutBuilder.Result candidate, CancellationToken ct)
            {
                installs.Add(candidate);
                return Task.FromResult((candidate, $"profile-{installs.Count}"));
            }

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
                {
                    InitialLuts = initial,
                    RungNits = ScriptRungs,
                    MeasureLadderAsync = Measure,
                    InstallAsync = Install,
                }, cts.Token));

            // Pass 1 installed its candidate; the cancel path must have restored the best
            // known state (the initial LUTs) before propagating.
            Assert.Equal(2, installs.Count);
            Assert.Same(initial, installs[^1]);
        }

        [Fact]
        public async Task Loop_ReportsPerPassProgress()
        {
            var initial = BuildInitial();
            var display = new SimulatedDisplay(initial, DriftedPanel);
            var progress = new List<HdrRefinementLoop.PassProgress>();

            await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = RefineRungs,
                MeasureLadderAsync = display.MeasureAsync,
                InstallAsync = display.InstallAsync,
                Progress = new SynchronousProgress<HdrRefinementLoop.PassProgress>(progress.Add),
            }, CancellationToken.None);

            Assert.Contains(progress, p => p.Phase.Contains("initial ladder"));
            Assert.Contains(progress, p => p.Pass == 1 && p.Phase == "installing");
        }

        private sealed class SynchronousProgress<T> : IProgress<T>
        {
            private readonly Action<T> _handler;
            public SynchronousProgress(Action<T> handler) => _handler = handler;
            public void Report(T value) => _handler(value);
        }
    }
}
