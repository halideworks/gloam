using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;
using Microsoft.Win32;

namespace HDRGammaController
{
    /// <summary>
    /// Window for displaying calibration results and metrics.
    /// </summary>
    public partial class CalibrationReportWindow : Window
    {
        /// <summary>All display state the XAML binds to.</summary>
        public CalibrationReportViewModel Vm { get; } = new CalibrationReportViewModel();

        private readonly CalibrationProfile? _profile;
        private readonly CalibrationMetrics? _metrics;
        private readonly DisplayCharacterization? _characterization;
        private readonly Lut3D? _correctionLut;
        private readonly IReadOnlyList<MeasurementResult>? _measurements;

        // Persisted drift observation from the calibration run (CalibrationResult), captured
        // BEFORE the orchestrator drift-normalized _measurements. The uncertainty budget must
        // use this pre-compensation peak drift: re-analyzing the (already compensated)
        // _measurements sees ~zero residual and would silently drop the drift-residual term.
        // Null when unknown (historical report re-opened from a saved profile).
        private readonly double? _peakWhiteDriftFraction;
        private readonly bool _driftCompensationApplied;

        private IReadOnlyList<MeasurementResult>? _verifyMeasurements;
        // Last complete verification result shown in the accuracy table. Report updates
        // from other tools (for example HDR characterization) must preserve this rather
        // than replacing the after-correction column with nulls.
        private CalibrationMetrics? _latestVerificationMetrics;
        private System.Threading.CancellationTokenSource? _verifyCts;

        // Single probe-busy gate: set for the whole duration of every op that holds the one
        // colorimeter (or actively re-installs profiles) — verify, HDR refine, white
        // re-anchor, HDR-renderer validation and the visual white trim. It disables
        // Apply/Verify/Refine/White-Tools/Close together so a second measurement session can
        // never start on the single probe mid-op (and clobber _verifyCts). Not all of these
        // ops set _verifyCts, so this is also what makes IsVerifyRunning honest for the tray.
        private bool _probeBusy;

        /// <summary>
        /// True while any probe-holding op is running. The tray reads this to refuse a second
        /// calibration flow (only one probe exists); re-anchor white and HDR-renderer
        /// validation hold the probe without a verify CTS, so the busy gate covers them too.
        /// </summary>
        internal bool IsVerifyRunning => _probeBusy || _verifyCts is { IsCancellationRequested: false };

        /// <summary>
        /// Prologue for every probe-holding op: claim the probe and lock out the actions that
        /// would start a competing measurement session or tear the window down mid-sweep.
        /// </summary>
        private void EnterProbeBusy()
        {
            _probeBusy = true;
            Vm.IsApplyEnabled = false;
            Vm.IsVerifyEnabled = false;
            Vm.IsWhiteToolsEnabled = false;
            Vm.IsCloseEnabled = false;
            RefineMenuButton.IsEnabled = false;
            CharacterizeHdrButton.IsEnabled = false;
        }

        /// <summary>Finally-clause counterpart to <see cref="EnterProbeBusy"/>.</summary>
        private void ExitProbeBusy()
        {
            _probeBusy = false;
            Vm.IsApplyEnabled = !_measurementInstallBlocked;
            Vm.IsVerifyEnabled = true;
            Vm.IsWhiteToolsEnabled = true;
            Vm.IsCloseEnabled = true;
            UpdateRefineButtonState();
        }

        // Detailed-verification per-patch results (name, category, dE) in measurement order.
        // Set by a detailed sweep, or rebuilt from the persisted summary for historical
        // reports; everything the Detailed Verification section shows derives from this.
        private IReadOnlyList<PatchDeltaE>? _detailedPatchResults;

        // Historical open (from the report browser): no metrics or measurements, just the
        // profile JSON loaded from disk. The accuracy table comes from ReportSummary and
        // the live tools stay disabled.
        private readonly bool _isHistorical;

        // Where this report's snapshot was written, so the post-verification re-save
        // updates the same file instead of creating a sibling.
        private string? _reportSavePath;
        private bool _lastReportSnapshotSaved;

        // HDR tone-mapping characterization (roadmap 2.3), when one ran this session.
        private ToneMappingCharacterization? _toneMapping;

        // Completion notice for the long probe operations (refine/characterize): set inside
        // the operation, shown by the click handler AFTER the Task returns — i.e. after the
        // topmost patch window has closed, so the modal isn't hidden behind it on a single
        // monitor. Every one of these operations should tell the user what happened.
        private (string Title, string Body)? _operationNotice;

        private void ShowOperationNotice()
        {
            if (_operationNotice is { } n && IsLoaded)
                ConfirmDialog.Info(this, n.Title, n.Body);
            _operationNotice = null;
        }

        private void RememberProbePlacement(double offsetX, double offsetY)
        {
            if (_applyContext is { } context)
                _applyContext = context with { PatchOffsetX = offsetX, PatchOffsetY = offsetY };
        }

        // SummaryText without the appended measurement-uncertainty line, so the line can
        // be recomputed (e.g. once the apply context reveals the CCSS state) without
        // stacking duplicates.
        private string? _summaryBaseText;

        // White tools state: re-anchoring replaces the characterization's white (drift fix);
        // the visual trim shifts the TARGET white (metameric fix). Both rebuild the profile.
        private DisplayCharacterization? _activeCharacterization;
        private Chromaticity? _trimmedTargetWhite;
        private bool _trimNameToggle;
        private bool _trimBusy;
        private (double Dx, double Dy)? _trimPendingNudge;

        // Everything needed to install the calibration as a native Windows MHC2 profile and
        // to verify it afterwards. Set by the calibration window; when present, "Apply
        // Profile" does the real install and "Verify" can re-measure through it.
        public sealed record ApplyContext(
            MonitorInfo Monitor, CalibrationTarget Target,
            double[] LutR, double[] LutG, double[] LutB, double WhiteLevel,
            Action<string, string?>? OnInstalled, ColorimeterService? Colorimeter = null,
            bool HdrMode = false,
            CalibrationStateManager? StateManager = null,
            SettingsManager? SettingsManager = null,
            GammaMode PreviousGammaMode = GammaMode.WindowsDefault,
            CalibrationSettings? PreviousSettings = null,
            double PatchSize = 600, double PatchOffsetX = 0, double PatchOffsetY = 0,
            bool CaptureSounds = false,
            string? MeasurementDefaultProfile = null);
        private ApplyContext? _applyContext;
        private bool _profileApplied;
        private bool _measurementInstallBlocked;

        public void SetApplyContext(ApplyContext context)
        {
            _applyContext = context;
            WindowBoundsPersistence.Attach(this, context.SettingsManager, "CalibrationReport");
            RefreshMeasurementValidation();
            UpdateRefineButtonState();

            // The apply context tells us whether a spectral correction is loaded for this
            // monitor, which changes the uncertainty budget's instrument term — recompute
            // (PopulateReport ran before the context existed) and re-snapshot so the saved
            // summary carries the corrected numbers.
            RefreshNativeUncertainty();
            // Guarded on "no verify has run yet" so a late SetApplyContext could never
            // wipe persisted after-verification numbers with an after:null snapshot.
            if (_metrics != null && !_isHistorical && _verifyMeasurements == null)
                PersistReportSummary(after: null);
        }

        /// <summary>
        /// Hands-free mode: apply the profile and run the verification sweep as soon as the
        /// window opens (set by the calibration window so the whole flow needs no clicks).
        /// </summary>
        public bool AutoApplyOnLoad { get; set; }

        /// <summary>
        /// Notes the closed-loop grey-ramp refinement result. This is a much narrower metric
        /// than the accuracy table (1D grey-axis tracking only, measured during calibration
        /// against the GPU-ramp candidate — not through the MHC2 profile), so it's shown as a
        /// footnote rather than as "the" before/after.
        /// </summary>
        public void SetBeforeAfter(double beforeDeltaE, double afterDeltaE, int refinementRounds)
        {
            Vm.BeforeAfterNoteText =
                $"Grey-ramp refinement during calibration: grey-axis tracking {beforeDeltaE:F2} → {afterDeltaE:F2} " +
                $"ΔE after {refinementRounds} pass(es). This 1D check excludes the white-point and gamut " +
                "correction - use Verify above for the full after numbers.";
            Vm.IsBeforeAfterNoteVisible = true;
        }

