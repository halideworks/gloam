using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class NightModeOptimizationTests
    {
        [Fact]
        public void NewSettings_RoundTripAndCloneWithoutChangingDefaults()
        {
            var settings = new NightModeSettings
            {
                HdrHighlightPolicy = NightHdrHighlightPolicy.DoseBound,
                OptimizeHardwareRamp = false,
                HarvestSubJndBudget = false,
                ContentAdaptiveDose = true
            };

            var data = NightModeSettingsData.FromNightModeSettings(settings);
            var restored = data.ToNightModeSettings();
            var cloned = NightModeService.CloneSettings(settings);

            Assert.Equal(NightHdrHighlightPolicy.DoseBound, restored.HdrHighlightPolicy);
            Assert.False(restored.OptimizeHardwareRamp);
            Assert.False(restored.HarvestSubJndBudget);
            Assert.True(restored.ContentAdaptiveDose);
            Assert.Equal(restored.HdrHighlightPolicy, cloned.HdrHighlightPolicy);
            Assert.Equal(restored.ContentAdaptiveDose, cloned.ContentAdaptiveDose);

            // Runtime calibration defaults preserve the pre-night-mode LUT behavior. The
            // night apply path explicitly opts into the user's new defaults.
            var runtime = CalibrationSettings.Default;
            Assert.Equal(NightHdrHighlightPolicy.Creative, runtime.NightHdrHighlightPolicy);
            Assert.False(runtime.OptimizeNightHardwareRamp);
            Assert.False(runtime.HarvestNightSubJndBudget);
        }

        [Theory]
        [InlineData(false, 200.0)]
        [InlineData(true, 203.0)]
        public void Compiler_EmitsExactlyRepresentableMonotoneHardwareKnots(bool hdr, double whiteNits)
        {
            var settings = WarmSettings();
            settings.OptimizeNightHardwareRamp = false;
            var source = LutGenerator.GenerateLut(GammaMode.Gamma22, whiteNits, settings, hdr);

            settings.OptimizeNightHardwareRamp = true;
            var sw = Stopwatch.StartNew();
            var result = NightModeRampCompiler.Compile(source.R, source.G, source.B, settings, hdr, whiteNits);
            sw.Stop();

            Assert.Equal(source.R.Length, result.R.Length);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"compiler took {sw.Elapsed}");
            foreach (var ramp in new[]
                     {
                         NativeGammaRamp.BuildRampChannel(result.R),
                         NativeGammaRamp.BuildRampChannel(result.G),
                         NativeGammaRamp.BuildRampChannel(result.B)
                     })
            {
                Assert.Equal(256, ramp.Length);
                for (int i = 1; i < ramp.Length; i++)
                    Assert.True(ramp[i] >= ramp[i - 1], $"hardware knot {i} is non-monotone");
            }

            Assert.True(result.EstimatedMelanopicReduction >= 0.0);
            Assert.True(double.IsFinite(result.CompiledAverageDeltaEPrime));
        }

        [Fact]
        public void HdrHighlightPolicies_FormAnExplicitWarmthOrdering()
        {
            double BlueAt(int index, NightHdrHighlightPolicy policy)
            {
                var settings = WarmSettings();
                settings.NightHdrHighlightPolicy = policy;
                settings.OptimizeNightHardwareRamp = false;
                return LutGenerator.GenerateLut(GammaMode.Gamma22, 200, settings, isHdr: true).B[index];
            }

            // Above diffuse white, creative intent may return to neutral, Comfort retains
            // 25% of the warm spectral shape, and DoseBound retains all of it.
            foreach (int index in new[] { 850, 950, 1023 })
            {
                double creative = BlueAt(index, NightHdrHighlightPolicy.Creative);
                double comfort = BlueAt(index, NightHdrHighlightPolicy.Comfort);
                double bounded = BlueAt(index, NightHdrHighlightPolicy.DoseBound);
                Assert.True(bounded <= comfort + 1e-12, $"index {index}: {bounded} > {comfort}");
                Assert.True(comfort <= creative + 1e-12, $"index {index}: {comfort} > {creative}");
            }
        }

        [Theory]
        [InlineData(NightHdrHighlightPolicy.Creative)]
        [InlineData(NightHdrHighlightPolicy.Comfort)]
        public void AppearanceProjection_PreservesExplicitHdrHeadroomPolicy(
            NightHdrHighlightPolicy policy)
        {
            var settings = WarmSettings();
            settings.NightHdrHighlightPolicy = policy;
            settings.OptimizeNightHardwareRamp = false;
            settings.HarvestNightSubJndBudget = false;
            var source = LutGenerator.GenerateLut(
                GammaMode.Gamma22, 200.0, settings, isHdr: true);

            settings.OptimizeNightHardwareRamp = true;
            var compiled = NightModeRampCompiler.Compile(
                source.R, source.G, source.B, settings, isHdr: true, 200.0);
            int firstHeadroomKnot = (int)Math.Floor(
                TransferFunctions.PqInverseEotf(200.0) * 255.0) + 1;

            var sourceR = NativeGammaRamp.BuildRampChannel(source.R);
            var sourceG = NativeGammaRamp.BuildRampChannel(source.G);
            var sourceB = NativeGammaRamp.BuildRampChannel(source.B);
            var compiledR = NativeGammaRamp.BuildRampChannel(compiled.R);
            var compiledG = NativeGammaRamp.BuildRampChannel(compiled.G);
            var compiledB = NativeGammaRamp.BuildRampChannel(compiled.B);
            for (int i = firstHeadroomKnot; i < 256; i++)
            {
                Assert.Equal(sourceR[i], compiledR[i]);
                Assert.Equal(sourceG[i], compiledG[i]);
                Assert.Equal(sourceB[i], compiledB[i]);
            }
        }

        [Fact]
        public void PerceptualArcFade_IsExactMonotoneAndSmootherThanMiredClock()
        {
            const int steps = 24;
            var arc = new int[steps + 1];
            var mired = new int[steps + 1];
            Parallel.For(0, steps + 1, i =>
            {
                double p = i / (double)steps;
                arc[i] = NightFadeTrajectory.InterpolateKelvin(
                    6500, 2200, p, NightModeAlgorithm.Perceptual, 0.8, false, false);
                mired[i] = NightModeService.InterpolateKelvinInMired(6500, 2200, p);
            });

            Assert.Equal(6500, arc[0]);
            Assert.Equal(2200, arc[^1]);
            for (int i = 1; i < arc.Length; i++)
            {
                Assert.InRange(arc[i], 2200, 6500);
                Assert.True(arc[i] <= arc[i - 1]);
            }

            double arcVariation = SegmentCoefficientOfVariation(arc);
            double miredVariation = SegmentCoefficientOfVariation(mired);
            Assert.True(arcVariation < miredVariation,
                $"arc CV {arcVariation:F4} should beat mired CV {miredVariation:F4}");
        }

        [Fact]
        public void ContentEstimateCanOnlyRelaxDoseRelativeToConservativeWhiteFallback()
        {
            var spectra = MelanopicCalculator.GenericPrimaries();
            var white = CircadianDoseGovernor.Solve(
                spectra, NightModeAlgorithm.Perceptual, 0.8, false, false,
                3600, 100, 200, 0.2, 12.0);
            var darkWarmContent = CircadianDoseGovernor.Solve(
                spectra, NightModeAlgorithm.Perceptual, 0.8, false, false,
                3600, 100, 200, 0.2, 12.0,
                contentLinearRgb: (0.08, 0.04, 0.02));

            Assert.True(darkWarmContent.BrightnessPercent >= white.BrightnessPercent);
            Assert.True(darkWarmContent.Kelvin >= white.Kelvin ||
                        darkWarmContent.BrightnessPercent > white.BrightnessPercent);
            Assert.True(!darkWarmContent.Adjusted || darkWarmContent.CeilingMet);
        }

        [Fact]
        public async Task FadeService_ConcurrentMutationReentrancyAndDispose_DoNotDeadlock()
        {
            var settings = new NightModeSettings
            {
                Enabled = true,
                ManualOverrideEnabled = true,
                TemperatureKelvin = 3000
            };
            var service = new NightModeService(settings);
            int nested = 0;
            service.BlendChanged += _ =>
            {
                if (System.Threading.Interlocked.Exchange(ref nested, 1) == 0)
                    service.UpdateSettings(settings);
            };

            var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    var clone = NightModeService.CloneSettings(settings);
                    clone.TemperatureKelvin = 2500 + (worker * 100 + i) % 2000;
                    service.UpdateSettings(clone);
                    _ = service.CurrentNightKelvin;
                }
            })).ToArray();

            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            service.UpdateSettings(settings);
            service.PauseUntil(DateTime.Now.AddMinutes(1));
            service.Start();
            service.Stop();
            service.Refresh();
            service.Dispose();
        }

        [Fact]
        public async Task FadeSubscriber_CanWaitForConcurrentPublisher_WithoutLockCycle()
        {
            var settings = new NightModeSettings
            {
                Enabled = true,
                ManualOverrideEnabled = true,
                TemperatureKelvin = 3000
            };
            using var service = new NightModeService(settings);
            int first = 0;
            service.BlendChanged += _ =>
            {
                if (System.Threading.Interlocked.Exchange(ref first, 1) == 0)
                {
                    var concurrent = NightModeService.CloneSettings(settings);
                    concurrent.TemperatureKelvin = 2800;
                    Task.Run(() => service.UpdateSettings(concurrent)).GetAwaiter().GetResult();
                }
            };

            await Task.Run(() => service.UpdateSettings(settings))
                .WaitAsync(TimeSpan.FromSeconds(3));
        }

        private static CalibrationSettings WarmSettings() => new()
        {
            Temperature = (2700 - 6500) / 70.0,
            Algorithm = NightModeAlgorithm.Perceptual,
            PerceptualStrength = 0.8,
            Brightness = 72,
            PreserveNightLuminance = false,
            OptimizeNightHardwareRamp = true,
            HarvestNightSubJndBudget = true
        };

        private static double SegmentCoefficientOfVariation(int[] kelvins)
        {
            var white = new CieXyz(ColorMath.D65White.X * 100, 100, ColorMath.D65White.Z * 100);
            var vc = Cam16Ucs.DisplayConditions(white);
            var distances = new double[kelvins.Length - 1];
            for (int i = 0; i < distances.Length; i++)
            {
                distances[i] = Cam16Ucs.DeltaEPrime(WhiteJab(kelvins[i]), WhiteJab(kelvins[i + 1]));
            }
            double mean = distances.Average();
            double variance = distances.Select(x => (x - mean) * (x - mean)).Average();
            return Math.Sqrt(variance) / Math.Max(mean, 1e-12);

            Cam16Ucs.JabPrime WhiteJab(int kelvin)
            {
                double scale = (kelvin - 6500) / 70.0;
                var gains = ColorAdjustments.GetTemperatureMultipliers(
                    scale, NightModeAlgorithm.Perceptual, false, 0.8, null, NightBasis.Srgb);
                var xyz = ColorMath.LinearSrgbToXyz(new LinearRgb(gains.R, gains.G, gains.B));
                return Cam16Ucs.ToJabPrime(new CieXyz(xyz.X * 100, xyz.Y * 100, xyz.Z * 100), vc);
            }
        }
    }
}
