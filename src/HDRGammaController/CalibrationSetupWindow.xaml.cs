using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
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

            // Initialize colorimeter asynchronously
            Loaded += async (s, e) => await InitializeColorimeterAsync();
        }

        private async Task InitializeColorimeterAsync()
        {
            StatusText.Text = "Searching for colorimeter...";
            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 165, 0)); // Orange

            try
            {
                // Find ArgyllCMS bin path
                string? argyllBinPath = FindArgyllBinPath();
                if (string.IsNullOrEmpty(argyllBinPath))
                {
                    StatusText.Text = "ArgyllCMS not found";
                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    ColorimeterModelText.Text = "Please install ArgyllCMS or configure its path";
                    StartButton.IsEnabled = false;
                    return;
                }

                _colorimeterService = new ColorimeterService(argyllBinPath);
                await _colorimeterService.InitializeAsync();

                UpdateColorimeterStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                StartButton.IsEnabled = false;
            }
        }

        private static string? FindArgyllBinPath()
        {
            // Search in common locations
            var searchPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HDRGammaController", "Argyll", "bin"),
                @"C:\Program Files\Argyll\bin",
                @"C:\Program Files (x86)\Argyll\bin",
                @"C:\Argyll\bin",
            };

            foreach (var path in searchPaths)
            {
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "spotread.exe")))
                {
                    return path;
                }
            }

            // Check DisplayCAL's bundled ArgyllCMS
            try
            {
                string displayCalDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "DisplayCAL");

                if (Directory.Exists(displayCalDir))
                {
                    foreach (var argyllDir in Directory.GetDirectories(displayCalDir, "Argyll_*"))
                    {
                        string binPath = Path.Combine(argyllDir, "bin");
                        if (File.Exists(Path.Combine(binPath, "spotread.exe")))
                        {
                            return binPath;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors when searching
            }

            // Check local app data HDRGammaController/Argyll
            try
            {
                string localArgyllDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HDRGammaController", "Argyll");

                if (Directory.Exists(localArgyllDir))
                {
                    foreach (var versionDir in Directory.GetDirectories(localArgyllDir, "Argyll_*"))
                    {
                        string binPath = Path.Combine(versionDir, "bin");
                        if (File.Exists(Path.Combine(binPath, "spotread.exe")))
                        {
                            return binPath;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors when searching
            }

            return null;
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

            // Get selected target
            SelectedTarget = GetSelectedTarget();

            // Get selected preset
            SelectedPreset = GetSelectedPreset();

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
