using System;
using System.Threading;
using System.Threading.Tasks;
using Gloam.Core;
using Gloam.Services;
using Xunit;

namespace Gloam.Tests
{
    public sealed class GameLibraryCoordinatorTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CancelWhileScanFinishes_ReportsCancellation(bool dispose)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var finish = new ManualResetEventSlim();
            using var coordinator = new GameLibraryCoordinator((token, progress) =>
            {
                started.SetResult();
                if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                // A scan can finish its last source before observing cancellation.
                return Array.Empty<DiscoveredGame>();
            });

            Task scan = coordinator.ScanLibrariesAsync();
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (dispose) coordinator.Dispose();
                else coordinator.CancelLibraryScan();
            }
            finally
            {
                finish.Set();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        }

        [Fact]
        public async Task ReplacementScan_DiscardsOldResultAndReturnsNewResult()
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var finish = new ManualResetEventSlim();
            int calls = 0;
            var expected = new[] { new DiscoveredGame("Example", "game.exe", @"C:\Games\game.exe", "Test") };
            using var coordinator = new GameLibraryCoordinator((token, progress) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult();
                    if (!finish.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    return Array.Empty<DiscoveredGame>();
                }
                return expected;
            });

            Task first = coordinator.ScanLibrariesAsync();
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(expected, await coordinator.ScanLibrariesAsync());
            }
            finally
            {
                finish.Set();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
    }
}
