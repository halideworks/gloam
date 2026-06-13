using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.ViewModels;
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
        private bool _measuredInHdr;

        private int _patchSize = 600;
        // FP16 scRGB renderer for HDR wire-ladder patches (ColorPatch.Nits). Created lazily
        // over the WPF patch area when the first wire patch displays; disposed when a normal
        // patch follows or the run ends.
        private HdrPatchRenderer? _hdrWireRenderer;
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

        // Animated "Shutting down calibration..." wait dialog while a cancel drains
        private BusyDialog? _shutdownDialog;

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

        /// <summary>
        /// Raised when the user wants to go back to the setup window to adjust choices.
        /// The window closes itself after raising this.
        /// </summary>
        public event EventHandler? BackRequested;

        #endregion

        #region Constructor

        public CalibrationWindow()
        {
            InitializeComponent();
            DataContext = Vm;
            // Keep the patch opaque if the user (or a stray hover) triggers Aero Peek mid-run,
            // so the probe never reads the desktop instead of the patch.
            Services.WindowTheme.ExcludeFromPeek(this);
            _calibrationPreset = CalibrationPreset.Standard;
        }

        /// <summary>All display state the XAML binds to.</summary>
        public CalibrationViewModel Vm { get; } = new CalibrationViewModel();

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
            Vm.PatchCountText = $"{_totalPatches} patches";
            Vm.EstimatedTimeText = FormatEstimatedTime(_totalPatches);
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
                Vm.ShowBypassWarning = true;
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
                if (!ConfirmDialog.Confirm(this, "Cancel Calibration",
                        "Calibration is in progress. Are you sure you want to cancel?"))
                {
                    e.Cancel = true;
                    return;
                }

                _isCancelled = true;
                _cancellationTokenSource?.Cancel();
            }

            DisposeHdrWireRenderer();

            // Always restore the GPU gamma ramp on close when we entered bypass — including
            // after a SUCCESSFUL calibration. The closed loop leaves its (possibly aggressive)
            // candidate correction on the ramp and recorded in the apply-state, so the ramp
            // guard would keep re-asserting it; this re-applies the user's real pre-calibration
            // gamma and resumes night mode. If the user applied an MHC2 profile, the tray then
            // re-asserts the correct MHC2-composed state on the window's Closed event.
            if (_bypassApplied && _stateManager != null)
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
            bool placingPatch = Vm.IsPositioningVisible || Vm.IsMeasurementVisible;
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

        private void ApplyDisplayMode()
        {
            // Note: Window has AllowsTransparency=True, so WindowStyle must remain None
            // We keep the chromeless look and just change size/position

            if (Vm.IsFullScreenMode)
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

                Vm.ShowWindowedModeBanner = false;
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

                Vm.ShowWindowedModeBanner = true;
                Topmost = Vm.AlwaysOnTop;
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
                Vm.SetColorimeterStatus(CalibrationViewModel.ErrorBrush, "Colorimeter service not available", "", canStart: false);
                return;
            }

            if (_colorimeterService.IsReady)
            {
                Vm.SetColorimeterStatus(CalibrationViewModel.SuccessBrush, "Connected and ready",
                    _colorimeterService.ConnectedColorimeter?.Model ?? "", canStart: true);
            }
            else
            {
                Vm.SetColorimeterStatus(CalibrationViewModel.WarningBrush, "No colorimeter detected",
                    "Connect your i1 Display Plus and click Refresh", canStart: false);
            }
        }

        private void ColorimeterService_StatusChanged(object? sender, ColorimeterStatusEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                switch (e.Status)
                {
                    case ColorimeterStatus.Ready:
                        Vm.ColorimeterBrush = CalibrationViewModel.SuccessBrush;
                        Vm.ColorimeterStatusText = "Connected and ready";
                        Vm.CanStart = true;
                        break;
                    case ColorimeterStatus.Searching:
                        Vm.ColorimeterBrush = CalibrationViewModel.WarningBrush;
                        Vm.ColorimeterStatusText = "Searching...";
                        Vm.CanStart = false;
                        break;
                    case ColorimeterStatus.NotConnected:
                    case ColorimeterStatus.NotFound:
                        Vm.ColorimeterBrush = CalibrationViewModel.ErrorBrush;
                        Vm.ColorimeterStatusText = e.Message;
                        Vm.CanStart = false;
                        break;
                    case ColorimeterStatus.Error:
                        Vm.ColorimeterBrush = CalibrationViewModel.ErrorBrush;
                        Vm.ColorimeterStatusText = $"Error: {e.Message}";
                        Vm.CanStart = false;
                        break;
                }
            });
        }

        private async void RefreshColorimeter_Click(object sender, RoutedEventArgs e)
        {
            if (_colorimeterService == null) return;

            Vm.SetColorimeterStatus(CalibrationViewModel.WarningBrush, "Searching for colorimeter...", "", canStart: false);

            // async void: surface failures instead of letting them escape to the
            // global dispatcher handler, which would swallow them with no feedback.
            try
            {
                await _colorimeterService.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationWindow.RefreshColorimeter: {ex.Message}");
                Vm.ColorimeterStatusText = "Colorimeter search failed";
            }
            UpdateColorimeterStatus();
        }

        #endregion

        #region Calibration Control

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_colorimeterService == null || _calibrationTarget == null)
            {
                ConfirmDialog.Info(this, "Error", "Colorimeter or calibration target not configured.");
                return;
            }

            // Apply display mode (full-screen or windowed)
            ApplyDisplayMode();

            // Update positioning patch size based on selected patch size
            UpdatePositioningPatchSize();

            // Show windowed mode banner if applicable
            if (Vm.IsWindowedMode)
            {
                Vm.ShowPositioningWindowedBanner = true;
            }

            // Show positioning panel for user to place colorimeter
            Vm.IsSetupVisible = false;
            Vm.IsPositioningVisible = true;
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
            Vm.IsPositioningVisible = false;
            Vm.ShowPositioningWindowedBanner = false;
            Vm.IsSetupVisible = true;

            // Restore to original setup window size (as defined in XAML: 600x700)
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Width = 600;
            Height = 700;
            Topmost = false;

            // Center on screen
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
        }

        private async void BeginMeasurement_Click(object sender, RoutedEventArgs e)
        {
            // If THIS app already installed a calibration profile on the target monitor, Windows
            // is applying it at the compositor and we'd be measuring the display THROUGH our own
            // correction — a useless reading. DISABLE it (disassociate only) so we characterize
            // the native panel; the .icm stays in the color store so the previous calibration
            // can be restored from Color Management if this run is abandoned.
            if (_targetMonitor != null)
            {
                if (_settingsManager?.GetMonitorProfile(_targetMonitor.MonitorDevicePath)?.Mhc2ProfileName is { } activeProfile)
                {
                    Log.Info($"CalibrationWindow: Disabling active calibration profile '{activeProfile}' before measuring native (kept in color store).");
                    CalibrationProfileInstaller.Disable(_targetMonitor, activeProfile);
                    _settingsManager?.SetMhc2Calibration(_targetMonitor.MonitorDevicePath, null);
                }

                // Belt and braces: also retire any STALE app-generated associations (a past
                // bug installed several in one session). If one became the fallback default,
                // "native" would silently measure through it.
                CalibrationProfileInstaller.DisableAllForMonitor(_targetMonitor);

                NativeGammaRamp.TryClear(_targetMonitor.DeviceName);
                await Task.Delay(300); // let the compositor drop the profile
            }

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
                    ConfirmDialog.Info(this, "Warning", $"Failed to disable color corrections: {ex.Message}\n\nCalibration may be inaccurate.");
                }
            }

            // Hide positioning, show measurement panel
            Vm.IsPositioningVisible = false;
            Vm.ShowPositioningWindowedBanner = false;
            Vm.IsMeasurementVisible = true;
            UpdateMuteButton();

            // Carry the patch placement chosen during positioning into the measurement patch.
            ApplyPatchOffset();

            // Create the calibration orchestrator
            // Measure in the display's actual HDR/SDR state — measuring HDR signal as if SDR
            // (the old hardcoded false) feeds the corrector nits-scaled data against an SDR
            // target, which made it diverge and produced an SDR profile for an HDR panel.
            bool hdrMode = _targetMonitor?.IsHdrActive ?? false;
            _measuredInHdr = hdrMode;
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
                Vm.PhaseText = label;
                int slash = label.LastIndexOf('/');
                int colon = label.LastIndexOf(':');
                if (slash > colon && colon >= 0
                    && int.TryParse(label.AsSpan(colon + 1, slash - colon - 1).Trim(), out int cur)
                    && int.TryParse(label.AsSpan(slash + 1).Trim(), out int total) && total > 0)
                {
                    Vm.ProgressPercent = cur * 100.0 / total;
                    Vm.PatchInfoText = label;
                }
            });

            // Enable the in-session apply → verify → refine closed loop when we have a target
            // monitor + bypass manager. It loads each candidate correction onto the display's
            // gamma ramp and re-measures, giving a real before/after instead of grading the
            // uncorrected panel. Keep-best (in the orchestrator) makes refinement safe.
            // HDR: skipped — the corrector's GPU-ramp curves are signal-domain, but in HDR the
            // ramp sits on the PQ wire (wrong axis); the MHC2 PQ LUTs do the tone correction.
            // The report's Verify button provides the measured after instead.
            if (hdrMode)
            {
                Log.Info("CalibrationWindow: closed-loop refinement skipped in HDR (use report Verify for the measured after).");
            }
            else if (_stateManager != null && _targetMonitor != null && _refinementRounds > 0)
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

            // HDR wire ladder: FP16 patches at exact PQ wire positions, far above SDR white.
            // HdrMhc2LutBuilder prefers these for the PQ tone LUTs (no SDR-mapping
            // assumption - probe-validated on the MAG 271QPX June 2026).
            if (hdrMode && _targetMonitor != null)
            {
                _orchestrator.AdditionalPatches = HdrWirePatchSet.Build(_targetMonitor.HdrPeakNits);
                Log.Info($"CalibrationWindow: appended {_orchestrator.AdditionalPatches.Count} HDR wire-ladder patches " +
                         $"(panel peak {_targetMonitor.HdrPeakNits:F0} nits).");
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
            try
            {
                await RunCalibrationAsync(_cancellationTokenSource.Token);
            }
            finally
            {
                DisposeHdrWireRenderer();
            }
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
                        Vm.PhaseText = "Generating LUT...";
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
                            Vm.ProgressPercent = 100;
                            Vm.SetProgressLabel("Generating...");
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

                        // Hands-free flow: open the report immediately — it auto-applies the
                        // profile and runs the verification sweep itself (the probe is still
                        // on the display), and plays the completion sound when verification
                        // finishes — not here, mid-flow.
                        // _isCalibrationRunning must be cleared FIRST: ViewReport_Click closes
                        // this window, and Window_Closing otherwise shows the "calibration in
                        // progress — cancel?" prompt, blocking the close and stranding the
                        // user on the completion screen in a report→prompt loop.
                        _isCalibrationRunning = false;
                        ViewReport_Click(this, new RoutedEventArgs());
                    });

                    CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(true, null));
                }
                else if (_calibrationResult.WasCancelled)
                {
                    Dispatcher.Invoke(ReturnToSetup);
                    CalibrationCancelled?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowCompletion(false, $"Calibration failed: {_calibrationResult.Message}");

                        if (Vm.SoundOnCompletion)
                            CalibrationSounds.PlayFailure();
                    });

                    CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, _calibrationResult.Message));
                }
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(ReturnToSetup);
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

                    if (Vm.SoundOnCompletion)
                        CalibrationSounds.PlayFailure();
                });

                CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, ex.Message));
            }
            finally
            {
                _isCalibrationRunning = false;
                _elapsedTimer.Stop();
                UnwireOrchestratorEvents();

                // Teardown is done (state restored, session closed) - release the
                // "Shutting down calibration..." wait dialog if a cancel opened it.
                Dispatcher.Invoke(() =>
                {
                    _shutdownDialog?.Dismiss();
                    _shutdownDialog = null;
                });
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

                Vm.ProgressPercent = e.ProgressPercent;
                Vm.PatchInfoText = $"Patch {e.CurrentIndex + 1} of {e.TotalPatches}";

                if (e.CurrentPatch != null)
                {
                    Vm.CurrentPatchText = e.CurrentPatch.Name ?? GetPatchDescription(e.CurrentPatch);
                    Vm.PhaseText = e.CurrentPatch.Category.ToString();
                }

                Vm.NextPatchText = e.NextPatch != null
                    ? e.NextPatch.Name ?? GetPatchDescription(e.NextPatch)
                    : "(last patch)";

                Vm.TimeInfoText = $"Elapsed: {FormatElapsedTime(e.Elapsed)} • Remaining: ~{FormatElapsedTime(e.EstimatedRemaining)}";
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
                        Vm.IsPauseOverlayVisible = true;
                        Vm.PauseButtonText = "Paused";
                        Vm.IsPauseEnabled = false;
                        break;
                    case CalibrationState.Running:
                        Vm.IsPauseOverlayVisible = false;
                        Vm.PauseButtonText = "Pause";
                        Vm.IsPauseEnabled = true;
                        break;
                }
            });
        }

        private void Orchestrator_MeasurementTaken(object? sender, MeasurementEventArgs e)
        {
            // Could update UI with measurement details if needed
            Dispatcher.Invoke(() =>
            {
                if (Vm.SoundOnCapture)
                    CalibrationSounds.PlayCapture();
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
                    Vm.TimeInfoText = e.Message;
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

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            CalibrationSounds.Muted = !CalibrationSounds.Muted;
            UpdateMuteButton();
        }

        private void UpdateMuteButton() =>
            Vm.MuteButtonText = CalibrationSounds.Muted ? "Sound: Muted" : "Sound: On";

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = true;
            _orchestrator?.Pause();
            Vm.IsPauseOverlayVisible = true;
            Vm.PauseButtonText = "Paused";
            Vm.IsPauseEnabled = false;
        }

        private async void Resume_Click(object sender, RoutedEventArgs e)
        {
            // Countdown before resuming
            for (int i = 3; i > 0; i--)
            {
                Vm.ResumeCountdownText = $"Resuming in {i}...";
                await Task.Delay(1000);
            }

            _isPaused = false;
            _orchestrator?.Resume();
            Vm.IsPauseOverlayVisible = false;
            Vm.PauseButtonText = "Pause";
            Vm.IsPauseEnabled = true;
            Vm.ResumeCountdownText = "Click Resume to continue";
        }

        private void CancelMeasurement_Click(object sender, RoutedEventArgs e)
        {
            if (ConfirmDialog.Confirm(this, "Cancel Calibration",
                    "Are you sure you want to cancel the calibration?\nAll progress will be lost."))
            {
                // Shutdown takes several seconds with no other visible change: the
                // in-flight read completes, the spotread session shuts down gracefully
                // (releases the meter's USB handle) and the display state is restored.
                // Keep an animated wait dialog up until RunCalibrationAsync finishes.
                Vm.IsPauseEnabled = false;
                Vm.IsCancelEnabled = false;
                _shutdownDialog ??= BusyDialog.Open(this, "Shutting down calibration...");

                _isCancelled = true;
                _orchestrator?.Cancel();
                _cancellationTokenSource?.Cancel();
                // Don't close: RunCalibrationAsync observes the cancellation and returns the
                // user to the setup screen so they can adjust and restart.
            }
        }

        /// <summary>
        /// After a cancelled run: restore the pre-calibration correction state and return to
        /// the initial setup screen (instead of closing), so the user can tweak and restart.
        /// </summary>
        private void ReturnToSetup()
        {
            if (_bypassApplied && _stateManager != null)
            {
                try
                {
                    _stateManager.RestorePreviousState();
                    _bypassApplied = false;
                    Log.Info("CalibrationWindow: Restored previous state after cancel");
                }
                catch (Exception ex)
                {
                    Log.Info($"CalibrationWindow: Failed to restore state: {ex.Message}");
                }
            }

            _isCancelled = false;
            Vm.IsMeasurementVisible = false;
            Vm.IsCompletionVisible = false;
            Vm.IsPauseOverlayVisible = false;
            Vm.IsPositioningVisible = false;
            Vm.ShowPositioningWindowedBanner = false;
            Vm.ShowWindowedModeBanner = false;
            Vm.IsSetupVisible = true;

            // Re-arm the measurement controls for the next run
            Vm.IsCancelEnabled = true;
            Vm.IsPauseEnabled = true;
            Vm.PauseButtonText = "Pause";
            Vm.ResumeCountdownText = "Click Resume to continue";

            // Same window restore as PositioningBack_Click: original setup chrome.
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowState = WindowState.Normal;
            Width = 600;
            Height = 700;
            Topmost = false;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
            Close();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            // The window background is transparent for true rounded corners; when
            // maximized for full-screen measurement the rounding would leave see-through
            // notches at the screen corners, so square it off.
            bool maximized = WindowState == WindowState.Maximized;
            RootBorder.CornerRadius = new CornerRadius(maximized ? 0 : 8);
            RootBorder.BorderThickness = new Thickness(maximized ? 0 : 1);
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

        private bool _reportOpened;

        private void ViewReport_Click(object sender, RoutedEventArgs e)
        {
            if (_calibrationResult?.Success != true || _calibrationTarget == null)
            {
                ConfirmDialog.Info(this, "Error", "No calibration data available.");
                return;
            }

            // One report per calibration: each report auto-applies + verifies, so opening
            // more than one (auto-open + a manual click) would install duplicate profiles
            // and run duplicate sweeps.
            if (_reportOpened) { Close(); return; }
            _reportOpened = true;

            // Create a calibration profile from the results
            var profile = new CalibrationProfile
            {
                MonitorDevicePath = _targetMonitor?.MonitorDevicePath ?? "calibration_session",
                MonitorName = !string.IsNullOrWhiteSpace(_targetMonitor?.FriendlyName)
                    ? _targetMonitor!.FriendlyName : "Calibrated Display",
                Target = _calibrationTarget,
                LutSize = _generatedLut?.Size ?? 33,
                PatchCount = _calibrationResult.Measurements?.Count ?? 0,
                MeasurementTime = _calibrationResult.TotalTime,
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
            var reportWindow = new CalibrationReportWindow(
                profile, _calibrationMetrics, _displayCharacterization, _generatedLut,
                _calibrationResult?.Measurements);

            // Pre-check "Detailed verification" so the auto-verify on load (AutoApplyOnLoad)
            // runs the extended sweep the user asked for on the finalize screen.
            reportWindow.Vm.IsDetailedVerifyChecked = Vm.RunDetailedVerify;

            // Closed-loop before/after: the real measured improvement, not just the native error.
            if (_calibrationResult is { ClosedLoopRan: true,
                    NativeResidualDeltaE: double beforeDe, CorrectedResidualDeltaE: double afterDe })
            {
                reportWindow.SetBeforeAfter(beforeDe, afterDe, _calibrationResult.RefinementRounds);
            }

            // Give the report what it needs to install the calibration natively. Prefer the
            // closed-loop's final correction (verified on-screen); otherwise derive per-channel
            // correction LUTs from the characterization.
            if (_targetMonitor != null && _calibrationTarget != null && _displayCharacterization != null)
            {
                double white = _targetMonitor.SdrWhiteLevel;

                // TONE-ONLY per-channel LUTs for the MHC2: each channel is inverse-mapped to
                // the target transfer with no cross-channel white balance. The MHC2 *matrix*
                // does ALL the chromatic correction (primaries + white point). Using the
                // closed-loop correction here instead would white-balance a second time on top
                // of the matrix — that double-correction is what turned the image magenta.
                double targetGamma = _calibrationTarget.Gamma ?? 2.2;
                var (lr, lg, lb, _) = LutGenerator.GenerateCalibratedLut(
                    targetGamma, _displayCharacterization, CalibrationSettings.Default,
                    white, isHdr: false);
                (double[] r, double[] g, double[] b) corr = (lr, lg, lb);

                var monitor = _targetMonitor;
                reportWindow.SetApplyContext(new CalibrationReportWindow.ApplyContext(
                    monitor, _calibrationTarget, corr.r, corr.g, corr.b, white,
                    OnInstalled: profileName =>
                    {
                        // Retire the previous calibration's association before recording the
                        // new one — otherwise repeated applies stack associations in the
                        // color store (the report loop left three in one session).
                        var previous = _settingsManager?.GetMonitorProfile(monitor.MonitorDevicePath)?.Mhc2ProfileName;
                        if (!string.IsNullOrEmpty(previous) && previous != profileName)
                            CalibrationProfileInstaller.Disable(monitor, previous);

                        // Persist the active calibration so the live apply path composes night
                        // mode on top of it instead of double-applying the gamma curve.
                        _settingsManager?.SetMhc2Calibration(monitor.MonitorDevicePath, profileName);
                        Log.Info($"CalibrationWindow: Installed + recorded calibration profile {profileName}");
                    },
                    Colorimeter: _colorimeterService,
                    HdrMode: _measuredInHdr,
                    StateManager: _stateManager,
                    PreviousGammaMode: _previousGammaMode,
                    PreviousSettings: _previousSettings,
                    PatchSize: _patchSize,
                    PatchOffsetX: _patchOffsetX,
                    PatchOffsetY: _patchOffsetY,
                    CaptureSounds: Vm.SoundOnCapture));

                // Hands-free: the report applies the profile and verifies on open.
                reportWindow.AutoApplyOnLoad = true;
            }

            reportWindow.Show();

            Close();
        }

        #endregion

        #region UI Updates

        private void DisplayPatch(ColorPatch patch)
        {
            if (patch.Nits is double nits)
            {
                // HDR wire patch: emit through the FP16 scRGB renderer placed exactly over
                // the WPF patch area (PointToScreen = physical pixels, so DPI-safe). The
                // WPF patch underneath goes black so any sliver around the renderer window
                // doesn't contaminate the surround.
                ColorPatchBorder.Background = System.Windows.Media.Brushes.Black;
                try
                {
                    if (_hdrWireRenderer == null)
                    {
                        var topLeft = ColorPatchBorder.PointToScreen(new Point(0, 0));
                        var bottomRight = ColorPatchBorder.PointToScreen(
                            new Point(ColorPatchBorder.ActualWidth, ColorPatchBorder.ActualHeight));
                        _hdrWireRenderer = new HdrPatchRenderer(
                            (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
                            (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y));
                        Log.Info($"CalibrationWindow: HDR wire renderer created at " +
                                 $"({topLeft.X:F0},{topLeft.Y:F0}) {bottomRight.X - topLeft.X:F0}x{bottomRight.Y - topLeft.Y:F0}.");
                    }
                    _hdrWireRenderer.PresentNits(nits, nits, nits);
                }
                catch (Exception ex)
                {
                    // The measurement will read whatever is on screen (black) and the
                    // builder will fall back to the SDR-mapped path - degraded, not fatal.
                    Log.Info($"CalibrationWindow: HDR wire renderer failed ({ex.Message}); wire patch shows black.");
                }
                return;
            }

            DisposeHdrWireRenderer();

            // Convert patch RGB to WPF color
            byte r = (byte)(Math.Clamp(patch.DisplayRgb.R, 0, 1) * 255);
            byte g = (byte)(Math.Clamp(patch.DisplayRgb.G, 0, 1) * 255);
            byte b = (byte)(Math.Clamp(patch.DisplayRgb.B, 0, 1) * 255);
            ColorPatchBorder.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void DisposeHdrWireRenderer()
        {
            if (_hdrWireRenderer == null) return;
            try { _hdrWireRenderer.Dispose(); } catch { }
            _hdrWireRenderer = null;
        }

        private void UpdateProgress()
        {
            Vm.ProgressPercent = _totalPatches > 0 ? (_currentPatchIndex * 100.0 / _totalPatches) : 0;
            Vm.PatchInfoText = $"Patch {_currentPatchIndex + 1} of {_totalPatches}";

            var elapsed = _elapsedTimer.Elapsed;
            var estimatedTotal = _currentPatchIndex > 0
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds / _currentPatchIndex * _totalPatches)
                : TimeSpan.FromSeconds(_totalPatches * 3); // ~3 seconds per patch estimate
            var remaining = estimatedTotal - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            Vm.TimeInfoText = $"Elapsed: {FormatElapsedTime(elapsed)} • Remaining: ~{FormatElapsedTime(remaining)}";

            if (_patches != null && _currentPatchIndex < _patches.Count)
            {
                var currentPatch = _patches[_currentPatchIndex];
                Vm.CurrentPatchText = currentPatch.Name ?? GetPatchDescription(currentPatch);
                Vm.PhaseText = currentPatch.Category.ToString();

                if (_currentPatchIndex + 1 < _patches.Count)
                {
                    var nextPatch = _patches[_currentPatchIndex + 1];
                    Vm.NextPatchText = nextPatch.Name ?? GetPatchDescription(nextPatch);
                }
                else
                {
                    Vm.NextPatchText = "(last patch)";
                }
            }
        }

        private void ShowCompletion(bool success, string message)
        {
            Vm.IsCompletionVisible = true;
            Vm.IsPauseOverlayVisible = false;

            if (success)
            {
                Vm.CompletionIcon = "✓";
                Vm.CompletionIconBrush = FindResource("SuccessBrush") as SolidColorBrush ?? CalibrationViewModel.SuccessBrush;
                Vm.CompletionTitle = "Calibration Complete";
                Vm.IsViewReportVisible = true;

                // Show display mode options if bypass was applied
                if (_bypassApplied && _stateManager != null)
                {
                    Vm.ShowDisplayModeOptions = true;
                    // Default to calibration only view
                    Vm.IsViewCalibrationOnly = true;
                }
            }
            else
            {
                Vm.CompletionIcon = "✕";
                Vm.CompletionIconBrush = FindResource("ErrorBrush") as SolidColorBrush ?? CalibrationViewModel.ErrorBrush;
                Vm.CompletionTitle = "Calibration Failed";
                Vm.IsViewReportVisible = false;
                Vm.ShowDisplayModeOptions = false;

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

            Vm.CompletionMessage = message;
        }

        private void DisplayModeOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_stateManager == null || _targetMonitor == null || !_bypassApplied)
                return;

            try
            {
                if (Vm.IsViewCalibrationOnly)
                {
                    // Apply calibration LUT only (no additional corrections)
                    _stateManager.ApplyCalibrationOnly(_targetMonitor, _generatedLut);
                    Log.Info("CalibrationWindow: Switched to calibration-only view");
                }
                else
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

        // Internal: the report window's verify sweep reuses the same fallback labels.
        internal static string GetPatchDescription(ColorPatch patch)
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
                Vm.IsCompletionVisible = false;
                _isCancelled = false;
                _isPaused = false;

                // Restart calibration by going back to positioning
                Vm.IsPositioningVisible = true;
                Vm.IsMeasurementVisible = false;
            }
            else
            {
                // User doesn't want to retry - show failure message
                ShowCompletion(false, "USB driver installation required.\n\nInstall the ArgyllCMS USB drivers and try again.");

                if (Vm.SoundOnCompletion)
                    CalibrationSounds.PlayFailure();

                CalibrationCompleted?.Invoke(this, new CalibrationCompleteEventArgs(false, "USB driver error"));
            }
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

