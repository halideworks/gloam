using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Owns the tray-launched setup → calibration → back-to-setup window chain and the
    /// single colorimeter's ownership transfers between those windows.
    /// </summary>
    internal sealed class CalibrationLaunchCoordinator
    {
        private readonly SettingsManager _settings;
        private readonly DispwinRunner _dispwin;
        private readonly NightModeService _nightMode;
        private readonly Func<IReadOnlyList<MonitorInfo>> _activeMonitors;
        private readonly Action _refreshMonitors;
        private readonly Action _calibrationClosed;

        internal CalibrationLaunchCoordinator(
            SettingsManager settings,
            DispwinRunner dispwin,
            NightModeService nightMode,
            Func<IReadOnlyList<MonitorInfo>> activeMonitors,
            Action refreshMonitors,
            Action calibrationClosed)
        {
            _settings = settings;
            _dispwin = dispwin;
            _nightMode = nightMode;
            _activeMonitors = activeMonitors;
            _refreshMonitors = refreshMonitors;
            _calibrationClosed = calibrationClosed;
        }

        internal void Open(
            string? preferredMonitorDevicePath = null,
            CalibrationWindow.UiState? calibrationUiState = null,
            ColorimeterService? reusableColorimeterService = null)
        {
            foreach (Window window in Application.Current.Windows)
            {
                bool inUse = window is CalibrationSetupWindow or CalibrationWindow ||
                    (window is CalibrationReportWindow report && report.IsVerifyRunning);
                if (!inUse) continue;

                Log.Info($"CalibrationLaunchCoordinator: focusing active {window.GetType().Name} flow.");
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
                return;
            }

            var setupWindow = new CalibrationSetupWindow(
                _activeMonitors().ToList(),
                _settings,
                preferredMonitorDevicePath,
                reusableColorimeterService);
            bool accepted = setupWindow.ShowDialog() == true;
            var colorimeter = setupWindow.ColorimeterService;

            if (!accepted || setupWindow.SelectedTarget == null || colorimeter == null ||
                setupWindow.SelectedMonitor == null)
            {
                colorimeter?.Dispose();
                return;
            }

            var monitor = setupWindow.SelectedMonitor;
            var profile = _settings.GetMonitorProfile(monitor.MonitorDevicePath);
            var calibrationWindow = new CalibrationWindow(
                colorimeter,
                setupWindow.SelectedTarget,
                setupWindow.SelectedPreset,
                new CalibrationStateManager(_dispwin, _nightMode),
                monitor,
                profile?.GammaMode ?? monitor.CurrentGamma,
                profile?.ToCalibrationSettings(),
                _settings);
            WindowBoundsPersistence.CopyBounds(calibrationWindow, setupWindow);
            calibrationWindow.ApplyUiState(calibrationUiState);

            calibrationWindow.CalibrationCompleted += (_, e) =>
            {
                if (e.Success) _refreshMonitors();
            };

            bool transferBack = false;
            CalibrationWindow.UiState? transferredUiState = null;
            calibrationWindow.BackRequested += (_, _) =>
            {
                transferredUiState = calibrationWindow.CaptureUiState();
                transferBack = true;
            };
            calibrationWindow.Closed += (_, _) =>
            {
                if (transferBack)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        Open(monitor.MonitorDevicePath, transferredUiState, colorimeter)));
                }
                else
                {
                    colorimeter.Dispose();
                }
                _calibrationClosed();
            };
            calibrationWindow.Show();
        }
    }
}
