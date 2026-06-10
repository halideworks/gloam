using System;
using System.Collections.Generic;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Manages display correction state during calibration.
    /// Saves the current state, bypasses all corrections for accurate measurements,
    /// and restores or applies new corrections after calibration.
    /// </summary>
    public class CalibrationStateManager
    {
        private readonly DispwinRunner _dispwinRunner;
        private readonly NightModeService _nightModeService;
        private readonly Dictionary<string, SavedMonitorState> _savedStates = new();
        private bool _wasBypassActive;
        private DateTime? _nightModePauseEndTime;

        /// <summary>
        /// Saved state for a single monitor.
        /// </summary>
        public class SavedMonitorState
        {
            public required string MonitorDevicePath { get; init; }
            public required MonitorInfo Monitor { get; init; }
            public GammaMode GammaMode { get; init; }
            public CalibrationSettings? CalibrationSettings { get; init; }
            public bool WasActive { get; init; }
        }

        /// <summary>
        /// Gets whether bypass mode is currently active.
        /// </summary>
        public bool IsBypassActive => _wasBypassActive;

        /// <summary>
        /// Gets the saved states for all monitors.
        /// </summary>
        public IReadOnlyDictionary<string, SavedMonitorState> SavedStates => _savedStates;

        public CalibrationStateManager(DispwinRunner dispwinRunner, NightModeService nightModeService)
        {
            _dispwinRunner = dispwinRunner ?? throw new ArgumentNullException(nameof(dispwinRunner));
            _nightModeService = nightModeService ?? throw new ArgumentNullException(nameof(nightModeService));
        }

        /// <summary>
        /// Saves the current correction state for a monitor and enters bypass mode.
        /// All color corrections (gamma, night mode, etc.) are disabled for accurate measurement.
        /// </summary>
        /// <param name="monitor">The monitor being calibrated.</param>
        /// <param name="currentMode">The current gamma mode applied.</param>
        /// <param name="currentSettings">The current calibration settings (temperature, etc.).</param>
        public void EnterBypassMode(MonitorInfo monitor, GammaMode currentMode, CalibrationSettings? currentSettings = null)
        {
            if (monitor == null) throw new ArgumentNullException(nameof(monitor));

            // Save current state
            var devicePath = monitor.MonitorDevicePath ?? monitor.DeviceName ?? "unknown";
            _savedStates[devicePath] = new SavedMonitorState
            {
                MonitorDevicePath = devicePath,
                Monitor = monitor,
                GammaMode = currentMode,
                CalibrationSettings = currentSettings,
                WasActive = currentMode != GammaMode.WindowsDefault || _nightModeService.IsNightModeActive
            };

            // Pause night mode for the duration of calibration (generous timeout)
            // We'll explicitly restore it later, but this prevents auto-updates
            _nightModePauseEndTime = DateTime.Now.AddHours(2);
            _nightModeService.PauseUntil(_nightModePauseEndTime.Value);

            // Clear all gamma corrections - set to identity/passthrough
            try
            {
                _dispwinRunner.ClearGamma(monitor);
                Log.Info($"CalibrationStateManager: Entered bypass mode for {monitor.FriendlyName ?? devicePath}");
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationStateManager: Failed to clear gamma: {ex.Message}");
                throw;
            }

            _wasBypassActive = true;
        }

        /// <summary>
        /// Loads a raw per-channel correction onto the monitor under test. Used by the
        /// closed loop to apply a candidate correction and re-measure it. No-op unless we're
        /// in bypass mode (i.e. a calibration is in progress).
        /// </summary>
        public void ApplyCorrectionLut(MonitorInfo monitor, double[] r, double[] g, double[] b)
        {
            if (!_wasBypassActive) return;
            _dispwinRunner.ApplyCorrectionLut(monitor, r, g, b);
        }

        /// <summary>
        /// Exits bypass mode and restores the previous correction state.
        /// Call this if calibration is cancelled or fails.
        /// </summary>
        public void RestorePreviousState()
        {
            if (!_wasBypassActive) return;

            foreach (var state in _savedStates.Values)
            {
                try
                {
                    if (state.WasActive)
                    {
                        // Re-apply the previous gamma mode and settings
                        _dispwinRunner.ApplyGamma(
                            state.Monitor,
                            state.GammaMode,
                            state.Monitor.SdrWhiteLevel,
                            state.CalibrationSettings ?? CalibrationSettings.Default);

                        Log.Info($"CalibrationStateManager: Restored previous state for {state.Monitor.FriendlyName}");
                    }
                    else
                    {
                        // "Defaults" means an identity ramp — but the closed loop (or a
                        // crashed run) may have left its last candidate correction loaded.
                        // Skipping here would strand that ramp on screen, so assert identity.
                        _dispwinRunner.ClearGamma(state.Monitor);
                        Log.Info($"CalibrationStateManager: Monitor {state.Monitor.FriendlyName} was at defaults; cleared ramp to identity");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"CalibrationStateManager: Failed to restore state for {state.Monitor.FriendlyName}: {ex.Message}");
                }
            }

            // Resume night mode service
            ResumeNightMode();
            _wasBypassActive = false;
        }

        /// <summary>
        /// Applies the new calibration LUT without any additional corrections.
        /// The display will show only the calibration correction.
        /// </summary>
        /// <param name="monitor">The calibrated monitor.</param>
        /// <param name="lut">The generated 3D LUT.</param>
        public void ApplyCalibrationOnly(MonitorInfo monitor, Lut3D? lut)
        {
            if (!_wasBypassActive) return;

            try
            {
                // For now, we stay in bypass mode (identity) since the LUT would need to be
                // integrated into the display pipeline. The user can export the LUT and load it
                // in their color management system.
                //
                // In the future, this could:
                // 1. Apply the LUT via MHC2 profile
                // 2. Use a custom shader pipeline
                // 3. Integrate with Windows color management

                Log.Info($"CalibrationStateManager: Applied calibration-only mode for {monitor.FriendlyName}");
                Log.Info("Note: 3D LUT application requires integration with display color management.");

                // Keep night mode paused - pure calibration view
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationStateManager: Failed to apply calibration: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies the new calibration LUT combined with the previous correction settings.
        /// This shows what the display will look like in normal use with calibration applied.
        /// </summary>
        /// <param name="monitor">The calibrated monitor.</param>
        /// <param name="lut">The generated 3D LUT.</param>
        public void ApplyCalibrationWithPreviousSettings(MonitorInfo monitor, Lut3D? lut)
        {
            if (!_wasBypassActive) return;

            var devicePath = monitor.MonitorDevicePath ?? monitor.DeviceName ?? "unknown";
            if (!_savedStates.TryGetValue(devicePath, out var state))
            {
                Log.Info($"CalibrationStateManager: No saved state for {devicePath}");
                return;
            }

            try
            {
                // Re-apply the previous gamma mode and settings
                // In the future, this would also incorporate the LUT
                _dispwinRunner.ApplyGamma(
                    monitor,
                    state.GammaMode,
                    monitor.SdrWhiteLevel,
                    state.CalibrationSettings ?? CalibrationSettings.Default);

                Log.Info($"CalibrationStateManager: Applied calibration with previous settings for {monitor.FriendlyName}");

                // Resume night mode so user can see the full effect
                ResumeNightMode();
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationStateManager: Failed to apply with previous settings: {ex.Message}");
            }
        }

        /// <summary>
        /// Cleans up and exits bypass mode without applying any changes.
        /// </summary>
        public void ExitBypassMode()
        {
            ResumeNightMode();
            _savedStates.Clear();
            _wasBypassActive = false;
        }

        private void ResumeNightMode()
        {
            // Resume by pausing until "now" which effectively unpauses
            _nightModeService.PauseUntil(DateTime.Now);
            _nightModePauseEndTime = null;
        }
    }
}
