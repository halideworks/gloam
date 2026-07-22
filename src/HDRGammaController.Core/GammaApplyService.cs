using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Owns the policy for applying gamma to monitors: resolves the effective
    /// calibration (saved profile + night mode + app exclusions), coalesces rapid
    /// requests per monitor, and persists profile changes. Extracted from
    /// TrayViewModel so the apply pipeline has a single owner with no UI
    /// dependencies.
    /// </summary>
    public class GammaApplyService : IDisposable
    {
        private readonly DispwinRunner _dispwinRunner;
        private readonly SettingsManager _settingsManager;
        private readonly NightModeService _nightModeService;
        private readonly Func<MonitorInfo, MonitorProfileData?, GamerCalibrationStatus> _calibrationStatusResolver;

        // Ramp guard: periodically verify the hardware still holds what we applied
        // and restore it when a fullscreen game or driver event stomps the VCGT.
        // Readback is ~free, so a 10s cadence costs nothing measurable.
        private readonly System.Timers.Timer _rampGuard;

        // Per-monitor coalescer: rapid slider drags collapse to one dispwin call per
        // completed invocation instead of queueing a backlog. See LatestValueCoalescer
        // for the concurrency contract and its tests for the proof.
        private readonly LatestValueCoalescer<IntPtr, (MonitorInfo Monitor, GammaMode Mode, CalibrationSettings Calibration, double WhiteLevel)> _coalescer;

        private readonly object _policyLock = new();
        private HashSet<IntPtr> _blockedMonitors = new HashSet<IntPtr>();
        private Dictionary<IntPtr, ActiveGamerSession> _activeGamerSessions = new();
        private int _disposed;

        private sealed record ActiveGamerSession(
            MonitorInfo Monitor,
            GamerProfileRule Profile,
            int LockedKelvin,
            DateTime StartedUtc,
            int SignalKey,
            IReadOnlyList<GamerSignalDiagnostic> Diagnostics);

        /// <summary>Hotkey override: forces day mode regardless of the schedule.</summary>
        public bool NightModeManuallyDisabled { get; set; }

        public GammaApplyService(DispwinRunner dispwinRunner, SettingsManager settingsManager, NightModeService nightModeService)
            : this(dispwinRunner, settingsManager, nightModeService, null)
        {
        }

        internal GammaApplyService(
            DispwinRunner dispwinRunner,
            SettingsManager settingsManager,
            NightModeService nightModeService,
            Func<MonitorInfo, MonitorProfileData?, GamerCalibrationStatus>? calibrationStatusResolver)
        {
            _dispwinRunner = dispwinRunner ?? throw new ArgumentNullException(nameof(dispwinRunner));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _nightModeService = nightModeService ?? throw new ArgumentNullException(nameof(nightModeService));
            _calibrationStatusResolver = calibrationStatusResolver ?? ResolveCalibrationStatus;

            _coalescer = new LatestValueCoalescer<IntPtr, (MonitorInfo, GammaMode, CalibrationSettings, double)>(
                (_, item, cancellationToken) =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        // Coalescer-time bypass re-check (TOCTOU close): RequestApply checked bypass
                        // on the caller thread, but the coalesced work runs later — a calibration may
                        // have called EnterBypassMode in between. Re-evaluate immediately before the
                        // hardware write so we never stomp the identity ramp the colorimeter is reading.
                        if (CalibrationStateManager.IsDeviceInBypass(item.Item1))
                        {
                            Log.Info($"GammaApplyService: skipping coalesced apply for {item.Item1.FriendlyName} (calibration bypass active)");
                            return;
                        }

                        // skipIfBypassed: re-check bypass inside ApplyGamma immediately before the
                        // hardware write, closing the window between this coalesced callback and the
                        // syscall (a calibration could call EnterBypassMode during LUT generation).
                        _dispwinRunner.ApplyGamma(item.Item1, item.Item2, item.Item4, item.Item3,
                            calibrationProfile: null, skipIfBypassed: true, cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"GammaApplyService: apply failed ({item.Item1.FriendlyName}): {ex.Message}");
                    }
                },
                // Hard floor on hardware gamma-write rate per monitor. A night-mode fade only
                // needs a couple of writes per second; this caps any runaway trigger (e.g. a
                // display-change feedback loop) so SetDeviceGammaRamp can never be hammered
                // fast enough to stall the compositor and hang the machine.
                minIntervalMs: 250);

            _rampGuard = new System.Timers.Timer(10000) { AutoReset = true };
            _rampGuard.Elapsed += (_, _) =>
            {
                try { _dispwinRunner.VerifyAndRestoreRamps(); }
                catch (Exception ex) { Log.Error($"GammaApplyService: ramp guard tick failed: {ex.Message}"); }
            };
            _rampGuard.Start();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _rampGuard.Stop();
            _rampGuard.Dispose();
            _coalescer.Dispose();
        }

        /// <summary>
        /// Replaces the blocked-monitor set (app exclusions). Returns true when the
        /// set actually changed, i.e. the caller should trigger a re-apply.
        /// </summary>
        public bool UpdateBlockedMonitors(HashSet<IntPtr> blocked)
        {
            ArgumentNullException.ThrowIfNull(blocked);
            lock (_policyLock)
            {
                if (_blockedMonitors.SetEquals(blocked)) return false;
                _blockedMonitors = new HashSet<IntPtr>(blocked);
                return true;
            }
        }

        /// <summary>
        /// Atomically replaces foreground-game assignments. A semantically identical update
        /// preserves each session's captured kelvin/start time, so focus-hook duplicates cannot
        /// move a Gameplay Lock underneath the player.
        /// </summary>
        public bool UpdateActiveGamerSessions(IEnumerable<GamerSessionAssignment>? assignments)
        {
            var eligible = (assignments ?? Array.Empty<GamerSessionAssignment>())
                .Where(a => a?.Monitor != null && a.Profile != null && a.Profile.Enabled)
                .Select(a => new GamerSessionAssignment(a.Monitor, a.Profile.Sanitized()))
                .Where(a => GamerExecutableSafety.IsSafeProfileTarget(
                    a.Profile.AppName, a.Profile.DisplayName))
                .ToList();

            // A game may own several displays, but several games may never own the display
            // pipeline together. This is a hard Core invariant rather than a UI convention:
            // even a buggy caller, stale settings file, or racing foreground event is reduced
            // to one executable before any output state is built.
            string? owner = eligible.Select(a => a.Profile.AppName).FirstOrDefault();
            int ownerCount = eligible.Select(a => a.Profile.AppName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (ownerCount > 1)
                Log.Info($"GammaApplyService: rejected {ownerCount - 1} competing gamer-session owner(s); '{owner}' retained.");

            var incoming = owner == null
                ? new List<GamerSessionAssignment>()
                : eligible
                    .Where(a => a.Profile.AppName.Equals(owner, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(a => a.Monitor.HMonitor)
                    .Select(g => g.Last())
                    .ToList();

            bool changed;
            IReadOnlyList<GamerSessionSnapshot> snapshots;
            lock (_policyLock)
            {
                var next = new Dictionary<IntPtr, ActiveGamerSession>();
                int scheduledKelvin = _nightModeService.CurrentNightKelvin;

                foreach (var assignment in incoming)
                {
                    MonitorInfo monitor = assignment.Monitor;
                    GamerProfileRule profile = assignment.Profile.Sanitized();
                    var monitorProfile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath);
                    GamerCalibrationStatus calibrationStatus = _calibrationStatusResolver(monitor, monitorProfile);
                    int signalKey = GamerSignalKey(monitor, monitorProfile, calibrationStatus);

                    if (_activeGamerSessions.TryGetValue(monitor.HMonitor, out var previous) &&
                        previous.Profile.SemanticallyEquals(profile) &&
                        previous.SignalKey == signalKey &&
                        string.Equals(previous.Monitor.MonitorDevicePath, monitor.MonitorDevicePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // Refresh the monitor object while preserving the locked session facts.
                        next[monitor.HMonitor] = previous with { Monitor = monitor };
                        continue;
                    }

                    var diagnostics = GamerSignalDiagnostics.Evaluate(
                        profile, monitor, monitorProfile, calibrationStatus).ToArray();
                    next[monitor.HMonitor] = new ActiveGamerSession(
                        monitor,
                        profile,
                        scheduledKelvin,
                        DateTime.UtcNow,
                        signalKey,
                        diagnostics);
                }

                changed = !GamerSessionMapsEqual(_activeGamerSessions, next);
                if (!changed) return false;
                _activeGamerSessions = next;
                snapshots = SnapshotGamerSessionsLocked();
            }

            try { GamerSessionsChanged?.Invoke(snapshots); }
            catch (Exception ex) { Log.Info($"GammaApplyService: gamer-session subscriber failed: {ex.Message}"); }
            return true;
        }

        public IReadOnlyList<GamerSessionSnapshot> ActiveGamerSessions
        {
            get { lock (_policyLock) { return SnapshotGamerSessionsLocked(); } }
        }

        public string? ActiveGamerOwner
        {
            get
            {
                lock (_policyLock)
                    return _activeGamerSessions.Values.Select(session => session.Profile.AppName).FirstOrDefault();
            }
        }

        public event Action<IReadOnlyList<GamerSessionSnapshot>>? GamerSessionsChanged;

        private IReadOnlyList<GamerSessionSnapshot> SnapshotGamerSessionsLocked() =>
            _activeGamerSessions.Values
                .OrderBy(s => s.Monitor.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .Select(CreateGamerSessionSnapshot)
                .ToArray();

        private GamerSessionSnapshot CreateGamerSessionSnapshot(ActiveGamerSession session)
        {
            GammaMode effectiveGamma = session.Profile.OverrideGamma
                ? session.Profile.GammaMode
                : _settingsManager.GetMonitorProfile(session.Monitor.MonitorDevicePath)?.GammaMode
                    ?? session.Monitor.CurrentGamma;
            return new GamerSessionSnapshot(
                session.Monitor.HMonitor,
                session.Monitor.MonitorDevicePath,
                session.Monitor.FriendlyName,
                session.Profile.AppName,
                session.Profile.DisplayName,
                session.Profile.PictureIntent,
                session.Profile.GameplayLock,
                ResolveGamerKelvin(session, _nightModeService.CurrentNightKelvin),
                effectiveGamma,
                session.Profile.ShadowDetailStrength,
                session.Profile.ShadowDetailPivot,
                session.Profile.NightPolicy,
                session.StartedUtc,
                session.Diagnostics);
        }

        private static bool GamerSessionMapsEqual(
            IReadOnlyDictionary<IntPtr, ActiveGamerSession> left,
            IReadOnlyDictionary<IntPtr, ActiveGamerSession> right)
        {
            if (left.Count != right.Count) return false;
            foreach (var pair in right)
            {
                if (!left.TryGetValue(pair.Key, out var existing)) return false;
                if (!existing.Profile.SemanticallyEquals(pair.Value.Profile) ||
                    existing.SignalKey != pair.Value.SignalKey ||
                    !string.Equals(existing.Monitor.MonitorDevicePath, pair.Value.Monitor.MonitorDevicePath,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static int GamerSignalKey(
            MonitorInfo monitor,
            MonitorProfileData? monitorProfile,
            GamerCalibrationStatus calibrationStatus)
        {
            var hash = new HashCode();
            hash.Add(monitor.IsHdrActive);
            hash.Add(monitor.DxgiColorSpace);
            hash.Add(monitor.BitsPerColor);
            hash.Add((int)Math.Round(monitor.SdrWhiteLevel));
            hash.Add((int)Math.Round(monitor.HdrPeakNits));
            hash.Add(monitor.MonitorDevicePath, StringComparer.OrdinalIgnoreCase);
            hash.Add(monitorProfile?.GammaMode);
            hash.Add(monitorProfile?.CalibrationProfileId, StringComparer.OrdinalIgnoreCase);
            hash.Add(monitorProfile?.Mhc2ProfileName, StringComparer.OrdinalIgnoreCase);
            hash.Add(calibrationStatus.Fingerprint);
            return hash.ToHashCode();
        }

        private GamerCalibrationStatus ResolveCalibrationStatus(
            MonitorInfo monitor,
            MonitorProfileData? monitorProfile)
        {
            if (monitorProfile == null) return GamerCalibrationStatus.None;

            string? mhc2Name = monitorProfile.Mhc2ProfileName;
            if (!string.IsNullOrWhiteSpace(mhc2Name))
            {
                bool verified = AdvancedColorProfileAssociation.TryIsVerifiedCurrentUserDefault(
                    monitor, mhc2Name, out bool isActive, out string? error);
                return new GamerCalibrationStatus(
                    HasMeasuredCalibration: true,
                    IsActive: verified && isActive,
                    WasVerified: verified,
                    ProfileIdentity: mhc2Name,
                    VerificationError: error);
            }

            string? profileId = monitorProfile.CalibrationProfileId;
            if (string.IsNullOrWhiteSpace(profileId)) return GamerCalibrationStatus.None;
            bool usable = _settingsManager.GetActiveCalibrationProfile(monitor.MonitorDevicePath) != null;
            return new GamerCalibrationStatus(
                HasMeasuredCalibration: true,
                IsActive: usable,
                WasVerified: true,
                ProfileIdentity: profileId);
        }

        public void RequestApply(
            MonitorInfo monitor,
            GammaMode mode,
            CalibrationSettings? manualCalibration = null,
            int? nightKelvinOverride = null,
            NightModeSettings? nightModeSettingsOverride = null)
        {
            // Mid-calibration guard: a calibration has bypassed this display's corrections so
            // the colorimeter measures the raw panel. The live apply path (slider, night mode,
            // app exclusions, display-change / resume re-apply) must not stomp that in-flight
            // ramp. Every live caller funnels through here, so this single skip also covers
            // ApplyAll and the InvalidateAppliedState()+ApplyAll() resume path.
            if (CalibrationStateManager.IsDeviceInBypass(monitor))
            {
                Log.Info($"GammaApplyService: skipping apply for {monitor.FriendlyName} (calibration bypass active)");
                return;
            }

            // Resolve the effective calibration synchronously on the caller's thread. This
            // keeps us reading fresh state (settings, game session, night mode, exclusions)
            // at the request boundary even if coalesced hardware work runs later.
            ActiveGamerSession? gamerSession;
            bool nightModeBlocked;
            lock (_policyLock)
            {
                _activeGamerSessions.TryGetValue(monitor.HMonitor, out gamerSession);
                nightModeBlocked = _blockedMonitors.Contains(monitor.HMonitor);
            }

            // Explicit Settings-window previews own the wire temporarily. The persistent
            // gamer session returns on the next normal ApplyAll; this prevents a foreground
            // game from making monitor controls appear broken.
            bool explicitPreview = manualCalibration != null || nightKelvinOverride.HasValue;
            if (explicitPreview) gamerSession = null;

            int currentKelvin = nightKelvinOverride ?? _nightModeService.CurrentNightKelvin;
            if (nightModeBlocked) currentKelvin = 6500;

            GammaMode effectiveMode = mode;
            NightModeSettings nightSettings = nightModeSettingsOverride ?? _settingsManager.NightMode;
            if (gamerSession != null)
            {
                currentKelvin = ResolveGamerKelvin(gamerSession, currentKelvin);
                if (gamerSession.Profile.OverrideGamma)
                    effectiveMode = gamerSession.Profile.GammaMode;
                if (gamerSession.Profile.NightPolicy == GamerNightPolicy.NightOps)
                    nightSettings = BuildNightOpsSettings(nightSettings, gamerSession.Profile);
            }

            // The global hotkey is the final authority and can always recover neutral output,
            // including from a Night Ops game profile.
            if (NightModeManuallyDisabled) currentKelvin = 6500;

            bool nightModeActive = nightKelvinOverride.HasValue || currentKelvin < 6450;

            // Composition with a native MHC2 calibration: the profile corrects the PANEL
            // (gamut, white point, tone tracking) at the compositor, which makes the display
            // behave like the ideal panel the mode LUTs already assume — so the user's gamma
            // preference composes CORRECTLY on top and must not be forced off. (The old
            // force-to-WindowsDefault here predates that: it guarded against the measured
            // VCGT correction curves double-applying, but the live path never passes those —
            // RequestApply always uses the profile-less ApplyGamma overload.)

            CalibrationSettings calibration;
            if (manualCalibration != null)
            {
                // Clone so later night-mode / offset mutations don't leak back into the caller's object.
                calibration = manualCalibration.Clone();
            }
            else
            {
                var profile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath) ?? new MonitorProfileData();
                calibration = profile.ToCalibrationSettings();
            }

            if (gamerSession != null)
            {
                calibration.ShadowDetailStrength = gamerSession.Profile.ShadowDetailStrength;
                calibration.ShadowDetailPivot = gamerSession.Profile.ShadowDetailPivot;
            }

            if (nightModeActive)
            {
                ApplyNightModeToCalibration(calibration, currentKelvin, nightSettings);
                calibration.NightMelanopicCoefficients = CcssMelanopicEstimator.TryLoad(calibration.NightModeCcssPath);

                // Constant-Y headroom ceiling, per monitor: on HDR the boost may use the
                // panel's real headroom above SDR white (capped 2× — typical need is only
                // 1.15–1.45×); on SDR the 256-entry GDI ramp clips at 1.0 and the ceiling
                // stays there (dimming still creates in-range headroom). Unreported peak
                // (0) falls back to 1.0 — never bloom past an unknown panel limit.
                double sdrWhite = MonitorInfo.SanitizeSdrWhiteLevel(monitor.SdrWhiteLevel);
                calibration.NightLuminanceCeiling =
                    calibration.PreserveNightLuminance &&
                    monitor.IsHdrActive && monitor.HdrPeakNits > sdrWhite
                        ? Math.Min(2.0, monitor.HdrPeakNits / sdrWhite)
                        : 1.0;

                ApplyDoseCeiling(calibration, nightSettings, monitor, sdrWhite);
            }

            // Extended range: 1900K..10000K. See CalibrationSettings.Temperature
            // for why this goes past the slider's nominal -50..+50.
            calibration.Temperature = Math.Clamp(
                calibration.Temperature,
                CalibrationSettings.MinimumTemperatureScale,
                CalibrationSettings.MaximumTemperatureScale);

            // Update persistent state only for real (non-preview) applies.
            if (manualCalibration == null)
            {
                monitor.CurrentGamma = mode;
                if (!string.IsNullOrEmpty(monitor.MonitorDevicePath))
                {
                    _settingsManager.SetProfileForMonitor(monitor.MonitorDevicePath, mode);
                }
            }

            PublishSnapshot(monitor, calibration);

            _coalescer.Submit(monitor.HMonitor, (monitor, effectiveMode, calibration, MonitorInfo.SanitizeSdrWhiteLevel(monitor.SdrWhiteLevel)));
        }

        /// <summary>
        /// What the live melanopic dashboard consumes: the resolved applied state per monitor
        /// (roadmap 3.1). Gains are white-SHAPE multipliers with brightness excluded — see
        /// <see cref="ColorAdjustments.GetAppliedWhiteShapeMultipliers"/>.
        /// </summary>
        public readonly record struct AppliedStateSnapshot(
            IntPtr HMonitor,
            string MonitorDevicePath,
            int EffectiveKelvin,
            NightModeAlgorithm Algorithm,
            double GainR,
            double GainG,
            double GainB,
            double Brightness,
            bool UseLinearBrightness,
            double SdrWhiteLevel,
            bool IsHdrActive,
            string? CcssPath,
            DateTime TimestampUtc);

        /// <summary>Raised once per EFFECTIVE state change (deduped) from the synchronous
        /// resolve in <see cref="RequestApply"/> — a state-change signal, never per ramp write.</summary>
        public event Action<AppliedStateSnapshot>? StateApplied;

        private readonly Dictionary<IntPtr, int> _lastSnapshotKey = new();
        private readonly object _snapshotLock = new();

        private void PublishSnapshot(MonitorInfo monitor, CalibrationSettings calibration)
        {
            var handler = StateApplied;
            if (handler == null) return;

            try
            {
                // The wire basis the eye actually sees: Rec.2020 container on HDR, sRGB on SDR.
                var basis = monitor.IsHdrActive ? NightBasis.Rec2020 : NightBasis.Srgb;
                var gains = ColorAdjustments.GetAppliedWhiteShapeMultipliers(calibration, basis);
                var snapshot = new AppliedStateSnapshot(
                    monitor.HMonitor,
                    monitor.MonitorDevicePath,
                    ColorAdjustments.TemperatureScaleToKelvin(
                        ColorAdjustments.ComposeTemperatureScaleMired(calibration.Temperature, calibration.TemperatureOffset)),
                    calibration.Algorithm,
                    gains.R, gains.G, gains.B,
                    calibration.Brightness,
                    calibration.UseLinearBrightness,
                    MonitorInfo.SanitizeSdrWhiteLevel(monitor.SdrWhiteLevel),
                    monitor.IsHdrActive,
                    calibration.NightModeCcssPath,
                    DateTime.UtcNow);

                // Dedupe on the effective content (excluding the timestamp) so a night-mode
                // fade only publishes when something perceptible changed.
                int key = HashCode.Combine(
                    snapshot.EffectiveKelvin, snapshot.Algorithm,
                    HashCode.Combine((int)Math.Round(gains.R * 1000), (int)Math.Round(gains.G * 1000),
                        (int)Math.Round(gains.B * 1000)),
                    (int)Math.Round(snapshot.Brightness), snapshot.IsHdrActive,
                    (int)Math.Round(snapshot.SdrWhiteLevel), snapshot.CcssPath?.GetHashCode(StringComparison.OrdinalIgnoreCase) ?? 0);
                lock (_snapshotLock)
                {
                    if (_lastSnapshotKey.TryGetValue(monitor.HMonitor, out int last) && last == key)
                        return;
                    _lastSnapshotKey[monitor.HMonitor] = key;
                }

                handler(snapshot);
            }
            catch (Exception ex)
            {
                Log.Info($"GammaApplyService: state snapshot publish failed: {ex.Message}");
            }
        }

        private readonly Dictionary<IntPtr, int> _doseLogKeys = new();
        private readonly object _doseLogLock = new();

        /// <summary>
        /// Dose-based circadian ceiling (roadmap 3.2): when the scheduled night state's
        /// melanopic EDI exceeds the user's ceiling, replace it with the governor's
        /// perceptually-cheapest compliant (kelvin, brightness) operating point. Runs per
        /// monitor because dose depends on the panel's spectra, white level and brightness.
        /// The solved kelvin already includes any per-monitor temperature trim (it was fed
        /// the COMPOSED effective kelvin), so the trim offset is folded to zero.
        /// </summary>
        private void ApplyDoseCeiling(
            CalibrationSettings calibration, NightModeSettings nightSettings,
            MonitorInfo monitor, double sdrWhite)
        {
            double ceiling = NightModeSettings.ClampMelanopicCeiling(nightSettings.MelanopicEdiCeiling);
            if (ceiling <= 0) return;

            try
            {
                var spectra = CcssMelanopicEstimator.TryLoadSpectra(calibration.NightModeCcssPath)
                    ?? MelanopicCalculator.GenericPrimaries(wideGamut: monitor.IsHdrActive);

                int effectiveKelvin = ColorAdjustments.TemperatureScaleToKelvin(
                    ColorAdjustments.ComposeTemperatureScaleMired(calibration.Temperature, calibration.TemperatureOffset));

                var solution = CircadianDoseGovernor.Solve(
                    spectra,
                    calibration.Algorithm,
                    calibration.PerceptualStrength,
                    calibration.UseUltraWarmMode,
                    calibration.PreserveNightLuminance,
                    effectiveKelvin,
                    calibration.Brightness,
                    sdrWhite,
                    MelanopicCalculator.DefaultViewingSolidAngleSr,
                    ceiling,
                    calibration.NightMelanopicCoefficients);

                if (!solution.Adjusted) return;

                calibration.Temperature = (solution.Kelvin - 6500) / 70.0;
                calibration.TemperatureOffset = 0.0;
                calibration.Brightness = solution.BrightnessPercent;

                // Log once per distinct solution per monitor — fades re-solve at up to 4 Hz.
                int logKey = HashCode.Combine(solution.Kelvin, (int)solution.BrightnessPercent, solution.CeilingMet);
                lock (_doseLogLock)
                {
                    if (_doseLogKeys.TryGetValue(monitor.HMonitor, out int last) && last == logKey) return;
                    _doseLogKeys[monitor.HMonitor] = logKey;
                }
                Log.Info($"GammaApplyService: dose ceiling {ceiling:F1} mel-lx on {monitor.FriendlyName}: " +
                         $"{effectiveKelvin}K → {solution.Kelvin}K, brightness → {solution.BrightnessPercent:F0}% " +
                         $"(est. {solution.MelanopicEdiLux:F1} mel-lx, ΔE′ {solution.DeltaEPrimeFromScheduled:F1}" +
                         $"{(solution.CeilingMet ? string.Empty : "; ceiling unreachable, best effort")})");
            }
            catch (Exception ex)
            {
                // The governor must never take down the apply path — worst case the
                // scheduled state applies un-governed.
                Log.Info($"GammaApplyService: dose ceiling evaluation failed: {ex.Message}");
            }
        }

        internal static void ApplyNightModeToCalibration(CalibrationSettings calibration, int currentKelvin, NightModeSettings nightModeSettings)
        {
            // Compose the night shift with any user temperature in mired space (reciprocal
            // Kelvin): summing on the linear (K−6500)/70 scale makes the combined result
            // over-warm, because the same scale distance is a far larger perceptual step at
            // the warm end. When the base temperature is 0 this reduces to the plain shift.
            double nightShift = (currentKelvin - 6500) / 70.0;
            calibration.Temperature = ColorAdjustments.ComposeTemperatureScaleMired(calibration.Temperature, nightShift);
            calibration.Algorithm = nightModeSettings.Algorithm;
            calibration.UseUltraWarmMode = nightModeSettings.UseUltraWarmMode;
            calibration.PerceptualStrength = nightModeSettings.PerceptualStrength;
            calibration.PreserveNightLuminance = nightModeSettings.PreserveLuminance;
        }

        private static int ResolveGamerKelvin(ActiveGamerSession session, int scheduledKelvin)
        {
            GamerProfileRule profile = session.Profile;
            int baseline = profile.GameplayLock
                ? NightModeSettings.ClampKelvin(session.LockedKelvin)
                : NightModeSettings.ClampKelvin(scheduledKelvin);

            return profile.NightPolicy switch
            {
                GamerNightPolicy.ForceDaylight => 6500,
                // Night Ops never cools an already-warmer schedule. This preserves a user's
                // stricter late-night state while applying the profile during daytime.
                GamerNightPolicy.NightOps => Math.Min(baseline, profile.NightOpsKelvin),
                _ => baseline
            };
        }

        internal static NightModeSettings BuildNightOpsSettings(
            NightModeSettings source,
            GamerProfileRule profile)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(profile);
            profile = profile.Sanitized();

            return new NightModeSettings
            {
                Enabled = true,
                ManualOverrideEnabled = true,
                UseAutoSchedule = source.UseAutoSchedule,
                Latitude = source.Latitude,
                Longitude = source.Longitude,
                StartTime = source.StartTime,
                EndTime = source.EndTime,
                TemperatureKelvin = profile.NightOpsKelvin,
                FadeMinutes = source.FadeMinutes,
                Algorithm = NightModeAlgorithm.Perceptual,
                UseUltraWarmMode = false,
                PerceptualStrength = profile.NightOpsStrength,
                PreserveLuminance = false,
                MelanopicEdiCeiling = profile.NightOpsMelanopicCeiling,
                Schedule = (source.Schedule ?? new List<NightModeSchedulePoint>())
                    .Where(point => point != null)
                    .Select(point => new NightModeSchedulePoint
                    {
                        TriggerType = point.TriggerType,
                        Time = point.Time,
                        OffsetMinutes = point.OffsetMinutes,
                        TargetKelvin = point.TargetKelvin,
                        FadeMinutes = point.FadeMinutes
                    })
                    .ToList()
            };
        }

        public void ApplyAll(IEnumerable<MonitorInfo> monitors)
        {
            int currentKelvin = _nightModeService.CurrentNightKelvin;
            if (NightModeManuallyDisabled) currentKelvin = 6500;
            bool nightModeActive = currentKelvin < 6450;

            foreach (var monitor in monitors)
            {
                var profile = _settingsManager.GetMonitorProfile(monitor.MonitorDevicePath);
                var mode = profile?.GammaMode ?? monitor.CurrentGamma;

                RequestApply(monitor, mode);
            }
        }

        /// <summary>Resets all gamma tables (panic). Failures are logged, never thrown.</summary>
        public void ClearAll(IEnumerable<MonitorInfo> monitors)
        {
            foreach (var monitor in monitors)
            {
                try { _dispwinRunner.ClearGamma(monitor); }
                catch (Exception ex)
                {
                    Log.Error($"GammaApplyService: clear failed ({monitor.FriendlyName}): {ex.Message}");
                }
            }
        }

        /// <summary>See <see cref="DispwinRunner.InvalidateAppliedState"/>.</summary>
        public void InvalidateAppliedState() => _dispwinRunner.InvalidateAppliedState();
    }
}
