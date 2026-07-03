using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HDRGammaController.ViewModels
{
    /// <summary>
    /// Display state for CalibrationReportWindow. The apply/verify orchestration stays in
    /// the window (it is entangled with the patch windows, the colorimeter session and
    /// modal dialogs); this holds everything the XAML renders so the code-behind never
    /// touches controls directly. The four chart canvases remain code-behind-drawn.
    /// </summary>
    public class CalibrationReportViewModel : ObservableObject
    {
        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static Brush FrozenArgb(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        // The report's shared color vocabulary (frozen so they can be reused freely).
        public static readonly Brush GreenBrush = Frozen(0x22, 0xc5, 0x5e);
        public static readonly Brush CyanBrush = Frozen(0x22, 0xd3, 0xee);
        public static readonly Brush BlueBrush = Frozen(0x3b, 0x82, 0xf6);
        public static readonly Brush OrangeBrush = Frozen(0xf9, 0x73, 0x16);
        public static readonly Brush AmberBrush = Frozen(0xf5, 0x9e, 0x0b);
        public static readonly Brush RedBrush = Frozen(0xef, 0x44, 0x44);
        public static readonly Brush DimBrush = Frozen(0xa0, 0xa0, 0xa0);
        public static readonly Brush DefaultValueBrush = Frozen(0xe0, 0xe0, 0xe0);

        // Grade disc backgrounds: the grade color at 25% alpha.
        public static readonly Brush GreenBackgroundBrush = FrozenArgb(0x40, 0x22, 0xc5, 0x5e);
        public static readonly Brush BlueBackgroundBrush = FrozenArgb(0x40, 0x3b, 0x82, 0xf6);
        public static readonly Brush OrangeBackgroundBrush = FrozenArgb(0x40, 0xf9, 0x73, 0x16);
        public static readonly Brush AmberBackgroundBrush = FrozenArgb(0x40, 0xf5, 0x9e, 0x0b);
        public static readonly Brush RedBackgroundBrush = FrozenArgb(0x40, 0xef, 0x44, 0x44);
        private static readonly Brush InitialGradeBackgroundBrush = Frozen(0x16, 0x4e, 0x63);

        /// <summary>The ΔE quality color coding used throughout the accuracy table.</summary>
        public static Brush DeltaEBrush(double deltaE) => !double.IsFinite(deltaE)
            ? DefaultValueBrush
            : deltaE switch
        {
            < 1.0 => GreenBrush,   // excellent
            < 2.0 => CyanBrush,    // good
            < 3.0 => OrangeBrush,  // acceptable
            < 5.0 => AmberBrush,   // marginal
            _ => RedBrush          // poor
        };

        // Print-safe palette: dark enough to stay readable as black-text-on-white-paper
        // companions (the on-screen neons wash out on a white page).
        public static readonly Brush PrintGoodBrush = Frozen(0x1A, 0x7F, 0x37);
        public static readonly Brush PrintMediumBrush = Frozen(0x9A, 0x67, 0x00);
        public static readonly Brush PrintBadBrush = Frozen(0xC4, 0x2B, 0x1C);

        /// <summary>
        /// The same ΔE thresholds as <see cref="DeltaEBrush"/>, collapsed onto the print-safe
        /// palette for the exported (light) report: good below 2.0, medium below 5.0, bad above.
        /// </summary>
        public static Brush DeltaEPrintBrush(double deltaE) => !double.IsFinite(deltaE)
            ? DefaultValueBrush
            : deltaE switch
        {
            < 2.0 => PrintGoodBrush,   // excellent/good
            < 5.0 => PrintMediumBrush, // acceptable/marginal
            _ => PrintBadBrush         // poor
        };

        #region Header

        private string _monitorNameText = "Monitor Name";
        public string MonitorNameText { get => _monitorNameText; set => SetProperty(ref _monitorNameText, value); }

        private string _calibrationDateText = "Calibrated: Today";
        public string CalibrationDateText { get => _calibrationDateText; set => SetProperty(ref _calibrationDateText, value); }

        private string _gradeText = "";
        public string GradeText { get => _gradeText; set => SetProperty(ref _gradeText, value); }

        private Brush _gradeBrush = CyanBrush;
        public Brush GradeBrush { get => _gradeBrush; set => SetProperty(ref _gradeBrush, value); }

        private Brush _gradeBackground = InitialGradeBackgroundBrush;
        public Brush GradeBackground { get => _gradeBackground; set => SetProperty(ref _gradeBackground, value); }

        private string _gradeScopeText = "uncorrected panel";
        public string GradeScopeText { get => _gradeScopeText; set => SetProperty(ref _gradeScopeText, value); }

        #endregion

        #region Accuracy table

        // The headline average is set as a single "value ± uncertainty" string (kept whole
        // for the print export), but the window renders it as two Runs so the ± tail can be
        // dim and small instead of inheriting the grade color at headline size. Setting the
        // full string splits it into the value head and the " ± …" tail automatically.
        private string _avgDeltaEText = "-";
        public string AvgDeltaEText
        {
            get => _avgDeltaEText;
            set
            {
                if (!SetProperty(ref _avgDeltaEText, value)) return;
                var (head, tail) = SplitUncertainty(value);
                AvgDeltaEValueText = head;
                AvgDeltaEUncertaintyText = tail;
            }
        }

        private string _avgDeltaEValueText = "-";
        public string AvgDeltaEValueText { get => _avgDeltaEValueText; private set => SetProperty(ref _avgDeltaEValueText, value); }

        private string _avgDeltaEUncertaintyText = "";
        public string AvgDeltaEUncertaintyText { get => _avgDeltaEUncertaintyText; private set => SetProperty(ref _avgDeltaEUncertaintyText, value); }

        /// <summary>
        /// Splits "1.23 ± 0.45" into ("1.23", " ± 0.45"); returns (text, "") when there is
        /// no uncertainty tail. The tail keeps a leading space so the two Runs read as one
        /// phrase without the XAML needing to inject whitespace between them.
        /// </summary>
        internal static (string Value, string Uncertainty) SplitUncertainty(string? text)
        {
            if (string.IsNullOrEmpty(text)) return (text ?? "", "");
            int i = text.IndexOf('±'); // ±
            if (i < 0) return (text, "");
            return (text.Substring(0, i).TrimEnd(), " " + text.Substring(i).Trim());
        }

        private Brush _avgDeltaEBrush = DefaultValueBrush;
        public Brush AvgDeltaEBrush { get => _avgDeltaEBrush; set => SetProperty(ref _avgDeltaEBrush, value); }

        // Measurement-uncertainty breakdowns (roadmap 1.3) shown as tooltips on the
        // headline averages; null (no tooltip) when no uncertainty budget is available.
        private string? _avgDeltaEToolTip;
        public string? AvgDeltaEToolTip { get => _avgDeltaEToolTip; set => SetProperty(ref _avgDeltaEToolTip, value); }

        private string? _afterAvgToolTip;
        public string? AfterAvgToolTip { get => _afterAvgToolTip; set => SetProperty(ref _afterAvgToolTip, value); }

        private string _maxDeltaEText = "-";
        public string MaxDeltaEText { get => _maxDeltaEText; set => SetProperty(ref _maxDeltaEText, value); }

        private Brush _maxDeltaEBrush = DefaultValueBrush;
        public Brush MaxDeltaEBrush { get => _maxDeltaEBrush; set => SetProperty(ref _maxDeltaEBrush, value); }

        private string _grayscaleDeltaEText = "-";
        public string GrayscaleDeltaEText { get => _grayscaleDeltaEText; set => SetProperty(ref _grayscaleDeltaEText, value); }

        private Brush _grayscaleDeltaEBrush = DefaultValueBrush;
        public Brush GrayscaleDeltaEBrush { get => _grayscaleDeltaEBrush; set => SetProperty(ref _grayscaleDeltaEBrush, value); }

        private string _primaryDeltaEText = "-";
        public string PrimaryDeltaEText { get => _primaryDeltaEText; set => SetProperty(ref _primaryDeltaEText, value); }

        private Brush _primaryDeltaEBrush = DefaultValueBrush;
        public Brush PrimaryDeltaEBrush { get => _primaryDeltaEBrush; set => SetProperty(ref _primaryDeltaEBrush, value); }

        private string _afterAvgText = "-";
        public string AfterAvgText
        {
            get => _afterAvgText;
            set
            {
                if (!SetProperty(ref _afterAvgText, value)) return;
                var (head, tail) = SplitUncertainty(value);
                AfterAvgValueText = head;
                AfterAvgUncertaintyText = tail;
            }
        }

        private string _afterAvgValueText = "-";
        public string AfterAvgValueText { get => _afterAvgValueText; private set => SetProperty(ref _afterAvgValueText, value); }

        private string _afterAvgUncertaintyText = "";
        public string AfterAvgUncertaintyText { get => _afterAvgUncertaintyText; private set => SetProperty(ref _afterAvgUncertaintyText, value); }

        private Brush _afterAvgBrush = DefaultValueBrush;
        public Brush AfterAvgBrush { get => _afterAvgBrush; set => SetProperty(ref _afterAvgBrush, value); }

        private string _afterMaxText = "-";
        public string AfterMaxText { get => _afterMaxText; set => SetProperty(ref _afterMaxText, value); }

        private Brush _afterMaxBrush = DefaultValueBrush;
        public Brush AfterMaxBrush { get => _afterMaxBrush; set => SetProperty(ref _afterMaxBrush, value); }

        private string _afterGrayscaleText = "-";
        public string AfterGrayscaleText { get => _afterGrayscaleText; set => SetProperty(ref _afterGrayscaleText, value); }

        private Brush _afterGrayscaleBrush = DefaultValueBrush;
        public Brush AfterGrayscaleBrush { get => _afterGrayscaleBrush; set => SetProperty(ref _afterGrayscaleBrush, value); }

        private string _afterPrimaryText = "-";
        public string AfterPrimaryText { get => _afterPrimaryText; set => SetProperty(ref _afterPrimaryText, value); }

        private Brush _afterPrimaryBrush = DefaultValueBrush;
        public Brush AfterPrimaryBrush { get => _afterPrimaryBrush; set => SetProperty(ref _afterPrimaryBrush, value); }

        private string _verifyDetailText = "";
        public string VerifyDetailText { get => _verifyDetailText; set => SetProperty(ref _verifyDetailText, value); }

        private bool _isVerifyDetailVisible;
        public bool IsVerifyDetailVisible { get => _isVerifyDetailVisible; set => SetProperty(ref _isVerifyDetailVisible, value); }

        // HDR verification passes broken out of the verify-detail prose so each can carry a
        // kicker label (visual hierarchy): the PQ luminance/ITP tracking sweep and the
        // colored-HDR (Rec.2020 container) sweep.
        private string _pqTrackingDetailText = "";
        public string PqTrackingDetailText { get => _pqTrackingDetailText; set => SetProperty(ref _pqTrackingDetailText, value); }

        private bool _isPqTrackingDetailVisible;
        public bool IsPqTrackingDetailVisible { get => _isPqTrackingDetailVisible; set => SetProperty(ref _isPqTrackingDetailVisible, value); }

        private string _coloredHdrDetailText = "";
        public string ColoredHdrDetailText { get => _coloredHdrDetailText; set => SetProperty(ref _coloredHdrDetailText, value); }

        private bool _isColoredHdrDetailVisible;
        public bool IsColoredHdrDetailVisible { get => _isColoredHdrDetailVisible; set => SetProperty(ref _isColoredHdrDetailVisible, value); }

        private string _beforeAfterNoteText = "";
        public string BeforeAfterNoteText { get => _beforeAfterNoteText; set => SetProperty(ref _beforeAfterNoteText, value); }

        private bool _isBeforeAfterNoteVisible;
        public bool IsBeforeAfterNoteVisible { get => _isBeforeAfterNoteVisible; set => SetProperty(ref _isBeforeAfterNoteVisible, value); }

        private string _summaryText = "Calibration completed successfully.";
        public string SummaryText { get => _summaryText; set => SetProperty(ref _summaryText, value); }

        private bool _isPerceptualNoteVisible;
        public bool IsPerceptualNoteVisible { get => _isPerceptualNoteVisible; set => SetProperty(ref _isPerceptualNoteVisible, value); }

        #endregion

        #region Charts

        // Historical reports (re-opened from the report browser) have no raw measurements,
        // so the chart canvases collapse and a short note takes their place.
        private bool _areChartsVisible = true;
        public bool AreChartsVisible { get => _areChartsVisible; set => SetProperty(ref _areChartsVisible, value); }

        private bool _isChartsNoteVisible;
        public bool IsChartsNoteVisible { get => _isChartsNoteVisible; set => SetProperty(ref _isChartsNoteVisible, value); }

        #endregion

        #region Detailed verification

        /// <summary>The "Detailed verification" toggle next to the Verify button.</summary>
        private bool _isDetailedVerifyChecked;
        public bool IsDetailedVerifyChecked { get => _isDetailedVerifyChecked; set => SetProperty(ref _isDetailedVerifyChecked, value); }

        /// <summary>Shows the Detailed Verification card (live sweep or persisted history).</summary>
        private bool _hasDetailedResults;
        public bool HasDetailedResults { get => _hasDetailedResults; set => SetProperty(ref _hasDetailedResults, value); }

        private string _categoryBreakdownText = "";
        public string CategoryBreakdownText { get => _categoryBreakdownText; set => SetProperty(ref _categoryBreakdownText, value); }

        /// <summary>One row of the worst-10 / best-10 lists: rank, patch name and color-coded ΔE.</summary>
        public sealed record PatchListItem(string Rank, string Name, string DeltaEText, Brush DeltaEBrush);

        public ObservableCollection<PatchListItem> WorstPatches { get; } = new();

        public ObservableCollection<PatchListItem> BestPatches { get; } = new();

        #endregion

        #region Display characteristics

        private string _peakLuminanceText = "-- cd/m2";
        public string PeakLuminanceText { get => _peakLuminanceText; set => SetProperty(ref _peakLuminanceText, value); }

        private string _blackLevelText = "-- cd/m2";
        public string BlackLevelText { get => _blackLevelText; set => SetProperty(ref _blackLevelText, value); }

        private string _contrastRatioText = "--:1";
        public string ContrastRatioText { get => _contrastRatioText; set => SetProperty(ref _contrastRatioText, value); }

        private string _measuredGammaText = "--";
        public string MeasuredGammaText { get => _measuredGammaText; set => SetProperty(ref _measuredGammaText, value); }

        private string _whitePointCctText = "-- K";
        public string WhitePointCctText { get => _whitePointCctText; set => SetProperty(ref _whitePointCctText, value); }

        private string _whitePointDuvText = "--";
        public string WhitePointDuvText { get => _whitePointDuvText; set => SetProperty(ref _whitePointDuvText, value); }

        private string _srgbCoverageText = "--%";
        public string SrgbCoverageText { get => _srgbCoverageText; set => SetProperty(ref _srgbCoverageText, value); }

        private string _targetText = "--";
        public string TargetText { get => _targetText; set => SetProperty(ref _targetText, value); }

        #endregion

        #region Primaries comparison

        private string _redMeasuredText = "(0.640, 0.330)";
        public string RedMeasuredText { get => _redMeasuredText; set => SetProperty(ref _redMeasuredText, value); }

        private string _redTargetText = "(0.640, 0.330)";
        public string RedTargetText { get => _redTargetText; set => SetProperty(ref _redTargetText, value); }

        private string _redErrorText = "0.000";
        public string RedErrorText { get => _redErrorText; set => SetProperty(ref _redErrorText, value); }

        private string _greenMeasuredText = "(0.300, 0.600)";
        public string GreenMeasuredText { get => _greenMeasuredText; set => SetProperty(ref _greenMeasuredText, value); }

        private string _greenTargetText = "(0.300, 0.600)";
        public string GreenTargetText { get => _greenTargetText; set => SetProperty(ref _greenTargetText, value); }

        private string _greenErrorText = "0.000";
        public string GreenErrorText { get => _greenErrorText; set => SetProperty(ref _greenErrorText, value); }

        private string _blueMeasuredText = "(0.150, 0.060)";
        public string BlueMeasuredText { get => _blueMeasuredText; set => SetProperty(ref _blueMeasuredText, value); }

        private string _blueTargetText = "(0.150, 0.060)";
        public string BlueTargetText { get => _blueTargetText; set => SetProperty(ref _blueTargetText, value); }

        private string _blueErrorText = "0.000";
        public string BlueErrorText { get => _blueErrorText; set => SetProperty(ref _blueErrorText, value); }

        private string _whiteMeasuredText = "(0.313, 0.329)";
        public string WhiteMeasuredText { get => _whiteMeasuredText; set => SetProperty(ref _whiteMeasuredText, value); }

        private string _whiteTargetText = "(0.313, 0.329)";
        public string WhiteTargetText { get => _whiteTargetText; set => SetProperty(ref _whiteTargetText, value); }

        private string _whiteErrorText = "0.000";
        public string WhiteErrorText { get => _whiteErrorText; set => SetProperty(ref _whiteErrorText, value); }

        #endregion

        #region Calibration details

        private string _patchCountText = "--";
        public string PatchCountText { get => _patchCountText; set => SetProperty(ref _patchCountText, value); }

        private string _measurementTimeText = "--";
        public string MeasurementTimeText { get => _measurementTimeText; set => SetProperty(ref _measurementTimeText, value); }

        private string _colorimeterText = "--";
        public string ColorimeterText { get => _colorimeterText; set => SetProperty(ref _colorimeterText, value); }

        private string _lutSizeText = "--";
        public string LutSizeText { get => _lutSizeText; set => SetProperty(ref _lutSizeText, value); }

        private string _profilePathText = "--";
        public string ProfilePathText { get => _profilePathText; set => SetProperty(ref _profilePathText, value); }

        private string _measurementValidationText = "";
        public string MeasurementValidationText { get => _measurementValidationText; set => SetProperty(ref _measurementValidationText, value); }

        private Brush _measurementValidationBrush = DimBrush;
        public Brush MeasurementValidationBrush { get => _measurementValidationBrush; set => SetProperty(ref _measurementValidationBrush, value); }

        private bool _isMeasurementValidationVisible;
        public bool IsMeasurementValidationVisible { get => _isMeasurementValidationVisible; set => SetProperty(ref _isMeasurementValidationVisible, value); }

        #endregion

        #region Recommendations

        public ObservableCollection<string> Recommendations { get; } = new();

        #endregion

        #region Status and buttons

        private string _statusText = "Profile is active and applied.";
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        private Brush _statusBrush = GreenBrush;
        public Brush StatusBrush { get => _statusBrush; set => SetProperty(ref _statusBrush, value); }

        private string _applyButtonContent = "Apply Profile";
        public string ApplyButtonContent { get => _applyButtonContent; set => SetProperty(ref _applyButtonContent, value); }

        private bool _isApplyEnabled = true;
        public bool IsApplyEnabled { get => _isApplyEnabled; set => SetProperty(ref _isApplyEnabled, value); }

        private string _verifyButtonContent = "Re-verify";
        public string VerifyButtonContent { get => _verifyButtonContent; set => SetProperty(ref _verifyButtonContent, value); }

        private bool _isVerifyEnabled = true;
        public bool IsVerifyEnabled { get => _isVerifyEnabled; set => SetProperty(ref _isVerifyEnabled, value); }

        private bool _isWhiteToolsEnabled = true;
        public bool IsWhiteToolsEnabled { get => _isWhiteToolsEnabled; set => SetProperty(ref _isWhiteToolsEnabled, value); }

        private bool _isCloseEnabled = true;
        public bool IsCloseEnabled { get => _isCloseEnabled; set => SetProperty(ref _isCloseEnabled, value); }

        #endregion
    }
}
