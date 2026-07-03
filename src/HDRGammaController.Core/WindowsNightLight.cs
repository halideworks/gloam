using System;
using HDRGammaController.Core.Calibration;
using Microsoft.Win32;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Turns Windows Night Light off. Night Light applies its own warm shift through the
    /// same display pipeline Gloam owns, so with both active the output is the product of
    /// two color transforms — corrupting calibrated output and night-mode colorimetry
    /// alike, whether or not Gloam's night mode is currently on.
    ///
    /// The state lives in the undocumented CloudStore blob whose layout is documented on
    /// <see cref="CalibrationInstallPreflight.IsNightLightActiveBlob"/>. Every operation
    /// here is conservative: only a blob the parser confidently recognizes as ON is
    /// transformed, the write is verified by re-reading, and any surprise is a no-op
    /// failure rather than a guess.
    /// </summary>
    public static class WindowsNightLight
    {
        /// <summary>Current Night Light state; null = cannot determine.</summary>
        public static bool? Detect() => CalibrationInstallPreflight.DetectNightLightActive();

        /// <summary>
        /// Pure transform of the CloudStore record: ON blob → OFF blob. Returns null unless
        /// the input is confidently the known ON layout (state header 0x15 at index 18 and
        /// the extra 0x10 0x00 field at indices 23/24 that the ON form carries).
        ///
        /// The varint timestamp at bytes 10..14 is bumped the same way the community
        /// togglers do (increment the first non-0xFF byte) so the shell accepts the record
        /// as newer and re-broadcasts the state to DWM.
        /// </summary>
        public static byte[]? BuildDisabledBlob(byte[]? data)
        {
            if (CalibrationInstallPreflight.IsNightLightActiveBlob(data) != true) return null;
            if (data!.Length < 25 || data[23] != 0x10 || data[24] != 0x00) return null;

            var result = new byte[data.Length - 2];
            Array.Copy(data, 0, result, 0, 23);
            Array.Copy(data, 25, result, 23, data.Length - 25);
            result[18] = 0x13;

            for (int i = 10; i < 15; i++)
            {
                if (result[i] != 0xFF)
                {
                    result[i]++;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Disables Night Light in the registry and verifies the write took. Returns true
        /// only when the state was confidently ON and is confidently OFF afterwards.
        /// </summary>
        public static bool TryDisable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    CalibrationInstallPreflight.NightLightStateKeyPath, writable: true);
                if (key == null) return false;

                var disabled = BuildDisabledBlob(key.GetValue("Data") as byte[]);
                if (disabled == null) return false;

                key.SetValue("Data", disabled, RegistryValueKind.Binary);

                bool verified = CalibrationInstallPreflight.IsNightLightActiveBlob(
                    key.GetValue("Data") as byte[]) == false;
                if (!verified)
                    Log.Info("WindowsNightLight: disable write did not verify as OFF.");
                return verified;
            }
            catch (Exception ex)
            {
                Log.Info($"WindowsNightLight: disable failed: {ex.Message}");
                return false;
            }
        }
    }
}
