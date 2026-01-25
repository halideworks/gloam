using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HDRGammaController.Core.Calibration;
using static HDRGammaController.Core.Calibration.PatchSetGenerator;

namespace HDRGammaController
{
    /// <summary>
    /// Calibration window for displaying color patches and showing measurement progress.
    /// Supports both full-screen and windowed modes with comprehensive progress tracking.
    /// </summary>
    public partial class CalibrationWindow : Window
    {
        #region Win32 Imports

        // Screen saver blocking
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        [Flags]
        private enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        // Monitor information for full-screen mode
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        #endregion

        #region Private Fields

        private readonly ColorimeterService? _colorimeterService;
        private readonly CalibrationTarget? _calibrationTarget;
        private readonly CalibrationPreset _calibrationPreset;

        private bool _isFullScreenMode = true;
        private int _patchSize = 250;
        private bool _isPaused;
        private bool _isCancelled;
        private bool _isCalibrationRunning;
        private int _currentPatchIndex;
        private int _totalPatches;
        private Stopwatch _elapsedTimer = new();
        private IReadOnlyList<ColorPatch>? _patches;
        private CancellationTokenSource? _cancellationTokenSource;

        // Window state for mode switching
        private WindowStyle _previousWindowStyle;
        private ResizeMode _previousResizeMode;
        private double _previousLeft, _previousTop, _previousWidth, _previousHeight;
        private bool _wasMaximized;

        #endregion

        #region Events

        /// <summary>
        /// Raised when a patch measurement is needed.
        /// </summary>
        public event EventHandler<PatchMeasurementEventArgs>? PatchMeasurementRequested;

        /// <summary>
        /// Raised when calibration completes successfully.
        /// </summary>
        public event EventHandler<CalibrationCompleteEventArgs>? CalibrationCompleted;

        /// <summary>
        /// Raised when calibration is cancelled.
        /// </summary>
        public event EventHandler? CalibrationCancelled;

        #endregion

        #region Constructor

        public CalibrationWindow()
        {
            InitializeComponent();
            _calibrationPreset = CalibrationPreset.Standard;
        }

        public CalibrationWindow(ColorimeterService colorimeterService, CalibrationTarget target, CalibrationPreset preset)
            : this()
        {
            _colorimeterService = colorimeterService;
            _calibrationTarget = target;
            _calibrationPreset = preset;

            // Generate patches
            _patches = PatchSetGenerator.GeneratePatchSet(target, preset);
            _totalPatches = _patches.Count;

            // Update UI
            PatchCountText.Text = $"{_totalPatches} patches";
            EstimatedTimeText.Text = FormatEstimatedTime(_totalPatches);
        }

        #endregion

        #region Window Event Handlers

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Start blocking screen saver
            PreventScreenSaver(true);

            // Check colorimeter status
            UpdateColorimeterStatus();

            // Subscribe to colorimeter events if available
            if (_colorimeterService != null)
            {
                _colorimeterService.StatusChanged += ColorimeterService_StatusChanged;
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isCalibrationRunning && !_isCancelled)
            {
                var result = MessageBox.Show(
                    "Calibration is in progress. Are you sure you want to cancel?",
                    "Cancel Calibration",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true;
                    return;
                }

                _isCancelled = true;
                _cancellationTokenSource?.Cancel();
            }

            // Stop blocking screen saver
            PreventScreenSaver(false);

