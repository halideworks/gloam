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
using HDRGammaController.Core;
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

        // State management for bypassing corrections during calibration
        private readonly CalibrationStateManager? _stateManager;
        private readonly SettingsManager? _settingsManager;
        private readonly MonitorInfo? _targetMonitor;
        private readonly GammaMode _previousGammaMode;
        private readonly CalibrationSettings? _previousSettings;
        private bool _bypassApplied;
        private bool _completedSuccessfully;

        private bool _isFullScreenMode = true;
        private int _patchSize = 600;
        private bool _isPaused;
        private bool _isCancelled;
        private bool _isCalibrationRunning;
        private int _currentPatchIndex;
        private int _totalPatches;
        private Stopwatch _elapsedTimer = new();
        private IReadOnlyList<ColorPatch>? _patches;
        private CancellationTokenSource? _cancellationTokenSource;

        // Calibration orchestrator for managing the measurement workflow
        private CalibrationOrchestrator? _orchestrator;
        private CalibrationResult? _calibrationResult;
        private Lut3D? _generatedLut;
        private DisplayCharacterization? _displayCharacterization;
        private CalibrationMetrics? _calibrationMetrics;
        private bool _driverInstallAttempted;

        // Window state for mode switching
        private ResizeMode _previousResizeMode;
        private double _previousLeft, _previousTop, _previousWidth, _previousHeight;
        private bool _wasMaximized;

        // Closed-loop refinement: number of apply→verify passes (1 = apply+verify once,
        // >1 refines). 0 disables it (plain measure-only). Surfaced as a setting later;
        // default to a few rounds so calibration produces a real before/after.
        private int _refinementRounds = 3;

        // Patch placement offset (shared by positioning + measurement patches)
        private double _patchOffsetX, _patchOffsetY;
        private bool _isDraggingPatch;
        private Point _dragStartMouse;
        private double _dragStartOffsetX, _dragStartOffsetY;

        #endregion

        #region Events

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

        /// <summary>
        /// Creates a calibration window with state management for bypassing corrections.
        /// </summary>
        public CalibrationWindow(
            ColorimeterService colorimeterService,
            CalibrationTarget target,
            CalibrationPreset preset,
            CalibrationStateManager stateManager,
            MonitorInfo targetMonitor,
            GammaMode previousGammaMode,
            CalibrationSettings? previousSettings,
            SettingsManager? settingsManager = null)
            : this(colorimeterService, target, preset)
        {
            _stateManager = stateManager;
            _targetMonitor = targetMonitor;
            _previousGammaMode = previousGammaMode;
            _previousSettings = previousSettings;
            _settingsManager = settingsManager;

            // Show bypass warning in the setup panel
            if (_previousGammaMode != GammaMode.WindowsDefault || previousSettings?.HasAdjustments == true)
            {
                BypassWarningPanel.Visibility = Visibility.Visible;
            }
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

            // If bypass was applied and we're closing without successful completion,
            // restore the previous correction state
            if (_bypassApplied && _stateManager != null && !_completedSuccessfully)
            {
                try
                {
                    _stateManager.RestorePreviousState();
                    Log.Info("CalibrationWindow: Restored previous correction state on close");
                }
                catch (Exception ex)
                {
                    Log.Info($"CalibrationWindow: Failed to restore state: {ex.Message}");
                }
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
            // Arrow keys nudge the patch while positioning or measuring, so the user can
            // fine-tune alignment to a fixed-hanging probe without the mouse.
            bool placingPatch = PositioningPanel.Visibility == Visibility.Visible
                                 || MeasurementPanel.Visibility == Visibility.Visible;
            if (placingPatch && TryNudgePatch(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
            {
                e.Handled = true;
                return;
            }

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
            // Guard against being called during XAML initialization before controls are created
            if (FullScreenModeRadio == null || WindowedWarning == null)
                return;

            _isFullScreenMode = FullScreenModeRadio.IsChecked == true;
            WindowedWarning.Visibility = _isFullScreenMode ? Visibility.Collapsed : Visibility.Visible;
        }

        // CalibrationMode_Changed removed - calibration mode is now selected in CalibrationSetupWindow

        private void ApplyDisplayMode()
        {
            // Note: Window has AllowsTransparency=True, so WindowStyle must remain None
            // We keep the chromeless look and just change size/position

            if (_isFullScreenMode)
            {
                // Store current window state
                _previousLeft = Left;
                _previousTop = Top;
                _previousWidth = Width;
                _previousHeight = Height;
                _previousResizeMode = ResizeMode;
                _wasMaximized = WindowState == WindowState.Maximized;

                // Go full screen - keep WindowStyle.None (required for AllowsTransparency)
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;

                // Position the fullscreen patch on the monitor the user chose to
                // calibrate — NOT wherever this window happens to sit. On a multi-monitor
                // setup these differ, and getting it wrong puts the patches on one screen
                // while the probe sits on another, silently measuring the wrong display.
                bool positioned = false;
                if (_targetMonitor != null)
                {
                    // Prefer the target's HMONITOR (most reliable across DPI/topologies).
                    IntPtr targetHmon = _targetMonitor.HMonitor;
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (targetHmon != IntPtr.Zero && GetMonitorInfo(targetHmon, ref mi))
                    {
                        Left = mi.rcMonitor.Left;
                        Top = mi.rcMonitor.Top;
                        Width = mi.rcMonitor.Right - mi.rcMonitor.Left;
                        Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                        positioned = true;
                    }
                    else
                    {
                        // Fall back to the DXGI desktop bounds captured at enumeration.
                        var b = _targetMonitor.MonitorBounds;
                        if (b.Right > b.Left && b.Bottom > b.Top)
                        {
                            Left = b.Left;
                            Top = b.Top;
                            Width = b.Right - b.Left;
                            Height = b.Bottom - b.Top;
                            positioned = true;
                        }
                    }
                }

                if (!positioned)
                {
                    // Last resort: the monitor this window is on, then the primary.
                    var helper = new WindowInteropHelper(this);
                    IntPtr hMonitor = MonitorFromWindow(helper.Handle, MONITOR_DEFAULTTONEAREST);
                    var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(hMonitor, ref monitorInfo))
                    {
                        Left = monitorInfo.rcMonitor.Left;
                        Top = monitorInfo.rcMonitor.Top;
                        Width = monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left;
                        Height = monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top;
                    }
                    else
                    {
                        Left = 0;
                        Top = 0;
                        Width = SystemParameters.PrimaryScreenWidth;
                        Height = SystemParameters.PrimaryScreenHeight;
                    }
                }

                WindowedModeBanner.Visibility = Visibility.Collapsed;
                Topmost = true;
            }
            else
            {
                // Windowed mode - keep chromeless style but allow resizing
                ResizeMode = ResizeMode.CanResizeWithGrip;

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
                // Don't restore WindowStyle - must stay None for AllowsTransparency
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
            // Default patch size is 600x600 - user can resize window to scale the patch
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

            // async void: surface failures instead of letting them escape to the
            // global dispatcher handler, which would swallow them with no feedback.
            try
            {
                await _colorimeterService.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationWindow.RefreshColorimeter: {ex.Message}");
                ColorimeterStatusText.Text = "Colorimeter search failed";
            }
            UpdateColorimeterStatus();
        }

        #endregion

        #region Calibration Control

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_colorimeterService == null || _calibrationTarget == null)
            {
                MessageBox.Show("Colorimeter or calibration target not configured.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Apply display mode (full-screen or windowed)
            ApplyDisplayMode();

            // Update positioning patch size based on selected patch size
            UpdatePositioningPatchSize();

            // Show windowed mode banner if applicable
            if (!_isFullScreenMode)
            {
                PositioningWindowedBanner.Visibility = Visibility.Visible;
            }

            // Show positioning panel for user to place colorimeter
            SetupPanel.Visibility = Visibility.Collapsed;
            PositioningPanel.Visibility = Visibility.Visible;
        }

        private void UpdatePositioningPatchSize()
        {
            // Set positioning patch size - user can resize window to scale it
            PositioningPatchBorder.Width = _patchSize;
            PositioningPatchBorder.Height = _patchSize;
        }

        private void PositioningBack_Click(object sender, RoutedEventArgs e)
        {
            // Go back to setup panel
            PositioningPanel.Visibility = Visibility.Collapsed;
            PositioningWindowedBanner.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;

            // Restore to original setup window size (as defined in XAML: 700x700)
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Width = 700;
            Height = 700;
            Topmost = false;

            // Center on screen
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
        }

        private async void BeginMeasurement_Click(object sender, RoutedEventArgs e)
        {
            // Enter bypass mode - disable all color corrections for accurate measurement
            if (_stateManager != null && _targetMonitor != null && !_bypassApplied)
            {
                try
                {
                    _stateManager.EnterBypassMode(_targetMonitor, _previousGammaMode, _previousSettings);
                    _bypassApplied = true;
                    Log.Info("CalibrationWindow: Entered bypass mode - all corrections disabled");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to disable color corrections: {ex.Message}\n\nCalibration may be inaccurate.",
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // Hide positioning, show measurement panel
            PositioningPanel.Visibility = Visibility.Collapsed;
            PositioningWindowedBanner.Visibility = Visibility.Collapsed;
            MeasurementPanel.Visibility = Visibility.Visible;

            // Carry the patch placement chosen during positioning into the measurement patch.
            ApplyPatchOffset();

            // Create the calibration orchestrator
            // Measure in the display's actual HDR/SDR state — measuring HDR signal as if SDR
            // (the old hardcoded false) feeds the corrector nits-scaled data against an SDR
            // target, which made it diverge and produced an SDR profile for an HDR panel.
            bool hdrMode = _targetMonitor?.IsHdrActive ?? false;
            _orchestrator = new CalibrationOrchestrator(
                _colorimeterService,
                _calibrationTarget,
                _calibrationPreset,
                settleTimeMs: 300,
                maxRetries: 3,
                hdrMode: hdrMode);

            // Wire up orchestrator events
            _orchestrator.DisplayPatchRequested += Orchestrator_DisplayPatchRequested;
            _orchestrator.ProgressChanged += Orchestrator_ProgressChanged;
            _orchestrator.StateChanged += Orchestrator_StateChanged;
            _orchestrator.MeasurementTaken += Orchestrator_MeasurementTaken;
            _orchestrator.ErrorOccurred += Orchestrator_ErrorOccurred;
            _orchestrator.CalibrationCompleted += Orchestrator_CalibrationCompleted;
            _orchestrator.PhaseChanged += (_, label) => Dispatcher.Invoke(() =>
            {
                // Verification/refinement passes report "Phase: i/N". Restart the progress bar
                // at 0 for each pass instead of leaving it pinned at ~99% from the measure pass.
                PhaseText.Text = label;
                int slash = label.LastIndexOf('/');
                int colon = label.LastIndexOf(':');
                if (slash > colon && colon >= 0
                    && int.TryParse(label.AsSpan(colon + 1, slash - colon - 1).Trim(), out int cur)
                    && int.TryParse(label.AsSpan(slash + 1).Trim(), out int total) && total > 0)
                {
                    double pct = cur * 100.0 / total;
                    CalibrationProgressBar.Value = pct;
                    ProgressPercentText.Text = $"{pct:F0}%";
                    PatchInfoText.Text = label;
                }
            });

            // Enable the in-session apply → verify → refine closed loop when we have a target
            // monitor + bypass manager. It loads each candidate correction onto the display's
            // gamma ramp and re-measures, giving a real before/after instead of grading the
            // uncorrected panel. Keep-best (in the orchestrator) makes refinement safe.
            if (_stateManager != null && _targetMonitor != null && _refinementRounds > 0)
            {
                var corrector = new ClosedLoopCorrector(
                    _calibrationTarget, _targetMonitor.SdrWhiteLevel, _targetMonitor.IsHdrActive);
                _orchestrator.ClosedLoop = new ClosedLoopConfig
                {
                    Corrector = corrector,
                    Apply = c => _stateManager.ApplyCorrectionLut(_targetMonitor, c.R, c.G, c.B),
                    MaxRefinementRounds = _refinementRounds,
                    TargetDeltaE = 1.0
                };
            }

            // Start calibration
            _isCalibrationRunning = true;
            _isPaused = false;
            _isCancelled = false;
            _currentPatchIndex = 0;
            _totalPatches = _orchestrator.TotalPatches;
            _cancellationTokenSource = new CancellationTokenSource();
            _elapsedTimer.Restart();

            // Update initial progress
            UpdateProgress();

            // Run calibration via orchestrator
            await RunCalibrationAsync(_cancellationTokenSource.Token);
        }

        private async Task RunCalibrationAsync(CancellationToken cancellationToken)
        {
            if (_orchestrator == null) return;

            try
            {
                // Run the calibration through the orchestrator
                _calibrationResult = await _orchestrator.StartCalibrationAsync(cancellationToken);

                // Process results if successful
                if (_calibrationResult.Success && _calibrationResult.Measurements != null)
                {
                    // Generate the 3D LUT from measurements
                    Dispatcher.Invoke(() =>
                    {
                        PhaseText.Text = "Generating LUT...";
                    });

                    // 33³ is the standard "high quality" 3D-LUT grid (the size Resolve/most
                    // .cube workflows default to). The grid is sampled from the fitted
                    // characterization model, not the raw patches, so a denser grid just
                    // interpolates the correction more smoothly — it costs <1s to build and
                    // doesn't require more measurements. 17³ was coarse enough to band gradients.
                    var generator = new Lut3DGenerator(
                        _calibrationTarget!,
                        _calibrationResult.Measurements,
                        lutSize: 33);

                    _generatedLut = generator.Generate(progress =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            // Update progress for LUT generation phase (scaled from calibration 100% to 110%)
                            CalibrationProgressBar.Value = 100;
                            ProgressPercentText.Text = "Generating...";
                        });
                    });

                    _displayCharacterization = generator.Characterization;
                    _calibrationMetrics = generator.CalculateMetrics();

                    Dispatcher.Invoke(() =>
                    {
                        string gradeStr = _calibrationMetrics?.GetGrade().ToString() ?? "?";
                        string detail =
                            $"{_calibrationResult.Measurements.Count} patches measured in {FormatElapsedTime(_calibrationResult.TotalTime)}\n" +
                            $"Average Delta E: {_calibrationMetrics?.AverageDeltaE:F2} (Grade: {gradeStr})";

                        // When the closed loop ran, the headline is the real before/after of the
                        // grayscale: how far off the panel was vs. after the correction we applied.
                        if (_calibrationResult.ClosedLoopRan &&
                            _calibrationResult.NativeResidualDeltaE is double before &&
                            _calibrationResult.CorrectedResidualDeltaE is double after)
                        {
                            detail =
                                $"Grayscale dE {before:F2} → {after:F2} after {_calibrationResult.RefinementRounds} pass(es)\n" +
                                detail;
                        }

                        ShowCompletion(true, detail);
                    });

                    if (SoundNotificationsCheck.IsChecked == true)
                    {
                        SystemSounds.Asterisk.Play();
                    }

                    CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(true, null));
                }
                else if (_calibrationResult.WasCancelled)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowCompletion(false, "Calibration was cancelled");
                    });
                    CalibrationCancelled?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowCompletion(false, $"Calibration failed: {_calibrationResult.Message}");
                    });

                    if (SoundNotificationsCheck.IsChecked == true)
                    {
                        SystemSounds.Hand.Play();
                    }

                    CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, _calibrationResult.Message));
                }
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    ShowCompletion(false, "Calibration was cancelled");
                });
                CalibrationCancelled?.Invoke(this, EventArgs.Empty);
            }
            catch (UsbDriverException ex)
            {
                // USB driver error - offer to install drivers automatically (once)
                Dispatcher.Invoke(() =>
                {
                    if (_driverInstallAttempted)
                    {
                        ShowCompletion(false,
                            "Colorimeter communication failed even after driver installation.\n\n" +
                            "Unplug/replug the colorimeter, close other calibration software, and retry.\n\n" +
                            $"Details: {ex.Message}");
                        CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, ex.Message));
                    }
                    else
                    {
                        _driverInstallAttempted = true;
                        ShowDriverInstallDialog();
                    }
                });
            }
            catch (Exception ex)
            {
                // Check if this is a wrapped driver exception
                if (ex.InnerException is UsbDriverException || UsbDriverHelper.IsDriverError(ex.Message))
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_driverInstallAttempted)
                        {
                            ShowCompletion(false,
                                "Colorimeter communication failed even after driver installation.\n\n" +
                                "Unplug/replug the colorimeter, close other calibration software, and retry.\n\n" +
                                $"Details: {ex.Message}");
                            CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, ex.Message));
                        }
                        else
                        {
                            _driverInstallAttempted = true;
                            ShowDriverInstallDialog();
                        }
                    });
                    return;
                }

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
                UnwireOrchestratorEvents();
            }
        }

        #region Orchestrator Event Handlers

        private void Orchestrator_DisplayPatchRequested(object? sender, DisplayPatchEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                DisplayPatch(e.Patch);
            });
        }

        private void Orchestrator_ProgressChanged(object? sender, CalibrationProgressEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _currentPatchIndex = e.CurrentIndex;
                _totalPatches = e.TotalPatches;

                CalibrationProgressBar.Value = e.ProgressPercent;
                ProgressPercentText.Text = $"{e.ProgressPercent:F0}%";
                PatchInfoText.Text = $"Patch {e.CurrentIndex + 1} of {e.TotalPatches}";

                if (e.CurrentPatch != null)
                {
                    CurrentPatchText.Text = e.CurrentPatch.Name ?? GetPatchDescription(e.CurrentPatch);
                    PhaseText.Text = e.CurrentPatch.Category.ToString();
                }

                if (e.NextPatch != null)
                {
                    NextPatchText.Text = e.NextPatch.Name ?? GetPatchDescription(e.NextPatch);
                }
                else
                {
                    NextPatchText.Text = "(last patch)";
                }

                TimeInfoText.Text = $"Elapsed: {FormatElapsedTime(e.Elapsed)} • Remaining: ~{FormatElapsedTime(e.EstimatedRemaining)}";
            });
        }

        private void Orchestrator_StateChanged(object? sender, CalibrationStateEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isPaused = e.NewState == CalibrationState.Paused;

                switch (e.NewState)
                {
                    case CalibrationState.Paused:
                        PauseOverlay.Visibility = Visibility.Visible;
                        PauseButton.Content = "⏸ Paused";
                        PauseButton.IsEnabled = false;
                        break;
                    case CalibrationState.Running:
                        PauseOverlay.Visibility = Visibility.Collapsed;
                        PauseButton.Content = "⏸ Pause";
                        PauseButton.IsEnabled = true;
                        break;
                }
            });
        }

        private void Orchestrator_MeasurementTaken(object? sender, MeasurementEventArgs e)
        {
            // Could update UI with measurement details if needed
            Dispatcher.Invoke(() =>
            {
                // Optional: Show measurement info
            });
        }

        private void Orchestrator_ErrorOccurred(object? sender, CalibrationErrorEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Show error in status but don't stop (unless fatal)
                if (e.IsFatal)
                {
                    ShowCompletion(false, e.Message);
                }
                else
                {
                    // Show retry message temporarily
                    TimeInfoText.Text = e.Message;
                }
            });
        }

        private void Orchestrator_CalibrationCompleted(object? sender, CalibrationResultEventArgs e)
        {
            // Main completion handling is in RunCalibrationAsync
        }

        private void UnwireOrchestratorEvents()
        {
            if (_orchestrator != null)
            {
                _orchestrator.DisplayPatchRequested -= Orchestrator_DisplayPatchRequested;
                _orchestrator.ProgressChanged -= Orchestrator_ProgressChanged;
                _orchestrator.StateChanged -= Orchestrator_StateChanged;
                _orchestrator.MeasurementTaken -= Orchestrator_MeasurementTaken;
                _orchestrator.ErrorOccurred -= Orchestrator_ErrorOccurred;
                _orchestrator.CalibrationCompleted -= Orchestrator_CalibrationCompleted;
            }
        }

        #endregion

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = true;
            _orchestrator?.Pause();
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
            _orchestrator?.Resume();
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
                _orchestrator?.Cancel();
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

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void PositioningPatch_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        // Patch placement: the user moves the patch (not the window) to wherever the
        // probe hangs. The offset is shared between the positioning and measurement
        // patches via two TranslateTransforms, so placement carries straight through.
        private void ApplyPatchOffset()
        {
            if (PositioningPatchTransform != null)
            {
                PositioningPatchTransform.X = _patchOffsetX;
                PositioningPatchTransform.Y = _patchOffsetY;
            }
            if (MeasurementPatchTransform != null)
            {
                MeasurementPatchTransform.X = _patchOffsetX;
                MeasurementPatchTransform.Y = _patchOffsetY;
            }
        }

        private void PatchDrag_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            _isDraggingPatch = true;
            _dragStartMouse = e.GetPosition(this);
            _dragStartOffsetX = _patchOffsetX;
            _dragStartOffsetY = _patchOffsetY;
            ((UIElement)sender).CaptureMouse();
        }

        private void PatchDrag_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingPatch) return;
            var p = e.GetPosition(this);
            _patchOffsetX = _dragStartOffsetX + (p.X - _dragStartMouse.X);
            _patchOffsetY = _dragStartOffsetY + (p.Y - _dragStartMouse.Y);
            ApplyPatchOffset();
        }

        private void PatchDrag_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingPatch) return;
            _isDraggingPatch = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        /// <summary>Nudges the patch with the arrow keys (Shift = larger step).</summary>
        private bool TryNudgePatch(Key key, bool shift)
        {
            double step = shift ? 25 : 5;
            switch (key)
            {
                case Key.Left:  _patchOffsetX -= step; break;
                case Key.Right: _patchOffsetX += step; break;
                case Key.Up:    _patchOffsetY -= step; break;
                case Key.Down:  _patchOffsetY += step; break;
                default: return false;
            }
            ApplyPatchOffset();
            return true;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void ViewReport_Click(object sender, RoutedEventArgs e)
        {
            if (_calibrationResult?.Success != true || _calibrationTarget == null)
            {
                MessageBox.Show("No calibration data available.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Create a calibration profile from the results
            var profile = new CalibrationProfile
            {
                MonitorDevicePath = _targetMonitor?.MonitorDevicePath ?? "calibration_session",
                MonitorName = !string.IsNullOrWhiteSpace(_targetMonitor?.FriendlyName)
                    ? _targetMonitor!.FriendlyName : "Calibrated Display",
                Target = _calibrationTarget,
                LutSize = _generatedLut?.Size ?? 33,
                PatchCount = _calibrationResult.Measurements?.Count ?? 0,
                ColorimeterModel = _colorimeterService?.ConnectedColorimeter?.Model,
                LastCalibratedAt = DateTime.UtcNow,
                PostCalibrationDeltaE = _calibrationMetrics?.AverageDeltaE,
                QualityGrade = _calibrationMetrics?.GetGrade(),
                CorrectionLut = _generatedLut
            };

            // Populate measured characteristics if available (MeasuredCct/MeasuredDuv are computed from MeasuredWhite)
            if (_displayCharacterization != null)
            {
                profile.MeasuredCharacteristics = new DisplayCharacteristics
                {
                    MeasuredRed = _displayCharacterization.RedPrimary,
                    MeasuredGreen = _displayCharacterization.GreenPrimary,
                    MeasuredBlue = _displayCharacterization.BluePrimary,
                    MeasuredWhite = _displayCharacterization.WhitePoint,
                    PeakLuminance = _displayCharacterization.PeakLuminance,
                    BlackLevel = _displayCharacterization.BlackLevel,
                    MeasuredGamma = _displayCharacterization.MeasuredGamma
                };
            }

            // Open the report window
            var reportWindow = new CalibrationReportWindow(profile, _calibrationMetrics, _displayCharacterization, _generatedLut);

            // Give the report what it needs to install the calibration natively. Prefer the
            // closed-loop's final correction (verified on-screen); otherwise derive per-channel
            // correction LUTs from the characterization.
            if (_targetMonitor != null && _calibrationTarget != null && _displayCharacterization != null)
            {
                double white = _targetMonitor.SdrWhiteLevel;
                (double[] r, double[] g, double[] b) corr;
                if (_calibrationResult?.FinalCorrection is { } fc)
                {
                    corr = (fc.R, fc.G, fc.B);
                }
                else
                {
                    double targetGamma = _calibrationTarget.Gamma ?? 2.2;
                    var (lr, lg, lb, _) = LutGenerator.GenerateCalibratedLut(
                        targetGamma, _displayCharacterization, CalibrationSettings.Default,
                        white, _targetMonitor.IsHdrActive);
                    corr = (lr, lg, lb);
                }

                var monitor = _targetMonitor;
                reportWindow.SetApplyContext(new CalibrationReportWindow.ApplyContext(
                    monitor, _calibrationTarget, corr.r, corr.g, corr.b, white,
                    OnInstalled: profileName =>
                    {
                        // Persist the active calibration so the live apply path composes night
                        // mode on top of it instead of double-applying the gamma curve.
                        _settingsManager?.SetMhc2Calibration(monitor.MonitorDevicePath, profileName);
                        Log.Info($"CalibrationWindow: Installed + recorded calibration profile {profileName}");
                    }));
            }

            reportWindow.Show();

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
                _completedSuccessfully = true;
                CompletionIcon.Text = "✓";
                CompletionIcon.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                CompletionTitle.Text = "Calibration Complete";
                ViewReportButton.Visibility = Visibility.Visible;

                // Show display mode options if bypass was applied
                if (_bypassApplied && _stateManager != null)
                {
                    DisplayModeOptionsPanel.Visibility = Visibility.Visible;
                    // Default to calibration only view
                    ViewCalibrationOnlyRadio.IsChecked = true;
                }
            }
            else
            {
                CompletionIcon.Text = "✕";
                CompletionIcon.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                CompletionTitle.Text = "Calibration Failed";
                ViewReportButton.Visibility = Visibility.Collapsed;
                DisplayModeOptionsPanel.Visibility = Visibility.Collapsed;

                // Restore previous settings on failure
                if (_bypassApplied && _stateManager != null)
                {
                    try
                    {
                        _stateManager.RestorePreviousState();
                        Log.Info("CalibrationWindow: Restored previous state after failure");
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"CalibrationWindow: Failed to restore state: {ex.Message}");
                    }
                }
            }

            CompletionMessage.Text = message;
        }

        private void DisplayModeOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_stateManager == null || _targetMonitor == null || !_bypassApplied)
                return;

            try
            {
                if (ViewCalibrationOnlyRadio.IsChecked == true)
                {
                    // Apply calibration LUT only (no additional corrections)
                    _stateManager.ApplyCalibrationOnly(_targetMonitor, _generatedLut);
                    Log.Info("CalibrationWindow: Switched to calibration-only view");
                }
                else if (ViewWithPreviousSettingsRadio.IsChecked == true)
                {
                    // Apply calibration with previous gamma/night mode settings
                    _stateManager.ApplyCalibrationWithPreviousSettings(_targetMonitor, _generatedLut);
                    Log.Info("CalibrationWindow: Switched to calibration + previous settings view");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationWindow: Failed to change display mode: {ex.Message}");
            }
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

        /// <summary>
        /// Shows the driver installation dialog and offers to retry calibration.
        /// </summary>
        private async void ShowDriverInstallDialog()
        {
            // First restore the previous state since calibration failed
            try
            {
                if (_stateManager != null && _bypassApplied)
                {
                    _stateManager.RestorePreviousState();
                    _bypassApplied = false;
                    Log.Info("CalibrationWindow: Restored previous state before driver dialog");
                }
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationWindow: Failed to restore state: {ex.Message}");
            }

            // Show the driver installation dialog
            var dialog = new DriverInstallDialog
            {
                Owner = this
            };

            var result = dialog.ShowDialog();

            if (dialog.ShouldRetry && dialog.DriverInstalled)
            {
                // User wants to retry - restart calibration
                Log.Info("CalibrationWindow: User requested retry after driver installation");

                if (_colorimeterService != null)
                {
                    try
                    {
                        // Re-initialize to pick up any driver changes
                        await _colorimeterService.InitializeAsync();
                        UpdateColorimeterStatus();
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"CalibrationWindow: Colorimeter re-init failed after driver install: {ex.Message}");
                    }
                }

                // Reset UI state
                MeasurementPanel.Visibility = Visibility.Visible;
                CompletionOverlay.Visibility = Visibility.Collapsed;
                _isCancelled = false;
                _isPaused = false;

                // Restart calibration by going back to positioning
                PositioningPanel.Visibility = Visibility.Visible;
                MeasurementPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                // User doesn't want to retry - show failure message
                ShowCompletion(false, "USB driver installation required.\n\nInstall the ArgyllCMS USB drivers and try again.");

                if (SoundNotificationsCheck.IsChecked == true)
                {
                    SystemSounds.Hand.Play();
                }

                CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, "USB driver error"));
            }
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
