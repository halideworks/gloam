using System;
using System.Collections.Generic;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Multi-display matching (roadmap 4.5): the minimax solve lands every panel on one
    /// reachable white, gains never exceed 1 (nothing clips), and the common luminance is
    /// the dimmest panel's honest reach.
    /// </summary>
    public class DisplayMatchSolverTests
    {
        private static DisplayMatchSolver.DisplayInput Display(
            string name, Chromaticity white, double peakNits,
            Chromaticity? red = null, Chromaticity? green = null, Chromaticity? blue = null)
        {
            var matrix = ColorMath.CalculateRgbToXyzMatrix(
                red ?? Chromaticity.Rec709Red,
                green ?? Chromaticity.Rec709Green,
                blue ?? Chromaticity.Rec709Blue,
                white);
            return new DisplayMatchSolver.DisplayInput($"dev-{name}", name, matrix, peakNits, white);
        }

        [Fact]
        public void IdenticalDisplays_MatchAtTheirOwnWhite_NearZeroCost()
        {
            var displays = new[]
            {
                Display("A", Chromaticity.D65, 200),
                Display("B", Chromaticity.D65, 200),
            };

            var solution = DisplayMatchSolver.Solve(displays);

            Assert.Equal(Chromaticity.D65.X, solution.CommonWhite.X, 2);
            Assert.Equal(Chromaticity.D65.Y, solution.CommonWhite.Y, 2);
            Assert.Equal(200, solution.CommonLuminanceNits, 0);
            Assert.True(solution.MaxDeltaEPrime < 1.0,
                $"identical panels should match nearly free, cost {solution.MaxDeltaEPrime:F2}");
            Assert.All(solution.Adjustments, a =>
            {
                Assert.InRange(a.GainR, 0.99, 1.0);
                Assert.InRange(a.GainG, 0.99, 1.0);
                Assert.InRange(a.GainB, 0.99, 1.0);
                Assert.InRange(a.BrightnessPercent, 99, 100);
            });
        }

        [Fact]
        public void MismatchedWhites_SolutionLandsBetween_AndGainsStayUnderOne()
        {
            // Panel A calibrated slightly warm, panel B slightly cool (a realistic residual
            // mismatch between two "D65" panels).
            var warm = new Chromaticity(0.3160, 0.3320);
            var cool = new Chromaticity(0.3095, 0.3260);
            var displays = new[]
            {
                Display("Warm", warm, 250),
                Display("Cool", cool, 220),
            };

            var solution = DisplayMatchSolver.Solve(displays);

            Assert.InRange(solution.CommonWhite.X, cool.X - 1e-9, warm.X + 1e-9);
            Assert.InRange(solution.CommonWhite.Y, cool.Y - 1e-9, warm.Y + 1e-9);
            Assert.All(solution.Adjustments, a =>
            {
                Assert.InRange(a.GainR, 0.5, 1.0 + 1e-9);
                Assert.InRange(a.GainG, 0.5, 1.0 + 1e-9);
                Assert.InRange(a.GainB, 0.5, 1.0 + 1e-9);
                double maxGain = Math.Max(a.GainR, Math.Max(a.GainG, a.GainB));
                Assert.Equal(1.0, maxGain, 6); // normalize-to-max: no headroom wasted
            });
        }

        [Fact]
        public void GainsReproduceTheCommonWhite_ThroughEachPanelsMatrix()
        {
            var warm = new Chromaticity(0.3160, 0.3320);
            var cool = new Chromaticity(0.3095, 0.3260);
            var displays = new[] { Display("Warm", warm, 250), Display("Cool", cool, 220) };

            var solution = DisplayMatchSolver.Solve(displays);

            foreach (var (input, adj) in displays.Zip(solution.Adjustments))
            {
                // Push the solved gains through the panel's measured matrix: the emitted
                // chromaticity must be the common white.
                var m = input.RgbToXyzMatrix;
                double x = m[0, 0] * adj.GainR + m[0, 1] * adj.GainG + m[0, 2] * adj.GainB;
                double y = m[1, 0] * adj.GainR + m[1, 1] * adj.GainG + m[1, 2] * adj.GainB;
                double z = m[2, 0] * adj.GainR + m[2, 1] * adj.GainG + m[2, 2] * adj.GainB;
                var emitted = new CieXyz(x, y, z).ToChromaticity();

                Assert.Equal(solution.CommonWhite.X, emitted.X, 3);
                Assert.Equal(solution.CommonWhite.Y, emitted.Y, 3);
            }
        }

        [Fact]
        public void CommonLuminance_IsTheDimmestPanelsReach_AndBrightPanelDims()
        {
            var displays = new[]
            {
                Display("Bright", Chromaticity.D65, 400),
                Display("Dim", Chromaticity.D65, 180),
            };

            var solution = DisplayMatchSolver.Solve(displays);

            Assert.InRange(solution.CommonLuminanceNits, 170, 181);
            var bright = solution.Adjustments.Single(a => a.FriendlyName == "Bright");
            var dim = solution.Adjustments.Single(a => a.FriendlyName == "Dim");
            Assert.InRange(bright.BrightnessPercent, 40, 50); // ~180/400
            Assert.InRange(dim.BrightnessPercent, 95, 100);
        }

        [Fact]
        public void WideAndNarrowGamutPair_StillSolves()
        {
            var displays = new[]
            {
                Display("WideGamut", Chromaticity.D65, 300,
                    new Chromaticity(0.680, 0.320), new Chromaticity(0.265, 0.690), new Chromaticity(0.150, 0.060)),
                Display("Srgb", new Chromaticity(0.3150, 0.3300), 200),
            };

            var solution = DisplayMatchSolver.Solve(displays);

            Assert.True(solution.MaxDeltaEPrime < 20);
            Assert.Equal(2, solution.Adjustments.Count);
        }

        [Fact]
        public void SingleDisplay_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                DisplayMatchSolver.Solve(new[] { Display("A", Chromaticity.D65, 200) }));
        }
    }

    public class DriftPredictorTests
    {
        private static TrustCheckEntry Entry(double daysAgo, double avgDeltaE, double? u95 = 0.4,
            string profileId = "p1") => new()
        {
            TimestampUtc = Now.AddDays(-daysAgo),
            MonitorDevicePath = "mon",
            ProfileId = profileId,
            AvgDeltaE2000 = avgDeltaE,
            U95DeltaE = u95,
        };

        private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void TooFewPoints_OrTooShortSpan_ReturnsNull()
        {
            Assert.Null(DriftPredictor.Predict(new[] { Entry(1, 0.5), Entry(0, 0.6) }, Now));
            Assert.Null(DriftPredictor.Predict(
                new[] { Entry(3, 0.5), Entry(2, 0.55), Entry(1, 0.6) }, Now)); // 2-day span
        }

        [Fact]
        public void StableHistory_ReportsNoSignificantDrift()
        {
            var history = new[]
            {
                Entry(60, 0.52), Entry(45, 0.48), Entry(30, 0.55), Entry(15, 0.50), Entry(1, 0.53),
            };

            var p = DriftPredictor.Predict(history, Now);

            Assert.NotNull(p);
            Assert.False(p!.SlopeSignificant);
            Assert.Null(p.PredictedThresholdCrossingUtc);
            Assert.Contains("Stable", p.Summary);
        }

        [Fact]
        public void CleanUpwardDrift_PredictsCrossingDate()
        {
            // 0.01 ΔE/day, baseline 0.5: crosses baseline+1.0 at day 100 (t0 = 90 days ago
            // → crossing ~10 days from now).
            var history = new[]
            {
                Entry(90, 0.50), Entry(60, 0.80), Entry(30, 1.10), Entry(0, 1.40),
            };

            var p = DriftPredictor.Predict(history, Now);

            Assert.NotNull(p);
            Assert.True(p!.SlopeSignificant);
            Assert.Equal(0.01, p.SlopeDeltaEPerDay, 4);
            Assert.NotNull(p.PredictedThresholdCrossingUtc);
            double daysOut = (p.PredictedThresholdCrossingUtc!.Value - Now).TotalDays;
            Assert.InRange(daysOut, 7, 13);
            Assert.Contains("Drifting", p.Summary);
        }

        [Fact]
        public void AlreadyPastThreshold_SaysSoNow()
        {
            var history = new[]
            {
                Entry(60, 0.50), Entry(40, 1.00), Entry(20, 1.50), Entry(0, 2.00),
            };

            var p = DriftPredictor.Predict(history, Now);

            Assert.NotNull(p);
            Assert.NotNull(p!.PredictedThresholdCrossingUtc);
            Assert.True(p.PredictedThresholdCrossingUtc <= Now);
            Assert.Contains("recalibrate", p.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ProfileChange_RestartsTheSeries()
        {
            // Old profile drifted badly; the new profile has only 2 points → no prediction
            // rather than inheriting the old trend.
            var history = new[]
            {
                Entry(90, 0.5, profileId: "old"), Entry(60, 1.5, profileId: "old"),
                Entry(30, 2.5, profileId: "old"),
                Entry(20, 0.4, profileId: "new"), Entry(0, 0.45, profileId: "new"),
            };

            Assert.Null(DriftPredictor.Predict(history, Now));
        }

        [Fact]
        public void PredictionInterval_WidensWithMeasurementUncertainty()
        {
            TrustCheckEntry[] History(double? u95) => new[]
            {
                Entry(90, 0.50, u95), Entry(60, 0.80, u95), Entry(30, 1.10, u95), Entry(0, 1.40, u95),
            };

            var tight = DriftPredictor.Predict(History(0.1), Now)!;
            var loose = DriftPredictor.Predict(History(1.5), Now)!;

            Assert.True(loose.PredictedCurrentU95 > tight.PredictedCurrentU95);
        }
    }
}
