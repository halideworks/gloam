using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class HdrJointRefinementTests
    {
        private static readonly double[] ToneRungs = { 16, 64, 150, 320 };
        private static readonly IReadOnlyList<ColoredHdrStimulus> ColorStimuli =
            ColoredHdrVerificationSet.BuildForMatrixRefinement(500.0);

        private static readonly double[,] ColorError =
        {
            { 0.98, 0.03, 0.00 },
            { 0.01, 1.04, -0.01 },
            { 0.00, 0.02, 0.97 },
        };

        private static CieXyz Apply(double[,] matrix, CieXyz xyz) => new(
            matrix[0, 0] * xyz.X + matrix[0, 1] * xyz.Y + matrix[0, 2] * xyz.Z,
            matrix[1, 0] * xyz.X + matrix[1, 1] * xyz.Y + matrix[1, 2] * xyz.Z,
            matrix[2, 0] * xyz.X + matrix[2, 1] * xyz.Y + matrix[2, 2] * xyz.Z);

        private static MeasurementResult Tone(double requested, double gain, int index = 0) => new()
        {
            Patch = new ColorPatch
            {
                Name = $"PQ {requested:F0}",
                DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                Nits = requested,
                Index = index,
            },
            Xyz = Chromaticity.D65.ToXyz(requested * gain),
            SequenceIndex = index,
        };

        private static HdrJointRefinement.Measurements Measure(double toneGain, double[,] colorError) => new(
            ToneRungs.Select((n, i) => Tone(n, toneGain, i)).ToList(),
            ColorStimuli.Select(s => (s, Apply(colorError, s.ReferenceXyz))).ToList());

        private static HdrMhc2LutBuilder.Result IdentityLuts(double scale = 1.0)
        {
            double[] lut = Enumerable.Range(0, 1024).Select(i => i / 1023.0).ToArray();
            return new HdrMhc2LutBuilder.Result(
                lut, (double[])lut.Clone(), (double[])lut.Clone(),
                0.0, 500.0, true, scale);
        }

        private static double Sample(IReadOnlyList<double> lut, double p)
        {
            double x = Math.Clamp(p, 0.0, 1.0) * (lut.Count - 1);
            int lo = (int)Math.Floor(x);
            int hi = Math.Min(lo + 1, lut.Count - 1);
            double t = x - lo;
            return lut[lo] + (lut[hi] - lut[lo]) * t;
        }

        [Fact]
        public void PreserveNeutralLuminance_RemovesWhiteGainButKeepsColorRotation()
        {
            var white = Chromaticity.D65.ToXyz(1.0);
            var residual = new double[,]
            {
                { 1.03, 0.02, 0.00 },
                { 0.01, 1.08, -0.01 },
                { 0.00, 0.01, 0.96 },
            };

            var normalized = HdrColorMatrixRefiner.PreserveNeutralLuminance(residual, white);
            var mappedWhite = Apply(normalized, white);

            Assert.Equal(white.Y, mappedWhite.Y, 12);
            Assert.NotEqual(0.0, normalized[0, 1]);
            Assert.NotEqual(0.0, normalized[1, 0]);
        }

        [Fact]
        public void RebaseNeutralScale_PreservesContentToLutMapping()
        {
            double[] shaped = Enumerable.Range(0, 1024)
                .Select(i => Math.Pow(i / 1023.0, 1.08))
                .ToArray();
            var old = new HdrMhc2LutBuilder.Result(
                shaped, (double[])shaped.Clone(), (double[])shaped.Clone(),
                0.0, 1000.0, true, 0.94);

            var rebased = HdrMhc2LutBuilder.RebaseNeutralScale(old, 0.81);

            foreach (double nits in new[] { 2.0, 16.0, 100.0, 400.0, 800.0 })
            {
                double oldInput = TransferFunctions.PqInverseEotf(old.MatrixNeutralScale * nits);
                double newInput = TransferFunctions.PqInverseEotf(rebased.MatrixNeutralScale * nits);
                Assert.InRange(
                    Math.Abs(Sample(old.LutR, oldInput) - Sample(rebased.LutR, newInput)),
                    0.0, 2e-5);
            }
            Assert.Equal(0.81, rebased.MatrixNeutralScale, 12);
            Assert.True(rebased.LutR.Zip(rebased.LutR.Skip(1), (a, b) => b >= a).All(x => x));
        }

        [Fact]
        public void BuildCandidate_ChangesMatrixAndToneWithoutDoubleCountingWhiteGain()
        {
            var initial = new HdrJointRefinement.State(
                HdrColorMatrixLoop.IdentityCorrection(), IdentityLuts());
            var measured = Measure(1.08, ColorError);

            var candidate = HdrJointRefinement.BuildCandidate(
                initial, measured, Chromaticity.D65.ToXyz(1.0), _ => 0.92);

            Assert.NotNull(candidate.Value);
            Assert.True(candidate.ChangedColor);
            Assert.True(candidate.ChangedTone);
            Assert.Equal(0.92, candidate.Value!.Luts.MatrixNeutralScale, 12);

            var mappedWhite = Apply(candidate.Value.XyzCorrection, Chromaticity.D65.ToXyz(1.0));
            Assert.Equal(1.0, mappedWhite.Y, 10);
        }

        [Fact]
        public async Task Loop_InstallsOneAtomicCandidateAndConvergesBothAxes()
        {
            var initialState = new HdrJointRefinement.State(
                HdrColorMatrixLoop.IdentityCorrection(), IdentityLuts());
            var scripted = new Queue<HdrJointRefinement.Measurements>(new[]
            {
                Measure(1.06, ColorError),
                Measure(1.0, HdrColorMatrixLoop.IdentityCorrection()),
            });
            var installed = new List<HdrJointRefinement.State>();

            Task<HdrJointRefinement.Measurements> MeasureAsync(
                IReadOnlyList<double> _, IReadOnlyList<ColoredHdrStimulus> __,
                int ___, CancellationToken ____) => Task.FromResult(scripted.Dequeue());

            Task<(HdrJointRefinement.State, string)> InstallAsync(
                HdrJointRefinement.State state, CancellationToken _)
            {
                installed.Add(state);
                return Task.FromResult((state, $"joint-{installed.Count}"));
            }

            var outcome = await HdrJointRefinement.RunAsync(new HdrJointRefinement.Config
            {
                InitialState = initialState,
                TargetWhite = Chromaticity.D65.ToXyz(1.0),
                ToneRungs = ToneRungs,
                ColorStimuli = ColorStimuli,
                MeasureAsync = MeasureAsync,
                ResolveMatrixNeutralScale = _ => 0.93,
                InstallAsync = InstallAsync,
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall);
            Assert.True(outcome.Converged, outcome.StopReason);
            Assert.Single(installed);
            Assert.True(outcome.FinalMetrics.JointScore < outcome.InitialMetrics.JointScore);
            Assert.True(outcome.Passes[0].ChangedColor);
            Assert.True(outcome.Passes[0].ChangedTone);
        }

        [Fact]
        public async Task Loop_JointRegression_RestoresInitialState()
        {
            var initialState = new HdrJointRefinement.State(
                HdrColorMatrixLoop.IdentityCorrection(), IdentityLuts());
            var awful = new double[,]
            {
                { 0.86, 0.09, 0.00 },
                { 0.08, 1.15, -0.02 },
                { 0.00, 0.08, 0.84 },
            };
            var scripted = new Queue<HdrJointRefinement.Measurements>(new[]
            {
                Measure(1.06, ColorError),
                Measure(1.18, awful),
            });
            var installed = new List<HdrJointRefinement.State>();

            var outcome = await HdrJointRefinement.RunAsync(new HdrJointRefinement.Config
            {
                InitialState = initialState,
                TargetWhite = Chromaticity.D65.ToXyz(1.0),
                ToneRungs = ToneRungs,
                ColorStimuli = ColorStimuli,
                MeasureAsync = (_, _, _, _) => Task.FromResult(scripted.Dequeue()),
                ResolveMatrixNeutralScale = _ => 0.93,
                InstallAsync = (state, _) =>
                {
                    installed.Add(state);
                    return Task.FromResult((state, $"joint-{installed.Count}"));
                },
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall);
            Assert.True(outcome.EndedOnBest);
            Assert.Equal(2, installed.Count);
            Assert.Same(initialState, installed[^1]);
            Assert.Same(initialState, outcome.FinalState);
            Assert.Equal(outcome.InitialMetrics.JointScore, outcome.FinalMetrics.JointScore, 10);
        }

        [Fact]
        public void IsBetter_RejectsLowerJointScoreWhenEitherAxisMateriallyRegresses()
        {
            var incumbent = new HdrJointRefinement.Metrics(0.020, 8.0, 5.0);

            Assert.False(HdrJointRefinement.IsBetter(
                new HdrJointRefinement.Metrics(0.023, 1.0, 1.1), incumbent));
            Assert.False(HdrJointRefinement.IsBetter(
                new HdrJointRefinement.Metrics(0.010, 8.6, 1.1), incumbent));
            Assert.True(HdrJointRefinement.IsBetter(
                new HdrJointRefinement.Metrics(0.019, 7.8, 4.8), incumbent));
        }

        [Fact]
        public async Task Loop_CancelAfterInstall_RestoresInitialState()
        {
            var initialState = new HdrJointRefinement.State(
                HdrColorMatrixLoop.IdentityCorrection(), IdentityLuts());
            int measurements = 0;
            var installed = new List<HdrJointRefinement.State>();

            async Task<HdrJointRefinement.Measurements> MeasureAsync(
                IReadOnlyList<double> _, IReadOnlyList<ColoredHdrStimulus> __,
                int ___, CancellationToken ____)
            {
                if (measurements++ == 0)
                    return Measure(1.06, ColorError);
                await Task.Yield();
                throw new OperationCanceledException();
            }

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                HdrJointRefinement.RunAsync(new HdrJointRefinement.Config
                {
                    InitialState = initialState,
                    TargetWhite = Chromaticity.D65.ToXyz(1.0),
                    ToneRungs = ToneRungs,
                    ColorStimuli = ColorStimuli,
                    MeasureAsync = MeasureAsync,
                    ResolveMatrixNeutralScale = _ => 0.93,
                    InstallAsync = (state, _) =>
                    {
                        installed.Add(state);
                        return Task.FromResult((state, $"joint-{installed.Count}"));
                    },
                }, CancellationToken.None));

            Assert.Equal(2, installed.Count);
            Assert.Same(initialState, installed[^1]);
        }
    }
}
