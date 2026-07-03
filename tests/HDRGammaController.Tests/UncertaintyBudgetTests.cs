using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class UncertaintyBudgetTests
    {
        private const double D2For3 = 1.693; // range→sigma factor for n=3

        #region Quadrature and coverage

        [Fact]
        public void Rss_CombinesInQuadrature()
        {
            Assert.Equal(5.0, UncertaintyBudget.Rss(3.0, 4.0), 9);
            Assert.Equal(0.0, UncertaintyBudget.Rss(), 9);
            Assert.Equal(2.0, UncertaintyBudget.Rss(2.0, double.NaN), 9); // non-finite terms ignored
        }

        [Fact]
        public void ZeroNoise_IntervalCollapsesToInstrumentFloor()
        {
            var patches = new List<UncertaintyBudget.PatchTerm>
            {
                new("a", 100.0, 0.0, 0.0),
                new("b", 1.0, 0.0, 0.0),
            };

            var result = UncertaintyBudget.Combine(
                patches, UncertaintyBudget.InstrumentClass.ColorimeterWithCorrection,
                peakWhiteDriftFraction: null, driftCompensated: false);

            Assert.Equal(0.0, result.RepeatabilityStdU, 9);
            Assert.Equal(0.0, result.DriftStdU, 9);
            Assert.Equal(UncertaintyBudget.ColorimeterWithCorrectionStdU, result.CombinedStdU, 9);
            Assert.Equal(2.0 * UncertaintyBudget.ColorimeterWithCorrectionStdU, result.ExpandedU, 9);
        }

        [Fact]
        public void ExpandedUncertainty_UsesCoverageFactorTwo()
        {
            var patches = new List<UncertaintyBudget.PatchTerm> { new("a", 50.0, 0.2, 0.3) };
            var result = UncertaintyBudget.Combine(
                patches, UncertaintyBudget.InstrumentClass.Spectrometer, 0.02, driftCompensated: true);

            Assert.Equal(2.0, result.CoverageFactor, 9);
            Assert.Equal(result.CombinedStdU * 2.0, result.ExpandedU, 9);
        }

        [Fact]
        public void Combine_AveragesIndependentPatchNoiseAndAddsSystematicTermsOnce()
        {
            var patches = new List<UncertaintyBudget.PatchTerm>
            {
                new("a", 10.0, 0.1, 0.3),
                new("b", 20.0, 0.1, 0.4),
            };

            var result = UncertaintyBudget.Combine(
                patches, UncertaintyBudget.InstrumentClass.ColorimeterGeneric, 0.04, driftCompensated: true);

            double expectedRepeat = Math.Sqrt(0.3 * 0.3 + 0.4 * 0.4) / 2.0; // √(Σu²)/N
            double expectedDrift = UncertaintyBudget.LStarPerRelativeLuminance *
                                   (UncertaintyBudget.DriftResidualFraction * 0.04) / Math.Sqrt(3.0);
            double expectedCombined = Math.Sqrt(
                expectedRepeat * expectedRepeat +
                UncertaintyBudget.ColorimeterGenericStdU * UncertaintyBudget.ColorimeterGenericStdU +
                expectedDrift * expectedDrift);

            Assert.Equal(expectedRepeat, result.RepeatabilityStdU, 9);
            Assert.Equal(UncertaintyBudget.ColorimeterGenericStdU, result.InstrumentStdU, 9);
            Assert.Equal(expectedDrift, result.DriftStdU, 9);
            Assert.Equal(expectedCombined, result.CombinedStdU, 9);
        }

        [Fact]
        public void PerPatchLuminance_ExpandedAtCoverageFactor()
        {
            var patches = new List<UncertaintyBudget.PatchTerm> { new("dark", 0.2, 0.05, 0.1) };
            var result = UncertaintyBudget.Combine(
                patches, UncertaintyBudget.InstrumentClass.Spectrometer, null, false);

            var lum = Assert.Single(result.PerPatchLuminance);
            Assert.Equal("dark", lum.Name);
            Assert.Equal(0.05, lum.YStdU, 9);
            Assert.Equal(0.10, lum.ExpandedYU, 9); // k=2 × std-u
        }

        #endregion

        #region Repeatability term

        [Fact]
        public void MultiRead_StandardErrorOfMedianFormula()
        {
            // spread = d₂(3)·σ with σ = 1 → SE(median) = 1.2533·1/√3.
            double stdU = UncertaintyBudget.RepeatabilityYStdU(
                measuredY: 100.0, readingCount: 3, readingSpreadY: D2For3, noiseModel: null);

            Assert.Equal(UncertaintyBudget.MedianEfficiencyFactor / Math.Sqrt(3.0), stdU, 3);
        }

        [Fact]
        public void MultiRead_ZeroSpread_IsZero()
        {
            Assert.Equal(0.0, UncertaintyBudget.RepeatabilityYStdU(100.0, 3, 0.0, null), 9);
        }

        [Fact]
        public void SingleRead_InheritsLuminanceBinEstimate()
        {
            var model = new LuminanceNoiseModel();
            model.Record(meanY: 0.5, spreadY: 0.05); // bin 0.1–1: relative spread 10%

            double stdU = UncertaintyBudget.RepeatabilityYStdU(0.4, 1, null, model);

            // σ_rel = rel / d₂(3), scaled by the patch's own Y.
            Assert.Equal(0.1 / D2For3 * 0.4, stdU, 6);
        }

        [Fact]
        public void SingleRead_EmptyModel_IsZero()
        {
            Assert.Equal(0.0, UncertaintyBudget.RepeatabilityYStdU(0.4, 1, null, new LuminanceNoiseModel()), 9);
            Assert.Equal(0.0, UncertaintyBudget.RepeatabilityYStdU(0.4, 1, null, null), 9);
        }

        [Fact]
        public void SingleRead_UnpopulatedBin_FallsBackToPopulatedAverage()
        {
            var model = new LuminanceNoiseModel();
            model.Record(0.05, 0.01); // dark bin only: rel = 0.01/0.05 = 20%

            double stdU = UncertaintyBudget.RepeatabilityYStdU(50.0, 1, null, model); // bright bin has no data

            Assert.Equal(0.2 / D2For3 * 50.0, stdU, 6);
        }

        #endregion

        #region Instrument term

        [Fact]
        public void InstrumentTable_EngineeringEstimates()
        {
            Assert.Equal(0.5, UncertaintyBudget.InstrumentTermStdU(UncertaintyBudget.InstrumentClass.ColorimeterWithCorrection), 9);
            Assert.Equal(1.5, UncertaintyBudget.InstrumentTermStdU(UncertaintyBudget.InstrumentClass.ColorimeterGeneric), 9);
            Assert.Equal(0.3, UncertaintyBudget.InstrumentTermStdU(UncertaintyBudget.InstrumentClass.Spectrometer), 9);
        }

        [Theory]
        [InlineData("i1 Display Pro", true, UncertaintyBudget.InstrumentClass.ColorimeterWithCorrection)]
        [InlineData("i1 Display Pro", false, UncertaintyBudget.InstrumentClass.ColorimeterGeneric)]
        [InlineData("SpyderX", false, UncertaintyBudget.InstrumentClass.ColorimeterGeneric)]
        [InlineData("i1 Pro 2", false, UncertaintyBudget.InstrumentClass.Spectrometer)]
        [InlineData("ColorMunki Photo", true, UncertaintyBudget.InstrumentClass.Spectrometer)]
        [InlineData(null, false, UncertaintyBudget.InstrumentClass.ColorimeterGeneric)]
        public void ClassifyInstrument_ByModelNameAndCorrectionState(
            string? model, bool hasCorrection, UncertaintyBudget.InstrumentClass expected)
        {
            Assert.Equal(expected, UncertaintyBudget.ClassifyInstrument(model, hasCorrection));
        }

        #endregion

        #region Drift residual

        [Fact]
        public void DriftResidual_CompensatedKeepsOnlyResidualFraction()
        {
            double compensated = UncertaintyBudget.DriftResidualStdU(0.04, driftCompensated: true);
            double uncompensated = UncertaintyBudget.DriftResidualStdU(0.04, driftCompensated: false);

            Assert.Equal(
                UncertaintyBudget.LStarPerRelativeLuminance * 0.25 * 0.04 / Math.Sqrt(3.0), compensated, 9);
            Assert.Equal(uncompensated * UncertaintyBudget.DriftResidualFraction, compensated, 9);
            Assert.Equal(0.0, UncertaintyBudget.DriftResidualStdU(null, true), 9);
            Assert.Equal(0.0, UncertaintyBudget.DriftResidualStdU(0.0, true), 9);
        }

        #endregion

        #region Luminance noise model

        [Fact]
        public void NoiseModel_BinsByLuminanceDecade()
        {
            Assert.Equal(0, LuminanceNoiseModel.BinIndex(0.05));
            Assert.Equal(1, LuminanceNoiseModel.BinIndex(0.5));
            Assert.Equal(2, LuminanceNoiseModel.BinIndex(5.0));
            Assert.Equal(3, LuminanceNoiseModel.BinIndex(50.0));
            Assert.Equal(0, LuminanceNoiseModel.BinIndex(double.NaN)); // defensive
        }

        [Fact]
        public void NoiseModel_HysteresisBetweenNoisyAndQuiet()
        {
            var model = new LuminanceNoiseModel();

            model.Record(0.5, 0.025); // rel 5% > 2% → noisy
            Assert.True(model.IsNoisy(0.5));
            Assert.False(model.IsNoisy(50.0)); // other bins untouched

            // In the hysteresis band (0.5%–2%) the flag holds its previous state.
            model.Record(0.5, 0.005); // EWMA: 0.05 + 0.3·(0.01−0.05) = 0.038 → still noisy
            Assert.True(model.IsNoisy(0.5));

            // Repeated quiet bursts pull the EWMA below the quiet threshold.
            for (int i = 0; i < 20; i++)
                model.Record(0.5, 0.0005); // rel 0.1%
            Assert.False(model.IsNoisy(0.5));
        }

        [Fact]
        public void NoiseModel_EwmaUpdatesCorrectly()
        {
            var model = new LuminanceNoiseModel();
            model.Record(0.5, 0.05); // rel 10%; first sample seeds the EWMA directly
            Assert.Equal(0.10, model.RelativeSpreadEstimate(0.5)!.Value, 9);

            model.Record(0.5, 0.10); // rel 20% → 0.10 + 0.3·(0.20−0.10) = 0.13
            Assert.Equal(0.13, model.RelativeSpreadEstimate(0.5)!.Value, 9);
        }

        [Fact]
        public void NoiseModel_FromMeasurements_UsesOnlyMultiReadBursts()
        {
            var patch = new ColorPatch { Name = "p", DisplayRgb = new LinearRgb(0.1, 0.1, 0.1) };
            var measurements = new List<MeasurementResult>
            {
                new() { Patch = patch, Xyz = new CieXyz(0.4, 0.5, 0.6), ReadingCount = 3, ReadingSpreadY = 0.05 },
                new() { Patch = patch, Xyz = new CieXyz(40, 50, 60), ReadingCount = 1 }, // single read: no spread info
            };

            var model = LuminanceNoiseModel.FromMeasurements(measurements);

            Assert.Equal(0.10, model.RelativeSpreadEstimate(0.5)!.Value, 9);
            Assert.True(model.IsNoisy(0.5));
            Assert.False(model.IsNoisy(50.0)); // bright bin fell back to nothing recorded → flag untouched
        }

        #endregion

        #region Verifier integration

        private static MeasurementResult Gray(double signal, double measuredY,
            int readingCount = 1, double? spreadY = null, string? name = null)
        {
            var patch = new ColorPatch
            {
                Name = name ?? $"Gray {signal:P0}",
                DisplayRgb = new LinearRgb(signal, signal, signal),
                Category = PatchCategory.Grayscale,
            };
            return new MeasurementResult
            {
                Patch = patch,
                Xyz = new CieXyz(measuredY * 0.95047, measuredY, measuredY * 1.08883),
                IsValid = true,
                ReadingCount = readingCount,
                ReadingSpreadY = spreadY,
            };
        }

        private static UncertaintyBudget.Result? ComputeBudget(List<MeasurementResult> measurements)
        {
            var context = new UncertaintyBudget.Context(
                UncertaintyBudget.InstrumentClass.Spectrometer,
                LuminanceNoiseModel.FromMeasurements(measurements),
                PeakWhiteDriftFraction: null,
                DriftCompensated: false);
            CalibrationVerifier.ComputeMetrics(
                measurements, StandardTargets.SrgbGamma22, context, out var uncertainty);
            return uncertainty;
        }

        [Fact]
        public void HigherDarkNoise_WidensInterval()
        {
            // Same measurements except the dark patch's recorded read spread. The
            // measured dark Y deliberately misses its target so the ΔE sensitivity has a
            // slope for the two-point evaluation to see.
            List<MeasurementResult> Run(double darkSpread) => new()
            {
                Gray(1.0, 100.0, readingCount: 3, spreadY: 0.0, name: "White"),
                Gray(0.10, 1.0, readingCount: 3, spreadY: darkSpread, name: "Dark"), // target ≈ 0.63 cd/m²
                Gray(0.50, 22.0, name: "Mid"),
            };

            var quiet = ComputeBudget(Run(0.002));
            var noisy = ComputeBudget(Run(0.20));

            Assert.NotNull(quiet);
            Assert.NotNull(noisy);
            Assert.True(noisy!.ExpandedU > quiet!.ExpandedU,
                $"noisy {noisy.ExpandedU} should exceed quiet {quiet.ExpandedU}");

            double quietDarkYU = quiet.PerPatchLuminance.Single(p => p.Name == "Dark").YStdU;
            double noisyDarkYU = noisy.PerPatchLuminance.Single(p => p.Name == "Dark").YStdU;
            Assert.True(noisyDarkYU > quietDarkYU);
        }

        [Fact]
        public void SingleReadPatch_InheritsBinEstimateThroughVerifier()
        {
            var measurements = new List<MeasurementResult>
            {
                Gray(1.0, 100.0, readingCount: 3, spreadY: 0.0, name: "White"),
                Gray(0.10, 0.63, readingCount: 3, spreadY: 0.1, name: "DarkBurst"), // seeds the 0.1–1 bin
                Gray(0.12, 0.90, name: "DarkSingle"), // single read, same decade
                Gray(0.50, 21.8, name: "MidSingle"),  // single read, decade 10–? (21.8 → bright bin)
            };

            var budget = ComputeBudget(measurements);

            Assert.NotNull(budget);
            var darkSingle = budget!.PerPatchLuminance.Single(p => p.Name == "DarkSingle");
            Assert.True(darkSingle.YStdU > 0, "single-read patch in a measured-noisy decade must inherit a nonzero estimate");

            // Inherited σ = (rel/d₂(3))·Y with rel = 0.1/0.63.
            double expected = 0.1 / 0.63 / D2For3 * 0.90;
            Assert.Equal(expected, darkSingle.YStdU, 3);
        }

        [Fact]
        public void ZeroNoiseRun_VerifierIntervalIsInstrumentFloor()
        {
            var measurements = new List<MeasurementResult>
            {
                Gray(1.0, 100.0, readingCount: 3, spreadY: 0.0, name: "White"),
                Gray(0.50, 21.8, name: "Mid"),
            };

            var budget = ComputeBudget(measurements);

            Assert.NotNull(budget);
            Assert.Equal(0.0, budget!.RepeatabilityStdU, 9);
            Assert.Equal(2.0 * UncertaintyBudget.SpectrometerStdU, budget.ExpandedU, 9);
        }

        [Fact]
        public void NullContext_ProducesNoBudgetAndIdenticalMetrics()
        {
            var measurements = new List<MeasurementResult>
            {
                Gray(1.0, 100.0, name: "White"),
                Gray(0.50, 21.8, name: "Mid"),
            };

            var plain = CalibrationVerifier.ComputeMetrics(measurements, StandardTargets.SrgbGamma22);
            var viaOverload = CalibrationVerifier.ComputeMetrics(
                measurements, StandardTargets.SrgbGamma22, null, out var uncertainty);

            Assert.Null(uncertainty);
            Assert.Equal(plain.AverageDeltaE, viaOverload.AverageDeltaE, 12);
            Assert.Equal(plain.MaxDeltaE, viaOverload.MaxDeltaE, 12);
        }

        #endregion

        #region Serialization

        [Fact]
        public void MeasurementReadingFields_JsonRoundTrip()
        {
            var m = new MeasurementResult
            {
                Patch = new ColorPatch { Name = "rt", DisplayRgb = new LinearRgb(0.1, 0.1, 0.1) },
                Xyz = new CieXyz(1.0, 2.0, 3.0),
                ReadingCount = 4,
                ReadingSpreadY = 0.125,
            };

            string json = JsonSerializer.Serialize(m);
            var back = JsonSerializer.Deserialize<MeasurementResult>(json);

            Assert.NotNull(back);
            Assert.Equal(4, back!.ReadingCount);
            Assert.Equal(0.125, back.ReadingSpreadY!.Value, 9);

            // Default (single-read) fields also survive.
            var single = new MeasurementResult
            {
                Patch = new ColorPatch { Name = "s", DisplayRgb = new LinearRgb(0, 0, 0) },
                Xyz = new CieXyz(0, 0, 0),
            };
            var singleBack = JsonSerializer.Deserialize<MeasurementResult>(JsonSerializer.Serialize(single));
            Assert.Equal(1, singleBack!.ReadingCount);
            Assert.Null(singleBack.ReadingSpreadY);
        }

        #endregion
    }
}
