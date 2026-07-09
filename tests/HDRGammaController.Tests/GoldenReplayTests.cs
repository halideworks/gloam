using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tier A of the golden-sample regression rig: recorded REAL-panel measurement sets
    /// (CSV fixtures under Fixtures\Golden) are replayed through the live pipeline stages —
    /// characterization fit, HDR wire LUT build, verification metrics, uncertainty budget —
    /// and every number is compared against the committed baseline with explicit tolerances.
    /// A failure here means a modeling change altered what the pipeline computes from real
    /// data; if the change is intended, regenerate the baseline with
    /// <c>cli golden-ingest &lt;fixtureDir&gt;</c> so the diff is visible in review.
    /// </summary>
    public class GoldenReplayTests
    {
        // Comparison tolerances: tight enough to catch modeling drift, loose enough to
        // survive benign floating-point reassociation.
        private const double DeltaETol = 0.05;
        private const double ChromaticityTol = 0.0005;
        private const double NitsRelTol = 0.005;   // 0.5%
        private const double GammaTol = 0.01;
        private const double LutTol = 1e-4;
        private const double ToneTol = 1e-4;

        public static TheoryData<string> Fixtures()
        {
            var data = new TheoryData<string>();
            foreach (string dir in DiscoverFixtureDirs())
                data.Add(Path.GetFileName(dir));
            return data;
        }

        private static string FixturesRoot =>
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Golden");

        private static IEnumerable<string> DiscoverFixtureDirs()
        {
            if (!Directory.Exists(FixturesRoot))
                yield break;
            foreach (string dir in Directory.EnumerateDirectories(FixturesRoot))
            {
                if (File.Exists(Path.Combine(dir, "manifest.json")) &&
                    File.Exists(Path.Combine(dir, "baseline.json")))
                {
                    yield return dir;
                }
            }
        }

        [Fact]
        public void AtLeastOneGoldenFixtureExists()
        {
            Assert.True(DiscoverFixtureDirs().Any(),
                $"No golden fixtures found under {FixturesRoot} - the regression rig is not running.");
        }

        private static (GoldenSampleManifest Manifest, GoldenSampleBaseline Baseline,
            IReadOnlyList<MeasurementResult> Native, IReadOnlyList<MeasurementResult> Verification,
            CalibrationTarget Target) LoadFixture(string fixtureName)
        {
            string dir = Path.Combine(FixturesRoot, fixtureName);
            var manifest = GoldenSampleManifest.Load(Path.Combine(dir, "manifest.json"));
            var baseline = GoldenSampleBaseline.Load(Path.Combine(dir, "baseline.json"));
            var native = MeasurementCsvImporter.Load(Path.Combine(dir, manifest.NativeCsv)).Measurements;
            var verification = MeasurementCsvImporter.Load(Path.Combine(dir, manifest.VerificationCsv)).Measurements;
            var target = StandardTargets.GetByName(manifest.TargetName)
                ?? throw new InvalidDataException($"Unknown target '{manifest.TargetName}' in {fixtureName}");
            return (manifest, baseline, native, verification, target);
        }

        private static GoldenSampleBaseline Recompute(string fixtureName)
        {
            var (manifest, _, native, verification, target) = LoadFixture(fixtureName);
            return GoldenSampleBaseline.Compute(
                native, verification, target, manifest.HdrMode, manifest.SdrWhiteNits,
                manifest.ToUncertaintyContext(verification));
        }

        private static void AssertClose(double expected, double actual, double tol, string what)
            => Assert.True(Math.Abs(expected - actual) <= tol,
                $"{what}: baseline {expected:G9}, recomputed {actual:G9} (tolerance {tol:G4})");

        private static void AssertCloseRel(double expected, double actual, double relTol, string what)
        {
            double scale = Math.Max(Math.Abs(expected), 1e-6);
            Assert.True(Math.Abs(expected - actual) / scale <= relTol,
                $"{what}: baseline {expected:G9}, recomputed {actual:G9} (rel tolerance {relTol:P2})");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Characterization_MatchesBaseline(string fixtureName)
        {
            var baseline = LoadFixture(fixtureName).Baseline;
            var actual = Recompute(fixtureName);

            AssertCloseRel(baseline.PeakLuminanceNits, actual.PeakLuminanceNits, NitsRelTol, "peak luminance");
            AssertClose(baseline.BlackLevelNits, actual.BlackLevelNits, 0.01, "black level nits");
            AssertClose(baseline.MeasuredGamma, actual.MeasuredGamma, GammaTol, "measured gamma");
            AssertClose(baseline.RedPrimaryX, actual.RedPrimaryX, ChromaticityTol, "red x");
            AssertClose(baseline.RedPrimaryY, actual.RedPrimaryY, ChromaticityTol, "red y");
            AssertClose(baseline.GreenPrimaryX, actual.GreenPrimaryX, ChromaticityTol, "green x");
            AssertClose(baseline.GreenPrimaryY, actual.GreenPrimaryY, ChromaticityTol, "green y");
            AssertClose(baseline.BluePrimaryX, actual.BluePrimaryX, ChromaticityTol, "blue x");
            AssertClose(baseline.BluePrimaryY, actual.BluePrimaryY, ChromaticityTol, "blue y");
            AssertClose(baseline.WhitePointX, actual.WhitePointX, ChromaticityTol, "white x");
            AssertClose(baseline.WhitePointY, actual.WhitePointY, ChromaticityTol, "white y");
            Assert.Equal(baseline.HasPerChannelToneCurves, actual.HasPerChannelToneCurves);

            Assert.Equal(baseline.NeutralToneSamples.Count, actual.NeutralToneSamples.Count);
            for (int i = 0; i < baseline.NeutralToneSamples.Count; i++)
                AssertClose(baseline.NeutralToneSamples[i], actual.NeutralToneSamples[i], ToneTol,
                    $"neutral tone sample {i}");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void VerifyMetrics_MatchBaseline(string fixtureName)
        {
            var baseline = LoadFixture(fixtureName).Baseline;
            var actual = Recompute(fixtureName);

            AssertClose(baseline.AverageDeltaE, actual.AverageDeltaE, DeltaETol, "avg dE2000");
            AssertClose(baseline.MaxDeltaE, actual.MaxDeltaE, DeltaETol, "max dE2000");
            AssertClose(baseline.MedianDeltaE, actual.MedianDeltaE, DeltaETol, "median dE2000");
            AssertClose(baseline.AverageGrayscaleDeltaE, actual.AverageGrayscaleDeltaE, DeltaETol, "grayscale avg dE");
            AssertClose(baseline.AveragePrimaryDeltaE, actual.AveragePrimaryDeltaE, DeltaETol, "primary avg dE");
            AssertClose(baseline.AverageItpDeltaE, actual.AverageItpDeltaE, DeltaETol * 3, "avg dE ITP");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void Uncertainty_MatchesBaseline(string fixtureName)
        {
            var baseline = LoadFixture(fixtureName).Baseline;
            var actual = Recompute(fixtureName);

            Assert.Equal(baseline.UncertaintyExpandedU.HasValue, actual.UncertaintyExpandedU.HasValue);
            if (baseline.UncertaintyExpandedU is { } expected && actual.UncertaintyExpandedU is { } got)
                AssertClose(expected, got, DeltaETol, "expanded uncertainty U95");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void HdrLutBuild_MatchesBaseline(string fixtureName)
        {
            var (manifest, baseline, _, _, _) = LoadFixture(fixtureName);
            if (!manifest.HdrMode || baseline.HdrLutSamples == null)
                return; // SDR fixture — nothing to compare

            var actual = Recompute(fixtureName);

            Assert.NotNull(actual.HdrLutSamples);
            AssertCloseRel(baseline.HdrMeasuredPeakNits!.Value, actual.HdrMeasuredPeakNits!.Value,
                NitsRelTol, "hdr peak nits");
            AssertClose(baseline.HdrMeasuredBlackNits!.Value, actual.HdrMeasuredBlackNits!.Value,
                0.01, "hdr black nits");
            Assert.Equal(baseline.HdrWireExact, actual.HdrWireExact);

            Assert.Equal(baseline.HdrLutSamples.Count, actual.HdrLutSamples!.Count);
            for (int i = 0; i < baseline.HdrLutSamples.Count; i++)
                AssertClose(baseline.HdrLutSamples[i], actual.HdrLutSamples[i], LutTol, $"hdr lut sample {i}");
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void RecordedMeasurements_AreInternallyConsistent(string fixtureName)
        {
            // Sanity gate on the recording itself (not the pipeline): a corrupted fixture
            // should fail loudly here rather than as a confusing baseline mismatch.
            var (_, _, native, verification, _) = LoadFixture(fixtureName);

            Assert.True(native.Count >= 10, "native recording implausibly small");
            Assert.True(verification.Count >= 6, "verification recording implausibly small");
            Assert.Contains(native, m => m.IsValid && m.Patch.Category == PatchCategory.Grayscale);
            Assert.All(native.Where(m => m.IsValid), m =>
            {
                Assert.True(double.IsFinite(m.Xyz.Y), $"non-finite Y in valid row '{m.Patch.Name}'");
                Assert.True(m.Xyz.Y >= 0, $"negative luminance in '{m.Patch.Name}'");
            });
        }
    }
}
