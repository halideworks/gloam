using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HDRGammaController.Core
{
    /// <summary>The picture-making goal of a per-game profile.</summary>
    public enum GamerPictureIntent
    {
        Reference,
        CompetitiveClarity,
        CinematicHdr,
        NightOps,
        Custom
    }

    /// <summary>How a game profile composes with Gloam's circadian schedule.</summary>
    public enum GamerNightPolicy
    {
        FollowSchedule,
        ForceDaylight,
        NightOps
    }

    /// <summary>Signal mode the user expects while a title is active.</summary>
    public enum GamerHdrExpectation
    {
        Automatic,
        RequireSdr,
        RequireHdr
    }

    /// <summary>Which connected displays receive a foreground game's profile.</summary>
    public enum GamerDisplayScope
    {
        WindowDisplays,
        AllDisplays,
        SpecificDisplay
    }

    /// <summary>
    /// Fail-closed boundary for automatic display control. Launcher manifests also contain
    /// tools, overlays, helpers and shell processes; none of those are allowed to own a game
    /// session even if an old settings file or a manual edit names one explicitly.
    /// </summary>
    public static class GamerExecutableSafety
    {
        private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
        {
            "applicationframehost.exe", "brave.exe", "cefsharp.browsersubprocess.exe",
            "chatgpt.exe", "chrome.exe", "codex.exe", "csrss.exe", "ctfmon.exe",
            "discord.exe", "dwm.exe", "epicgameslauncher.exe", "explorer.exe",
            "firefox.exe", "fpsvr.exe", "galaxyclient.exe", "galaxyclientservice.exe",
            "gamingservices.exe", "gloam.exe", "lockapp.exe", "msedge.exe", "obs64.exe",
            "opera.exe", "resolve.exe", "runtimebroker.exe", "searchapp.exe", "searchhost.exe",
            "services.exe", "shellexperiencehost.exe", "sihost.exe", "soundpad.exe",
            "startmenuexperiencehost.exe", "steam.exe", "steamwebhelper.exe", "svchost.exe",
            "systemsettings.exe", "taskmgr.exe", "textinputhost.exe", "update.exe",
            "voiceattack.exe", "vrmonitor.exe", "vrpathreg.exe", "vrserver.exe",
            "winlogon.exe", "xboxappservices.exe"
        };

        private static readonly string[] BlockedExecutableFragments =
        [
            "anticheat", "battleye", "bootstrapper", "cefsharp", "companion", "crash",
            "dxsetup", "easyanticheat", "installer", "launcher", "overlay", "prereq",
            "redist", "reporter", "setup", "subprocess", "unins", "updater", "vc_redist",
            "webhelper", "webview"
        ];

        private static readonly HashSet<string> BlockedTitles = new(StringComparer.OrdinalIgnoreCase)
        {
            "fpsVR", "OVR Toolkit", "Soundpad", "SteamVR", "VoiceAttack", "XSOverlay", "YUR"
        };

        private static readonly string[] BlockedTitleFragments =
        [
            "dedicated server", "digital companion", "modding tools", "redistributable",
            "soundtrack", "steam linux runtime", "workshop tools"
        ];

        public static bool IsSafeProfileTarget(string? executableName, string? displayName = null) =>
            RejectionReason(executableName, displayName) == null;

        public static string? RejectionReason(string? executableName, string? displayName = null)
        {
            string app = AppExclusionRule.NormalizeAppName(executableName);
            if (app.Length == 0) return "No executable was identified.";
            if (BlockedExecutables.Contains(app)) return "That executable is a desktop, launcher, or utility process.";

            string key = Path.GetFileNameWithoutExtension(app).ToLowerInvariant();
            if (BlockedExecutableFragments.Any(key.Contains))
                return "That executable looks like a launcher, helper, overlay, or installer.";
            if (key.EndsWith("server", StringComparison.Ordinal) || key.Contains("dedicatedserver"))
                return "Dedicated server executables cannot own a display profile.";

            string title = displayName?.Trim() ?? string.Empty;
            if (BlockedTitles.Contains(title)) return "That library entry is a utility, not a game.";
            string titleKey = title.ToLowerInvariant();
            if (BlockedTitleFragments.Any(titleKey.Contains) || titleKey.EndsWith(" sdk", StringComparison.Ordinal))
                return "That library entry is a tool or companion package, not a game.";
            if (titleKey.StartsWith("proton ", StringComparison.Ordinal))
                return "Compatibility runtimes cannot own a display profile.";

            return null;
        }
    }

    /// <summary>
    /// Persistent per-executable gamer profile. It contains rendering intent and launch-time
    /// policy only; measured panel correction stays in the monitor calibration profile and is
    /// always composed first by the normal apply pipeline.
    /// </summary>
    public sealed class GamerProfileRule
    {
        public string AppName { get; set; } = string.Empty;
        /// <summary>
        /// Optional canonical executable path captured from launcher metadata. When present,
        /// automatic activation requires both name and path so generic names such as game.exe
        /// cannot match an unrelated process.
        /// </summary>
        public string? ExecutablePath { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public GamerPictureIntent PictureIntent { get; set; } = GamerPictureIntent.CompetitiveClarity;
        public GamerDisplayScope DisplayScope { get; set; } = GamerDisplayScope.WindowDisplays;
        public string? MonitorDevicePath { get; set; }
        public GamerHdrExpectation HdrExpectation { get; set; } = GamerHdrExpectation.Automatic;

        public bool OverrideGamma { get; set; } = true;
        public GammaMode GammaMode { get; set; } = GammaMode.Gamma22;

        /// <summary>Strength of the black-anchored visibility toe, 0..1.</summary>
        public double ShadowDetailStrength { get; set; } = 0.55;

        /// <summary>
        /// Linear-light point where the visibility toe rejoins the unmodified curve. Values
        /// above this point are bit-identical to the underlying calibration.
        /// </summary>
        public double ShadowDetailPivot { get; set; } = 0.10;

        public GamerNightPolicy NightPolicy { get; set; } = GamerNightPolicy.FollowSchedule;
        public int NightOpsKelvin { get; set; } = 3400;
        public double NightOpsStrength { get; set; } = 0.55;
        /// <summary>
        /// Optional hard melanopic-EDI ceiling. Zero disables the governor. Night Ops is a
        /// color-preserving warm render by default; users opt into a dose ceiling when they
        /// explicitly prefer circadian compliance over keeping the selected look intact.
        /// </summary>
        public double NightOpsMelanopicCeiling { get; set; }

        /// <summary>
        /// Freeze the schedule-derived state captured at game activation. The ramp guard may
        /// still restore a verified driver/game reset, but ordinary fades cannot rewrite it.
        /// </summary>
        public bool GameplayLock { get; set; } = true;

        /// <summary>Optional measured/recommended game-menu values. Zero means unknown.</summary>
        public double PaperWhiteNits { get; set; }
        public double PeakNits { get; set; }
        public double BlackLevelNits { get; set; }

        /// <summary>Last foreground activation, used only to order the recent-games picker.</summary>
        public DateTime? LastUsedUtc { get; set; }

        public GamerProfileRule Clone() => new()
        {
            AppName = AppName,
            ExecutablePath = ExecutablePath,
            DisplayName = DisplayName,
            Enabled = Enabled,
            PictureIntent = PictureIntent,
            DisplayScope = DisplayScope,
            MonitorDevicePath = MonitorDevicePath,
            HdrExpectation = HdrExpectation,
            OverrideGamma = OverrideGamma,
            GammaMode = GammaMode,
            ShadowDetailStrength = ShadowDetailStrength,
            ShadowDetailPivot = ShadowDetailPivot,
            NightPolicy = NightPolicy,
            NightOpsKelvin = NightOpsKelvin,
            NightOpsStrength = NightOpsStrength,
            NightOpsMelanopicCeiling = NightOpsMelanopicCeiling,
            GameplayLock = GameplayLock,
            PaperWhiteNits = PaperWhiteNits,
            PeakNits = PeakNits,
            BlackLevelNits = BlackLevelNits,
            LastUsedUtc = LastUsedUtc
        };

        public GamerProfileRule Sanitized()
        {
            var copy = Clone();
            copy.AppName = AppExclusionRule.NormalizeAppName(copy.AppName);
            copy.ExecutablePath = SanitizeExecutablePath(copy.ExecutablePath);
            copy.DisplayName = SanitizeLabel(copy.DisplayName, copy.AppName);
            copy.PictureIntent = Enum.IsDefined(typeof(GamerPictureIntent), copy.PictureIntent)
                ? copy.PictureIntent : GamerPictureIntent.CompetitiveClarity;
            copy.DisplayScope = Enum.IsDefined(typeof(GamerDisplayScope), copy.DisplayScope)
                ? copy.DisplayScope : GamerDisplayScope.WindowDisplays;
            copy.HdrExpectation = Enum.IsDefined(typeof(GamerHdrExpectation), copy.HdrExpectation)
                ? copy.HdrExpectation : GamerHdrExpectation.Automatic;
            copy.GammaMode = Enum.IsDefined(typeof(GammaMode), copy.GammaMode)
                ? copy.GammaMode : GammaMode.Gamma22;
            copy.NightPolicy = Enum.IsDefined(typeof(GamerNightPolicy), copy.NightPolicy)
                ? copy.NightPolicy : GamerNightPolicy.FollowSchedule;
            copy.ShadowDetailStrength = ClampFinite(copy.ShadowDetailStrength, 0.0, 1.0, 0.0);
            copy.ShadowDetailPivot = ClampFinite(copy.ShadowDetailPivot, 0.02, 0.25, 0.10);
            copy.NightOpsKelvin = Math.Clamp(copy.NightOpsKelvin, 1900, 6500);
            copy.NightOpsStrength = ClampFinite(copy.NightOpsStrength, 0.0, 1.0, 0.55);
            copy.NightOpsMelanopicCeiling = ClampFinite(copy.NightOpsMelanopicCeiling, 0.0, 1000.0, 0.0);
            copy.PaperWhiteNits = ClampFinite(copy.PaperWhiteNits, 0.0, 1000.0, 0.0);
            copy.PeakNits = ClampFinite(copy.PeakNits, 0.0, 10000.0, 0.0);
            copy.BlackLevelNits = ClampFinite(copy.BlackLevelNits, 0.0, 10.0, 0.0);
            if (copy.LastUsedUtc.HasValue)
                copy.LastUsedUtc = copy.LastUsedUtc.Value.ToUniversalTime();
            copy.MonitorDevicePath = string.IsNullOrWhiteSpace(copy.MonitorDevicePath)
                ? null : copy.MonitorDevicePath.Trim();
            if (copy.DisplayScope == GamerDisplayScope.SpecificDisplay && copy.MonitorDevicePath == null)
                copy.DisplayScope = GamerDisplayScope.WindowDisplays;
            return copy;
        }

        public bool SemanticallyEquals(GamerProfileRule? other)
        {
            if (other == null) return false;
            GamerProfileRule a = Sanitized();
            GamerProfileRule b = other.Sanitized();
            return string.Equals(a.AppName, b.AppName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
                && a.Enabled == b.Enabled
                && a.PictureIntent == b.PictureIntent
                && a.DisplayScope == b.DisplayScope
                && string.Equals(a.MonitorDevicePath, b.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)
                && a.HdrExpectation == b.HdrExpectation
                && a.OverrideGamma == b.OverrideGamma
                && a.GammaMode == b.GammaMode
                && a.ShadowDetailStrength == b.ShadowDetailStrength
                && a.ShadowDetailPivot == b.ShadowDetailPivot
                && a.NightPolicy == b.NightPolicy
                && a.NightOpsKelvin == b.NightOpsKelvin
                && a.NightOpsStrength == b.NightOpsStrength
                && a.NightOpsMelanopicCeiling == b.NightOpsMelanopicCeiling
                && a.GameplayLock == b.GameplayLock
                && a.PaperWhiteNits == b.PaperWhiteNits
                && a.PeakNits == b.PeakNits
                && a.BlackLevelNits == b.BlackLevelNits
                && a.LastUsedUtc == b.LastUsedUtc;
        }

        private static string SanitizeLabel(string? value, string fallback)
        {
            string label = value?.Trim() ?? string.Empty;
            if (label.Length > 80) label = label[..80];
            if (label.Length > 0) return label;
            return Path.GetFileNameWithoutExtension(fallback);
        }

        private static string? SanitizeExecutablePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try
            {
                string path = Path.GetFullPath(value.Trim());
                return path.Length <= 1024 ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }

    /// <summary>Reference presets used when a title is first added.</summary>
    public static class GamerPresetCatalog
    {
        public static GamerProfileRule Create(string appName, GamerPictureIntent intent)
        {
            var profile = new GamerProfileRule
            {
                AppName = AppExclusionRule.NormalizeAppName(appName),
                PictureIntent = intent
            };
            Apply(profile, intent);
            return profile.Sanitized();
        }

        public static void Apply(GamerProfileRule profile, GamerPictureIntent intent)
        {
            ArgumentNullException.ThrowIfNull(profile);
            profile.PictureIntent = intent;
            if (intent == GamerPictureIntent.Custom) return;

            profile.OverrideGamma = true;
            profile.GameplayLock = true;
            profile.HdrExpectation = GamerHdrExpectation.Automatic;

            switch (intent)
            {
                case GamerPictureIntent.Reference:
                    profile.OverrideGamma = false;
                    profile.GammaMode = GammaMode.Gamma22;
                    profile.ShadowDetailStrength = 0.0;
                    profile.ShadowDetailPivot = 0.10;
                    profile.NightPolicy = GamerNightPolicy.ForceDaylight;
                    break;
                case GamerPictureIntent.CinematicHdr:
                    // PQ already defines the HDR transfer function. Preserve it; applying an
                    // SDR 2.4 correction here would reshape genuine HDR shadow/midtone codes.
                    profile.GammaMode = GammaMode.WindowsDefault;
                    profile.ShadowDetailStrength = 0.0;
                    profile.ShadowDetailPivot = 0.10;
                    profile.NightPolicy = GamerNightPolicy.ForceDaylight;
                    profile.HdrExpectation = GamerHdrExpectation.RequireHdr;
                    break;
                case GamerPictureIntent.NightOps:
                    profile.GammaMode = GammaMode.Gamma22;
                    profile.ShadowDetailStrength = 0.45;
                    profile.ShadowDetailPivot = 0.10;
                    profile.NightPolicy = GamerNightPolicy.NightOps;
                    profile.NightOpsKelvin = 3400;
                    profile.NightOpsStrength = 0.55;
                    // A hard dose ceiling can substantially alter both brightness and white
                    // point. Keep it opt-in instead of silently collapsing Night Ops toward
                    // one governor-selected rendering.
                    profile.NightOpsMelanopicCeiling = 0.0;
                    break;
                default:
                    profile.GammaMode = GammaMode.Gamma22;
                    profile.ShadowDetailStrength = 0.55;
                    profile.ShadowDetailPivot = 0.10;
                    profile.NightPolicy = GamerNightPolicy.FollowSchedule;
                    break;
            }
        }
    }

    /// <summary>
    /// Black-anchored competitive visibility curve. The analytic toe is C1-continuous at
    /// the pivot, leaves the entire range above the pivot unchanged, and remains monotonic
    /// for every allowed setting. It is deliberately evaluated in linear light.
    /// </summary>
    public static class GamerShadowVisibility
    {
        private const double MaximumToeAmount = 1.5;

        public static double Apply(double linear, double strength, double pivot = 0.10)
        {
            linear = double.IsFinite(linear) ? Math.Clamp(linear, 0.0, 1.0) : 0.0;
            strength = double.IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 0.0;
            pivot = double.IsFinite(pivot) ? Math.Clamp(pivot, 0.02, 0.25) : 0.10;
            if (strength <= 0.0 || linear <= 0.0 || linear >= pivot) return linear;

            double u = linear / pivot;
            double shoulder = 1.0 - u;
            double lifted = linear + MaximumToeAmount * strength * linear * shoulder * shoulder;
            return Math.Min(lifted, pivot);
        }

        /// <summary>Minimum analytic slope inside the toe; always positive at valid strength.</summary>
        public static double MinimumSlope(double strength)
        {
            strength = double.IsFinite(strength) ? Math.Clamp(strength, 0.0, 1.0) : 0.0;
            return 1.0 - MaximumToeAmount * strength / 3.0;
        }
    }

    public enum GamerDiagnosticSeverity
    {
        Information,
        Warning,
        Critical
    }

    public sealed record GamerSignalDiagnostic(
        string Code,
        GamerDiagnosticSeverity Severity,
        string Message);

    /// <summary>Launch-time checks over facts Gloam can verify from the Windows output path.</summary>
    public static class GamerSignalDiagnostics
    {
        public static IReadOnlyList<GamerSignalDiagnostic> Evaluate(
            GamerProfileRule profile,
            MonitorInfo monitor,
            MonitorProfileData? monitorProfile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(monitor);
            profile = profile.Sanitized();
            var findings = new List<GamerSignalDiagnostic>();

            if (profile.HdrExpectation == GamerHdrExpectation.RequireHdr && !monitor.IsHdrActive)
            {
                findings.Add(new("HDR_REQUIRED_INACTIVE", GamerDiagnosticSeverity.Critical,
                    "This profile expects HDR, but Windows is outputting SDR on this display."));
            }
            else if (profile.HdrExpectation == GamerHdrExpectation.RequireSdr && monitor.IsHdrActive)
            {
                findings.Add(new("SDR_REQUIRED_HDR_ACTIVE", GamerDiagnosticSeverity.Warning,
                    "This profile expects SDR, but Windows Advanced Color HDR is active."));
            }

            if (monitor.IsHdrActive)
            {
                if (!DxgiColorSpaceInfo.IsHdr(monitor.DxgiColorSpace))
                {
                    findings.Add(new("HDR_COLOR_SPACE_MISMATCH", GamerDiagnosticSeverity.Critical,
                        "Windows reports HDR active while the DXGI output color space is not PQ/HLG HDR."));
                }

                if (monitor.BitsPerColor is > 0 and < 3)
                {
                    findings.Add(new("HDR_LOW_BIT_DEPTH", GamerDiagnosticSeverity.Warning,
                        $"The active HDR output is {DxgiColorSpaceInfo.DecodeBitsPerColor(monitor.BitsPerColor)}; 10-bit or higher is preferred."));
                }

                if (monitor.HdrPeakNits <= 0)
                {
                    findings.Add(new("HDR_PEAK_UNKNOWN", GamerDiagnosticSeverity.Information,
                        "The display did not report a usable HDR peak; game-menu peak guidance remains unverified."));
                }
                else if (MonitorInfo.SanitizeSdrWhiteLevel(monitor.SdrWhiteLevel) > monitor.HdrPeakNits * 0.80)
                {
                    findings.Add(new("HDR_HEADROOM_LOW", GamerDiagnosticSeverity.Warning,
                        "Windows SDR reference white consumes over 80% of the panel's reported HDR peak, leaving little highlight headroom."));
                }

                if (profile.OverrideGamma && profile.GammaMode != GammaMode.WindowsDefault)
                {
                    findings.Add(new("HDR_GAMMA_OVERRIDE", GamerDiagnosticSeverity.Information,
                        $"The profile applies {GammaLabel(profile.GammaMode)} inside the HDR output path; use Windows Default for reference PQ tracking."));
                }
            }

            bool hasMeasuredProfile = monitorProfile != null &&
                (!string.IsNullOrWhiteSpace(monitorProfile.CalibrationProfileId) ||
                 !string.IsNullOrWhiteSpace(monitorProfile.Mhc2ProfileName));
            if (!hasMeasuredProfile)
            {
                findings.Add(new("DISPLAY_UNMEASURED", GamerDiagnosticSeverity.Information,
                    "This display has no active measured calibration; visibility and HDR guidance are model-based."));
            }

            if (!profile.GameplayLock)
            {
                findings.Add(new("GAMEPLAY_LOCK_OFF", GamerDiagnosticSeverity.Information,
                    "Gameplay Lock is off, so scheduled night-mode changes may write a new ramp during play."));
            }

            return findings;
        }

        private static string GammaLabel(GammaMode mode) => mode switch
        {
            GammaMode.Gamma22 => "Gamma 2.2",
            GammaMode.Gamma24 => "Gamma 2.4",
            _ => "Windows Default"
        };
    }

    /// <summary>Read-only status exposed to the dashboard for one active display session.</summary>
    public sealed record GamerSessionSnapshot(
        IntPtr HMonitor,
        string MonitorDevicePath,
        string MonitorName,
        string AppName,
        string DisplayName,
        GamerPictureIntent PictureIntent,
        bool GameplayLock,
        int LockedKelvin,
        GammaMode EffectiveGamma,
        double ShadowDetailStrength,
        double ShadowDetailPivot,
        GamerNightPolicy NightPolicy,
        DateTime StartedUtc,
        IReadOnlyList<GamerSignalDiagnostic> Diagnostics);

    /// <summary>One foreground-game assignment produced by window/display intersection.</summary>
    public sealed record GamerSessionAssignment(MonitorInfo Monitor, GamerProfileRule Profile);
}
