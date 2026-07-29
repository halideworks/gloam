using System;
using System.Collections.Concurrent;
using Gloam.Interop;

namespace Gloam.Core
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
        // Windows validates every SetDeviceGammaRamp call (win32k, not the driver) unless
        // the ICM "GdiIcmGammaRange" registry override is set: each entry's HIGH BYTE must
        // stay within ±128 of its own index — |(ramp[i] >> 8) − i| ≤ 128, i.e. an entry may
        // deviate from the identity line by at most half of full scale. One violating entry
        // rejects the whole call and NOTHING applies. Measured on Windows 11 22621
        // (monotonicity is NOT required; the bound is per entry, anchored at the index).
        //
        // This is why night mode used to silently no-op on SDR displays: 2700K cuts blue to
        // ~0.21 linear, whose gamma-encoded ramp tops out at 0.489 of full scale — a hair
        // below the 0.496 the envelope allows at index 255. The HDR path never hit it
        // because its PQ LUTs stay near identity at the top (headroom passthrough).
        internal const int GdiEnvelopeHalfRangeHighBytes = 128;

        /// <summary>Test seam for the GdiIcmGammaRange registry probe.</summary>
        internal static bool? GammaRangeUnlockOverride;
        private static bool? _gammaRangeUnlockedCache;

        /// <summary>
        /// True when the user has disabled Windows' gamma-range validation via the
        /// well-known ICM registry override (HKLM\...\ICM\GdiIcmGammaRange = 0x100,
        /// the same unlock f.lux and ArgyllCMS document). Cached for the process
        /// lifetime — the value only takes effect after sign-out anyway.
        /// </summary>
        internal static bool IsGammaRangeUnlocked()
        {
            if (GammaRangeUnlockOverride.HasValue) return GammaRangeUnlockOverride.Value;
            if (_gammaRangeUnlockedCache.HasValue) return _gammaRangeUnlockedCache.Value;

            bool unlocked = false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM");
                unlocked = key?.GetValue("GdiIcmGammaRange") is int v && v == 0x100;
            }
            catch (Exception ex)
            {
                Log.Info($"NativeGammaRamp: GdiIcmGammaRange probe failed ({ex.Message}); assuming locked range.");
            }
            _gammaRangeUnlockedCache = unlocked;
            return unlocked;
        }

        /// <summary>Lowest ramp value Windows' validation accepts at entry <paramref name="i"/>.</summary>
        internal static ushort GdiEnvelopeLower(int i) =>
            (ushort)(i <= GdiEnvelopeHalfRangeHighBytes ? 0 : (i - GdiEnvelopeHalfRangeHighBytes) * 256);

        /// <summary>Highest ramp value Windows' validation accepts at entry <paramref name="i"/>.</summary>
        internal static ushort GdiEnvelopeUpper(int i) =>
            (ushort)Math.Min(65535, (i + GdiEnvelopeHalfRangeHighBytes + 1) * 256 - 1);

        /// <summary>
        /// Resamples a LUT (any length ≥ 2, values in [0,1]) onto the fixed 256-entry
        /// 16-bit hardware ramp using linear interpolation — the same resampling
        /// dispwin performs when loading a 1024-point .cal. Unless the user has
        /// unlocked the range in the registry, entries are clamped into Windows'
        /// validation envelope: applying the closest ACCEPTED ramp (the clamp only
        /// bends the extreme end of an aggressive channel) beats the alternative,
        /// which is win32k rejecting the whole call and the screen not changing at all.
        /// </summary>
        public static ushort[] BuildRampChannel(double[] lut) =>
            BuildRampChannel(lut, applyGdiEnvelope: !IsGammaRangeUnlocked(), out _);

        internal static ushort[] BuildRampChannel(double[] lut, bool applyGdiEnvelope, out bool clamped)
        {
            if (lut == null || lut.Length < 2)
                throw new ArgumentException("LUT must have at least 2 entries", nameof(lut));

            clamped = false;
            var ramp = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                double pos = i / 255.0 * (lut.Length - 1);
                int lo = (int)pos;
                int hi = Math.Min(lo + 1, lut.Length - 1);
                double frac = pos - lo;
                double value = lut[lo] + (lut[hi] - lut[lo]) * frac;
                value = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;
                ushort word = (ushort)Math.Clamp(Math.Round(value * 65535.0), 0, 65535);

                if (applyGdiEnvelope)
                {
                    ushort bounded = Math.Clamp(word, GdiEnvelopeLower(i), GdiEnvelopeUpper(i));
                    if (bounded != word) clamped = true;
                    word = bounded;
                }
                ramp[i] = word;
            }
            return ramp;
        }

        // One log line per display the first time the envelope actually bends a ramp —
        // fades re-apply several times a second and must not flood the log.
        private static readonly ConcurrentDictionary<string, bool> EnvelopeLoggedByDevice = new();

        /// <summary>
        /// Sets the hardware gamma ramp for the given GDI display (e.g. @"\\.\DISPLAY1").
        /// Returns false if the device DC could not be created or the driver rejected
        /// the ramp — callers should fall back to dispwin in that case.
        /// </summary>
        public static bool TryApply(string deviceName, double[] lutR, double[] lutG, double[] lutB)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;

            bool applyEnvelope = !IsGammaRangeUnlocked();
            var ramp = Gdi32.GammaRamp.Create();
            ramp.Red = BuildRampChannel(lutR, applyEnvelope, out bool clampedR);
            ramp.Green = BuildRampChannel(lutG, applyEnvelope, out bool clampedG);
            ramp.Blue = BuildRampChannel(lutB, applyEnvelope, out bool clampedB);

            if ((clampedR || clampedG || clampedB) && EnvelopeLoggedByDevice.TryAdd(deviceName, true))
            {
                Log.Info(
                    $"NativeGammaRamp: {deviceName}: Windows' gamma-range validation limits the requested " +
                    $"correction; applied the closest accepted ramp instead (channels clamped:" +
                    $"{(clampedR ? " R" : "")}{(clampedG ? " G" : "")}{(clampedB ? " B" : "")}). " +
                    "For the full range set HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM\\" +
                    "GdiIcmGammaRange (DWORD) = 0x100 and sign out and back in.");
            }

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
