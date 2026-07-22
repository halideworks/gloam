using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        // Raw-pixel placement: WPF Left/Top/Width/Height are DIPs, so assigning monitor
        // PIXEL bounds to them mis-sizes the window at any scale other than 100% (and can
        // land it on the wrong monitor entirely). Same approach as PatchDisplayWindow.
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int x, int y, int cx, int cy, uint uFlags);
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        // Effective DPI of a monitor (MDT_EFFECTIVE_DPI = 0), for converting the DIP size
        // of the windowed-mode chrome into pixels on the TARGET monitor before centering.
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

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
        private string? _disabledProfileForMeasurement;
        private bool _disabledProfileForMeasurementHdrMode;

        // Non-Gloam MHC2 default profiles the user chose to disable for the measurement
        // run. Restored on EVERY exit path (cancel, failure, close, driver dialog) via
        // RestoreProfileDisabledForMeasurement; a persisted intent file additionally lets
        // the next launch restore them if the app crashes mid-run.
        private List<CalibrationInstallPreflight.ForeignDefaultProfile>? _foreignProfilesDisabledForMeasurement;

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

        public sealed record UiState(
            bool IsFullScreenMode,
            bool AlwaysOnTop,
            bool SoundOnCompletion,
            bool SoundOnCapture,
            bool RunDetailedVerify,
            int PatchSize,
            double PatchOffsetX,
            double PatchOffsetY);

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

        public ColorimeterService? ColorimeterService => _colorimeterService;

        public UiState CaptureUiState() => new(
            Vm.IsFullScreenMode,
            Vm.AlwaysOnTop,
            Vm.SoundOnCompletion,
            Vm.SoundOnCapture,
            Vm.RunDetailedVerify,
            _patchSize,
            _patchOffsetX,
            _patchOffsetY);

        public void ApplyUiState(UiState? state)
        {
            if (state == null) return;

            Vm.IsFullScreenMode = state.IsFullScreenMode;
            Vm.AlwaysOnTop = state.AlwaysOnTop;
            Vm.SoundOnCompletion = state.SoundOnCompletion;
            Vm.SoundOnCapture = state.SoundOnCapture;
            Vm.RunDetailedVerify = state.RunDetailedVerify;
            _patchSize = Math.Clamp(state.PatchSize, 120, 2000);
            _patchOffsetX = state.PatchOffsetX;
            _patchOffsetY = state.PatchOffsetY;
            ApplyPatchOffset();
            UpdatePatchSize();
            UpdatePositioningPatchSize();
        }

        public CalibrationWindow(ColorimeterService colorimeterService, CalibrationTarget target, CalibrationPreset preset)
            : this()
        {
            _colorimeterService = colorimeterService;
            _calibrationTarget = target;
            _calibrationPreset = preset;

            // Generate patches. For the Adaptive preset this is only the coarse SEED; the
            // orchestrator grows the run round-by-round up to the patch budget, so the
            // labels use the budget as an upper bound ("usually finishes early").
            _patches = PatchSetGenerator.GeneratePatchSet(target, preset);
            _totalPatches = _patches.Count;

            // Update UI
            if (preset == CalibrationPreset.Adaptive)
            {
                int budget = PatchSetGenerator.GetApproximatePatchCount(CalibrationPreset.Adaptive);
                Vm.PatchCountText = $"adaptive (up to {budget} patches)";
                Vm.EstimatedTimeText = $"up to {FormatEstimatedTime(budget)}, usually less";
            }
            else
            {
                Vm.PatchCountText = $"{_totalPatches} patches";
                Vm.EstimatedTimeText = FormatEstimatedTime(_totalPatches);
            }
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
            Services.WindowBoundsPersistence.Attach(
                this,
                settingsManager,
                "CalibrationSetup",
                shouldSave: () => Vm.IsSetupVisible && !_isCalibrationRunning);

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
            // If a previous run crashed after disabling a foreign color profile for
            // measurement, restore it before this session measures anything.
            RecoverStaleForeignRestoreIntent();

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
            RestoreProfileDisabledForMeasurement();

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
            // The shared placement surface owns its own keyboard behavior. During the
            // measurement itself, preserve the ability to fine-tune the live patch.
            if (Vm.IsPositioningVisible &&
                ProbePlacement.TryNudge(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
            {
                e.Handled = true;
                return;
            }
            if (Vm.IsMeasurementVisible &&
                TryNudgePatch(e.Key, (Keyboard.Modifiers & ModifierKeys.Shift) != 0))
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

        private void RestoreProfileDisabledForMeasurement()
        {
            RestoreForeignProfilesDisabledForMeasurement();

            if (_targetMonitor == null || string.IsNullOrEmpty(_disabledProfileForMeasurement))
                return;

            string profileName = _disabledProfileForMeasurement;
            _disabledProfileForMeasurement = null;
            if (CalibrationProfileInstaller.Reenable(_targetMonitor, profileName, _disabledProfileForMeasurementHdrMode))
            {
                _settingsManager?.SetMhc2Calibration(_targetMonitor.MonitorDevicePath, profileName);
                Log.Info($"CalibrationWindow: Restored calibration profile disabled for measurement: {profileName}");
            }
            else
            {
                Log.Info($"CalibrationWindow: Could not restore calibration profile disabled for measurement: {profileName}");
            }
        }

        /// <summary>
        /// Restores any NON-Gloam MHC2 profiles that were temporarily disabled for the
        /// measurement run. Called from every exit path (cancel, failure, window close,
        /// driver dialog) via <see cref="RestoreProfileDisabledForMeasurement"/>; idempotent.
        /// </summary>
        private void RestoreForeignProfilesDisabledForMeasurement()
        {
            var foreign = _foreignProfilesDisabledForMeasurement;
            _foreignProfilesDisabledForMeasurement = null;
            if (_targetMonitor == null || foreign is not { Count: > 0 })
                return;

            foreach (var f in foreign)
            {
                bool restored = CalibrationProfileInstaller.RestoreDefaultProfile(
                    _targetMonitor, f.ProfileName, f.IsAdvancedColor);
                Log.Info(restored
                    ? $"CalibrationWindow: restored foreign profile disabled for measurement: {f.ProfileName}"
                    : $"CalibrationWindow: could not restore foreign profile disabled for measurement: {f.ProfileName}");
            }
            ClearForeignRestoreIntent();
        }

        #region Foreign-profile restore intent (crash recovery)

        // If the app dies mid-measurement the in-memory restore list is gone; this file
        // records what was disabled so the NEXT calibration window can put it back —
        // the same "remember what to restore" idea as MonitorProfileData.PreviousColorProfileName.
        private static string ForeignRestoreIntentPath =>
            Path.Combine(AppPaths.DataDir, "pending-foreign-profile-restore.json");

        private sealed class ForeignRestoreIntentFile
        {
            public string? MonitorDevicePath { get; set; }
            public string? DeviceName { get; set; }
            public List<ForeignRestoreEntry> Profiles { get; set; } = new();
        }

        private sealed class ForeignRestoreEntry
        {
            public string ProfileName { get; set; } = "";
            public bool IsAdvancedColor { get; set; }
        }

        private static void PersistForeignRestoreIntent(
            MonitorInfo monitor, IReadOnlyList<CalibrationInstallPreflight.ForeignDefaultProfile> profiles)
        {
            try
            {
                var intent = new ForeignRestoreIntentFile
                {
                    MonitorDevicePath = monitor.MonitorDevicePath,
                    DeviceName = monitor.DeviceName,
                    Profiles = profiles.Select(p => new ForeignRestoreEntry
                    {
                        ProfileName = p.ProfileName,
                        IsAdvancedColor = p.IsAdvancedColor
                    }).ToList()
                };
                Directory.CreateDirectory(AppPaths.DataDir);
                File.WriteAllText(ForeignRestoreIntentPath,
                    System.Text.Json.JsonSerializer.Serialize(intent));
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationWindow: could not persist foreign-profile restore intent: {ex.Message}");
            }
        }

        private static void ClearForeignRestoreIntent()
        {
            try
            {
                if (File.Exists(ForeignRestoreIntentPath))
                    File.Delete(ForeignRestoreIntentPath);
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationWindow: could not clear foreign-profile restore intent: {ex.Message}");
            }
        }

        /// <summary>
        /// Crash recovery: if a previous run disabled a foreign profile and never restored
        /// it (app crash mid-measurement), put it back now. Best-effort; never throws.
        /// </summary>
        private void RecoverStaleForeignRestoreIntent()
        {
            try
            {
                if (!File.Exists(ForeignRestoreIntentPath)) return;
                var intent = System.Text.Json.JsonSerializer.Deserialize<ForeignRestoreIntentFile>(
                    File.ReadAllText(ForeignRestoreIntentPath));
                if (intent?.Profiles is not { Count: > 0 })
                {
                    ClearForeignRestoreIntent();
                    return;
                }

                // Resolve the monitor the intent refers to: the current target if it
                // matches, otherwise a fresh enumeration.
                MonitorInfo? monitor = null;
                if (_targetMonitor != null && string.Equals(
                        _targetMonitor.MonitorDevicePath, intent.MonitorDevicePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    monitor = _targetMonitor;
                }
                else
                {
                    monitor = new MonitorManager().EnumerateMonitors().FirstOrDefault(m =>
                        string.Equals(m.MonitorDevicePath, intent.MonitorDevicePath, StringComparison.OrdinalIgnoreCase));
                }
                if (monitor == null)
                {
                    // Display not attached right now; keep the intent for a later launch.
                    Log.Info("CalibrationWindow: stale foreign-profile restore intent found but its display is not attached; keeping it.");
                    return;
                }

                foreach (var p in intent.Profiles)
                {
                    if (string.IsNullOrWhiteSpace(p.ProfileName)) continue;
                    bool restored = CalibrationProfileInstaller.RestoreDefaultProfile(
                        monitor, p.ProfileName, p.IsAdvancedColor);
                    Log.Info(restored
                        ? $"CalibrationWindow: recovered foreign profile from a previous crashed run: {p.ProfileName}"
                        : $"CalibrationWindow: could not recover foreign profile from a previous run: {p.ProfileName}");
                }
                ClearForeignRestoreIntent();
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationWindow: foreign-profile crash recovery failed: {ex.Message}");
            }
        }

        #endregion

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
                // The move is done in RAW PIXELS via SetWindowPos (see MoveWindowToPixelRect):
                // monitor bounds are physical pixels, and pushing them through WPF's
                // DIP-based Left/Width mis-sized the patch surface at any scale other than
                // 100% — and could even land the window on the WRONG monitor.
                if (TryGetTargetMonitorPixelBounds(out RECT px, out _))
                {
                    MoveWindowToPixelRect(px);
                }
                else
                {
                    // Last resort (no target, no HWND): primary in DIPs, as before.
                    Left = 0;
                    Top = 0;
                    Width = SystemParameters.PrimaryScreenWidth;
                    Height = SystemParameters.PrimaryScreenHeight;
                }

                Vm.ShowWindowedModeBanner = false;
                Topmost = true;
            }
            else
            {
                // Windowed mode - keep chromeless style but allow resizing
                ResizeMode = ResizeMode.CanResizeWithGrip;

                // Reasonable window size, centered on the TARGET monitor (not the primary:
                // when calibrating a secondary display the windowed patch must sit on the
                // panel being measured).
                CenterOnTargetMonitor(600, 500);

                Vm.ShowWindowedModeBanner = true;
                Topmost = Vm.AlwaysOnTop;
            }

            // Update patch size
            UpdatePatchSize();
        }

        /// <summary>
        /// Physical-pixel bounds of the monitor being calibrated: the target's HMONITOR
        /// first (most reliable across DPI/topologies), the DXGI desktop bounds captured
        /// at enumeration second, and the monitor this window currently sits on last.
        /// </summary>
        private bool TryGetTargetMonitorPixelBounds(out RECT bounds, out IntPtr hMonitor)
        {
            bounds = default;
            hMonitor = IntPtr.Zero;

            if (_targetMonitor != null)
            {
                IntPtr targetHmon = _targetMonitor.HMonitor;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (targetHmon != IntPtr.Zero && GetMonitorInfo(targetHmon, ref mi))
                {
                    bounds = mi.rcMonitor;
                    hMonitor = targetHmon;
                    return true;
                }

                var b = _targetMonitor.MonitorBounds;
                if (b.Right > b.Left && b.Bottom > b.Top)
                {
                    bounds = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
                    hMonitor = MonitorFromPoint(new POINT
                    {
                        X = (b.Left + b.Right) / 2,
                        Y = (b.Top + b.Bottom) / 2
                    }, MONITOR_DEFAULTTONEAREST);
                    return true;
                }
            }

            var helper = new WindowInteropHelper(this);
            if (helper.Handle != IntPtr.Zero)
            {
                IntPtr hMon = MonitorFromWindow(helper.Handle, MONITOR_DEFAULTTONEAREST);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    bounds = mi.rcMonitor;
                    hMonitor = hMon;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Positions/sizes this window at an exact PHYSICAL-pixel rect via SetWindowPos —
        /// the same approach PatchDisplayWindow uses, so calibration and verification render
        /// the patch at the same physical size. Works whether or not the HWND exists yet
        /// (initial show vs. later monitor/mode changes), and re-asserts the rect once WPF
        /// has processed any WM_DPICHANGED from crossing into a different-DPI monitor.
        /// </summary>
        private void MoveWindowToPixelRect(RECT px)
        {
            void Apply()
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                SetWindowPos(hwnd, IntPtr.Zero, px.Left, px.Top,
                    px.Right - px.Left, px.Bottom - px.Top, SWP_NOZORDER | SWP_NOACTIVATE);
                // Crossing a DPI boundary makes WPF rescale the window after this call
                // returns; re-assert the exact pixel rect once the dust settles.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    if (h != IntPtr.Zero)
                        SetWindowPos(h, IntPtr.Zero, px.Left, px.Top,
                            px.Right - px.Left, px.Bottom - px.Top, SWP_NOZORDER | SWP_NOACTIVATE);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
            {
                Apply();
            }
            else
            {
                // Not shown yet: rough WPF placement so the window materializes near the
                // right monitor (and DPI context), then pixel-exact once the HWND exists.
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = px.Left;
                Top = px.Top;
                void OnSourceInitialized(object? s, EventArgs e)
                {
                    SourceInitialized -= OnSourceInitialized;
                    Apply();
                }
                SourceInitialized += OnSourceInitialized;
            }
        }

        /// <summary>
        /// Sizes the window to a DIP size and centers it on the TARGET monitor, converting
        /// through that monitor's effective DPI. Falls back to primary-screen centering
        /// only when no monitor can be resolved at all.
        /// </summary>
        private void CenterOnTargetMonitor(double dipWidth, double dipHeight)
        {
            Width = dipWidth;
            Height = dipHeight;

            if (TryGetTargetMonitorPixelBounds(out RECT px, out IntPtr hMonitor))
            {
                double scale = GetMonitorDpiScale(hMonitor);
                int w = (int)Math.Round(dipWidth * scale);
                int h = (int)Math.Round(dipHeight * scale);
                int x = px.Left + (px.Right - px.Left - w) / 2;
                int y = px.Top + (px.Bottom - px.Top - h) / 2;
                MoveWindowToPixelRect(new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h });
            }
            else
            {
                Left = (SystemParameters.PrimaryScreenWidth - dipWidth) / 2;
                Top = (SystemParameters.PrimaryScreenHeight - dipHeight) / 2;
            }
        }

        private static double GetMonitorDpiScale(IntPtr hMonitor)
        {
            try
            {
                if (hMonitor != IntPtr.Zero &&
                    GetDpiForMonitor(hMonitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 &&
                    dpiX > 0)
                    return dpiX / 96.0;
            }
            catch { /* shcore unavailable -> assume 100% */ }
            return 1.0;
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

            // Show positioning panel for user to place colorimeter
            Vm.IsSetupVisible = false;
            Vm.IsPositioningVisible = true;
            ProbePlacement.Focus();
        }

        private void UpdatePositioningPatchSize()
        {
            ProbePlacement.Configure(
                _patchSize,
                _patchOffsetX,
                _patchOffsetY,
                operationLabel: "Calibration",
                secondaryLabel: "Back",
                showWindowedBanner: Vm.IsWindowedMode);
        }

        private void PositioningBack_Click(object sender, RoutedEventArgs e)
        {
            _patchOffsetX = ProbePlacement.OffsetX;
            _patchOffsetY = ProbePlacement.OffsetY;
            // Go back to setup panel
            Vm.IsPositioningVisible = false;
            Vm.IsSetupVisible = true;
            RestoreWindowMode();
        }

        private async void BeginMeasurement_Click(object sender, RoutedEventArgs e)
        {
            if (_colorimeterService is null || _calibrationTarget is null)
            {
                Log.Error("CalibrationWindow: measurement requested without a colorimeter or calibration target");
                ConfirmDialog.Info(this, "Calibration unavailable",
                    "The calibration setup is incomplete. Go back and select a meter and target before measuring.");
                return;
            }

            // The reusable placement control owns drag/nudge state until the user commits
            // it. Measurement and all follow-up report operations receive these coordinates.
            _patchOffsetX = ProbePlacement.OffsetX;
            _patchOffsetY = ProbePlacement.OffsetY;

            // MEASUREMENT-START GATE: Windows Night Light warms the whole output at the
            // compositor — every reading through it is corrupted. Detection is a heuristic
            // over an undocumented registry blob, so only a confident "on" blocks here
            // (unknown/off proceed silently; see CalibrationInstallPreflight).
            if (CalibrationInstallPreflight.DetectNightLightActive() == true)
            {
                Log.Info("CalibrationWindow: Night Light detected active at measurement start.");
                if (!ConfirmDialog.Confirm(this, "Night Light Is Active",
                        CalibrationInstallPreflight.NightLightWarning +
                        "\n\nSettings > System > Display > Night light.\n\nMeasure anyway?",
                        confirmLabel: "Measure Anyway", cancelLabel: "Cancel"))
                {
                    return;
                }
            }

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
                    _disabledProfileForMeasurement = activeProfile;
                    _disabledProfileForMeasurementHdrMode = _targetMonitor.IsHdrActive;
                    CalibrationProfileInstaller.Disable(_targetMonitor, activeProfile);
                    _settingsManager?.SetMhc2Calibration(_targetMonitor.MonitorDevicePath, null);
                }

                // Belt and braces: also retire any STALE app-generated associations (a past
                // bug installed several in one session). If one became the fallback default,
                // "native" would silently measure through it.
                CalibrationProfileInstaller.DisableAllForMonitor(_targetMonitor);

                // FOREIGN-CORRECTION PREFLIGHT: a default profile from ANOTHER tool
                // (DisplayCAL, Windows HDR Calibration, vendor software) that carries an
                // MHC2 tag is being applied by the compositor right now. Characterizing
                // "native" through it would bake a double correction into this run. Check
                // BOTH association lists (SDR ICMProfile and Advanced-Color ICMProfileAC)
                // and offer to disable it for the measurement, restoring afterwards.
                try
                {
                    string gloamPrefix = CalibrationProfileInstaller.BuildProfileNamePrefix(_targetMonitor);
                    var foreignDefaults = CalibrationInstallPreflight.AssessForeignDefaults(
                        CalibrationProfileInstaller.GetCurrentDefaultProfile(_targetMonitor, hdrMode: false),
                        CalibrationProfileInstaller.GetCurrentDefaultProfile(_targetMonitor, hdrMode: true),
                        gloamPrefix,
                        CalibrationInstallPreflight.ProfileHasMhc2Tag);

                    var mhc2Foreign = foreignDefaults.Where(f => f.HasMhc2Tag).ToList();
                    if (mhc2Foreign.Count > 0 && _foreignProfilesDisabledForMeasurement == null)
                    {
                        string names = string.Join("\n", mhc2Foreign.Select(f =>
                            $"  • {f.ProfileName} ({(f.IsAdvancedColor ? "Advanced Color" : "SDR")} default)"));
                        Log.Info($"CalibrationWindow: foreign MHC2 default profile(s) detected before measurement:\n{names}");
                        bool disable = ConfirmDialog.Confirm(this, "Another Correction Is Active",
                            "Windows is applying a color correction from another tool on this display:\n\n" +
                            names + "\n\n" +
                            "Measuring through it would characterize an already-corrected panel and " +
                            "bake a DOUBLE correction into this calibration.\n\n" +
                            "Temporarily disable it during measurement and restore it afterwards?",
                            confirmLabel: "Disable During Measurement",
                            cancelLabel: "Keep It Active");
                        if (disable)
                        {
                            // Persist the restore intent FIRST so a crash mid-run can
                            // still restore on the next launch, then disassociate.
                            _foreignProfilesDisabledForMeasurement = mhc2Foreign;
                            PersistForeignRestoreIntent(_targetMonitor, mhc2Foreign);
                            foreach (var f in mhc2Foreign)
                            {
                                CalibrationProfileInstaller.Disable(_targetMonitor, f.ProfileName);
                                Log.Info($"CalibrationWindow: temporarily disabled foreign profile '{f.ProfileName}' for measurement.");
                            }
                            await Task.Delay(300); // let the compositor drop it
                        }
                        else
                        {
                            Log.Info("CalibrationWindow: user kept the foreign MHC2 profile active; measurements include it.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Detection must never block calibration.
                    Log.Info($"CalibrationWindow: foreign-profile preflight failed (continuing): {ex.Message}");
                }

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
                    // Generate the calibration artifact from measurements.
                    Dispatcher.Invoke(() =>
                    {
                        Vm.PhaseText = _measuredInHdr ? "Building HDR profile model..." : "Generating LUT...";
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

                    if (_measuredInHdr)
                    {
                        // HDR install builds Windows Advanced Color MHC2 PQ LUTs from the
                        // measured patches. Do not also emit a generic 33³ SDR-oriented LUT:
                        // it is not what gets installed and can mislead reports/exports.
                        _generatedLut = null;
                        _displayCharacterization = generator.BuildCharacterizationOnly(hdrMode: true);
                        Dispatcher.Invoke(() =>
                        {
                            Vm.ProgressPercent = 100;
                            Vm.SetProgressLabel("Model built");
                        });
                    }
                    else
                    {
                        _generatedLut = generator.Generate(progress =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                Vm.ProgressPercent = 100;
                                Vm.SetProgressLabel("Generating...");
                            });
                        });

                        _displayCharacterization = generator.Characterization;
                    }
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

        // Re-entrancy guard for Resume_Click: it's async void with a 3s countdown, and
        // both the Resume button and the Space key route here — without the guard a
        // second trigger stacks a second countdown (and a second Resume()).
        private bool _isResumeCountdownRunning;

        private async void Resume_Click(object sender, RoutedEventArgs e)
        {
            if (_isResumeCountdownRunning || !_isPaused) return;
            _isResumeCountdownRunning = true;

            // Disable the clicked button for the countdown (the Space path has no button;
            // the guard flag covers it). SetCurrentValue, NOT a local set: IsEnabled is
            // bound to IsCancelEnabled in XAML and a local value would kill the binding.
            var resumeButton = sender as System.Windows.Controls.Button;
            resumeButton?.SetCurrentValue(IsEnabledProperty, false);

            try
            {
                // Countdown before resuming. Abort silently if the run is cancelled (or
                // the pause state was torn down) mid-countdown.
                for (int i = 3; i > 0; i--)
                {
                    Vm.ResumeCountdownText = $"Resuming in {i}...";
                    await Task.Delay(1000);
                    if (_isCancelled || !_isPaused || !_isCalibrationRunning)
                    {
                        Vm.ResumeCountdownText = "Click Resume to continue";
                        return;
                    }
                }

                _isPaused = false;
                _orchestrator?.Resume();
                Vm.IsPauseOverlayVisible = false;
                Vm.PauseButtonText = "Pause";
                Vm.IsPauseEnabled = true;
                Vm.ResumeCountdownText = "Click Resume to continue";
            }
            finally
            {
                _isResumeCountdownRunning = false;
                if (resumeButton != null)
                {
                    // Restore the XAML binding's value (or plain enabled if unbound).
                    var binding = resumeButton.GetBindingExpression(IsEnabledProperty);
                    if (binding != null) binding.UpdateTarget();
                    else resumeButton.SetCurrentValue(IsEnabledProperty, true);
                }
            }
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
            RestoreProfileDisabledForMeasurement();

            _isCancelled = false;
            Vm.IsMeasurementVisible = false;
            Vm.IsCompletionVisible = false;
            Vm.IsPauseOverlayVisible = false;
            Vm.IsPositioningVisible = false;
            Vm.ShowWindowedModeBanner = false;
            Vm.IsSetupVisible = true;

            // Re-arm the measurement controls for the next run
            Vm.IsCancelEnabled = true;
            Vm.IsPauseEnabled = true;
            Vm.PauseButtonText = "Pause";
            Vm.ResumeCountdownText = "Click Resume to continue";

            // Same window restore as PositioningBack_Click: original setup chrome,
            // centered on the TARGET monitor (a cancelled secondary-display calibration
            // must not teleport the setup screen back to the primary).
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowState = WindowState.Normal;
            Topmost = false;
            CenterOnTargetMonitor(600, 700);
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

        // Patch placement chosen by ProbePlacement carries into the live measurement patch.
        private void ApplyPatchOffset()
        {
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
                // These measurements describe the native panel. The verified correction
                // fills PostCalibrationDeltaE after Apply/Verify (and after every refine).
                PreCalibrationDeltaE = _calibrationMetrics?.AverageDeltaE,
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
                _calibrationResult?.Measurements,
                // Persisted pre-compensation drift so the uncertainty budget can honestly
                // size the drift-residual term (the measurements above are already
                // drift-normalized, so the report can no longer recover this itself).
                _calibrationResult?.PeakWhiteDriftFraction,
                _calibrationResult?.DriftCompensationApplied ?? false);

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

                // TONE-ONLY per-channel LUTs for the MHC2: no cross-channel white balance.
                // The MHC2 *matrix* does ALL the chromatic correction (primaries + white
                // point). Shipping the raw closed-loop correction here would white-balance a
                // second time on top of the matrix — that double-correction is what turned
                // the image magenta.
                //
                // M4: when the closed loop ran, its final VCGT is the MEASURED-verified tone;
                // decompose it into neutral tone × white gains and ship ONLY the neutral
                // component (the gains' job belongs to the matrix). This way the installed
                // profile carries the correction the loop actually verified on screen instead
                // of a fresh open-loop rebuild that silently discards the refinement.
                // M3/m4: the open-loop fallback routes the tone target through the target's
                // real EOTF (with measured black wired into BT.1886) — the same linearization
                // the closed loop and the verifier use — not a bare pow(v, gamma).
                (double[] r, double[] g, double[] b) corr;
                if (_calibrationResult is { ClosedLoopRan: true, FinalCorrection: { } finalVcgt }
                    && !_measuredInHdr)
                {
                    var (neutralTone, _, _, _) = ClosedLoopCorrector.DecomposeCorrection(
                        finalVcgt, _displayCharacterization.GreenToneCurve);
                    corr = (neutralTone, (double[])neutralTone.Clone(), (double[])neutralTone.Clone());
                }
                else
                {
                    var effectiveTarget = ClosedLoopCorrector.MakeEffectiveTarget(
                        _calibrationTarget, _displayCharacterization);
                    var (lr, lg, lb, _) = LutGenerator.GenerateCalibratedLut(
                        effectiveTarget, _displayCharacterization, CalibrationSettings.Default,
                        white, isHdr: false);
                    corr = (lr, lg, lb);
                }

                var monitor = _targetMonitor;
                reportWindow.SetApplyContext(new CalibrationReportWindow.ApplyContext(
                    monitor, _calibrationTarget, corr.r, corr.g, corr.b, white,
                    OnInstalled: (profileName, previousDefaultProfile) =>
                    {
                        // Retire the previous calibration's association before recording the
                        // new one — otherwise repeated applies stack associations in the
                        // color store (the report loop left three in one session).
                        var previous = _settingsManager?.GetMonitorProfile(monitor.MonitorDevicePath)?.Mhc2ProfileName;
                        if (!string.IsNullOrEmpty(previous) && previous != profileName)
                            CalibrationProfileInstaller.Disable(monitor, previous);

                        // Persist the active calibration so the live apply path composes night
                        // mode on top of it instead of double-applying the gamma curve.
                        _settingsManager?.SetMhc2Calibration(
                            monitor.MonitorDevicePath,
                            profileName,
                            previousDefaultProfile,
                            _measuredInHdr);
                        _disabledProfileForMeasurement = null;
                        Log.Info($"CalibrationWindow: Installed + recorded calibration profile {profileName}");
                    },
                    Colorimeter: _colorimeterService,
                    HdrMode: _measuredInHdr,
                    StateManager: _stateManager,
                    SettingsManager: _settingsManager,
                    PreviousGammaMode: _previousGammaMode,
                    PreviousSettings: _previousSettings,
                    PatchSize: _patchSize,
                    PatchOffsetX: _patchOffsetX,
                    PatchOffsetY: _patchOffsetY,
                    CaptureSounds: Vm.SoundOnCapture,
                    MeasurementDefaultProfile: CalibrationProfileInstaller.GetCurrentDefaultProfile(monitor, _measuredInHdr)));

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
                    DisposeHdrWireRenderer();
                    Log.Error($"CalibrationWindow: HDR wire renderer failed ({ex.Message}).");
                    throw new InvalidOperationException(
                        "HDR wire-ladder patches require the FP16 scRGB renderer, but it failed to present the patch. " +
                        "Keep Windows HDR enabled on the measured display and retry calibration. " +
                        $"Renderer error: {ex.Message}", ex);
                }
                return;
            }

            DisposeHdrWireRenderer();

            // Convert patch RGB to WPF color. Shared with PatchDisplayWindow.SetColor so
            // calibration and verification render IDENTICAL codes (Math.Round — a cast
            // truncates, biasing every patch down by up to 1/255, ~6% relative at 5% gray).
            byte r = PatchDisplayWindow.ToPatchByte(patch.DisplayRgb.R);
            byte g = PatchDisplayWindow.ToPatchByte(patch.DisplayRgb.G);
            byte b = PatchDisplayWindow.ToPatchByte(patch.DisplayRgb.B);
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
                Vm.CompletionIcon = "OK";
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
                Vm.CompletionIcon = "FAILED";
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
                RestoreProfileDisabledForMeasurement();
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
            RestoreProfileDisabledForMeasurement();

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
                UpdatePositioningPatchSize();
                Vm.IsPositioningVisible = true;
                Vm.IsMeasurementVisible = false;
                ProbePlacement.Focus();
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
