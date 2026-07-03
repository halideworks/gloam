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
    }
}
