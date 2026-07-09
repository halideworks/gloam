using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HDRGammaController.Core;

namespace HDRGammaController.ViewModels
{
    /// <summary>
    /// Display state for NightModeScheduleControl. The canvas drawing, timeline drag
    /// handling and geolocation detect flow stay in the control (they are entangled
    /// with hit testing and canvas geometry); this holds the schedule rows, location
    /// text, ultra warm flag and drag overlay state the XAML binds to.
    /// </summary>
    public class NightModeScheduleViewModel : ObservableObject
    {
        private NightModeSettings _settings = null!; // Set by Initialize

        /// <summary>Raised when an edit changed the settings (the control maps this to ScheduleChanged).</summary>
        public event Action? SettingsEdited;

        /// <summary>Raised when a schedule row was edited (the control redraws the curve and saves).</summary>
        public event Action? PointEdited;

        public List<ScheduleTriggerType> TriggerTypes { get; } =
            Enum.GetValues(typeof(ScheduleTriggerType)).Cast<ScheduleTriggerType>().ToList();

        /// <summary>Schedule rows, sorted chronologically by resolved time of day.</summary>
        public ObservableCollection<SchedulePointViewModel> Points { get; } = new();

        private SchedulePointViewModel? _selectedPoint;
        public SchedulePointViewModel? SelectedPoint { get => _selectedPoint; set => SetProperty(ref _selectedPoint, value); }

        /// <summary>Parsed latitude used for sunrise/sunset resolution. Read by the drawing code.</summary>
        public double? Latitude { get; set; }

        /// <summary>Parsed longitude used for sunrise/sunset resolution. Read by the drawing code.</summary>
        public double? Longitude { get; set; }

        private string _latitudeText = "";
        public string LatitudeText { get => _latitudeText; set => SetProperty(ref _latitudeText, value); }

        private string _longitudeText = "";
        public string LongitudeText { get => _longitudeText; set => SetProperty(ref _longitudeText, value); }

        private bool _useUltraWarmMode;
        public bool UseUltraWarmMode
        {
            get => _useUltraWarmMode;
            set
            {
                if (SetProperty(ref _useUltraWarmMode, value))
                {
                    if (_settings == null) return;
                    _settings.UseUltraWarmMode = value;
                    SettingsEdited?.Invoke();
                }
            }
        }

        /// <summary>
        /// Rendering algorithms offered in the dashboard, ordered best-perceptual-first.
        /// </summary>
        public IReadOnlyList<NightModeAlgorithmOption> AvailableAlgorithms { get; } = NightModeAlgorithmOption.DefaultOptions;

        private NightModeAlgorithm _algorithm = NightModeAlgorithm.Perceptual;
        public NightModeAlgorithm Algorithm
        {
            get => _algorithm;
            set
            {
                if (SetProperty(ref _algorithm, value))
                {
                    OnPropertyChanged(nameof(AlgorithmDescription));
                    OnPropertyChanged(nameof(IsUltraWarmApplicable));
                    OnPropertyChanged(nameof(IsPreserveLuminanceApplicable));
                    OnPropertyChanged(nameof(IsPerceptualSelected));
                    if (_settings == null) return;
                    _settings.Algorithm = value;
                    SettingsEdited?.Invoke();
                }
            }
        }

        /// <summary>Ultra Warm only affects the Classic (Standard/Helland) curve.</summary>
        public bool IsUltraWarmApplicable => _algorithm == NightModeAlgorithm.Standard;

        private bool _preserveLuminance;
        public bool PreserveLuminance
        {
            get => _preserveLuminance;
            set
            {
                if (SetProperty(ref _preserveLuminance, value))
                {
                    if (_settings == null) return;
                    _settings.PreserveLuminance = value;
                    SettingsEdited?.Invoke();
                }
            }
        }

        /// <summary>
        /// Constant-Y does not apply to UltraNight — its dimming is deliberate
        /// melanopic-dose reduction that a luminance boost would undo.
        /// </summary>
        public bool IsPreserveLuminanceApplicable => _algorithm != NightModeAlgorithm.UltraNight;

        /// <summary>The intensity slider only applies to the Perceptual algorithm.</summary>
        public bool IsPerceptualSelected => _algorithm == NightModeAlgorithm.Perceptual;

        // Perceptual intensity exposed to the UI as a 0–100% slider (maps to strength 0–1).
        private double _perceptualStrengthPercent = ColorAdjustments.DefaultPerceptualStrength * 100.0;
        public double PerceptualStrengthPercent
        {
            get => _perceptualStrengthPercent;
            set
            {
                double clamped = Math.Clamp(double.IsFinite(value) ? value : 80.0, 0.0, 100.0);
                if (SetProperty(ref _perceptualStrengthPercent, clamped))
                {
                    if (_settings == null) return;
                    _settings.PerceptualStrength = clamped / 100.0;
                    SettingsEdited?.Invoke();
                }
            }
        }

