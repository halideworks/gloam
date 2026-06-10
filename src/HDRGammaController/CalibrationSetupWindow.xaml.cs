using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using static HDRGammaController.Core.Calibration.PatchSetGenerator;

namespace HDRGammaController
{
    /// <summary>
    /// Setup window for configuring display calibration settings before starting.
    /// </summary>
    public partial class CalibrationSetupWindow : Window
    {
        private readonly List<MonitorInfo> _monitors;
        private ColorimeterService? _colorimeterService;

        /// <summary>
        /// Gets the selected calibration target after the dialog completes.
        /// </summary>
        public CalibrationTarget? SelectedTarget { get; private set; }

        /// <summary>
        /// Gets the selected calibration preset after the dialog completes.
        /// </summary>
        public CalibrationPreset SelectedPreset { get; private set; }

        /// <summary>
        /// Gets the selected monitor after the dialog completes.
        /// </summary>
        public MonitorInfo? SelectedMonitor { get; private set; }

        /// <summary>
        /// Gets the selected display type after the dialog completes.
        /// </summary>
        public DisplayType SelectedDisplayType { get; private set; }

        /// <summary>
        /// Gets the initialized colorimeter service.
        /// </summary>
        public ColorimeterService? ColorimeterService => _colorimeterService;

        public CalibrationSetupWindow(List<MonitorInfo> monitors)
        {
            InitializeComponent();
            _monitors = monitors;

            // Populate monitor dropdown
            foreach (var monitor in monitors)
            {
                MonitorComboBox.Items.Add(new MonitorComboItem(monitor));
            }
            if (MonitorComboBox.Items.Count > 0)
            {
                MonitorComboBox.SelectedIndex = 0;
            }

            // Flash a big "this is the display" overlay on the chosen monitor whenever the
            // selection changes (and once on open), so it's unambiguous which physical screen
            // will be calibrated — critical when two identical displays are attached.
            MonitorComboBox.SelectionChanged += (_, _) => IdentifySelectedMonitor();

            // Initialize colorimeter asynchronously
            Loaded += async (s, e) =>
            {
                IdentifySelectedMonitor();
                await InitializeColorimeterAsync();
            };
        }

        private void IdentifySelectedMonitor()
        {
            if (MonitorComboBox.SelectedItem is MonitorComboItem item)
            {
                Services.DisplayIdentify.Flash(item.Monitor, MonitorComboBox.SelectedIndex + 1);
                UpdateTargetAvailability(item.Monitor);
            }
        }

        /// <summary>
        /// Greys out calibration targets the selected display can't reach (per its EDID gamut),
        /// and HDR-only targets when the display isn't in HDR — so the user only picks settings
        /// that will actually apply, before spending a calibration on them.
        /// </summary>
        private void UpdateTargetAvailability(MonitorInfo monitor)
        {
            var gamut = monitor.EdidColor;
            bool hdr = monitor.IsHdrActive;

            SetTargetReachable(Target709G22, StandardTargets.SrgbGamma22, gamut, false, hdr);
            SetTargetReachable(Target709G24, StandardTargets.Rec709Gamma24, gamut, false, hdr);
            SetTargetReachable(TargetP3D65, StandardTargets.P3D65Gamma22, gamut, false, hdr);
            SetTargetReachable(Target2020SDR, StandardTargets.Rec2020Gamma24, gamut, false, hdr);
            SetTargetReachable(Target2020PQ, StandardTargets.Rec2020Pq, gamut, true, hdr);

            // If the currently-checked target just got disabled, fall back to sRGB (always reachable).
            foreach (var rb in new[] { Target709G22, Target709G24, TargetP3D65, Target2020SDR, Target2020PQ })
                if (rb.IsChecked == true && !rb.IsEnabled) { Target709G22.IsChecked = true; break; }
        }

