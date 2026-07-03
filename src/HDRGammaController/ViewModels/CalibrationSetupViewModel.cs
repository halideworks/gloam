using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using static HDRGammaController.Core.Calibration.PatchSetGenerator;

namespace HDRGammaController.ViewModels
{
    /// <summary>One selectable calibration target with gamut/HDR availability state.</summary>
    public class TargetOption : ObservableObject
    {
        public string Label { get; }
        public CalibrationTarget Target { get; }
        public bool RequiresHdr { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private string? _disabledReason;
        public string? DisabledReason
        {
            get => _disabledReason;
            set => SetProperty(ref _disabledReason, value);
        }

        public TargetOption(string label, CalibrationTarget target, bool requiresHdr)
        {
            Label = label;
            Target = target;
            RequiresHdr = requiresHdr;
        }
    }

    /// <summary>A meter correction file choice; null Path means the built-in correction.</summary>
    public class CorrectionChoice
    {
        public string Label { get; }
        public string? Path { get; }

        public CorrectionChoice(string label, string? path)
        {
            Label = label;
            Path = path;
        }

        // Templated ComboBoxes render the closed-state selection box via ToString().
        public override string ToString() => Label;
    }

    public class CalibrationPreflightItem
    {
        public string Severity { get; }
        public string Message { get; }
        public Brush Brush { get; }

        public CalibrationPreflightItem(string severity, string message, Brush brush)
        {
            Severity = severity;
            Message = message;
            Brush = brush;
        }
    }

    public class CalibrationSetupViewModel : ObservableObject
    {
        private static readonly Brush SuccessBrush = CreateFrozen(Color.FromRgb(0x22, 0xC5, 0x5E));
        private static readonly Brush WarningBrush = CreateFrozen(Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly Brush ErrorBrush = CreateFrozen(Color.FromRgb(0xEF, 0x44, 0x44));

        private static Brush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private readonly List<MonitorInfo> _monitors;
        private readonly SettingsManager? _settingsManager;
        private bool _loadingPrefs;
        private bool _colorimeterReady;
        private bool _updatingTargetSelection;

        public ObservableCollection<MonitorChoice> Monitors { get; }
        public ObservableCollection<TargetOption> Targets { get; }
        public ObservableCollection<CorrectionChoice> Corrections { get; } = new();
        public ObservableCollection<CalibrationPreflightItem> PreflightItems { get; } = new();

        public ICommand IdentifyCommand { get; }
        public ICommand RefreshColorimeterCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand CancelCommand { get; }

        /// <summary>Raised when the dialog should close, with the DialogResult to set.</summary>
        public event Action<bool>? CloseRequested;

        /// <summary>
        /// Set by the view: shows the Argyll download dialog for the given reason and
        /// returns whether the download succeeded.
        /// </summary>
        public Func<string, bool>? OfferArgyllDownload { get; set; }

        public ColorimeterService? ColorimeterService { get; private set; }

        // Results read by the caller after the dialog closes with true.
        public CalibrationTarget? ResultTarget { get; private set; }
        public CalibrationPreset ResultPreset { get; private set; }
        public MonitorInfo? ResultMonitor { get; private set; }
        public DisplayType ResultDisplayType { get; private set; }

        /// <summary>
        /// User's drop-folder for meter correction files; created so there's an obvious
        /// place to put downloaded .ccss/.ccmx files.
        /// </summary>
        public static string CorrectionsFolder => System.IO.Path.Combine(
            AppPaths.DataDir, "corrections");

        public CalibrationSetupViewModel(
            List<MonitorInfo> monitors,
            SettingsManager? settingsManager,
            string? preferredMonitorDevicePath = null,
            ColorimeterService? reusableColorimeterService = null)
        {
            _monitors = monitors;
            _settingsManager = settingsManager;
            ColorimeterService = reusableColorimeterService;

            Targets = new ObservableCollection<TargetOption>
            {
                new("Rec.709 Gamma 2.2 (sRGB)", StandardTargets.SrgbGamma22, requiresHdr: false) { IsSelected = true },
                new("Rec.709 Gamma 2.4 (BT.1886)", StandardTargets.Rec709Gamma24, requiresHdr: false),
                new("DCI-P3 D65", StandardTargets.P3D65Gamma22, requiresHdr: false),
                new("BT.2020 SDR (Gamma 2.4)", StandardTargets.Rec2020Gamma24, requiresHdr: false),
                new("HDR Desktop PQ (sRGB gamut) - recommended for HDR", StandardTargets.Rec709Pq, requiresHdr: true),
            };
            foreach (var target in Targets)
            {
                target.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TargetOption.IsSelected))
                    {
                        if (!_updatingTargetSelection && target.IsSelected)
                            SelectTarget(target);
                        RefreshPreflight();
                    }
                };
            }

            PopulateCorrectionFiles();

