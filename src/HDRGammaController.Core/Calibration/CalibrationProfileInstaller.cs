using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <param name="HdrLuts">
        /// HDR installs only: the PQ-domain tone LUTs actually written into the profile
        /// (built by <see cref="HdrMhc2LutBuilder.Build"/>, or the caller's refined override).
        /// Callers keep this so a later closed-loop refinement can start from the LUT that is
        /// really on the wire. Null for SDR installs and failures before LUT generation.
        /// </param>
        public sealed record InstallResult(bool Success, string ProfileName, string? Error,
            HdrMhc2LutBuilder.Result? HdrLuts = null,
            Mhc2ProofCertificate? ProofCertificate = null,
            Mhc2CompileResult? CompiledPayload = null);

        /// <summary>
        /// Chooses the Windows profile backup to preserve before activating a Gloam profile.
        /// Existing backups win; otherwise capture the current default only if it is not
        /// already the app's active calibration.
        /// </summary>
        public static string? SelectPreviousProfileBackup(
            string? currentDefaultProfile,
            string? activeGloamProfile,
            string? existingBackup)
        {
            if (!string.IsNullOrWhiteSpace(existingBackup))
                return existingBackup;
            if (string.IsNullOrWhiteSpace(currentDefaultProfile))
                return null;
            if (!string.IsNullOrWhiteSpace(activeGloamProfile) &&
                string.Equals(currentDefaultProfile, activeGloamProfile, StringComparison.OrdinalIgnoreCase))
                return null;
            return currentDefaultProfile;
        }

        /// <summary>Returns the currently-default Windows profile for this display/mode, if known.</summary>
        public static string? GetCurrentDefaultProfile(MonitorInfo monitor, bool hdrMode)
        {
            if (monitor == null || string.IsNullOrEmpty(monitor.MonitorDevicePath)) return null;
            try
            {
                if (hdrMode)
                {
                    if (AdvancedColorProfileAssociation.TryGetSelectedDefault(
                            monitor, out string? advancedDefault, out string? error))
                        return advancedDefault;
                    Log.Info($"CalibrationProfileInstaller: Advanced Color default query failed: {error}");
                    return null;
                }

                if (!Wcs.WcsGetDefaultColorProfileSize(
                        Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                        monitor.MonitorDevicePath, Wcs.CPT_ICC, Wcs.CPST_PERCEPTUAL, 0,
                        out int size) || size <= 1)
                    return null;

                var buffer = new char[size];
                if (!Wcs.WcsGetDefaultColorProfile(
                        Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                        monitor.MonitorDevicePath, Wcs.CPT_ICC, Wcs.CPST_PERCEPTUAL, 0,
                        size, buffer))
                    return null;

                return new string(buffer).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationProfileInstaller: default profile query failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Restores a previously-captured Windows profile as the display default. The profile
        /// must already exist in the Windows color store.
        /// </summary>
        public static bool RestoreDefaultProfile(MonitorInfo monitor, string profileName, bool hdrMode)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrEmpty(profileName)) return false;
            try
            {
                if (hdrMode)
                {
                    bool restored = AdvancedColorProfileAssociation.TryActivateInstalled(
                        monitor, profileName, out _, out string? error);
                    if (!restored)
                        Log.Info($"CalibrationProfileInstaller: restore Advanced Color default failed for {profileName}: {error}");
                    return restored;
                }

                if (!Wcs.AssociateColorProfileWithDevice(null, profileName, monitor.MonitorDevicePath))
                    return false;
                return Wcs.WcsSetDefaultColorProfile(
                    Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                    monitor.MonitorDevicePath, Wcs.CPT_ICC, Wcs.CPST_PERCEPTUAL, 0, profileName);
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: restore default failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Builds and installs the calibrated profile for <paramref name="monitor"/>. Returns
        /// the installed profile filename so callers can record it (and later revert).
        /// </summary>
        /// <param name="xyzCorrectionOverride">
        /// Closed-loop HDR color refinement (roadmap 2.2): the measured 3×3 XYZ residual F
        /// (measured ≈ F·reference through the installed chain, from
        /// <see cref="HdrColorMatrixRefiner"/>). When present the gamut matrix becomes
        /// M′ = D⁻¹·F⁻¹·T, cancelling the residual to first order. Note the changed matrix
        /// changes the uniform neutral scale, so an accompanying
        /// <paramref name="hdrLutsOverride"/> will normally be rejected and the HDR tone
        /// LUTs rebuilt from <paramref name="measurements"/> at the new scale — by design,
        /// the LUT input domain depends on the matrix scale.
        /// </param>
        /// <param name="idealCorrectionLut">
        /// SDR only: the ideal full 3D correction to distill into MHC2's actual 3×3 matrix
        /// and monotone per-channel LUT primitives. Null retains the established direct
        /// matrix/tone path and produces no proof certificate.
        /// </param>
        /// <param name="compiledOverride">
        /// SDR closed-loop only: an exact, already model-gated MHC2 payload to serialize.
        /// This bypasses recompilation so physical A/B verification tests precisely the
        /// candidate proposed by <see cref="Mhc2ClosedLoopRefiner"/>. The installer still
        /// validates dimensions, finiteness, bounds and monotonicity before touching Windows.
        /// </param>
        public static InstallResult Install(
            MonitorInfo monitor,
            DisplayCharacterization characterization,
            CalibrationTarget target,
            double[] lutR, double[] lutG, double[] lutB,
            double whiteLevel,
            bool hdrMode = false,
            IReadOnlyList<MeasurementResult>? measurements = null,
            string? profileNameOverride = null,
            HdrMhc2LutBuilder.Result? hdrLutsOverride = null,
            double[,]? xyzCorrectionOverride = null,
            Lut3D? idealCorrectionLut = null,
            Mhc2CompileResult? compiledOverride = null)
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

            if (measurements != null)
            {
                var measurementValidation = CalibrationMeasurementValidator.ValidateForProfile(measurements, target, hdrMode);
                if (!measurementValidation.IsValid)
                    return new InstallResult(false, "", "Measurement validation failed: " + measurementValidation.Error);
            }

            string? template = FindTemplate(whiteLevel);
            if (template == null)
                return new InstallResult(false, "", "No MHC2 template found to base the profile on.");

            // White-point-only mode: substitute the panel's MEASURED primaries for the
            // target's, so the matrix carries the white correction and nothing else. The
            // tone LUTs are unaffected. Verification still grades against the full target.
            var matrixTarget = target;
            if (target.WhitePointOnly)
            {
                matrixTarget = new CalibrationTarget
                {
                    Name = target.Name,
                    RedPrimary = characterization.RedPrimary,
                    GreenPrimary = characterization.GreenPrimary,
                    BluePrimary = characterization.BluePrimary,
                    WhitePoint = target.WhitePoint,
                    TransferFunction = target.TransferFunction,
                    Gamma = target.Gamma,
                    PeakLuminance = target.PeakLuminance,
                    BlackLevel = target.BlackLevel,
                    ReferenceWhite = target.ReferenceWhite,
                };
            }

            double[,] matrix;
            try
            {
                matrix = Mhc2ProfileBuilder.BuildGamutMatrix(characterization, matrixTarget);
                if (xyzCorrectionOverride != null)
                {
                    // M′ = D⁻¹·F⁻¹·T: pre-compensate the measured XYZ residual so the
                    // panel lands the references the matrix intended. BuildGamutMatrix is
                    // D⁻¹·T, so left-compose D⁻¹·F⁻¹·D — equivalently rebuild directly.
                    var displayXyzToRgb = ColorMath.Invert3x3(characterization.RgbToXyzMatrix);
                    matrix = ColorMath.MultiplyMatrices(
                        displayXyzToRgb,
                        ColorMath.MultiplyMatrices(
                            ColorMath.Invert3x3(xyzCorrectionOverride),
                            matrixTarget.RgbToXyzMatrix));
                }
            }
            catch (Exception ex) { return new InstallResult(false, "", $"Gamut matrix failed: {ex.Message}"); }

            // GAMUT GUARD: block only when the target gamut is wider than the panel can EMIT
            // (e.g. an sRGB-class display calibrated to Rec.2020). There the matrix has to
            // synthesize primaries the display can't produce, so a target primary maps to a
            // display drive value well above full-scale → clipping and a heavy cast (the magenta
            // we hit). NARROWING a slightly-wide panel to sRGB/Rec.709 is perfectly reachable
            // (drive values stay <= 1) and must NOT be blocked — that wrongly rejected a good
            // Gamma-2.4 calibration. Gauge REACH on the PRIMARIES ONLY: the matrix is ABSOLUTE,
            // so white (1,1,1) drives above 1.0 whenever the panel white differs from the target
            // white — that is a luminance shift UniformScale absorbs, not unreachable gamut, and
            // must not trip this reject.
            // Negative drives are the other unreachable signature: a target primary outside
            // the panel gamut demands negative light from some channel even when the positive
            // drives stay under the ceiling.
            var (minPrimaryDrive, maxPrimaryDrive) = GamutReachability.PrimaryDriveExtent(matrix);
            if (!GamutReachability.IsReachable(maxPrimaryDrive, minPrimaryDrive))
                return new InstallResult(false, "",
                    $"The chosen target ('{target.Name}') needs primaries about {maxPrimaryDrive:P0} of this " +
                    "display's maximum - i.e. a wider gamut than the panel can physically produce, so the " +
                    "correction would clip and cast color.\n\nCalibrate to a target the panel can reach - " +
                    "for an SDR display that's usually \"sRGB (Gamma 2.2)\" or Rec.709. Re-run with that selected.");

            // White-point handling: the matrix is ABSOLUTE (maps content white to the target
            // white), so on a panel whose white differs some channel needs >1.0 drive for
            // white. Dim ALL channels uniformly to fit — chromaticities (including the
            // primaries) stay exact; per-channel compensation here would re-tint them. The
            // scale uses the FULL drive (primaries AND white) so the white overshoot the guard
            // ignored is still absorbed here.
            double maxDrive = MaxTargetDrive(matrix);
            double uniformScale = Mhc2ProfileBuilder.UniformScale(maxDrive);
            double[,] scaledMatrix = Mhc2ProfileBuilder.ScaleMatrix(matrix, uniformScale);

            // Tone LUTs.
            //  SDR: the caller's signal-domain tone LUTs. When the characterization carries
            //       per-channel tone curves (E4 single-channel ramps) and the caller shipped
            //       a NEUTRAL LUT (identical channels — the closed-loop decomposition path,
            //       or an open-loop build without ramp data), the per-channel gray-tracking
            //       delta f_c⁻¹∘f_neutral is composed on top so grays hold the white point
            //       at every level. LUTs that already differ per channel pass through
            //       untouched (LutGenerator built them from these same curves).
            //  HDR: PQ wire-signal domain LUTs built from the HDR measurements, COMPOSED with
            //       the gamut matrix's uniform scale (M5): the DWM applies the scaled matrix
            //       BEFORE the LUTs, so the wire positions the LUTs actually see are the
            //       matrix-scaled encodings, not the raw content positions the response was
            //       measured at. The builder inverts the measured response at the post-matrix
            //       positions so matrix+LUT together track absolute PQ.
            double[] toneR = lutR, toneG = lutG, toneB = lutB;
            double? headerMinNits = null, headerMaxNits = null;
            bool wireExactLuts = false;
            bool perChannelGrayTracking = false;
            if (!hdrMode)
            {
                (toneR, toneG, toneB) =
                    Lut3DGenerator.ComposePerChannelToneLuts(characterization, lutR, lutG, lutB);
                perChannelGrayTracking =
                    !ReferenceEquals(toneR, lutR) || !ReferenceEquals(toneG, lutG) || !ReferenceEquals(toneB, lutB);
            }
            Mhc2ProofCertificate? proofCertificate = null;
            Mhc2CompileResult? compiledPayload = null;
            if (!hdrMode && compiledOverride != null)
            {
                string? invalid = ValidateCompiledOverride(compiledOverride, toneR.Length);
                if (invalid != null)
                    return new InstallResult(false, "", "Closed-loop payload validation failed: " + invalid);
                scaledMatrix = (double[,])compiledOverride.Matrix.Clone();
                (toneR, toneG, toneB) = ((double[])compiledOverride.LutR.Clone(),
                    (double[])compiledOverride.LutG.Clone(), (double[])compiledOverride.LutB.Clone());
                proofCertificate = compiledOverride.Certificate;
                compiledPayload = compiledOverride;
                Log.Info("CalibrationProfileInstaller: installing exact closed-loop payload: " +
                         proofCertificate.Describe());
            }
            else if (!hdrMode && idealCorrectionLut != null)
            {
                try
                {
                    IReadOnlyList<ModelResidual> residuals = measurements is { Count: > 0 }
                        ? new Lut3DGenerator(target, measurements).ComputeModelResiduals()
                        : Array.Empty<ModelResidual>();
                    var compiled = Mhc2HardwareCompiler.Compile(
                        scaledMatrix, toneR, toneG, toneB,
                        idealCorrectionLut, characterization, target,
                        measurements, residuals,
                        optimizeMatrix: !target.WhitePointOnly);
                    scaledMatrix = compiled.Matrix;
                    (toneR, toneG, toneB) = (compiled.LutR, compiled.LutG, compiled.LutB);
                    proofCertificate = compiled.Certificate;
                    compiledPayload = compiled;
                    Log.Info("CalibrationProfileInstaller: " + proofCertificate.Describe());
                }
                catch (Exception ex)
                {
                    // Compilation is deliberately an enhancement, never an install gate.
                    // The pre-existing matrix/LUT payload remains untouched on any model,
                    // numerical or data-shape failure.
                    Log.Error($"CalibrationProfileInstaller: Proof-Calibrate compile skipped; baseline retained: {ex.Message}");
                }
            }
            HdrMhc2LutBuilder.Result? installedHdrLuts = null;
            if (hdrMode)
            {
                HdrMhc2LutBuilder.Result hdrLuts;
                // Closed-loop refinement path: the caller hands back a refined version of the
                // LUTs a previous Install built. It is only valid if it composed the SAME
                // matrix neutral scale this install computes (the LUT input domain depends on
                // it); a mismatch means the matrix changed since (re-anchored white, trimmed
                // target), so rebuild from the measurements instead of installing a LUT built
                // for a different matrix.
                if (hdrLutsOverride != null &&
                    Math.Abs(hdrLutsOverride.MatrixNeutralScale - uniformScale) <= 1e-9)
                {
                    hdrLuts = hdrLutsOverride;
                }
                else
                {
                    if (hdrLutsOverride != null)
                        Log.Info("CalibrationProfileInstaller: HDR LUT override composed scale " +
                                 $"{hdrLutsOverride.MatrixNeutralScale:F6} but this install needs {uniformScale:F6} — " +
                                 "the matrix changed since the override was built; rebuilding from measurements.");
                    try { hdrLuts = HdrMhc2LutBuilder.Build(measurements!, monitor.SdrWhiteLevel, uniformScale); }
                    catch (Exception ex) { return new InstallResult(false, "", $"HDR LUT generation failed: {ex.Message}"); }
                }
                installedHdrLuts = hdrLuts;
                (toneR, toneG, toneB) = (hdrLuts.LutR, hdrLuts.LutG, hdrLuts.LutB);
                wireExactLuts = hdrLuts.WireExact;

                // MHC2 header range provenance (m7): one policy — MEASURED for BOTH ends
                // whenever the calibration produced a usable black/peak pair (min from the
                // black reading, max from the measured peak), so both numbers describe the
                // same panel the same way. The old mix of measured black + DXGI-advertised
                // peak gave the header two provenances, and DXGI metadata routinely disagrees
                // with the measured panel. DXGI peak is the FALLBACK only (defensive — the
                // builder already validates the measured range before returning).
                bool measuredRangeValid =
                    double.IsFinite(hdrLuts.MeasuredBlackNits) && hdrLuts.MeasuredBlackNits >= 0.0 &&
                    double.IsFinite(hdrLuts.MeasuredPeakNits) &&
                    hdrLuts.MeasuredPeakNits > hdrLuts.MeasuredBlackNits;
                headerMinNits = measuredRangeValid ? hdrLuts.MeasuredBlackNits : 0.0;
                headerMaxNits = measuredRangeValid
                    ? hdrLuts.MeasuredPeakNits
                    : (monitor.HdrPeakNits > 50 ? monitor.HdrPeakNits : hdrLuts.MeasuredPeakNits);
            }

            // Windows applies the MHC2 matrix sandwiched between fixed sRGB↔XYZ conversions,
            // so the tag must hold the matrix pre-wrapped into that domain.
            double[,] mhc2Matrix = Mhc2ProfileBuilder.ToMhc2MatrixDomain(scaledMatrix);

            // Self-documenting diagnostics: every install logs exactly what was computed, so a
            // bad-looking result can be diagnosed from app.log without re-running calibration.
            var c = characterization;
            Log.Info(
                $"CalibrationProfileInstaller: target '{target.Name}'\n" +
                $"  measured primaries R({c.RedPrimary.X:F4},{c.RedPrimary.Y:F4}) G({c.GreenPrimary.X:F4},{c.GreenPrimary.Y:F4}) " +
                $"B({c.BluePrimary.X:F4},{c.BluePrimary.Y:F4}) W({c.WhitePoint.X:F4},{c.WhitePoint.Y:F4}) " +
                $"peak {c.PeakLuminance:F1} cd/m², gamma {c.MeasuredGamma:F2}\n" +
                $"  gamut matrix (RGB→RGB, absolute): {FormatMatrix(matrix)}\n" +
                $"  uniform scale {uniformScale:F4} (max target drive {maxDrive:F3}, max primary drive {maxPrimaryDrive:F3})\n" +
                $"  MHC2 tag matrix (XYZ-domain wrapped): {FormatMatrix(mhc2Matrix)}\n" +
                $"  mode {(hdrMode ? "HDR (PQ-domain LUTs)" : "SDR")}{(target.WhitePointOnly ? ", WHITE-POINT-ONLY matrix" : "")}" +
                (!hdrMode ? $", tone LUTs {(perChannelGrayTracking
                    ? "PER-CHANNEL gray-tracking (ramp-fitted delta composed onto neutral)"
                        : characterization.HasPerChannelToneCurves
                            ? "per-channel (built from ramp-fitted curves)"
                            : "neutral (shared luminance curve)")}" : "") +
                (hdrMode ? $", header range {headerMinNits:F3}–{headerMaxNits:F0} nits, SDR white {monitor.SdrWhiteLevel:F0} nits" +
                           $", LUT source {(wireExactLuts ? "WIRE-EXACT FP16 ladder" : "SDR-mapped grayscale fallback")}" +
                           (ReferenceEquals(installedHdrLuts, hdrLutsOverride) && hdrLutsOverride != null
                               ? " (CLOSED-LOOP REFINED)" : "") : ""));

            // Override names support the live white-trim preview: it alternates between two
            // fixed names so each step forces the compositor to load fresh content instead
            // of trusting a possibly-cached profile, without littering the store.
            string profileName = profileNameOverride ?? BuildProfileName(monitor, target);
            string srcPath = Path.Combine(Path.GetTempPath(), profileName);

            try
            {
                // The internal description is what Windows Color Management displays — without
                // it the profile shows the template's leftover "SDR ACM: srgb_d50 [...]" text.
                //
                // colorimetry: rewrite the template's characterization primaries/white with
                // what the calibrated display presents. In HDR the builder deliberately keeps
                // the ordinary ICC TRCs sRGB: Windows apps and SDR content target that
                // presentation encoding under Advanced Color, while PQ belongs exclusively to
                // the MHC2 wire-domain payload. Exposing the target's PQ TRC here made Photoshop
                // color-manage sRGB images into PQ before DWM managed them again (washed-out
                // canvas in both of Photoshop's HDR display modes).
                //
                // lumiPeakNits: SDR installs carry the measured peak in the 'lumi' tag without
                // touching the MHC2 header range; HDR installs already patch lumi through
                // maxLuminanceNits (measured peak per the m7 provenance policy above).
                Mhc2ProfileBuilder.Build(template, srcPath, mhc2Matrix, toneR, toneG, toneB,
                    description: Path.GetFileNameWithoutExtension(profileName),
                    minLuminanceNits: headerMinNits, maxLuminanceNits: headerMaxNits,
                    colorimetry: matrixTarget,
                    lumiPeakNits: !hdrMode && characterization.PeakLuminance > 0
                        ? characterization.PeakLuminance
                        : null);

                if (hdrMode)
                {
                    if (!TryInstallVerified(srcPath, profileName,
                            AdvancedColorProfileAssociation.Platform, out bool newlyInstalled, out string? installError))
                        return new InstallResult(false, profileName, installError);

                    if (!AdvancedColorProfileAssociation.TryActivateInstalled(
                            monitor, profileName, out _, out string? associationError))
                    {
                        if (newlyInstalled)
                            AdvancedColorProfileAssociation.Platform.UninstallColorProfile(profileName, delete: true);
                        return new InstallResult(false, profileName, associationError);
                    }
                }
                else
                {
                    if (!Wcs.InstallColorProfile(null, srcPath))
                        Log.Info($"CalibrationProfileInstaller: InstallColorProfile returned false for {profileName} (may already exist).");
                    if (!Wcs.AssociateColorProfileWithDevice(null, srcPath, monitor.MonitorDevicePath))
                        return new InstallResult(false, profileName,
                            "Windows refused to associate the profile with the display. Make sure the monitor is active.");

                    if (!Wcs.WcsSetDefaultColorProfile(
                            Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                            monitor.MonitorDevicePath, Wcs.CPT_ICC, Wcs.CPST_PERCEPTUAL, 0, profileName))
                        Log.Info($"CalibrationProfileInstaller: WcsSetDefaultColorProfile returned false for {profileName}.");
                }

                Log.Info($"CalibrationProfileInstaller: Installed + set default '{profileName}' for {monitor.FriendlyName} ({(hdrMode ? "advanced color" : "SDR")} association).");
                return new InstallResult(true, profileName, null, installedHdrLuts, proofCertificate, compiledPayload);
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
        public static bool Disable(MonitorInfo monitor, string profileName)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrEmpty(profileName)) return false;
            try
            {
                // Remove from BOTH association lists (SDR and Advanced Color) — we don't track
                // which mode the profile was installed for, and removing from a list it isn't
                // in is harmless.
                Wcs.DisassociateColorProfileFromDevice(null, profileName, monitor.MonitorDevicePath);
                bool removed = AdvancedColorProfileAssociation.TryRemoveCurrentUser(
                    monitor, profileName, out string? error);
                if (!removed)
                    Log.Error($"CalibrationProfileInstaller: Advanced Color disable failed: {error}");
                Log.Info($"CalibrationProfileInstaller: Disabled '{profileName}' on {monitor.FriendlyName} (file kept in color store).");
                return removed;
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Disable failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Re-associates an already-installed profile after <see cref="Disable"/> - the
        /// on/off half of A/B comparing a fresh calibration. Returns false if Windows
        /// refuses the association.
        /// </summary>
        public static bool Reenable(MonitorInfo monitor, string profileName, bool hdrMode)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrEmpty(profileName)) return false;
            try
            {
                if (hdrMode)
                {
                    return AdvancedColorProfileAssociation.TryActivateInstalled(
                        monitor, profileName, out _, out _);
                }

                if (!Wcs.AssociateColorProfileWithDevice(null, profileName, monitor.MonitorDevicePath))
                    return false;
                Wcs.WcsSetDefaultColorProfile(
                    Wcs.WCS_PROFILE_MANAGEMENT_SCOPE.WCS_PROFILE_MANAGEMENT_SCOPE_CURRENT_USER,
                    monitor.MonitorDevicePath, Wcs.CPT_ICC, Wcs.CPST_PERCEPTUAL, 0, profileName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Reenable failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates and installs an ICC-safe copy of an existing Gloam HDR profile while
        /// preserving its measured MHC2 calibration payload. The new copy becomes the
        /// Advanced Color default, then the old association is retired (the old profile file
        /// remains installed for rollback). No colorimeter run is required.
        /// </summary>
        public static InstallResult RepairAdvancedColorProfile(
            MonitorInfo monitor, string existingProfileName, string repairedProfileName)
        {
            if (monitor == null || string.IsNullOrEmpty(monitor.MonitorDevicePath))
                return new InstallResult(false, "", "Monitor has no device path; cannot repair its profile.");
            if (string.IsNullOrWhiteSpace(existingProfileName))
                return new InstallResult(false, "", "Existing profile name is required.");
            if (string.IsNullOrWhiteSpace(repairedProfileName))
                return new InstallResult(false, "", "Repaired profile name is required.");

            existingProfileName = Path.GetFileName(existingProfileName.Trim());
            repairedProfileName = Path.GetFileName(repairedProfileName.Trim());
            if (string.Equals(existingProfileName, repairedProfileName, StringComparison.OrdinalIgnoreCase))
                return new InstallResult(false, repairedProfileName,
                    "The repaired profile must use a fresh filename so Windows reloads its transform.");
            if (!string.Equals(Path.GetExtension(repairedProfileName), ".icm", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(repairedProfileName), ".icc", StringComparison.OrdinalIgnoreCase))
                return new InstallResult(false, repairedProfileName, "Repaired profile must use an .icm or .icc extension.");

            var platform = AdvancedColorProfileAssociation.Platform;
            string colorStore = platform.ColorStoreDirectory;
            string sourcePath = Path.Combine(colorStore, existingProfileName);
            if (!File.Exists(sourcePath))
                return new InstallResult(false, repairedProfileName,
                    $"Existing profile was not found in the Windows color store: {existingProfileName}");

            string stagingDirectory = Path.Combine(Path.GetTempPath(), $"gloam-profile-repair-{Guid.NewGuid():N}");
            string stagedPath = Path.Combine(stagingDirectory, repairedProfileName);
            bool newlyInstalled = false;
            AdvancedColorProfileAssociation.ActivationReceipt? receipt = null;
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                Mhc2ProfileBuilder.RepairAdvancedColorIccCharacterization(
                    sourcePath, stagedPath, Path.GetFileNameWithoutExtension(repairedProfileName));

                if (!TryInstallVerified(stagedPath, repairedProfileName, platform,
                        out newlyInstalled, out string? installError))
                    return new InstallResult(false, repairedProfileName,
                        installError);

                if (!AdvancedColorProfileAssociation.TryActivateInstalled(
                        monitor, repairedProfileName, out receipt, out string? associationError, platform))
                {
                    if (newlyInstalled)
                        platform.UninstallColorProfile(repairedProfileName, delete: true);
                    return new InstallResult(false, repairedProfileName, associationError);
                }

                // New default is live before the old association is removed. Keep the old
                // profile installed so rollback does not depend on recalibration.
                if (!AdvancedColorProfileAssociation.TryRemoveCurrentUser(
                        monitor, existingProfileName, out string? retireError, platform))
                {
                    if (receipt != null)
                        AdvancedColorProfileAssociation.TryRollback(monitor, receipt, platform, out _);
                    if (newlyInstalled)
                        platform.UninstallColorProfile(repairedProfileName, delete: true);
                    return new InstallResult(false, repairedProfileName,
                        $"Could not retire the old association; the change was rolled back. {retireError}");
                }
                Log.Info($"CalibrationProfileInstaller: Repaired '{existingProfileName}' as '{repairedProfileName}' " +
                         $"for {monitor.FriendlyName}; MHC2 calibration preserved.");
                return new InstallResult(true, repairedProfileName, null);
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: HDR profile repair failed: {ex.Message}");
                return new InstallResult(false, repairedProfileName, ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Disables EVERY profile this app ever associated with the monitor (matched by our
        /// "&lt;monitor&gt; - &lt;target&gt; - &lt;date&gt;" naming), not just the recorded latest. A past
        /// bug installed several in one session; if a stale association ever becomes the
        /// fallback default, the "native" measurement would silently run through it and the
        /// new correction would be built against an already-corrected panel.
        /// </summary>
        public static void DisableAllForMonitor(MonitorInfo monitor)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrWhiteSpace(monitor.FriendlyName))
                return;
            try
            {
                // Derive the prefix from the SAME Sanitize() form BuildProfileName writes on
                // disk — an invalid char or a >40-char name makes the stored name diverge from
                // the raw FriendlyName, and a prefix off the raw name would match nothing, so
                // cleanup would silently leave a stale association behind.
                string prefix = BuildProfileNamePrefix(monitor);
                var names = AdvancedColorProfileAssociation.GetCurrentUserProfiles(monitor).ToList();

                foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    Disable(monitor, name);
                    Log.Info($"CalibrationProfileInstaller: Retired stale association '{name}' on {monitor.FriendlyName}.");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationProfileInstaller: DisableAllForMonitor failed (non-fatal): {ex.Message}");
            }
        }

        /// <summary>Fully removes a previously-installed calibration profile from a monitor.</summary>
        public static void Uninstall(MonitorInfo monitor, string profileName)
        {
            if (string.IsNullOrEmpty(monitor.MonitorDevicePath) || string.IsNullOrEmpty(profileName)) return;
            try
            {
                Wcs.DisassociateColorProfileFromDevice(null, profileName, monitor.MonitorDevicePath);
                AdvancedColorProfileAssociation.TryRemoveCurrentUser(monitor, profileName, out _);
                Wcs.UninstallColorProfile(null, profileName, true);
                Log.Info($"CalibrationProfileInstaller: Removed '{profileName}' from {monitor.FriendlyName}.");
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationProfileInstaller: Uninstall failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Installs a staged profile and verifies the color-store bytes. InstallColorProfile
        /// returns false both for real failures and for a filename that already exists, so a
        /// false return is accepted only when the existing file is byte-identical. A same-name
        /// different profile is never silently activated.
        /// </summary>
        private static bool TryInstallVerified(
            string stagedPath,
            string profileName,
            IAdvancedColorProfilePlatform platform,
            out bool newlyInstalled,
            out string? error)
        {
            newlyInstalled = platform.InstallColorProfile(stagedPath);
            try
            {
                if (AdvancedColorProfileAssociation.VerifyInstalledProfile(
                        stagedPath, profileName, platform, out error))
                    return true;

                if (newlyInstalled)
                    platform.UninstallColorProfile(profileName, delete: true);
                newlyInstalled = false;
                return false;
            }
            catch (Exception ex)
            {
                if (newlyInstalled)
                    platform.UninstallColorProfile(profileName, delete: true);
                newlyInstalled = false;
                error = $"Could not verify the installed profile: {ex.Message}";
                return false;
            }
        }

        /// <summary>Compact one-line "[r0; r1; r2]" rendering of a 3x3 matrix for the log.</summary>
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

            // ResourceExtractor's fallback when the app dir is read-only (unelevated under
            // Program Files): %LocalAppData%\Gloam.
            yield return AppPaths.DataDir;
        }

        /// <summary>
        /// Human-readable, explanatory, dated profile filename, e.g.
        /// "M27Q P - sRGB G2.2 - 2026-06-09 2245.icm". Sanitized for the file system; the
        /// trailing timestamp keeps each calibration distinct in Color Management.
        /// </summary>
        private static string BuildProfileName(MonitorInfo monitor, CalibrationTarget target)
        {
            string monitorName = SanitizeProfileNameComponent(string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Display" : monitor.FriendlyName);
            string targetName = Sanitize(ShortTargetName(target));
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HHmm");
            return $"{monitorName} - {targetName} - {stamp}.icm";
        }

        /// <summary>
        /// Prefix used by all Gloam-generated profile filenames for a monitor. This must
        /// match <see cref="BuildProfileName"/> so profile listing and stale-association
        /// cleanup can find profiles whose raw EDID names contained invalid filename
        /// characters or exceeded the 40-character component limit.
        /// </summary>
        public static string BuildProfileNamePrefix(MonitorInfo monitor)
            => SanitizeProfileNameComponent(string.IsNullOrWhiteSpace(monitor.FriendlyName) ? "Display" : monitor.FriendlyName) + " - ";

        private static string? ValidateCompiledOverride(Mhc2CompileResult payload, int expectedLutLength)
        {
            if (payload.Matrix == null || payload.Matrix.GetLength(0) != 3 || payload.Matrix.GetLength(1) != 3)
                return "matrix is not 3×3";
            if (payload.LutR == null || payload.LutG == null || payload.LutB == null ||
                payload.LutR.Length != expectedLutLength || payload.LutG.Length != expectedLutLength ||
                payload.LutB.Length != expectedLutLength)
                return $"LUTs must each contain exactly {expectedLutLength} entries";
            foreach (double value in payload.Matrix)
                if (!double.IsFinite(value) || value < -32768 || value >= 32768)
                    return "matrix contains a non-finite or non-s15Fixed16-representable coefficient";
            foreach (var lut in new[] { payload.LutR, payload.LutG, payload.LutB })
            {
                double previous = -1;
                foreach (double value in lut)
                {
                    if (!double.IsFinite(value) || value < 0 || value > 1 || value + 1e-12 < previous)
                        return "LUTs must be finite, [0,1], and monotone non-decreasing";
                    previous = value;
                }
            }
            return payload.Certificate == null ? "proof certificate is missing" : null;
        }

        private static string ShortTargetName(CalibrationTarget t)
        {
            // Compact, recognizable label rather than the long descriptive Name.
            string n = t.Name ?? "Custom";
            n = n.Replace("(", "").Replace(")", "").Replace("Gamma ", "G").Trim();
            if (t.WhitePointOnly) n += " WPonly";
            return n;
        }

        private static string Sanitize(string s)
            => SanitizeProfileNameComponent(s);

        public static string SanitizeProfileNameComponent(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            s = s.Replace("  ", " ").Trim();
            return s.Length > 40 ? s[..40].Trim() : s;
        }
    }
}
