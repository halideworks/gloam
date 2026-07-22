using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Owns background process discovery and launcher-library scan lifetime. Generation
    /// checks prevent stale scans from replacing a newer dashboard state after refresh,
    /// dismissal, or window disposal.
    /// </summary>
    internal sealed class GameLibraryCoordinator : IDisposable
    {
        internal sealed record RunningAppScan(
            IReadOnlyList<string> AppNames,
            IReadOnlyDictionary<string, string> ExecutablePaths);

        private readonly GameDiscoveryService _discoveryService;
        private CancellationTokenSource? _discoveryCancellation;
        private int _discoveryGeneration;
        private int _runningAppGeneration;
        private bool _disposed;

        internal GameLibraryCoordinator(GameDiscoveryService? discoveryService = null)
        {
            _discoveryService = discoveryService ?? new GameDiscoveryService();
        }

        internal async Task<RunningAppScan?> RefreshRunningAppsAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int generation = Interlocked.Increment(ref _runningAppGeneration);
            RunningAppScan scan = await Task.Run(ScanRunningApps);
            return !_disposed && generation == Volatile.Read(ref _runningAppGeneration)
                ? scan
                : null;
        }

        internal async Task<IReadOnlyList<DiscoveredGame>> ScanLibrariesAsync(
            IProgress<GameDiscoveryProgress>? progress = null)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelLibraryScan();

            var cancellation = new CancellationTokenSource();
            _discoveryCancellation = cancellation;
            int generation = Interlocked.Increment(ref _discoveryGeneration);
            var guardedProgress = progress == null
                ? null
                : new Progress<GameDiscoveryProgress>(value =>
                {
                    if (!_disposed && !cancellation.IsCancellationRequested &&
                        generation == Volatile.Read(ref _discoveryGeneration))
                        progress.Report(value);
                });

            try
            {
                var games = await Task.Run(
                    () => _discoveryService.Scan(cancellation.Token, guardedProgress),
                    cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_disposed || generation != Volatile.Read(ref _discoveryGeneration))
                    throw new OperationCanceledException(cancellation.Token);
                return games;
            }
            finally
            {
                if (ReferenceEquals(_discoveryCancellation, cancellation))
                    _discoveryCancellation = null;
                cancellation.Dispose();
            }
        }

        internal void CancelLibraryScan()
        {
            Interlocked.Increment(ref _discoveryGeneration);
            var cancellation = Interlocked.Exchange(ref _discoveryCancellation, null);
            if (cancellation == null) return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private static RunningAppScan ScanRunningApps()
        {
            var appNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var executablePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try
                        {
                            if (process.MainWindowHandle == IntPtr.Zero ||
                                string.IsNullOrEmpty(process.MainWindowTitle))
                                continue;

                            string appName = process.ProcessName.ToLowerInvariant() + ".exe";
                            appNames.Add(appName);
                            try
                            {
                                string? path = process.MainModule?.FileName;
                                if (!string.IsNullOrWhiteSpace(path))
                                    executablePaths[appName] = Path.GetFullPath(path);
                            }
                            catch (Exception ex)
                            {
                                Log.DebugRateLimited(
                                    "game-library-protected-process",
                                    $"GameLibraryCoordinator: a protected process path could not be read: {ex.Message}",
                                    TimeSpan.FromMinutes(5));
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.DebugRateLimited(
                                "game-library-process-race",
                                $"GameLibraryCoordinator: a process exited during discovery: {ex.Message}",
                                TimeSpan.FromMinutes(5));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"GameLibraryCoordinator: running-app discovery failed: {ex.Message}");
            }

            return new RunningAppScan(
                appNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
                executablePaths);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _runningAppGeneration);
            Interlocked.Increment(ref _discoveryGeneration);
            if (_discoveryCancellation != null)
            {
                _discoveryCancellation.Cancel();
                _discoveryCancellation.Dispose();
                _discoveryCancellation = null;
            }
        }
    }
}
