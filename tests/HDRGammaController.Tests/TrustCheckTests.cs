using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Trust check (roadmap 4.3): the 6-patch grading math and the honesty rules — black by
    /// nits only, drift alerts gated on the combined uncertainty of the two runs.
    /// </summary>
    public class TrustCheckTests
    {
        private const double WhiteNits = 200.0;

        private static MeasurementResult Measure(ColorPatch patch, CieXyz xyz, int seq = 0) => new()
        {
            Patch = patch,
            Xyz = xyz,
            SequenceIndex = seq,
        };

        /// <summary>Simulates a perfect sRGB/2.2 panel measuring the six patches.</summary>
        private static List<MeasurementResult> PerfectPanelMeasurements()
        {
            var target = StandardTargets.SrgbGamma22;
            var results = new List<MeasurementResult>();
            foreach (var patch in TrustCheck.BuildPatches())
            {
                double lin(double signal) => Math.Pow(signal, 2.2);
                var rgb = new LinearRgb(
                    lin(patch.DisplayRgb.R), lin(patch.DisplayRgb.G), lin(patch.DisplayRgb.B));
                var xyz = ColorMath.LinearSrgbToXyz(rgb);
                results.Add(Measure(patch, new CieXyz(
                    xyz.X * WhiteNits, xyz.Y * WhiteNits, xyz.Z * WhiteNits), patch.Index));
            }
            return results;
        }

        [Fact]
        public void BuildPatches_SixPatches_WhiteGrayBlackAndPrimaries()
        {
            var patches = TrustCheck.BuildPatches();
            Assert.Equal(6, patches.Count);
            Assert.Equal(3, patches.Count(p => p.Category == PatchCategory.Grayscale));
            Assert.Equal(3, patches.Count(p => p.Category == PatchCategory.Primary));
            Assert.All(patches, p => Assert.True(p.IsCritical));
        }

        [Fact]
        public void Compute_PerfectPanel_NearZeroDeltaE_AndCorrectWhite()
        {
            var grade = TrustCheck.Compute(PerfectPanelMeasurements(), StandardTargets.SrgbGamma22);

            Assert.True(grade.AvgDeltaE2000 < 0.3, $"perfect panel graded {grade.AvgDeltaE2000:F3}");
            Assert.InRange(grade.WhiteCctK, 6400, 6600); // D65 ≈ 6504 K
            Assert.InRange(Math.Abs(grade.WhiteDuv), 0.0, 0.004);
            Assert.Equal(WhiteNits, grade.WhiteNits, 3);
        }

        [Fact]
        public void Compute_BlackPatch_GradedByLuminanceOnly()
        {
            var measurements = PerfectPanelMeasurements();
            var grade = TrustCheck.Compute(measurements, StandardTargets.SrgbGamma22);

            var black = grade.Patches.Single(p => p.Name == "Black");
            Assert.Null(black.DeltaE2000);
            Assert.Null(black.DeltaEItp);
            Assert.False(double.IsNaN(grade.BlackNits));
            // And the black patch does not contaminate the average.
            Assert.Equal(5, grade.Patches.Count(p => p.DeltaE2000 != null));
        }

        [Fact]
        public void Compute_WithUncertaintyContext_ProducesU95()
        {
            var context = new UncertaintyBudget.Context(
                UncertaintyBudget.InstrumentClass.ColorimeterGeneric, null, null, false);
            var grade = TrustCheck.Compute(PerfectPanelMeasurements(), StandardTargets.SrgbGamma22, context);

            Assert.NotNull(grade.U95DeltaE);
            Assert.True(grade.U95DeltaE > 0);
        }

        [Fact]
        public void Compute_NoWhite_Throws()
        {
            var noWhite = PerfectPanelMeasurements().Where(m => m.Patch.Name != "White").ToList();
            Assert.Throws<InvalidOperationException>(() =>
                TrustCheck.Compute(noWhite, StandardTargets.SrgbGamma22));
        }
    }

    public class TrustCheckHistoryTests : IDisposable
    {
        private readonly string _dir;

        public TrustCheckHistoryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"gloam-trend-test-{Guid.NewGuid():N}");
            TrustCheckHistory.TrendDirectoryOverride = _dir;
        }

        public void Dispose()
        {
            TrustCheckHistory.TrendDirectoryOverride = null;
            try { Directory.Delete(_dir, recursive: true); } catch { }
            GC.SuppressFinalize(this);
        }

        private static TrustCheckEntry Entry(
            string monitor = @"\\?\DISPLAY#TEST#1",
            string? profileId = "profile-a",
            double avgDeltaE = 0.5,
            double whiteDuv = 0.001,
            double? u95 = 0.3,
            DateTime? at = null) => new()
        {
            TimestampUtc = at ?? new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            MonitorDevicePath = monitor,
            ProfileId = profileId,
            ProfileName = "Test Profile",
            TargetName = "sRGB (Gamma 2.2)",
            AvgDeltaE2000 = avgDeltaE,
            WhiteDeltaE2000 = avgDeltaE,
            WhiteCctK = 6500,
            WhiteDuv = whiteDuv,
            WhiteNits = 200,
            BlackNits = 0.2,
            U95DeltaE = u95,
            Patches = new[] { new TrustCheck.PatchGrade("White", 0.4, 1.1, 200.0) },
        };

        [Fact]
        public void AppendLoad_RoundTrips_InOrder()
        {
            TrustCheckHistory.Append(Entry(avgDeltaE: 0.4));
            TrustCheckHistory.Append(Entry(avgDeltaE: 0.6, at: new DateTime(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc)));

            var loaded = TrustCheckHistory.Load(@"\\?\DISPLAY#TEST#1");

            Assert.Equal(2, loaded.Count);
            Assert.Equal(0.4, loaded[0].AvgDeltaE2000);
            Assert.Equal(0.6, loaded[1].AvgDeltaE2000);
            Assert.Equal("Test Profile", loaded[0].ProfileName);
            var patch = Assert.Single(loaded[0].Patches);
            Assert.Equal("White", patch.Name);
        }

        [Fact]
        public void Load_CorruptLine_SkippedNotFatal()
        {
            TrustCheckHistory.Append(Entry());
            File.AppendAllText(TrustCheckHistory.GetHistoryPath(@"\\?\DISPLAY#TEST#1"),
                "{ torn json line without an end\n");
            TrustCheckHistory.Append(Entry(avgDeltaE: 0.9));

            var loaded = TrustCheckHistory.Load(@"\\?\DISPLAY#TEST#1");

            Assert.Equal(2, loaded.Count);
        }

        [Fact]
        public void Load_UnknownJsonFields_Ignored()
        {
            File.AppendAllText(TrustCheckHistory.GetHistoryPath("mon-x"),
                "{\"SchemaVersion\":9,\"MonitorDevicePath\":\"mon-x\",\"AvgDeltaE2000\":0.7,\"FutureField\":\"surprise\"}\n");

            var loaded = TrustCheckHistory.Load("mon-x");

            var entry = Assert.Single(loaded);
            Assert.Equal(0.7, entry.AvgDeltaE2000);
        }

        [Fact]
        public void PerMonitorFiles_AreIsolated()
        {
            TrustCheckHistory.Append(Entry(monitor: "monitor-one"));
            TrustCheckHistory.Append(Entry(monitor: "monitor-two", avgDeltaE: 2.0));

            Assert.Single(TrustCheckHistory.Load("monitor-one"));
            Assert.Single(TrustCheckHistory.Load("monitor-two"));
            Assert.Empty(TrustCheckHistory.Load("monitor-three"));
        }

        [Fact]
        public void AnalyzeDrift_WithinCombinedU95_NoAlert()
        {
            // The honesty case: a 1.2 ΔE drift with ±1.0 U95 on each run (combined ≈ 1.41)
            // is statistically indistinguishable — no alert, whatever the floor says.
            var history = new[]
            {
                Entry(avgDeltaE: 0.5, u95: 1.0),
                Entry(avgDeltaE: 1.7, u95: 1.0, at: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
            };

            var verdict = TrustCheckHistory.AnalyzeDrift(history);

            Assert.NotNull(verdict);
            Assert.False(verdict!.Alert);
            Assert.Equal(1.2, verdict.DeltaEDrift, 6);
        }

        [Fact]
        public void AnalyzeDrift_BeyondU95AndFloor_Alerts()
        {
            var history = new[]
            {
                Entry(avgDeltaE: 0.5, u95: 0.3),
                Entry(avgDeltaE: 2.2, u95: 0.3, at: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            };

            var verdict = TrustCheckHistory.AnalyzeDrift(history);

            Assert.NotNull(verdict);
            Assert.True(verdict!.Alert);
            Assert.Contains("recalibrat", verdict.Summary, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AnalyzeDrift_SmallDrift_UnderFloor_NoAlert()
        {
            // Even with tiny uncertainty, a 0.4 ΔE drift is under the 1.0 practical floor.
            var history = new[]
            {
                Entry(avgDeltaE: 0.5, u95: 0.05),
                Entry(avgDeltaE: 0.9, u95: 0.05, at: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            };

            Assert.False(TrustCheckHistory.AnalyzeDrift(history)!.Alert);
        }

        [Fact]
        public void AnalyzeDrift_WhiteDuvShift_Alerts()
        {
            var history = new[]
            {
                Entry(whiteDuv: 0.000),
                Entry(whiteDuv: 0.005, at: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            };

            var verdict = TrustCheckHistory.AnalyzeDrift(history);

            Assert.True(verdict!.Alert);
            Assert.Equal(0.005, verdict.DuvDrift, 6);
        }

        [Fact]
        public void AnalyzeDrift_ProfileChanged_BaselineResets()
        {
            // Latest was recorded under profile-b; only the profile-b entry can be baseline,
            // and a single entry under the new profile has nothing to compare against.
            var history = new[]
            {
                Entry(profileId: "profile-a", avgDeltaE: 0.5),
                Entry(profileId: "profile-b", avgDeltaE: 3.0, at: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            };

            Assert.Null(TrustCheckHistory.AnalyzeDrift(history));
        }

        [Fact]
        public void ShouldRemind_RespectsIntervalAndNull()
        {
            var now = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(TrustCheckHistory.ShouldRemind(null, now, 30));
            Assert.False(TrustCheckHistory.ShouldRemind(now.AddDays(-10), now, 30));
            Assert.True(TrustCheckHistory.ShouldRemind(now.AddDays(-31), now, 30));
            Assert.False(TrustCheckHistory.ShouldRemind(null, now, 0)); // disabled
        }

        [Fact]
        public void BuildCsv_ContainsHeaderAndRows()
        {
            var csv = TrustCheckHistory.BuildCsv(new[] { Entry(avgDeltaE: 0.55) });
            Assert.StartsWith("timestamp_utc,", csv);
            Assert.Contains("0.55", csv);
        }
    }
}
