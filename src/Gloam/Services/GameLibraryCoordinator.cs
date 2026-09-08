using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gloam.Core;

namespace Gloam.Services
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

        private readonly Func<CancellationToken, IProgress<GameDiscoveryProgress>?, IReadOnlyList<DiscoveredGame>> _scan;
        private readonly object _discoveryLock = new();
        [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
            Justification = "ScanLibrariesAsync owns and disposes each source in finally after its worker exits; shutdown only requests cancellation.")]
        private CancellationTokenSource? _discoveryCancellation;
        private int _discoveryGeneration;
        private int _runningAppGeneration;
        private volatile bool _disposed;

        internal GameLibraryCoordinator(GameDiscoveryService? discoveryService = null)
        {
            _scan = (discoveryService ?? new GameDiscoveryService()).Scan;
        }

        internal GameLibraryCoordinator(
            Func<CancellationToken, IProgress<GameDiscoveryProgress>?, IReadOnlyList<DiscoveredGame>> scan)
        {
            _scan = scan;
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
            CancellationTokenSource cancellation;
            CancellationToken token;
            int generation;
            lock (_discoveryLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                CancelLibraryScan();
                cancellation = new CancellationTokenSource();
                token = cancellation.Token;
                _discoveryCancellation = cancellation;
                generation = Interlocked.Increment(ref _discoveryGeneration);
            }
            var guardedProgress = progress == null
                ? null
                : new Progress<GameDiscoveryProgress>(value =>
                {
                    if (!_disposed && !token.IsCancellationRequested &&
                        generation == Volatile.Read(ref _discoveryGeneration))
                        progress.Report(value);
                });

            try
            {
                var games = await Task.Run(
                    () => _scan(token, guardedProgress),
                    token);
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != Volatile.Read(ref _discoveryGeneration))
                    throw new OperationCanceledException(token);
                return games;
            }
            finally
            {
                lock (_discoveryLock)
                {
                    if (ReferenceEquals(_discoveryCancellation, cancellation))
                        _discoveryCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        internal void CancelLibraryScan()
        {
            lock (_discoveryLock)
            {
                Interlocked.Increment(ref _discoveryGeneration);
                var cancellation = _discoveryCancellation;
                _discoveryCancellation = null;
                // Serialize Cancel with the async owner's final disposal.
                cancellation?.Cancel();
            }
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
            lock (_discoveryLock)
            {
                if (_disposed) return;
                _disposed = true;
                Interlocked.Increment(ref _runningAppGeneration);
                CancelLibraryScan();
            }
        }
    }
}