        /// <summary>One-line explanation of the selected algorithm, shown under the selector.</summary>
        public string AlgorithmDescription => _algorithm switch
        {
            NightModeAlgorithm.Perceptual =>
                "Warm white point eased toward neutral (partial chromatic adaptation) — cuts blue while keeping colours far closer to normal than a full shift.",
            NightModeAlgorithm.AccurateCIE1931 =>
                "True black-body white point at full strength. Colorimetrically exact, but strongly warms and desaturates the whole image.",
            NightModeAlgorithm.Standard =>
                "Classic photo-style warm tint (Tanner Helland). Pair with Ultra Warm for a stronger effect below 2800K.",
            NightModeAlgorithm.UltraNight =>
                "Amber/red for maximum circadian protection — cuts ~90% of blue and deeply cuts green. Not colour-accurate; for the darkest part of the evening.",
            _ => string.Empty
        };

        #region Drag overlay

        private bool _isDragOverlayVisible;
        public bool IsDragOverlayVisible { get => _isDragOverlayVisible; set => SetProperty(ref _isDragOverlayVisible, value); }

        private string _overlayTimeText = "22:00";
        public string OverlayTimeText { get => _overlayTimeText; set => SetProperty(ref _overlayTimeText, value); }

        private string _overlayTempText = "3500K";
        public string OverlayTempText { get => _overlayTempText; set => SetProperty(ref _overlayTempText, value); }

        #endregion

        public void Initialize(NightModeSettings settings)
        {
            _settings = settings;
            Latitude = settings.Latitude;
            Longitude = settings.Longitude;

            if (Latitude.HasValue) LatitudeText = Latitude.Value.ToString("F2");
            if (Longitude.HasValue) LongitudeText = Longitude.Value.ToString("F2");

            // Reflect the persisted rendering settings without triggering a redundant
            // SettingsEdited/save just by opening or re-syncing the editor.
            SyncRenderingSettings();

            // Ensure schedule exists
            settings.EnsureSchedule(Latitude, Longitude);

            RefreshPoints();
        }

        public void SyncRenderingSettings()
        {
            if (_settings == null) return;

            _algorithm = _settings.Algorithm;
            _perceptualStrengthPercent = NightModeSettings.ClampPerceptualStrength(_settings.PerceptualStrength) * 100.0;
            _useUltraWarmMode = _settings.UseUltraWarmMode;
            _preserveLuminance = _settings.PreserveLuminance;

            OnPropertyChanged(nameof(Algorithm));
            OnPropertyChanged(nameof(AlgorithmDescription));
            OnPropertyChanged(nameof(IsUltraWarmApplicable));
            OnPropertyChanged(nameof(IsPreserveLuminanceApplicable));
            OnPropertyChanged(nameof(IsPerceptualSelected));
            OnPropertyChanged(nameof(PerceptualStrengthPercent));
            OnPropertyChanged(nameof(UseUltraWarmMode));
            OnPropertyChanged(nameof(PreserveLuminance));
        }

        /// <summary>
        /// Parses the lat/lon text the user typed and writes it through to the settings.
        /// Called by the control on TextBox LostFocus.
        /// </summary>
        public void CommitLocation()
        {
            if (TryParseCoordinate(LatitudeText, out double lat))
                Latitude = NightModeSettings.ClampLatitude(lat);
            if (TryParseCoordinate(LongitudeText, out double lon))
                Longitude = NightModeSettings.ClampLongitude(lon);

            _settings.Latitude = Latitude;
            _settings.Longitude = Longitude;

            LatitudeText = Latitude.HasValue ? Latitude.Value.ToString("F2") : "";
            LongitudeText = Longitude.HasValue ? Longitude.Value.ToString("F2") : "";
        }

        /// <summary>
        /// Culture-tolerant coordinate parsing: invariant first (auto-detect writes "40.67"
        /// with an invariant dot, which must parse the same on every locale), then the
        /// user's culture so typed comma-decimal input ("40,67" on fr-FR) still works.
        /// NumberStyles.Float deliberately excludes AllowThousands: with the default styles
        /// the invariant pass would read "40,67" as 4067 (comma = group separator) before
        /// the current-culture pass ever ran, turning it into clamped garbage.
        /// </summary>
        internal static bool TryParseCoordinate(string? text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        /// <summary>Rebuilds the row list from the settings, sorted chronologically.</summary>
        public void RefreshPoints()
        {
            // Sort points chronologically by their resolved time of day
            var sortedPoints = _settings.Schedule
                .Select(p => new { Point = p, Time = p.GetTimeOfDay(Latitude, Longitude) })
                .OrderBy(x => x.Time)
                .Select(x => new SchedulePointViewModel(x.Point) { Changed = OnPointEdited })
                .ToList();

            Points.Clear();
            foreach (var vm in sortedPoints) Points.Add(vm);
        }

        private void OnPointEdited() => PointEdited?.Invoke();
    }

