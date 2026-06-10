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

        private readonly SettingsManager? _settingsManager;

        public CalibrationSetupWindow(List<MonitorInfo> monitors, SettingsManager? settingsManager = null)
        {
            InitializeComponent();
            _monitors = monitors;
            _settingsManager = settingsManager;

            PopulateCorrectionFiles();

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
            MonitorComboBox.SelectionChanged += (_, _) =>
            {
                IdentifySelectedMonitor();
                LoadCorrectionForSelectedMonitor();
            };
            LoadCorrectionForSelectedMonitor();

            // Initialize colorimeter asynchronously
            Loaded += async (s, e) =>
            {
                IdentifySelectedMonitor();
                await InitializeColorimeterAsync();
            };
        }

        /// <summary>
        /// User's drop-folder for meter correction files; created so there's an obvious
        /// place to put downloaded .ccss/.ccmx files.
        /// </summary>
        private static string CorrectionsFolder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HDRGammaController", "corrections");

        /// <summary>
        /// Fills the meter-correction combo with every .ccss/.ccmx found in the usual
        /// places: our corrections folder, Argyll's per-user instrument data (where
        /// oeminst installs converted X-Rite EDRs), and the app directory.
        /// </summary>
        private void PopulateCorrectionFiles()
        {
            CorrectionCombo.Items.Clear();
            CorrectionCombo.Items.Add(new ComboBoxItem
            {
                Content = "(Built-in for display type)",
                Tag = null,
            });

            var dirs = new[]
            {
                CorrectionsFolder,
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArgyllCMS"),
                AppContext.BaseDirectory,
            };
            try { System.IO.Directory.CreateDirectory(CorrectionsFolder); } catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                try
                {
                    if (!System.IO.Directory.Exists(dir)) continue;
                    foreach (var pattern in new[] { "*.ccss", "*.ccmx" })
                        foreach (var file in System.IO.Directory.GetFiles(dir, pattern, System.IO.SearchOption.AllDirectories))
                            if (seen.Add(file))
                                CorrectionCombo.Items.Add(new ComboBoxItem
                                {
                                    Content = System.IO.Path.GetFileName(file),
                                    ToolTip = file,
                                    Tag = file,
                                });
                }
                catch { /* unreadable dir — skip */ }
            }
            CorrectionCombo.SelectedIndex = 0;
        }

        /// <summary>
        /// Opens the in-app browser for the DisplayCAL community corrections database,
        /// pre-seeded with the selected monitor's name; the downloaded file is selected
        /// in the picker (and persisted per monitor on Start).
        /// </summary>
        private void FindCorrection_Click(object sender, RoutedEventArgs e)
        {
            var monitor = (MonitorComboBox.SelectedItem as MonitorComboItem)?.Monitor;
            var browser = new CcssBrowserWindow(monitor?.FriendlyName ?? "", CorrectionsFolder)
            {
                Owner = this,
            };
            if (browser.ShowDialog() == true && browser.SavedPath != null)
                SelectCorrectionPath(browser.SavedPath, addIfMissing: true);
        }

        private void BrowseCorrection_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select meter correction file",
                Filter = "Colorimeter corrections (*.ccss;*.ccmx)|*.ccss;*.ccmx|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog() != true) return;
            SelectCorrectionPath(dialog.FileName, addIfMissing: true);
        }

        private void LoadCorrectionForSelectedMonitor()
        {
            var monitor = (MonitorComboBox.SelectedItem as MonitorComboItem)?.Monitor;
            var saved = monitor != null && _settingsManager != null
                ? _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath)?.MeterCorrectionPath
                : null;
            SelectCorrectionPath(saved, addIfMissing: !string.IsNullOrEmpty(saved));
        }

        private void SelectCorrectionPath(string? path, bool addIfMissing)
        {
            if (string.IsNullOrEmpty(path))
            {
                CorrectionCombo.SelectedIndex = 0;
                return;
            }
            foreach (ComboBoxItem item in CorrectionCombo.Items)
            {
                if (string.Equals(item.Tag as string, path, StringComparison.OrdinalIgnoreCase))
                {
                    CorrectionCombo.SelectedItem = item;
                    return;
                }
            }
            if (addIfMissing && System.IO.File.Exists(path))
            {
                var item = new ComboBoxItem { Content = System.IO.Path.GetFileName(path), ToolTip = path, Tag = path };
                CorrectionCombo.Items.Add(item);
                CorrectionCombo.SelectedItem = item;
            }
            else
            {
                CorrectionCombo.SelectedIndex = 0;
            }
        }

        private string? SelectedCorrectionPath =>
            (CorrectionCombo.SelectedItem as ComboBoxItem)?.Tag as string;

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
            SetTargetReachable(TargetHdrPq, StandardTargets.Rec709Pq, gamut, true, hdr);
            SetTargetReachable(Target2020PQ, StandardTargets.Rec2020Pq, gamut, true, hdr);

            // If the currently-checked target just got disabled, fall back to the first target
            // that is still enabled (sRGB in SDR mode, the HDR desktop target in HDR mode).
            var radios = new[] { Target709G22, Target709G24, TargetP3D65, Target2020SDR, TargetHdrPq, Target2020PQ };
            foreach (var rb in radios)
            {
                if (rb.IsChecked != true || rb.IsEnabled) continue;
                foreach (var candidate in radios)
                    if (candidate.IsEnabled) { candidate.IsChecked = true; break; }
                break;
            }
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

        /// <summary>
        /// Whether the display can reasonably reach the target, using the SAME drive-value
        /// metric as the apply-time gamut guard (so setup and apply agree). We build the
        /// display's RGB→XYZ from its EDID primaries, derive the correction matrix toward the
        /// target, and ask how hard it drives the channels for the target's primaries/white.
        /// A small overshoot (e.g. a 98%-P3 panel reaching for P3's green corner) is fine; only
        /// a genuinely wider gamut (Rec.2020 on a P3 panel) blows past the limit.
        /// </summary>
        private static bool TargetFitsGamut(CalibrationTarget t, EdidColorInfo g)
        {
            try
            {
                var displayRgbToXyz = ColorMath.CalculateRgbToXyzMatrix(
                    new Chromaticity(g.RedX, g.RedY), new Chromaticity(g.GreenX, g.GreenY),
                    new Chromaticity(g.BlueX, g.BlueY), new Chromaticity(g.WhiteX, g.WhiteY));
                // Same ABSOLUTE construction as Mhc2ProfileBuilder.BuildGamutMatrix, so setup
                // and apply measure the same drive values.
                var matrix = ColorMath.MultiplyMatrices(ColorMath.Invert3x3(displayRgbToXyz), t.RgbToXyzMatrix);

                double max = 0;
                (double, double, double)[] contents = { (1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 1) };
                foreach (var (a, b, c) in contents)
                    for (int r = 0; r < 3; r++)
                        max = Math.Max(max, matrix[r, 0] * a + matrix[r, 1] * b + matrix[r, 2] * c);
                // Matches the installer's gamut guard threshold so setup and apply never disagree.
                return max <= 1.3;
            }
            catch
            {
                return true; // if the EDID is unusable, don't block — the apply guard still protects.
            }
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

            // Meter spectral correction: applied to this session and remembered per monitor.
            string? correction = SelectedCorrectionPath;
            _colorimeterService?.SetCorrectionFile(correction);
            if (SelectedMonitor != null)
                _settingsManager?.SetMeterCorrection(SelectedMonitor.MonitorDevicePath, correction);

            Log.Info($"Start_Click: Monitor={SelectedMonitor?.FriendlyName ?? "null"}, Target={SelectedTarget?.Name ?? "null"}, DisplayType={SelectedDisplayType}, " +
                     $"Correction={(correction != null ? System.IO.Path.GetFileName(correction) : "built-in")}, Colorimeter={(_colorimeterService != null ? "present" : "null")}");

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
            if (TargetHdrPq.IsChecked == true)
                return StandardTargets.Rec709Pq;
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
