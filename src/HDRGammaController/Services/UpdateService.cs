using System;
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
        // Canonical repo for the Gloam rebrand. The runtime feed here and the CI
        // 'vpk upload github' target MUST point at the same repo, or CheckForUpdatesAsync
        // 404s. The repo must actually be named 'gloam' (GitHub web-redirects the old
        // name, but release assets resolve under the canonical name).
        private const string RepoUrl = "https://github.com/halideworks/gloam";

        private readonly UpdateManager _manager;

        public UpdateService()
        {
            // prerelease: false - only stable, tag-driven releases are offered (see build.yml).
            _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
        }

        /// <summary>
        /// True only when running from a Velopack install (%LocalAppData%\GloamApp\current).
        /// False for `dotnet run` / F5 debugging and the portable zip, where there is no
        /// install to update - every operation below is then a safe no-op.
        /// </summary>
        public bool IsInstalled => _manager.IsInstalled;

        /// <summary>Returns the pending update, or null if up to date / not installed / on error.</summary>
        public async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            if (!_manager.IsInstalled) return null;
            try
            {
                return await _manager.CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateService: update check failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Downloads the update package (delta when possible) in the background.</summary>
        public async Task<bool> DownloadUpdatesAsync(UpdateInfo info)
        {
            try
            {
                await _manager.DownloadUpdatesAsync(info);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateService: update download failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Applies the downloaded update and restarts into the new version. Terminates the
        /// current process, so callers must persist any state first.
        /// </summary>
        public void ApplyUpdatesAndRestart(UpdateInfo info)
        {
            _manager.ApplyUpdatesAndRestart(info);
        }

        /// <summary>
        /// Schedules the downloaded update to be applied after the process exits, without
        /// relaunching. Used on normal shutdown so a user who ignored the restart toast is
        /// still on the new version next launch. Safe to call when nothing is downloaded.
        /// </summary>
        public void ApplyUpdatesOnExit(UpdateInfo info)
        {
            try
            {
                _manager.WaitExitThenApplyUpdates(info, silent: true, restart: false);
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateService: scheduling apply-on-exit failed: {ex.Message}");
            }
        }

        /// <summary>Version label for the pending update (e.g. "1.2.0").</summary>
        public static string VersionLabel(UpdateInfo info)
            => info.TargetFullRelease?.Version?.ToString() ?? "new version";
    }
}
