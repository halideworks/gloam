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
        public const uint Srccopy = 0x00CC0020;
        public const uint CaptureBlt = 0x40000000;
        public const uint DibRgbColors = 0;
        public const int ColorOnColor = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;
        }

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
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool StretchBlt(
            IntPtr hdcDest, int xDest, int yDest, int widthDest, int heightDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int widthSrc, int heightSrc,
            uint rasterOperation);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int GetDIBits(
            IntPtr hdc, IntPtr hBitmap, uint start, uint scanLines,
            [Out] byte[] bits, ref BitmapInfo bitmapInfo, uint usage);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDeviceGammaRamp(IntPtr hdc, ref GammaRamp ramp);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDeviceGammaRamp(IntPtr hdc, ref GammaRamp ramp);
    }
}
