using System;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationTargetTests
    {
        [Fact]
        public void StandardTargets_All_ExposesOnlyProductionHdrDesktopTarget()
        {
            Assert.Contains(StandardTargets.Rec709Pq, StandardTargets.All);
            Assert.DoesNotContain(StandardTargets.Rec2020Pq, StandardTargets.All);
            Assert.DoesNotContain(StandardTargets.Rec2020Hlg, StandardTargets.All);
            Assert.DoesNotContain(StandardTargets.P3Pq, StandardTargets.All);

            var hdrTargets = StandardTargets.All.Where(t => t.IsHdr).ToList();
            var target = Assert.Single(hdrTargets);
            Assert.Same(StandardTargets.Rec709Pq, target);
        }

        [Fact]
        public void StandardTargets_GetByName_ResolvesHdrDesktopToSupportedPqTarget()
        {
            var target = StandardTargets.GetByName("HDR Desktop");

            Assert.Same(StandardTargets.Rec709Pq, target);
            Assert.Equal(Chromaticity.Rec709Red, target!.RedPrimary);
            Assert.Equal(TransferFunctionType.Pq, target.TransferFunction);
        }

        [Fact]
        public void ApplyTransferFunctions_NonFiniteInputs_ReturnFiniteBoundedValues()
        {
            foreach (var target in StandardTargets.All.Concat(new[]
                     {
                         StandardTargets.Rec2020Pq,
                         StandardTargets.Rec2020Hlg,
                         StandardTargets.P3Pq
                     }))
            {
                AssertTransferOutputIsFiniteAndBounded(target.ApplyOetf(double.NaN), target.Name);
                AssertTransferOutputIsFiniteAndBounded(target.ApplyEotf(double.PositiveInfinity), target.Name);
            }
        }

        [Fact]
        public void CustomTarget_NonFiniteGammaAndPeak_FallBackToSafeTransferParameters()
        {
            var gammaTarget = StandardTargets.CreateNative(
                Chromaticity.Rec709Red,
                Chromaticity.Rec709Green,
                Chromaticity.Rec709Blue,
                Chromaticity.D65,
                gamma: double.NaN);

            Assert.Equal(Math.Pow(0.5, 2.2), gammaTarget.ApplyEotf(0.5), 10);

            var pqTarget = new CalibrationTarget
            {
                Name = "Corrupt PQ",
                RedPrimary = Chromaticity.Rec709Red,
                GreenPrimary = Chromaticity.Rec709Green,
                BluePrimary = Chromaticity.Rec709Blue,
                WhitePoint = Chromaticity.D65,
                TransferFunction = TransferFunctionType.Pq,
                PeakLuminance = double.NaN
            };

            Assert.Equal(1.0, pqTarget.ApplyEotf(1.0), 10);
        }

        [Fact]
        public void PqDesktopPatchTargets_UseSdrSrgbContentCurve()
        {
            var patches = PatchSetGenerator.GeneratePatchSet(
                StandardTargets.Rec709Pq,
                PatchSetGenerator.CalibrationPreset.GrayscaleOnly);

            var midGray = patches.Single(p => p.Name == "Gray 50%");

            // Patch signals are snapped to the 8-bit grid at generation time (M9), so the
            // target is computed from the snapped stimulus (128/255), not the ideal 0.5.
            double snapped = PatchSetGenerator.Snap8Bit(0.5);
            Assert.Equal(128.0 / 255.0, snapped, 12);
            Assert.NotNull(midGray.TargetXyz);
            Assert.Equal(ColorMath.SrgbEotf(snapped), midGray.TargetXyz!.Value.Y, 10);
            Assert.NotEqual(StandardTargets.Rec709Pq.ApplyEotf(snapped), midGray.TargetXyz.Value.Y, 6);
        }

        private static void AssertTransferOutputIsFiniteAndBounded(double value, string targetName)
        {
            Assert.True(double.IsFinite(value), $"{targetName} produced non-finite transfer output");
            Assert.InRange(value, 0.0, 1.0);
        }
    }
}
