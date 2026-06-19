using System;
using System.Collections.Generic;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Owns the policy for applying gamma to monitors: resolves the effective
    /// calibration (saved profile + night mode + app exclusions), coalesces rapid
    /// requests per monitor, and persists profile changes. Extracted from
    /// TrayViewModel so the apply pipeline has a single owner with no UI
    /// dependencies.
    /// </summary>
    public class GammaApplyService : IDisposable
    {
        private readonly DispwinRunner _dispwinRunner;
        private readonly SettingsManager _settingsManager;
        private readonly NightModeService _nightModeService;

        // Ramp guard: periodically verify the hardware still holds what we applied
        // and restore it when a fullscreen game or driver event stomps the VCGT.
        // Readback is ~free, so a 10s cadence costs nothing measurable.
        private readonly System.Timers.Timer _rampGuard;

        // Per-monitor coalescer: rapid slider drags collapse to one dispwin call per
        // completed invocation instead of queueing a backlog. See LatestValueCoalescer
        // for the concurrency contract and its tests for the proof.
        private readonly LatestValueCoalescer<IntPtr, (MonitorInfo Monitor, GammaMode Mode, CalibrationSettings Calibration, double WhiteLevel)> _coalescer;

        private HashSet<IntPtr> _blockedMonitors = new HashSet<IntPtr>();

        /// <summary>Hotkey override: forces day mode regardless of the schedule.</summary>
        public bool NightModeManuallyDisabled { get; set; }

        public GammaApplyService(DispwinRunner dispwinRunner, SettingsManager settingsManager, NightModeService nightModeService)
        {
            _dispwinRunner = dispwinRunner ?? throw new ArgumentNullException(nameof(dispwinRunner));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _nightModeService = nightModeService ?? throw new ArgumentNullException(nameof(nightModeService));

            _coalescer = new LatestValueCoalescer<IntPtr, (MonitorInfo, GammaMode, CalibrationSettings, double)>(
                (_, item) =>
                {
                    try
                    {
                        // Coalescer-time bypass re-check (TOCTOU close): RequestApply checked bypass
                        // on the caller thread, but the coalesced work runs later — a calibration may
                        // have called EnterBypassMode in between. Re-evaluate immediately before the
                        // hardware write so we never stomp the identity ramp the colorimeter is reading.
                        if (CalibrationStateManager.IsDeviceInBypass(item.Item1))
                        {
                            Log.Info($"GammaApplyService: skipping coalesced apply for {item.Item1.FriendlyName} (calibration bypass active)");
                            return;
                        }

                        // skipIfBypassed: re-check bypass inside ApplyGamma immediately before the
                        // hardware write, closing the window between this coalesced callback and the
                        // syscall (a calibration could call EnterBypassMode during LUT generation).
                        _dispwinRunner.ApplyGamma(item.Item1, item.Item2, item.Item4, item.Item3, calibrationProfile: null, skipIfBypassed: true);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"GammaApplyService: apply failed ({item.Item1.FriendlyName}): {ex.Message}");
                    }
                });

            _rampGuard = new System.Timers.Timer(10000) { AutoReset = true };
            _rampGuard.Elapsed += (_, _) =>
            {
                try { _dispwinRunner.VerifyAndRestoreRamps(); }
                catch (Exception ex) { Log.Error($"GammaApplyService: ramp guard tick failed: {ex.Message}"); }
            };
            _rampGuard.Start();
        }

        public void Dispose()
        {
            _rampGuard.Stop();
            _rampGuard.Dispose();
        }

        /// <summary>
        /// Replaces the blocked-monitor set (app exclusions). Returns true when the
        /// set actually changed, i.e. the caller should trigger a re-apply.
        /// </summary>
        public bool UpdateBlockedMonitors(HashSet<IntPtr> blocked)
        {
            if (_blockedMonitors.SetEquals(blocked)) return false;
            _blockedMonitors = blocked;
            return true;
        }

        public void RequestApply(MonitorInfo monitor, GammaMode mode, CalibrationSettings? manualCalibration = null, int? nightKelvinOverride = null)
        {
            // Mid-calibration guard: a calibration has bypassed this display's corrections so
            // the colorimeter measures the raw panel. The live apply path (slider, night mode,
            // app exclusions, display-change / resume re-apply) must not stomp that in-flight
            // ramp. Every live caller funnels through here, so this single skip also covers
            // ApplyAll and the InvalidateAppliedState()+ApplyAll() resume path.
            if (CalibrationStateManager.IsDeviceInBypass(monitor))
            {
                Log.Info($"GammaApplyService: skipping apply for {monitor.FriendlyName} (calibration bypass active)");
                return;
            }

            // Resolve the effective calibration synchronously on the caller's thread. This
            // keeps us reading fresh state (settings, night mode, exclusions) right at the
            // moment of the request, even if the actual apply is delayed by coalescing.
            int currentKelvin = nightKelvinOverride ?? _nightModeService.CurrentNightKelvin;
            if (NightModeManuallyDisabled) currentKelvin = 6500;
            if (_blockedMonitors.Contains(monitor.HMonitor)) currentKelvin = 6500;

            bool nightModeActive = nightKelvinOverride.HasValue || currentKelvin < 6450;

            // Composition with a native MHC2 calibration: the profile corrects the PANEL
            // (gamut, white point, tone tracking) at the compositor, which makes the display
            // behave like the ideal panel the mode LUTs already assume — so the user's gamma
            // preference composes CORRECTLY on top and must not be forced off. (The old
            // force-to-WindowsDefault here predates that: it guarded against the measured
            // VCGT correction curves double-applying, but the live path never passes those —
            // RequestApply always uses the profile-less ApplyGamma overload.)

            CalibrationSettings calibration;
            if (manualCalibration != null)
            {
                // Clone so later night-mode / offset mutations don't leak back into the caller's object.
                calibration = manualCalibration.Clone();
            }
            else
            {
                var profile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath) ?? new MonitorProfileData();
                calibration = profile.ToCalibrationSettings();
            }

            calibration.Temperature += calibration.TemperatureOffset;

            if (nightModeActive)
            {
                double nightShift = (currentKelvin - 6500) / 70.0;
                calibration.Temperature += nightShift;
                calibration.Algorithm = _settingsManager.NightMode.Algorithm;
                calibration.UseUltraWarmMode = _settingsManager.NightMode.UseUltraWarmMode;
            }

            // Extended range: -65.7 → 1900K, +50 → 10000K. See CalibrationSettings.Temperature
            // for why this goes past the slider's nominal -50..+50.
            calibration.Temperature = Math.Clamp(calibration.Temperature, -65.7, 50.0);

            // Update persistent state only for real (non-preview) applies.
            if (manualCalibration == null)
            {
                monitor.CurrentGamma = mode;
                if (!string.IsNullOrEmpty(monitor.MonitorDevicePath))
                {
                    _settingsManager.SetProfileForMonitor(monitor.MonitorDevicePath, mode);
                }
            }

            _coalescer.Submit(monitor.HMonitor, (monitor, mode, calibration, monitor.SdrWhiteLevel));
        }

        public void ApplyAll(IEnumerable<MonitorInfo> monitors)
        {
            int currentKelvin = _nightModeService.CurrentNightKelvin;
            if (NightModeManuallyDisabled) currentKelvin = 6500;
            bool nightModeActive = currentKelvin < 6450;

            foreach (var monitor in monitors)
            {
                var profile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath);
                var mode = profile?.GammaMode ?? monitor.CurrentGamma;

                if (mode == GammaMode.WindowsDefault && !nightModeActive) continue;

                RequestApply(monitor, mode);
            }
        }

        /// <summary>Resets all gamma tables (panic). Failures are logged, never thrown.</summary>
        public void ClearAll(IEnumerable<MonitorInfo> monitors)
        {
            foreach (var monitor in monitors)
            {
                try { _dispwinRunner.ClearGamma(monitor); }
                catch (Exception ex)
                {
                    Log.Error($"GammaApplyService: clear failed ({monitor.FriendlyName}): {ex.Message}");
                }
            }
        }

        /// <summary>See <see cref="DispwinRunner.InvalidateAppliedState"/>.</summary>
        public void InvalidateAppliedState() => _dispwinRunner.InvalidateAppliedState();
    }
}