            Monitors = new ObservableCollection<MonitorChoice>(monitors.Select(m => new MonitorChoice(m)));
            _selectedMonitor = Monitors.FirstOrDefault(m =>
                !string.IsNullOrWhiteSpace(preferredMonitorDevicePath) &&
                string.Equals(m.Model.MonitorDevicePath, preferredMonitorDevicePath, StringComparison.OrdinalIgnoreCase))
                ?? Monitors.FirstOrDefault();
            if (_selectedMonitor != null)
            {
                LoadPrefsForSelectedMonitor();
            }

            IdentifyCommand = new RelayCommand(IdentifySelectedMonitor);
            RefreshColorimeterCommand = new AsyncRelayCommand(RefreshColorimeterAsync);
            StartCommand = new RelayCommand(Start);
            CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
            RefreshPreflight();
        }

        /// <summary>Identify flash + colorimeter detection; called once from the view's Loaded.</summary>
        public async Task OnLoadedAsync()
        {
            IdentifySelectedMonitor();
            if (ColorimeterService != null)
                UpdateColorimeterStatus();
            else
                await InitializeColorimeterAsync();
        }

        private MonitorChoice? _selectedMonitor;
        public MonitorChoice? SelectedMonitor
        {
            get => _selectedMonitor;
            set
            {
                if (value == null || ReferenceEquals(value, _selectedMonitor)) return;
                _selectedMonitor = value;
                OnPropertyChanged();

                // Flash a big "this is the display" overlay on the chosen monitor whenever the
                // selection changes, so it's unambiguous which physical screen will be
                // calibrated - critical when two identical displays are attached.
                IdentifySelectedMonitor();
                LoadPrefsForSelectedMonitor();
            }
        }

        #region Display type

        private DisplayType _displayType = DisplayType.LcdLed;

        public bool IsLcdLed { get => _displayType == DisplayType.LcdLed; set { if (value) SetDisplayType(DisplayType.LcdLed); } }
        public bool IsOled { get => _displayType == DisplayType.Oled; set { if (value) SetDisplayType(DisplayType.Oled); } }
        public bool IsLcdWideGamut { get => _displayType == DisplayType.LcdWideGamut; set { if (value) SetDisplayType(DisplayType.LcdWideGamut); } }
        public bool IsLcdCcfl { get => _displayType == DisplayType.LcdCcfl; set { if (value) SetDisplayType(DisplayType.LcdCcfl); } }

        public bool IsOledHintVisible => _displayType == DisplayType.Oled;

        private DisplayType? _detectedDisplayType;

        /// <summary>What the panel looks like from EDID; drives the DETECTED badges.</summary>
        public DisplayType? DetectedDisplayType
        {
            get => _detectedDisplayType;
            private set
            {
                if (SetProperty(ref _detectedDisplayType, value))
                {
                    OnPropertyChanged(nameof(IsOledDetected));
                    OnPropertyChanged(nameof(IsWideGamutDetected));
                }
            }
        }

        public bool IsOledDetected => _detectedDisplayType == DisplayType.Oled;
        public bool IsWideGamutDetected => _detectedDisplayType == DisplayType.LcdWideGamut;

        /// <summary>
        /// Best-effort panel-type detection from what the monitor reports about itself.
        /// OLED: the EDID name says so, OR the panel is wide gamut AND reports a
        /// true-black HDR floor (emissive; LCDs report a real backlight floor - measured
        /// examples: MAG 271QPX QD-OLED 0.000 nits vs M27Q P IPS 0.384 nits). Wide
        /// gamut: EDID primaries span well past sRGB (P3-class is ~1.35x the sRGB
        /// triangle). SDR-mode OLED TVs are undetectable: they report Rec.709 primaries
        /// and no HDR metadata until switched to HDR.
        /// </summary>
        private static DisplayType? DetectPanelType(MonitorInfo monitor)
        {
            DisplayType? result = null;

            double areaRatio = 0;
            var g = monitor.EdidColor;
            if (g != null)
            {
                const double srgbArea = 0.11205; // sRGB primaries triangle in xy
                areaRatio = TriangleArea(g.RedX, g.RedY, g.GreenX, g.GreenY, g.BlueX, g.BlueY) / srgbArea;
            }

            if (monitor.FriendlyName?.IndexOf("OLED", StringComparison.OrdinalIgnoreCase) >= 0)
                result = DisplayType.Oled;
            else if (areaRatio >= 1.2 && monitor.HdrPeakNits > 0 && monitor.HdrMinNits <= 0.05)
                result = DisplayType.Oled; // wide gamut + true black = emissive
            else if (areaRatio >= 1.2)
                result = DisplayType.LcdWideGamut;

            Log.Info($"CalibrationSetup: panel detect '{monitor.FriendlyName}': gamut {areaRatio:F2}x sRGB, " +
                     $"HDR range {monitor.HdrMinNits:F3}-{monitor.HdrPeakNits:F0} nits -> {(result?.ToString() ?? "no detection")}");
            return result;
        }

