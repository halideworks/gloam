using System.Text.Json;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests;

public class ColorSerializationTests
{
    [Fact]
    public void Chromaticity_RoundTripPreservesCoordinates() =>
        RoundTrip(new Chromaticity(0.33, 0.34));
    [Fact]
    public void Xyz_RoundTripPreservesMeasuredLight() =>
        RoundTrip(new CieXyz(31, 42, 27));
    [Fact]
    public void Lab_RoundTripPreservesColor() =>
        RoundTrip(new CieLab(50, 12, -9));
    [Fact]
    public void LinearRgb_RoundTripPreservesPatch() =>
        RoundTrip(new LinearRgb(0.2, 0.4, 0.7));

    private static void RoundTrip<T>(T value) where T : struct =>
        Assert.Equal(value, JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value)));
}
