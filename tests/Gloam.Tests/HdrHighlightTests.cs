using Gloam.Core;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests;

public class HdrHighlightTests
{
    [Theory]
    [InlineData(GammaMode.Gamma22)]
    [InlineData(GammaMode.Gamma24)]
    public void FullBrightness_PreservesEveryHdrHighlight(GammaMode mode)
    {
        foreach (double white in new[] { 80.0, 200.0, 480.0, 1000.0 })
        {
            var lut = LutGenerator.GenerateLut(mode, white);
            AssertHighlightIdentity(lut, white);

            // Check the actual 256-entry, 16-bit upload, including Windows' envelope.
            var ramp = NativeGammaRamp.BuildRampChannel(lut, applyGdiEnvelope: true, out bool clamped);
            Assert.False(clamped);
            for (int i = 0; i < ramp.Length; i++)
            {
                // Both source interpolation knots must be above SDR white.
                int lower = (int)(i / 255.0 * (lut.Length - 1));
                if (TransferFunctions.PqEotf(lower / 1023.0) >= white)
                    Assert.Equal((ushort)(i * 257), ramp[i]);
            }
        }
    }

    [Theory]
    [InlineData(2.2)]
    [InlineData(2.4)]
    public void MeasuredResponse_DoesNotCompressUnmeasuredHdrHeadroom(double gamma)
    {
        const double white = 200.0;
        var display = new DisplayCharacterization
        {
            PeakLuminance = white,
            BlackLevel = 0.0,
            MeasuredGamma = 2.2
        };
        var lut = LutGenerator.GenerateCalibratedLut(
            gamma, display, CalibrationSettings.Default, white, isHdr: true);

        foreach (var channel in new[] { lut.R, lut.G, lut.B, lut.Grey })
            AssertHighlightIdentity(channel, white);
    }

    [Theory]
    [InlineData(GammaMode.WindowsDefault, false)]
    [InlineData(GammaMode.Gamma22, false)]
    [InlineData(GammaMode.Gamma24, false)]
    [InlineData(GammaMode.WindowsDefault, true)]
    [InlineData(GammaMode.Gamma22, true)]
    [InlineData(GammaMode.Gamma24, true)]
    public void Dimming_PreservesHdrRangeUsingOnlyRequestedBrightness(GammaMode mode, bool linearDimming)
    {
        const double white = 200.0;
        foreach (double brightness in new[] { 10.0, 50.0, 80.0 })
        {
            var lut = LutGenerator.GenerateLut(mode, white, new CalibrationSettings
            {
                Brightness = brightness,
                UseLinearBrightness = linearDimming
            });
            for (int i = 1; i < lut.R.Length; i++)
            {
                double nits = TransferFunctions.PqEotf(i / 1023.0);
                if (nits < white) continue;

                double expected = ColorAdjustments.ApplyDimmingNits(nits, brightness, white, linearDimming);
                double actual = TransferFunctions.PqEotf(lut.R[i]);
                Assert.True(Math.Abs(actual - expected) <= Math.Max(1e-8, expected * 1e-9),
                    $"{mode}, {brightness}%: {nits:F3} nits became {actual:F3}, expected {expected:F3}");
                Assert.True(lut.R[i] > lut.R[i - 1], $"Highlight detail lost at sample {i}");
            }
        }
    }

    [Theory]
    [InlineData(GammaMode.Gamma22)]
    [InlineData(GammaMode.Gamma24)]
    public void ShadowOnlyAdjustment_LeavesHdrHighlightsUntouched(GammaMode mode)
    {
        var lut = LutGenerator.GenerateLut(mode, 200.0, new CalibrationSettings
        {
            ShadowDetailStrength = 0.55,
            ShadowDetailPivot = 0.10
        });
        foreach (var channel in new[] { lut.R, lut.G, lut.B, lut.Grey })
            AssertHighlightIdentity(channel, 200.0);
    }

    private static void AssertHighlightIdentity(double[] lut, double white)
    {
        for (int i = 1; i < lut.Length; i++)
        {
            double input = i / 1023.0;
            double nits = TransferFunctions.PqEotf(input);
            if (nits < white) continue;

            Assert.True(Math.Abs(lut[i] - input) < 1e-12,
                $"White {white} nits: HDR input {nits:F3} became {TransferFunctions.PqEotf(lut[i]):F3} nits");
            Assert.True(lut[i] > lut[i - 1], $"Highlight detail lost at sample {i}");
        }
    }
}