        private static double TriangleArea(double x1, double y1, double x2, double y2, double x3, double y3)
            => Math.Abs(x1 * (y2 - y3) + x2 * (y3 - y1) + x3 * (y1 - y2)) / 2.0;

        private void SetDisplayType(DisplayType type)
        {
            if (_displayType == type) return;
            _displayType = type;
            OnPropertyChanged(nameof(IsLcdLed));
            OnPropertyChanged(nameof(IsOled));
            OnPropertyChanged(nameof(IsLcdWideGamut));
            OnPropertyChanged(nameof(IsLcdCcfl));
            OnPropertyChanged(nameof(IsOledHintVisible));

            // OLED panels in HDR overshoot with full gamut correction (their processing is
            // nonlinear); suggest white-point-only when the user picks OLED. Suggestion only:
            // the user can still untick it.
            if (type == DisplayType.Oled && !_loadingPrefs)
            {
                WhitePointOnly = true;
            }
            RefreshPreflight();
        }

        #endregion

        #region Preset

        private CalibrationPreset _preset = CalibrationPreset.Standard;

        public bool IsPresetAdaptive { get => _preset == CalibrationPreset.Adaptive; set { if (value) SetPreset(CalibrationPreset.Adaptive); } }
        public bool IsPresetQuick { get => _preset == CalibrationPreset.Quick; set { if (value) SetPreset(CalibrationPreset.Quick); } }
        public bool IsPresetStandard { get => _preset == CalibrationPreset.Standard; set { if (value) SetPreset(CalibrationPreset.Standard); } }
        public bool IsPresetThorough { get => _preset == CalibrationPreset.Thorough; set { if (value) SetPreset(CalibrationPreset.Thorough); } }

        // Adaptive measures a small seed, then spends patches only where the fitted model
        // is least certain, so it reaches the same accuracy as a fixed grid in far fewer
        // patches. The time is a budget-based UPPER bound — it usually stops early.
        public string AdaptivePresetLabel => $"Adaptive (recommended)";
        public string AdaptivePresetDetail =>
            $"up to {FormatEstimatedTime(PatchSetGenerator.GetApproximatePatchCount(CalibrationPreset.Adaptive))}, usually finishes early - excellent accuracy";
        public string QuickPresetLabel => PresetLabel("Quick", CalibrationPreset.Quick);
        public string QuickPresetDetail => PresetDetail(CalibrationPreset.Quick, "Good accuracy");
        public string StandardPresetLabel => PresetLabel("Standard", CalibrationPreset.Standard);
        public string StandardPresetDetail => PresetDetail(CalibrationPreset.Standard, "Very good accuracy");
        public string ThoroughPresetLabel => PresetLabel("Thorough", CalibrationPreset.Thorough);
        public string ThoroughPresetDetail => PresetDetail(CalibrationPreset.Thorough, "Excellent accuracy");

        private void SetPreset(CalibrationPreset preset)
        {
            if (_preset == preset) return;
            _preset = preset;
            OnPropertyChanged(nameof(IsPresetAdaptive));
            OnPropertyChanged(nameof(IsPresetQuick));
            OnPropertyChanged(nameof(IsPresetStandard));
            OnPropertyChanged(nameof(IsPresetThorough));
        }

        private string PresetLabel(string name, CalibrationPreset preset)
            => $"{name} ({PatchCountFor(preset)} patches)";

        private string PresetDetail(CalibrationPreset preset, string quality)
            => $"{FormatEstimatedTime(PatchCountFor(preset))} - {quality}";

        private int PatchCountFor(CalibrationPreset preset)
        {
            var target = Targets?.FirstOrDefault(t => t.IsSelected)?.Target ?? StandardTargets.SrgbGamma22;
            return PatchSetGenerator.GeneratePatchSet(target, preset).Count;
        }

        private static string FormatEstimatedTime(int patchCount)
        {
            var time = TimeSpan.FromSeconds(patchCount * 3);
            return time.TotalMinutes >= 60
                ? $"~{time.Hours}h {time.Minutes}m"
                : $"~{time.Minutes} minutes";
        }

        private void RefreshPresetLabels()
        {
            OnPropertyChanged(nameof(AdaptivePresetLabel));
            OnPropertyChanged(nameof(AdaptivePresetDetail));
            OnPropertyChanged(nameof(QuickPresetLabel));
            OnPropertyChanged(nameof(QuickPresetDetail));
            OnPropertyChanged(nameof(StandardPresetLabel));
            OnPropertyChanged(nameof(StandardPresetDetail));
            OnPropertyChanged(nameof(ThoroughPresetLabel));
            OnPropertyChanged(nameof(ThoroughPresetDetail));
        }

