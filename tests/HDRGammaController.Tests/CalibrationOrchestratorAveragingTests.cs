using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationOrchestratorAveragingTests
    {
        /// <summary>
        /// Fake colorimeter: returns scripted readings for chosen patch names and a
        /// deterministic gamma-2.2 response for everything else. No spotread involved.
        /// </summary>
        private sealed class FakeColorimeterService : ColorimeterService
        {
            private readonly Dictionary<string, Queue<double>> _scriptedY;

            public FakeColorimeterService(Dictionary<string, Queue<double>> scriptedY)
                : base(string.Empty)
            {
                _scriptedY = scriptedY;
            }

            public override bool IsReady => true;

            public override Task BeginMeasurementSessionAsync(bool hdrMode, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public override Task EndMeasurementSessionAsync() => Task.CompletedTask;

            public override Task<MeasurementResult> MeasureAsync(
                ColorPatch patch, bool hdrMode = false, CancellationToken cancellationToken = default)
            {
                double y;
                if (_scriptedY.TryGetValue(patch.Name, out var queue) && queue.Count > 0)
                {
                    y = queue.Dequeue();
                }
                else
                {
                    double s = patch.DisplayRgb.R; // grayscale sets: R=G=B
                    y = 0.05 + 100.0 * Math.Pow(s, 2.2);
                }

                return Task.FromResult(new MeasurementResult
                {
                    Patch = patch,
                    Xyz = new CieXyz(0.95047 * y, y, 1.08883 * y),
                    IsValid = true
                });
            }
        }

        private static CalibrationOrchestrator FastOrchestrator(FakeColorimeterService fake)
        {
            var orchestrator = new CalibrationOrchestrator(
                fake,
                StandardTargets.SrgbGamma22,
                PatchSetGenerator.CalibrationPreset.GrayscaleOnly,
                settleTimeMs: 100,
                maxRetries: 3,
                hdrMode: false)
            {
                // Shrink all waits so the test runs in milliseconds; the logic under test
                // (read counts, median, progress) is unaffected.
                SettleBaseMs = 1,
                SettleScaleFullSwingMs = 0,
                LargeFallSettleFloorMs = 1,
                SettleMaxMs = 1,
                InterReadDelayMs = 1
            };
            return orchestrator;
        }

        [Fact]
        public async Task WhitePatch_GlitchedReadingAmongThree_MedianWins()
        {
            // White qualifies for multi-read. One glitched reading (150 among ~100s) makes
            // the spread exceed the 5% noise gate, so a 4th read is taken; the median of
            // {100, 150, 101, 100} = 100.5 — the glitch cannot skew the anchor.
            var fake = new FakeColorimeterService(new Dictionary<string, Queue<double>>
            {
                ["White"] = new Queue<double>(new[] { 100.0, 150.0, 101.0, 100.0 })
            });

            var result = await FastOrchestrator(fake).StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            var white = Assert.Single(result.Measurements!, m => m.Patch.Name == "White");
            Assert.Equal(100.5, white.Xyz.Y, 6);
        }

        [Fact]
        public async Task ProgressAndMeasurementCount_AreNotDoubleCountedByMultiReads()
        {
            var fake = new FakeColorimeterService(new Dictionary<string, Queue<double>>());
            var orchestrator = FastOrchestrator(fake);

            int expectedPatches = PatchSetGenerator.GeneratePatchSet(
                StandardTargets.SrgbGamma22, PatchSetGenerator.CalibrationPreset.GrayscaleOnly).Count;

            int measurementEvents = 0;
            orchestrator.MeasurementTaken += (_, _) => measurementEvents++;

            var result = await orchestrator.StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(expectedPatches, result.Measurements!.Count);
            Assert.Equal(expectedPatches, measurementEvents);
        }

        [Fact]
        public void NeedsMultiRead_SelectsNearBlackWhiteAndPrimaries()
        {
            static ColorPatch Patch(double r, double g, double b, PatchCategory cat = PatchCategory.Grayscale, double? nits = null) =>
                new() { Name = "t", DisplayRgb = new LinearRgb(r, g, b), Category = cat, Nits = nits };

            Assert.True(CalibrationOrchestrator.NeedsMultiRead(Patch(0, 0, 0)));           // black
            Assert.True(CalibrationOrchestrator.NeedsMultiRead(Patch(0.05, 0.05, 0.05)));  // near-black
            Assert.True(CalibrationOrchestrator.NeedsMultiRead(Patch(1, 1, 1)));           // white
            Assert.True(CalibrationOrchestrator.NeedsMultiRead(Patch(1, 0, 0, PatchCategory.Primary)));
            Assert.False(CalibrationOrchestrator.NeedsMultiRead(Patch(0.5, 0.5, 0.5)));    // mid-tone
            Assert.False(CalibrationOrchestrator.NeedsMultiRead(Patch(0.5, 0.5, 0.5, PatchCategory.General, nits: 200)));
        }

        [Fact]
        public void MedianMeasurement_OddCount_PicksMiddlePerComponent()
        {
            var patch = new ColorPatch { Name = "m", DisplayRgb = new LinearRgb(1, 1, 1) };
            var reads = new[]
            {
                new MeasurementResult { Patch = patch, Xyz = new CieXyz(90, 100, 110) },
                new MeasurementResult { Patch = patch, Xyz = new CieXyz(95, 160, 105) },
                new MeasurementResult { Patch = patch, Xyz = new CieXyz(91, 101, 111) }
            };

            var median = CalibrationOrchestrator.MedianMeasurement(patch, reads);

            Assert.Equal(91, median.Xyz.X, 9);
            Assert.Equal(101, median.Xyz.Y, 9);
            Assert.Equal(110, median.Xyz.Z, 9);
        }

        [Fact]
        public void MedianMeasurement_RecordsReadingCountAndSpread()
        {
            var patch = new ColorPatch { Name = "m", DisplayRgb = new LinearRgb(1, 1, 1) };
            var reads = new[]
            {
                new MeasurementResult { Patch = patch, Xyz = new CieXyz(90, 100, 110) },
                new MeasurementResult { Patch = patch, Xyz = new CieXyz(95, 104, 105) },
                new MeasurementResult { Patch = patch, Xyz = new CieXyz(91, 101, 111) }
            };

            var median = CalibrationOrchestrator.MedianMeasurement(patch, reads);

            Assert.Equal(3, median.ReadingCount);
            Assert.Equal(4.0, median.ReadingSpreadY!.Value, 9); // 104 − 100

            // Single read passes through untouched.
            var single = CalibrationOrchestrator.MedianMeasurement(patch, new[] { reads[0] });
            Assert.Equal(1, single.ReadingCount);
            Assert.Null(single.ReadingSpreadY);
        }

        // ------- Variance-adaptive integration (1.4) -------

        /// <summary>
        /// Fake colorimeter simulating a noisy-dark / quiet-bright panel: readings below
        /// 1 cd/m² jitter ±10% (alternating per call so a 3-read burst always shows the
        /// full spread); everything brighter is perfectly repeatable.
        /// </summary>
        private sealed class NoisyDarkColorimeterService : ColorimeterService
        {
            private readonly Dictionary<string, int> _callCounts = new();

            public NoisyDarkColorimeterService() : base(string.Empty) { }

            public override bool IsReady => true;

            public override Task BeginMeasurementSessionAsync(bool hdrMode, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public override Task EndMeasurementSessionAsync() => Task.CompletedTask;

            public int Calls(string patchName) => _callCounts.TryGetValue(patchName, out var c) ? c : 0;

            public override Task<MeasurementResult> MeasureAsync(
                ColorPatch patch, bool hdrMode = false, CancellationToken cancellationToken = default)
            {
                double s = patch.DisplayRgb.R;
                double y = 0.05 + 100.0 * Math.Pow(s, 2.2);

                int n = Calls(patch.Name);
                _callCounts[patch.Name] = n + 1;
                if (y < 1.0)
                    y *= n % 2 == 0 ? 1.10 : 0.90; // dark regime: ±10% meter noise

                return Task.FromResult(new MeasurementResult
                {
                    Patch = patch,
                    Xyz = new CieXyz(0.95047 * y, y, 1.08883 * y),
                    IsValid = true
                });
            }
        }

        [Fact]
        public async Task NoisyDarkPanel_EscalatesDarkSingleReadsButKeepsBrightSingle()
        {
            var fake = new NoisyDarkColorimeterService();
            var orchestrator = new CalibrationOrchestrator(
                fake,
                StandardTargets.SrgbGamma22,
                PatchSetGenerator.CalibrationPreset.GrayscaleOnly,
                settleTimeMs: 100,
                maxRetries: 3,
                hdrMode: false)
            {
                SettleBaseMs = 1,
                SettleScaleFullSwingMs = 0,
                LargeFallSettleFloorMs = 1,
                SettleMaxMs = 1,
                InterReadDelayMs = 1,
                // Appended AFTER the grayscale set, so the run's near-black multi-reads
                // have already taught the noise model that the dark decades are noisy.
                // Both signals are above the 10% near-black threshold, so neither
                // qualifies for the fixed a-priori multi-read set.
                AdditionalPatches = new[]
                {
                    new ColorPatch { Name = "AdaptDark", DisplayRgb = new LinearRgb(0.11, 0.11, 0.11), Index = 1000 },
                    new ColorPatch { Name = "AdaptBright", DisplayRgb = new LinearRgb(0.70, 0.70, 0.70), Index = 1001 },
                }
            };

            var result = await orchestrator.StartCalibrationAsync();
            Assert.True(result.Success, result.Message);

            // Dark single-read patch (~0.8 cd/m², a decade the model measured as noisy):
            // automatically escalated to a multi-read burst.
            var dark = Assert.Single(result.Measurements!, m => m.Patch.Name == "AdaptDark");
            Assert.True(dark.ReadingCount >= CalibrationOrchestrator.MultiReadCount,
                $"dark patch took {dark.ReadingCount} read(s), expected ≥ {CalibrationOrchestrator.MultiReadCount}");
            Assert.True(fake.Calls("AdaptDark") >= CalibrationOrchestrator.MultiReadCount);
            Assert.NotNull(dark.ReadingSpreadY);

            // Bright single-read patch (~45 cd/m², measured quiet): stays a single read.
            var bright = Assert.Single(result.Measurements!, m => m.Patch.Name == "AdaptBright");
            Assert.Equal(1, bright.ReadingCount);
            Assert.Equal(1, fake.Calls("AdaptBright"));

            // The noise model learned the run's character: dark decades noisy, bright quiet.
            Assert.True(orchestrator.NoiseModel.IsNoisy(0.5));
            Assert.False(orchestrator.NoiseModel.IsNoisy(50.0));
        }

        [Fact]
        public async Task QuietPanel_NeverEscalatesSingleReads()
        {
            // The deterministic fake has zero read-to-read noise, so no decade may ever
            // be flagged noisy and ordinary patches must all stay single reads.
            var fake = new FakeColorimeterService(new Dictionary<string, Queue<double>>());
            var orchestrator = FastOrchestrator(fake);

            var result = await orchestrator.StartCalibrationAsync();
            Assert.True(result.Success, result.Message);

            foreach (var m in result.Measurements!)
            {
                if (!CalibrationOrchestrator.NeedsMultiRead(m.Patch))
                    Assert.Equal(1, m.ReadingCount);
            }
        }

        [Fact]
        public void NoisyRegime_LengthensSettleBounded()
        {
            var fake = new FakeColorimeterService(new Dictionary<string, Queue<double>>());
            var target = StandardTargets.SrgbGamma22;
            var darkPatch = new ColorPatch { Name = "d", DisplayRgb = new LinearRgb(0.11, 0.11, 0.11) };

            // Baseline: quiet model.
            var quietOrchestrator = new CalibrationOrchestrator(
                fake, target, PatchSetGenerator.CalibrationPreset.GrayscaleOnly);
            int baseline = quietOrchestrator.ComputeSettleDelayMs(darkPatch);

            // Same patch with the predicted decade (~0.78 cd/m² via the assumed 100-nit
            // peak) flagged noisy: settle doubles.
            var noisyOrchestrator = new CalibrationOrchestrator(
                fake, target, PatchSetGenerator.CalibrationPreset.GrayscaleOnly);
            noisyOrchestrator.NoiseModel.Record(0.5, 0.1); // 20% relative spread → noisy bin
            int lengthened = noisyOrchestrator.ComputeSettleDelayMs(darkPatch);

            Assert.Equal(
                Math.Min(baseline * CalibrationOrchestrator.NoisySettleMultiplier,
                         noisyOrchestrator.SettleMaxMs * CalibrationOrchestrator.NoisySettleMultiplier),
                lengthened);
            Assert.True(lengthened <= noisyOrchestrator.SettleMaxMs * CalibrationOrchestrator.NoisySettleMultiplier,
                "noisy-regime settle must stay bounded");

            // A full-swing patch already at the settle ceiling hits exactly the bound.
            var noisyCeiling = new CalibrationOrchestrator(
                fake, target, PatchSetGenerator.CalibrationPreset.GrayscaleOnly, settleTimeMs: 5000);
            noisyCeiling.NoiseModel.Record(0.5, 0.1);
            int bounded = noisyCeiling.ComputeSettleDelayMs(darkPatch);
            Assert.Equal(noisyCeiling.SettleMaxMs * CalibrationOrchestrator.NoisySettleMultiplier, bounded);
        }
    }
}
