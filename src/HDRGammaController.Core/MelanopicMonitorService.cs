using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// A live melanopic evaluation for one monitor: the reading, its honest uncertainty, and
    /// where the spectra came from.
    /// </summary>
    public sealed record MelanopicMonitorState(
        string MonitorDevicePath,
        int Kelvin,
        MelanopicReading Reading,
        UncertaintyBudget.MelanopicUncertainty Uncertainty,
        string SpectraSourceName,
        bool IsHdrActive,
        DateTime TimestampUtc);

    /// <summary>
    /// The live melanopic dashboard's engine (roadmap 3.1): listens to the apply pipeline's
    /// per-monitor state snapshots, evaluates melanopic EDI / % reduction from the loaded
    /// CCSS spectra (or the generic fallback, honestly labeled), and appends samples to the
    /// nightly dose store. A slow keepalive timer re-samples the last state so the dose
    /// curve advances through long static periods — sampling is driven by state CHANGES and
    /// this timer, never by ramp writes.
    /// </summary>
    public sealed class MelanopicMonitorService : IDisposable
    {
        private readonly GammaApplyService _applyService;
        private readonly System.Timers.Timer _keepalive;
        private readonly object _lock = new();
        private readonly Dictionary<string, GammaApplyService.AppliedStateSnapshot> _lastSnapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MelanopicMonitorState> _lastStates = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <summary>Assumed viewing solid angle Ω_eff in steradian (see MelanopicCalculator).</summary>
        public double ViewingSolidAngleSr { get; set; } = MelanopicCalculator.DefaultViewingSolidAngleSr;

        public const double KeepaliveMinutes = 5.0;

        /// <summary>Raised (on a worker thread) whenever any monitor's melanopic state was
        /// re-evaluated. UI subscribers marshal and throttle themselves.</summary>
        public event Action<MelanopicMonitorState>? MelanopicUpdated;

        public MelanopicMonitorService(GammaApplyService applyService)
        {
            _applyService = applyService ?? throw new ArgumentNullException(nameof(applyService));
            _applyService.StateApplied += OnStateApplied;

            _keepalive = new System.Timers.Timer(TimeSpan.FromMinutes(KeepaliveMinutes).TotalMilliseconds) { AutoReset = true };
            _keepalive.Elapsed += (_, _) => ResampleAll();
            _keepalive.Start();

            MelanopicDoseStore.RotateRetention();
        }

        /// <summary>Latest evaluated states, one per monitor.</summary>
        public IReadOnlyList<MelanopicMonitorState> CurrentStates
        {
            get { lock (_lock) { return _lastStates.Values.ToList(); } }
        }

        private void OnStateApplied(GammaApplyService.AppliedStateSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.MonitorDevicePath)) return;
            lock (_lock) { _lastSnapshots[snapshot.MonitorDevicePath] = snapshot; }
            Evaluate(snapshot, persist: true);
        }

        private void ResampleAll()
        {
            List<GammaApplyService.AppliedStateSnapshot> snapshots;
            lock (_lock) { snapshots = _lastSnapshots.Values.ToList(); }
            foreach (var snapshot in snapshots)
            {
                // Keepalive samples reuse the last state at the current wall clock so the
                // dose integral advances while nothing changes on screen.
                Evaluate(snapshot with { TimestampUtc = DateTime.UtcNow }, persist: true);
            }
        }

        private void Evaluate(GammaApplyService.AppliedStateSnapshot snapshot, bool persist)
        {
            if (_disposed) return;
            try
            {
                // Skip while a calibration owns the display: measurements through a bypassed
                // ramp are not the user's viewing state and would poison the dose curve.
                if (CalibrationStateManager.IsDeviceInBypass(snapshot.MonitorDevicePath)) return;

                var (spectra, provenance, hasSpectra) = ResolveSpectra(snapshot.CcssPath);

                double whiteNits = ColorAdjustments.ApplyDimmingNits(
                    snapshot.SdrWhiteLevel, snapshot.Brightness, snapshot.SdrWhiteLevel,
                    snapshot.UseLinearBrightness);

                var reading = MelanopicCalculator.Compute(
                    spectra,
                    (snapshot.GainR, snapshot.GainG, snapshot.GainB),
                    whiteNits,
                    ViewingSolidAngleSr,
                    hasSpectra);

                var uncertainty = UncertaintyBudget.CombineMelanopic(
                    reading, provenance, spectra.WhiteResidualFraction);

                var state = new MelanopicMonitorState(
                    snapshot.MonitorDevicePath,
                    snapshot.EffectiveKelvin,
                    reading,
                    uncertainty,
                    spectra.SourceName,
                    snapshot.IsHdrActive,
                    snapshot.TimestampUtc);

                lock (_lock) { _lastStates[snapshot.MonitorDevicePath] = state; }

                if (persist && double.IsFinite(reading.MelanopicEdiLux))
                {
                    MelanopicDoseStore.Append(new MelanopicDoseSample
                    {
                        TimestampUtc = snapshot.TimestampUtc,
                        MonitorDevicePath = snapshot.MonitorDevicePath,
                        MelanopicEdiLux = reading.MelanopicEdiLux,
                        EdiExpandedU = uncertainty.EdiExpandedU,
                        ReductionFraction = reading.ReductionFraction,
                        Kelvin = snapshot.EffectiveKelvin,
                        HasSpectra = hasSpectra,
                    });
                }

                MelanopicUpdated?.Invoke(state);
            }
            catch (Exception ex)
            {
                Log.Info($"MelanopicMonitorService: evaluation failed for {snapshot.MonitorDevicePath}: {ex.Message}");
            }
        }

        /// <summary>
        /// Spectra source resolution with honest provenance: a Gloam-captured CCSS (this
        /// panel) beats a user-loaded community CCSS (assumed same-model), beats the generic
        /// primaries fallback. Detection of a Gloam capture is by the file's ORIGINATOR tag.
        /// </summary>
        private static (CcssMelanopicEstimator.CcssSpectra Spectra,
            UncertaintyBudget.CcssProvenance Provenance, bool HasSpectra) ResolveSpectra(string? ccssPath)
        {
            var spectra = CcssMelanopicEstimator.TryLoadSpectra(ccssPath);
            if (spectra == null)
                return (MelanopicCalculator.GenericPrimaries(),
                    UncertaintyBudget.CcssProvenance.GenericOrOtherPanel, false);

            var provenance = UncertaintyBudget.CcssProvenance.DbMatchedSameModel;
            try
            {
                if (ccssPath != null)
                {
                    // Gloam's own SpectralCaptureService writes ORIGINATOR "Gloam" — that
                    // file measured THIS unit's spectra.
                    using var reader = new StreamReader(ccssPath);
                    for (int i = 0; i < 12 && reader.ReadLine() is { } line; i++)
                    {
                        if (line.Contains("ORIGINATOR", StringComparison.OrdinalIgnoreCase) &&
                            line.Contains("Gloam", StringComparison.OrdinalIgnoreCase))
                        {
                            provenance = UncertaintyBudget.CcssProvenance.UserCapturedThisPanel;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Provenance stays at the conservative DB classification.
            }

            return (spectra, provenance, true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _applyService.StateApplied -= OnStateApplied;
            _keepalive.Stop();
            _keepalive.Dispose();
        }
    }
}
