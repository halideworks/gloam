using System;
using System.IO;
using Gloam.Core.Calibration;
using Xunit;

namespace Gloam.Tests;

public class LutBinaryValidationTests
{
    [Fact]
    public void TruncatedLargeLut_IsRejectedBeforeAllocatingColorTables()
    {
        byte[] header = new byte[17];
        using (var stream = new MemoryStream(header))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new byte[] { (byte)'L', (byte)'U', (byte)'T', (byte)'3', 1 });
            writer.Write(128);
            writer.Write(0f);
            writer.Write(1f);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => Lut3D.FromBytes(header));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000_000,
            "Malformed input must be rejected before allocating the declared LUT.");
    }

    [Fact]
    public void Serialization_AllocatesOnlyOneLargePayloadBuffer()
    {
        var lut = new Lut3D(65);
        _ = new Lut3D(2).ToBytes(); // Warm the serializer before measuring allocation.
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] data = lut.ToBytes();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, data.LongLength, data.LongLength + 65_536);
    }

    [Fact]
    public void BinaryRoundTrip_PreservesDomainAndEntries()
    {
        var source = new Lut3D(3) { DomainMin = -1, DomainMax = 2 };
        source.SetEntry(1, 2, 0, 0.123f, 0.456f, 0.789f);
        byte[] data = source.ToBytes();
        Assert.Equal(17 + 12 * 27, data.Length);
        var result = Lut3D.FromBytes(data);
        Assert.Equal(source.DomainMin, result.DomainMin);
        Assert.Equal(source.DomainMax, result.DomainMax);
        Assert.Equal(source.GetEntry(1, 2, 0), result.GetEntry(1, 2, 0));
    }
}
