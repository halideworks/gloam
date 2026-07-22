using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Interop;

namespace HDRGammaController.Core
{
    public sealed record GamerPolicyChange(
        string ForegroundApp,
        int NightBlockedDisplayCount,
        int GamerDisplayCount,
        bool GamerSessionChanged,
        IReadOnlyList<GamerSessionSnapshot> ActiveSessions);

    /// <summary>
    /// Single owner for Game Mode persistence and foreground display policy. The tray and
    /// dashboard are views over this coordinator; neither is allowed to independently
    /// decide which executable owns the output pipeline.
    /// </summary>
    public sealed class GamerModeCoordinator : IDisposable
    {
        private const string ForegroundKey = "foreground";
        private readonly SettingsManager _settings;
        private readonly GammaApplyService _applyService;
        private readonly Func<IReadOnlyList<MonitorInfo>> _monitorSnapshot;
        private readonly LatestValueCoalescer<string, ForegroundObservation> _foregroundCoalescer;
        private readonly object _lastLock = new();
        private readonly object _evaluationLock = new();
        private ForegroundObservation _last = ForegroundObservation.Empty;
        private int _emergencySuspended;
        private int _disposed;

        private sealed record ForegroundObservation(
            string AppName,
            string? ExecutablePath,
            Dxgi.RECT? Bounds,
            TimeSpan SettleDelay)
        {
            internal static ForegroundObservation Empty { get; } =
                new(string.Empty, null, null, TimeSpan.Zero);
        }

        public GamerModeCoordinator(
            SettingsManager settings,
            GammaApplyService applyService,
            Func<IReadOnlyList<MonitorInfo>> monitorSnapshot)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _applyService = applyService ?? throw new ArgumentNullException(nameof(applyService));
            _monitorSnapshot = monitorSnapshot ?? throw new ArgumentNullException(nameof(monitorSnapshot));
            _foregroundCoalescer = new LatestValueCoalescer<string, ForegroundObservation>(
                (_, observation, token) =>
                {
                    if (observation.SettleDelay > TimeSpan.Zero &&
                        token.WaitHandle.WaitOne(observation.SettleDelay))
                        return;
                    if (!token.IsCancellationRequested)
                        Evaluate(observation);
                });
            _settings.GamerSettingsChanged += OnGamerSettingsChanged;
            _settings.MonitorProfileChanged += OnMonitorProfileChanged;
        }

        public event Action<GamerPolicyChange>? PolicyChanged;

        public bool Enabled => _settings.GamerModeEnabled &&
            Volatile.Read(ref _emergencySuspended) == 0;
        public IReadOnlyList<GamerProfileRule> Profiles => _settings.GamerProfiles;
        public IReadOnlyList<GamerSessionSnapshot> ActiveSessions => _applyService.ActiveGamerSessions;

        public string LastExternalForegroundApp
        {
            get { lock (_lastLock) return _last.AppName; }
        }

        public bool TrySetEnabled(bool enabled)
        {
            if (!_settings.TrySetGamerModeEnabled(enabled)) return false;
            if (enabled && Interlocked.Exchange(ref _emergencySuspended, 0) != 0)
                ReevaluateLatest();
            else if (!enabled)
                Interlocked.Exchange(ref _emergencySuspended, 0);
            return true;
        }

        public bool TrySetProfiles(IEnumerable<GamerProfileRule>? profiles) =>
            _settings.TrySetGamerProfiles(profiles);

        public void ObserveForeground(string appName, string? executablePath, Dxgi.RECT? appBounds)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            appName = AppExclusionRule.NormalizeAppName(appName);
            if (appName.Equals("Gloam.exe", StringComparison.OrdinalIgnoreCase)) return;

            bool candidateIsGame = Enabled &&
                GamerExecutableSafety.IsSafeProfileTarget(appName) &&
                _settings.GamerProfiles.Any(profile =>
                    profile.Enabled && MatchesForeground(profile, appName, executablePath));
            bool gameAlreadyActive = _applyService.ActiveGamerOwner != null;
            TimeSpan delay = candidateIsGame
                ? TimeSpan.FromMilliseconds(350)
                : gameAlreadyActive
                    ? TimeSpan.FromMilliseconds(900)
                    : TimeSpan.FromMilliseconds(250);