        #endregion

        private bool _whitePointOnly;
        public bool WhitePointOnly
        {
            get => _whitePointOnly;
            set
            {
                if (SetProperty(ref _whitePointOnly, value))
                    RefreshPreflight();
            }
        }

        private CorrectionChoice? _selectedCorrection;
        public CorrectionChoice? SelectedCorrection
        {
            get => _selectedCorrection;
            set
            {
                if (SetProperty(ref _selectedCorrection, value))
                    RefreshPreflight();
            }
        }

        #region Colorimeter status

        private string _statusText = "Checking...";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private string _statusDetailText = "";
        public string StatusDetailText
        {
            get => _statusDetailText;
            set => SetProperty(ref _statusDetailText, value);
        }

        private Brush _statusBrush = WarningBrush;
        public Brush StatusBrush
        {
            get => _statusBrush;
            set => SetProperty(ref _statusBrush, value);
        }

        private bool _canStart;
        public bool CanStart
        {
            get => _canStart;
            set => SetProperty(ref _canStart, value);
        }

        private string _startBlockReason = "Waiting for colorimeter.";
        public string StartBlockReason
        {
            get => _startBlockReason;
            set => SetProperty(ref _startBlockReason, value);
        }

        public bool HasPreflightItems => PreflightItems.Count > 0;

        #endregion

