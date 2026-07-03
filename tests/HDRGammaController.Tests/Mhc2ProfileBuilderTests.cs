using System;
using System.IO;
using System.Linq;
using Xunit;
using HDRGammaController.Core;
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
        public void Build_PatchesProfileDescription_AsMlucEnUs()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            // Longer than the template's 42-char allocation, to exercise the append path.
            const string name = "M27Q P - Display P3 G2.2 - 2026-06-10 1142 (test)";
            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = new double[1024];
            for (int i = 0; i < 1024; i++) lut[i] = i / 1023.0;

            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_desc_{Guid.NewGuid():N}.icm");
            try
            {
                Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut, name);
                var b = File.ReadAllBytes(outPath);

                // Header profile-size field must match the (grown) file.
                Assert.Equal(b.Length, ReadU32(b, 0));

                // Find the desc tag and parse it as mluc/enUS.
                int tagCount = ReadU32(b, 128);
                int off = -1, size = 0;
                for (int i = 0; i < tagCount; i++)
                {
                    int e = 132 + i * 12;
                    if (ReadU32(b, e) == 0x64657363) { off = ReadU32(b, e + 4); size = ReadU32(b, e + 8); break; }
                }
                Assert.True(off > 0, "no desc tag");
                Assert.Equal(0x6D6C7563, ReadU32(b, off));         // 'mluc'
                Assert.Equal(1, ReadU32(b, off + 8));              // one record
                int strLen = ReadU32(b, off + 20);
                int strOff = ReadU32(b, off + 24);
                Assert.Equal(28 + strLen, size);
                string text = System.Text.Encoding.BigEndianUnicode.GetString(b, off + strOff, strLen);
                Assert.Equal(name, text);
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void Build_PatchesMinMaxLuminance_AndLumiTag()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = new double[1024];
            for (int i = 0; i < 1024; i++) lut[i] = i / 1023.0;

            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_lumi_{Guid.NewGuid():N}.icm");
            try
            {
                Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut,
                    minLuminanceNits: 0.0512, maxLuminanceNits: 437.5);
                var b = File.ReadAllBytes(outPath);
                int t = FindMhc2(b);
                Assert.Equal(0.0512, ReadS15(b, t + 12), 3);
                Assert.Equal(437.5, ReadS15(b, t + 16), 3);

                // lumi tag Y must carry the peak as well.
                int tagCount = ReadU32(b, 128);
                for (int i = 0; i < tagCount; i++)
                {
                    int e = 132 + i * 12;
                    if (ReadU32(b, e) != 0x6C756D69) continue; // 'lumi'
                    int off = ReadU32(b, e + 4);
                    Assert.Equal(437.5, ReadS15(b, off + 12), 3);
                    return;
                }
                // Template without a lumi tag would also be acceptable — but ours has one.
                Assert.Fail("template has no lumi tag");
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void Build_RejectsNonFiniteLuminanceMetadata()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = IdentityLut();
            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_bad_lumi_{Guid.NewGuid():N}.icm");

            try
            {
                Assert.Throws<ArgumentException>(() =>
                    Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut,
                        minLuminanceNits: double.NaN, maxLuminanceNits: 500));
                Assert.Throws<ArgumentException>(() =>
                    Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut,
                        minLuminanceNits: 0.01, maxLuminanceNits: double.PositiveInfinity));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut,
                        minLuminanceNits: -0.01, maxLuminanceNits: 500));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut,
                        minLuminanceNits: 500, maxLuminanceNits: 100));
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut,
                        minLuminanceNits: 0.01, maxLuminanceNits: 40000));
                Assert.False(File.Exists(outPath));
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void Build_RejectsNonFiniteMatrix()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            var matrix = new double[,] { { 1, 0, 0 }, { 0, double.NaN, 0 }, { 0, 0, 1 } };
            var lut = IdentityLut();
            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_bad_matrix_{Guid.NewGuid():N}.icm");

            try
            {
                Assert.Throws<ArgumentException>(() =>
                    Mhc2ProfileBuilder.Build(template, outPath, matrix, lut, lut, lut));
                Assert.False(File.Exists(outPath));
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void Build_RejectsInvalidToneLut()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var good = IdentityLut();
            var bad = IdentityLut();
            bad[500] = bad[499] - 0.01;
            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_bad_lut_{Guid.NewGuid():N}.icm");

            try
            {
                Assert.Throws<ArgumentException>(() =>
                    Mhc2ProfileBuilder.Build(template, outPath, identity, bad, good, good));
                Assert.False(File.Exists(outPath));
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

        [Fact]
        public void ScaledAbsoluteMatrix_HitsTargetWhite_WithoutClipping_AndKeepsPrimaries()
        {
            // The absolute matrix + UNIFORM scale must reproduce the target white chromaticity
            // (the whole point of white-point calibration), keep every drive value at or
            // below full scale, AND leave primary chromaticities exact — per-channel gains
            // here would re-tint them (the verified-primaries regression on the first HDR run).
            var target = StandardTargets.SrgbGamma22;
            var coolWhite = new Chromaticity(0.297, 0.318); // measured M27Q-like cool white
            var displayMatrix = ColorMath.CalculateRgbToXyzMatrix(
                new Chromaticity(0.64, 0.33), new Chromaticity(0.30, 0.60), new Chromaticity(0.15, 0.06), coolWhite);
            var characterization = new DisplayCharacterization
            {
                RgbToXyzMatrix = displayMatrix,
                WhitePoint = coolWhite,
            };

            var m = Mhc2ProfileBuilder.BuildGamutMatrix(characterization, target);
            double maxDrive = 0;
            foreach (var content in new[] { (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 1.0, 1.0) })
                foreach (double c in MulVec(m, content.Item1, content.Item2, content.Item3))
                    maxDrive = Math.Max(maxDrive, c);
            var scaled = Mhc2ProfileBuilder.ScaleMatrix(m, Mhc2ProfileBuilder.UniformScale(maxDrive));

            // White: lands on D65 chromaticity, limiting channel exactly at full scale.
            double[] whiteDrive = MulVec(scaled, 1, 1, 1);
            Assert.True(whiteDrive.All(c => c <= 1.0 + 1e-9), "white drive must not clip");
            double[] whiteXyz = MulVec(displayMatrix, whiteDrive[0], whiteDrive[1], whiteDrive[2]);
            var pw = new CieXyz(whiteXyz[0], whiteXyz[1], whiteXyz[2]).ToChromaticity();
            Assert.Equal(target.WhitePoint.X, pw.X, 3);
            Assert.Equal(target.WhitePoint.Y, pw.Y, 3);

            // Primary: content red must land exactly on the target red chromaticity.
            double[] redDrive = MulVec(scaled, 1, 0, 0);
            Assert.True(redDrive.All(c => c <= 1.0 + 1e-9), "red drive must not clip");
            double[] redXyz = MulVec(displayMatrix, redDrive[0], redDrive[1], redDrive[2]);
            var pr = new CieXyz(redXyz[0], redXyz[1], redXyz[2]).ToChromaticity();
            Assert.Equal(target.RedPrimary.X, pr.X, 3);
            Assert.Equal(target.RedPrimary.Y, pr.Y, 3);
        }

        /// <summary>
        /// Models the Windows MHC2 engine: the tag's matrix is applied between FIXED sRGB↔XYZ
        /// conversions (wire = xyzToSrgb · M_tag · srgbToXyz · linear content).
        /// </summary>
        private static double[,] EngineNetTransform(double[,] tagMatrix)
        {
            var srgbToXyz = ColorMath.CalculateRgbToXyzMatrix(
                Chromaticity.Rec709Red, Chromaticity.Rec709Green, Chromaticity.Rec709Blue, Chromaticity.D65);
            return ColorMath.MultiplyMatrices(ColorMath.Invert3x3(srgbToXyz),
                ColorMath.MultiplyMatrices(tagMatrix, srgbToXyz));
        }

        private static double[] IdentityLut()
        {
            var lut = new double[1024];
            for (int i = 0; i < lut.Length; i++) lut[i] = i / 1023.0;
            return lut;
        }

        [Fact]
        public void ToMhc2MatrixDomain_IdentityWrap_RoundTripsToIdentity()
        {
            // m8 regression pin: the MHC2 engine sandwich is UNDOCUMENTED Windows behavior
            // (validated on-screen against the Windows 11 22621 DWM). Wrapping an identity
            // calibration matrix must return identity within numeric noise — the wrap's
            // srgbToXyz and its inverse are built from the same D65/sRGB constants, so any
            // drift in those constants (or an asymmetric wrap) shows up here immediately.
            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var wrapped = Mhc2ProfileBuilder.ToMhc2MatrixDomain(identity);
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.True(Math.Abs(wrapped[r, c] - (r == c ? 1.0 : 0.0)) < 1e-6,
                        $"identity wrap drifted at [{r},{c}]: {wrapped[r, c]:E3}");
        }

        [Fact]
        public void ToMhc2MatrixDomain_EngineSandwichRecoversIntendedRgbMatrix()
        {
            // Wrapping must make the engine's fixed conversions cancel exactly, so the engine
            // applies precisely the intended RGB→RGB correction.
            var intended = new double[,] { { 0.88, 0.20, 0.01 }, { 0.05, 0.93, -0.001 }, { 0.01, 0.04, 0.86 } };
            var engineNet = EngineNetTransform(Mhc2ProfileBuilder.ToMhc2MatrixDomain(intended));
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(intended[r, c], engineNet[r, c], 6);
        }

        [Fact]
        public void EngineModel_WhiteLandsOnD65_WithMeasuredM27QPanel()
        {
            // Regression for the magenta cast (2026-06-10): the M27Q P's actual measured
            // primaries/white. Writing the RGB→RGB matrix RAW into the tag made the engine
            // render white as (R 1.39→clip, G 0.87, B 0.91) — blown red, crushed green =
            // strong magenta. With the wrapped, uniformly-scaled absolute matrix the engine
            // must drive white WITHOUT clipping, and the panel must then show D65.
            var measured = ColorMath.CalculateRgbToXyzMatrix(
                new Chromaticity(0.6890, 0.3056), new Chromaticity(0.2569, 0.6714),
                new Chromaticity(0.1472, 0.0590), new Chromaticity(0.2992, 0.3216));
            var characterization = new DisplayCharacterization
            {
                RgbToXyzMatrix = measured,
                WhitePoint = new Chromaticity(0.2992, 0.3216),
            };
            var target = StandardTargets.Rec709Gamma24;

            var abs = Mhc2ProfileBuilder.BuildGamutMatrix(characterization, target);
            double maxDrive = 0;
            foreach (var content in new[] { (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 1.0, 1.0) })
                foreach (double c in MulVec(abs, content.Item1, content.Item2, content.Item3))
                    maxDrive = Math.Max(maxDrive, c);
            var scaled = Mhc2ProfileBuilder.ScaleMatrix(abs, Mhc2ProfileBuilder.UniformScale(maxDrive));
            var tag = Mhc2ProfileBuilder.ToMhc2MatrixDomain(scaled);

            double[] wire = MulVec(EngineNetTransform(tag), 1.0, 1.0, 1.0);
            Assert.True(wire.All(c => c is >= 0 and <= 1.0 + 1e-9),
                $"white drive must not clip: ({wire[0]:F3}, {wire[1]:F3}, {wire[2]:F3})");

            double[] producedXyz = MulVec(measured, wire[0], wire[1], wire[2]);
            var p = new CieXyz(producedXyz[0], producedXyz[1], producedXyz[2]).ToChromaticity();
            Assert.Equal(target.WhitePoint.X, p.X, 3);
            Assert.Equal(target.WhitePoint.Y, p.Y, 3);
        }

        private static double[] MulVec(double[,] m, double a, double b, double c) => new[]
        {
            m[0,0]*a + m[0,1]*b + m[0,2]*c,
            m[1,0]*a + m[1,1]*b + m[1,2]*c,
            m[2,0]*a + m[2,1]*b + m[2,2]*c,
        };

        // ---- True ICC characterization tags (M6) ---------------------------------------

        private static int FindTag(byte[] b, int sig, out int size)
        {
            int tagCount = ReadU32(b, 128);
            for (int i = 0; i < tagCount; i++)
            {
                int e = 132 + i * 12;
                if (ReadU32(b, e) == sig) { size = ReadU32(b, e + 8); return ReadU32(b, e + 4); }
            }
            size = 0;
            return -1;
        }

        private static double[] ReadXyzTag(byte[] b, int sig)
        {
            int off = FindTag(b, sig, out _);
            Assert.True(off > 0, $"tag 0x{sig:X8} missing");
            return new[] { ReadS15(b, off + 8), ReadS15(b, off + 12), ReadS15(b, off + 16) };
        }

        private static double[,] ReadChad(byte[] b)
        {
            int off = FindTag(b, 0x63686164, out _);
            Assert.True(off > 0, "chad tag missing");
            var m = new double[3, 3];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    m[r, c] = ReadS15(b, off + 8 + (r * 3 + c) * 4);
            return m;
        }

        private static Chromaticity XyzToXy(double[] xyz)
        {
            double sum = xyz[0] + xyz[1] + xyz[2];
            return new Chromaticity(xyz[0] / sum, xyz[1] / sum);
        }

        [Fact]
        public void Build_WritesTrueCharacterizationTags_ForP3Target()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            // A P3-primaries / D65-white / pure-gamma-2.2 target: previously the installed
            // profile still described the display as sRGB/G2.2 with the template's synthetic
            // values regardless (the M6 finding). Round-trip every synthesized tag.
            var target = StandardTargets.P3D65Gamma22;
            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = IdentityLut();

            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_char_{Guid.NewGuid():N}.icm");
            try
            {
                Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut,
                    description: "M27Q P - Display P3 G2.2 - characterization test",
                    colorimetry: target, lumiPeakNits: 412.5);
                var b = File.ReadAllBytes(outPath);

                // Header profile-size must track the grown file (TRC + desc appends).
                Assert.Equal(b.Length, ReadU32(b, 0));

                // wtpt = the PCS illuminant D50 (ICC v4: media white is stored ADAPTED).
                var wtpt = ReadXyzTag(b, 0x77747074);
                Assert.Equal(0.96422, wtpt[0], 3);
                Assert.Equal(1.00000, wtpt[1], 3);
                Assert.Equal(0.82521, wtpt[2], 3);

                // chad carries Bradford(D65 → D50): its inverse must take the stored wtpt
                // back to the target's actual white within chromaticity tolerance.
                var chad = ReadChad(b);
                var chadInv = ColorMath.Invert3x3(chad);
                double[] deviceWhite = MulVec(chadInv, wtpt[0], wtpt[1], wtpt[2]);
                var whiteXy = XyzToXy(deviceWhite);
                Assert.True(Math.Abs(whiteXy.X - target.WhitePoint.X) < 1e-3, $"white x {whiteXy.X:F5}");
                Assert.True(Math.Abs(whiteXy.Y - target.WhitePoint.Y) < 1e-3, $"white y {whiteXy.Y:F5}");

                // rXYZ/gXYZ/bXYZ are PCS-adapted primaries: inverse-chad recovers the device
                // chromaticities, which must land on P3 within 1e-3.
                var expected = new[]
                {
                    (Sig: 0x7258595A, Xy: target.RedPrimary),
                    (Sig: 0x6758595A, Xy: target.GreenPrimary),
                    (Sig: 0x6258595A, Xy: target.BluePrimary),
                };
                double sumY = 0;
                foreach (var (sig, xy) in expected)
                {
                    var pcs = ReadXyzTag(b, sig);
                    double[] device = MulVec(chadInv, pcs[0], pcs[1], pcs[2]);
                    var deviceXy = XyzToXy(device);
                    Assert.True(Math.Abs(deviceXy.X - xy.X) < 1e-3, $"{sig:X8} x: {deviceXy.X:F5} vs {xy.X:F5}");
                    Assert.True(Math.Abs(deviceXy.Y - xy.Y) < 1e-3, $"{sig:X8} y: {deviceXy.Y:F5} vs {xy.Y:F5}");
                    sumY += device[1];
                }
                // The three device-relative primary luminances must sum to the white Y (=1).
                Assert.Equal(1.0, sumY, 2);

                // TRC: pure-gamma target → shared 'curv' gamma tag on all three channels.
                int rTrc = FindTag(b, 0x72545243, out int rTrcSize);
                int gTrc = FindTag(b, 0x67545243, out int gTrcSize);
                int bTrc = FindTag(b, 0x62545243, out int bTrcSize);
                Assert.True(rTrc > 0 && rTrc == gTrc && rTrc == bTrc, "TRC entries must share one block");
                Assert.Equal(rTrcSize, gTrcSize);
                Assert.Equal(rTrcSize, bTrcSize);
                Assert.Equal(0x63757276, ReadU32(b, rTrc));      // 'curv'
                Assert.Equal(1, ReadU32(b, rTrc + 8));           // count=1: u8Fixed8 gamma
                double gammaStored = ((b[rTrc + 12] << 8) | b[rTrc + 13]) / 256.0;
                Assert.Equal(2.2, gammaStored, 2);

                // lumi carries the measured peak (patched via lumiPeakNits, SDR path).
                int lumi = FindTag(b, 0x6C756D69, out _);
                Assert.True(lumi > 0, "lumi tag missing");
                Assert.Equal(412.5, ReadS15(b, lumi + 12), 3);

                // chrm (informational) carries the raw device chromaticities.
                int chrm = FindTag(b, 0x6368726D, out _);
                Assert.True(chrm > 0, "chrm tag missing");
                for (int i = 0; i < 3; i++)
                {
                    double x = (uint)ReadU32(b, chrm + 12 + i * 8) / 65536.0;
                    double y = (uint)ReadU32(b, chrm + 12 + i * 8 + 4) / 65536.0;
                    var xy = expected[i].Xy;
                    Assert.True(Math.Abs(x - xy.X) < 1e-4 && Math.Abs(y - xy.Y) < 1e-4,
                        $"chrm[{i}] ({x:F5},{y:F5}) vs ({xy.X:F5},{xy.Y:F5})");
                }

                // Windows-required tag set intact, every entry within the file.
                foreach (int sig in new[]
                {
                    0x64657363 /*desc*/, 0x63707274 /*cprt*/, 0x77747074 /*wtpt*/,
                    0x63686164 /*chad*/, 0x7258595A, 0x6758595A, 0x6258595A,
                    0x72545243, 0x67545243, 0x62545243, 0x6C756D69 /*lumi*/,
                    0x4D484332 /*MHC2*/,
                })
                {
                    int off = FindTag(b, sig, out int size);
                    Assert.True(off > 0 && size > 0 && off + size <= b.Length,
                        $"tag 0x{sig:X8} missing or out of range (off={off}, size={size})");
                }

                // The MHC2 payload still parses and the profile ID was re-zeroed.
                Assert.True(FindMhc2(b) > 0);
                for (int z = 84; z < 100; z++) Assert.Equal(0, b[z]);
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void Build_WritesSampledTrc_ForPiecewiseTargets()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            // sRGB's piecewise EOTF can't be a single gamma value: it must round-trip as a
            // 1024-point sampled 'curv' matching the target EOTF at arbitrary probe points.
            // (SrgbGamma22 is a pure power-2.2 calibration target and gets a gamma 'curv';
            // the piecewise encoding curve lives on SrgbPiecewise.)
            var target = StandardTargets.SrgbPiecewise;
            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = IdentityLut();

            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_trc_{Guid.NewGuid():N}.icm");
            try
            {
                Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut, colorimetry: target);
                var b = File.ReadAllBytes(outPath);

                int trc = FindTag(b, 0x72545243, out int trcSize);
                Assert.True(trc > 0, "rTRC missing");
                Assert.Equal(0x63757276, ReadU32(b, trc));   // 'curv'
                Assert.Equal(1024, ReadU32(b, trc + 8));
                Assert.Equal(12 + 1024 * 2, trcSize);

                foreach (int idx in new[] { 0, 25, 102, 256, 512, 767, 1023 })
                {
                    double stored = ((b[trc + 12 + idx * 2] << 8) | b[trc + 12 + idx * 2 + 1]) / 65535.0;
                    double expected = ColorMath.SrgbEotf(idx / 1023.0);
                    Assert.True(Math.Abs(stored - expected) < 1e-3,
                        $"TRC[{idx}] = {stored:F5}, expected sRGB EOTF {expected:F5}");
                }

                // Device white round trip for an sRGB/D65 target too.
                var chadInv = ColorMath.Invert3x3(ReadChad(b));
                var wtpt = ReadXyzTag(b, 0x77747074);
                var whiteXy = XyzToXy(MulVec(chadInv, wtpt[0], wtpt[1], wtpt[2]));
                Assert.True(Math.Abs(whiteXy.X - Chromaticity.D65.X) < 1e-3);
                Assert.True(Math.Abs(whiteXy.Y - Chromaticity.D65.Y) < 1e-3);

                // Cross-validation against an INDEPENDENT implementation: for an sRGB/D65
                // target our synthesized D50-adapted rXYZ must agree with the template's own
                // (MHC2Gen-produced) sRGB rXYZ — the canonical Bradford-adapted red primary.
                var rXyz = ReadXyzTag(b, 0x7258595A);
                Assert.True(Math.Abs(rXyz[0] - 0.43607) < 2e-3, $"rXYZ.X {rXyz[0]:F5}");
                Assert.True(Math.Abs(rXyz[1] - 0.22249) < 2e-3, $"rXYZ.Y {rXyz[1]:F5}");
                Assert.True(Math.Abs(rXyz[2] - 0.01392) < 2e-3, $"rXYZ.Z {rXyz[2]:F5}");
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void Build_WithoutColorimetry_LeavesTemplateCharacterizationBytes()
        {
            string? template = FindTemplate();
            if (template == null) return; // template not present in this checkout; skip silently

            // Callers that don't opt in (colorimetry: null) must get the template's original
            // characterization tags byte-for-byte — the M6 synthesis is opt-in.
            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = IdentityLut();
            string outPath = Path.Combine(Path.GetTempPath(), $"mhc2_nochar_{Guid.NewGuid():N}.icm");
            try
            {
                Mhc2ProfileBuilder.Build(template, outPath, identity, lut, lut, lut);
                var templateBytes = File.ReadAllBytes(template);
                var b = File.ReadAllBytes(outPath);

                foreach (int sig in new[] { 0x77747074, 0x63686164, 0x7258595A, 0x6758595A, 0x6258595A, 0x72545243 })
                {
                    int tOff = FindTag(templateBytes, sig, out int tSize);
                    int oOff = FindTag(b, sig, out int oSize);
                    Assert.Equal(tOff, oOff);
                    Assert.Equal(tSize, oSize);
                    for (int i = 0; i < tSize; i++)
                        Assert.Equal(templateBytes[tOff + i], b[oOff + i]);
                }
            }
            finally
            {
                try { File.Delete(outPath); } catch { }
            }
        }

        [Fact]
        public void GamutGuard_AllowsP3_BlocksRec2020_OnA98PercentP3Panel()
        {
            // The user's M27Q P measured/EDID primaries (≈ 98% DCI-P3).
            var m27q = ColorMath.CalculateRgbToXyzMatrix(
                new Chromaticity(0.685, 0.309), new Chromaticity(0.265, 0.668),
                new Chromaticity(0.150, 0.058), new Chromaticity(0.3135, 0.329));

            double p3Drive = MaxPrimaryDrive(m27q, StandardTargets.P3D65Gamma22);
            double srgbDrive = MaxPrimaryDrive(m27q, StandardTargets.SrgbGamma22);
            double rec2020Drive = MaxPrimaryDrive(m27q, StandardTargets.Rec2020Gamma24);

            // sRGB/Rec.709 narrows a wide panel — never over the limit.
            Assert.True(GamutReachability.IsReachable(srgbDrive), $"sRGB should be reachable, drive={srgbDrive:F3}");
            // P3 on a 98%-P3 panel is a small reach — must be allowed.
            Assert.True(GamutReachability.IsReachable(p3Drive), $"P3 should be reachable on a 98% P3 panel, drive={p3Drive:F3}");
            // Rec.2020 far exceeds P3 — must be blocked.
            Assert.False(GamutReachability.IsReachable(rec2020Drive), $"Rec.2020 should be unreachable, drive={rec2020Drive:F3}");
        }

        [Fact]
        public void GamutReachability_EdidPathMatchesInstallerPrimaryDriveMetric()
        {
            var edid = new EdidColorInfo
            {
                RedX = 0.685,
                RedY = 0.309,
                GreenX = 0.265,
                GreenY = 0.668,
                BlueX = 0.150,
                BlueY = 0.058,
                WhiteX = 0.3135,
                WhiteY = 0.329
            };

            Assert.True(GamutReachability.TargetFitsEdidGamut(StandardTargets.P3D65Gamma22, edid));
            Assert.False(GamutReachability.TargetFitsEdidGamut(StandardTargets.Rec2020Gamma24, edid));
        }

        [Fact]
        public void GamutReachability_ModestNegativeCrossTermsDoNotBlockReachableNarrowing()
        {
            var reachable = new double[,]
            {
                { 0.82742244, 0.16682524, -0.00132744 },
                { 0.04595092, 0.95192185,  0.00407374 },
                { 0.01244350, 0.05019300,  0.93941203 },
            };

            double drive = GamutReachability.MaxPrimaryDrive(reachable);

            Assert.InRange(drive, 0.95, 0.96);
            Assert.True(GamutReachability.IsReachable(drive));
        }

        [Fact]
        public void GamutReachability_NonFinitePrimaryDriveIsUnreachable()
        {
            var invalid = new double[,]
            {
                { 1.0, 0.0, 0.0 },
                { 0.0, double.NaN, 0.0 },
                { 0.0, 0.0, 1.0 },
            };

            double drive = GamutReachability.MaxPrimaryDrive(invalid);

            Assert.Equal(double.PositiveInfinity, drive);
            Assert.False(GamutReachability.IsReachable(drive));
        }

        private static double MaxPrimaryDrive(double[,] displayRgbToXyz, CalibrationTarget target)
        {
            var m = ColorMath.MultiplyMatrices(ColorMath.Invert3x3(displayRgbToXyz), target.RgbToXyzMatrix);
            return GamutReachability.MaxPrimaryDrive(m);
        }
    }
}
