using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests
{
    /// <summary>
    /// Regression cover for the OLED brightness-limiter transient that failed two real runs
    /// on a MAG 271QP X28 (2026-08-05). A white drift anchor measured right after a long
    /// dark blue-ramp sequence read 265.71 / 263.36 / 260.94 cd/m² across its 1.2s burst
    /// while the same run's other three whites all sat at 242-243. The median of that
    /// decaying burst (263.36) was recorded as if it were the panel's steady state, which
    /// tripped <see cref="CalibrationMeasurementValidator"/>'s 8% repeated-white gate
    /// (8.76%) and failed the entire run with a message blaming warm-up.
    /// </summary>
    public class HighAplSettleTests
    {
        /// <summary>Returns scripted readings per patch name; gamma-2.2 for everything else.</summary>
        private sealed class ScriptedColorimeter : ColorimeterService
        {
            private readonly Dictionary<string, Queue<double>> _scriptedY;

            public ScriptedColorimeter(Dictionary<string, Queue<double>> scriptedY)
                : base(string.Empty) => _scriptedY = scriptedY;

            public int ReadsFor(string patchName) => _readCounts.TryGetValue(patchName, out int n) ? n : 0;

            private readonly Dictionary<string, int> _readCounts = new();

            public override bool IsReady => true;

            public override Task BeginMeasurementSessionAsync(bool hdrMode, CancellationToken cancellationToken = default)
                => Task.CompletedTask;

            public override Task EndMeasurementSessionAsync() => Task.CompletedTask;

            public override Task<MeasurementResult> MeasureAsync(
                ColorPatch patch, bool hdrMode = false, CancellationToken cancellationToken = default)
            {
                _readCounts[patch.Name] = ReadsFor(patch.Name) + 1;

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

        private static CalibrationOrchestrator FastOrchestrator(
            ScriptedColorimeter fake,
            PatchSetGenerator.CalibrationPreset preset = PatchSetGenerator.CalibrationPreset.GrayscaleOnly) =>
            new CalibrationOrchestrator(
                fake,
                StandardTargets.SrgbGamma22,
                preset,
                settleTimeMs: 100,
                maxRetries: 3,
                hdrMode: false)
            {
                // Shrink every wait; the logic under test is timing-independent.
                SettleBaseMs = 1,
                SettleScaleFullSwingMs = 0,
                LargeFallSettleFloorMs = 1,
                SettleMaxMs = 1,
                InterReadDelayMs = 1,
                SettleReadDelayMs = 1,
            };

        /// <summary>
        /// The measured hardware decay, then the steady state the panel actually holds.
        /// The recorded value must be the settled 242.4, not the transient median 263.4.
        /// </summary>
        [Fact]
        public async Task DecayingWhiteBurst_KeepsReadingUntilSettled_AndRecordsTheSettledValue()
        {
            var decay = new Queue<double>(new[]
            {
                265.71, 263.36, 260.94, 258.9, 256.3, 253.1, 250.2, 247.8,
                245.6, 244.0, 243.1, 242.6, 242.45, 242.42, 242.40, 242.41,
            });
            var fake = new ScriptedColorimeter(new Dictionary<string, Queue<double>> { ["White"] = decay });

            var result = await FastOrchestrator(fake).StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            var white = Assert.Single(result.Measurements!, m => m.Patch.Name == "White");

            // Settled tail, not the transient. The old behavior recorded 263.36.
            Assert.InRange(white.Xyz.Y, 242.0, 243.0);
            Assert.True(fake.ReadsFor("White") > 3,
                "the orchestrator should have kept reading while the panel was still moving");
        }

        /// <summary>
        /// The whole point of the fix: with the transient rejected, the run's whites agree
        /// and the validator's 8% repeated-white gate passes instead of failing the run.
        /// </summary>
        [Fact]
        public void SettledWhites_PassTheRepeatedWhiteGate_WhereTheTransientMedianFailedIt()
        {
            // Exactly the four white anchors from the failed run.
            Assert.False(RepeatedWhitesPass(243.22, 242.15, 242.41, 263.36),
                "the transient median must be what the gate rejected");
            Assert.True(RepeatedWhitesPass(243.22, 242.15, 242.41, 242.40),
                "the settled value must let the same run through");
        }

        [Fact]
        public async Task SteadyWhiteBurst_TakesNoExtraReads()
        {
            // The run's well-behaved anchors: 243.07 / 243.22 / 243.42, monotonic but only
            // 0.14% of the mean. Direction alone must not trigger a chase.
            var fake = new ScriptedColorimeter(new Dictionary<string, Queue<double>>
            {
                ["White"] = new Queue<double>(new[] { 243.07, 243.22, 243.42 })
            });

            var result = await FastOrchestrator(fake).StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(3, fake.ReadsFor("White"));
            var white = Assert.Single(result.Measurements!, m => m.Patch.Name == "White");
            Assert.Equal(243.22, white.Xyz.Y, 2);
        }

        [Fact]
        public async Task SingleGlitchedReading_IsStillRejectedByTheMedian_NotChasedAsDrift()
        {
            // A spike recovers in one step: uneven step sizes, so it is a glitch and not a
            // transient. Multi-read median rejection must survive the settle logic.
            var fake = new ScriptedColorimeter(new Dictionary<string, Queue<double>>
            {
                ["White"] = new Queue<double>(new[] { 100.0, 150.0, 101.0, 100.0 })
            });

            var result = await FastOrchestrator(fake).StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            // 3 scripted + 1 from the existing spread gate, and no settle chase beyond that.
            Assert.Equal(4, fake.ReadsFor("White"));
            var white = Assert.Single(result.Measurements!, m => m.Patch.Name == "White");
            Assert.Equal(100.5, white.Xyz.Y, 6);
        }

        /// <summary>
        /// Every other high-APL burst from the same failed run. These all show a first-read
        /// overshoot that has already settled by read 2, which the 3-read median handles.
        /// The step-ratio test must tell them apart from the still-decaying anchor, or a
        /// healthy run pays a settle chase on every bright patch.
        /// </summary>
        [Theory]
        [InlineData("Green 100%", 182.306660, 174.573104, 173.706255)]
        [InlineData("Cyan 100%", 194.451567, 191.837535, 191.886684)]
        [InlineData("Yellow 100%", 224.840442, 221.318439, 221.435464)]
        [InlineData("White", 242.468890, 242.101550, 242.150245)]
        public async Task FastSettlingBurst_FromRealHardware_TakesNoExtraReads(
            string patchName, double first, double second, double third)
        {
            var fake = new ScriptedColorimeter(new Dictionary<string, Queue<double>>
            {
                [patchName] = new Queue<double>(new[] { first, second, third })
            });
            // Standard carries the primaries and secondaries as well as white.
            var orchestrator = FastOrchestrator(fake, PatchSetGenerator.CalibrationPreset.Standard);

            var result = await orchestrator.StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            // At most the 3-read burst plus one from the pre-existing 5% spread gate.
            // Secondaries (Cyan/Yellow) are single-read by policy and never reach the
            // settle path at all; what matters is that none of these start a chase.
            Assert.InRange(fake.ReadsFor(patchName), 1, MaxReadsWithoutChase);
            Assert.Empty(orchestrator.UnsettledPatches);
        }

        private const int MaxReadsWithoutChase = 4;

        [Fact]
        public async Task PatchThatNeverSettles_IsRecordedAndReported_NotSilentlyAccepted()
        {
            // A panel that never holds still: every read keeps falling. The run must not
            // hang, must record the last readings, and must say so.
            var neverSettles = new Queue<double>(Enumerable.Range(0, 200).Select(i => 300.0 - i * 2.0));
            var fake = new ScriptedColorimeter(new Dictionary<string, Queue<double>> { ["White"] = neverSettles });
            var orchestrator = FastOrchestrator(fake);
            orchestrator.MaxSettleReads = 8;

            var result = await orchestrator.StartCalibrationAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(3 + 8, fake.ReadsFor("White"));
            Assert.Contains("White", orchestrator.UnsettledPatches);
        }

        /// <summary>
        /// Runs the validator's repeated-white gate over a measurement set whose only
        /// variable is the white anchor luminances.
        /// </summary>
        private static bool RepeatedWhitesPass(params double[] whiteLuminances)
        {
            var measurements = new List<MeasurementResult>();

            void Add(ColorPatch patch, double y) => measurements.Add(new MeasurementResult
            {
                Patch = patch,
                Xyz = new CieXyz(0.95047 * y, y, 1.08883 * y),
                IsValid = true
            });

            // A grayscale ramp so the set clears the count/monotonicity/anchor gates.
            for (int i = 0; i <= 8; i++)
            {
                double s = i / 8.0;
                Add(
                    new ColorPatch
                    {
                        Name = $"Gray {s:P0}",
                        DisplayRgb = new LinearRgb(s, s, s),
                        Category = PatchCategory.Grayscale,
                    },
                    0.05 + 242.0 * Math.Pow(s, 2.2));
            }

            foreach (double y in whiteLuminances)
            {
                Add(
                    new ColorPatch
                    {
                        Name = "Drift White",
                        DisplayRgb = new LinearRgb(1, 1, 1),
                        Category = PatchCategory.DriftCheck,
                    },
                    y);
            }

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                measurements, StandardTargets.SrgbGamma22, hdrMode: false);

            // Only the repeated-white gate is under test here.
            if (!result.IsValid && result.Error?.Contains("drifted by more than", StringComparison.Ordinal) != true)
                Assert.Fail($"an unrelated gate rejected the fixture: {result.Error}");
            return result.IsValid;
        }
    }
}
