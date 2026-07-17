using System;
using System.IO;
using System.Runtime.InteropServices;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Exercises the same public ICC matrix/TRC path used by color-critical desktop apps.
    /// This is a Windows CMM proxy for Photoshop: MHC2 remains private to DWM, while the CMM
    /// must see an ordinary sRGB-like display profile for SDR image pixels in HDR mode.
    /// </summary>
    public sealed class IccColorManagementRegressionTests
    {
        [Fact]
        public void WindowsCmm_HdrProfileKeepsSrgbMidgray_WhileLegacyPqTrcDoesNot()
        {
            string? template = FindTemplate();
            if (template == null) return;
            string srgb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool", "drivers", "color", "sRGB Color Space Profile.icm");
            if (!File.Exists(srgb)) return;

            string safe = Path.Combine(Path.GetTempPath(), $"gloam-cmm-safe-{Guid.NewGuid():N}.icm");
            string legacy = Path.Combine(Path.GetTempPath(), $"gloam-cmm-legacy-{Guid.NewGuid():N}.icm");
            try
            {
                BuildSafeProfile(template, safe);
                File.Copy(safe, legacy);
                ReplaceTrcWithPq(legacy);

                ushort safeGray = TranslateGray(srgb, safe, 0.5);
                ushort legacyGray = TranslateGray(srgb, legacy, 0.5);
                double safeSignal = safeGray / 65535.0;
                double legacySignal = legacyGray / 65535.0;

                Assert.InRange(safeSignal, 0.47, 0.53);
                Assert.True(legacySignal - safeSignal > 0.08,
                    $"legacy PQ destination should reproduce the washed-out ICC shift: safe={safeSignal:F4}, legacy={legacySignal:F4}");
            }
            finally
            {
                try { File.Delete(safe); } catch { }
                try { File.Delete(legacy); } catch { }
            }
        }

        private static ushort TranslateGray(string sourcePath, string destinationPath, double signal)
        {
            IntPtr source = OpenProfile(sourcePath);
            IntPtr destination = OpenProfile(destinationPath);
            Assert.NotEqual(IntPtr.Zero, source);
            Assert.NotEqual(IntPtr.Zero, destination);
            IntPtr transform = IntPtr.Zero;
            try
            {
                transform = CreateMultiProfileTransform(
                    new[] { source, destination }, 2, new uint[] { 0, 0 }, 2, 0, 0);
                Assert.NotEqual(IntPtr.Zero, transform);

                ushort word = (ushort)Math.Round(signal * 65535.0);
                var input = new[] { new NativeColor { Red = word, Green = word, Blue = word } };
                var output = new NativeColor[1];
                Assert.True(TranslateColors(transform, input, 1, 2, output, 2)); // COLOR_RGB
                Assert.InRange(Math.Abs(output[0].Red - output[0].Green), 0, 32);
                Assert.InRange(Math.Abs(output[0].Red - output[0].Blue), 0, 32);
                return output[0].Red;
            }
            finally
            {
                if (transform != IntPtr.Zero) DeleteColorTransform(transform);
                CloseColorProfile(destination);
                CloseColorProfile(source);
            }
        }

        private static IntPtr OpenProfile(string path)
        {
            IntPtr filename = Marshal.StringToHGlobalUni(path);
            try
            {
                var profile = new NativeProfile
                {
                    Type = 1, // PROFILE_FILENAME
                    Data = filename,
                    DataSize = checked((uint)((path.Length + 1) * 2))
                };
                return OpenColorProfileW(ref profile, 1, 1, 3); // PROFILE_READ, FILE_SHARE_READ, OPEN_EXISTING
            }
            finally
            {
                Marshal.FreeHGlobal(filename);
            }
        }

        private static void BuildSafeProfile(string template, string path)
        {
            var identity = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            var lut = new double[1024];
            for (int i = 0; i < lut.Length; i++) lut[i] = i / 1023.0;
            Mhc2ProfileBuilder.Build(template, path, identity, lut, lut, lut,
                minLuminanceNits: 0.02, maxLuminanceNits: 242,
                colorimetry: StandardTargets.Rec709Pq);
        }

        private static void ReplaceTrcWithPq(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int trc = FindTag(bytes, 0x72545243);
            for (int i = 0; i < 1024; i++)
            {
                int sample = (int)Math.Round(Math.Clamp(
                    StandardTargets.Rec709Pq.ApplyEotf(i / 1023.0), 0, 1) * 65535);
                bytes[trc + 12 + i * 2] = (byte)(sample >> 8);
                bytes[trc + 13 + i * 2] = (byte)sample;
            }
            File.WriteAllBytes(path, bytes);
        }

        private static int FindTag(byte[] bytes, int signature)
        {
            int count = ReadU32(bytes, 128);
            for (int i = 0; i < count; i++)
            {
                int entry = 132 + i * 12;
                if (ReadU32(bytes, entry) == signature) return ReadU32(bytes, entry + 4);
            }
            throw new InvalidDataException("TRC tag missing.");
        }

        private static int ReadU32(byte[] bytes, int offset) =>
            (bytes[offset] << 24) | (bytes[offset + 1] << 16) |
            (bytes[offset + 2] << 8) | bytes[offset + 3];

        private static string? FindTemplate()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "srgb_to_gamma2p2_200_mhc2.icm");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeProfile
        {
            public uint Type;
            public IntPtr Data;
            public uint DataSize;
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct NativeColor
        {
            [FieldOffset(0)] public ushort Red;
            [FieldOffset(2)] public ushort Green;
            [FieldOffset(4)] public ushort Blue;
        }

        [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenColorProfileW(
            ref NativeProfile profile, uint desiredAccess, uint shareMode, uint creationMode);

        [DllImport("mscms.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseColorProfile(IntPtr profile);

        [DllImport("mscms.dll")]
        private static extern IntPtr CreateMultiProfileTransform(
            [In] IntPtr[] profiles, uint profileCount,
            [In] uint[] intents, uint intentCount, uint flags, uint preferredCmm);

        [DllImport("mscms.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteColorTransform(IntPtr transform);

        [DllImport("mscms.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateColors(
            IntPtr transform, [In] NativeColor[] input, uint count, uint inputType,
            [Out] NativeColor[] output, uint outputType);
    }
}
