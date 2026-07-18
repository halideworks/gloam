using System;
using System.Collections.Generic;
using System.IO;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationProfilePersistenceTests
    {
        [Fact]
        public void SaveToFile_SanitizesNonFiniteMetricsAndDerivedCharacteristics()
        {
            string path = Path.Combine(Path.GetTempPath(), $"gloam-profile-{Guid.NewGuid():N}.json");
            try
            {
                var profile = new CalibrationProfile
                {
                    MonitorDevicePath = @"\\.\DISPLAY1",
                    MonitorName = "Test Panel",
                    Target = StandardTargets.SrgbGamma22,
                    PreCalibrationDeltaE = double.NaN,
                    PostCalibrationDeltaE = double.PositiveInfinity,
                    MeasuredCharacteristics = new DisplayCharacteristics
                    {
                        MeasuredRed = new Chromaticity(double.NaN, 0.33),
                        MeasuredGreen = new Chromaticity(0.30, double.PositiveInfinity),
                        MeasuredBlue = new Chromaticity(0.15, 0.06),
                        MeasuredWhite = new Chromaticity(double.NegativeInfinity, 0.32903),
                        MeasuredGamma = double.NaN,
                        PeakLuminance = double.PositiveInfinity,
                        BlackLevel = 0.0
                    },
                    ReportSummary = new CalibrationReportSummary
                    {
                        AvgDeltaE = double.NaN,
                        MaxDeltaE = 2.0,
                        DetailedHistogram = new[] { 3, -1, 2 },
                        DetailedPatches = new List<VerifiedPatchResult>
                        {
                            new VerifiedPatchResult { Name = "bad", DeltaE = double.PositiveInfinity },
                            new VerifiedPatchResult { Name = "ok", Category = "Grayscale", DeltaE = 1.25 }
                        },
                        DetailedGrayscaleDeltaE = double.NegativeInfinity,
                        ProofCertificate = new Mhc2ProofCertificate
                        {
                            CorrectabilityFraction = double.PositiveInfinity,
                            OrderConflictRed = double.PositiveInfinity,
                            ContinuousEnvelopeGapDeltaE = double.NaN,
                            MatrixQuantizationMaxAbs = double.NegativeInfinity,
                            Compiled = new Mhc2ErrorStatistics
                            {
                                P95DeltaE = 1.1, MaxDeltaE = double.NaN,
                                MaxNeutralChroma = double.PositiveInfinity,
                            },
                            Counterexamples = new List<Mhc2Counterexample>
                            {
                                new() { Name = "proof", R = 0.25, G = double.NaN, B = 0.75,
                                    PredictedDeltaE = 1.0, EmpiricalUpperEstimate = 1.8 },
                            },
                            PhysicalObservations = new List<Mhc2PhysicalObservation>
                            {
                                new(0.2, 0.3, 0.4, double.NaN, 0.2, 0.3),
                                new(0.4, 0.5, 0.6, 0.1, 0.2, 0.3),
                            },
                        }
                    }
                };

                profile.SaveToFile(path);
                string json = File.ReadAllText(path);

                Assert.DoesNotContain("NaN", json);
                Assert.DoesNotContain("Infinity", json);
                Assert.DoesNotContain("ContrastRatio", json);

                var loaded = CalibrationProfile.LoadFromFile(path);

                Assert.Null(loaded.PreCalibrationDeltaE);
                Assert.Null(loaded.PostCalibrationDeltaE);
                Assert.Equal(Chromaticity.Rec709Red.X, loaded.MeasuredCharacteristics!.MeasuredRed.X);
                Assert.Equal(100.0, loaded.MeasuredCharacteristics.PeakLuminance);
                Assert.Equal(0.0, loaded.MeasuredCharacteristics.BlackLevel);
                Assert.Null(loaded.ReportSummary!.AvgDeltaE);
                Assert.Equal(2.0, loaded.ReportSummary.MaxDeltaE);
                Assert.Equal(new[] { 3, 0, 2 }, loaded.ReportSummary.DetailedHistogram);
                var patch = Assert.Single(loaded.ReportSummary.DetailedPatches!);
                Assert.Equal("ok", patch.Name);
                Assert.Equal(1.25, patch.DeltaE);
                Assert.NotNull(loaded.ReportSummary.ProofCertificate);
                Assert.Equal(0, loaded.ReportSummary.ProofCertificate!.CorrectabilityFraction);
                Assert.Equal(1.1, loaded.ReportSummary.ProofCertificate.Compiled.P95DeltaE);
                Assert.Equal(0, loaded.ReportSummary.ProofCertificate.Compiled.MaxDeltaE);
                Assert.Equal(0, loaded.ReportSummary.ProofCertificate.Compiled.MaxNeutralChroma);
                Assert.Equal(0, loaded.ReportSummary.ProofCertificate.OrderConflictRed);
                Assert.Equal(0, loaded.ReportSummary.ProofCertificate.ContinuousEnvelopeGapDeltaE);
                Assert.Equal(0, Assert.Single(loaded.ReportSummary.ProofCertificate.Counterexamples).G);
                Assert.Single(loaded.ReportSummary.ProofCertificate.PhysicalObservations);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Fact]
        public void SaveToFile_SanitizesChromaticitiesOutsidePhysicalPlane()
        {
            string path = Path.Combine(Path.GetTempPath(), $"gloam-profile-{Guid.NewGuid():N}.json");
            try
            {
                var profile = new CalibrationProfile
                {
                    MonitorDevicePath = @"\\.\DISPLAY1",
                    MonitorName = "Test Panel",
                    Target = StandardTargets.SrgbGamma22,
                    MeasuredCharacteristics = new DisplayCharacteristics
                    {
                        MeasuredRed = new Chromaticity(0.70, 0.35),
                        MeasuredGreen = Chromaticity.Rec709Green,
                        MeasuredBlue = Chromaticity.Rec709Blue,
                        MeasuredWhite = new Chromaticity(0.70, 0.35),
                        MeasuredGamma = 2.2,
                        PeakLuminance = 120.0,
                        BlackLevel = 0.05
                    }
                };

                profile.SaveToFile(path);
                var loaded = CalibrationProfile.LoadFromFile(path);

                Assert.Equal(Chromaticity.Rec709Red, loaded.MeasuredCharacteristics!.MeasuredRed);
                Assert.Equal(Chromaticity.D65, loaded.MeasuredCharacteristics.MeasuredWhite);
                var matrix = loaded.MeasuredCharacteristics.RgbToXyzMatrix;
                for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.True(double.IsFinite(matrix[r, c]));
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