        /// <summary>
        /// Creates a new CalibrationReportWindow for displaying calibration results.
        /// </summary>
        /// <param name="profile">The calibration profile</param>
        /// <param name="metrics">The calibration metrics</param>
        /// <param name="characterization">The display characterization</param>
        /// <param name="correctionLut">The generated correction LUT</param>
        public CalibrationReportWindow(
            CalibrationProfile profile,
            CalibrationMetrics? metrics = null,
            DisplayCharacterization? characterization = null,
            Lut3D? correctionLut = null,
            IReadOnlyList<MeasurementResult>? measurements = null,
            double? peakWhiteDriftFraction = null,
            bool driftCompensationApplied = false)
        {
            InitializeComponent();
            DataContext = Vm;

            // Standard chrome toggle glyph reflects the current theme; the charts are
            // imperatively drawn, so they must be re-rendered (with the matching palette)
            // whenever the app-wide brutalist palette flips.
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
            BrutalistTheme.Changed += OnThemeChanged;
            Closing += (_, _) => BrutalistTheme.Changed -= OnThemeChanged;

            _profile = profile;
            _metrics = metrics;
            _characterization = characterization;
            _activeCharacterization = characterization;
            _correctionLut = correctionLut;
            _measurements = measurements;
            _peakWhiteDriftFraction = peakWhiteDriftFraction;
            _driftCompensationApplied = driftCompensationApplied;
            _isHistorical = metrics == null && measurements == null;

            PopulateReport();

            // Fresh calibration: snapshot the displayed numbers so the report can be
            // re-opened later from the report browser. Never allowed to break the report.
            if (_metrics != null)
                PersistReportSummary(after: null);

            // Charts need real canvas sizes, which exist only after layout. RenderCharts()
            // redraws the detailed charts too when detailed results exist.
            Loaded += (_, _) => RenderCharts();
            SizeChanged += (_, _) => RenderCharts();

            Loaded += async (_, _) =>
            {
                if (!AutoApplyOnLoad || _applyContext == null || _measurementInstallBlocked) return;
                // async void (event handler): an installer/verify exception escaping here
                // goes straight to the global crash dialog — surface it in the report's
                // status strip instead.
                try
                {
                    // Let the tray's window-closed re-apply land first; the verify bypass then
                    // clears it cleanly instead of racing it.
                    await Task.Delay(600);
                    await ApplyAndVerifyAsync();
                }
                catch (Exception ex)
                {
                    Log.Error($"CalibrationReportWindow: auto apply+verify failed: {ex}");
                    Vm.StatusText = $"Automatic apply failed: {ex.Message}";
                    Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
                }
            };

            // Escape aborts a running verification from this window too, and closing the
            // window mid-sweep cancels it instead of leaving the sweep running headless.
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape && _verifyCts is { IsCancellationRequested: false })
                {
                    e.Handled = true;
                    _verifyCts.Cancel();
                }
            };
            Closing += (_, _) => _verifyCts?.Cancel();
        }

        private void PopulateReport()
        {
            if (_profile == null) return;

            // Header info
            Vm.MonitorNameText = _profile.MonitorName;
            Vm.CalibrationDateText = $"Calibrated: {_profile.LastCalibratedAt?.ToLocalTime():g}";

            // Grade display
            var grade = _profile.QualityGrade ?? _metrics?.GetGrade() ?? CalibrationGrade.C;
            SetGradeDisplay(grade);

            // Summary metrics
            if (_metrics != null)
            {
                Vm.AvgDeltaEText = $"{_metrics.AverageDeltaE:F2}";
                Vm.MaxDeltaEText = $"{_metrics.MaxDeltaE:F2}";
                Vm.GrayscaleDeltaEText = $"{_metrics.AverageGrayscaleDeltaE:F2}";
                Vm.PrimaryDeltaEText = $"{_metrics.AveragePrimaryDeltaE:F2}";

                // Color code the values
                Vm.AvgDeltaEBrush = CalibrationReportViewModel.DeltaEBrush(_metrics.AverageDeltaE);
                Vm.MaxDeltaEBrush = CalibrationReportViewModel.DeltaEBrush(_metrics.MaxDeltaE);
                Vm.GrayscaleDeltaEBrush = CalibrationReportViewModel.DeltaEBrush(_metrics.AverageGrayscaleDeltaE);
                Vm.PrimaryDeltaEBrush = CalibrationReportViewModel.DeltaEBrush(_metrics.AveragePrimaryDeltaE);
            }
            else if (_profile.PostCalibrationDeltaE.HasValue)
            {
                Vm.AvgDeltaEText = $"{_profile.PostCalibrationDeltaE:F2}";
                Vm.AvgDeltaEBrush = CalibrationReportViewModel.DeltaEBrush(_profile.PostCalibrationDeltaE.Value);
            }

            // Summary text
            Vm.SummaryText = GetSummaryText(grade);
            _summaryBaseText = Vm.SummaryText;

            // Measurement uncertainty (1.3): "avg ± expanded" on the headline average,
            // breakdown in the tooltip, and a summary line so it persists into history.
            RefreshNativeUncertainty();

            // Display characteristics
            PopulateDisplayCharacteristics();

            // Primaries comparison
            PopulatePrimariesComparison();

            // Calibration details
            Vm.PatchCountText = _profile.PatchCount.ToString();
            Vm.MeasurementTimeText = _profile.MeasurementTime is { } measurementTime
                ? FormatTimeSpan(measurementTime)
                : "--";
            Vm.ColorimeterText = _profile.ColorimeterModel ?? "Unknown";
            Vm.LutSizeText = $"{_profile.LutSize}x{_profile.LutSize}x{_profile.LutSize}";
            Vm.TargetText = _profile.Target.Name;
            Vm.ProfilePathText = _profile.GetFilePath();

            // Recommendations
            PopulateRecommendations(grade);

            // Status
            UpdateStatus();

            // Historical open: restore the displayed numbers from the saved summary and
            // shut off everything that needs the live calibration session.
            if (_isHistorical)
                ApplyHistoricalPresentation();
        }

        /// <summary>
        /// Presentation for a report re-opened from the saved-reports browser: the accuracy
        /// table and summary come from the persisted ReportSummary, the charts (which need
        /// the raw measurements) give way to a note, and Apply/Verify/White tools stay
        /// disabled because they all require the live apply context and a connected probe.
        /// </summary>
        private void ApplyHistoricalPresentation()
        {
            if (_profile?.ReportSummary is { } s)
            {
                static (string Text, Brush Brush) Fmt(double? v) => v is { } d
                    ? ($"{d:F2}", CalibrationReportViewModel.DeltaEBrush(d))
                    : ("-", CalibrationReportViewModel.DefaultValueBrush);

                (Vm.AvgDeltaEText, Vm.AvgDeltaEBrush) = Fmt(s.AvgDeltaE);
                (Vm.MaxDeltaEText, Vm.MaxDeltaEBrush) = Fmt(s.MaxDeltaE);
                (Vm.GrayscaleDeltaEText, Vm.GrayscaleDeltaEBrush) = Fmt(s.GrayscaleDeltaE);
                (Vm.PrimaryDeltaEText, Vm.PrimaryDeltaEBrush) = Fmt(s.PrimaryDeltaE);
                (Vm.AfterAvgText, Vm.AfterAvgBrush) = Fmt(s.AfterAvgDeltaE);
                (Vm.AfterMaxText, Vm.AfterMaxBrush) = Fmt(s.AfterMaxDeltaE);
                (Vm.AfterGrayscaleText, Vm.AfterGrayscaleBrush) = Fmt(s.AfterGrayscaleDeltaE);
                (Vm.AfterPrimaryText, Vm.AfterPrimaryBrush) = Fmt(s.AfterPrimaryDeltaE);

                if (!string.IsNullOrEmpty(s.GradeScopeLabel))
                    Vm.GradeScopeText = s.GradeScopeLabel;
                if (!string.IsNullOrEmpty(s.SummaryText))
                    Vm.SummaryText = s.SummaryText;

                Vm.VerifyDetailText = s.VerificationDetailText ?? string.Empty;
                Vm.IsVerifyDetailVisible = !string.IsNullOrWhiteSpace(s.VerificationDetailText);
                Vm.PqTrackingDetailText = s.PqTrackingDetailText ?? string.Empty;
                Vm.IsPqTrackingDetailVisible = !string.IsNullOrWhiteSpace(s.PqTrackingDetailText);
                Vm.ColoredHdrDetailText = s.ColoredHdrDetailText ?? string.Empty;
                Vm.IsColoredHdrDetailVisible = !string.IsNullOrWhiteSpace(s.ColoredHdrDetailText);
                _toneMapping = s.ToneMapping;

                // Detailed verification survives into history: the persisted per-patch list
                // is enough to rebuild the histogram, per-patch chart, worst-10 and the
                // category breakdown (unlike the tone/gamut charts, which need raw XYZ).
                if (s.DetailedPatches is { Count: > 0 } detailed)
                {
                    _detailedPatchResults = detailed.Select(d => new PatchDeltaE(
                        d.Name,
                        Enum.TryParse<PatchCategory>(d.Category, out var cat) ? cat : PatchCategory.General,
                        d.DeltaE)).ToList();
                    PresentDetailedResults();
                }
            }

            Vm.AreChartsVisible = false;
            Vm.IsChartsNoteVisible = true;

            Vm.IsApplyEnabled = false;
            Vm.IsVerifyEnabled = false;
            Vm.IsWhiteToolsEnabled = false;

            // These actions all need the live calibration session (apply context, connected
            // probe, freshly-built LUT). None of that survives into a saved report, so hide
            // them outright rather than leaving dead disabled buttons. Export Report and Close
            // still work (rebuilt from the persisted summary).
            ApplyButton.Visibility = Visibility.Collapsed;
            VerifyButton.Visibility = Visibility.Collapsed;
            RefineMenuButton.Visibility = Visibility.Collapsed; // HDR tone/color + white tools all need the live session
            CharacterizeHdrButton.Visibility = Visibility.Collapsed;
            DetailedVerifyCheck.Visibility = Visibility.Collapsed;
            ExportLutMenuItem.IsEnabled = false; // Export LUT: no LUT is persisted (Export Report still works)

            Vm.StatusText = "Saved report. Use Export Report to print or save a PDF.";
            Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
        }

        /// <summary>
        /// Writes the profile (with the displayed accuracy summary baked in) to the saved
        /// reports folder so it can be browsed later. Called once when a fresh report opens
        /// and again after a successful verification sweep (same file). A persistence
        /// failure must never break the report, so everything is swallowed into the log.
        /// </summary>
        /// <summary>
        /// Measurement-uncertainty budget (roadmap 1.3) for a metric computed from
        /// <paramref name="gradedMeasurements"/>. The repeatability inputs (per-patch
        /// read spreads and the luminance-decade noise model) always come from the
        /// CALIBRATION run's measurements — the verify sweep takes single reads, which
        /// by design inherit the run's noise model. Never allowed to break the report:
        /// any failure returns null (no ± shown).
        /// </summary>
        private UncertaintyBudget.Result? ComputeUncertainty(
            IEnumerable<MeasurementResult> gradedMeasurements, CalibrationTarget target)
        {
            if (_measurements == null || _measurements.Count == 0) return null;
            try
            {
                var noiseModel = LuminanceNoiseModel.FromMeasurements(_measurements);

                // Drift term: prefer the run's PERSISTED pre-compensation peak drift
                // (CalibrationResult.PeakWhiteDriftFraction). _measurements have ALREADY been
                // drift-normalized by the orchestrator, so re-analyzing them here is
                // idempotent-to-zero and would silently drop the drift-residual term for
                // every compensated run. Fall back to re-analysis only when the persisted
                // fraction is unavailable (e.g. a historical report re-opened from a saved
                // profile) — there the measurements are whatever the profile carried.
                double? peakDrift;
                bool driftApplied;
                if (_peakWhiteDriftFraction is double persistedPeak)
                {
                    peakDrift = persistedPeak;
                    driftApplied = _driftCompensationApplied;
                }
                else
                {
                    var drift = DriftCompensator.Compensate(_measurements);
                    peakDrift = drift.MaxWhiteDriftFraction;
                    driftApplied = drift.Applied;
                }

                bool hasCorrection = !string.IsNullOrEmpty(_applyContext?.SettingsManager?
                    .GetMonitorProfile(_applyContext.Monitor.MonitorDevicePath)?.MeterCorrectionPath);
                var instrument = UncertaintyBudget.ClassifyInstrument(_profile?.ColorimeterModel, hasCorrection);

                var context = new UncertaintyBudget.Context(
                    instrument, noiseModel, peakDrift, driftApplied);
                CalibrationVerifier.ComputeMetrics(gradedMeasurements, target, context, out var uncertainty);
                return uncertainty;
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationReportWindow: uncertainty budget failed (report continues without it): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Shows the native (before) headline average as "x.xx ± y.yy" with the budget
        /// breakdown in the tooltip, and appends the uncertainty line to the summary so
        /// it persists into the saved report. Recomputed when the apply context arrives
        /// (the CCSS state changes the instrument term).
        /// </summary>
        private void RefreshNativeUncertainty()
        {
            if (_isHistorical || _metrics == null || _profile == null) return;
            var uncertainty = ComputeUncertainty(_measurements ?? Enumerable.Empty<MeasurementResult>(), _profile.Target);
            if (uncertainty == null) return;

            Vm.AvgDeltaEText = $"{_metrics.AverageDeltaE:F2} ± {uncertainty.ExpandedU:F2}";
            Vm.AvgDeltaEToolTip = "Expanded measurement uncertainty: " + uncertainty.Describe();
            if (_summaryBaseText != null)
                Vm.SummaryText = _summaryBaseText + $"\nMeasurement uncertainty on the average ΔE: {uncertainty.Describe()}.";
        }

        private void PersistReportSummary(CalibrationMetrics? after)
        {
            if (_profile == null) return;
            _lastReportSnapshotSaved = false;
            try
            {
                if (after != null)
                    _latestVerificationMetrics = after;
                after ??= _latestVerificationMetrics;

                // Detailed sweep results (when one ran): per-patch list capped at the
                // detailed set size, plus the derived histogram and category breakdown so
                // the saved JSON is self-describing.
                var detailed = _detailedPatchResults;
                CategoryBreakdown? breakdown = detailed != null
                    ? VerificationAnalysis.ComputeCategoryBreakdown(detailed)
                    : null;

                _profile.ReportSummary = new CalibrationReportSummary
                {
                    AvgDeltaE = _metrics?.AverageDeltaE,
                    MaxDeltaE = _metrics?.MaxDeltaE,
                    GrayscaleDeltaE = _metrics?.AverageGrayscaleDeltaE,
                    PrimaryDeltaE = _metrics?.AveragePrimaryDeltaE,
                    AfterAvgDeltaE = after?.AverageDeltaE,
                    AfterMaxDeltaE = after?.MaxDeltaE,
                    AfterGrayscaleDeltaE = after?.AverageGrayscaleDeltaE,
                    AfterPrimaryDeltaE = after?.AveragePrimaryDeltaE,
                    GradeScopeLabel = Vm.GradeScopeText,
                    SummaryText = Vm.SummaryText,
                    DetailedPatches = detailed?
                        .Take(VerificationPatchSets.DetailedPatchCount)
                        .Select(p => new VerifiedPatchResult
                        {
                            Name = p.Name,
                            Category = p.Category.ToString(),
                            DeltaE = Math.Round(p.DeltaE, 3),
                        })
                        .ToList(),
                    DetailedHistogram = detailed != null
                        ? VerificationAnalysis.HistogramCounts(detailed.Select(p => p.DeltaE))
                        : null,
                    DetailedGrayscaleDeltaE = breakdown?.GrayscaleDeltaE,
                    DetailedPrimariesDeltaE = breakdown?.PrimariesDeltaE,
                    DetailedSaturationDeltaE = breakdown?.SaturationDeltaE,
                    DetailedMemoryColorsDeltaE = breakdown?.MemoryColorsDeltaE,
                    VerificationDetailText = Vm.IsVerifyDetailVisible ? Vm.VerifyDetailText : null,
                    PqTrackingDetailText = Vm.IsPqTrackingDetailVisible ? Vm.PqTrackingDetailText : null,
                    ColoredHdrDetailText = Vm.IsColoredHdrDetailVisible ? Vm.ColoredHdrDetailText : null,
                    ToneMapping = _toneMapping,
                };

                if (_reportSavePath == null)
                {
                    string safeName = string.Join("_", _profile.MonitorName.Split(Path.GetInvalidFileNameChars()));
                    _reportSavePath = Path.Combine(
                        CalibrationProfile.GetReportsDirectory(),
                        $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                }
                _profile.SaveToFile(_reportSavePath);
                PersistRawMeasurementCsvs();
                _lastReportSnapshotSaved = true;
                Log.Info($"CalibrationReportWindow: report snapshot saved to {_reportSavePath}");
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationReportWindow: report snapshot save failed (report unaffected): {ex.Message}");
            }
        }

        private void PersistRawMeasurementCsvs()
        {
            if (_profile == null || string.IsNullOrEmpty(_reportSavePath)) return;

            string reportDir = Path.GetDirectoryName(_reportSavePath) ?? CalibrationProfile.GetReportsDirectory();
            string measurementDir = Path.Combine(reportDir, "measurements");
            string baseName = Path.GetFileNameWithoutExtension(_reportSavePath);
            string reportId = _profile.Id.ToString();

            if (_measurements is { Count: > 0 })
            {
                MeasurementCsvExporter.Save(
                    Path.Combine(measurementDir, $"{baseName}_native-measurements.csv"),
                    reportId,
                    "native",
                    _measurements);
            }

            if (_verifyMeasurements is { Count: > 0 })
            {
                MeasurementCsvExporter.Save(
                    Path.Combine(measurementDir, $"{baseName}_verification-measurements.csv"),
                    reportId,
                    "verification",
                    _verifyMeasurements);
            }
        }

        private void SetGradeDisplay(CalibrationGrade grade)
        {
            string gradeText = grade switch
            {
                CalibrationGrade.APLus => "A+",
                CalibrationGrade.A => "A",
                CalibrationGrade.AMinus => "A-",
                CalibrationGrade.BPlus => "B+",
                CalibrationGrade.B => "B",
                CalibrationGrade.BMinus => "B-",
                CalibrationGrade.CPlus => "C+",
                CalibrationGrade.C => "C",
                CalibrationGrade.CMinus => "C-",
                CalibrationGrade.D => "D",
                CalibrationGrade.F => "F",
                _ => "?"
            };

            Vm.GradeText = gradeText;

            // Set color based on grade
            var (gradeBrush, gradeBackground) = grade switch
            {
                CalibrationGrade.APLus or CalibrationGrade.A or CalibrationGrade.AMinus =>
                    (CalibrationReportViewModel.GreenBrush, CalibrationReportViewModel.GreenBackgroundBrush),
                CalibrationGrade.BPlus or CalibrationGrade.B or CalibrationGrade.BMinus =>
                    (CalibrationReportViewModel.BlueBrush, CalibrationReportViewModel.BlueBackgroundBrush),
                CalibrationGrade.CPlus or CalibrationGrade.C or CalibrationGrade.CMinus =>
                    (CalibrationReportViewModel.OrangeBrush, CalibrationReportViewModel.OrangeBackgroundBrush),
                CalibrationGrade.D =>
                    (CalibrationReportViewModel.AmberBrush, CalibrationReportViewModel.AmberBackgroundBrush),
                _ =>
                    (CalibrationReportViewModel.RedBrush, CalibrationReportViewModel.RedBackgroundBrush)
            };

            Vm.GradeBrush = gradeBrush;
            Vm.GradeBackground = gradeBackground;
        }

        private string GetSummaryText(CalibrationGrade grade)
        {
            return grade switch
            {
                CalibrationGrade.APLus or CalibrationGrade.A =>
                    "Excellent calibration! Your display is performing at reference-level accuracy. Color reproduction is virtually indistinguishable from the target.",

                CalibrationGrade.AMinus or CalibrationGrade.BPlus =>
                    "Very good calibration. Your display shows excellent color accuracy suitable for color-critical work. Minor variations may exist in edge cases.",

                CalibrationGrade.B or CalibrationGrade.BMinus =>
                    "Good calibration. Your display shows solid color accuracy for most professional and creative work. Some deviation may be noticeable in critical comparisons.",

                CalibrationGrade.CPlus or CalibrationGrade.C =>
                    "Acceptable calibration. Your display shows reasonable color accuracy for general use. Some color shifts may be visible in side-by-side comparisons.",

                CalibrationGrade.CMinus or CalibrationGrade.D =>
                    "Marginal calibration. Your display shows noticeable color inaccuracies. Consider re-calibrating or checking display settings.",

                _ =>
                    "Poor calibration. Significant color errors were detected. Please check your display settings, colorimeter placement, and try calibrating again."
            };
        }

        private void PopulateDisplayCharacteristics()
        {
            if (_characterization != null)
            {
                Vm.PeakLuminanceText = FormatNits(_characterization.PeakLuminance, "F1");
                Vm.BlackLevelText = FormatNits(_characterization.BlackLevel, "F4");
                Vm.ContrastRatioText = FormatContrastRatio(_characterization.ContrastRatio);
                Vm.MeasuredGammaText = FormatNumber(_characterization.MeasuredGamma, "F2");

                double cct = ColorMath.ChromaticityToCct(_characterization.WhitePoint);
                double duv = ColorMath.CalculateDuv(_characterization.WhitePoint);
                Vm.WhitePointCctText = double.IsFinite(cct) ? $"{cct:F0} K" : "-- K";
                Vm.WhitePointDuvText = FormatNumber(duv, "F4");
            }
            else if (_profile?.MeasuredCharacteristics != null)
            {
                var mc = _profile.MeasuredCharacteristics;
                Vm.PeakLuminanceText = FormatNits(mc.PeakLuminance, "F1");
                Vm.BlackLevelText = FormatNits(mc.BlackLevel, "F4");
                Vm.ContrastRatioText = FormatContrastRatio(mc.ContrastRatio);
                Vm.MeasuredGammaText = FormatNumber(mc.MeasuredGamma, "F2");
                Vm.WhitePointCctText = double.IsFinite(mc.MeasuredCct) ? $"{mc.MeasuredCct:F0} K" : "-- K";
                Vm.WhitePointDuvText = FormatNumber(mc.MeasuredDuv, "F4");
            }

            // sRGB coverage from the measured primaries (CIE xy): area of the measured
            // gamut triangle intersected with the sRGB triangle, over the sRGB area.
            // Works for live reports (characterization) and historical ones (the
            // measured characteristics round-trip through the report JSON).
            (Chromaticity R, Chromaticity G, Chromaticity B)? primaries = _characterization != null
                ? (_characterization.RedPrimary, _characterization.GreenPrimary, _characterization.BluePrimary)
                : _profile?.MeasuredCharacteristics is { } chars
                    ? (chars.MeasuredRed, chars.MeasuredGreen, chars.MeasuredBlue)
                    : null;
            Vm.SrgbCoverageText = primaries is { } p && double.IsFinite(ColorMath.GamutCoverage(p.R, p.G, p.B))
                ? $"{ColorMath.GamutCoverage(p.R, p.G, p.B) * 100:F1}%"
                : "--";
        }

        private void PopulatePrimariesComparison()
        {
            var target = _applyContext != null ? EffectiveTarget(_applyContext) : _profile?.Target;
            if (target == null) return;

            // Target primaries
            Vm.RedTargetText = FormatChromaticity(target.RedPrimary);
            Vm.GreenTargetText = FormatChromaticity(target.GreenPrimary);
            Vm.BlueTargetText = FormatChromaticity(target.BluePrimary);
            Vm.WhiteTargetText = FormatChromaticity(target.WhitePoint);

            // Measured primaries
            if (_characterization != null)
            {
                Vm.RedMeasuredText = FormatChromaticity(_characterization.RedPrimary);
                Vm.GreenMeasuredText = FormatChromaticity(_characterization.GreenPrimary);
                Vm.BlueMeasuredText = FormatChromaticity(_characterization.BluePrimary);
                Vm.WhiteMeasuredText = FormatChromaticity(_characterization.WhitePoint);

                Vm.RedErrorText = FormatError(_characterization.RedPrimary, target.RedPrimary);
                Vm.GreenErrorText = FormatError(_characterization.GreenPrimary, target.GreenPrimary);
                Vm.BlueErrorText = FormatError(_characterization.BluePrimary, target.BluePrimary);
                Vm.WhiteErrorText = FormatError(_characterization.WhitePoint, target.WhitePoint);
            }
            else if (_profile?.MeasuredCharacteristics != null)
            {
                var mc = _profile.MeasuredCharacteristics;
                Vm.RedMeasuredText = FormatChromaticity(mc.MeasuredRed);
                Vm.GreenMeasuredText = FormatChromaticity(mc.MeasuredGreen);
                Vm.BlueMeasuredText = FormatChromaticity(mc.MeasuredBlue);
                Vm.WhiteMeasuredText = FormatChromaticity(mc.MeasuredWhite);

                Vm.RedErrorText = FormatError(mc.MeasuredRed, target.RedPrimary);
                Vm.GreenErrorText = FormatError(mc.MeasuredGreen, target.GreenPrimary);
                Vm.BlueErrorText = FormatError(mc.MeasuredBlue, target.BluePrimary);
                Vm.WhiteErrorText = FormatError(mc.MeasuredWhite, target.WhitePoint);
            }
        }

        private static string FormatChromaticity(Chromaticity c)
        {
            return double.IsFinite(c.X) && double.IsFinite(c.Y)
                ? $"({c.X:F3}, {c.Y:F3})"
                : "(--, --)";
        }

        private static string FormatError(Chromaticity measured, Chromaticity target)
        {
            double error = measured.DistanceTo(target);
            return FormatNumber(error, "F4");
        }

        private static string FormatNumber(double value, string format) =>
            double.IsFinite(value) ? value.ToString(format) : "--";

        private static string FormatNits(double value, string format) =>
            double.IsFinite(value) ? $"{value.ToString(format)} cd/m\u00B2" : $"-- cd/m\u00B2";

        private static string FormatContrastRatio(double value)
        {
            if (double.IsPositiveInfinity(value) || value > 100000) return "Infinite";
            return double.IsFinite(value) && value >= 0.0 ? $"{value:F0}:1" : "--:1";
        }

        private void PopulateRecommendations(CalibrationGrade grade, CalibrationMetrics? verified = null)
        {
            var recommendations = new List<string>();

            // Verify-aware guidance beats generic advice: once we have measured-after data,
            // lead with what it actually says.
            if (verified != null)
            {
                if (_metrics != null && verified.AverageDeltaE > _metrics.AverageDeltaE + 0.3
                    && _profile?.Target.WhitePointOnly != true)
                {
                    recommendations.Add(
                        $"Verified accuracy ({verified.AverageDeltaE:F2}) came back worse than native ({_metrics.AverageDeltaE:F2}) - " +
                        "this panel is already inside the correction's noise floor. Recalibrate with " +
                        "\"White point correction only\", or run without a profile and use a small visual trim.");
                }

                double tone = verified.AverageGrayscaleToneDeltaE;
                double color = verified.AverageGrayscaleColorDeltaE;
                if (color > 1.0 && color > tone * 1.5)
                {
                    recommendations.Add(
                        $"Remaining grayscale error is mostly CHROMATIC (color {color:F2} vs tone {tone:F2}) - a visible cast. " +
                        "Check the meter spectral correction matches this panel; a small Tint trim can finish the job.");
                }
                else if (tone > 1.0 && tone > color * 1.5)
                {
                    recommendations.Add(
                        $"Remaining grayscale error is mostly TONE-AXIS (tone {tone:F2} vs color {color:F2}), typically " +
                        "concentrated near black where colorimeter accuracy is poorest - much of this is instrument noise, " +
                        "not visible error.");
                }
            }

            if (grade <= CalibrationGrade.A)
            {
                recommendations.Add("Your display is calibrated to professional standards.");
                recommendations.Add("Re-calibrate every 2-4 weeks to maintain accuracy.");
            }
            else if (grade <= CalibrationGrade.B)
            {
                recommendations.Add("Consider recalibrating with more patches for improved accuracy.");
                recommendations.Add("Ensure room lighting is consistent during calibration.");
                recommendations.Add("Re-calibrate every 2-4 weeks to maintain accuracy.");
            }
            else if (grade <= CalibrationGrade.C)
            {
                recommendations.Add("Check that your display has warmed up for at least 30 minutes.");
                recommendations.Add("Verify the colorimeter is properly positioned on the display.");
                recommendations.Add("Try using a larger patch set for more accurate profiling.");
            }
            else
            {
                recommendations.Add("Verify your colorimeter is properly connected and positioned.");
                recommendations.Add("Check display settings (reset to factory defaults if possible).");
                recommendations.Add("Ensure ambient light is minimized during calibration.");
                recommendations.Add("Consider using full-screen mode for better measurement accuracy.");
            }

            // The hard-won rule of this project: once the panel already measures inside the
            // system's noise + nonlinearity floor, full gamut correction has nothing real to
            // fix and verification tends to come back WORSE (seen on both test panels).
            // (_profile.Target carries the WhitePointOnly flag; _applyContext isn't set yet
            // when this runs from the constructor.)
            if (_metrics != null && _metrics.AverageDeltaE < 2.5 && _profile?.Target.WhitePointOnly != true)
            {
                recommendations.Add(
                    $"This panel already measures close to target natively (avg ΔE {_metrics.AverageDeltaE:F2}). " +
                    "Full gamut correction usually can't improve that and may verify worse - try the " +
                    "\"White point correction only\" option in calibration setup instead.");
            }

            if (_characterization != null)
            {
                double cct = ColorMath.ChromaticityToCct(_characterization.WhitePoint);
                if (cct < 6000 || cct > 7000)
                {
                    recommendations.Add(
                        $"Native white point ({cct:F0}K) sits well off D65. The installed profile corrects this " +
                        "digitally; setting the monitor's OSD color temperature closer to 6500K instead preserves " +
                        "more brightness and bit depth.");
                }

                double duv = ColorMath.CalculateDuv(_characterization.WhitePoint);
                if (Math.Abs(duv) > 0.01)
                {
                    string tint = duv > 0 ? "green" : "magenta";
                    recommendations.Add($"White point has a slight {tint} tint (Duv={duv:F3}). Adjust OSD tint if available.");
                }
            }

            Vm.Recommendations.Clear();
            foreach (var recommendation in recommendations)
                Vm.Recommendations.Add(recommendation);
        }

        private void UpdateStatus()
        {
            if (_profile?.IsActive == true)
            {
                Vm.StatusText = "Profile is active and applied.";
                Vm.StatusBrush = CalibrationReportViewModel.GreenBrush;
                Vm.ApplyButtonContent = "Re-apply Profile";
            }
            else
            {
                Vm.StatusText = "Profile is not currently active.";
                Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
                Vm.ApplyButtonContent = "Apply Profile";
            }
        }

        private bool RefreshMeasurementValidation()
        {
            if (_isHistorical || _applyContext == null || _measurements == null)
            {
                _measurementInstallBlocked = false;
                Vm.IsMeasurementValidationVisible = false;
                return true;
            }

            var result = CalibrationMeasurementValidator.ValidateForProfile(
                _measurements,
                EffectiveTarget(_applyContext),
                _applyContext.HdrMode);

            Vm.IsMeasurementValidationVisible = true;
            Vm.MeasurementValidationText = CalibrationMeasurementValidator.BuildRecoveryText(result);

            if (result.IsValid)
            {
                _measurementInstallBlocked = false;
                Vm.MeasurementValidationBrush = CalibrationReportViewModel.GreenBrush;
                return true;
            }

            _measurementInstallBlocked = true;
            Vm.MeasurementValidationBrush = CalibrationReportViewModel.RedBrush;
            Vm.IsApplyEnabled = false;
            Vm.StatusText = "Apply blocked: measurement validation failed.";
            Vm.StatusBrush = CalibrationReportViewModel.RedBrush;
            Vm.ApplyButtonContent = "Apply Blocked";
            return false;
        }

        private static string FormatTimeSpan(TimeSpan time)
        {
            if (time.TotalHours >= 1)
                return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
            return $"{time.Minutes}:{time.Seconds:D2}";
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_correctionLut == null && _profile?.CorrectionLut == null)
            {
                ConfirmDialog.Info(this, "Export Error", "No LUT data available to export.");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Export 3D LUT",
                Filter = "Adobe Cube LUT (*.cube)|*.cube|All Files (*.*)|*.*",
                DefaultExt = ".cube",
                FileName = $"{_profile?.MonitorName}_calibration.cube"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lut = _correctionLut ?? _profile?.CorrectionLut;
                    lut?.SaveAsCube(dialog.FileName, $"{_profile?.MonitorName} Calibration LUT");

                    ConfirmDialog.Info(this, "Export Complete",
                        $"LUT exported successfully to:\n{dialog.FileName}");
                }
                catch (Exception ex)
                {
                    ConfirmDialog.Info(this, "Export Error", $"Failed to export LUT: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Prints the report (users pick "Microsoft Print to PDF" to save a PDF) as a
        /// dedicated LIGHT, printer-friendly layout built by <see cref="ReportPrintBuilder"/>
        /// from the same view-model strings the window displays - NOT a screenshot of the
        /// dark UI. The charts (live reports only) are re-rendered offscreen with the light
        /// chart palette at print size and embedded as figures; FlowDocument's paginator
        /// handles the page breaks.
        /// </summary>
        private void ExportReportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Controls.PrintDialog();
            if (dialog.ShowDialog() != true) return;

            try
            {
                // Chart figures exist only in the live report; historical reports never
                // render the charts (no raw measurements), so the section becomes a note.
                // The on-screen canvases are NOT captured: the figures are drawn fresh into
                // offscreen canvases with the light palette so they sit on white paper.
                List<ReportPrintBuilder.ChartFigure>? charts = null;
                if (!_isHistorical && ToneCanvas.Children.Count > 0)
                {
                    var tone = ReportPrintBuilder.CreatePrintCanvas();
                    var gamma = ReportPrintBuilder.CreatePrintCanvas();
                    var balance = ReportPrintBuilder.CreatePrintCanvas();
                    var gamut = ReportPrintBuilder.CreatePrintCanvas();
                    RenderCharts(tone, gamma, balance, gamut, CalibrationCharts.ChartPalette.Light);

                    var figures = new List<ReportPrintBuilder.ChartFigure>();
                    void Capture(string title, System.Windows.Controls.Canvas canvas)
                    {
                        if (canvas.Children.Count > 0)
                            figures.Add(new ReportPrintBuilder.ChartFigure(
                                title, ReportPrintBuilder.RenderPrintCanvas(canvas)));
                    }
                    Capture("Tone Response", tone);
                    Capture("Gamma", gamma);
                    Capture("RGB Balance", balance);
                    Capture("Gamut", gamut);
                    if (figures.Count > 0) charts = figures;
                }

                // Detailed verification prints whenever its data exists - including for
                // historical reports, where the per-patch list persists in the summary and
                // the charts are simply re-rendered from it (light palette, print size).
                ReportPrintBuilder.DetailedPrintSection? detailedSection = null;
                if (_detailedPatchResults is { Count: > 0 } detailedResults)
                {
                    var histogram = ReportPrintBuilder.CreatePrintCanvas();
                    var perPatch = ReportPrintBuilder.CreatePrintCanvas();
                    RenderDetailedCharts(histogram, perPatch, CalibrationCharts.ChartPalette.Light);

                    var detailedFigures = new List<ReportPrintBuilder.ChartFigure>
                    {
                        new("Delta E Distribution", ReportPrintBuilder.RenderPrintCanvas(histogram)),
                        new("Per-patch Delta E", ReportPrintBuilder.RenderPrintCanvas(perPatch)),
                    };
                    detailedSection = new ReportPrintBuilder.DetailedPrintSection(
                        detailedFigures,
                        VerificationAnalysis.WorstPatches(detailedResults),
                        VerificationAnalysis.BestPatches(detailedResults),
                        BuildCategoryBreakdownText(detailedResults));
                }

                var document = ReportPrintBuilder.Build(Vm, _isHistorical, charts, detailedSection);

                const double margin = 48;
                document.PageWidth = dialog.PrintableAreaWidth;
                document.PageHeight = dialog.PrintableAreaHeight;
                document.PagePadding = new Thickness(margin);
                // Single column: without this, FlowDocument breaks a wide page into
                // newspaper columns.
                document.ColumnWidth = Math.Max(dialog.PrintableAreaWidth - margin * 2, 1);

                // The job name doubles as Microsoft Print to PDF's suggested filename,
                // so make it descriptive and strip filename-hostile characters.
                string jobName = $"Calibration Report - {_profile?.MonitorName ?? "Display"} - " +
                                 $"{(_profile?.LastCalibratedAt?.ToLocalTime() ?? DateTime.Now):yyyy-MM-dd}";
                foreach (char c in Path.GetInvalidFileNameChars())
                    jobName = jobName.Replace(c, '_');

                dialog.PrintDocument(
                    ((System.Windows.Documents.IDocumentPaginatorSource)document).DocumentPaginator,
                    jobName);
                Vm.StatusText = "Report sent to the printer.";
            }
            catch (Exception ex)
            {
                // PTS (the FlowDocument pagination engine) surfaces native failures as
                // non-CLS exceptions, which the CLR delivers wrapped. Unwrap and log the
                // FULL exception so the next failure pinpoints the faulting element.
                if (ex is System.Runtime.CompilerServices.RuntimeWrappedException rwe)
                {
                    Log.Error("CalibrationReportWindow: print failed with RuntimeWrappedException. " +
                              $"Wrapped: {rwe.WrappedException?.ToString() ?? "<null>"}\nOuter: {ex}");
                }
                else
                {
                    Log.Error($"CalibrationReportWindow: print failed: {ex}");
                }
                ConfirmDialog.Info(this, "Export Report", $"Could not print the report:\n\n{ex.Message}");
            }
        }

        /// <summary>
        /// Draws the on-screen charts with the dark report theme: the four main charts plus,
        /// when detailed results exist, the two detailed charts - so every layout change
        /// (Loaded, SizeChanged) and every verify completion regenerates all of them together.
        /// </summary>
        private void RenderCharts()
        {
            RenderCharts(ToneCanvas, GammaCanvas, BalanceCanvas, GamutCanvas, OnScreenPalette);
            RenderDetailedCharts();
        }

        /// <summary>The chart palette matching the current app theme (light or dark).</summary>
        private static CalibrationCharts.ChartPalette OnScreenPalette =>
            BrutalistTheme.IsDark ? CalibrationCharts.ChartPalette.Dark : CalibrationCharts.ChartPalette.Light;

        /// <summary>
        /// App palette flipped: refresh the toggle glyph and re-draw the imperatively-rendered
        /// charts with the matching palette (they otherwise keep the palette baked in at their
        /// last draw).
        /// </summary>
        private void OnThemeChanged()
        {
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
            RenderCharts();
        }

        /// <summary>
        /// Draws all four report charts into the given canvases with the given palette.
        /// The window calls this with its own canvases and the dark palette; the print
        /// export calls it again with fresh offscreen canvases and the light palette.
        /// </summary>
        private void RenderCharts(
            System.Windows.Controls.Canvas toneCanvas,
            System.Windows.Controls.Canvas gammaCanvas,
            System.Windows.Controls.Canvas balanceCanvas,
            System.Windows.Controls.Canvas gamutCanvas,
            CalibrationCharts.ChartPalette palette)
        {
            if (_characterization == null) return;
            var target = _profile?.Target;
            // For PQ (HDR) targets the measured patches are SDR content rendered by Windows
            // with the sRGB curve — plotting the PQ EOTF against SDR-content signals produced
            // a nonsense S-curve "target". Grade and draw against the sRGB content curve.
            bool pqTarget = target?.TransferFunction == TransferFunctionType.Pq;
            double targetGamma = pqTarget ? 2.2 : (target?.Gamma ?? 2.2);
            string targetLabel = pqTarget ? "Target (sRGB content)" : "Target";
            const int N = 41;

            double MeasuredOut(ToneCurve? curve, double v) => curve?.Lookup(v) ?? v;
            double TargetOut(double v) => pqTarget
                ? ColorMath.SrgbEotf(v)
                : (target?.ApplyEotf(v) ?? Math.Pow(v, targetGamma));

            // The raw measured grayscale steps (when available): the fitted curve plus real
            // data points, instead of a model line pretending to be measurements.
            var grays = _measurements?
                .Where(m => m.IsValid && m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R)
                .ToList();
            double grayMinY = 0, grayRangeY = 1;
            if (grays is { Count: > 1 })
            {
                grayMinY = grays.Min(m => m.Xyz.Y);
                grayRangeY = Math.Max(grays.Max(m => m.Xyz.Y) - grayMinY, 1e-6);
            }
            double NormY(MeasurementResult m) => Math.Clamp((m.Xyz.Y - grayMinY) / grayRangeY, 0, 1);

            // 1. Tone response: target, fitted curve, and the measured points. (The per-channel
            // tone curves are all fitted from the same grayscale luminance, so a single fit
            // line is honest — per-channel behavior lives in the RGB balance chart.)
            var tRef = new List<(double, double)>();
            var tFit = new List<(double, double)>();
            for (int i = 0; i < N; i++)
            {
                double v = i / (N - 1.0);
                tRef.Add((v, TargetOut(v)));
                tFit.Add((v, MeasuredOut(_characterization.RedToneCurve, v)));
            }
            // Corrected (verified) grayscale, when a verify pass has run — the proof that the
            // scary-looking native curves were actually fixed.
            var corrected = _verifyMeasurements?
                .Where(m => m.IsValid && m.Patch.Category == PatchCategory.Grayscale)
                .OrderBy(m => m.Patch.DisplayRgb.R)
                .ToList();
            double corrMinY = 0, corrRangeY = 1;
            if (corrected is { Count: > 1 })
            {
                corrMinY = corrected.Min(m => m.Xyz.Y);
                corrRangeY = Math.Max(corrected.Max(m => m.Xyz.Y) - corrMinY, 1e-6);
            }
            double CorrNormY(MeasurementResult m) => Math.Clamp((m.Xyz.Y - corrMinY) / corrRangeY, 0, 1);
            var corrColor = palette.Green;

            var toneSeries = new List<CalibrationCharts.Series>
            {
                new(targetLabel, palette.Neutral, tRef, Dashed: true),
                new("Panel (fit)", palette.Cyan, tFit),
            };
            if (grays is { Count: > 1 })
                toneSeries.Add(new CalibrationCharts.Series("Measured", palette.Orange,
                    grays.Select(m => (m.Patch.DisplayRgb.R, NormY(m))).ToList(), Scatter: true));
            if (corrected is { Count: > 1 })
            {
                var pts = corrected.Select(m => (m.Patch.DisplayRgb.R, CorrNormY(m))).ToList();
                toneSeries.Add(new CalibrationCharts.Series("Corrected", corrColor, pts));
                toneSeries.Add(new CalibrationCharts.Series("", corrColor, pts, Scatter: true));
            }
            CalibrationCharts.DrawLineChart(toneCanvas, toneSeries, 0, 1, 0, 1,
                "Input signal", "Output (relative)", palette: palette);

            // 2. Gamma tracking (fit line + per-point measured gamma) vs the TARGET's
            // effective gamma curve - a flat "2.2" reference is wrong for sRGB-curve targets
            // (piecewise sRGB runs ~1.9–2.1 effective in the shadows), which made honest
            // tracking read as error. log(out)/log(in) is numerically unstable as input → 1
            // (the fit line used to nosedive at the right edge) — evaluate only [0.05, 0.95].
            var gammaMeas = new List<(double, double)>();
            var gammaRef = new List<(double, double)>();
            for (int i = 1; i < N; i++)
            {
                double v = i / (N - 1.0);
                if (v is < 0.05 or > 0.95) continue;
                double refOut = TargetOut(v);
                if (refOut > 0) gammaRef.Add((v, Math.Log(refOut) / Math.Log(v)));
                double outv = MeasuredOut(_characterization.RedToneCurve, v);
                if (outv > 0) gammaMeas.Add((v, Math.Log(outv) / Math.Log(v)));
            }
            var gammaSeries = new List<CalibrationCharts.Series>
            {
                new("Target (effective)", palette.Neutral, gammaRef, Dashed: true),
                new("Fit", palette.Cyan, gammaMeas),
            };
            var gammaScatter = new List<(double, double)>();
            if (grays is { Count: > 1 })
            {
                gammaScatter = grays
                    .Where(m => m.Patch.DisplayRgb.R is >= 0.05 and <= 0.95 && NormY(m) > 0)
                    .Select(m => (m.Patch.DisplayRgb.R, Math.Log(NormY(m)) / Math.Log(m.Patch.DisplayRgb.R)))
                    .ToList();
                gammaSeries.Add(new CalibrationCharts.Series("Measured", palette.Orange,
                    gammaScatter, Scatter: true));
            }
            if (corrected is { Count: > 1 })
            {
                var corrGamma = corrected
                    .Where(m => m.Patch.DisplayRgb.R is >= 0.05 and <= 0.95 && CorrNormY(m) > 0)
                    .Select(m => (m.Patch.DisplayRgb.R, Math.Log(CorrNormY(m)) / Math.Log(m.Patch.DisplayRgb.R)))
                    .ToList();
                gammaSeries.Add(new CalibrationCharts.Series("Corrected", corrColor, corrGamma));
                gammaSeries.Add(new CalibrationCharts.Series("", corrColor, corrGamma, Scatter: true));
                gammaScatter = gammaScatter.Concat(corrGamma).ToList(); // include in auto-range
            }
            // Auto-range so a deep tone-mapping rolloff (HDR knee) shows as data, not as a
            // suspicious flatline pinned to the chart floor.
            double gMin = 2.8, gMax = 1.6;
            foreach (var (_, gy) in gammaRef.Concat(gammaMeas).Concat(gammaScatter))
            {
                gMin = Math.Min(gMin, gy);
                gMax = Math.Max(gMax, gy);
            }
            gMin = Math.Floor(Math.Clamp(gMin - 0.1, 0.8, 2.0) * 10) / 10;
            gMax = Math.Ceiling(Math.Clamp(gMax + 0.1, 2.4, 3.2) * 10) / 10;
            CalibrationCharts.DrawLineChart(gammaCanvas, gammaSeries, 0, 1, gMin, gMax,
                "Input signal", "Gamma", palette: palette);

            // 3. Grayscale RGB balance from the MEASURED XYZ of each gray step: each channel's
            // linear contribution relative to neutral (1.0 = no cast). The old version plotted
            // the three fitted tone curves, which are identical by construction — they overlap
            // exactly and only the last-drawn series was visible.
            var balanceSeries = new List<CalibrationCharts.Series>
            {
                new("Neutral", palette.Neutral, new List<(double, double)> { (0, 1), (1, 1) }, Dashed: true),
            };
            if (grays is { Count: > 1 } && _characterization.RgbToXyzMatrix != null)
            {
                var inv = ColorMath.Invert3x3(_characterization.RgbToXyzMatrix);
                double[] ChannelLin(MeasurementResult m) => new[]
                {
                    inv[0, 0] * m.Xyz.X + inv[0, 1] * m.Xyz.Y + inv[0, 2] * m.Xyz.Z,
                    inv[1, 0] * m.Xyz.X + inv[1, 1] * m.Xyz.Y + inv[1, 2] * m.Xyz.Z,
                    inv[2, 0] * m.Xyz.X + inv[2, 1] * m.Xyz.Y + inv[2, 2] * m.Xyz.Z,
                };
                var white = grays.OrderByDescending(m => m.Xyz.Y).First();
                double[] whiteLin = ChannelLin(white);
                var balR = new List<(double, double)>(); var balG = new List<(double, double)>(); var balB = new List<(double, double)>();
                foreach (var m in grays.Where(m => m.Patch.DisplayRgb.R >= 0.08))
                {
                    double[] lin = ChannelLin(m);
                    if (whiteLin.Any(w => w <= 1e-6)) continue;
                    double r = lin[0] / whiteLin[0], g = lin[1] / whiteLin[1], b = lin[2] / whiteLin[2];
                    double avg = (r + g + b) / 3.0;
                    if (avg <= 1e-5) continue;
                    double v = m.Patch.DisplayRgb.R;
                    balR.Add((v, r / avg)); balG.Add((v, g / avg)); balB.Add((v, b / avg));
                }
                balanceSeries.Add(new CalibrationCharts.Series("R", palette.BalanceRed, balR));
                balanceSeries.Add(new CalibrationCharts.Series("", palette.BalanceRed, balR, Scatter: true));
                balanceSeries.Add(new CalibrationCharts.Series("G", palette.BalanceGreen, balG));
                balanceSeries.Add(new CalibrationCharts.Series("", palette.BalanceGreen, balG, Scatter: true));
                balanceSeries.Add(new CalibrationCharts.Series("B", palette.BalanceBlue, balB));
                balanceSeries.Add(new CalibrationCharts.Series("", palette.BalanceBlue, balB, Scatter: true));
            }
            CalibrationCharts.DrawLineChart(balanceCanvas, balanceSeries,
                0, 1, 0.85, 1.15, "Input signal", "Gain vs neutral", palette: palette);

            // 4. Gamut + white point on CIE xy.
            if (target != null)
            {
                CalibrationCharts.DrawGamutDiagram(gamutCanvas,
                    (target.RedPrimary.X, target.RedPrimary.Y), (target.GreenPrimary.X, target.GreenPrimary.Y),
                    (target.BluePrimary.X, target.BluePrimary.Y), (target.WhitePoint.X, target.WhitePoint.Y),
                    (_characterization.RedPrimary.X, _characterization.RedPrimary.Y),
                    (_characterization.GreenPrimary.X, _characterization.GreenPrimary.Y),
                    (_characterization.BluePrimary.X, _characterization.BluePrimary.Y),
                    (_characterization.WhitePoint.X, _characterization.WhitePoint.Y),
                    palette);
            }
        }

        /// <summary>
        /// Fills the Detailed Verification section from <see cref="_detailedPatchResults"/>:
        /// worst-10 and best-10 lists, category breakdown text, section visibility and both
        /// charts. Works identically for a fresh detailed sweep and a historical restore.
        /// </summary>
        private void PresentDetailedResults()
        {
            if (_detailedPatchResults is not { Count: > 0 } results) return;

            static void Fill(
                System.Collections.ObjectModel.ObservableCollection<CalibrationReportViewModel.PatchListItem> list,
                IEnumerable<PatchDeltaE> ranked)
            {
                list.Clear();
                int rank = 1;
                foreach (var p in ranked)
                {
                    list.Add(new CalibrationReportViewModel.PatchListItem(
                        $"{rank++}.", p.Name, $"{p.DeltaE:F2}",
                        CalibrationReportViewModel.DeltaEBrush(p.DeltaE)));
                }
            }
            Fill(Vm.WorstPatches, VerificationAnalysis.WorstPatches(results));
            Fill(Vm.BestPatches, VerificationAnalysis.BestPatches(results));

            Vm.CategoryBreakdownText = BuildCategoryBreakdownText(results);
            Vm.HasDetailedResults = true;

            // The card just became visible (BoolToVis), so its canvases have not been laid
            // out yet - drawing now would see ActualWidth 0 and render blank. Defer the draw
            // until after the pending layout pass; later layout changes re-render through
            // the shared RenderCharts path.
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(RenderDetailedCharts));
        }

        /// <summary>
        /// Category breakdown line plus, for HDR sweeps, the honesty caveat: saturation and
        /// memory color patches measure the Windows SDR-to-HDR pipeline and the panel's HDR
        /// color handling, which the (typically white-point-only) correction does not remap.
        /// Without the note those two averages read as calibration failure on wide-gamut HDR
        /// panels. The target carries everything needed (PQ <=> HDR sweep, WhitePointOnly),
        /// so live and restored reports annotate identically.
        /// </summary>
        private string BuildCategoryBreakdownText(IReadOnlyList<PatchDeltaE> results)
        {
            string text = VerificationAnalysis.ComputeCategoryBreakdown(results).ToDisplayText();
            var target = _applyContext != null ? EffectiveTarget(_applyContext) : _profile?.Target;
            if (target != null &&
                VerificationAnalysis.CategoryCaveat(target.IsHdr, target.WhitePointOnly) is { } caveat)
            {
                text += "\n" + caveat;
            }
            return text;
        }

        /// <summary>Draws the on-screen detailed charts with the dark report theme.</summary>
        private void RenderDetailedCharts()
            => RenderDetailedCharts(HistogramCanvas, PerPatchCanvas, OnScreenPalette);

        /// <summary>
        /// Draws the ΔE histogram and per-patch strip chart into the given canvases with the
        /// given palette - same parameterization as the four main charts, so the print export
        /// re-renders them offscreen with the light palette.
        /// </summary>
        private void RenderDetailedCharts(
            System.Windows.Controls.Canvas histogramCanvas,
            System.Windows.Controls.Canvas perPatchCanvas,
            CalibrationCharts.ChartPalette palette)
        {
            if (_detailedPatchResults is not { Count: > 0 } results) return;

            CalibrationCharts.DrawDeltaEHistogram(
                histogramCanvas,
                VerificationAnalysis.HistogramCounts(results.Select(p => p.DeltaE)),
                VerificationAnalysis.HistogramBucketLabels,
                palette);
            CalibrationCharts.DrawPerPatchDeltaE(
                perPatchCanvas,
                results.Select(p => (p.Name, p.DeltaE)).ToList(),
                palette);
        }

        private string? _installedProfileName;
        private bool _profileEnabled;

        // The PQ-domain tone LUTs the last HDR install actually wrote into the profile
        // (with the matrix neutral scale they composed) — the starting point for the
        // closed-loop "Refine HDR" pass, which must refine what is really on the wire.
        private HdrMhc2LutBuilder.Result? _installedHdrLuts;

        // Cumulative XYZ residual correction composed into the last joint HDR install.
        // A fresh open-loop apply has no residual matrix; subsequent joint passes must
        // start from the correction that is actually active rather than fitting identity.
        private double[,] _installedHdrXyzCorrection = HdrColorMatrixLoop.IdentityCorrection();

        /// <summary>
        /// One button, three states: Apply Profile (not yet installed) → Disable Profile
        /// (installed + active) ↔ Enable Profile (installed + toggled off for comparison).
        /// </summary>
        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            // async void (event handler): an installer/preflight/ConfirmDialog exception
            // escaping here goes straight to the global crash dialog — route it to the
            // status strip instead (mirrors RefineHdrButton_Click).
            try
            {
                if (_installedProfileName is { } profileName && _applyContext is { } ctx)
                {
                    if (_profileEnabled)
                    {
                        CalibrationProfileInstaller.Disable(ctx.Monitor, profileName);
                        _profileEnabled = false;
                        Vm.ApplyButtonContent = "Enable Profile";
                        Vm.StatusText = "Profile disabled - showing the uncorrected panel for comparison.";
                    }
                    else if (CalibrationProfileInstaller.Reenable(ctx.Monitor, profileName, ctx.HdrMode))
                    {
                        _profileEnabled = true;
                        Vm.ApplyButtonContent = "Disable Profile";
                        Vm.StatusText = "Profile re-enabled.";
                    }
                    else
                    {
                        Vm.StatusText = "Could not re-enable the profile - close and re-run the calibration.";
                    }
                    UpdateRefineButtonState();
                    return;
                }

                await ApplyAndVerifyAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationReportWindow: apply failed: {ex}");
                Vm.StatusText = $"Apply failed: {ex.Message}";
                Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
            }
        }

        /// <summary>
        /// Installs the measured correction as the monitor's native Windows color profile
        /// (gamut matrix + tone LUTs; in HDR the installer rebuilds the LUTs in PQ wire-signal
        /// domain and associates via the Advanced Color list), then runs the verification
        /// sweep automatically — the probe is still on the display right after a calibration.
        /// </summary>
        /// <summary>The target with the visual white trim applied, when one is set.</summary>
        private CalibrationTarget EffectiveTarget(ApplyContext ctx) =>
            _trimmedTargetWhite is { } w ? ctx.Target.WithWhitePoint(w) : ctx.Target;

        private async Task ApplyAndVerifyAsync(bool runVerify = true)
        {
            if (_applyContext == null || _activeCharacterization == null)
            {
                ConfirmDialog.Info(this, "Apply Profile",
                    "This calibration can't be applied (missing display characterization).");
                return;
            }

            if (!RefreshMeasurementValidation())
            {
                ConfirmDialog.Info(this, "Measurement Validation",
                    Vm.MeasurementValidationText);
                return;
            }

            var ctx = _applyContext;
            Vm.IsApplyEnabled = false;
            try
            {
                Vm.StatusText = "Applying profile…";
                MonitorInfo? installMonitor = ResolveCurrentMonitor(ctx.Monitor);
                string? currentDefaultProfile = installMonitor != null
                    ? CalibrationProfileInstaller.GetCurrentDefaultProfile(installMonitor, ctx.HdrMode)
                    : null;
                var preflightMessages = CalibrationInstallPreflight.BuildMessages(
                    ctx.Monitor,
                    installMonitor,
                    ctx.HdrMode,
                    ctx.WhiteLevel,
                    ctx.MeasurementDefaultProfile,
                    currentDefaultProfile,
                    EffectiveTarget(ctx));

                if (preflightMessages.Any(m => m.Severity == CalibrationInstallPreflight.Error))
                {
                    Vm.StatusText = "Apply blocked by preflight.";
                    ConfirmDialog.Info(this, "Install Preflight",
                        string.Join("\n\n", preflightMessages
                            .Where(m => m.Severity == CalibrationInstallPreflight.Error)
                            .Select(m => m.Message)));
                    return;
                }

                if (preflightMessages.Count > 0)
                {
                    bool continueInstall = ConfirmDialog.Confirm(this, "Install Preflight",
                        string.Join("\n\n", preflightMessages.Select(m => m.Message)) +
                        "\n\nContinue installing this profile?",
                        confirmLabel: "Install",
                        cancelLabel: "Cancel");
                    if (!continueInstall)
                    {
                        Vm.StatusText = "Apply cancelled.";
                        return;
                    }
                }

                installMonitor ??= ctx.Monitor;
                string? previousDefaultProfile = null;
                if (installMonitor.MonitorDevicePath.Length > 0)
                {
                    var saved = ctx.SettingsManager?.GetMonitorProfile(installMonitor.MonitorDevicePath);
                    previousDefaultProfile = CalibrationProfileInstaller.SelectPreviousProfileBackup(
                        currentDefaultProfile ?? CalibrationProfileInstaller.GetCurrentDefaultProfile(installMonitor, ctx.HdrMode),
                        saved?.Mhc2ProfileName,
                        saved?.PreviousColorProfileName);
                }

                var result = CalibrationProfileInstaller.Install(
                    installMonitor, _activeCharacterization, EffectiveTarget(ctx),
                    ctx.LutR, ctx.LutG, ctx.LutB, ctx.WhiteLevel,
                    hdrMode: ctx.HdrMode, measurements: _measurements);

                if (result.Success)
                {
                    _profileApplied = true;
                    _installedProfileName = result.ProfileName;
                    _profileEnabled = true;
                    _installedHdrLuts = result.HdrLuts;
                    _installedHdrXyzCorrection = HdrColorMatrixLoop.IdentityCorrection();
                    Vm.ApplyButtonContent = "Disable Profile";
                    ctx.OnInstalled?.Invoke(result.ProfileName, previousDefaultProfile);

                    if (runVerify && ctx.Colorimeter != null)
                    {
                        Vm.StatusText = "Profile applied - verifying through the correction…";
                        await RunVerificationAsync();
                    }
                    else
                    {
                        Vm.StatusText = "Profile applied.";
                    }
                }
                else
                {
                    Vm.StatusText = "Apply failed.";
                    ConfirmDialog.Info(this, "Apply Failed",
                        $"Could not apply the calibration:\n\n{result.Error}");
                }
            }
            finally
            {
                Vm.IsApplyEnabled = !_measurementInstallBlocked;
                UpdateRefineButtonState();
            }
        }

        private static MonitorInfo? ResolveCurrentMonitor(MonitorInfo measuredMonitor)
        {
            try
            {
                var monitors = new MonitorManager().EnumerateMonitors();
                return monitors.FirstOrDefault(m =>
                           !string.IsNullOrEmpty(m.MonitorDevicePath) &&
                           string.Equals(m.MonitorDevicePath, measuredMonitor.MonitorDevicePath, StringComparison.OrdinalIgnoreCase))
                       ?? monitors.FirstOrDefault(m =>
                           !string.IsNullOrEmpty(m.DeviceName) &&
                           string.Equals(m.DeviceName, measuredMonitor.DeviceName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Info($"CalibrationReportWindow: install preflight monitor refresh failed: {ex.Message}");
                return null;
            }
        }

        private async void WhiteToolsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyContext == null || _activeCharacterization == null)
            {
                ConfirmDialog.Info(this, "White Tools",
                    "White tools need the live calibration context (open this report right after a calibration).");
                return;
            }

            var dialog = new WhiteToolsDialog(
                canMeasure: _applyContext.Colorimeter != null,
                canTrim: _profileApplied,
                canValidateHdr: _applyContext.HdrMode && _applyContext.Colorimeter != null)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true) return;

            // async void (event handler): a failure in any white-tool path (each holds the
            // probe and/or re-installs profiles) must land in the status strip, not the
            // global crash dialog.
            try
            {
                switch (dialog.SelectedAction)
                {
                    case WhiteToolAction.ReanchorWhite:
                        await ReanchorWhiteAsync();
                        break;
                    case WhiteToolAction.VisualTrim:
                        await RunVisualWhiteTrimAsync();
                        break;
                    case WhiteToolAction.ValidateHdrRenderer:
                        await ValidateHdrRendererAsync();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationReportWindow: white tools failed: {ex}");
                Vm.StatusText = $"White tools failed: {ex.Message}";
                Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
            }
        }

        /// <summary>
        /// Probe-certification of the HDR-range patch path (see docs/hdr-patch-renderer-design.md):
        ///  A) the FP16 swapchain at the SDR white level must measure ≈ the SDR window's white
        ///     (same pipeline, same profile application);
        ///  B) at 2× SDR white it must measure well ABOVE the SDR ceiling (the slider must not
        ///     clamp values above 1.0).
        /// Both passing means HDR-range patch sets can be trusted for calibration/verify.
        /// </summary>
        private async Task ValidateHdrRendererAsync()
        {
            if (_applyContext is not { Colorimeter: { } colorimeter } ctx) return;
            if (!ConfirmDialog.Confirm(this, "Validate HDR Renderer",
                    "Gloam will open the probe-positioning surface, then take three short measurements: " +
                    "SDR white, the FP16 renderer at the same level, and the FP16 renderer at double " +
                    "that level (briefly bright).",
                    confirmLabel: "Position probe", cancelLabel: "Cancel"))
                return;

            ProbeOperationScope? probe = null;
            HdrPatchRenderer? renderer = null;
            using var validationCts = new System.Threading.CancellationTokenSource();
            var whitePatch = new ColorPatch { Name = "White", DisplayRgb = new LinearRgb(1, 1, 1), Category = PatchCategory.Grayscale };
            try
            {
                probe = await ProbeOperationScope.StartAsync(new ProbeOperationScope.Options(
                    ctx.Monitor,
                    colorimeter,
                    "HDR renderer validation",
                    HdrMode: true,
                    PatchSize: ctx.PatchSize,
                    PatchOffsetX: ctx.PatchOffsetX,
                    PatchOffsetY: ctx.PatchOffsetY,
                    StateManager: ctx.StateManager,
                    PreviousGammaMode: ctx.PreviousGammaMode,
                    PreviousSettings: ctx.PreviousSettings,
                    EnterBypass: ctx.StateManager != null,
                    EnterBusy: EnterProbeBusy,
                    ExitBusy: ExitProbeBusy,
                    PlacementCommitted: RememberProbePlacement,
                    CancellationToken: validationCts.Token));
                var surround = probe.PatchWindow;

                // 1) SDR baseline through the normal window.
                surround.SetProgress(1, 3, "SDR white (baseline)");
                surround.SetColor(1, 1, 1);
                await Task.Delay(1500, probe.Token);
                var sdr = await colorimeter.MeasureAsync(whitePatch, true, probe.Token);

                // Patch rectangle in pixels (same placement as the surround's patch).
                Int32Rect patchRect = surround.GetPatchPixelRect();

                surround.SetColor(0, 0, 0); // black behind the FP16 window
                renderer = new HdrPatchRenderer(
                    patchRect.X, patchRect.Y, patchRect.Width, patchRect.Height);
                Log.Info($"HDR renderer created; scRGB colorspace support reported: {renderer.ScRgbSupported}");

                // 2) FP16 at the SDR white level - must match the SDR baseline.
                surround.SetProgress(2, 3, "FP16 at SDR white level");
                renderer.PresentNits(sdr.Xyz.Y, sdr.Xyz.Y, sdr.Xyz.Y);
                await Task.Delay(1500, probe.Token);
                var hdrSame = await colorimeter.MeasureAsync(whitePatch, true, probe.Token);

                // 3) FP16 at 2x - must exceed the SDR ceiling (capped below panel peak).
                double targetHigh = Math.Min(sdr.Xyz.Y * 2.0,
                    ctx.Monitor.HdrPeakNits > 50 ? ctx.Monitor.HdrPeakNits * 0.9 : sdr.Xyz.Y * 2.0);
                surround.SetProgress(3, 3, $"FP16 at {targetHigh:F0} nits");
                renderer.PresentNits(targetHigh, targetHigh, targetHigh);
                await Task.Delay(1500, probe.Token);
                var hdrHigh = await colorimeter.MeasureAsync(whitePatch, true, probe.Token);

                // Verdicts.
                double sameRatio = hdrSame.Xyz.Y / Math.Max(sdr.Xyz.Y, 1e-6);
                double dxSame = Math.Abs(hdrSame.Chromaticity.X - sdr.Chromaticity.X);
                double dySame = Math.Abs(hdrSame.Chromaticity.Y - sdr.Chromaticity.Y);
                bool passPipeline = sameRatio > 0.90 && sameRatio < 1.10 && dxSame < 0.006 && dySame < 0.006;
                bool passRange = hdrHigh.Xyz.Y > sdr.Xyz.Y * 1.4;

                string report =
                    $"SDR white baseline:   {sdr.Xyz.Y:F1} nits  ({sdr.Chromaticity.X:F4}, {sdr.Chromaticity.Y:F4})\n" +
                    $"FP16 at same level:   {hdrSame.Xyz.Y:F1} nits  ({hdrSame.Chromaticity.X:F4}, {hdrSame.Chromaticity.Y:F4})  ratio {sameRatio:F3}\n" +
                    $"FP16 at {targetHigh:F0} nits:    {hdrHigh.Xyz.Y:F1} nits measured\n\n" +
                    $"Pipeline parity (profile applies to FP16): {(passPipeline ? "PASS" : "FAIL")}\n" +
                    $"Above-SDR-white emission:                  {(passRange ? "PASS" : "FAIL")}\n\n" +
                    (passPipeline && passRange
                        ? "The HDR-range patch path is trustworthy on this system. HDR-range calibration can be built on it."
                        : "Do NOT trust HDR-range measurements on this system yet - send the log for diagnosis.");
                Log.Info($"HDR renderer validation:\n{report}");
                // Tear the FP16 renderer + surround down BEFORE the modal result dialog:
                // on a single monitor the dialog would otherwise render under them.
                renderer?.Dispose();
                renderer = null;
                await probe.DisposeAsync();
                ConfirmDialog.Info(this, "HDR Renderer Validation", report);
            }
            catch (OperationCanceledException)
            {
                Vm.StatusText = "HDR renderer validation cancelled.";
                Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
            }
            catch (Exception ex)
            {
                Log.Info($"HDR renderer validation failed: {ex}");
                renderer?.Dispose();
                renderer = null;
                if (probe != null)
                    await probe.DisposeAsync();
                ConfirmDialog.Info(this, "HDR Renderer Validation",
                    $"HDR renderer validation failed:\n\n{ex.Message}");
            }
            finally
            {
                renderer?.Dispose();
                if (probe != null)
                    await probe.DisposeAsync();
            }
        }

        private sealed record PqTrackingSweepResult(
            string Summary,
            IReadOnlyList<MeasurementResult> Readings,
            int AttemptedRungs = 0);

        /// <summary>
        /// HDR PQ-tracking verify: FP16 wire patches through the applied profile, graded in
        /// ABSOLUTE nits against ST.2084 plus ΔE ITP against D65 gray at each level. Runs
        /// only when the calibration itself measured a wire ladder (so the LUT actually
        /// corrected this range); rungs stay below the LUT's identity blend at the panel's
        /// reachable peak. Returns the report line, or null when not applicable.
        /// </summary>
        private async Task<PqTrackingSweepResult?> RunPqTrackingSweepAsync(
            PatchDisplayWindow patchWindow, ColorimeterService colorimeter,
            ApplyContext ctx, int sequenceOffset, System.Threading.CancellationToken token)
        {
            var wireCal = _measurements?
                .Where(m => m.IsValid && m.Patch.Nits is not null && m.Patch.Nits > 0)
                .ToList();
            if (wireCal == null || wireCal.Count == 0)
                return null; // profile predates the wire ladder - nothing above SDR white was corrected

            // Grade only the region the LUT corrects: below the identity blend that starts
            // at 90% of the panel's reachable (measured) peak.
            double reachablePeak = wireCal.Max(m => m.Xyz.Y);
            double top = reachablePeak * 0.85;
            var rungs = new[] { 16.0, 64, 150, 320, 650, 1000 }.Where(n => n <= top).ToList();
            if (rungs.Count == 0)
                return null;

            List<(double Requested, MeasurementResult M)> readings;
            try
            {
                readings = await MeasurePqRungsAsync(
                    patchWindow, colorimeter, ctx, rungs, "HDR PQ tracking", sequenceOffset, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Info($"CalibrationReportWindow: PQ tracking sweep failed: {ex.Message}");
                return new PqTrackingSweepResult(
                    $"HDR PQ tracking sweep failed ({ex.Message}).",
                    Array.Empty<MeasurementResult>(),
                    rungs.Count);
            }

            if (readings.Count == 0)
                return new PqTrackingSweepResult(
                    "HDR PQ tracking sweep returned no valid readings.",
                    Array.Empty<MeasurementResult>(),
                    rungs.Count);

            double sumAbsErr = 0, worstErr = 0, worstNits = 0, itpSum = 0, itpMax = 0;
            int itpCount = 0, itpSkipped = 0;
            foreach (var (requested, m) in readings)
            {
                double err = (m.Xyz.Y - requested) / requested;
                sumAbsErr += Math.Abs(err);
                if (Math.Abs(err) > Math.Abs(worstErr)) { worstErr = err; worstNits = requested; }

                // D65 gray at the requested absolute luminance - the PQ spec target.
                var target = new CieXyz(
                    requested * 0.3127 / 0.3290, requested, requested * (1 - 0.3127 - 0.3290) / 0.3290);
                double itp = CalibrationVerifier.DeltaEItp(m.Xyz, target);
                // DeltaEItp can be non-finite for non-physical readings; one NaN would
                // poison the whole average, so exclude and count instead.
                if (double.IsFinite(itp))
                {
                    itpSum += itp;
                    itpMax = Math.Max(itpMax, itp);
                    itpCount++;
                }
                else
                {
                    itpSkipped++;
                }
                Log.Info($"CalibrationReportWindow: PQ verify {requested,6:F0} nits -> {m.Xyz.Y,7:F1} " +
                         $"({err:+0.0%;-0.0%}), xy ({m.Xyz.ToChromaticity().X:F4},{m.Xyz.ToChromaticity().Y:F4}), ITP {itp:F1}");
            }
            if (itpSkipped > 0)
                Log.Info($"CalibrationReportWindow: PQ tracking excluded {itpSkipped} non-finite ΔE ITP value(s) from the aggregate.");

            string itpSummary = itpCount > 0
                ? $"ΔE ITP avg {itpSum / itpCount:F1}, max {itpMax:F1} vs D65 gray" +
                  (itpSkipped > 0 ? $" ({itpSkipped} non-physical reading(s) excluded)" : "")
                : "ΔE ITP unavailable (no physical readings)";
            string summary = $"HDR PQ tracking (FP16 through profile, {readings.Count} levels to {readings[^1].Requested:F0} nits): " +
                             $"avg luminance error {sumAbsErr / readings.Count:P1}, worst {worstErr:+0.0%;-0.0%} at {worstNits:F0} nits; " +
                             $"{itpSummary}.";
            return new PqTrackingSweepResult(summary, readings.Select(r => r.M).ToList(), rungs.Count);
        }

        private sealed record ColoredHdrSweepResult(
            string Summary,
            IReadOnlyList<MeasurementResult> Readings);

        /// <summary>
        /// Colored HDR verification: Rec.2020-container R/G/B/C/M/Y stimuli at absolute PQ
        /// luminance rungs, presented FP16 scRGB through the applied profile and graded
        /// ABSOLUTELY — ΔE ITP (BT.2124) against the container reference XYZ plus
        /// luminance error. Container-referred: the reference is what an ideal HDR10
        /// mastering display would show for this wire signal, so a panel short of
        /// Rec.2020 (or of the rung luminance on a saturated hue) shows its real gamut /
        /// tone mapping here — exactly the above-SDR-white color error this sweep exists
        /// to characterize. Runs on every HDR verify (the PQ tracking sweep exposes no
        /// options, so this follows suit with no toggle); rungs above the panel's
        /// reachable/reported peak are skipped inside the set builder. Results are
        /// reported separately from the neutral metrics and never fold into the grade.
        /// </summary>
        private async Task<ColoredHdrSweepResult?> RunColoredHdrSweepAsync(
            PatchDisplayWindow patchWindow, ColorimeterService colorimeter,
            ApplyContext ctx, int sequenceOffset, System.Threading.CancellationToken token)
        {
            // Peak for rung capping: prefer the calibration's measured reachable wire
            // peak (what the panel actually emitted), else the DXGI-reported panel peak.
            double measuredPeak = _measurements?
                .Where(m => m.IsValid && m.Patch.Nits is not null && m.Patch.Nits > 0)
                .Select(m => m.Xyz.Y)
                .DefaultIfEmpty(0)
                .Max() ?? 0;
            double peak = measuredPeak > 50 ? measuredPeak
                : ctx.Monitor.HdrPeakNits > 50 ? ctx.Monitor.HdrPeakNits
                : 0; // unknown peak -> the builder keeps only the 100-nit rung
            // Gamut-aware: reference the hues in the TARGET's container (sRGB-gamut targets
            // get reachable sRGB hues, not Rec.2020 hues the panel can't emit).
            var stimuli = ColoredHdrVerificationSet.Build(peak, EffectiveTarget(ctx).RgbToXyzMatrix);
            if (stimuli.Count == 0)
                return null;

            HdrPatchRenderer? wire = null;
            var readings = new List<(ColoredHdrStimulus S, MeasurementResult M)>();
            try
            {
                var rect = patchWindow.GetPatchPixelRect();
                wire = new HdrPatchRenderer(rect.X, rect.Y, rect.Width, rect.Height);
                patchWindow.SetColor(0, 0, 0);

                for (int i = 0; i < stimuli.Count; i++)
                {
                    var s = stimuli[i];
                    var p = new ColorPatch
                    {
                        Name = $"HDR {s.Name}",
                        // Hue marker at half signal for the CSV: never all-high or
                        // all-low, so the white/black patch heuristics cannot match it.
                        DisplayRgb = s.UnitRgb.Scale(0.5),
                        // Nits marks it as a wire patch: excluded from the SDR accuracy
                        // metrics (and their peak normalization) by ComputeMetrics.
                        Nits = s.RungNits,
                        TargetXyz = s.ReferenceXyz, // absolute container reference (CSV)
                        Category = s.IsPrimaryHue ? PatchCategory.Primary : PatchCategory.Secondary,
                        Index = sequenceOffset + i,
                    };
                    Vm.VerifyButtonContent = $"Colored HDR {i + 1}/{stimuli.Count}…";
                    patchWindow.SetProgress(i + 1, stimuli.Count, p.Name,
                        i + 1 < stimuli.Count ? $"HDR {stimuli[i + 1].Name}" : null,
                        phase: "Colored HDR");
                    wire.PresentNits(s.ScRgbNits.R, s.ScRgbNits.G, s.ScRgbNits.B);
                    await Task.Delay(i == 0 ? 1200 : 600, token); // settle (longer for the first patch)
                    var m = WithSequenceIndex(await colorimeter.MeasureAsync(p, ctx.HdrMode, token), sequenceOffset + i);
                    if (ctx.CaptureSounds)
                        CalibrationSounds.PlayCapture();
                    if (m.IsValid)
                        readings.Add((s, m));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Info($"CalibrationReportWindow: colored HDR sweep failed: {ex.Message}");
                return new ColoredHdrSweepResult(
                    $"Colored HDR sweep failed ({ex.Message}).",
                    Array.Empty<MeasurementResult>());
            }
            finally
            {
                wire?.Dispose();
            }

            if (readings.Count == 0)
                return new ColoredHdrSweepResult(
                    "Colored HDR sweep returned no valid readings.",
                    Array.Empty<MeasurementResult>());

            var metrics = CalibrationVerifier.GradeColoredHdr(
                readings.Select(r => (r.S, r.M.Xyz)));
            foreach (var grade in metrics.Patches)
            {
                Log.Info($"CalibrationReportWindow: Colored HDR {grade.Name,-11} -> {grade.MeasuredY,7:F1} nits " +
                         $"vs rung {grade.RungNits:F0} ({grade.LuminanceError:+0.0%;-0.0%}), ΔE ITP {grade.DeltaEItp:F1}");
            }
            if (metrics.ExcludedCount > 0)
                Log.Info($"CalibrationReportWindow: colored HDR excluded {metrics.ExcludedCount} non-finite ΔE ITP value(s) from the aggregate.");

            string summary = metrics.GradedCount > 0
                ? $"Colored HDR (Rec.2020-container R/G/B/C/M/Y through profile, {metrics.GradedCount} patches, graded absolutely): " +
                  $"avg ΔE ITP {metrics.AverageItpDeltaE:F1}, max {metrics.MaxItpDeltaE:F1} (worst: {metrics.WorstPatchName}); " +
                  $"avg luminance error {metrics.AverageAbsLuminanceError:P1}" +
                  (metrics.ExcludedCount > 0 ? $" ({metrics.ExcludedCount} non-physical reading(s) excluded)" : "") + "."
                : "Colored HDR sweep: ΔE ITP unavailable (no physical readings).";
            return new ColoredHdrSweepResult(summary, readings.Select(r => r.M).ToList());
        }

        /// <summary>
        /// Measures a ladder of absolute-nits PQ rungs (wire-exact FP16 patches on a black
        /// surround) through whatever correction Windows is currently applying — the shared
        /// machinery behind the verify PQ-tracking sweep and the closed-loop HDR refinement.
        /// The caller owns the measurement session, patch window and cancellation; invalid
        /// readings are dropped. Settle timings and capture sounds match the verify sweep.
        /// </summary>
        private async Task<List<(double Requested, MeasurementResult M)>> MeasurePqRungsAsync(
            PatchDisplayWindow patchWindow, ColorimeterService colorimeter, ApplyContext ctx,
            IReadOnlyList<double> rungs, string phase, int sequenceOffset,
            System.Threading.CancellationToken token)
        {
            HdrPatchRenderer? wire = null;
            var readings = new List<(double Requested, MeasurementResult M)>();
            try
            {
                var rect = patchWindow.GetPatchPixelRect();
                wire = new HdrPatchRenderer(rect.X, rect.Y, rect.Width, rect.Height);
                patchWindow.SetColor(0, 0, 0);

                for (int i = 0; i < rungs.Count; i++)
                {
                    double nits = rungs[i];
                    var p = new ColorPatch
                    {
                        Name = $"PQ {nits:F0} nits",
                        DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                        Nits = nits,
                        Category = PatchCategory.General,
                        Index = sequenceOffset + i,
                    };
                    Vm.VerifyButtonContent = $"{phase} {i + 1}/{rungs.Count}…";
                    patchWindow.SetProgress(i + 1, rungs.Count, p.Name,
                        i + 1 < rungs.Count ? $"PQ {rungs[i + 1]:F0} nits" : null,
                        phase: phase);
                    wire.PresentNits(nits, nits, nits);
                    await Task.Delay(i == 0 ? 1200 : 600, token);
                    var m = WithSequenceIndex(await colorimeter.MeasureAsync(p, ctx.HdrMode, token), sequenceOffset + i);
                    if (ctx.CaptureSounds)
                        CalibrationSounds.PlayCapture();
                    if (m.IsValid)
                        readings.Add((nits, m));
                }
            }
            finally
            {
                wire?.Dispose();
            }
            return readings;
        }

        private static MeasurementResult WithSequenceIndex(MeasurementResult source, int sequenceIndex) => new()
        {
            Id = source.Id,
            Timestamp = source.Timestamp,
            Patch = source.Patch,
            Xyz = source.Xyz,
            IntegrationTimeMs = source.IntegrationTimeMs,
            IsValid = source.IsValid,
            ErrorMessage = source.ErrorMessage,
            RawOutput = source.RawOutput,
            SequenceIndex = sequenceIndex,
        };

        /// <summary>
        /// Drift fix for OLEDs and warm-up shifts: measure ONLY white (averaged 3x), replace
        /// the characterization's white anchor, rebuild + reinstall + re-verify. ~30 seconds
        /// instead of a full calibration.
        /// </summary>
        private async Task ReanchorWhiteAsync()
        {
            if (_applyContext is not { Colorimeter: { } colorimeter } ctx || _activeCharacterization is not { } ch)
                return;
            if (!ConfirmDialog.Confirm(this, "Re-anchor White",
                    "Gloam will open the probe-positioning surface, then re-measure the display's white anchor.",
                    confirmLabel: "Position probe", cancelLabel: "Cancel"))
                return;

            ProbeOperationScope? probe = null;
            using var placementCts = new System.Threading.CancellationTokenSource();
            try
            {
                probe = await ProbeOperationScope.StartAsync(new ProbeOperationScope.Options(
                    ctx.Monitor,
                    colorimeter,
                    "White re-anchor",
                    HdrMode: ctx.HdrMode,
                    PatchSize: ctx.PatchSize,
                    PatchOffsetX: ctx.PatchOffsetX,
                    PatchOffsetY: ctx.PatchOffsetY,
                    StateManager: ctx.StateManager,
                    PreviousGammaMode: ctx.PreviousGammaMode,
                    PreviousSettings: ctx.PreviousSettings,
                    EnterBypass: ctx.StateManager != null,
                    EnterBusy: EnterProbeBusy,
                    ExitBusy: ExitProbeBusy,
                    PlacementCommitted: RememberProbePlacement,
                    CancellationToken: placementCts.Token));
                var patchWindow = probe.PatchWindow;
                patchWindow.SetColor(1, 1, 1);
                patchWindow.SetProgress(1, 1, "White (re-anchor)");
                await Task.Delay(1500, probe.Token);

                var whitePatch = new ColorPatch
                {
                    Name = "White",
                    DisplayRgb = new LinearRgb(1, 1, 1),
                    Category = PatchCategory.Grayscale,
                };
                double sx = 0, sy = 0, sz = 0;
                const int reads = 3;
                for (int i = 0; i < reads; i++)
                {
                    var m = await colorimeter.MeasureAsync(whitePatch, ctx.HdrMode, probe.Token);
                    sx += m.Xyz.X; sy += m.Xyz.Y; sz += m.Xyz.Z;
                    if (ctx.CaptureSounds) CalibrationSounds.PlayCapture();
                    await Task.Delay(300, probe.Token);
                }
                var avg = new CieXyz(sx / reads, sy / reads, sz / reads);
                var newWhite = avg.ToChromaticity();
                Log.Info($"CalibrationReportWindow: Re-anchored white to ({newWhite.X:F4},{newWhite.Y:F4}), {avg.Y:F1} nits " +
                         $"(was ({ch.WhitePoint.X:F4},{ch.WhitePoint.Y:F4}), {ch.PeakLuminance:F1} nits).");

                _activeCharacterization = new DisplayCharacterization
                {
                    BlackXyz = ch.BlackXyz,
                    WhiteXyz = avg,
                    RedPrimary = ch.RedPrimary,
                    GreenPrimary = ch.GreenPrimary,
                    BluePrimary = ch.BluePrimary,
                    WhitePoint = newWhite,
                    BlackLevel = ch.BlackLevel,
                    PeakLuminance = avg.Y,
                    RedToneCurve = ch.RedToneCurve,
                    GreenToneCurve = ch.GreenToneCurve,
                    BlueToneCurve = ch.BlueToneCurve,
                    RgbToXyzMatrix = ColorMath.CalculateRgbToXyzMatrix(
                        ch.RedPrimary, ch.GreenPrimary, ch.BluePrimary, newWhite),
                    MeasuredGamma = ch.MeasuredGamma,
                };
            }
            catch (Exception ex)
            {
                // Close the patch window before the modal dialog (single-monitor: it would
                // otherwise render under the topmost patch window).
                if (probe != null)
                    await probe.DisposeAsync();
                ConfirmDialog.Info(this, "Re-anchor White", $"White re-anchor failed:\n\n{ex.Message}");
                return;
            }
            finally
            {
                if (probe != null)
                    await probe.DisposeAsync();
            }

            await ApplyAndVerifyAsync();
        }

        /// <summary>
        /// "Refine HDR" is meaningful only for a live HDR report with a probe, and only once
        /// a profile is installed AND enabled (it measures THROUGH the active correction);
        /// it also needs the LUTs that install actually wrote (the refinement's starting
        /// point). Collapsed outside HDR so the SDR report doesn't grow a dead button.
        /// </summary>
        private void UpdateRefineButtonState()
        {
            bool hdrLive = !_isHistorical && _applyContext is { HdrMode: true, Colorimeter: not null };
            // HDR tone/color refine need the profile applied — the fit measures the residual
            // THROUGH the installed correction.
            bool hdrRefineReady = hdrLive && _profileApplied && _profileEnabled && _installedHdrLuts != null;

            RefineHdrMenuItem.Visibility = hdrLive ? Visibility.Visible : Visibility.Collapsed;
            RefineHdrMenuItem.IsEnabled = hdrRefineReady && !_probeBusy;
            WhiteToolsMenuItem.IsEnabled = Vm.IsWhiteToolsEnabled;
            RefineMenuButton.IsEnabled = !_probeBusy && (hdrRefineReady || Vm.IsWhiteToolsEnabled);

            // Roadmap 2.3: characterization measures whatever chain is currently active
            // (profile applied or native panel — the summary says which), so it only needs
            // the live HDR session and the probe.
            CharacterizeHdrButton.Visibility = hdrLive ? Visibility.Visible : Visibility.Collapsed;
            CharacterizeHdrButton.IsEnabled = hdrLive && !_probeBusy;
        }

        private void ExportMenu_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);
        private void RefineMenu_Click(object sender, RoutedEventArgs e) => OpenButtonMenu(sender);

        private static void OpenButtonMenu(object sender)
        {
            if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
            {
                menu.PlacementTarget = button;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
                menu.IsOpen = true;
            }
        }

        private async void RefineHdrJointButton_Click(object sender, RoutedEventArgs e)
        {
            // async void (event handler): surface failures in the status strip, never the
            // global crash dialog.
            try
            {
                bool refreshReport = await RefineHdrJointAsync();
                if (refreshReport && IsLoaded)
                {
                    var previousVerification = _verifyMeasurements;
                    Vm.StatusText = "HDR refinement complete - refreshing report measurements…";
                    await RunVerificationAsync();
                    if (_operationNotice is { } notice)
                    {
                        string refreshNote = !ReferenceEquals(previousVerification, _verifyMeasurements)
                            ? _lastReportSnapshotSaved
                                ? "The accuracy numbers, charts, saved report, and future PDF exports now reflect the final correction."
                                : "The on-screen numbers, charts, and future PDF exports were refreshed, but the saved report could not be updated. Use Re-verify to retry saving it."
                            : "The correction was installed, but report verification did not complete. Use Re-verify to refresh the numbers, charts, and saved report.";
                        _operationNotice = (notice.Title, notice.Body + "\n\n" + refreshNote);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationReportWindow: joint HDR refinement failed: {ex}");
                Vm.StatusText = $"HDR tone + color refinement failed: {ex.Message}";
                Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
                _operationNotice = ("Refine HDR Tone + Color", $"Failed:\n\n{ex.Message}");
            }
            ShowOperationNotice();
        }

        // Denser than the verify sweep's 6 grading rungs: the refinement interpolates a
        // correction curve through these, so coverage matters. Capped below the LUT's
        // identity blend (≤ 85% of the reachable peak) — above it the LUT deliberately
        // passes the panel's own rolloff through and there is nothing to refine.
        private static readonly double[] RefinementLadderNits =
            { 2, 4, 8, 16, 32, 64, 100, 150, 220, 320, 450, 650, 1000 };

        /// <summary>
        /// Refines the complete MHC2 HDR transform as one measured system. Tone and color
        /// are sampled through the same installed state; the color residual is normalized
        /// so it cannot steal neutral luminance from the tone fit, and both the matrix and
        /// rebased PQ LUT are installed together. The core loop keeps a joint score and
        /// restores the best state if either objective materially regresses.
        /// </summary>
        private async Task<bool> RefineHdrJointAsync()
        {
            if (_applyContext is not { Colorimeter: { } colorimeter, HdrMode: true } ctx ||
                _activeCharacterization is not { } characterization)
            {
                ConfirmDialog.Info(this, "Refine HDR Tone + Color",
                    "HDR refinement needs the live HDR calibration session (open this report right after an HDR calibration).");
                return false;
            }
            if (!_profileApplied || !_profileEnabled || _installedHdrLuts is not { } currentLuts ||
                _installedProfileName is not { } installedName)
            {
                ConfirmDialog.Info(this, "Refine HDR Tone + Color",
                    "Apply the profile first - refinement measures the display through the installed correction.");
                return false;
            }
            if (_verifyCts != null || !Vm.IsVerifyEnabled)
                return false;

            var target = EffectiveTarget(ctx);
            var rungs = RefinementLadderNits
                .Where(n => n <= currentLuts.MeasuredPeakNits * 0.85)
                .ToList();
            if (rungs.Count < 4)
            {
                Vm.StatusText = "HDR refinement unavailable: the measured peak leaves too few PQ rungs.";
                Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
                return false;
            }

            var stimuli = ColoredHdrVerificationSet.BuildForMatrixRefinement(
                currentLuts.MeasuredPeakNits, target.RgbToXyzMatrix);
            if (stimuli.Count < HdrColorMatrixRefiner.MinValidPatches)
            {
                Vm.StatusText = "HDR refinement unavailable: the measured peak leaves too few usable color patches.";
                Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
                return false;
            }

            int patchesPerSweep = rungs.Count + stimuli.Count;
            if (!ConfirmDialog.Confirm(this, "Refine HDR Tone + Color",
                    $"Gloam will open the probe-positioning surface, then measure {patchesPerSweep} HDR patches " +
                    "through the active profile and tune " +
                    "brightness tracking and color together. It runs at most 2 passes, stops early when both " +
                    "are accurate, and automatically keeps the best measured result.\n\n" +
                    "Tip: turn night mode off first - it tints and dims the measurements.",
                    confirmLabel: "Position probe", cancelLabel: "Cancel"))
                return false;

            ProbeOperationScope? probe = null;
            using var refineCts = new System.Threading.CancellationTokenSource();
            _verifyCts = refineCts;

            bool installedRefined = false;
            try
            {
                probe = await ProbeOperationScope.StartAsync(new ProbeOperationScope.Options(
                    ctx.Monitor,
                    colorimeter,
                    "HDR tone + color refine",
                    HdrMode: true,
                    PatchSize: ctx.PatchSize,
                    PatchOffsetX: ctx.PatchOffsetX,
                    PatchOffsetY: ctx.PatchOffsetY,
                    StateManager: ctx.StateManager,
                    PreviousGammaMode: ctx.PreviousGammaMode,
                    PreviousSettings: ctx.PreviousSettings,
                    EnterBypass: ctx.StateManager != null,
                    EnterBusy: EnterProbeBusy,
                    ExitBusy: ExitProbeBusy,
                    ConfigurePatchWindow: window => window.EnableSweepControls(() =>
                    {
                        if (_verifyCts is { IsCancellationRequested: false } cts)
                            cts.Cancel();
                    }),
                    PlacementCommitted: RememberProbePlacement,
                    CancellationToken: refineCts.Token));
                var patchWindow = probe.PatchWindow;

                MonitorInfo installMonitor = ResolveCurrentMonitor(ctx.Monitor) ?? ctx.Monitor;
                string? previousDefaultProfile = null;
                if (installMonitor.MonitorDevicePath.Length > 0)
                {
                    var saved = ctx.SettingsManager?.GetMonitorProfile(installMonitor.MonitorDevicePath);
                    previousDefaultProfile = CalibrationProfileInstaller.SelectPreviousProfileBackup(
                        CalibrationProfileInstaller.GetCurrentDefaultProfile(installMonitor, ctx.HdrMode),
                        saved?.Mhc2ProfileName,
                        saved?.PreviousColorProfileName);
                }

                string? supersededThisSession = null;
                var outcome = await HdrJointRefinement.RunAsync(new HdrJointRefinement.Config
                {
                    InitialState = new HdrJointRefinement.State(
                        _installedHdrXyzCorrection, currentLuts),
                    TargetWhite = target.WhitePoint.ToXyz(1.0),
                    ToneRungs = rungs,
                    ColorStimuli = stimuli,
                    MeasureAsync = async (ladder, colorSet, sequenceOffset, token) =>
                    {
                        var toneReadings = await MeasurePqRungsAsync(
                            patchWindow!, colorimeter, ctx, ladder,
                            "HDR tone + color", sequenceOffset, token);
                        var colorReadings = await MeasureColoredStimuliAsync(
                            patchWindow!, colorimeter, ctx, colorSet,
                            "HDR tone + color", sequenceOffset + ladder.Count, token);
                        return new HdrJointRefinement.Measurements(
                            toneReadings.Select(r => r.M).ToList(), colorReadings);
                    },
                    ResolveMatrixNeutralScale = correction =>
                    {
                        try
                        {
                            return CalibrationProfileInstaller.BuildGamutMatrixPlan(
                                characterization, target, correction).UniformScale;
                        }
                        catch (InvalidOperationException ex) when (IsGamutLimitError(ex.Message))
                        {
                            throw new GamutLimitException();
                        }
                    },
                    InstallAsync = async (candidate, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        var result = CalibrationProfileInstaller.Install(
                            installMonitor, characterization, target,
                            ctx.LutR, ctx.LutG, ctx.LutB, ctx.WhiteLevel,
                            hdrMode: true, measurements: _measurements,
                            profileNameOverride: BuildRefinedProfileName(_installedProfileName ?? installedName),
                            hdrLutsOverride: candidate.Luts,
                            xyzCorrectionOverride: candidate.XyzCorrection);
                        if (!result.Success)
                        {
                            if (IsGamutLimitError(result.Error))
                                throw new GamutLimitException();
                            throw new InvalidOperationException($"Joint HDR profile install failed: {result.Error}");
                        }

                        installedRefined = true;
                        if (supersededThisSession is { } stale && stale != result.ProfileName)
                        {
                            try { CalibrationProfileInstaller.Uninstall(installMonitor, stale); }
                            catch (Exception ex)
                            {
                                Log.Info($"CalibrationReportWindow: superseded joint-refined profile cleanup failed: {ex.Message}");
                            }
                        }
                        supersededThisSession = result.ProfileName;

                        var installedLuts = result.HdrLuts ?? candidate.Luts;
                        _installedProfileName = result.ProfileName;
                        _profileApplied = true;
                        _profileEnabled = true;
                        _installedHdrLuts = installedLuts;
                        _installedHdrXyzCorrection = candidate.XyzCorrection;
                        Vm.ApplyButtonContent = "Disable Profile";
                        try { ctx.OnInstalled?.Invoke(result.ProfileName, previousDefaultProfile); }
                        catch (Exception ex)
                        {
                            // Installation already changed Windows state; a persistence
                            // callback failure must not prevent keep-best from tracking it.
                            Log.Error($"CalibrationReportWindow: recording joint-refined profile failed: {ex}");
                        }

                        // Do not let a late Cancel strand an installed candidate outside the
                        // core loop's keep-best bookkeeping. Cancellation is observed by the
                        // next measurement, after the install delegate has returned its state.
                        await Task.Delay(1000, System.Threading.CancellationToken.None);
                        return (new HdrJointRefinement.State(
                            candidate.XyzCorrection, installedLuts), result.ProfileName);
                    },
                    Progress = new Progress<HdrRefinementLoop.PassProgress>(p =>
                    {
                        Vm.StatusText = p.Pass == 0
                            ? "Measuring HDR tone and color through the installed profile…"
                            : $"Joint pass {p.Pass}/{p.MaxPasses}: {p.Phase}…";
                    }),
                }, probe.Token);

                var start = outcome.InitialMetrics;
                var finish = outcome.FinalMetrics;
                if (!outcome.AnyInstall)
                {
                    Vm.StatusText = $"HDR refinement skipped: {outcome.StopReason}.";
                    Vm.StatusBrush = outcome.Converged
                        ? CalibrationReportViewModel.GreenBrush
                        : CalibrationReportViewModel.AmberBrush;
                    _operationNotice = ("Refine HDR Tone + Color",
                        outcome.Converged
                            ? $"Already on target — no change needed.\n\nTone error {start.ToneAverageAbsError:P1}; color ΔE ITP {start.ColorAverageDeltaEItp:F1}."
                            : $"No change applied.\n\n{outcome.StopReason}\n\nTone error {start.ToneAverageAbsError:P1}; color ΔE ITP {start.ColorAverageDeltaEItp:F1}.");
                    return true;
                }

                bool improved = finish.JointScore < start.JointScore - 1e-9;
                string bestNote = outcome.EndedOnBest
                    ? " The best measured state was restored."
                    : string.Empty;
                string line = $"Joint HDR refinement: tone {start.ToneAverageAbsError:P1} → " +
                              $"{finish.ToneAverageAbsError:P1}; color ΔE ITP " +
                              $"{start.ColorAverageDeltaEItp:F1} → {finish.ColorAverageDeltaEItp:F1} " +
                              $"({outcome.StopReason}).{bestNote}";
                Vm.VerifyDetailText = string.IsNullOrEmpty(Vm.VerifyDetailText)
                    ? line
                    : Vm.VerifyDetailText + "\n" + line;
                Vm.IsVerifyDetailVisible = true;

                Vm.StatusText = improved
                    ? $"HDR refined: tone {start.ToneAverageAbsError:P1} → {finish.ToneAverageAbsError:P1}; " +
                      $"color {start.ColorAverageDeltaEItp:F1} → {finish.ColorAverageDeltaEItp:F1} ΔE ITP."
                    : "HDR refinement kept the existing correction; no candidate measured better.";
                Vm.StatusBrush = improved
                    ? CalibrationReportViewModel.GreenBrush
                    : CalibrationReportViewModel.DimBrush;
                _operationNotice = ("Refine HDR Tone + Color",
                    improved
                        ? $"Done.\n\nTone error {start.ToneAverageAbsError:P1} → {finish.ToneAverageAbsError:P1}\n" +
                          $"Color ΔE ITP {start.ColorAverageDeltaEItp:F1} → {finish.ColorAverageDeltaEItp:F1}{bestNote}"
                        : $"The existing correction was already the best measured result, so Gloam restored it.\n\n" +
                          $"Tone error {finish.ToneAverageAbsError:P1}; color ΔE ITP {finish.ColorAverageDeltaEItp:F1}.");
                CalibrationSounds.PlayCompletion();
                Log.Info($"CalibrationReportWindow: {line}");
                return true;
            }
            catch (OperationCanceledException)
            {
                Vm.StatusText = installedRefined
                    ? "HDR refinement cancelled - the best measured tone + color state was restored."
                    : "HDR refinement cancelled - the profile is unchanged.";
                Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
                _operationNotice = ("Refine HDR Tone + Color", installedRefined
                    ? "Cancelled — the best measured tone + color state was restored."
                    : "Cancelled — the profile is unchanged.");
                return installedRefined;
            }
            catch (GamutLimitException)
            {
                Vm.StatusText = "HDR color is already at the panel's gamut limit - no change applied.";
                Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
                _operationNotice = ("Refine HDR Tone + Color",
                    "No change applied. The fitted color correction would require output beyond what this panel can physically produce. " +
                    "Gloam left the existing profile untouched rather than introducing clipping or a color cast.");
                return false;
            }
            finally
            {
                _verifyCts = null;
                Vm.VerifyButtonContent = "Re-verify";
                if (probe != null)
                    await probe.DisposeAsync();
            }
        }

        private static bool IsGamutLimitError(string? message) =>
            message?.Contains("wider gamut", StringComparison.OrdinalIgnoreCase) == true ||
            message?.Contains("physically produce", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// Filename for the refined re-install: the original profile name with a
        /// millisecond-precision "refined" suffix (a fresh name forces Windows to load the new
        /// bytes — InstallColorProfile keeps existing content under a reused name). Repeated
        /// passes replace the suffix instead of stacking it, and the monitor prefix is
        /// preserved so stale-association cleanup still matches.
        /// </summary>
        private static string BuildRefinedProfileName(string installedProfileName)
        {
            string stem = Path.GetFileNameWithoutExtension(installedProfileName);
            stem = System.Text.RegularExpressions.Regex.Replace(stem, @" refined \d{6,9}$", "");
            return $"{stem} refined {DateTime.Now:HHmmssfff}.icm";
        }

        /// <summary>Signals that the fitted color correction would exceed the panel's
        /// gamut — a graceful "no improvement possible", not an error.</summary>
        private sealed class GamutLimitException : Exception { }
        /// <summary>
        /// Measures colored/neutral wire stimuli through the active correction — the
        /// parameterized core of <see cref="RunColoredHdrSweepAsync"/>, returning
        /// (stimulus, measured XYZ) pairs for the matrix fit.
        /// </summary>
        private async Task<IReadOnlyList<(ColoredHdrStimulus Stimulus, CieXyz Measured)>> MeasureColoredStimuliAsync(
            PatchDisplayWindow patchWindow, ColorimeterService colorimeter, ApplyContext ctx,
            IReadOnlyList<ColoredHdrStimulus> stimuli, string phase, int sequenceOffset,
            System.Threading.CancellationToken token)
        {
            HdrPatchRenderer? wire = null;
            var readings = new List<(ColoredHdrStimulus, CieXyz)>();
            try
            {
                var rect = patchWindow.GetPatchPixelRect();
                wire = new HdrPatchRenderer(rect.X, rect.Y, rect.Width, rect.Height);
                patchWindow.SetColor(0, 0, 0);

                for (int i = 0; i < stimuli.Count; i++)
                {
                    var s = stimuli[i];
                    var p = new ColorPatch
                    {
                        Name = $"{phase} {s.Name}",
                        DisplayRgb = s.UnitRgb.Scale(0.5),
                        Nits = s.RungNits,
                        TargetXyz = s.ReferenceXyz,
                        Category = s.IsPrimaryHue ? PatchCategory.Primary : PatchCategory.Secondary,
                        Index = sequenceOffset + i,
                    };
                    Vm.VerifyButtonContent = $"{phase} {i + 1}/{stimuli.Count}…";
                    patchWindow.SetProgress(i + 1, stimuli.Count, p.Name,
                        i + 1 < stimuli.Count ? stimuli[i + 1].Name : null, phase: phase);
                    wire.PresentNits(s.ScRgbNits.R, s.ScRgbNits.G, s.ScRgbNits.B);
                    await Task.Delay(i == 0 ? 1200 : 600, token);
                    var m = WithSequenceIndex(await colorimeter.MeasureAsync(p, ctx.HdrMode, token), sequenceOffset + i);
                    if (ctx.CaptureSounds)
                        CalibrationSounds.PlayCapture();
                    if (m.IsValid)
                        readings.Add((s, m.Xyz));
                }
            }
            finally
            {
                wire?.Dispose();
            }
            return readings;
        }

        // ---- HDR tone-mapping characterization (roadmap 2.3) --------------------------------

        private async void CharacterizeHdrButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await CharacterizeHdrAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationReportWindow: HDR characterization failed: {ex}");
                Vm.StatusText = $"HDR characterization failed: {ex.Message}";
                Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
                _operationNotice = ("Characterize HDR", $"Failed:\n\n{ex.Message}");
            }
            ShowOperationNotice();
        }

        /// <summary>
        /// Tone-mapping characterization (roadmap 2.3): a dense near-peak PQ ladder finds
        /// the panel's TRUE roll-off knee and peak versus its DXGI claims; an APL sweep
        /// (white windows from 1% to full-frame) charts ABL behavior; the result is
        /// summarized with HGIG game-calibration suggestions and persisted on the report.
        /// Measures through whatever chain is currently active — the summary says which.
        /// </summary>
        private async Task CharacterizeHdrAsync()
        {
            if (_applyContext is not { Colorimeter: { } colorimeter, HdrMode: true } ctx)
            {
                ConfirmDialog.Info(this, "Characterize HDR",
                    "HDR characterization needs the live HDR calibration session.");
                return;
            }
            if (_verifyCts != null || !Vm.IsVerifyEnabled)
                return;

            double claimedPeak = ctx.Monitor.HdrPeakNits;
            double claimedFullFrame = ctx.Monitor.HdrMaxFullFrameNits;
            double measuredWirePeak = _measurements?
                .Where(m => m.IsValid && m.Patch.Nits is not null && m.Patch.Nits > 0)
                .Select(m => m.Xyz.Y)
                .DefaultIfEmpty(0)
                .Max() ?? 0;
            double ladderBase = claimedPeak > 50 ? claimedPeak
                : measuredWirePeak > 50 ? measuredWirePeak
                : 1000.0;

            var rungs = ToneMappingAnalyzer.LadderFractionsOfClaimedPeak
                .Select(f => Math.Min(Math.Round(ladderBase * f), 10000.0))
                .Distinct()
                .OrderBy(n => n)
                .ToList();

            if (!ConfirmDialog.Confirm(this, "Characterize HDR",
                    $"Gloam will open the probe-positioning surface, then measure {rungs.Count} PQ rungs " +
                    $"concentrated near the claimed {ladderBase:F0}-nit peak, " +
                    $"then the same white through window sizes from 1% to full-frame (~3 minutes total), and reports " +
                    "the panel's TRUE tone-mapping knee, peak and ABL behavior with suggested HGIG game settings.\n\n" +
                    "Tip: turn night mode off first.",
                    confirmLabel: "Position probe", cancelLabel: "Cancel"))
                return;

            ProbeOperationScope? probe = null;
            using var charCts = new System.Threading.CancellationTokenSource();
            _verifyCts = charCts;

            try
            {
                probe = await ProbeOperationScope.StartAsync(new ProbeOperationScope.Options(
                    ctx.Monitor,
                    colorimeter,
                    "Characterize HDR",
                    HdrMode: true,
                    PatchSize: ctx.PatchSize,
                    PatchOffsetX: ctx.PatchOffsetX,
                    PatchOffsetY: ctx.PatchOffsetY,
                    StateManager: ctx.StateManager,
                    PreviousGammaMode: ctx.PreviousGammaMode,
                    PreviousSettings: ctx.PreviousSettings,
                    EnterBypass: ctx.StateManager != null,
                    EnterBusy: EnterProbeBusy,
                    ExitBusy: ExitProbeBusy,
                    ConfigurePatchWindow: window => window.EnableSweepControls(() =>
                    {
                        if (_verifyCts is { IsCancellationRequested: false } cts)
                            cts.Cancel();
                    }),
                    PlacementCommitted: RememberProbePlacement,
                    CancellationToken: charCts.Token));
                var patchWindow = probe.PatchWindow;

                // 1) Dense near-peak ladder at the standard patch window.
                Vm.StatusText = "Measuring dense near-peak PQ ladder…";
                var ladderReadings = await MeasurePqRungsAsync(
                    patchWindow, colorimeter, ctx, rungs, "Tone map ladder", 0, probe.Token);
                var ladder = ladderReadings
                    .Select(r => new ToneMapLadderPoint(r.Requested, r.M.Xyz.Y))
                    .ToList();

                // 2) APL sweep: same white request through growing window sizes. Each size
                // needs its own FP16 renderer (the swapchain rect is immutable).
                var apl = new List<AplPoint>();
                var bounds = ctx.Monitor.MonitorBounds;
                int monitorW = bounds.Right - bounds.Left;
                int monitorH = bounds.Bottom - bounds.Top;
                double aplRequest = Math.Min(ladderBase, 10000.0);
                int aplIndex = 0;
                foreach (double pct in ToneMappingAnalyzer.AplWindowPercents)
                {
                    probe.Token.ThrowIfCancellationRequested();
                    double side = Math.Sqrt(pct / 100.0);
                    int w = Math.Max(64, (int)Math.Round(monitorW * side));
                    int h = Math.Max(64, (int)Math.Round(monitorH * side));
                    int x = bounds.Left + (monitorW - w) / 2;
                    int y = bounds.Top + (monitorH - h) / 2;

                    Vm.StatusText = $"Measuring ABL: {pct:F0}% window…";
                    Vm.VerifyButtonContent = $"APL {pct:F0}%…";
                    patchWindow.SetColor(0, 0, 0);

                    HdrPatchRenderer? aplWire = null;
                    try
                    {
                        aplWire = new HdrPatchRenderer(x, y, w, h);
                        aplWire.PresentNits(aplRequest, aplRequest, aplRequest);
                        // Longer settle: big field changes exercise ABL/power limiting,
                        // which reacts over hundreds of milliseconds.
                        await Task.Delay(1800, probe.Token);
                        var patch = new ColorPatch
                        {
                            Name = $"APL {pct:F0}% window",
                            DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                            Nits = aplRequest,
                            Category = PatchCategory.General,
                            Index = 1000 + aplIndex,
                        };
                        var m = WithSequenceIndex(
                            await colorimeter.MeasureAsync(patch, ctx.HdrMode, probe.Token), 1000 + aplIndex);
                        if (ctx.CaptureSounds)
                            CalibrationSounds.PlayCapture();
                        if (m.IsValid)
                            apl.Add(new AplPoint(pct, m.Xyz.Y));
                    }
                    finally
                    {
                        aplWire?.Dispose();
                    }
                    aplIndex++;
                }

                if (ladder.Count < 3)
                {
                    Vm.StatusText = "HDR characterization failed: too few valid ladder readings.";
                    Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
                    return;
                }

                var characterizationResult = ToneMappingAnalyzer.Analyze(
                    claimedPeak, claimedFullFrame, ladder, apl);
                _toneMapping = characterizationResult;

                string chainNote = _profileApplied && _profileEnabled
                    ? " (measured through the installed profile)"
                    : " (native panel, no profile active)";
                string line = ToneMappingAnalyzer.Describe(characterizationResult) + chainNote;
                Vm.VerifyDetailText = string.IsNullOrEmpty(Vm.VerifyDetailText)
                    ? line
                    : Vm.VerifyDetailText + "\n" + line;
                Vm.IsVerifyDetailVisible = true;

                // Persist the whole current presentation. PersistReportSummary retains the
                // last verification metrics, so adding characterization cannot erase the
                // after-correction column or its diagnostics.
                PersistReportSummary(after: null);

                Vm.StatusText = $"HDR characterized: true peak {characterizationResult.MeasuredPeakNits:F0} nits " +
                                $"(claimed {claimedPeak:F0}), knee ~{characterizationResult.KneeNits:F0} nits.";
                Vm.StatusBrush = CalibrationReportViewModel.GreenBrush;
                _operationNotice = ("Characterize HDR",
                    ToneMappingAnalyzer.Describe(characterizationResult) + chainNote +
                    "\n\nSaved to this report.");
                CalibrationSounds.PlayCompletion();
                Log.Info($"CalibrationReportWindow: {line}");
            }
            catch (OperationCanceledException)
            {
                Vm.StatusText = "HDR characterization cancelled.";
                Vm.StatusBrush = CalibrationReportViewModel.DimBrush;
                _operationNotice = ("Characterize HDR", "Cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error($"CalibrationReportWindow: HDR characterization failed: {ex}");
                Vm.StatusText = $"HDR characterization failed: {ex.Message}";
                Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
                _operationNotice = ("Characterize HDR", $"Failed:\n\n{ex.Message}");
            }
            finally
            {
                _verifyCts = null;
                Vm.VerifyButtonContent = "Re-verify";
                if (probe != null)
                    await probe.DisposeAsync();
            }
        }

        /// <summary>
        /// Visual white trim: live-preview nudges of the TARGET white, each rebuilding the
        /// profile (alternating between two preview names so the compositor never serves a
        /// cached profile). Done bakes the trim into a final, recorded profile.
        /// </summary>
        private async Task RunVisualWhiteTrimAsync()
        {
            if (_applyContext is not { } ctx || _activeCharacterization == null) return;

            // Claim the probe gate for the whole trim: it re-installs preview profiles and
            // finishes with a re-apply, so a competing measurement/install must be locked out.
            EnterProbeBusy();
            try
            {
                await RunVisualWhiteTrimCoreAsync(ctx);
            }
            finally
            {
                ExitProbeBusy();
            }
        }

        private async Task RunVisualWhiteTrimCoreAsync(ApplyContext ctx)
        {
            var baseWhite = EffectiveTarget(ctx).WhitePoint;
            var editor = new WhiteTrimWindow { Owner = this };
            editor.TrimChanged += async (dx, dy) =>
            {
                // async void (event handler): an installer exception escaping here would
                // hit the global crash dialog mid-trim. Report it in the status strip.
                try
                {
                    await InstallTrimPreviewAsync(ctx, baseWhite, dx, dy);
                }
                catch (Exception ex)
                {
                    Log.Error($"CalibrationReportWindow: white-trim preview install failed: {ex}");
                    Vm.StatusText = $"White-trim preview failed: {ex.Message}";
                    Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
                }
            };

            bool? accepted = editor.ShowDialog();

            // Retire the preview profiles either way (throwaway artifacts, fully ours).
            foreach (string previewName in new[] { TrimPreviewName(ctx, true), TrimPreviewName(ctx, false) })
            {
                CalibrationProfileInstaller.Disable(ctx.Monitor, previewName);
                CalibrationProfileInstaller.Uninstall(ctx.Monitor, previewName);
            }

            if (accepted == true && editor.Result is { } trim && (trim.Dx != 0 || trim.Dy != 0))
            {
                _trimmedTargetWhite = new Chromaticity(baseWhite.X + trim.Dx, baseWhite.Y + trim.Dy);
                Vm.StatusText = $"White trim baked in (dx {trim.Dx:+0.0000;-0.0000}, dy {trim.Dy:+0.0000;-0.0000}).";
                await ApplyAndVerifyAsync(runVerify: false);
            }
            else
            {
                // Cancelled (or zero trim): restore the untrimmed profile.
                await ApplyAndVerifyAsync(runVerify: false);
            }
        }

        private static string TrimPreviewName(ApplyContext ctx, bool a) =>
            $"{ctx.Monitor.FriendlyName} - white trim preview {(a ? "A" : "B")}.icm";

        private async Task InstallTrimPreviewAsync(ApplyContext ctx, Chromaticity baseWhite, double dx, double dy)
        {
            // Serialize: nudges can arrive faster than installs complete. Keep only the latest.
            if (_trimBusy) { _trimPendingNudge = (dx, dy); return; }
            _trimBusy = true;
            try
            {
                while (true)
                {
                    var target = ctx.Target.WithWhitePoint(new Chromaticity(baseWhite.X + dx, baseWhite.Y + dy));
                    _trimNameToggle = !_trimNameToggle;
                    var result = CalibrationProfileInstaller.Install(
                        ctx.Monitor, _activeCharacterization!, target,
                        ctx.LutR, ctx.LutG, ctx.LutB, ctx.WhiteLevel,
                        hdrMode: ctx.HdrMode, measurements: _measurements,
                        profileNameOverride: TrimPreviewName(ctx, _trimNameToggle));
                    if (!result.Success)
                        Log.Info($"CalibrationReportWindow: trim preview install failed: {result.Error}");

                    await Task.Delay(150); // let the compositor pick it up before the next step
                    if (_trimPendingNudge is { } pending) { (dx, dy) = pending; _trimPendingNudge = null; }
                    else break;
                }
            }
            finally
            {
                _trimBusy = false;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            BrutalistTheme.Toggle();
            ThemeToggleButton.Content = BrutalistTheme.IsDark ? "◐" : "◑";
        }

        /// <summary>
        /// Re-measures a quick patch sweep THROUGH whatever Windows is currently applying
        /// (normally the just-installed profile) and fills in the "after" row of the accuracy
        /// table - the honest, measured counterpart to the native "before" numbers.
        /// </summary>
        private async void VerifyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_applyContext?.Colorimeter is not { } colorimeter || _applyContext is not { } ctx)
            {
                ConfirmDialog.Info(this, "Verify Calibration",
                    "Verification isn't available for this report (no colorimeter context). " +
                    "Run it from the report that opens right after a calibration.");
                return;
            }

            string prompt = _profileApplied
                ? "Gloam will open the probe-positioning surface, then measure the active correction.\n\n" +
                  "Tip: turn night mode off while verifying - it tints the measurements."
                : "The profile hasn't been applied from this window yet, so Verify will measure " +
                  "whatever is currently active on the display. The probe-positioning surface opens next.";
            if (!ConfirmDialog.Confirm(this, "Verify Calibration", prompt,
                    confirmLabel: "Position probe", cancelLabel: "Cancel"))
                return;

            await RunVerificationAsync(requestPlacement: true);
        }

        /// <summary>Strip label for a sweep patch: its name, or an RGB-derived description.</summary>
        private static string PatchLabel(ColorPatch patch) =>
            string.IsNullOrEmpty(patch.Name) ? CalibrationWindow.GetPatchDescription(patch) : patch.Name;

        /// <summary>
        /// The verify sweep itself (no prompts): measures the verification patches through
        /// whatever Windows is currently applying and fills the "after" row + grade.
        /// Runs automatically after Apply Profile, and from the Verify button for re-runs.
        /// </summary>
        private async Task RunVerificationAsync(bool requestPlacement = false)
        {
            if (_applyContext?.Colorimeter is not { } colorimeter || _applyContext is not { } ctx)
                return;

            _lastReportSnapshotSaved = false;
            ProbeOperationScope? probe = null;
            using var verifyCts = new System.Threading.CancellationTokenSource();
            _verifyCts = verifyCts;

            try
            {
                // RAMP QUIESCENCE: verification grades the calibration, so the user's gamma
                // preference and night mode must not ride on the GPU ramp during the sweep.
                probe = await ProbeOperationScope.StartAsync(new ProbeOperationScope.Options(
                    ctx.Monitor,
                    colorimeter,
                    "Calibration verification",
                    HdrMode: ctx.HdrMode,
                    PatchSize: ctx.PatchSize,
                    PatchOffsetX: ctx.PatchOffsetX,
                    PatchOffsetY: ctx.PatchOffsetY,
                    RequestPlacement: requestPlacement,
                    StateManager: ctx.StateManager,
                    PreviousGammaMode: ctx.PreviousGammaMode,
                    PreviousSettings: ctx.PreviousSettings,
                    EnterBypass: ctx.StateManager != null,
                    EnterBusy: EnterProbeBusy,
                    ExitBusy: ExitProbeBusy,
                    ConfigurePatchWindow: window => window.EnableSweepControls(() =>
                    {
                        if (_verifyCts is { IsCancellationRequested: false } cts)
                            cts.Cancel();
                    }),
                    PlacementCommitted: RememberProbePlacement,
                    CancellationToken: verifyCts.Token));
                var patchWindow = probe.PatchWindow;

                // Detailed mode swaps in the extended patch set (fine grayscale, saturation
                // ramps, memory colors); everything downstream - progress, settle delays,
                // metrics, the PQ ladder - is shared with the standard sweep.
                bool detailedSweep = Vm.IsDetailedVerifyChecked;
                var verificationTarget = EffectiveTarget(ctx);
                var patches = detailedSweep
                    ? VerificationPatchSets.Detailed(verificationTarget, ctx.HdrMode)
                    : CalibrationVerifier.BuildVerificationPatches();
                var results = new List<MeasurementResult>();
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    var next = i + 1 < patches.Count ? patches[i + 1] : null;
                    Vm.VerifyButtonContent = $"Verifying {i + 1}/{patches.Count}…";
                    patchWindow.SetProgress(i + 1, patches.Count, PatchLabel(p),
                        next == null ? null : PatchLabel(next));
                    patchWindow.SetColor(p.DisplayRgb.R, p.DisplayRgb.G, p.DisplayRgb.B);
                    await Task.Delay(i == 0 ? 1200 : 500, probe.Token); // settle (longer for the first patch)
                    results.Add(await colorimeter.MeasureAsync(p, ctx.HdrMode, probe.Token));
                    if (ctx.CaptureSounds)
                        CalibrationSounds.PlayCapture();
                }
                // HDR PQ-TRACKING SWEEP: when the applied profile was built from the FP16
                // wire ladder, verify it the same way - wire-exact FP16 patches THROUGH the
                // profile, graded in absolute nits against the PQ spec. Only rungs inside
                // the corrected region (below the LUT's identity blend near the panel's
                // reachable peak) are graded; above it the LUT intentionally passes the
                // panel's own rolloff through.
                var pqTracking = ctx.HdrMode
                    ? await RunPqTrackingSweepAsync(patchWindow, colorimeter, ctx, patches.Count, probe.Token)
                    : null;
                // COLORED HDR VERIFICATION: R/G/B/C/M/Y Rec.2020-container stimuli at
                // absolute luminance rungs, graded with ΔE ITP against the container
                // reference. Part of every HDR verify (the PQ sweep exposes no options,
                // so neither does this); reported separately from the neutral metrics.
                var coloredHdr = ctx.HdrMode
                    ? await RunColoredHdrSweepAsync(patchWindow, colorimeter, ctx,
                        patches.Count + (pqTracking?.AttemptedRungs ?? 0), probe.Token)
                    : null;
                var persistedVerifyMeasurements = new List<MeasurementResult>(results);
                if (pqTracking?.Readings.Count > 0)
                    persistedVerifyMeasurements.AddRange(pqTracking.Readings);
                if (coloredHdr?.Readings.Count > 0)
                    persistedVerifyMeasurements.AddRange(coloredHdr.Readings);
                _verifyMeasurements = persistedVerifyMeasurements;

                var after = CalibrationVerifier.ComputeMetrics(results, verificationTarget);
                var activation = _measurements != null
                    ? VerificationAnalysis.AnalyzeProfileActivation(
                        CalibrationVerifier.ComputeMetrics(_measurements, verificationTarget).PatchResults,
                        after.PatchResults,
                        verificationTarget.WhitePointOnly)
                    : null;
                Vm.AfterAvgText = $"{after.AverageDeltaE:F2}";
                Vm.AfterMaxText = $"{after.MaxDeltaE:F2}";
                Vm.AfterGrayscaleText = $"{after.AverageGrayscaleDeltaE:F2}";
                Vm.AfterPrimaryText = $"{after.AveragePrimaryDeltaE:F2}";
                Vm.AfterAvgBrush = CalibrationReportViewModel.DeltaEBrush(after.AverageDeltaE);
                Vm.AfterMaxBrush = CalibrationReportViewModel.DeltaEBrush(after.MaxDeltaE);
                Vm.AfterGrayscaleBrush = CalibrationReportViewModel.DeltaEBrush(after.AverageGrayscaleDeltaE);
                Vm.AfterPrimaryBrush = CalibrationReportViewModel.DeltaEBrush(after.AveragePrimaryDeltaE);

                // The grade AND its summary sentence now reflect what the user actually
                // sees: the corrected display, not the uncorrected panel.
                var afterGrade = after.GetGrade();
                SetGradeDisplay(afterGrade);
                Vm.GradeScopeText = "after correction";
                Vm.SummaryText = GetSummaryText(afterGrade);
                _summaryBaseText = Vm.SummaryText;
                Vm.IsPerceptualNoteVisible = true;

                // Uncertainty on the verified average (1.3): the sweep's single reads
                // inherit the calibration run's noise model for their repeatability term.
                var afterUncertainty = ComputeUncertainty(results, verificationTarget);
                if (afterUncertainty != null)
                {
                    Vm.AfterAvgText = $"{after.AverageDeltaE:F2} ± {afterUncertainty.ExpandedU:F2}";
                    Vm.AfterAvgToolTip = "Expanded measurement uncertainty: " + afterUncertainty.Describe();
                    Vm.SummaryText = _summaryBaseText +
                        $"\nMeasurement uncertainty on the verified average ΔE: {afterUncertainty.Describe()}.";
                }

                // Diagnostics line: tone/color decomposition answers "is the residual real?",
                // and ΔE ITP is the HDR-native metric for cross-tool comparison.
                Vm.VerifyDetailText =
                    $"Grayscale residual split: tone {after.AverageGrayscaleToneDeltaE:F2} / color {after.AverageGrayscaleColorDeltaE:F2} ΔE2000 " +
                    $"(tone near black is mostly instrument noise; color is a visible cast). " +
                    $"ΔE ITP avg {after.AverageItpDeltaE:F1}, max {after.MaxItpDeltaE:F1} (BT.2124; ~3x ΔE2000 scale, 1 unit ≈ 1 JND).";
                // The two HDR sweeps get their own kicker-labeled blocks (visual hierarchy)
                // instead of being newline-joined into the prose. Cleared on an SDR re-verify
                // so stale HDR results don't linger next to fresh SDR numbers.
                if (pqTracking != null)
                {
                    Vm.PqTrackingDetailText = pqTracking.Summary;
                    Vm.IsPqTrackingDetailVisible = true;
                }
                else
                {
                    Vm.IsPqTrackingDetailVisible = false;
                }
                if (coloredHdr != null)
                {
                    Vm.ColoredHdrDetailText = coloredHdr.Summary;
                    Vm.IsColoredHdrDetailVisible = true;
                }
                else
                {
                    Vm.IsColoredHdrDetailVisible = false;
                }
                if (activation != null)
                    Vm.VerifyDetailText += "\n" + activation.Message;
                Vm.IsVerifyDetailVisible = true;

                // Detailed sweep: hand the per-patch results to the Detailed Verification
                // section. A standard re-verify retires earlier detailed results instead of
                // showing them next to "after" numbers they no longer describe.
                if (detailedSweep)
                {
                    _detailedPatchResults = after.PatchResults.ToList();
                    PresentDetailedResults();
                }
                else if (_detailedPatchResults != null)
                {
                    _detailedPatchResults = null;
                    Vm.HasDetailedResults = false;
                    Vm.WorstPatches.Clear();
                    Vm.BestPatches.Clear();
                }

                // Refresh recommendations with verify-aware guidance.
                PopulateRecommendations(afterGrade, after);
                if (activation?.ShouldWarn == true)
                {
                    Vm.StatusText = $"Verified, but profile activation is suspect: average ΔE {after.AverageDeltaE:F2} " +
                                    $"({results.Count(r => r.IsValid)} of {patches.Count} patches).";
                    Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
                }
                else
                {
                    Vm.StatusText = $"Verified through the applied profile: average ΔE {after.AverageDeltaE:F2} " +
                                    $"({results.Count(r => r.IsValid)} of {patches.Count} patches).";
                    Vm.StatusBrush = CalibrationReportViewModel.GreenBrush;
                }

                // Overlay the corrected response on the charts: the native curves alone read
                // as alarming even when the corrected result is good.
                RenderCharts();

                CalibrationSounds.PlayCompletion();
                Log.Info($"CalibrationReportWindow: Verify pass avg dE {after.AverageDeltaE:F2}, " +
                         $"max {after.MaxDeltaE:F2}, gray {after.AverageGrayscaleDeltaE:F2}, primary {after.AveragePrimaryDeltaE:F2}.");
                if (activation != null)
                    Log.Info($"CalibrationReportWindow: {activation.Message}");

                // Re-save the report snapshot so the "after" column (and the verified
                // grade the disc now shows) survive into the saved-reports browser.
                if (_metrics != null && _profile != null)
                {
                    _profile.PostCalibrationDeltaE = after.AverageDeltaE;
                    _profile.QualityGrade = afterGrade;
                    _profile.ModifiedAt = DateTime.UtcNow;
                    PersistReportSummary(after);
                }
            }
            catch (OperationCanceledException)
            {
                Vm.StatusText = "Verification cancelled. Use Re-verify to run it again.";
            }
            catch (Exception ex)
            {
                // Tear the patch window down BEFORE the modal dialog: on a single monitor the
                // topmost patch window would otherwise render over (and hide) the error.
                if (probe != null)
                    await probe.DisposeAsync();
                // The window may already be closed (Closing cancels the CTS, but a failure
                // can surface after teardown) — showing a dialog owned by a closed window
                // throws InvalidOperationException from ShowDialog.
                if (IsLoaded)
                {
                    ConfirmDialog.Info(this, "Verify Calibration", $"Verification failed:\n\n{ex.Message}");
                }
                else
                {
                    Log.Error($"CalibrationReportWindow: verification failed after the window closed: {ex.Message}");
                }
                Vm.StatusText = $"Verification failed: {ex.Message}";
                Vm.StatusBrush = CalibrationReportViewModel.AmberBrush;
            }
            finally
            {
                _verifyCts = null;
                Vm.VerifyButtonContent = "Re-verify";
                if (probe != null)
                    await probe.DisposeAsync();
            }
        }
    }
}
