using System;
using System.IO;
using System.Linq;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Melanopic dashboard math (roadmap 3.1): spectra extraction, mel-EDI, the
    /// geometry-independence of the % reduction headline, and the uncertainty split
    /// (geometry inflates the EDI band, never the reduction band).
    /// </summary>
    public class MelanopicCalculatorTests
    {
        private static SpectralSample Sample(double start, double end, params double[] values) =>
            new(start, end, values);

        private static CcssWriter.SpectralSet MakeSet() => new(
            White: Sample(380, 730, 0.31, 0.75, 0.46, 0.36, 0.45, 0.31),
            Red:   Sample(380, 730, 0.00, 0.00, 0.01, 0.05, 0.40, 0.30),
            Green: Sample(380, 730, 0.01, 0.05, 0.40, 0.30, 0.05, 0.01),
            Blue:  Sample(380, 730, 0.30, 0.70, 0.05, 0.01, 0.00, 0.00));

        // ---- CcssSpectra extraction -----------------------------------------------------------

        [Fact]
        public void TryParseSpectra_RoundTripsWriterOutput()
        {
            var set = MakeSet();
            string content = CcssWriter.Build("Panel X", "i1 Pro 3", set);

            var spectra = CcssMelanopicEstimator.TryParseSpectra(content, "test.ccss");

            Assert.NotNull(spectra);
            Assert.Equal(new double[] { 380, 450, 520, 590, 660, 730 }, spectra!.Wavelengths);
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(set.Red.Values[i], spectra.Red[i], 6);
                Assert.Equal(set.Green.Values[i], spectra.Green[i], 6);
                Assert.Equal(set.Blue.Values[i], spectra.Blue[i], 6);
                Assert.Equal(set.White.Values[i], spectra.White[i], 6);
            }
            // The toy white is exactly R+G+B, so the additivity residual is ~0.
            Assert.True(spectra.WhiteResidualFraction < 0.01,
                $"residual {spectra.WhiteResidualFraction:F4} should be ~0 for an additive white");
        }

        [Fact]
        public void TryParseSpectra_NonAdditiveWhite_ReportsResidual()
        {
            // A white row with extra broadband power (RGBW-style) must surface a residual.
            var set = new CcssWriter.SpectralSet(
                White: Sample(380, 730, 0.80, 1.20, 0.90, 0.85, 0.95, 0.80),
                Red: MakeSet().Red, Green: MakeSet().Green, Blue: MakeSet().Blue);
            string content = CcssWriter.Build("RGBW Panel", "i1 Pro 3", set);

            var spectra = CcssMelanopicEstimator.TryParseSpectra(content);

            Assert.NotNull(spectra);
            Assert.True(spectra!.WhiteResidualFraction > 0.05,
                $"residual {spectra.WhiteResidualFraction:F4} should flag the non-additive white");
        }

        [Fact]
        public void TryLoadSpectra_ReadsFileAndCaches()
        {
            string path = Path.Combine(Path.GetTempPath(), $"gloam-mel-{Guid.NewGuid():N}.ccss");
            try
            {
                File.WriteAllText(path, CcssWriter.Build("Panel X", "i1 Pro 3", MakeSet()));
                var first = CcssMelanopicEstimator.TryLoadSpectra(path);
                var second = CcssMelanopicEstimator.TryLoadSpectra(path);
                Assert.NotNull(first);
                Assert.Same(first, second); // mtime/length cache hit
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        // ---- mel-EDI / reduction math ----------------------------------------------------------

        /// <summary>D65-ish broadband SPD on a 5nm grid — melDER should be ≈ 1.</summary>
        [Fact]
        public void MelanopicDer_FlatDaylightSpd_NearOne()
        {
            // Equal-energy is not exactly D65, but its melDER is within a few percent of 1;
            // this pins the constant stack (Km, Kmel, D65 ELR) rather than spectral shape.
            var wavelengths = Enumerable.Range(0, 81).Select(i => 380.0 + i * 5.0).ToArray();
            var flat = Enumerable.Repeat(1.0, 81).ToArray();
            double der = CcssMelanopicEstimator.MelanopicDer(wavelengths, flat);
            Assert.InRange(der, 0.85, 1.15);
        }

        [Fact]
        public void Compute_IdentityGains_ZeroReduction()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var reading = MelanopicCalculator.Compute(spectra, (1.0, 1.0, 1.0), 200.0);

            Assert.Equal(0.0, reading.ReductionFraction, 9);
            Assert.Equal(1.0, reading.RelativePhotopicLuminance, 9);
            Assert.Equal(200.0, reading.ScreenLuminanceNits, 6);
        }

        [Fact]
        public void Compute_WarmGains_PositiveReduction_AndLowerMelDer()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var warm = ColorAdjustments.GetPerceptualMultipliers(2700, 0.8, NightBasis.Srgb);
            var reading = MelanopicCalculator.Compute(spectra, warm, 200.0);

            Assert.True(reading.ReductionFraction > 0.2,
                $"2700K should cut melanopic output substantially, got {reading.ReductionFraction:P1}");
            Assert.True(reading.MelDerState < reading.MelDerBaseline,
                "warmer white must have lower melDER than the panel's native white");
        }

        [Fact]
        public void Compute_ReductionIsGeometryAndBrightnessInvariant()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var warm = ColorAdjustments.GetAccurateMultipliers(3000, NightBasis.Srgb);

            var a = MelanopicCalculator.Compute(spectra, warm, 200.0, viewingSolidAngleSr: 0.2);
            var b = MelanopicCalculator.Compute(spectra, warm, 87.5, viewingSolidAngleSr: 0.9);

            Assert.Equal(a.ReductionFraction, b.ReductionFraction, 12);
            Assert.NotEqual(a.MelanopicEdiLux, b.MelanopicEdiLux); // EDI is absolute, must move
        }

        [Fact]
        public void Compute_DimmingHalvesEdi_LeavesReductionUnchanged()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var warm = ColorAdjustments.GetAccurateMultipliers(3000, NightBasis.Srgb);

            var full = MelanopicCalculator.Compute(spectra, warm, 200.0);
            var dimmed = MelanopicCalculator.Compute(spectra, warm, 100.0);

            Assert.Equal(full.MelanopicEdiLux / 2.0, dimmed.MelanopicEdiLux, 9);
            Assert.Equal(full.ReductionFraction, dimmed.ReductionFraction, 12);
        }

        [Fact]
        public void Compute_EdiScalesLinearlyWithSolidAngle()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var a = MelanopicCalculator.Compute(spectra, (1, 1, 1), 200.0, 0.1);
            var b = MelanopicCalculator.Compute(spectra, (1, 1, 1), 200.0, 0.3);
            Assert.Equal(a.MelanopicEdiLux * 3.0, b.MelanopicEdiLux, 9);
        }

        // ---- Uncertainty split -------------------------------------------------------------------

        [Fact]
        public void CombineMelanopic_ProvenanceOrdering_HoldsInBothBands()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var warm = ColorAdjustments.GetAccurateMultipliers(3000, NightBasis.Srgb);
            var reading = MelanopicCalculator.Compute(spectra, warm, 200.0);

            var user = UncertaintyBudget.CombineMelanopic(reading, UncertaintyBudget.CcssProvenance.UserCapturedThisPanel, 0);
            var db = UncertaintyBudget.CombineMelanopic(reading, UncertaintyBudget.CcssProvenance.DbMatchedSameModel, 0);
            var generic = UncertaintyBudget.CombineMelanopic(reading, UncertaintyBudget.CcssProvenance.GenericOrOtherPanel, 0);

            Assert.True(user.ReductionExpandedU < db.ReductionExpandedU);
            Assert.True(db.ReductionExpandedU < generic.ReductionExpandedU);
            Assert.True(user.EdiExpandedU < db.EdiExpandedU);
            Assert.True(db.EdiExpandedU < generic.EdiExpandedU);
        }

        [Fact]
        public void CombineMelanopic_GeometryTermInflatesOnlyTheEdiBand()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var reading = MelanopicCalculator.Compute(
                spectra, ColorAdjustments.GetAccurateMultipliers(3000, NightBasis.Srgb), 200.0);

            var u = UncertaintyBudget.CombineMelanopic(
                reading, UncertaintyBudget.CcssProvenance.UserCapturedThisPanel, 0);

            // Relative EDI band must exceed the geometry floor; relative reduction band must
            // sit well under it (no geometry inside).
            double ediRel = u.EdiExpandedU / Math.Abs(u.EdiValue);
            double redRel = u.ReductionExpandedU / Math.Max(Math.Abs(1.0 - u.ReductionValue), 1e-9);
            Assert.True(ediRel > UncertaintyBudget.ViewingGeometryRelStdU * UncertaintyBudget.CoverageFactorK * 0.99);
            Assert.True(redRel < UncertaintyBudget.ViewingGeometryRelStdU,
                $"reduction band {redRel:P1} should not carry the geometry term");
        }

        [Fact]
        public void CombineMelanopic_WhiteResidualWidensBothBands()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var reading = MelanopicCalculator.Compute(
                spectra, ColorAdjustments.GetAccurateMultipliers(3000, NightBasis.Srgb), 200.0);

            var clean = UncertaintyBudget.CombineMelanopic(reading, UncertaintyBudget.CcssProvenance.DbMatchedSameModel, 0.0);
            var rgbw = UncertaintyBudget.CombineMelanopic(reading, UncertaintyBudget.CcssProvenance.DbMatchedSameModel, 0.2);

            Assert.True(rgbw.ReductionExpandedU > clean.ReductionExpandedU);
            Assert.True(rgbw.EdiExpandedU > clean.EdiExpandedU);
        }

        // ---- Generic fallback ---------------------------------------------------------------------

        [Fact]
        public void GenericPrimaries_ProduceUsableSpectra()
        {
            foreach (bool wide in new[] { false, true })
            {
                var spectra = MelanopicCalculator.GenericPrimaries(wide);
                Assert.Equal(81, spectra.Wavelengths.Count);
                Assert.True(CcssMelanopicEstimator.Photopic(spectra.Wavelengths, spectra.White) > 0);
                double der = CcssMelanopicEstimator.MelanopicDer(spectra.Wavelengths, spectra.White);
                Assert.InRange(der, 0.4, 1.6); // plausible display white, not garbage
            }
        }
    }

    public class MelanopicDoseStoreTests : IDisposable
    {
        private readonly string _dir;

        public MelanopicDoseStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), $"gloam-dose-test-{Guid.NewGuid():N}");
            MelanopicDoseStore.DirectoryOverride = _dir;
        }

        public void Dispose()
        {
            MelanopicDoseStore.DirectoryOverride = null;
            try { Directory.Delete(_dir, recursive: true); } catch { }
            GC.SuppressFinalize(this);
        }

        private static MelanopicDoseSample At(DateTime utc, double edi, string monitor = "mon-1") => new()
        {
            TimestampUtc = utc,
            MonitorDevicePath = monitor,
            MelanopicEdiLux = edi,
            ReductionFraction = 0.5,
            Kelvin = 2700,
        };

        [Fact]
        public void AppendLoadSince_RoundTripsAndFilters()
        {
            var now = DateTime.UtcNow;
            MelanopicDoseStore.Append(At(now.AddHours(-3), 20));
            MelanopicDoseStore.Append(At(now.AddHours(-1), 10));

            var since = MelanopicDoseStore.LoadSince(now.AddHours(-2).ToLocalTime());

            var sample = Assert.Single(since);
            Assert.Equal(10, sample.MelanopicEdiLux);
        }

        [Fact]
        public void IntegrateDose_PiecewiseConstant_MatchesAnalytic()
        {
            var t0 = DateTime.UtcNow.Date.AddHours(20);
            // 10 mel-lx for 2 hours then 20 mel-lx for 1 hour (trapezoid: 10·2? no — trapezoid
            // between 10→10 over 2h = 20, then 10→20 over 1h = 15... build constant segments
            // with duplicate samples at the step for an exact expectation of 20 + 20 = 40).
            var samples = new[]
            {
                At(t0, 10), At(t0.AddHours(2), 10),
                At(t0.AddHours(2).AddSeconds(1), 20), At(t0.AddHours(3), 20),
            };

            double dose = MelanopicDoseStore.IntegrateDose(samples);

            Assert.Equal(10 * 2 + 20 * 1, dose, 1);
        }

        [Fact]
        public void IntegrateDose_GapsAreNotIntegrated()
        {
            var t0 = DateTime.UtcNow.Date.AddHours(18);
            var samples = new[] { At(t0, 50), At(t0.AddHours(8), 50) }; // 8h gap = screen off
            Assert.Equal(0.0, MelanopicDoseStore.IntegrateDose(samples));
        }

        [Fact]
        public void IntegrateDose_EmptyOrSingle_IsZero()
        {
            Assert.Equal(0.0, MelanopicDoseStore.IntegrateDose(Array.Empty<MelanopicDoseSample>()));
            Assert.Equal(0.0, MelanopicDoseStore.IntegrateDose(new[] { At(DateTime.UtcNow, 5) }));
        }

        [Fact]
        public void LoadSince_TornLine_Skipped()
        {
            var now = DateTime.UtcNow;
            MelanopicDoseStore.Append(At(now, 7));
            string dayFile = Directory.EnumerateFiles(_dir, "*.jsonl").Single();
            File.AppendAllText(dayFile, "{ torn line\n");
            MelanopicDoseStore.Append(At(now.AddMinutes(1), 8));

            Assert.Equal(2, MelanopicDoseStore.LoadSince(now.AddMinutes(-5).ToLocalTime()).Count);
        }

        [Fact]
        public void RotateRetention_DeletesOldDayFiles()
        {
            Directory.CreateDirectory(_dir);
            string old = Path.Combine(_dir, "2020-01-01.jsonl");
            File.WriteAllText(old, "{}\n");
            string fresh = Path.Combine(_dir, $"{DateTime.Now:yyyy-MM-dd}.jsonl");
            File.WriteAllText(fresh, "{}\n");

            MelanopicDoseStore.RotateRetention(90);

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
        }
    }
}