    /// <summary>
    /// Row state for one schedule point. Wraps the model directly and notifies the
    /// parent view model when the user edits a value (unless suppressed during drag).
    /// </summary>
    public class SchedulePointViewModel : ObservableObject
    {
        public NightModeSchedulePoint Model { get; }

        /// <summary>Set by the parent view model; invoked when an edit should redraw and save.</summary>
        public Action? Changed { get; set; }

        /// <summary>
        /// When true, property setters don't trigger change notifications.
        /// Used during drag operations to avoid redundant updates.
        /// </summary>
        public bool SuppressNotifications { get; set; }

        public SchedulePointViewModel(NightModeSchedulePoint model)
        {
            Model = model;
        }

        public ScheduleTriggerType TriggerType
        {
            get => Model.TriggerType;
            set
            {
                Model.TriggerType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTime)); // Format switches between time and offset
                Notify();
            }
        }

        public string DisplayTime
        {
            get
            {
                if (Model.TriggerType == ScheduleTriggerType.FixedTime)
                    return NightModeSettings.NormalizeTimeOfDay(Model.Time).ToString(@"hh\:mm");
                else
                    return (Model.OffsetMinutes >= 0 ? "+" : "") + NightModeSettings.ClampOffsetMinutes(Model.OffsetMinutes) + "m";
            }
            set
            {
                if (Model.TriggerType == ScheduleTriggerType.FixedTime)
                {
                    var parsed = ParseTimeInput(value);
                    if (parsed.HasValue)
                        Model.Time = NightModeSettings.NormalizeTimeOfDay(parsed.Value);
                }
                else
                {
                    if (double.TryParse(value.Replace("m", ""), out var d))
                        Model.OffsetMinutes = NightModeSettings.ClampOffsetMinutes(d);
                }
                OnPropertyChanged();
                Notify();
            }
        }

        /// <summary>
        /// Parses time input flexibly. Accepts formats like:
        /// "23:00", "2300", "23", "9:30", "930", "9"
        /// </summary>
        private static TimeSpan? ParseTimeInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            input = input.Trim();

            // Standard format with colon (23:00, 9:30)
            if (TimeSpan.TryParse(input, out var t))
                return t;

            // Try parsing as a number
            if (int.TryParse(input, out int num))
            {
                // Single or double digit: treat as hours (e.g., "9" -> 09:00, "23" -> 23:00)
                if (num >= 0 && num <= 24)
                {
                    return TimeSpan.FromHours(num == 24 ? 0 : num);
                }

                // 3-4 digit format: HHMM or HMM (e.g., "2300" -> 23:00, "930" -> 09:30)
                if (num >= 100 && num <= 2400)
                {
                    int hours = num / 100;
                    int minutes = num % 100;

                    if (hours >= 0 && hours <= 24 && minutes >= 0 && minutes < 60)
                    {
                        if (hours == 24) { hours = 0; }
                        return new TimeSpan(hours, minutes, 0);
                    }
                }
            }

            return null;
        }

        public int TargetKelvin
        {
            get => Model.TargetKelvin;
            set { Model.TargetKelvin = NightModeSettings.ClampKelvin(value); OnPropertyChanged(); Notify(); }
        }

        public int FadeMinutes
        {
            get => Model.FadeMinutes;
            set { Model.FadeMinutes = NightModeSettings.ClampFadeMinutes(value); OnPropertyChanged(); Notify(); }
        }

        /// <summary>
        /// Raises change notifications after the model was mutated directly
        /// (the timeline drag writes Model.Time and TargetKelvin each move).
        /// </summary>
        public void RefreshDisplay()
        {
            OnPropertyChanged(nameof(DisplayTime));
            OnPropertyChanged(nameof(TargetKelvin));
        }

        private void Notify()
        {
            if (!SuppressNotifications)
            {
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// A selectable night-mode rendering algorithm plus its display label, bound by the
    /// dashboard's algorithm ComboBox (SelectedValuePath = Value, DisplayMemberPath = Label).
    /// </summary>
    public class NightModeAlgorithmOption
    {
        public static IReadOnlyList<NightModeAlgorithmOption> DefaultOptions { get; } = new List<NightModeAlgorithmOption>
        {
            // "default", not "recommended": Ultra Night may become the recommended mode once
            // its melanopic tuning is complete — don't bake a ranking into the label.
            new(NightModeAlgorithm.Perceptual, "Perceptual (default)"),
            new(NightModeAlgorithm.UltraNight, "Ultra Night (amber)"),
            new(NightModeAlgorithm.AccurateCIE1931, "Accurate (CIE 1931)"),
            new(NightModeAlgorithm.Standard, "Classic (warm)"),
        };

        public NightModeAlgorithmOption(NightModeAlgorithm value, string label)
        {
            Value = value;
            Label = label;
        }

        public NightModeAlgorithm Value { get; }
        public string Label { get; }

        // The Brutalist ComboBox template renders the collapsed selection box via ToString()
        // rather than DisplayMemberPath, so without this it shows the type name until opened.
        public override string ToString() => Label;
    }
}
