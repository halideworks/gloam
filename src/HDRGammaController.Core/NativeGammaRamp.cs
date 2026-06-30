using System;
using HDRGammaController.Interop;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Applies per-channel LUTs to a display's hardware gamma ramp via the Win32
    /// SetDeviceGammaRamp API — the same call ArgyllCMS dispwin makes after parsing a
    /// .cal file, so the resulting hardware state is identical to the dispwin path
    /// while avoiding the ~100-500ms process spawn per apply. DispwinRunner uses this
    /// first and falls back to dispwin only when the native call fails.
    /// </summary>
    public static class NativeGammaRamp
    {
        /// <summary>
        /// Resamples a LUT (any length ≥ 2, values in [0,1]) onto the fixed 256-entry
        /// 16-bit hardware ramp using linear interpolation — the same resampling
        /// dispwin performs when loading a 1024-point .cal.
        /// </summary>
        public static ushort[] BuildRampChannel(double[] lut)
        {
            if (lut == null || lut.Length < 2)
                throw new ArgumentException("LUT must have at least 2 entries", nameof(lut));

            var ramp = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                double pos = i / 255.0 * (lut.Length - 1);
                int lo = (int)pos;
                int hi = Math.Min(lo + 1, lut.Length - 1);
                double frac = pos - lo;
                double value = lut[lo] + (lut[hi] - lut[lo]) * frac;
                value = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
                ramp[i] = (ushort)Math.Clamp(Math.Round(value * 65535.0), 0, 65535);
            }
            return ramp;
        }

        /// <summary>
        /// Sets the hardware gamma ramp for the given GDI display (e.g. @"\\.\DISPLAY1").
        /// Returns false if the device DC could not be created or the driver rejected
        /// the ramp — callers should fall back to dispwin in that case.
        /// </summary>
        public static bool TryApply(string deviceName, double[] lutR, double[] lutG, double[] lutB)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;

            var ramp = Gdi32.GammaRamp.Create();
            ramp.Red = BuildRampChannel(lutR);
            ramp.Green = BuildRampChannel(lutG);
            ramp.Blue = BuildRampChannel(lutB);

            return SetRamp(deviceName, ref ramp);
        }

        /// <summary>
        /// Reads the display's current hardware ramp. Returns false (with empty
        /// arrays) if the DC can't be created or the driver refuses the read.
        /// </summary>
        public static bool TryRead(string deviceName, out ushort[] red, out ushort[] green, out ushort[] blue)
        {
            red = green = blue = Array.Empty<ushort>();
            if (string.IsNullOrEmpty(deviceName)) return false;

            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = Gdi32.CreateDC(deviceName, deviceName, null, IntPtr.Zero);
                if (hdc == IntPtr.Zero) return false;

                var ramp = Gdi32.GammaRamp.Create();
                if (!Gdi32.GetDeviceGammaRamp(hdc, ref ramp)) return false;

                red = ramp.Red;
                green = ramp.Green;
                blue = ramp.Blue;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (hdc != IntPtr.Zero) Gdi32.DeleteDC(hdc);
            }
        }

        /// <summary>Restores the identity (linear) ramp.</summary>
        public static bool TryClear(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;

            var ramp = Gdi32.GammaRamp.Create();
            for (int i = 0; i < 256; i++)
            {
                ushort value = (ushort)(i * 257); // 0..65535 in 256 even steps
                ramp.Red[i] = value;
                ramp.Green[i] = value;
                ramp.Blue[i] = value;
            }
            return SetRamp(deviceName, ref ramp);
        }

        private static bool SetRamp(string deviceName, ref Gdi32.GammaRamp ramp)
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = Gdi32.CreateDC(deviceName, deviceName, null, IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                {
                    Log.Error($"NativeGammaRamp: CreateDC failed for {deviceName}");
                    return false;
                }

                bool ok = Gdi32.SetDeviceGammaRamp(hdc, ref ramp);
                if (!ok)
                {
                    // Typically the GDI gamma-range validation rejecting an aggressive
                    // ramp; dispwin would hit the same wall, but let the fallback try.
                    Log.Error($"NativeGammaRamp: SetDeviceGammaRamp rejected for {deviceName}");
                }
                return ok;
            }
            catch (Exception ex)
            {
                Log.Error($"NativeGammaRamp: {deviceName}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (hdc != IntPtr.Zero) Gdi32.DeleteDC(hdc);
            }
        }
    }
}