            var observation = new ForegroundObservation(appName, executablePath, appBounds, delay);
            lock (_lastLock) _last = observation;
            _foregroundCoalescer.Submit(ForegroundKey, observation);
        }

        /// <summary>Re-runs the latest foreground decision without focus churn.</summary>
        public void ReevaluateLatest(bool immediate = false)
        {
            ForegroundObservation observation;
            lock (_lastLock) observation = _last;
            if (immediate)
            {
                _foregroundCoalescer.Cancel(ForegroundKey);
                Evaluate(observation with { SettleDelay = TimeSpan.Zero });
            }
            else
            {
                _foregroundCoalescer.Submit(ForegroundKey, observation);
            }
        }

        public void CancelPendingForeground() => _foregroundCoalescer.Cancel(ForegroundKey);

        /// <summary>
        /// In-memory fail-safe used by the panic hotkey. It works even if the settings
        /// directory is unavailable, ensuring a saved profile cannot re-arm itself after
        /// the emergency gamma clear.
        /// </summary>
        public void EmergencySuspend()
        {
            Interlocked.Exchange(ref _emergencySuspended, 1);
            ReevaluateLatest(immediate: true);
        }

        private void OnGamerSettingsChanged()
        {
            // Disabling is an emergency boundary; no focus grace period is allowed to leave
            // a ramp owned by a game after the universal pause says it is off.
            ReevaluateLatest(immediate: !Enabled);
        }

        private void OnMonitorProfileChanged(string monitorDevicePath)
        {
            if (_applyService.ActiveGamerSessions.Any(session =>
                    session.MonitorDevicePath.Equals(
                        monitorDevicePath, StringComparison.OrdinalIgnoreCase)))
                ReevaluateLatest(immediate: true);
        }

        private void Evaluate(ForegroundObservation observation)
        {
            try
            {
                lock (_evaluationLock)
                {
                    IReadOnlyList<MonitorInfo> activeMonitors =
                        _monitorSnapshot() ?? Array.Empty<MonitorInfo>();
                    AppExclusionRule? exclusion = _settings.ExcludedApps.FirstOrDefault(rule =>
                        rule.AppName.Equals(observation.AppName, StringComparison.OrdinalIgnoreCase));
                    var blocked = new HashSet<IntPtr>();
                    if (exclusion?.FullDisable == true)
                    {
                        foreach (MonitorInfo monitor in activeMonitors)
                            blocked.Add(monitor.HMonitor);
                    }
                    else if (exclusion != null && observation.Bounds.HasValue)
                    {
                        foreach (MonitorInfo monitor in activeMonitors)
                        {
                            if (Intersects(observation.Bounds.Value, monitor.MonitorBounds))
                                blocked.Add(monitor.HMonitor);
                        }
                    }

                    var assignments = new List<GamerSessionAssignment>();
                    GamerProfileRule? profile = null;
                    if (Enabled)
                    {
                        profile = _settings.GamerProfiles.FirstOrDefault(candidate =>
                            candidate.Enabled &&
                            GamerExecutableSafety.IsSafeProfileTarget(candidate.AppName, candidate.DisplayName) &&
                            MatchesForeground(candidate, observation.AppName, observation.ExecutablePath));
                        if (profile != null)
                        {
                            foreach (MonitorInfo monitor in activeMonitors)
                            {
                                if (TargetsMonitor(profile, monitor, observation.Bounds, activeMonitors.Count))
                                    assignments.Add(new GamerSessionAssignment(monitor, profile));
                            }
                        }
                    }

                    bool blockChanged = _applyService.UpdateBlockedMonitors(blocked);
                    bool gamerChanged = _applyService.UpdateActiveGamerSessions(assignments);
                    if (gamerChanged && profile != null && assignments.Count > 0)
                    {
                        string activatedApp = observation.AppName;
                        _ = Task.Run(() => _settings.MarkGamerProfileUsed(activatedApp, DateTime.UtcNow));
                    }

                    if (!blockChanged && !gamerChanged) return;
                    IReadOnlyList<GamerSessionSnapshot> sessions = _applyService.ActiveGamerSessions;
                    Log.Info($"GamerModeCoordinator: foreground policy changed. App={observation.AppName}, " +
                             $"night-blocked={blocked.Count}, gamer-displays={assignments.Count}");
                    try
                    {
                        PolicyChanged?.Invoke(new GamerPolicyChange(
                            observation.AppName,
                            blocked.Count,
                            assignments.Count,
                            gamerChanged,
                            sessions));
                    }
                    catch (Exception ex)
                    {
                        Log.Info($"GamerModeCoordinator: policy subscriber failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"GamerModeCoordinator: foreground evaluation failed: {ex.Message}");
            }
        }

        internal static bool TargetsMonitor(
            GamerProfileRule profile,
            MonitorInfo monitor,
            Dxgi.RECT? appBounds,
            int activeMonitorCount)
        {
            profile = profile.Sanitized();
            return profile.DisplayScope switch
            {
                GamerDisplayScope.AllDisplays => true,
                GamerDisplayScope.SpecificDisplay =>
                    !string.IsNullOrWhiteSpace(profile.MonitorDevicePath) &&
                    profile.MonitorDevicePath.Equals(monitor.MonitorDevicePath, StringComparison.OrdinalIgnoreCase),
                _ => appBounds.HasValue
                    ? Intersects(appBounds.Value, monitor.MonitorBounds)
                    : activeMonitorCount == 1
            };
        }

        internal static bool MatchesForeground(
            GamerProfileRule profile,
            string appName,
            string? executablePath)
        {
            profile = profile.Sanitized();
            if (!profile.Enabled ||
                !GamerExecutableSafety.IsSafeProfileTarget(profile.AppName, profile.DisplayName) ||
                !profile.AppName.Equals(appName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.IsNullOrWhiteSpace(profile.ExecutablePath)) return true;
            if (string.IsNullOrWhiteSpace(executablePath)) return false;
            try
            {
                return Path.GetFullPath(profile.ExecutablePath).Equals(
                    Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool Intersects(Dxgi.RECT left, Dxgi.RECT right) =>
            left.Left < right.Right && right.Left < left.Right &&
            left.Top < right.Bottom && right.Top < left.Bottom;

        internal bool WaitForIdle(TimeSpan timeout) =>
            _foregroundCoalescer.WaitForIdle(ForegroundKey, timeout);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _settings.GamerSettingsChanged -= OnGamerSettingsChanged;
            _settings.MonitorProfileChanged -= OnMonitorProfileChanged;
            _foregroundCoalescer.Dispose();
        }
    }
}
