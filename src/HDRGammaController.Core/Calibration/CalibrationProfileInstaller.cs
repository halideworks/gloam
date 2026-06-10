using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HDRGammaController.Interop;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Generates a calibrated MHC2 ICC profile (gamut matrix + per-channel tone LUTs) and
    /// installs it as a monitor's default Windows color profile, so the Desktop Window
    /// Manager applies the correction natively and persistently — across reboots and in HDR —
    /// with no .cube export and no per-frame work from this app. The GPU gamma ramp is then
    /// free to layer night-mode/gamma on top (see the apply path's calibration-aware mode).
    /// </summary>
    public static class CalibrationProfileInstaller
    {
        // White levels for which an MHC2 template is shipped/extracted.
        private static readonly int[] TemplateWhiteLevels = { 100, 200, 300, 400 };

        public sealed record InstallResult(bool Success, string ProfileName, string? Error);

        /// <summary>
        /// Builds and installs the calibrated profile for <paramref name="monitor"/>. Returns
        /// the installed profile filename so callers can record it (and later revert).
        /// </summary>
        public static InstallResult Install(
            MonitorInfo monitor,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            double[] lutR, double[] lutG, double[] lutB,
            double whiteLevel,
            bool hdrMode = false,
            IReadOnlyList<MeasurementResult>? measurements = null)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath))
                return new InstallResult(false, "", "Monitor has no device path; cannot associate a profile.");

            // SAFETY GATE: the correction is only valid in the mode it was MEASURED in. The
            // LUT domain differs completely (gamma-encoded signal in SDR vs PQ wire signal in
            // HDR), so a cross-mode install wrecks the image (the original hardcoded-SDR bug
            // washed out the HDR desktop). Refuse the mismatch instead.
            if (monitor.IsHdrActive != hdrMode)
                return new InstallResult(false, "", hdrMode
                    ? "This calibration was measured in HDR, but the display is now in SDR mode.\n\n" +
                      "Switch the display back to HDR and apply again."
                    : "This calibration was measured in SDR, but the display is now in HDR mode.\n\n" +
                      "Switch the display back to SDR and apply again (or re-run the calibration in HDR).");

            if (hdrMode && measurements == null)
                return new InstallResult(false, "", "HDR install needs the calibration measurements to build PQ-domain LUTs.");

            string? template = FindTemplate(whiteLevel);
            if (template == null)
                return new InstallResult(false, "", "No MHC2 template found to base the profile on.");

            double[,] matrix;
            (double R, double G, double B) whiteGains;
            try
            {
                matrix = Mhc2ProfileBuilder.BuildGamutMatrix(characterization, target);
                whiteGains = Mhc2ProfileBuilder.WhitePointLutGains(characterization, target);
            }
            catch (Exception ex) { return new InstallResult(false, "", $"Gamut matrix failed: {ex.Message}"); }

            // GAMUT GUARD: block only when the target gamut is wider than the panel can EMIT
            // (e.g. an sRGB-class display calibrated to Rec.2020). There the matrix has to
            // synthesize primaries the display can't produce, so a target primary maps to a
            // display drive value well above full-scale → clipping and a heavy cast (the magenta
            // we hit). NARROWING a slightly-wide panel to sRGB/Rec.709 is perfectly reachable
            // (drive values stay <= 1) and must NOT be blocked — that wrongly rejected a good
            // Gamma-2.4 calibration. (The matrix is white-preserving now, so white-point
            // correction no longer contributes any overshoot here.)
            double maxDrive = MaxTargetDrive(matrix);
            if (maxDrive > 1.3) // keep in sync with the setup-time EDID filter
                return new InstallResult(false, "",
                    $"The chosen target ('{target.Name}') needs primaries about {maxDrive:P0} of this " +
                    "display's maximum — i.e. a wider gamut than the panel can physically produce, so the " +
                    "correction would clip and cast color.\n\nCalibrate to a target the panel can reach — " +
                    "for an SDR display that's usually \"sRGB (Gamma 2.2)\" or Rec.709. Re-run with that selected.");

            // Per-channel tone LUTs with the white-point correction folded in.
            //  SDR: scale the caller's signal-domain LUTs by gain^(1/gamma) (for a gamma-law
            //       panel a linear gain is exactly that signal gain).
            //  HDR: the LUTs live in PQ wire-signal domain instead — build them from the HDR
            //       measurements with the gains applied in nits.
            double[] gainedR, gainedG, gainedB;
            double? headerMinNits = null, headerMaxNits = null;
            if (hdrMode)
            {
                HdrMhc2LutBuilder.Result hdrLuts;
                try { hdrLuts = HdrMhc2LutBuilder.Build(measurements!, monitor.SdrWhiteLevel, whiteGains); }
                catch (Exception ex) { return new InstallResult(false, "", $"HDR LUT generation failed: {ex.Message}"); }
                (gainedR, gainedG, gainedB) = (hdrLuts.LutR, hdrLuts.LutG, hdrLuts.LutB);

                // MHC2 header range: the panel's DXGI-reported HDR range when available
                // (matches what the Windows HDR Calibration app writes), else measured.
                headerMinNits = hdrLuts.MeasuredBlackNits;
                headerMaxNits = monitor.HdrPeakNits > 50 ? monitor.HdrPeakNits : hdrLuts.MeasuredPeakNits;
            }
            else
            {
                double gamma = characterization.MeasuredGamma is > 1.0 and < 3.5
                    ? characterization.MeasuredGamma : 2.2;
                gainedR = ApplySignalGain(lutR, whiteGains.R, gamma);
                gainedG = ApplySignalGain(lutG, whiteGains.G, gamma);
                gainedB = ApplySignalGain(lutB, whiteGains.B, gamma);
            }

            // Windows applies the MHC2 matrix sandwiched between fixed sRGB↔XYZ conversions,
            // so the tag must hold the matrix pre-wrapped into that domain.
            double[,] mhc2Matrix = Mhc2ProfileBuilder.ToMhc2MatrixDomain(matrix);

            // Self-documenting diagnostics: every install logs exactly what was computed, so a
            // bad-looking result can be diagnosed from app.log without re-running calibration.
            var c = characterization;
            Log.Info(
                $"CalibrationProfileInstaller: target '{target.Name}'\n" +
                $"  measured primaries R({c.RedPrimary.X:F4},{c.RedPrimary.Y:F4}) G({c.GreenPrimary.X:F4},{c.GreenPrimary.Y:F4}) " +
                $"B({c.BluePrimary.X:F4},{c.BluePrimary.Y:F4}) W({c.WhitePoint.X:F4},{c.WhitePoint.Y:F4}) " +
                $"peak {c.PeakLuminance:F1} cd/m², gamma {c.MeasuredGamma:F2}\n" +
                $"  gamut matrix (RGB→RGB, white-preserving): {FormatMatrix(matrix)}\n" +
                $"  MHC2 tag matrix (XYZ-domain wrapped):     {FormatMatrix(mhc2Matrix)}\n" +
                $"  white-point LUT gains R={whiteGains.R:F4} G={whiteGains.G:F4} B={whiteGains.B:F4}, " +
                $"max target drive {maxDrive:F3}, mode {(hdrMode ? "HDR (PQ-domain LUTs)" : "SDR")}" +
                (hdrMode ? $", header range {headerMinNits:F3}–{headerMaxNits:F0} nits, SDR white {monitor.SdrWhiteLevel:F0} nits" : ""));

            string profileName = BuildProfileName(monitor, target);
            string srcPath = Path.Combine(Path.GetTempPath(), profileName);

            try
            {
                // The internal description is what Windows Color Management displays — without
                // it the profile shows the template's leftover "SDR ACM: srgb_d50 [...]" text.
                Mhc2ProfileBuilder.Build(template, srcPath, mhc2Matrix, gainedR, gainedG, gainedB,
                    description: Path.GetFileNameWithoutExtension(profileName),
                    minLuminanceNits: headerMinNits, maxLuminanceNits: headerMaxNits);

                // Copy into the system color store. Returns false if an identical name already
                // exists — harmless, we re-associate below regardless.
                if (!Wcs.InstallColorProfile(null, srcPath))
                    Log.Info($"CalibrationProfileInstaller: InstallColorProfile returned false for {profileName} (may already exist).");

                if (hdrMode)
                {
                    // HDR displays read profiles from the ADVANCED COLOR association list
                    // (registry ICMProfileAC) — the classic association APIs only touch the
                    // SDR list, which Windows ignores while HDR is on. This is how the
                    // Windows HDR Calibration app's own profiles are associated.
                    // Resolve the DisplayConfig identity FRESH at install time — the cached
                    // enumeration may predate an HDR toggle or display-topology change.
                    var adapterId = monitor.DisplayConfigAdapterId;
                    uint sourceId = monitor.DisplayConfigSourceId;
                    bool haveIds = monitor.HasDisplayConfigIds;
                    if (DisplayConfig.TryGetPathForGdiName(monitor.DeviceName, out var freshAdapter, out uint freshSource, out _))
                    {
                        adapterId = freshAdapter;
                        sourceId = freshSource;
                        haveIds = true;
                    }
                    if (!haveIds)
                        return new InstallResult(false, profileName,
                            "Could not resolve this display's identity (adapter LUID / source id) for the " +
                            "Advanced Color profile association. Try re-opening calibration to refresh displays.");

                    int hr = Wcs.ColorProfileAddDisplayAssociation(
                        Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                        profileName, adapterId, sourceId,
                        setAsDefault: true, associateAsAdvancedColor: true);
                    if (hr != 0)
                        return new InstallResult(false, profileName,
                            $"Windows refused the Advanced Color profile association (HRESULT 0x{hr:X8}).");
                }
                else
                {
                    if (!Wcs.AssociateColorProfileWithDevice(null, srcPath, monitor.MonitorDevicePath))
                        return new InstallResult(false, profileName,
                            "Windows refused to associate the profile with the display. Make sure the monitor is active.");

                    if (!Wcs.WcsSetDefaultColorProfile(
                            Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                            monitor.MonitorDevicePath, Wcs.CPT_ICC, Wcs.CPST_PERCEPTUAL, 0, profileName))
                        Log.Info($"CalibrationProfileInstaller: WcsSetDefaultColorProfile returned false for {profileName}.");
                }

                Log.Info($"CalibrationProfileInstaller: Installed + set default '{profileName}' for {monitor.FriendlyName} ({(hdrMode ? "advanced color" : "SDR")} association).");
                return new InstallResult(true, profileName, null);
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Install failed: {ex.Message}");
                return new InstallResult(false, profileName, ex.Message);
            }
            finally
            {
                try { if (File.Exists(srcPath)) File.Delete(srcPath); } catch { }
            }
        }

        /// <summary>
        /// Disables a calibration profile on a monitor WITHOUT deleting it: removes the
        /// device association so Windows stops applying it, but the .icm stays in the system
        /// color store and can be re-associated from Color Management at any time. This is
        /// what pre-measurement bypass should use — measuring native must not destroy the
        /// user's previous calibration.
        /// </summary>
        public static void Disable(MonitorInfo monitor, string profileName)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrEmpty(profileName)) return;
            try
            {
                // Remove from BOTH association lists (SDR and Advanced Color) — we don't track
                // which mode the profile was installed for, and removing from a list it isn't
                // in is harmless.
                Wcs.DisassociateColorProfileFromDevice(null, profileName, monitor.MonitorDevicePath);
                if (DisplayConfig.TryGetPathForGdiName(monitor.DeviceName, out var adapterId, out uint sourceId, out _))
                {
                    Wcs.ColorProfileRemoveDisplayAssociation(
                        Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                        profileName, adapterId, sourceId,
                        dissociateAdvancedColor: true);
                }
                Log.Info($"CalibrationProfileInstaller: Disabled '{profileName}' on {monitor.FriendlyName} (file kept in color store).");
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Disable failed: {ex.Message}");
            }
        }

        /// <summary>Fully removes a previously-installed calibration profile from a monitor.</summary>
        public static void Uninstall(MonitorInfo monitor, string profileName)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrEmpty(profileName)) return;
            try
            {
                Wcs.DisassociateColorProfileFromDevice(null, profileName, monitor.MonitorDevicePath);
                Wcs.UninstallColorProfile(null, profileName, true);
                Log.Info($"CalibrationProfileInstaller: Removed '{profileName}' from {monitor.FriendlyName}.");
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Uninstall failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Largest display drive value the matrix demands for the target's primaries and white
        /// (content R/G/B = (1,0,0),(0,1,0),(0,0,1),(1,1,1)). &lt;= ~1 means every target color is
        /// reachable (including narrowing a wider panel); well above 1 means the target asks for
        /// primaries the display can't emit → it would clip and cast.
        /// </summary>
        /// <summary>Scales a signal-domain tone LUT by a linear-light gain (gain^(1/gamma)).</summary>
        private static double[] ApplySignalGain(double[] lut, double linearGain, double gamma)
        {
            double signalGain = Math.Pow(Math.Clamp(linearGain, 1e-6, 1.0), 1.0 / gamma);
            var result = new double[lut.Length];
            for (int i = 0; i < lut.Length; i++)
                result[i] = Math.Clamp(lut[i] * signalGain, 0.0, 1.0);
            return result;
        }

        private static string FormatMatrix(double[,] m) =>
            $"[{m[0, 0]:F5} {m[0, 1]:F5} {m[0, 2]:F5}; " +
            $"{m[1, 0]:F5} {m[1, 1]:F5} {m[1, 2]:F5}; " +
            $"{m[2, 0]:F5} {m[2, 1]:F5} {m[2, 2]:F5}]";

        private static double MaxTargetDrive(double[,] m)
        {
            double max = 0;
            (double, double, double)[] contents = { (1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 1) };
            foreach (var (a, b, c) in contents)
                for (int r = 0; r < 3; r++)
                    max = Math.Max(max, m[r, 0] * a + m[r, 1] * b + m[r, 2] * c);
            return max;
        }

        private static string? FindTemplate(double whiteLevel)
        {
            int nearest = TemplateWhiteLevels.OrderBy(w => Math.Abs(w - whiteLevel)).First();
            string fileName = $"srgb_to_gamma2p2_{nearest}_mhc2.icm";
            foreach (var dir in CandidateDirectories())
            {
                string p = Path.Combine(dir, fileName);
                if (File.Exists(p)) return p;
            }
            Log.Error($"CalibrationProfileInstaller: template {fileName} not found near {AppContext.BaseDirectory}.");
            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> CandidateDirectories()
        {
            // ResourceExtractor drops the templates next to the executable; walk up too so dev
            // runs (bin/Debug/...) find the repo-root copies.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                yield return dir.FullName;
        }

        /// <summary>
        /// Human-readable, explanatory, dated profile filename, e.g.
        /// "M27Q P - sRGB G2.2 - 2026-06-09 2245.icm". Sanitized for the file system; the
        /// trailing timestamp keeps each calibration distinct in Color Management.
        /// </summary>
        private static string BuildProfileName(MonitorInfo monitor, CalibrationTarget target)
        {
            string monitorName = Sanitize(string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Display" : monitor.FriendlyName);
            string targetName = Sanitize(ShortTargetName(target));
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmm");
            return $"{monitorName} - {targetName} - {stamp}.icm";
        }

        private static string ShortTargetName(CalibrationTarget t)
        {
            // Compact, recognizable label rather than the long descriptive Name.
            string n = t.Name ?? "Custom";
            return n.Replace("(", "").Replace(")", "").Replace("Gamma ", "G").Trim();
        }

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            s = s.Replace("  ", " ").Trim();
            return s.Length > 40 ? s[..40].Trim() : s;
        }

        private static string ShortHash(string input)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash)[..8];
        }
    }
}
