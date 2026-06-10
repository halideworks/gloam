using System;
using System.IO;
using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Byte-level verification that the MHC2 builder writes the gamut matrix and per-channel
    /// tone LUTs into the correct places, by patching a real template and re-parsing the
    /// output. This can't confirm Windows renders it correctly (only a screen can), but it
    /// proves the binary structure round-trips.
    /// </summary>
    public class Mhc2ProfileBuilderTests
    {
        private static string? FindTemplate()
        {
            // Walk up from the test output dir to the repo root where the .icm templates live.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "srgb_to_gamma2p2_200_mhc2.icm");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static int ReadU32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        private static double ReadS15(byte[] b, int o)
        {
            int v = ReadU32(b, o);
            return v / 65536.0;
        }
        private static int FindMhc2(byte[] b)
        {
            int tagCount = ReadU32(b, 128);
            for (int i = 0; i < tagCount; i++)
            {
                int e = 132 + i * 12;
                if (ReadU32(b, e) == 0x4D484332) return ReadU32(b, e + 4);
            }
            throw new InvalidOperationException("no MHC2 tag");
        }

        [Fact]
        public void Build_PatchesMatrixAndLuts_RoundTrips()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            // Distinctive matrix and a gamma-ish LUT per channel.
            var matrix = new double[,] { { 1.05, -0.03, 0.01 }, { 0.02, 0.94, 0.04 }, { -0.01, 0.05, 1.18 } };
            var lutR = new double[1024];
            var lutG = new double[1024];
            var lutB = new double[1024];
            for (int i = 0; i < 1024; i++)
            {
                double v = i / 1023.0;
                lutR[i] = Math.Pow(v, 1.0 / 2.2);
                lutG[i] = v;                       // identity
                lutB[i] = Math.Pow(v, 2.2);
            }

            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_test_{Guid.NewGuid():N}.icm");
            try
            {
                Mhc2ProfileBuilder.Build(template, outPath, matrix, lutR, lutG, lutB);
                var b = File.ReadAllBytes(outPath);
                int t = FindMhc2(b);
                int matrixOff = t + ReadU32(b, t + 20);
                int lut0 = t + ReadU32(b, t + 24);
                int lut1 = t + ReadU32(b, t + 28);
                int lut2 = t + ReadU32(b, t + 32);

                // Matrix round-trips (within s15Fixed16 precision).
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                        Assert.Equal(matrix[r, c], ReadS15(b, matrixOff + (r * 4 + c) * 4), 4);
                    Assert.Equal(0.0, ReadS15(b, matrixOff + (r * 4 + 3) * 4), 5); // offset column
                }

                // LUT samples round-trip (data starts after the 8-byte 'sf32' header).
                foreach (int idx in new[] { 0, 256, 512, 768, 1023 })
                {
                    Assert.Equal(lutR[idx], ReadS15(b, lut0 + 8 + idx * 4), 4);
                    Assert.Equal(lutG[idx], ReadS15(b, lut1 + 8 + idx * 4), 4);
                    Assert.Equal(lutB[idx], ReadS15(b, lut2 + 8 + idx * 4), 4);
                }

                // Profile ID was zeroed (we changed the body).
                for (int z = 84; z < 100; z++) Assert.Equal(0, b[z]);
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void BuildGamutMatrix_IdentityWhenDisplayMatchesTarget()
        {
            // If the display's measured matrix equals the target's, the gamut correction is identity.
            var target = StandardTargets.SrgbGamma22;
            var characterization = new DisplayCharacterization { RgbToXyzMatrix = target.RgbToXyzMatrix };

            var m = Mhc2ProfileBuilder.BuildGamutMatrix(characterization, target);
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(r == c ? 1.0 : 0.0, m[r, c], 4);
        }
    }
}
