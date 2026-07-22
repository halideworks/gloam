using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core
{
    /// <summary>Schema migrations that change persisted meaning between releases.</summary>
    internal static class SettingsMigration
    {
        internal static void Apply(SettingsManager.SettingsData data)
        {
            // Schema 3 introduced Game Lab with a mandatory 10 mel-lx Night Ops ceiling.
            // Schema 4 makes that governor opt-in. Preserve any explicitly chosen value.
            if (data.SchemaVersion > 3 || data.GamerProfiles == null) return;
            foreach (GamerProfileRule profile in data.GamerProfiles)
            {
                if (profile != null &&
                    profile.NightPolicy == GamerNightPolicy.NightOps &&
                    Math.Abs(profile.NightOpsMelanopicCeiling - 10.0) < 0.0001)
                {
                    profile.NightOpsMelanopicCeiling = 0.0;
                }
            }
        }
    }

    /// <summary>
    /// Canonicalizes untrusted or hand-edited settings into the runtime domain. This has no
    /// file-system side effects, so loading, imports, and tests all use one policy.
    /// </summary>
    internal static class SettingsNormalization
    {
        internal static List<GamerProfileRule> GamerProfiles(IEnumerable<GamerProfileRule>? profiles)
        {
            var normalized = new List<GamerProfileRule>();
            if (profiles == null) return normalized;

            foreach (var source in profiles)
            {
                if (source == null) continue;
                GamerProfileRule profile = source.Sanitized();
                if (profile.AppName.Length == 0) continue;
                if (!GamerExecutableSafety.IsSafeProfileTarget(profile.AppName, profile.DisplayName))
                {
                    Log.Info($"SettingsNormalization: removed unsafe game-profile target '{profile.AppName}' ({profile.DisplayName}).");
                    continue;
                }

                int duplicate = normalized.FindIndex(existing =>
                    existing.AppName.Equals(profile.AppName, StringComparison.OrdinalIgnoreCase));
                if (duplicate >= 0)
                    normalized[duplicate] = profile;
                else
                    normalized.Add(profile);
            }
            return normalized;
        }

        internal static List<AppExclusionRule> ExcludedApps(IEnumerable<AppExclusionRule?>? apps)
        {
            var normalized = new List<AppExclusionRule>();
            if (apps == null) return normalized;

            foreach (var rule in apps)
            {
                if (rule == null) continue;
                string appName = AppExclusionRule.NormalizeAppName(rule.AppName);
                if (appName.Length == 0) continue;

                var duplicate = normalized.FirstOrDefault(existing =>
                    existing.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase));
                if (duplicate != null)
                {
                    duplicate.FullDisable |= rule.FullDisable;
                    continue;
                }

                normalized.Add(new AppExclusionRule
                {
                    AppName = appName,
                    FullDisable = rule.FullDisable,
                });
            }
            return normalized;
        }

        internal static void Validate(SettingsManager.SettingsData data)
        {
            ArgumentNullException.ThrowIfNull(data);
            data.MonitorProfiles ??= new Dictionary<string, MonitorProfileData>();
            data.NightMode ??= new NightModeSettingsData();
            data.ExcludedApps = ExcludedApps(data.ExcludedApps);
            data.GamerProfiles = GamerProfiles(data.GamerProfiles);

            foreach (var profile in data.MonitorProfiles.Values)
            {
                if (profile == null) continue;
                profile.Brightness = ClampFinite(profile.Brightness, 10.0, 100.0, 100.0);
                profile.Temperature = ClampFinite(profile.Temperature, -50.0, 50.0, 0.0);
                profile.TemperatureOffset = ClampFinite(profile.TemperatureOffset, -50.0, 50.0, 0.0);
                profile.Tint = ClampFinite(profile.Tint, -50.0, 50.0, 0.0);
                profile.RedGain = ClampFinite(profile.RedGain, 0.5, 1.5, 1.0);
                profile.GreenGain = ClampFinite(profile.GreenGain, 0.5, 1.5, 1.0);
                profile.BlueGain = ClampFinite(profile.BlueGain, 0.5, 1.5, 1.0);
                profile.RedOffset = ClampFinite(profile.RedOffset, -0.5, 0.5, 0.0);
                profile.GreenOffset = ClampFinite(profile.GreenOffset, -0.5, 0.5, 0.0);
                profile.BlueOffset = ClampFinite(profile.BlueOffset, -0.5, 0.5, 0.0);

                if (profile.CalibrationProfileId != null &&
                    !Guid.TryParseExact(profile.CalibrationProfileId, "N", out _) &&
                    !Guid.TryParseExact(profile.CalibrationProfileId, "D", out _))
                {
                    profile.CalibrationProfileId = null;
                    profile.UseCalibrationForGamma = false;
                }
            }

            var night = data.NightMode;
            if (night != null)
            {
                if (night.Latitude.HasValue)
                    night.Latitude = ClampFinite(night.Latitude.Value, -90.0, 90.0, 0.0);
                if (night.Longitude.HasValue)
                    night.Longitude = ClampFinite(night.Longitude.Value, -180.0, 180.0, 0.0);
                night.TemperatureKelvin = Math.Clamp(night.TemperatureKelvin, 1900, 6500);
                night.FadeMinutes = Math.Clamp(night.FadeMinutes, 0, 120);
                if (night.Algorithm == NightModeAlgorithm.BlueReduction ||
                    !Enum.IsDefined(typeof(NightModeAlgorithm), night.Algorithm))
                    night.Algorithm = NightModeAlgorithm.Perceptual;
                night.PerceptualStrength = ClampFinite(
                    night.PerceptualStrength, 0.0, 1.0, ColorAdjustments.DefaultPerceptualStrength);

                if (night.Schedule != null)
                {
                    foreach (var point in night.Schedule)
                    {
                        if (point == null) continue;
                        point.TargetKelvin = Math.Clamp(point.TargetKelvin, 1900, 6500);
                        point.FadeMinutes = Math.Clamp(point.FadeMinutes, 0, 120);
                        point.OffsetMinutes = ClampFinite(point.OffsetMinutes, -120.0, 120.0, 0.0);
                    }
                }
            }

            data.WindowBounds ??= new Dictionary<string, WindowBoundsData>();
            foreach (var key in data.WindowBounds.Keys.ToList())
            {
                var bounds = data.WindowBounds[key];
                if (bounds == null ||
                    !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) ||
                    !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                    bounds.Width < 320 || bounds.Height < 240)
                    data.WindowBounds.Remove(key);
            }

            data.UiSectionExpanded ??= new Dictionary<string, bool>();
            foreach (string key in data.UiSectionExpanded.Keys.ToList())
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 80)
                    data.UiSectionExpanded.Remove(key);
            }
        }

        private static double ClampFinite(double value, double min, double max, double fallback) =>
            double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    }
}