        private static void SetTargetReachable(
            System.Windows.Controls.RadioButton rb, CalibrationTarget target,
            EdidColorInfo? gamut, bool requiresHdr, bool displayIsHdr)
        {
            string? reason = null;
            if (requiresHdr && !displayIsHdr)
                reason = "Requires the display to be in HDR mode.";
            else if (!requiresHdr && displayIsHdr)
                reason = "This is an SDR target; switch the display to SDR to use it.";
            else if (gamut != null && !TargetFitsGamut(target, gamut))
                reason = "Exceeds this display's gamut — it can't reproduce these primaries.";

            rb.IsEnabled = reason == null;
            rb.ToolTip = reason;
            rb.Opacity = reason == null ? 1.0 : 0.45;
        }

        /// <summary>True if every target primary sits inside the display's EDID gamut triangle (with a small tolerance).</summary>
        private static bool TargetFitsGamut(CalibrationTarget t, EdidColorInfo g)
        {
            const double tol = 0.025; // EDID primaries are approximate
            return InTriangle(t.RedPrimary.X, t.RedPrimary.Y, g, tol)
                && InTriangle(t.GreenPrimary.X, t.GreenPrimary.Y, g, tol)
                && InTriangle(t.BluePrimary.X, t.BluePrimary.Y, g, tol);
        }

        private static bool InTriangle(double px, double py, EdidColorInfo g, double tol)
        {
            // Barycentric sign test against the display's R/G/B chromaticity triangle, expanded
            // outward by `tol` toward the centroid to tolerate EDID rounding.
            double cx = (g.RedX + g.GreenX + g.BlueX) / 3.0;
            double cy = (g.RedY + g.GreenY + g.BlueY) / 3.0;
            (double x, double y) Expand(double x, double y)
            {
                double dx = x - cx, dy = y - cy;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-6) return (x, y);
                return (x + dx / len * tol, y + dy / len * tol);
            }
            var (ax, ay) = Expand(g.RedX, g.RedY);
            var (bx, by) = Expand(g.GreenX, g.GreenY);
            var (cxx, cyy) = Expand(g.BlueX, g.BlueY);