            // Unsubscribe from events
            if (_colorimeterService != null)
            {
                _colorimeterService.StatusChanged -= ColorimeterService_StatusChanged;
            }
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_isCalibrationRunning)
                {
                    if (_isPaused)
                    {
                        CancelMeasurement_Click(sender, e);
                    }
                    else
                    {
                        Pause_Click(sender, e);
                    }
                }
                else
                {
                    Cancel_Click(sender, e);
                }
            }
            else if (e.Key == Key.Space && _isPaused)
            {
                Resume_Click(sender, e);
            }
        }

        #endregion

        #region Screen Saver Control

        /// <summary>
        /// Prevents or allows the screen saver and display timeout.
        /// </summary>
        private static void PreventScreenSaver(bool prevent)
        {
            if (prevent)
            {
                // Prevent screen saver and display timeout
                SetThreadExecutionState(
                    EXECUTION_STATE.ES_CONTINUOUS |
                    EXECUTION_STATE.ES_DISPLAY_REQUIRED |
                    EXECUTION_STATE.ES_SYSTEM_REQUIRED);
            }
            else
            {
                // Allow normal screen saver behavior
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            }
        }

        #endregion

        #region Display Mode Handling

        private void DisplayMode_Changed(object sender, RoutedEventArgs e)
        {
            _isFullScreenMode = FullScreenModeRadio.IsChecked == true;
            WindowedWarning.Visibility = _isFullScreenMode ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyDisplayMode()
        {
            if (_isFullScreenMode)
            {
                // Store current window state
                _previousLeft = Left;
                _previousTop = Top;
                _previousWidth = Width;
                _previousHeight = Height;
                _previousWindowStyle = WindowStyle;
                _previousResizeMode = ResizeMode;
                _wasMaximized = WindowState == WindowState.Maximized;

                // Go full screen
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;

                // Get the monitor this window is on using Win32 API
                var helper = new WindowInteropHelper(this);
                IntPtr hMonitor = MonitorFromWindow(helper.Handle, MONITOR_DEFAULTTONEAREST);

                var monitorInfo = new MONITORINFO();
                monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));

                if (GetMonitorInfo(hMonitor, ref monitorInfo))
                {
                    Left = monitorInfo.rcMonitor.Left;
                    Top = monitorInfo.rcMonitor.Top;
                    Width = monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left;
                    Height = monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top;
                }
                else
                {
                    // Fallback to primary screen
                    Left = 0;
                    Top = 0;
                    Width = SystemParameters.PrimaryScreenWidth;
                    Height = SystemParameters.PrimaryScreenHeight;
                }

                WindowedModeBanner.Visibility = Visibility.Collapsed;
                Topmost = true;
            }
            else
            {
                // Windowed mode
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;

                // Set reasonable window size for windowed mode
                Width = 600;
                Height = 500;

                // Center on screen using SystemParameters
                Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
                Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;

                WindowedModeBanner.Visibility = Visibility.Visible;
                Topmost = AlwaysOnTopCheck.IsChecked == true;
            }

            // Update patch size
            UpdatePatchSize();
        }

        private void RestoreWindowMode()
        {
            if (_wasMaximized)
            {
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = _previousWindowStyle;
                ResizeMode = _previousResizeMode;
                Left = _previousLeft;
                Top = _previousTop;
                Width = _previousWidth;
                Height = _previousHeight;
            }
            Topmost = false;
        }

        private void UpdatePatchSize()
        {
            if (SmallPatchRadio.IsChecked == true)
                _patchSize = 150;
            else if (MediumPatchRadio.IsChecked == true)
                _patchSize = 250;
            else if (LargePatchRadio.IsChecked == true)
                _patchSize = 350;

            ColorPatchBorder.Width = _patchSize;
            ColorPatchBorder.Height = _patchSize;
        }

        #endregion

        #region Colorimeter Status

        private void UpdateColorimeterStatus()
        {
            if (_colorimeterService == null)
            {
                ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                ColorimeterStatusText.Text = "Colorimeter service not available";
                ColorimeterModelText.Text = "";
                StartButton.IsEnabled = false;
                return;
            }

            if (_colorimeterService.IsReady)
            {
                ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                ColorimeterStatusText.Text = "Connected and ready";
                ColorimeterModelText.Text = _colorimeterService.ConnectedColorimeter?.Model ?? "";
                StartButton.IsEnabled = true;
            }
            else
            {
                ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                ColorimeterStatusText.Text = "No colorimeter detected";
                ColorimeterModelText.Text = "Connect your i1 Display Plus and click Refresh";
                StartButton.IsEnabled = false;
            }
        }

        private void ColorimeterService_StatusChanged(object? sender, ColorimeterStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                switch (e.Status)
                {
                    case ColorimeterStatus.Ready:
                        ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        ColorimeterStatusText.Text = "Connected and ready";
                        StartButton.IsEnabled = true;
                        break;
                    case ColorimeterStatus.Searching:
                        ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 0));
                        ColorimeterStatusText.Text = "Searching...";
                        StartButton.IsEnabled = false;
                        break;
                    case ColorimeterStatus.NotConnected:
                    case ColorimeterStatus.NotFound:
                        ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                        ColorimeterStatusText.Text = e.Message;
                        StartButton.IsEnabled = false;
                        break;
                    case ColorimeterStatus.Error:
                        ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                        ColorimeterStatusText.Text = $"Error: {e.Message}";
                        StartButton.IsEnabled = false;
                        break;
                }
            });
        }

        private async void RefreshColorimeter_Click(object sender, RoutedEventArgs e)
        {
            if (_colorimeterService == null) return;

            ColorimeterStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 0));
            ColorimeterStatusText.Text = "Searching for colorimeter...";
            ColorimeterModelText.Text = "";
            StartButton.IsEnabled = false;

            await _colorimeterService.InitializeAsync();
            UpdateColorimeterStatus();
        }

        #endregion

        #region Calibration Control

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            // Apply display mode
            ApplyDisplayMode();

            // Show measurement panel
            SetupPanel.Visibility = Visibility.Collapsed;
            MeasurementPanel.Visibility = Visibility.Visible;

            // Start calibration
            _isCalibrationRunning = true;
            _isPaused = false;
            _isCancelled = false;
            _currentPatchIndex = 0;
            _cancellationTokenSource = new CancellationTokenSource();
            _elapsedTimer.Restart();

            // Update initial progress
            UpdateProgress();

            // Run calibration loop
            await RunCalibrationAsync(_cancellationTokenSource.Token);
        }

        private async Task RunCalibrationAsync(CancellationToken cancellationToken)
        {
            if (_patches == null) return;

            try
            {
                for (_currentPatchIndex = 0; _currentPatchIndex < _patches.Count; _currentPatchIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Wait while paused
                    while (_isPaused && !_isCancelled)
                    {
                        await Task.Delay(100, cancellationToken);
                    }

                    if (_isCancelled) break;

                    var patch = _patches[_currentPatchIndex];

                    // Update UI
                    Dispatcher.Invoke(() =>
                    {
                        UpdateProgress();
                        DisplayPatch(patch);
                    });

                    // Wait for display to settle
                    await Task.Delay(300, cancellationToken);

                    // Request measurement
                    var measurementArgs = new PatchMeasurementEventArgs(patch, _currentPatchIndex, _totalPatches);
                    PatchMeasurementRequested?.Invoke(this, measurementArgs);

                    // Wait for measurement to complete (this would be handled externally)
                    // For now, simulate a measurement delay
                    await Task.Delay(200, cancellationToken);
                }

                // Calibration complete
                if (!_isCancelled)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowCompletion(true, $"{_totalPatches} patches measured successfully in {FormatElapsedTime(_elapsedTimer.Elapsed)}");
                    });

                    if (SoundNotificationsCheck.IsChecked == true)
                    {
                        SystemSounds.Asterisk.Play();
                    }

                    CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(true, null));
                }
            }
            catch (OperationCanceledException)
            {
                if (!_isCancelled)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowCompletion(false, "Calibration was cancelled");
                    });
                }
                CalibrationCancelled?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ShowCompletion(false, $"Calibration failed: {ex.Message}");
                });

                if (SoundNotificationsCheck.IsChecked == true)
                {
                    SystemSounds.Hand.Play();
                }

                CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, ex.Message));
            }
            finally
            {
                _isCalibrationRunning = false;
                _elapsedTimer.Stop();
            }
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = true;
            PauseOverlay.Visibility = Visibility.Visible;
            PauseButton.Content = "⏸ Paused";
            PauseButton.IsEnabled = false;
        }

        private async void Resume_Click(object sender, RoutedEventArgs e)
        {
            // Countdown before resuming
            for (int i = 3; i > 0; i--)
            {
                ResumeCountdownText.Text = $"Resuming in {i}...";
                await Task.Delay(1000);
            }

            _isPaused = false;
            PauseOverlay.Visibility = Visibility.Collapsed;
            PauseButton.Content = "⏸ Pause";
            PauseButton.IsEnabled = true;
            ResumeCountdownText.Text = "Click Resume to continue";
        }

        private void CancelMeasurement_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to cancel the calibration?\nAll progress will be lost.",
                "Cancel Calibration",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _isCancelled = true;
                _cancellationTokenSource?.Cancel();
                Close();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ViewReport_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Open calibration report window
            Close();
        }

        #endregion

        #region UI Updates

        private void DisplayPatch(ColorPatch patch)
        {
            // Convert patch RGB to WPF color
            byte r = (byte)(Math.Clamp(patch.DisplayRgb.R, 0, 1) * 255);
            byte g = (byte)(Math.Clamp(patch.DisplayRgb.G, 0, 1) * 255);
            byte b = (byte)(Math.Clamp(patch.DisplayRgb.B, 0, 1) * 255);
            ColorPatchBorder.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void UpdateProgress()
        {
            double progress = _totalPatches > 0 ? (_currentPatchIndex * 100.0 / _totalPatches) : 0;
            CalibrationProgressBar.Value = progress;
            ProgressPercentText.Text = $"{progress:F0}%";

            PatchInfoText.Text = $"Patch {_currentPatchIndex + 1} of {_totalPatches}";

            var elapsed = _elapsedTimer.Elapsed;
            var estimatedTotal = _currentPatchIndex > 0
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds / _currentPatchIndex * _totalPatches)
                : TimeSpan.FromSeconds(_totalPatches * 3); // ~3 seconds per patch estimate
            var remaining = estimatedTotal - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            TimeInfoText.Text = $"Elapsed: {FormatElapsedTime(elapsed)} • Remaining: ~{FormatElapsedTime(remaining)}";

            if (_patches != null && _currentPatchIndex < _patches.Count)
            {
                var currentPatch = _patches[_currentPatchIndex];
                CurrentPatchText.Text = currentPatch.Name ?? GetPatchDescription(currentPatch);
                PhaseText.Text = currentPatch.Category.ToString();

                if (_currentPatchIndex + 1 < _patches.Count)
                {
                    var nextPatch = _patches[_currentPatchIndex + 1];
                    NextPatchText.Text = nextPatch.Name ?? GetPatchDescription(nextPatch);
                }
                else
                {
                    NextPatchText.Text = "(last patch)";
                }
            }
        }

        private void ShowCompletion(bool success, string message)
        {
            CompletionOverlay.Visibility = Visibility.Visible;
            PauseOverlay.Visibility = Visibility.Collapsed;

            if (success)
            {
                CompletionIcon.Text = "✓";
                CompletionIcon.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                CompletionTitle.Text = "Calibration Complete";
                ViewReportButton.Visibility = Visibility.Visible;
            }
            else
            {
                CompletionIcon.Text = "✕";
                CompletionIcon.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                CompletionTitle.Text = "Calibration Failed";
                ViewReportButton.Visibility = Visibility.Collapsed;
            }

            CompletionMessage.Text = message;
        }

        #endregion

        #region Helper Methods

        private static string FormatEstimatedTime(int patchCount)
        {
            // Estimate ~3 seconds per patch (500ms settle + 2-2.5s measurement)
            var totalSeconds = patchCount * 3;
            var time = TimeSpan.FromSeconds(totalSeconds);
            return time.TotalMinutes >= 60
                ? $"~{time.Hours}h {time.Minutes}m"
                : $"~{time.Minutes} minutes";
        }

        private static string FormatElapsedTime(TimeSpan time)
        {
            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
                : $"{time.Minutes}:{time.Seconds:D2}";
        }

        private static string GetPatchDescription(ColorPatch patch)
        {
            // Generate a simple description based on RGB values
            var rgb = patch.DisplayRgb;
            if (Math.Abs(rgb.R - rgb.G) < 0.01 && Math.Abs(rgb.G - rgb.B) < 0.01)
            {
                var level = (int)(rgb.R * 100);
                return level == 100 ? "White 100%" : level == 0 ? "Black 0%" : $"Gray {level}%";
            }

            if (rgb.R > rgb.G && rgb.R > rgb.B)
                return $"Red {(int)(rgb.R * 100)}%";
            if (rgb.G > rgb.R && rgb.G > rgb.B)
                return $"Green {(int)(rgb.G * 100)}%";
            if (rgb.B > rgb.R && rgb.B > rgb.G)
                return $"Blue {(int)(rgb.B * 100)}%";

            return $"RGB({(int)(rgb.R * 255)},{(int)(rgb.G * 255)},{(int)(rgb.B * 255)})";
        }

        #endregion

        #region Public Methods for External Control

        /// <summary>
        /// Sets the current patch color programmatically.
        /// </summary>
        public void SetPatchColor(double r, double g, double b)
        {
            Dispatcher.Invoke(() =>
            {
                byte br = (byte)(Math.Clamp(r, 0, 1) * 255);
                byte bg = (byte)(Math.Clamp(g, 0, 1) * 255);
                byte bb = (byte)(Math.Clamp(b, 0, 1) * 255);
                ColorPatchBorder.Background = new SolidColorBrush(Color.FromRgb(br, bg, bb));
            });
        }

        /// <summary>
        /// Updates progress from external orchestrator.
        /// </summary>
        public void UpdateExternalProgress(int currentPatch, int totalPatches, string phaseName,
            string currentPatchName, string nextPatchName, TimeSpan elapsed, TimeSpan remaining)
        {
            Dispatcher.Invoke(() =>
            {
                _currentPatchIndex = currentPatch;
                _totalPatches = totalPatches;

                double progress = totalPatches > 0 ? (currentPatch * 100.0 / totalPatches) : 0;
                CalibrationProgressBar.Value = progress;
                ProgressPercentText.Text = $"{progress:F0}%";
                PatchInfoText.Text = $"Patch {currentPatch + 1} of {totalPatches}";
                PhaseText.Text = phaseName;
                CurrentPatchText.Text = currentPatchName;
                NextPatchText.Text = nextPatchName;
                TimeInfoText.Text = $"Elapsed: {FormatElapsedTime(elapsed)} • Remaining: ~{FormatElapsedTime(remaining)}";
            });
        }

        #endregion
    }

    #region Event Args

    /// <summary>
    /// Event arguments for patch measurement requests.
    /// </summary>
    public class PatchMeasurementEventArgs : EventArgs
    {
        public ColorPatch Patch { get; }
        public int PatchIndex { get; }
        public int TotalPatches { get; }

        public PatchMeasurementEventArgs(ColorPatch patch, int patchIndex, int totalPatches)
        {
            Patch = patch;
            PatchIndex = patchIndex;
            TotalPatches = totalPatches;
        }
    }

    /// <summary>
    /// Event arguments for calibration completion.
    /// </summary>
    public class CalibrationCompleteEventArgs : EventArgs
    {
        public bool Success { get; }
        public string? ErrorMessage { get; }

        public CalibrationCompleteEventArgs(bool success, string? errorMessage)
        {
            Success = success;
            ErrorMessage = errorMessage;
        }
    }

    #endregion
}
