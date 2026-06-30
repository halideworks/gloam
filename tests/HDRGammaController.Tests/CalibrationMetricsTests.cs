using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Regression coverage for the report's headline ΔE figure (Lut3DGenerator.CalculateMetrics).
    /// The colorimeter reports ABSOLUTE luminance (white Y ≈ 100–120 cd/m²) while patch targets
    /// are normalized to white Y = 1. Taking Lab on raw absolute XYZ pushes white to L* ≈ 559 and
    /// makes ΔE2000 explode into the dozens/hundreds — the "ΔE 90+" the user reported. These tests
    /// pin the normalization so a perfect (just-scaled) measurement reads ~0.
    /// </summary>
    public class CalibrationMetricsTests
    {
        private static List<MeasurementResult> BuildGrayscale(CalibrationTarget target, double whiteNits, double extraBlueGain = 1.0)
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i < 16; i++)
            {
                double lvl = i / 15.0;
                var signal = new LinearRgb(lvl, lvl, lvl);

                double lin = target.ApplyEotf(lvl);
                var txyz = target.LinearRgbToXyz(new LinearRgb(lin, lin, lin));

                var patch = new ColorPatch
                {
                    Name = $"Gray {lvl * 100:F0}%",
                    DisplayRgb = signal,
                    Category = PatchCategory.Grayscale,
                    Index = i,
                    TargetXyz = txyz,
                    TargetLab = ColorMath.XyzToLab(txyz)
                };

                // Measured = target reproduced perfectly, but on the ABSOLUTE colorimeter scale
                // (×whiteNits). extraBlueGain lets a test inject a real white-point error.
                var measured = new CieXyz(txyz.X * whiteNits, txyz.Y * whiteNits, txyz.Z * whiteNits * extraBlueGain);
                list.Add(new MeasurementResult { Patch = patch, Xyz = measured });
            }
            return list;
        }

        [Fact]
        public void CalculateMetrics_PerfectMatchOnAbsoluteScale_IsNearZero()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            // A perfect reproduction (only differing by an absolute luminance scale) must read ~0,
            // NOT the ~90 the un-normalized code produced.
            Assert.True(metrics.AverageDeltaE < 1.0,
                $"perfect match should be ~0 after normalization, got ΔE={metrics.AverageDeltaE:F1}");
            Assert.True(metrics.MaxDeltaE < 2.0, $"max ΔE should be tiny, got {metrics.MaxDeltaE:F1}");
        }

        [Fact]
        public void CalculateMetrics_RealWhitePointError_IsModerateNotHundreds()
        {
            // A genuinely-off white point (15% extra blue) should surface as a believable
            // single/low-double-digit ΔE, not a luminance-blowout in the dozens/hundreds.
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0, extraBlueGain: 1.15);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            Assert.InRange(metrics.AverageDeltaE, 1.0, 25.0);
        }

        [Fact]
        public void GrayscaleDecomposition_BlueCast_IsChromaticNotTonal()
        {
            // A pure white-point error must land in the CHROMATIC component of the grayscale
            // decomposition, with the tone component near zero (luminance is unchanged).
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0, extraBlueGain: 1.15);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            Assert.True(metrics.AverageGrayscaleColorDeltaE > metrics.AverageGrayscaleToneDeltaE * 2,
                $"blue cast should be chromatic: color={metrics.AverageGrayscaleColorDeltaE:F2} tone={metrics.AverageGrayscaleToneDeltaE:F2}");
            Assert.True(metrics.AverageGrayscaleToneDeltaE < 1.0,
                $"tone component should be tiny for a pure cast, got {metrics.AverageGrayscaleToneDeltaE:F2}");
        }

        [Fact]
        public void DeltaEItp_IdenticalColors_IsZero()
        {
            var xyz = new CieXyz(95.0, 100.0, 108.0);
            Assert.Equal(0.0, CalibrationVerifier.DeltaEItp(xyz, xyz), 9);
        }

        [Fact]
        public void DeltaEItp_BehavesSanely()
        {
            // Luminance-only and chroma-only differences both register; the metric grows
            // with error size; values stay in the JND-scaled range, not thousands.
            var white = new CieXyz(95.0, 100.0, 108.0);
            var dimmer = new CieXyz(85.5, 90.0, 97.2);
            var bluer = new CieXyz(95.0, 100.0, 118.0);
            var muchBluer = new CieXyz(95.0, 100.0, 130.0);

            double lum = CalibrationVerifier.DeltaEItp(white, dimmer);
            double chroma = CalibrationVerifier.DeltaEItp(white, bluer);
            double chromaBig = CalibrationVerifier.DeltaEItp(white, muchBluer);

            Assert.True(lum > 0.1 && double.IsFinite(lum));
            Assert.True(chroma > 0.1 && double.IsFinite(chroma));
            Assert.True(chromaBig > chroma, "larger error must produce larger dE ITP");
            Assert.True(chroma < 200, $"dE ITP implausibly large: {chroma:F1}");
        }

        [Fact]
        public void Metrics_PopulateItpValues()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0, extraBlueGain: 1.10);

            var metrics = new Lut3DGenerator(target, measurements, 9).CalculateMetrics();

            Assert.True(metrics.ItpDeltaEs.Count > 0, "ITP values should be computed");
            Assert.True(metrics.AverageItpDeltaE > 0);
            Assert.True(metrics.MaxItpDeltaE >= metrics.AverageItpDeltaE);
        }

        [Fact]
        public void Metrics_NormalizationPrefersWhitePatchOverBrightestPatch()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 100.0);

            var redPatch = new ColorPatch
            {
                Name = "Overbright red",
                DisplayRgb = new LinearRgb(1, 0, 0),
                Category = PatchCategory.Primary,
                Index = measurements.Count
            };
            // An HDR/display-processing anomaly can make a saturated patch's Y exceed white.
            // That must not become the normalization anchor for the whole report.
            measurements.Add(new MeasurementResult
            {
                Patch = redPatch,
                Xyz = new CieXyz(95, 150, 5),
                IsValid = true
            });

            var metrics = CalibrationVerifier.ComputeMetrics(measurements, target);
            var white = metrics.PatchResults.Single(p => p.Name == "Gray 100%");

            Assert.True(white.DeltaE < 0.5,
                $"white should stay near-perfect when a non-white patch is brightest, got {white.DeltaE:F2}");
        }

        [Fact]
        public void ComputeMetrics_IgnoresNonFiniteMeasurements()
        {
            var target = StandardTargets.SrgbGamma22;
            var grayscale = BuildGrayscale(target, whiteNits: 120.0);
            var measurements = new List<MeasurementResult>
            {
                grayscale[0],
                grayscale[5],
                grayscale[10],
                grayscale[15],
            };
            measurements.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Corrupt gray",
                    DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                    Category = PatchCategory.Grayscale,
                },
                Xyz = new CieXyz(double.NaN, 50, 50),
                IsValid = true
            });

            var metrics = CalibrationVerifier.ComputeMetrics(measurements, target);

            Assert.Equal(4, metrics.PatchResults.Count);
            Assert.DoesNotContain(metrics.PatchResults, p => p.Name == "Corrupt gray");
            Assert.True(double.IsFinite(metrics.AverageDeltaE));
            Assert.True(metrics.AverageDeltaE < 0.5);
        }

        [Fact]
        public void ComputeMetrics_IgnoresNonPhysicalNegativeMeasurements()
        {
            var target = StandardTargets.SrgbGamma22;
            var grayscale = BuildGrayscale(target, whiteNits: 120.0);
            var measurements = new List<MeasurementResult>
            {
                grayscale[0],
                grayscale[5],
                grayscale[10],
                grayscale[15],
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Negative gray",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Category = PatchCategory.Grayscale,
                    },
                    Xyz = new CieXyz(-0.01, 50, 50),
                    IsValid = true
                }
            };

            var metrics = CalibrationVerifier.ComputeMetrics(measurements, target);

            Assert.Equal(4, metrics.PatchResults.Count);
            Assert.DoesNotContain(metrics.PatchResults, p => p.Name == "Negative gray");
            Assert.Equal(0.0, CalibrationVerifier.DeltaEItp(new CieXyz(-0.01, 1.0, 1.0), new CieXyz(1.0, 1.0, 1.0)), 10);
        }

        [Fact]
        public void ReportDeltaEStats_SynthesizesTargetsWhenPatchTargetsAreMissing()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0)
                .Select(m => new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = m.Patch.Name,
                        DisplayRgb = m.Patch.DisplayRgb,
                        Category = m.Patch.Category,
                        Index = m.Patch.Index,
                    },
                    Xyz = m.Xyz,
                    IsValid = true
                })
                .ToList();

            var report = BuildReport(target, measurements);

            Assert.Equal(measurements.Count, report.OverallDeltaE.Count);
            Assert.True(report.OverallDeltaE.Average < 0.5,
                $"perfect report stats should synthesize targets and stay near zero, got {report.OverallDeltaE.Average:F2}");
        }

        [Fact]
        public void ReportDeltaEStats_IgnoreInvalidAndHdrWireRows()
        {
            var target = StandardTargets.SrgbGamma22;
            var grayscale = BuildGrayscale(target, whiteNits: 120.0);
            var measurements = new List<MeasurementResult>
            {
                grayscale[0],
                grayscale[5],
                grayscale[10],
                grayscale[15],
            };
            measurements.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Invalid red",
                    DisplayRgb = new LinearRgb(1, 0, 0),
                    Category = PatchCategory.Primary,
                    TargetXyz = target.LinearRgbToXyz(new LinearRgb(1, 0, 0)),
                },
                Xyz = new CieXyz(1000, 1000, 1000),
                IsValid = false
            });
            measurements.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "HDR wire 1000 nits",
                    DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                    Category = PatchCategory.General,
                    Nits = 1000
                },
                Xyz = new CieXyz(950, 1000, 1080),
                IsValid = true
            });

            var report = BuildReport(target, measurements);

            Assert.Equal(4, report.OverallDeltaE.Count);
            Assert.True(report.OverallDeltaE.Average < 0.5);
        }

        [Fact]
        public void ReportMetrics_IgnoreNonFiniteAccuracyRows()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0).Take(4).ToList();
            measurements.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Corrupt white",
                    DisplayRgb = new LinearRgb(1, 1, 1),
                    Category = PatchCategory.Grayscale,
                    IsCritical = true
                },
                Xyz = new CieXyz(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
                IsValid = true
            });

            var report = BuildReport(target, measurements);

            Assert.Equal(4, report.OverallDeltaE.Count);
            Assert.True(double.IsFinite(report.OverallDeltaE.Average));
            Assert.DoesNotContain(report.ChromaticityPoints, p => p.Name == "Corrupt white");
            Assert.DoesNotContain(report.GrayscaleTracking, p => double.IsInfinity(p.MeasuredLuminance));
            Assert.DoesNotContain(report.Issues, i => i.Message.Contains("Corrupt white", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ReportMetrics_IgnoreNonPhysicalNegativeAccuracyRows()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 120.0).Take(4).ToList();
            measurements.Add(new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Negative gray",
                    DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                    Category = PatchCategory.Grayscale,
                    IsCritical = true
                },
                Xyz = new CieXyz(10, 50, -0.01),
                IsValid = true
            });

            var report = BuildReport(target, measurements);

            Assert.Equal(4, report.OverallDeltaE.Count);
            Assert.DoesNotContain(report.ChromaticityPoints, p => p.Name == "Negative gray");
            Assert.DoesNotContain(report.GrayscaleTracking, p => Math.Abs(p.InputLevel - 0.5) < 1e-9);
            Assert.DoesNotContain(report.Issues, i => i.Message.Contains("Negative gray", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ReportDeltaEStats_PqTargetIgnoresStalePqDecodedUiPatchTargets()
        {
            var target = StandardTargets.Rec709Pq;
            var whiteXyz = target.LinearRgbToXyz(new LinearRgb(1, 1, 1));
            var displayRgb = new LinearRgb(0.76, 0.57, 0.46);
            var correctTarget = target.LinearRgbToXyz(new LinearRgb(
                ColorMath.SrgbEotf(displayRgb.R),
                ColorMath.SrgbEotf(displayRgb.G),
                ColorMath.SrgbEotf(displayRgb.B)));
            var stalePqTarget = target.LinearRgbToXyz(new LinearRgb(
                target.ApplyEotf(displayRgb.R),
                target.ApplyEotf(displayRgb.G),
                target.ApplyEotf(displayRgb.B)));

            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "White",
                        DisplayRgb = new LinearRgb(1, 1, 1),
                        Category = PatchCategory.Grayscale,
                        TargetXyz = whiteXyz,
                        TargetLab = ColorMath.XyzToLab(whiteXyz)
                    },
                    Xyz = new CieXyz(whiteXyz.X * 100, whiteXyz.Y * 100, whiteXyz.Z * 100)
                },
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Skin tone",
                        DisplayRgb = displayRgb,
                        Category = PatchCategory.SkinTone,
                        TargetXyz = stalePqTarget,
                        TargetLab = ColorMath.XyzToLab(stalePqTarget)
                    },
                    Xyz = new CieXyz(correctTarget.X * 100, correctTarget.Y * 100, correctTarget.Z * 100)
                }
            };

            var report = BuildReport(target, measurements);

            Assert.Equal(2, report.OverallDeltaE.Count);
            Assert.True(report.OverallDeltaE.Maximum < 0.1,
                $"PQ desktop UI patches should grade against sRGB content math, got dE {report.OverallDeltaE.Maximum:F2}");
        }

        [Fact]
        public void ReportGrayscaleTracking_PqTargetIgnoresStalePqDecodedUiPatchLuminance()
        {
            var target = StandardTargets.Rec709Pq;
            double signal = 0.5;
            double correctRelativeY = ColorMath.SrgbEotf(signal);
            double staleRelativeY = target.ApplyEotf(signal);
            var whiteXyz = target.LinearRgbToXyz(new LinearRgb(1, 1, 1));
            var staleGrayXyz = target.LinearRgbToXyz(new LinearRgb(staleRelativeY, staleRelativeY, staleRelativeY));

            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "White",
                        DisplayRgb = new LinearRgb(1, 1, 1),
                        Category = PatchCategory.Grayscale,
                        TargetXyz = whiteXyz
                    },
                    Xyz = new CieXyz(whiteXyz.X * 100, 100, whiteXyz.Z * 100)
                },
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Gray 50%",
                        DisplayRgb = new LinearRgb(signal, signal, signal),
                        Category = PatchCategory.Grayscale,
                        TargetXyz = staleGrayXyz
                    },
                    Xyz = new CieXyz(95 * correctRelativeY, 100 * correctRelativeY, 108 * correctRelativeY)
                }
            };

            var report = BuildReport(target, measurements);
            var point = report.GrayscaleTracking.Single(p => Math.Abs(p.InputLevel - signal) < 1e-9);

            Assert.Equal(correctRelativeY * 100, point.TargetLuminance, 6);
            Assert.NotEqual(staleRelativeY * 100, point.TargetLuminance, 6);
        }

        [Fact]
        public void ReportGrayscaleTracking_TargetLuminanceUsesTransferFunctionAndPeakNits()
        {
            var target = StandardTargets.SrgbGamma22;
            var halfSignal = 0.5;
            var targetRelative = target.ApplyEotf(halfSignal);
            var patch = new ColorPatch
            {
                Name = "Gray 50%",
                DisplayRgb = new LinearRgb(halfSignal, halfSignal, halfSignal),
                Category = PatchCategory.Grayscale,
                TargetXyz = target.LinearRgbToXyz(new LinearRgb(targetRelative, targetRelative, targetRelative))
            };
            var whitePatch = new ColorPatch
            {
                Name = "White",
                DisplayRgb = new LinearRgb(1, 1, 1),
                Category = PatchCategory.Grayscale,
                TargetXyz = target.LinearRgbToXyz(new LinearRgb(1, 1, 1))
            };
            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = patch,
                    Xyz = new CieXyz(patch.TargetXyz!.Value.X * 100, patch.TargetXyz.Value.Y * 100, patch.TargetXyz.Value.Z * 100)
                },
                new()
                {
                    Patch = whitePatch,
                    Xyz = new CieXyz(whitePatch.TargetXyz!.Value.X * 100, 100, whitePatch.TargetXyz.Value.Z * 100)
                }
            };

            var report = BuildReport(target, measurements);
            var point = report.GrayscaleTracking.Single(p => Math.Abs(p.InputLevel - halfSignal) < 1e-9);

            Assert.Equal(targetRelative * 100.0, point.TargetLuminance, 6);
        }

        [Fact]
        public void ReportIssues_SynthesizeTargetForCriticalPatchWithoutAttachedTarget()
        {
            var target = StandardTargets.SrgbGamma22;
            var whiteXyz = target.LinearRgbToXyz(new LinearRgb(1, 1, 1));
            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "White",
                        DisplayRgb = new LinearRgb(1, 1, 1),
                        Category = PatchCategory.Grayscale,
                    },
                    Xyz = new CieXyz(whiteXyz.X * 100, whiteXyz.Y * 100, whiteXyz.Z * 100)
                },
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Critical red",
                        DisplayRgb = new LinearRgb(1, 0, 0),
                        Category = PatchCategory.Primary,
                        IsCritical = true
                    },
                    // Deliberately measure blue where red was expected.
                    Xyz = new CieXyz(0.0193339 * 100, 0.0721750 * 100, 0.9503041 * 100)
                }
            };

            var report = BuildReport(target, measurements);

            Assert.Contains(report.Issues, issue =>
                issue.Category == IssueCategory.ColorAccuracy &&
                issue.Message.Contains("Critical red", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ReportChromaticityPoints_SynthesizeTargetsAndIgnoreWireRows()
        {
            var target = StandardTargets.SrgbGamma22;
            var redXyz = target.LinearRgbToXyz(new LinearRgb(1, 0, 0));
            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Red",
                        DisplayRgb = new LinearRgb(1, 0, 0),
                        Category = PatchCategory.Primary,
                    },
                    Xyz = new CieXyz(redXyz.X * 100, redXyz.Y * 100, redXyz.Z * 100)
                },
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "HDR wire 1000 nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Category = PatchCategory.General,
                        Nits = 1000
                    },
                    Xyz = new CieXyz(950, 1000, 1080)
                }
            };

            var report = BuildReport(target, measurements);
            var points = report.ChromaticityPoints;

            Assert.Single(points);
            Assert.NotNull(points[0].Target);
            Assert.Equal(target.RedPrimary.X, points[0].Target!.Value.X, 4);
            Assert.Equal(target.RedPrimary.Y, points[0].Target!.Value.Y, 4);
        }

        [Fact]
        public void ReportGraphData_IgnoreInvalidGrayscaleRows()
        {
            var target = StandardTargets.SrgbGamma22;
            var whiteXyz = target.LinearRgbToXyz(new LinearRgb(1, 1, 1));
            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "White",
                        DisplayRgb = new LinearRgb(1, 1, 1),
                        Category = PatchCategory.Grayscale,
                    },
                    Xyz = new CieXyz(whiteXyz.X * 100, whiteXyz.Y * 100, whiteXyz.Z * 100),
                    IsValid = true
                },
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Invalid gray",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Category = PatchCategory.Grayscale,
                    },
                    Xyz = new CieXyz(1000, 1000, 1000),
                    IsValid = false
                }
            };

            var report = BuildReport(target, measurements);

            Assert.Single(report.GrayscaleTracking);
            Assert.Single(report.RgbBalance);
            Assert.Single(report.ColorTemperatureTracking);
            Assert.Empty(report.GammaResponse);
            Assert.DoesNotContain(report.ChromaticityPoints, p => p.Name == "Invalid gray");
        }

        [Fact]
        public void ReportGammaResponse_ExcludesWhiteEndpoint()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 100.0);
            var report = BuildReport(target, measurements);

            Assert.DoesNotContain(report.GammaResponse, p => Math.Abs(p.InputLevel - 1.0) < 1e-9);
            Assert.All(report.GammaResponse, p => Assert.True(double.IsFinite(p.EffectiveGamma)));
        }

        [Fact]
        public void ReportGammaResponse_NormalizesAgainstMeasuredWhiteNotBrightestMidGray()
        {
            var target = StandardTargets.Rec709PureGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 100.0);
            var report = BuildReport(target, measurements);

            Assert.NotEmpty(report.GammaResponse);
            Assert.All(report.GammaResponse, point =>
                Assert.InRange(point.EffectiveGamma, 2.19, 2.21));
        }

        [Fact]
        public void ReportQuality_GammaTrackingScorePenalizesSmoothButWrongGamma()
        {
            var target = StandardTargets.Rec709PureGamma22;
            var measurements = BuildMeasuredGammaGrayscale(target, measuredGamma: 1.7, whiteNits: 100.0);
            var report = BuildReport(target, measurements);

            Assert.True(report.Quality.GammaTrackingScore < 80.0,
                $"wrong but smooth gamma should not score as excellent, got {report.Quality.GammaTrackingScore:F1}");
        }

        [Fact]
        public void ReportIssues_GammaTrackingComparesAgainstTargetGamma()
        {
            var target = StandardTargets.Rec709PureGamma22;
            var measurements = BuildMeasuredGammaGrayscale(target, measuredGamma: 1.7, whiteNits: 100.0);
            var report = BuildReport(target, measurements);

            Assert.Contains(report.Issues, issue =>
                issue.Category == IssueCategory.GammaTracking &&
                issue.Message.Contains("target", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ReportQuality_WhitePointUsesBrightestCompleteWhitePatch()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 100.0);
            var white = measurements.Single(m => m.Patch.DisplayRgb.R >= 0.99);

            measurements.Insert(0, new MeasurementResult
            {
                Patch = new ColorPatch
                {
                    Name = "Dim stale white",
                    DisplayRgb = new LinearRgb(1, 1, 1),
                    Category = PatchCategory.Grayscale,
                    IsCritical = true
                },
                Xyz = new CieXyz(white.Xyz.X * 0.8, white.Xyz.Y * 0.8, white.Xyz.Z * 1.2),
                IsValid = true
            });

            var report = BuildReport(target, measurements);

            Assert.True(report.Quality.WhitePointScore > 99.0,
                $"white point score should use the brightest complete white patch, got {report.Quality.WhitePointScore:F1}");
            Assert.DoesNotContain(report.Issues, issue => issue.Category == IssueCategory.WhitePoint);
        }

        [Fact]
        public void ReportGraphData_NoGrayscaleRows_DoesNotThrow()
        {
            var target = StandardTargets.SrgbGamma22;
            var redXyz = target.LinearRgbToXyz(new LinearRgb(1, 0, 0));
            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Red",
                        DisplayRgb = new LinearRgb(1, 0, 0),
                        Category = PatchCategory.Primary,
                    },
                    Xyz = new CieXyz(redXyz.X * 100, redXyz.Y * 100, redXyz.Z * 100)
                }
            };

            var report = BuildReport(target, measurements);

            Assert.Empty(report.GrayscaleTracking);
            Assert.Empty(report.GammaResponse);
            Assert.Empty(report.RgbBalance);
            Assert.Empty(report.ColorTemperatureTracking);
            Assert.Single(report.ChromaticityPoints);
        }

        [Fact]
        public void ReportGraphData_CorruptDisplayCharacteristics_DegradeToFiniteDefaults()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = BuildGrayscale(target, whiteNits: 100.0).Take(4).ToList();
            var report = new CalibrationReport
            {
                MonitorDevicePath = @"MONITOR\TEST\0001",
                MonitorName = "Corrupt Display Characteristics",
                Target = target,
                ColorimeterModel = "Test Meter",
                Measurements = measurements,
                MeasuredCharacteristics = new DisplayCharacteristics
                {
                    MeasuredRed = new Chromaticity(0.70, 0.35),
                    MeasuredGreen = new Chromaticity(double.NaN, 0.60),
                    MeasuredBlue = new Chromaticity(0.15, double.PositiveInfinity),
                    MeasuredWhite = new Chromaticity(0.70, 0.35),
                    MeasuredGamma = double.NaN,
                    PeakLuminance = double.PositiveInfinity,
                    BlackLevel = double.NaN
                }
            };

            Assert.All(report.GrayscaleTracking, point =>
            {
                Assert.True(double.IsFinite(point.TargetLuminance));
                Assert.True(point.TargetLuminance >= 0.0);
                Assert.True(double.IsFinite(point.MeasuredCct));
                Assert.True(double.IsFinite(point.Duv));
            });
            Assert.All(report.RgbBalance, point =>
            {
                Assert.True(double.IsFinite(point.RedRatio));
                Assert.True(double.IsFinite(point.GreenRatio));
                Assert.True(double.IsFinite(point.BlueRatio));
            });
            Assert.True(double.IsFinite(report.MeasuredCharacteristics.MeasuredCct));
            Assert.True(double.IsFinite(report.MeasuredCharacteristics.MeasuredDuv));
            Assert.Equal(0.0, report.MeasuredCharacteristics.ContrastRatio, 10);
        }

        [Fact]
        public void ReportQuality_NoValidAccuracyRows_DoesNotReportPerfectScore()
        {
            var target = StandardTargets.SrgbGamma22;
            var measurements = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "HDR wire 1000 nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Category = PatchCategory.General,
                        Nits = 1000
                    },
                    Xyz = new CieXyz(950, 1000, 1080),
                    IsValid = true
                },
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Invalid gray",
                        DisplayRgb = new LinearRgb(1, 1, 1),
                        Category = PatchCategory.Grayscale
                    },
                    Xyz = new CieXyz(95, 100, 108),
                    IsValid = false
                }
            };

            var report = BuildReport(target, measurements);

            Assert.Equal(0, report.OverallDeltaE.Count);
            Assert.Equal(CalibrationGrade.F, report.QualityGrade);
            Assert.Equal(0.0, report.Quality.OverallScore, 10);
            Assert.Equal(0.0, report.Quality.GrayscaleScore, 10);
            Assert.Equal(0.0, report.Quality.ColorAccuracyScore, 10);
            Assert.Equal(0.0, report.Quality.GammaTrackingScore, 10);
            Assert.Equal(0.0, report.Quality.WhitePointScore, 10);
        }

        [Fact]
        public void ReportDeltaEImprovement_RequiresValidPreAndPostEvidence()
        {
            var target = StandardTargets.SrgbGamma22;
            var valid = BuildGrayscale(target, whiteNits: 100.0).Take(4).ToList();
            var invalid = new List<MeasurementResult>
            {
                new()
                {
                    Patch = new ColorPatch
                    {
                        Name = "Invalid white",
                        DisplayRgb = new LinearRgb(1, 1, 1),
                        Category = PatchCategory.Grayscale
                    },
                    Xyz = new CieXyz(95, 100, 108),
                    IsValid = false
                }
            };

            var missingPostEvidence = BuildReport(target, valid, preCalibration: valid, postCalibration: invalid);
            var missingPreEvidence = BuildReport(target, valid, preCalibration: invalid, postCalibration: valid);

            Assert.Null(missingPostEvidence.DeltaEImprovement);
            Assert.Null(missingPostEvidence.DeltaEImprovementPercent);
            Assert.Null(missingPreEvidence.DeltaEImprovement);
            Assert.Null(missingPreEvidence.DeltaEImprovementPercent);
        }

        [Fact]
        public void ReportDeltaEImprovement_ValidPreAndPostEvidence_ComputesImprovement()
        {
            var target = StandardTargets.SrgbGamma22;
            var post = BuildGrayscale(target, whiteNits: 100.0).Take(4).ToList();
            var pre = post.Select(m => new MeasurementResult
            {
                Patch = m.Patch,
                Xyz = new CieXyz(m.Xyz.X * 0.90, m.Xyz.Y, m.Xyz.Z * 1.15),
                IsValid = true
            }).ToList();

            var report = BuildReport(target, post, preCalibration: pre, postCalibration: post);

            Assert.NotNull(report.DeltaEImprovement);
            Assert.NotNull(report.DeltaEImprovementPercent);
            Assert.True(report.DeltaEImprovement > 0);
            Assert.True(report.DeltaEImprovementPercent > 0);
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(20, 18)]
        [InlineData(21, 19)]
        [InlineData(100, 94)]
        public void DeltaEStatistics_Percentile95_UsesNearestRankIndex(int count, int expectedIndex)
        {
            Assert.Equal(expectedIndex, DeltaEStatistics.NearestRankPercentileIndex(count, 0.95));
        }

        [Theory]
        [InlineData(0, 0.95, 0)]
        [InlineData(10, double.NaN, 0)]
        [InlineData(10, -1.0, 0)]
        [InlineData(10, 2.0, 9)]
        public void DeltaEStatistics_PercentileIndex_ClampsInvalidInputs(int count, double percentile, int expectedIndex)
        {
            Assert.Equal(expectedIndex, DeltaEStatistics.NearestRankPercentileIndex(count, percentile));
        }

        private static CalibrationReport BuildReport(
            CalibrationTarget target,
            IReadOnlyList<MeasurementResult> measurements,
            IReadOnlyList<MeasurementResult>? preCalibration = null,
            IReadOnlyList<MeasurementResult>? postCalibration = null)
            => new()
            {
                MonitorDevicePath = @"MONITOR\TEST\0001",
                MonitorName = "Test Display",
                Target = target,
                ColorimeterModel = "Test Meter",
                Measurements = measurements,
                PreCalibrationMeasurements = preCalibration,
                PostCalibrationMeasurements = postCalibration,
                MeasuredCharacteristics = new DisplayCharacteristics
                {
                    MeasuredRed = target.RedPrimary,
                    MeasuredGreen = target.GreenPrimary,
                    MeasuredBlue = target.BluePrimary,
                    MeasuredWhite = target.WhitePoint,
                    MeasuredGamma = target.Gamma ?? 2.2,
                    PeakLuminance = measurements.Where(m => m.Patch.Nits is null).Max(m => m.Xyz.Y),
                    BlackLevel = 0.05
                }
            };

        private static List<MeasurementResult> BuildMeasuredGammaGrayscale(
            CalibrationTarget target,
            double measuredGamma,
            double whiteNits)
        {
            var list = new List<MeasurementResult>();
            for (int i = 0; i < 16; i++)
            {
                double signal = i / 15.0;
                double targetLinear = target.ApplyEotf(signal);
                double measuredLinear = Math.Pow(signal, measuredGamma);
                var targetXyz = target.LinearRgbToXyz(new LinearRgb(targetLinear, targetLinear, targetLinear));
                var measuredXyz = target.LinearRgbToXyz(new LinearRgb(measuredLinear, measuredLinear, measuredLinear));

                list.Add(new MeasurementResult
                {
                    Patch = new ColorPatch
                    {
                        Name = $"Gray {signal * 100:F0}%",
                        DisplayRgb = new LinearRgb(signal, signal, signal),
                        Category = PatchCategory.Grayscale,
                        TargetXyz = targetXyz,
                        TargetLab = ColorMath.XyzToLab(targetXyz)
                    },
                    Xyz = new CieXyz(
                        measuredXyz.X * whiteNits,
                        measuredXyz.Y * whiteNits,
                        measuredXyz.Z * whiteNits),
                    IsValid = true
                });
            }

            return list;
        }
    }
}