            double Sign(double x1, double y1, double x2, double y2, double x3, double y3)
                => (x1 - x3) * (y2 - y3) - (x2 - x3) * (y1 - y3);
            double d1 = Sign(px, py, ax, ay, bx, by);
            double d2 = Sign(px, py, bx, by, cxx, cyy);
            double d3 = Sign(px, py, cxx, cyy, ax, ay);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos); // inside if all same sign
        }

        private void IdentifyButton_Click(object sender, System.Windows.RoutedEventArgs e) => IdentifySelectedMonitor();

        private async Task InitializeColorimeterAsync()
        {
            StatusText.Text = "Finding ArgyllCMS...";
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 0)); // Orange
            ColorimeterModelText.Text = "";

            try
            {
                string? argyllBinPath;

                // First check if we have our own downloaded version (preferred)
                if (ArgyllDownloader.IsInstalled())
                {
                    argyllBinPath = ArgyllDownloader.LocalArgyllBinDir;
                    Log.Info($"CalibrationSetupWindow: Using our downloaded ArgyllCMS from {argyllBinPath}");
                }
                else
                {
                    // Check if there's any ArgyllCMS available (might be old version from DisplayCAL)
                    argyllBinPath = FindArgyllBinPath();

                    if (string.IsNullOrEmpty(argyllBinPath))
                    {
                        // No ArgyllCMS found at all - offer to download
                        await OfferArgyllDownloadAsync("ArgyllCMS (required for colorimeter calibration) was not found.");
                    }
                    else
                    {
                        // Found some ArgyllCMS, but it might be old - check the version
                        string versionInfo = ExtractVersionFromPath(argyllBinPath);
                        if (IsOldVersion(versionInfo))
                        {
                            // Old version found - offer to download newer
                            Log.Info($"CalibrationSetupWindow: Found old ArgyllCMS version: {versionInfo}");
                            await OfferArgyllDownloadAsync(
                                $"Found ArgyllCMS {versionInfo}, but a newer version ({ArgyllDownloader.ArgyllVersion}) is recommended for better compatibility.");
                        }
                    }

                    // After potential download, prefer our version if now installed
                    if (ArgyllDownloader.IsInstalled())
                    {
                        argyllBinPath = ArgyllDownloader.LocalArgyllBinDir;
                    }
                    else
                    {
                        // Re-check in case download failed and we need fallback
                        argyllBinPath = FindArgyllBinPath();
                    }

                    if (string.IsNullOrEmpty(argyllBinPath))
                    {
                        StatusText.Text = "ArgyllCMS not installed";
                        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                        ColorimeterModelText.Text = "Click Refresh to try downloading again";
                        StartButton.IsEnabled = false;
                        return;
                    }
                }

                // Show that we found ArgyllCMS and log the version info
                StatusText.Text = "Searching for colorimeter...";
                string binDirName = Path.GetFileName(Path.GetDirectoryName(argyllBinPath) ?? argyllBinPath);
                ColorimeterModelText.Text = $"Using: {binDirName}";
                Log.Info($"CalibrationSetupWindow: Using ArgyllCMS from {argyllBinPath}");

                _colorimeterService = new ColorimeterService(argyllBinPath);

                // Add timeout for initialization
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await _colorimeterService.InitializeAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    StatusText.Text = "Detection timed out";
                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    ColorimeterModelText.Text = "Check colorimeter connection and USB drivers";
                    StartButton.IsEnabled = false;
                    return;
                }

                UpdateColorimeterStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                ColorimeterModelText.Text = "Check console for details";
                Log.Info($"Colorimeter initialization error: {ex}");
                StartButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// Extracts version string from ArgyllCMS path (e.g., "V2.3.1" from path containing "Argyll_V2.3.1").
        /// </summary>
        private static string ExtractVersionFromPath(string path)
        {
            // Look for pattern like "Argyll_V2.3.1" or "Argyll_V3.3.0" in path
            var match = Regex.Match(path, @"Argyll_V?(\d+\.\d+\.?\d*)", RegexOptions.IgnoreCase);
            if (match.Success)
                return "V" + match.Groups[1].Value;
            return "unknown";
        }

        /// <summary>
        /// Checks if the given version is older than our minimum recommended version (V3.0.0).
        /// </summary>
        private static bool IsOldVersion(string versionInfo)
        {
            // Consider anything below V3.0.0 as "old"
            if (versionInfo == "unknown")
                return true;

            var match = Regex.Match(versionInfo, @"V?(\d+)\.(\d+)");
            if (!match.Success)
                return true;

            int major = int.Parse(match.Groups[1].Value);
            // V3.0.0+ is considered modern
            return major < 3;
        }

        private async Task OfferArgyllDownloadAsync(string reason)
        {
            // Check if we should offer download
            if (ArgyllDownloader.IsInstalled())
            {
                // Already have our version installed, nothing to do
                return;
            }

            // Show styled download dialog
            var dialog = new ArgyllDownloadDialog(reason)
            {
                Owner = this
            };

            var dialogResult = dialog.ShowDialog();

            if (dialog.DownloadSucceeded)
            {
                StatusText.Text = "Download complete";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green
                ColorimeterModelText.Text = "ArgyllCMS installed successfully";
            }
            else if (dialogResult == false)
            {
                // User cancelled or download failed - status will be updated by caller
                Log.Info("ArgyllCMS download was cancelled or failed");
            }
        }

        private static string? FindArgyllBinPath()
        {
            // Use unified path finder for consistent behavior across the application
            return ArgyllPathFinder.FindArgyllBinPath();
        }

        private void UpdateColorimeterStatus()
        {
            if (_colorimeterService == null)
            {
                StatusText.Text = "Colorimeter service unavailable";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                ColorimeterModelText.Text = "";
                StartButton.IsEnabled = false;
                return;
            }

            if (_colorimeterService.IsReady)
            {
                StatusText.Text = "Connected and ready";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green
                ColorimeterModelText.Text = _colorimeterService.ConnectedColorimeter?.Model ?? "Unknown model";
                StartButton.IsEnabled = true;
            }
            else
            {
                StatusText.Text = "No colorimeter detected";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 0)); // Orange
                ColorimeterModelText.Text = "Connect your colorimeter and click Refresh";
                StartButton.IsEnabled = false;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = "Searching...";
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 0));
            ColorimeterModelText.Text = "";
            StartButton.IsEnabled = false;

            if (_colorimeterService != null)
            {
                await _colorimeterService.InitializeAsync();
                UpdateColorimeterStatus();
            }
            else
            {
                await InitializeColorimeterAsync();
            }
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            // Get selected monitor
            if (MonitorComboBox.SelectedItem is MonitorComboItem selectedItem)
            {
                SelectedMonitor = selectedItem.Monitor;
            }
            else
            {
                // Fallback: try to get first monitor if selection failed
                Log.Info($"MonitorComboBox.SelectedItem type: {MonitorComboBox.SelectedItem?.GetType().Name ?? "null"}");
                if (_monitors.Count > 0)
                {
                    SelectedMonitor = _monitors[0];
                    Log.Info($"Falling back to first monitor: {SelectedMonitor.FriendlyName}");
                }
            }

            // Get selected target
            SelectedTarget = GetSelectedTarget();

            // Get selected preset
            SelectedPreset = GetSelectedPreset();

            // Get selected display type and apply to colorimeter service
            SelectedDisplayType = GetSelectedDisplayType();
            _colorimeterService?.SetDisplayType(SelectedDisplayType);

            Log.Info($"Start_Click: Monitor={SelectedMonitor?.FriendlyName ?? "null"}, Target={SelectedTarget?.Name ?? "null"}, DisplayType={SelectedDisplayType}, Colorimeter={(_colorimeterService != null ? "present" : "null")}");

            DialogResult = true;
            Close();
        }

        private CalibrationTarget GetSelectedTarget()
        {
            if (Target709G22.IsChecked == true)
                return StandardTargets.SrgbGamma22;
            if (Target709G24.IsChecked == true)
                return StandardTargets.Rec709Gamma24;
            if (TargetP3D65.IsChecked == true)
                return StandardTargets.P3D65Gamma22;
            if (Target2020SDR.IsChecked == true)
                return StandardTargets.Rec2020Gamma24;
            if (Target2020PQ.IsChecked == true)
                return StandardTargets.Rec2020Pq;

            return StandardTargets.SrgbGamma22;
        }

        private CalibrationPreset GetSelectedPreset()
        {
            if (PresetQuick.IsChecked == true)
                return CalibrationPreset.Quick;
            if (PresetThorough.IsChecked == true)
                return CalibrationPreset.Thorough;

            return CalibrationPreset.Standard;
        }

        private DisplayType GetSelectedDisplayType()
        {
            if (DisplayTypeOled.IsChecked == true)
                return DisplayType.Oled;
            if (DisplayTypeWideGamut.IsChecked == true)
                return DisplayType.LcdWideGamut;
            if (DisplayTypeCcfl.IsChecked == true)
                return DisplayType.LcdCcfl;

            return DisplayType.LcdLed; // Default
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    /// <summary>
    /// Helper class for monitor combo box items.
    /// </summary>
    internal class MonitorComboItem
    {
        public MonitorInfo Monitor { get; }

        public MonitorComboItem(MonitorInfo monitor)
        {
            Monitor = monitor;
        }

        public override string ToString()
        {
            return Monitor.FriendlyName ?? Monitor.DeviceName ?? "Unknown Monitor";
        }
    }
}
