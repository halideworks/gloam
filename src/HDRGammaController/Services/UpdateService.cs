using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using HDRGammaController.Core;
using Velopack;
using Velopack.Sources;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Wraps Velopack's <see cref="UpdateManager"/> against our GitHub Releases feed.
    ///
    /// Replaces the old model (poll the rolling 'latest' tag over the GitHub API, compare
    /// a compile-time build timestamp, then open the releases page in a browser). Velopack
    /// installs the app per-user under %LocalAppData%\GloamApp and applies updates by swapping
    /// the current\ folder, so updates need no elevation and no manual download/extract.
    /// (Note: the app's data dir %LocalAppData%\Gloam is intentionally distinct from the
    /// install dir, so settings/logs/reports survive an uninstall.)
    /// </summary>
    public class UpdateService
    {
        internal static readonly TimeSpan SuccessfulCheckInterval = TimeSpan.FromHours(6);
        internal static readonly TimeSpan FailedCheckRetryInterval = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan FailureNotificationInterval = TimeSpan.FromHours(24);
        private const int FailureNotificationThreshold = 3;

        // Canonical repo for the Gloam rebrand. The runtime feed here and the CI
        // 'vpk upload github' target MUST point at the same repo, or CheckForUpdatesAsync
        // 404s. The repo must actually be named 'gloam' (GitHub web-redirects the old
        // name, but release assets resolve under the canonical name).
        private const string RepoUrl = "https://github.com/halideworks/gloam";
        private const string ExpectedPackageId = "GloamApp";

        private readonly IUpdateManagerAdapter _manager;
        private readonly Func<DateTimeOffset> _clock;
        private readonly object _stateLock = new();
        private UpdateState _state;

        public UpdateService()
            : this(new VelopackUpdateManagerAdapter(
                new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false))))
        {
        }

        internal UpdateService(IUpdateManagerAdapter manager)
            : this(manager, () => DateTimeOffset.UtcNow)
        {
        }

        internal UpdateService(IUpdateManagerAdapter manager, Func<DateTimeOffset> clock)
        {
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _state = LoadState();
            CaptureRuntimeState();
            SaveState();
        }

        /// <summary>
        /// True only when running from a Velopack install (%LocalAppData%\GloamApp\current).
        /// False for `dotnet run` / F5 debugging and the portable zip, where there is no
        /// install to update - every operation below is then a safe no-op.
        /// </summary>
        public bool IsInstalled => _manager.IsInstalled;

        /// <summary>The installed Velopack package version, or null for dev / portable runs.</summary>
        public string? InstalledVersion => _manager.CurrentVersion?.ToString();

        /// <summary>
        /// User-facing version label. Prefer Velopack's package version so the UI matches the
        /// update feed's comparison basis; fall back to assembly metadata for dev / portable runs.
        /// </summary>
        public string DisplayVersion =>
            "v" + (InstalledVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                ?? "?");

        /// <summary>Current updater diagnostics snapshot; safe to serialize into support bundles.</summary>
        public UpdateState StateSnapshot
        {
            get
            {
                lock (_stateLock) { return _state.Clone(); }
            }
        }

        /// <summary>
        /// Schedules an already-downloaded Velopack update, if one is pending. This covers
        /// interrupted shutdowns and any previous launch that downloaded but did not apply.
        /// </summary>
        public bool TrySchedulePendingUpdateOnExit()
        {
            CaptureRuntimeState();
            var pending = _manager.UpdatePendingRestart;
            if (pending == null)
            {
                RecordResult("no-pending-update", error: null, target: null, clearFailures: false);
                return false;
            }

            if (!IsNewerThanCurrent(pending, _manager.CurrentVersion))
            {
                RecordResult("ignored-non-newer-pending-update", error: null, target: pending, clearFailures: false);
                return false;
            }

            if (ApplyUpdatesOnExit(pending))
            {
                RecordResult("pending-update-scheduled", error: null, target: pending, clearFailures: true);
                return true;
            }

            return false;
        }

        /// <summary>Returns the pending update, or null if up to date / not installed / on error.</summary>
        public async Task<UpdateInfo?> CheckForUpdatesAsync(bool force = false)
        {
            CaptureRuntimeState();
            if (!_manager.IsInstalled)
            {
                Log.Info("UpdateService: update check skipped; app is not a Velopack install.");
                RecordResult("skipped-not-installed", error: null, target: null, clearFailures: false);
                return null;
            }

            if (!force && !ShouldCheckNow(out var skipReason))
            {
                Log.Info($"UpdateService: update check skipped; {skipReason}.");
                RecordResult(skipReason, error: null, target: null, clearFailures: false);
                return null;
            }

            RecordCheckAttempt();
            try
            {
                var current = _manager.CurrentVersion;
                var info = await _manager.CheckForUpdatesAsync();
                if (info == null)
                {
                    Log.Info($"UpdateService: no update available. installed={VersionLabel(current)}");
                    RecordResult("up-to-date", error: null, target: null, clearFailures: true, checkSucceeded: true);
                    return null;
                }

                if (!IsAcceptableRemoteUpdate(info, current))
                {
                    RecordResult("ignored-unacceptable-update", error: null, target: info.TargetFullRelease, clearFailures: true, checkSucceeded: true);
                    return null;
                }

                Log.Info($"UpdateService: update available. installed={VersionLabel(current)}, target={VersionLabel(info)}");
                RecordResult("update-available", error: null, target: info.TargetFullRelease, clearFailures: true, checkSucceeded: true);
                return info;
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateService: update check failed: {ex.Message}");
                RecordResult("check-failed", error: ex.Message, target: null, clearFailures: false, countAsFailure: true);
                return null;
            }
        }

        /// <summary>Downloads the update package (delta when possible) in the background.</summary>
        public async Task<bool> DownloadUpdatesAsync(UpdateInfo info)
        {
            try
            {
                if (!IsAcceptableRemoteUpdate(info, _manager.CurrentVersion))
                    return false;

                await _manager.DownloadUpdatesAsync(info);
                RecordResult("downloaded", error: null, target: info.TargetFullRelease, clearFailures: true, downloaded: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateService: update download failed: {ex.Message}");
                RecordResult("download-failed", error: ex.Message, target: info.TargetFullRelease, clearFailures: false, countAsFailure: true);
                return false;
            }
        }

        /// <summary>
        /// Applies the downloaded update and restarts into the new version. Terminates the
        /// current process, so callers must persist any state first.
        /// </summary>
        public void ApplyUpdatesAndRestart(UpdateInfo info)
        {
            if (!IsNewerThanCurrent(info, _manager.CurrentVersion))
                return;

            RecordResult("apply-and-restart", error: null, target: info.TargetFullRelease, clearFailures: true, scheduled: true);
            _manager.ApplyUpdatesAndRestart(info);
        }

        /// <summary>
        /// Schedules the downloaded update to be applied after the process exits, without
        /// relaunching. Used after a background download and retried during normal shutdown
        /// so the next launch can land on the new version. Safe to call when nothing is downloaded.
        /// </summary>
        public bool ApplyUpdatesOnExit(UpdateInfo info)
            => ApplyUpdatesOnExit(info.TargetFullRelease);

        private bool ApplyUpdatesOnExit(VelopackAsset? asset)
        {
            try
            {
                if (!IsNewerThanCurrent(asset, _manager.CurrentVersion))
                    return false;

                _manager.WaitExitThenApplyUpdates(asset!, silent: true, restart: false);
                RecordResult("scheduled-on-exit", error: null, target: asset, clearFailures: true, scheduled: true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateService: scheduling apply-on-exit failed: {ex.Message}");
                RecordResult("schedule-on-exit-failed", error: ex.Message, target: asset, clearFailures: false, countAsFailure: true);
                return false;
            }
        }

        public bool ShouldNotifyPersistentFailure()
        {
            lock (_stateLock)
            {
                if (_state.ConsecutiveFailures < FailureNotificationThreshold)
                    return false;

                if (!_state.LastFailureNotificationUtc.HasValue)
                    return true;

                return _clock() - _state.LastFailureNotificationUtc.Value >= FailureNotificationInterval;
            }
        }

        public void MarkFailureNotified()
        {
            lock (_stateLock)
            {
                _state.LastFailureNotificationUtc = _clock();
            }
            SaveState();
        }

        public bool ShouldNotifyUpdateReady(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return false;

            lock (_stateLock)
            {
                return !string.Equals(_state.LastUpdateReadyNotificationVersion, version, StringComparison.OrdinalIgnoreCase);
            }
        }

        public void MarkUpdateReadyNotified(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return;

            lock (_stateLock)
            {
                _state.LastUpdateReadyNotificationVersion = version;
            }
            SaveState();
        }

        /// <summary>Version label for the pending update (e.g. "1.2.0").</summary>
        public static string VersionLabel(UpdateInfo info)
            => info.TargetFullRelease?.Version?.ToString() ?? "new version";

        internal static bool IsNewerThanCurrent(UpdateInfo info, SemanticVersion? currentVersion)
            => IsNewerThanCurrent(info.TargetFullRelease, currentVersion, info.IsDowngrade);

        internal static bool IsAcceptableRemoteUpdate(UpdateInfo info, SemanticVersion? currentVersion)
        {
            if (!IsNewerThanCurrent(info, currentVersion))
                return false;

            var asset = info.TargetFullRelease!;
            if (!string.Equals(asset.PackageId, ExpectedPackageId, StringComparison.Ordinal))
            {
                Log.Error($"UpdateService: refusing update for unexpected package id '{asset.PackageId ?? "unknown"}'.");
                return false;
            }

            if (asset.Size <= 0)
            {
                Log.Error("UpdateService: refusing update with invalid package size.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(asset.SHA256) || asset.SHA256.Length != 64 || !IsHex(asset.SHA256))
            {
                Log.Error("UpdateService: refusing update with missing SHA-256 metadata.");
                return false;
            }

            return true;
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value)
            {
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                bool upper = c >= 'A' && c <= 'F';
                if (!digit && !lower && !upper)
                    return false;
            }

            return true;
        }

        internal static bool IsNewerThanCurrent(VelopackAsset? asset, SemanticVersion? currentVersion, bool isDowngrade = false)
        {
            if (isDowngrade)
            {
                Log.Error($"UpdateService: refusing downgrade update to {VersionLabel(asset)}.");
                return false;
            }

            var targetVersion = asset?.Version;
            if (targetVersion == null)
            {
                Log.Error("UpdateService: refusing update with no target version.");
                return false;
            }

            if (currentVersion != null && targetVersion <= currentVersion)
            {
                Log.Info($"UpdateService: ignoring non-newer update. installed={VersionLabel(currentVersion)}, target={targetVersion}");
                return false;
            }

            return true;
        }

        private static string VersionLabel(SemanticVersion? version)
            => version?.ToString() ?? "unknown";

        private static string VersionLabel(VelopackAsset? asset)
            => asset?.Version?.ToString() ?? "new version";

        private void CaptureRuntimeState()
        {
            lock (_stateLock)
            {
                _state.IsInstalled = _manager.IsInstalled;
                _state.IsPortable = _manager.IsPortable;
                _state.InstalledVersion = InstalledVersion;
                _state.AssemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
                _state.PendingRestartVersion = _manager.UpdatePendingRestart?.Version?.ToString();
            }
        }

        private bool ShouldCheckNow(out string reason)
        {
            lock (_stateLock)
            {
                var lastAttempt = _state.LastCheckAttemptUtc;
                if (!lastAttempt.HasValue)
                {
                    reason = "never checked";
                    return true;
                }

                var interval = _state.ConsecutiveFailures > 0
                    ? FailedCheckRetryInterval
                    : SuccessfulCheckInterval;
                var elapsed = _clock() - lastAttempt.Value;
                if (elapsed >= interval)
                {
                    reason = $"last check was {elapsed:g} ago";
                    return true;
                }

                reason = $"last check was {elapsed:g} ago; next retry after {interval:g}";
                return false;
            }
        }

        private void RecordCheckAttempt()
        {
            lock (_stateLock)
            {
                _state.LastCheckAttemptUtc = _clock();
            }
            SaveState();
        }

        private void RecordResult(
            string result,
            string? error,
            VelopackAsset? target,
            bool clearFailures,
            bool countAsFailure = false,
            bool checkSucceeded = false,
            bool downloaded = false,
            bool scheduled = false)
        {
            lock (_stateLock)
            {
                _state.LastResult = result;
                if (error != null || clearFailures)
                    _state.LastError = error;
                _state.LastTargetVersion = target?.Version?.ToString();
                _state.LastTargetFileName = target?.FileName;
                _state.LastTargetSha256 = target?.SHA256;
                _state.LastTargetSize = target?.Size;
                _state.PendingRestartVersion = _manager.UpdatePendingRestart?.Version?.ToString();

                if (clearFailures)
                {
                    _state.ConsecutiveFailures = 0;
                }
                else if (countAsFailure)
                {
                    _state.ConsecutiveFailures++;
                }

                if (checkSucceeded)
                    _state.LastSuccessfulCheckUtc = _clock();

                if (downloaded)
                    _state.LastDownloadedVersion = target?.Version?.ToString();

                if (scheduled)
                    _state.LastScheduledVersion = target?.Version?.ToString();
            }

            SaveState();
        }

        private static string StateFilePath => Path.Combine(AppPaths.DataDir, "update-state.json");

        private static UpdateState LoadState()
        {
            try
            {
                if (!File.Exists(StateFilePath))
                    return new UpdateState();

                return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StateFilePath), JsonOptions)
                    ?? new UpdateState();
            }
            catch (Exception ex)
            {
                Log.Info($"UpdateService: failed to load update state: {ex.Message}");
                return new UpdateState();
            }
        }

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                UpdateState snapshot;
                lock (_stateLock) { snapshot = _state.Clone(); }
                string tempPath = StateFilePath + $".{Guid.NewGuid():N}.tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
                File.Move(tempPath, StateFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Info($"UpdateService: failed to save update state: {ex.Message}");
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };
    }

    internal interface IUpdateManagerAdapter
    {
        bool IsInstalled { get; }
        bool IsPortable { get; }
        SemanticVersion? CurrentVersion { get; }
        VelopackAsset? UpdatePendingRestart { get; }
        Task<UpdateInfo?> CheckForUpdatesAsync();
        Task DownloadUpdatesAsync(UpdateInfo info);
        void ApplyUpdatesAndRestart(UpdateInfo info);
        void WaitExitThenApplyUpdates(VelopackAsset asset, bool silent, bool restart);
    }

    internal sealed class VelopackUpdateManagerAdapter : IUpdateManagerAdapter
    {
        private readonly UpdateManager _manager;

        public VelopackUpdateManagerAdapter(UpdateManager manager)
        {
            _manager = manager;
        }

        public bool IsInstalled => _manager.IsInstalled;
        public bool IsPortable => _manager.IsPortable;
        public SemanticVersion? CurrentVersion => _manager.CurrentVersion;
        public VelopackAsset? UpdatePendingRestart => _manager.UpdatePendingRestart;
        public Task<UpdateInfo?> CheckForUpdatesAsync() => _manager.CheckForUpdatesAsync();
        public Task DownloadUpdatesAsync(UpdateInfo info) => _manager.DownloadUpdatesAsync(info);
        public void ApplyUpdatesAndRestart(UpdateInfo info) => _manager.ApplyUpdatesAndRestart(info);
        public void WaitExitThenApplyUpdates(VelopackAsset asset, bool silent, bool restart)
            => _manager.WaitExitThenApplyUpdates(asset, silent, restart);
    }

    public sealed class UpdateState
    {
        public bool IsInstalled { get; set; }
        public bool IsPortable { get; set; }
        public string? InstalledVersion { get; set; }
        public string? AssemblyVersion { get; set; }
        public string? PendingRestartVersion { get; set; }
        public DateTimeOffset? LastCheckAttemptUtc { get; set; }
        public DateTimeOffset? LastSuccessfulCheckUtc { get; set; }
        public string? LastResult { get; set; }
        public string? LastError { get; set; }
        public string? LastTargetVersion { get; set; }
        public string? LastTargetFileName { get; set; }
        public string? LastTargetSha256 { get; set; }
        public long? LastTargetSize { get; set; }
        public string? LastDownloadedVersion { get; set; }
        public string? LastScheduledVersion { get; set; }
        public string? LastUpdateReadyNotificationVersion { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? LastFailureNotificationUtc { get; set; }

        public UpdateState Clone() => new()
        {
            IsInstalled = IsInstalled,
            IsPortable = IsPortable,
            InstalledVersion = InstalledVersion,
            AssemblyVersion = AssemblyVersion,
            PendingRestartVersion = PendingRestartVersion,
            LastCheckAttemptUtc = LastCheckAttemptUtc,
            LastSuccessfulCheckUtc = LastSuccessfulCheckUtc,
            LastResult = LastResult,
            LastError = LastError,
            LastTargetVersion = LastTargetVersion,
            LastTargetFileName = LastTargetFileName,
            LastTargetSha256 = LastTargetSha256,
            LastTargetSize = LastTargetSize,
            LastDownloadedVersion = LastDownloadedVersion,
            LastScheduledVersion = LastScheduledVersion,
            LastUpdateReadyNotificationVersion = LastUpdateReadyNotificationVersion,
            ConsecutiveFailures = ConsecutiveFailures,
            LastFailureNotificationUtc = LastFailureNotificationUtc
        };
    }
}
