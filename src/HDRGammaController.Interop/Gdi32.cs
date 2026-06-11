using System;
using System.Runtime.InteropServices;

namespace HDRGammaController.Interop
{
    /// <summary>
    /// GDI gamma ramp interop. SetDeviceGammaRamp is the same API ArgyllCMS dispwin
    /// uses to load the VCGT on Windows, so calling it directly produces bit-identical
    /// hardware state without the external process spawn.
    /// </summary>
    public static class Gdi32
    {
        /// <summary>
        /// Hardware gamma ramp: 256 16-bit entries per channel, the fixed size of the
        /// GDI gamma ramp interface.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct GammaRamp
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;

            public static GammaRamp Create() => new GammaRamp
            {
                Red = new ushort[256],
                Green = new ushort[256],
                Blue = new ushort[256]
            };
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateDC(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDeviceGammaRamp(IntPtr hdc, ref GammaRamp ramp);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDeviceGammaRamp(IntPtr hdc, ref GammaRamp ramp);
    }
}