        /// <summary>
        /// Fills the meter-correction list with every .ccss/.ccmx found in the usual
        /// places: our corrections folder, Argyll's per-user instrument data (where
        /// oeminst installs converted X-Rite EDRs), and the app directory.
        /// </summary>
        private void PopulateCorrectionFiles()
        {
            Corrections.Clear();
            Corrections.Add(new CorrectionChoice("(Built-in for display type)", null));

            var dirs = new[]
            {
                CorrectionsFolder,
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArgyllCMS"),
                AppContext.BaseDirectory,
            };
            try { Directory.CreateDirectory(CorrectionsFolder); } catch { }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var pattern in new[] { "*.ccss", "*.ccmx" })
                        foreach (var file in Directory.GetFiles(dir, pattern, SearchOption.AllDirectories))
                            if (seen.Add(file))
                                Corrections.Add(new CorrectionChoice(System.IO.Path.GetFileName(file), file));
                }
                catch { /* unreadable dir - skip */ }
            }
            _selectedCorrection = Corrections[0];
        }

        /// <summary>
        /// Selects the given correction path in the list, optionally adding it when it's
        /// a file we haven't discovered (fresh download or manual browse).
        /// </summary>
        public void SelectCorrectionPath(string? path, bool addIfMissing)
        {
            if (string.IsNullOrEmpty(path))
            {
                SelectedCorrection = Corrections[0];
                return;
            }
            var existing = Corrections.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                SelectedCorrection = existing;
            }
            else if (addIfMissing && File.Exists(path))
            {
                var choice = new CorrectionChoice(System.IO.Path.GetFileName(path), path);
                Corrections.Add(choice);
                SelectedCorrection = choice;
            }
            else
            {
                SelectedCorrection = Corrections[0];
            }
        }

        /// <summary>
        /// Restores the monitor's last calibration-setup choices: meter correction file,
        /// display type, and white-point-only scope. Users shouldn't have to re-pick OLED
        /// and the CCSS for the same panel every session.
        /// </summary>
        private void LoadPrefsForSelectedMonitor()
        {
            var monitor = _selectedMonitor?.Model;
            if (monitor != null)
            {
                UpdateTargetAvailability(monitor);
            }

            var prefs = monitor != null && _settingsManager != null
                ? _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath ?? "")
                : null;

            SelectCorrectionPath(prefs?.MeterCorrectionPath, addIfMissing: !string.IsNullOrEmpty(prefs?.MeterCorrectionPath));
            SelectSavedTarget(prefs?.CalibTargetName);

            DetectedDisplayType = monitor != null ? DetectPanelType(monitor) : null;

            _loadingPrefs = true;
            try
            {
                if (Enum.TryParse(prefs?.CalibDisplayType, out DisplayType savedType))
                {
                    // A saved explicit choice wins over detection.
                    SetDisplayType(savedType);
                }
                else if (DetectedDisplayType is DisplayType detected)
                {
                    SetDisplayType(detected);
                    // First time on a detected OLED: apply the white-point-only
                    // suggestion the manual OLED pick would have made.
                    if (detected == DisplayType.Oled)
                    {
                        WhitePointOnly = true;
                    }
                }

                // A saved explicit white-point-only choice wins over the OLED suggestion.
                if (prefs?.CalibWhitePointOnly is bool savedWpOnly)
                {
                    WhitePointOnly = savedWpOnly;
                }

                if (Enum.TryParse(prefs?.CalibPreset, out CalibrationPreset savedPreset))
                {
                    SetPreset(savedPreset);
                }
            }
            finally
            {
                _loadingPrefs = false;
            }
        }

        private void SelectSavedTarget(string? targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName)) return;

            var target = Targets.FirstOrDefault(t =>
                t.IsEnabled &&
                string.Equals(t.Target.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (target != null)
                SelectTarget(target);
        }

        private void IdentifySelectedMonitor()
        {
            if (_selectedMonitor == null) return;
            DisplayIdentify.Flash(_selectedMonitor.Model, Monitors.IndexOf(_selectedMonitor) + 1);
            UpdateTargetAvailability(_selectedMonitor.Model);
        }

        /// <summary>
        /// Greys out calibration targets the selected display can't reach (per its EDID gamut),
        /// and HDR-only targets when the display isn't in HDR - so the user only picks settings
        /// that will actually apply, before spending a calibration on them.
        /// </summary>
        private void UpdateTargetAvailability(MonitorInfo monitor)
        {
            var gamut = monitor.EdidColor;
            bool hdr = monitor.IsHdrActive;

            foreach (var option in Targets)
            {
                string? reason = null;
                if (option.RequiresHdr && !hdr)
                    reason = "Requires the display to be in HDR mode.";
                else if (!option.RequiresHdr && hdr)
                    reason = "This is an SDR target; switch the display to SDR to use it.";
                else if (gamut != null && !GamutReachability.TargetFitsEdidGamut(option.Target, gamut))
                    reason = "Exceeds this display's gamut - it can't reproduce these primaries.";

                option.IsEnabled = reason == null;
                option.DisabledReason = reason;
            }

            ResolveTargetSelection(hdr);
            RefreshPreflight();
        }

        private void ResolveTargetSelection(bool hdr)
        {
            var selected = Targets.Where(t => t.IsSelected).ToList();
            if (selected.Count == 1 && selected[0].IsEnabled) return;

            var fallback = Targets.FirstOrDefault(t => t.IsEnabled && t.RequiresHdr == hdr)
                           ?? Targets.FirstOrDefault(t => t.IsEnabled);
            SelectTarget(fallback);
        }

        private void SelectTarget(TargetOption? selected)
        {
            _updatingTargetSelection = true;
            try
            {
                foreach (var option in Targets)
                    option.IsSelected = ReferenceEquals(option, selected);
            }
            finally
            {
                _updatingTargetSelection = false;
            }
            RefreshPresetLabels();
        }

        public static IReadOnlyList<(string Severity, string Message)> BuildPreflightMessages(
            MonitorInfo? monitor,
            CalibrationTarget? target,
            TargetOption? selectedOption,
            DisplayType displayType,
            DisplayType? detectedDisplayType,
            CorrectionChoice? correction,
            bool whitePointOnly,
            MonitorProfileData? monitorProfile,
            bool? nightLightActive = null,
            bool? sdrAcmActive = null)
        {
            var items = new List<(string Severity, string Message)>();

            if (monitor == null)
            {
                items.Add(("ERROR", "Select a display before starting calibration."));
                return items;
            }

            // Windows Night Light warms the whole output at the compositor: every reading
            // through it is corrupted. Detection is heuristic (undocumented registry blob),
            // so only a confident "on" warns — unknown stays silent.
            if (nightLightActive == true)
                items.Add(("WARN", CalibrationInstallPreflight.NightLightWarning));

            // SDR Auto Color Management re-renders SDR through a color pipeline; measuring
            // through it unknowingly gives the characterization the wrong basis. Only
            // relevant to SDR-mode calibrations (in HDR the pipeline is expected).
            if (sdrAcmActive == true && !monitor.IsHdrActive)
                items.Add(("WARN",
                    "Windows Auto Color Management (ACM) is active on this display. SDR is being " +
                    "re-rendered through a color pipeline, so measurements will include ACM's " +
                    "correction — disable ACM in Windows display settings to calibrate the native panel."));

            if (selectedOption != null && !selectedOption.IsEnabled)
                items.Add(("ERROR", selectedOption.DisabledReason ?? "The selected target is not available for this display state."));

            if (monitor.IsHdrActive)
            {
                if (target != null && target.TransferFunction != TransferFunctionType.Pq)
                    items.Add(("ERROR", "Windows HDR is active but the selected target is SDR. Switch Windows HDR off or choose an HDR target."));
                if (monitor.SdrWhiteLevel < 80 || monitor.SdrWhiteLevel > 500)
                    items.Add(("WARN", $"Windows SDR white is {monitor.SdrWhiteLevel:F0} nits. Confirm this is intentional before measuring HDR desktop behavior."));
                if (target != null && target.TransferFunction == TransferFunctionType.Pq)
                    AddHdrLuminanceWarnings(items, monitor, target);
            }
            else
            {
                if (target != null && target.TransferFunction == TransferFunctionType.Pq)
                    items.Add(("ERROR", "The selected HDR target requires Windows HDR to be active on this display."));
                else if (monitor.IsHdrCapable)
                    items.Add(("WARN", "This HDR-capable display is currently in SDR mode; HDR targets are unavailable until Windows HDR is enabled."));
            }

            if (detectedDisplayType is { } detected && detected != displayType)
                items.Add(("WARN", $"Panel detection suggests {DisplayTypeLabel(detected)}, but {DisplayTypeLabel(displayType)} is selected."));

            bool correctionRecommended = displayType is DisplayType.Oled or DisplayType.LcdWideGamut ||
                                         detectedDisplayType is DisplayType.Oled or DisplayType.LcdWideGamut;
            if (correctionRecommended && string.IsNullOrEmpty(correction?.Path))
                items.Add(("WARN", "Use a panel-matched CCSS/CCMX meter correction for OLED and wide-gamut displays."));

            if (!string.IsNullOrEmpty(correction?.Path) &&
                ValidateCorrectionChoice(correction.Path) is { } correctionError)
            {
                items.Add(("ERROR", correctionError));
            }

            if (displayType == DisplayType.Oled && !whitePointOnly && monitor.IsHdrActive)
                items.Add(("WARN", "OLED HDR measurements usually behave better with white-point-only correction unless you have verified full gamut correction on this panel."));

            if (displayType == DisplayType.Oled || monitor.IsHdrActive)
                items.Add(("INFO", "Warm the display for at least 30 minutes and avoid panel compensation cycles during measurement."));

            if (!string.IsNullOrEmpty(monitorProfile?.Mhc2ProfileName))
                items.Add(("INFO", $"Existing Gloam profile '{monitorProfile.Mhc2ProfileName}' will be bypassed while measuring native response."));

            return items;
        }

        private static string? ValidateCorrectionChoice(string path)
        {
            if (!File.Exists(path))
                return $"The selected meter correction file no longer exists: {System.IO.Path.GetFileName(path)}.";

            string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (ext is not ("ccss" or "ccmx"))
                return "Meter correction files must be .ccss spectral samples or .ccmx correction matrices.";

            try
            {
                var result = CgatsValidator.Validate(File.ReadAllText(path), ext);
                return result.IsValid
                    ? null
                    : $"The selected meter correction file is not valid ({result.Error}).";
            }
            catch (Exception ex)
            {
                return $"The selected meter correction file could not be read ({ex.Message}).";
            }
        }

        private static void AddHdrLuminanceWarnings(
            List<(string Severity, string Message)> items,
            MonitorInfo monitor,
            CalibrationTarget target)
        {
            if (monitor.HdrPeakNits <= 0)
            {
                items.Add(("WARN", "HDR peak luminance metadata is unavailable. Verify Windows HDR is on and the selected display is the one being measured."));
                return;
            }

            if (monitor.HdrMinNits < 0 || (monitor.HdrMinNits > 0 && monitor.HdrMinNits >= monitor.HdrPeakNits))
            {
                items.Add(("WARN", $"HDR luminance metadata looks inconsistent ({monitor.HdrMinNits:F3}-{monitor.HdrPeakNits:F0} nits). Treat the resulting HDR tone report cautiously."));
            }

            if (monitor.HdrMaxFullFrameNits > 0 && monitor.HdrMaxFullFrameNits > monitor.HdrPeakNits * 1.05)
            {
                items.Add(("WARN", $"HDR full-frame metadata ({monitor.HdrMaxFullFrameNits:F0} nits) exceeds peak metadata ({monitor.HdrPeakNits:F0} nits). Confirm the display is reporting HDR data correctly."));
            }

            if (target.PeakLuminance is { } targetPeak && targetPeak > monitor.HdrPeakNits * 1.10)
            {
                items.Add(("WARN", $"The selected HDR target peaks at {targetPeak:F0} nits, above this display's reported {monitor.HdrPeakNits:F0}-nit peak. Highlights above the panel limit will be preserved or roll off rather than fully corrected."));
            }

            if (target.ReferenceWhite is { } referenceWhite && referenceWhite > monitor.HdrPeakNits * 0.90)
            {
                items.Add(("WARN", $"The selected HDR reference white ({referenceWhite:F0} nits) is close to this display's reported peak ({monitor.HdrPeakNits:F0} nits). Lower Windows SDR content brightness or use a brighter HDR display for reliable desktop calibration."));
            }
        }

        private void RefreshPreflight()
        {
            var monitor = _selectedMonitor?.Model;
            var selectedOption = Targets.FirstOrDefault(t => t.IsSelected);
            var profile = monitor != null ? _settingsManager?.GetMonitorProfile(monitor.MonitorDevicePath ?? "") : null;

            // Environment detections (registry / DisplayConfig reads). Each returns null
            // on any failure — "unknown" never blocks or warns.
            bool? nightLightActive = CalibrationInstallPreflight.DetectNightLightActive();
            bool? sdrAcmActive = monitor != null
                ? CalibrationInstallPreflight.DetectSdrAutoColorManagement(monitor.DeviceName, monitor.IsHdrActive)
                : null;

            PreflightItems.Clear();
            foreach (var item in BuildPreflightMessages(
                         monitor,
                         selectedOption?.Target,
                         selectedOption,
                         _displayType,
                         DetectedDisplayType,
                         SelectedCorrection,
                         WhitePointOnly,
                         profile,
                         nightLightActive,
                         sdrAcmActive))
            {
                PreflightItems.Add(new CalibrationPreflightItem(item.Severity, item.Message, BrushForSeverity(item.Severity)));
            }

            OnPropertyChanged(nameof(HasPreflightItems));
            RefreshCanStart();
        }

        private void RefreshCanStart()
        {
            var criticalError = CriticalStartError();
            CanStart = _colorimeterReady && criticalError == null;
            StartBlockReason = criticalError != null
                ? criticalError.Message
                : !_colorimeterReady
                    ? "Connect your colorimeter, then refresh."
                    : "Ready to start calibration.";
        }

        private CalibrationPreflightItem? CriticalStartError()
        {
            return PreflightItems.FirstOrDefault(i =>
                i.Severity == "ERROR" &&
                (i.Message.StartsWith("Select a display", StringComparison.OrdinalIgnoreCase) ||
                 i.Message.StartsWith("The selected target", StringComparison.OrdinalIgnoreCase) ||
                 i.Message.Contains("requires Windows HDR", StringComparison.OrdinalIgnoreCase) ||
                 i.Message.Contains("selected target is SDR", StringComparison.OrdinalIgnoreCase) ||
                 i.Message.Contains("correction", StringComparison.OrdinalIgnoreCase)));
        }

        private static Brush BrushForSeverity(string severity) => severity switch
        {
            "ERROR" => ErrorBrush,
            "WARN" => WarningBrush,
            _ => SuccessBrush
        };

        private static string DisplayTypeLabel(DisplayType type) => type switch
        {
            DisplayType.LcdLed => "LCD LED",
            DisplayType.Oled => "OLED",
            DisplayType.LcdWideGamut => "wide-gamut LCD",
            DisplayType.LcdCcfl => "CCFL LCD",
            _ => type.ToString()
        };

        #region Colorimeter init

        private async Task InitializeColorimeterAsync()
        {
            StatusText = "Finding ArgyllCMS...";
            StatusBrush = WarningBrush;
            StatusDetailText = "";

            try
            {
                string? argyllBinPath;

                // First check if we have our own downloaded version (preferred)
                if (ArgyllDownloader.IsInstalled())
                {
                    argyllBinPath = ArgyllDownloader.LocalArgyllBinDir;
                    Log.Info($"CalibrationSetupViewModel: Using our downloaded ArgyllCMS from {argyllBinPath}");
                }
                else
                {
                    // Check if there's any ArgyllCMS available (might be old version from DisplayCAL)
                    argyllBinPath = ArgyllPathFinder.FindArgyllBinPath();

                    if (string.IsNullOrEmpty(argyllBinPath))
                    {
                        // No ArgyllCMS found at all - offer to download
                        OfferDownload("ArgyllCMS (required for colorimeter calibration) was not found.");
                    }
                    else
                    {
                        // Found some ArgyllCMS, but it might be old - check the version
                        string versionInfo = ExtractVersionFromPath(argyllBinPath);
                        if (IsOldVersion(versionInfo))
                        {
                            Log.Info($"CalibrationSetupViewModel: Found old ArgyllCMS version: {versionInfo}");
                            OfferDownload(
                                $"Found ArgyllCMS {versionInfo}, but a newer version ({ArgyllDownloader.ArgyllVersion}) is recommended for better compatibility.");
                        }
                    }

                    // After potential download, prefer our version if now installed
                    argyllBinPath = ArgyllDownloader.IsInstalled()
                        ? ArgyllDownloader.LocalArgyllBinDir
                        : ArgyllPathFinder.FindArgyllBinPath();

                    if (string.IsNullOrEmpty(argyllBinPath))
                    {
                        StatusText = "ArgyllCMS not installed";
                        StatusBrush = ErrorBrush;
                        StatusDetailText = "Click Refresh to try downloading again";
                        _colorimeterReady = false;
                        RefreshCanStart();
                        return;
                    }
                }

                // Show that we found ArgyllCMS and log the version info
                StatusText = "Searching for colorimeter...";
                string binDirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(argyllBinPath) ?? argyllBinPath);
                StatusDetailText = $"Using: {binDirName}";
                Log.Info($"CalibrationSetupViewModel: Using ArgyllCMS from {argyllBinPath}");

                ColorimeterService = new ColorimeterService(argyllBinPath);

                // Add timeout for initialization
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await ColorimeterService.InitializeAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    StatusText = "Detection timed out";
                    StatusBrush = ErrorBrush;
                    StatusDetailText = "Check colorimeter connection and USB drivers";
                    _colorimeterReady = false;
                    RefreshCanStart();
                    return;
                }

                UpdateColorimeterStatus();
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                StatusBrush = ErrorBrush;
                StatusDetailText = "Check console for details";
                Log.Info($"Colorimeter initialization error: {ex}");
                _colorimeterReady = false;
                RefreshCanStart();
            }
        }

        private void OfferDownload(string reason)
        {
            // Already have our version installed, nothing to do
            if (ArgyllDownloader.IsInstalled()) return;
            if (OfferArgyllDownload == null) return;

            if (OfferArgyllDownload(reason))
            {
                StatusText = "Download complete";
                StatusBrush = SuccessBrush;
                StatusDetailText = "ArgyllCMS installed successfully";
            }
            else
            {
                Log.Info("ArgyllCMS download was cancelled or failed");
            }
        }

        private async Task RefreshColorimeterAsync()
        {
            StatusText = "Searching...";
            StatusBrush = WarningBrush;
            StatusDetailText = "";
            _colorimeterReady = false;
            RefreshCanStart();

            if (ColorimeterService != null)
            {
                await ColorimeterService.InitializeAsync();
                UpdateColorimeterStatus();
            }
            else
            {
                await InitializeColorimeterAsync();
            }
        }

        private void UpdateColorimeterStatus()
        {
            if (ColorimeterService == null)
            {
                StatusText = "Colorimeter service unavailable";
                StatusBrush = ErrorBrush;
                StatusDetailText = "";
                _colorimeterReady = false;
                RefreshCanStart();
                return;
            }

            if (ColorimeterService.IsReady)
            {
                StatusText = "Connected and ready";
                StatusBrush = SuccessBrush;
                StatusDetailText = ColorimeterService.ConnectedColorimeter?.Model ?? "Unknown model";
                _colorimeterReady = true;
                RefreshCanStart();
            }
            else
            {
                StatusText = "No colorimeter detected";
                StatusBrush = WarningBrush;
                StatusDetailText = "Connect your colorimeter and click Refresh";
                _colorimeterReady = false;
                RefreshCanStart();
            }
        }

        #endregion

        /// <summary>
        /// Extracts version string from ArgyllCMS path (e.g., "V2.3.1" from path containing "Argyll_V2.3.1").
        /// </summary>
        private static string ExtractVersionFromPath(string path)
        {
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
            if (versionInfo == "unknown")
                return true;

            var match = Regex.Match(versionInfo, @"V?(\d+)\.(\d+)");
            if (!match.Success)
                return true;

            int major = int.Parse(match.Groups[1].Value);
            // V3.0.0+ is considered modern
            return major < 3;
        }

        private void Start()
        {
            RefreshPreflight();
            var criticalError = CriticalStartError();
            if (!_colorimeterReady || criticalError != null)
            {
                StatusText = !_colorimeterReady ? "Colorimeter not ready" : "Calibration setup needs attention";
                StatusBrush = ErrorBrush;
                StatusDetailText = !_colorimeterReady
                    ? "Connect your colorimeter and click Refresh"
                    : criticalError!.Message;
                return;
            }

            ResultMonitor = _selectedMonitor?.Model ?? (_monitors.Count > 0 ? _monitors[0] : null);

            var target = Targets.FirstOrDefault(t => t.IsSelected)?.Target ?? StandardTargets.SrgbGamma22;
            ResultTarget = WhitePointOnly ? target.AsWhitePointOnly() : target;
            ResultPreset = _preset;
            ResultDisplayType = _displayType;
            ColorimeterService?.SetDisplayType(_displayType);

            // Meter spectral correction: applied to this session; all setup choices are
            // remembered per monitor for the next session.
            string? correction = SelectedCorrection?.Path;
            ColorimeterService?.SetCorrectionFile(correction);
            if (ResultMonitor != null)
                _settingsManager?.SetCalibrationPrefs(
                    ResultMonitor.MonitorDevicePath, correction,
                    _displayType.ToString(), WhitePointOnly,
                    target.Name, _preset.ToString());

            Log.Info($"CalibrationSetup.Start: Monitor={ResultMonitor?.FriendlyName ?? "null"}, Target={ResultTarget?.Name ?? "null"}, DisplayType={_displayType}, " +
                     $"Correction={(correction != null ? System.IO.Path.GetFileName(correction) : "built-in")}, Colorimeter={(ColorimeterService != null ? "present" : "null")}");

            CloseRequested?.Invoke(true);
        }
    }
}
