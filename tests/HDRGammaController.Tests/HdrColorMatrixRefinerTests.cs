using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Closed-loop HDR color (roadmap 2.2): the luminance-weighted matrix fit recovers a
    /// synthetic gamut rotation, the refusal policy holds, and the keep-best loop drives a
    /// simulated rotated panel onto the references.
    /// </summary>
    public class HdrColorMatrixRefinerTests
    {
        private static IReadOnlyList<ColoredHdrStimulus> Stimuli =>
            ColoredHdrVerificationSet.BuildForMatrixRefinement(500.0);

        /// <summary>A gentle gamut rotation + green gain — the QD-OLED signature.</summary>
        private static readonly double[,] PanelError =
        {
            { 0.98, 0.03, 0.00 },
            { 0.01, 1.04, -0.01 },
            { 0.00, 0.02, 0.97 },
        };

        private static CieXyz Apply(double[,] f, CieXyz xyz) => new(
            f[0, 0] * xyz.X + f[0, 1] * xyz.Y + f[0, 2] * xyz.Z,
            f[1, 0] * xyz.X + f[1, 1] * xyz.Y + f[1, 2] * xyz.Z,
            f[2, 0] * xyz.X + f[2, 1] * xyz.Y + f[2, 2] * xyz.Z);

        private static List<(ColoredHdrStimulus, CieXyz)> MeasureThrough(double[,] panelTimesCorrectionInverse)
            => Stimuli.Select(s => (s, Apply(panelTimesCorrectionInverse, s.ReferenceXyz))).ToList();

        [Fact]
        public void BuildForMatrixRefinement_GamutAware_UsesTargetPrimaries()
        {
            // On an sRGB-gamut HDR target, the red reference must be sRGB red (reachable),
            // NOT Rec.2020 red (which the panel can't emit — the source of the spurious
            // 40–160 ΔE ITP that made Refine HDR Color refuse on David's MAG).
            var srgb = ColoredHdrVerificationSet.BuildForMatrixRefinement(500.0, ColorMath.SrgbToXyzMatrix);
            var rec2020 = ColoredHdrVerificationSet.BuildForMatrixRefinement(500.0, ColorMath.Rec2020ToXyzMatrix);

            var srgbRed = srgb.First(s => s.Hue == "Red").ReferenceXyz.ToChromaticity();
            var recRed = rec2020.First(s => s.Hue == "Red").ReferenceXyz.ToChromaticity();

            Assert.Equal(Chromaticity.Rec709Red.X, srgbRed.X, 3);
            Assert.Equal(Chromaticity.Rec709Red.Y, srgbRed.Y, 3);
            Assert.Equal(Chromaticity.Rec2020Red.X, recRed.X, 3);
            // The two containers put "red" at genuinely different chromaticities.
            Assert.True(Math.Abs(srgbRed.X - recRed.X) > 0.05);

            // sRGB-container references round-trip to a positive (in-gamut) scRGB triple.
            var redScRgb = srgb.First(s => s.Hue == "Red").ScRgbNits;
            Assert.True(redScRgb.R > 0 && redScRgb.G >= -1e-6 && redScRgb.B >= -1e-6);
        }

        [Fact]
        public void BuildForMatrixRefinement_IncludesNeutralsAtEveryRung()
        {
            var stimuli = Stimuli;
            var whites = stimuli.Where(s => s.Hue == "White").ToList();
            Assert.Equal(stimuli.Select(s => s.RungNits).Distinct().Count(), whites.Count);
            foreach (var white in whites)
            {
                Assert.Equal(white.RungNits, white.ReferenceXyz.Y, 6);
                // Neutral in the container: reference chromaticity is D65.
                var xy = white.ReferenceXyz.ToChromaticity();
                Assert.Equal(Chromaticity.D65.X, xy.X, 3);
                Assert.Equal(Chromaticity.D65.Y, xy.Y, 3);
            }
            // The plain verification set is unchanged (no White).
            Assert.DoesNotContain(ColoredHdrVerificationSet.Build(500.0), s => s.Hue == "White");
        }

        [Fact]
        public void Fit_RecoversSyntheticRotation_Exactly()
        {
            var readings = MeasureThrough(PanelError);

            var result = HdrColorMatrixRefiner.Fit(readings);

            Assert.Null(result.RefusalReason);
            Assert.NotNull(result.XyzCorrection);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Assert.Equal(PanelError[i, j], result.XyzCorrection![i, j], 6);
        }

        [Fact]
        public void Fit_Damped_TakesPartialStep()
        {
            var result = HdrColorMatrixRefiner.Fit(MeasureThrough(PanelError), damping: 0.5);

            Assert.NotNull(result.XyzCorrection);
            // Diagonal example: 1 + 0.5·(1.04 − 1) = 1.02 on the green row.
            Assert.Equal(1.02, result.XyzCorrection![1, 1], 6);
            Assert.Equal(0.015, result.XyzCorrection[0, 1], 6);
        }

        [Fact]
        public void Fit_Refuses_WhenConverged()
        {
            var identity = HdrColorMatrixLoop.IdentityCorrection();
            var result = HdrColorMatrixRefiner.Fit(MeasureThrough(identity));

            Assert.Null(result.XyzCorrection);
            Assert.Contains("converged", result.RefusalReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Fit_Refuses_WildError()
        {
            var wild = new double[,] { { 0.5, 0, 0 }, { 0, 1.6, 0 }, { 0, 0, 0.4 } };
            var result = HdrColorMatrixRefiner.Fit(MeasureThrough(wild));

            Assert.Null(result.XyzCorrection);
            Assert.Contains("re-run the calibration", result.RefusalReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Fit_Refuses_TooFewValidReadings()
        {
            var few = MeasureThrough(PanelError).Take(5).ToList();
            var result = HdrColorMatrixRefiner.Fit(few);

            Assert.Null(result.XyzCorrection);
            Assert.Contains("reachable colored reading", result.RefusalReason);
        }

        [Fact]
        public void Fit_ExcludesUnreachableClippedStimuli_FromGradeAndFit()
        {
            // Simulate David's MAG: most hues accurate, but saturated blue clips hard (the
            // panel can't emit blue that bright). The clipped blue must NOT drag the graded
            // error above the refine gate — it's a hardware limit, not a fixable error.
            var readings = Stimuli.Select(s =>
            {
                CieXyz measured;
                if (s.Hue == "Blue" && s.RungNits >= 150)
                    measured = new CieXyz(s.ReferenceXyz.X * 0.3, s.ReferenceXyz.Y * 0.3, s.ReferenceXyz.Z * 0.3); // clipped
                else
                    measured = Apply(PanelError, s.ReferenceXyz); // small correctable error
                return (s, measured);
            }).ToList();

            var result = HdrColorMatrixRefiner.Fit(readings);

            Assert.True(result.ExcludedUnreachable >= 1, "the clipped blue rung(s) should be excluded");
            // Graded error reflects the reachable hues (small), so refinement proceeds.
            Assert.NotNull(result.XyzCorrection);
            Assert.True(result.Before.AverageItpDeltaE < HdrColorMatrixRefiner.MaxAvgItpToRefine,
                $"graded error {result.Before.AverageItpDeltaE:F1} should exclude the clip and stay under the gate");
        }

        [Fact]
        public void AverageReachableItp_IgnoresClippedReadings()
        {
            var readings = Stimuli.Select(s =>
            {
                CieXyz measured = s.Hue == "Blue"
                    ? new CieXyz(s.ReferenceXyz.X * 0.2, s.ReferenceXyz.Y * 0.2, s.ReferenceXyz.Z * 0.2)
                    : s.ReferenceXyz; // perfect on everything else
                return (s, measured);
            }).ToList();

            // Perfect on all reachable hues → ~0 despite blue clipping badly.
            Assert.True(HdrColorMatrixRefiner.AverageReachableItp(readings) < 1.0);
        }

        [Fact]
        public void Fit_Refuses_WithoutNeutralAnchors()
        {
            // Colored-only readings (the plain verification set) must be refused: an
            // unanchored fit can rotate the white point.
            var coloredOnly = ColoredHdrVerificationSet.Build(500.0)
                .Select(s => (s, Apply(PanelError, s.ReferenceXyz)))
                .ToList();

            var result = HdrColorMatrixRefiner.Fit(coloredOnly);

            Assert.Null(result.XyzCorrection);
            Assert.Contains("White", result.RefusalReason);
        }

        // ---- The loop on a simulated rotated panel -------------------------------------------

        [Fact]
        public async Task Loop_RotatedPanel_ConvergesAndInstallsCumulativeCorrection()
        {
            // Simulated chain: with cumulative correction C installed, the panel emits
            // F_panel·C⁻¹·reference (the installer's M′ = D⁻¹·F⁻¹·T cancels F when C = F).
            double[,] installed = HdrColorMatrixLoop.IdentityCorrection();
            var installs = new List<double[,]>();

            Task<IReadOnlyList<(ColoredHdrStimulus, CieXyz)>> Measure(
                IReadOnlyList<ColoredHdrStimulus> stimuli, int offset, CancellationToken ct)
            {
                var chain = ColorMath.MultiplyMatrices(PanelError, ColorMath.Invert3x3(installed));
                IReadOnlyList<(ColoredHdrStimulus, CieXyz)> result = stimuli
                    .Select(s => (s, Apply(chain, s.ReferenceXyz)))
                    .ToList();
                return Task.FromResult(result);
            }

            Task<string> Install(double[,] correction, CancellationToken ct)
            {
                installed = correction;
                installs.Add(correction);
                return Task.FromResult($"color-refined-{installs.Count}");
            }

            var outcome = await HdrColorMatrixLoop.RunAsync(new HdrColorMatrixLoop.Config
            {
                Stimuli = Stimuli,
                MeasureSweepAsync = Measure,
                InstallAsync = Install,
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall);
            Assert.True(outcome.Converged, $"loop did not converge: {outcome.StopReason}");
            Assert.True(outcome.FinalAvgItp < 2.0,
                $"final avg ΔE ITP {outcome.FinalAvgItp:F2} should be under the convergence gate");
            Assert.True(outcome.InitialAvgItp > outcome.FinalAvgItp);

            // The installed cumulative correction recovered the panel error.
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Assert.Equal(PanelError[i, j], installed[i, j], 3);
        }

        [Fact]
        public async Task Loop_AlreadyConverged_NoInstall()
        {
            Task<IReadOnlyList<(ColoredHdrStimulus, CieXyz)>> Measure(
                IReadOnlyList<ColoredHdrStimulus> stimuli, int offset, CancellationToken ct)
            {
                IReadOnlyList<(ColoredHdrStimulus, CieXyz)> exact = stimuli
                    .Select(s => (s, s.ReferenceXyz)).ToList();
                return Task.FromResult(exact);
            }

            var outcome = await HdrColorMatrixLoop.RunAsync(new HdrColorMatrixLoop.Config
            {
                Stimuli = Stimuli,
                MeasureSweepAsync = Measure,
                InstallAsync = (_, _) => throw new InvalidOperationException("must not install"),
            }, CancellationToken.None);

            Assert.False(outcome.AnyInstall);
            Assert.True(outcome.Converged);
        }

        [Fact]
        public async Task Loop_RegressingPass_RestoresBest()
        {
            // Install makes things WORSE (pathological panel): the loop must end with the
            // best-measured correction (identity/initial) back on the display.
            double[,] installed = HdrColorMatrixLoop.IdentityCorrection();
            var installs = new List<double[,]>();
            var mild = new double[,] { { 1.0, 0.02, 0 }, { 0, 1.03, 0 }, { 0, 0.02, 0.98 } };
            var awful = new double[,] { { 0.9, 0.1, 0 }, { 0.1, 1.1, 0 }, { 0, 0.1, 0.9 } };

            Task<IReadOnlyList<(ColoredHdrStimulus, CieXyz)>> Measure(
                IReadOnlyList<ColoredHdrStimulus> stimuli, int offset, CancellationToken ct)
            {
                // Before any install: mild error. After ANY install: awful (regression).
                var error = installs.Count == 0 ? mild : awful;
                IReadOnlyList<(ColoredHdrStimulus, CieXyz)> result = stimuli
                    .Select(s => (s, Apply(error, s.ReferenceXyz))).ToList();
                return Task.FromResult(result);
            }

            Task<string> Install(double[,] correction, CancellationToken ct)
            {
                installed = correction;
                installs.Add(correction);
                return Task.FromResult($"p{installs.Count}");
            }

            var outcome = await HdrColorMatrixLoop.RunAsync(new HdrColorMatrixLoop.Config
            {
                Stimuli = Stimuli,
                MeasureSweepAsync = Measure,
                InstallAsync = Install,
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall);
            Assert.True(outcome.EndedOnBest);
            // Best was the initial (identity) state; final install restored it.
            Assert.Equal(1.0, installed[0, 0], 9);
            Assert.Equal(0.0, installed[0, 1], 9);
            Assert.Equal(outcome.InitialAvgItp, outcome.FinalAvgItp, 6);
        }
    }

    public class ToneMappingAnalyzerTests
    {
        [Fact]
        public void Analyze_PerfectTracking_NoKneeObserved()
        {
            var ladder = new[] { 100.0, 200, 300, 400, 450 }
                .Select(n => new ToneMapLadderPoint(n, n)).ToList();

            var c = ToneMappingAnalyzer.Analyze(450, 300, ladder);

            Assert.False(c.KneeObserved);
            Assert.Equal(450, c.KneeNits, 3);
            Assert.Equal(450, c.MeasuredPeakNits, 3);
            Assert.Equal(450, c.HgigPeakNits, 3);
        }

        [Fact]
        public void Analyze_RollOff_FindsInterpolatedKnee()
        {
            // Tracks perfectly to 400, then rolls off: 500→470 (94%), 600→510 (85%).
            var ladder = new List<ToneMapLadderPoint>
            {
                new(100, 100), new(200, 200), new(300, 300), new(400, 400),
                new(500, 470), new(600, 510),
            };

            var c = ToneMappingAnalyzer.Analyze(600, 400, ladder);

            Assert.True(c.KneeObserved);
            // Ratio crosses 0.95 between 400 (1.00) and 500 (0.94): t = 0.05/0.06 → ~483.
            Assert.InRange(c.KneeNits, 470, 495);
            Assert.Equal(510, c.MeasuredPeakNits, 3);
        }

        [Fact]
        public void Analyze_OverclaimingPanel_MeasuredPeakBelowClaim()
        {
            // Claims 1000, actually plateaus ~520 — the classic HDR400-panel-claims-1000 case.
            var ladder = new List<ToneMapLadderPoint>
            {
                new(400, 398), new(500, 480), new(600, 505), new(800, 515),
                new(1000, 520), new(1100, 520),
            };

            var c = ToneMappingAnalyzer.Analyze(1000, 500, ladder);

            Assert.True(c.KneeObserved);
            Assert.Equal(520, c.MeasuredPeakNits, 3);
            Assert.Equal(520, c.SuggestedMaxCllNits, 3);
            var text = ToneMappingAnalyzer.Describe(c);
            Assert.Contains("520", text);
            Assert.Contains("1000", text);
        }

        [Fact]
        public void Analyze_AplSweep_YieldsFullFrameAndMaxFall()
        {
            var ladder = new[] { 100.0, 300, 450 }.Select(n => new ToneMapLadderPoint(n, n)).ToList();
            var apl = new List<AplPoint>
            {
                new(10, 450), new(25, 430), new(50, 380), new(100, 260),
            };

            var c = ToneMappingAnalyzer.Analyze(450, 0, ladder, apl);

            Assert.Equal(260, c.MeasuredFullFramePeakNits!.Value, 3);
            Assert.Equal(260, c.SuggestedMaxFallNits!.Value, 3);
            Assert.Contains("ABL", ToneMappingAnalyzer.Describe(c));
        }

        [Fact]
        public void Analyze_TooFewPoints_Throws()
        {
            Assert.Throws<ArgumentException>(() => ToneMappingAnalyzer.Analyze(
                400, 0, new[] { new ToneMapLadderPoint(100, 100) }));
        }

        [Fact]
        public void Characterization_RoundTripsThroughJson()
        {
            var ladder = new[] { 100.0, 300, 450 }.Select(n => new ToneMapLadderPoint(n, n)).ToList();
            var c = ToneMappingAnalyzer.Analyze(450, 300, ladder, new List<AplPoint> { new(10, 450), new(100, 300) });

            string json = System.Text.Json.JsonSerializer.Serialize(c);
            var back = System.Text.Json.JsonSerializer.Deserialize<ToneMappingCharacterization>(json);

            Assert.NotNull(back);
            Assert.Equal(c.MeasuredPeakNits, back!.MeasuredPeakNits);
            Assert.Equal(c.Ladder.Count, back.Ladder.Count);
            Assert.Equal(c.AplSweep.Count, back.AplSweep.Count);
        }
    }
}
