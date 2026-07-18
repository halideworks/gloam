using System;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Interop;
using Xunit;

namespace HDRGammaController.Tests
{
    public class NightContentFeedbackServiceTests
    {
        [Fact]
        public async Task Timer_IsNonReentrant_AndDisposeStopsSamplingAndNotifications()
        {
            var settings = EnabledSettings();
            var sampler = new SlowSampler(TimeSpan.FromMilliseconds(175));
            using var service = new NightContentFeedbackService(
                () => settings, sampler, TimeSpan.FromMilliseconds(100));
            int notifications = 0;
            service.ContentEstimateChanged += () => Interlocked.Increment(ref notifications);
            service.UpdateMonitors(new[] { Monitor(1) });

            await sampler.ThreeCalls.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, sampler.MaximumConcurrentCalls);
            Assert.True(SpinWait.SpinUntil(
                () => service.TryGetContentLinearRgb(new IntPtr(1), out _),
                TimeSpan.FromSeconds(1)));
            Assert.True(service.TryGetContentLinearRgb(new IntPtr(1), out var rgb));
            Assert.InRange(rgb.B, 0.0, 1.0);

            service.SetSuspended(true);
            Assert.False(service.TryGetContentLinearRgb(new IntPtr(1), out _));
            service.SetSuspended(false);

            service.Dispose();
            int callsAtDispose = sampler.Calls;
            int eventsAtDispose = Volatile.Read(ref notifications);
            await Task.Delay(350);
            Assert.Equal(callsAtDispose, sampler.Calls);
            Assert.Equal(eventsAtDispose, Volatile.Read(ref notifications));
        }

        [Fact]
        public async Task MonitorRefreshCanRaceSamplingWithoutMutatingEnumeratedLists()
        {
            var settings = EnabledSettings();
            var sampler = new SlowSampler(TimeSpan.FromMilliseconds(20));
            using var service = new NightContentFeedbackService(
                () => settings, sampler, TimeSpan.FromMilliseconds(100));

            var refreshers = new Task[4];
            for (int worker = 0; worker < refreshers.Length; worker++)
            {
                int offset = worker * 10;
                refreshers[worker] = Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                        service.UpdateMonitors(new[] { Monitor(offset + i), Monitor(offset + i + 1) });
                });
            }

            await Task.WhenAll(refreshers).WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(500);
            Assert.Equal(1, sampler.MaximumConcurrentCalls);
        }

        [Fact]
        public async Task DisabledOrUnboundedModeNeverCaptures()
        {
            var settings = EnabledSettings();
            settings.ContentAdaptiveDose = false;
            var sampler = new SlowSampler(TimeSpan.Zero);
            using var service = new NightContentFeedbackService(
                () => settings, sampler, TimeSpan.FromMilliseconds(100));
            service.UpdateMonitors(new[] { Monitor(1) });

            await Task.Delay(500);
            Assert.Equal(0, sampler.Calls);
        }

        [Fact]
        public async Task HdrMonitorUsesConservativeFallbackAndIsNotCaptured()
        {
            var sampler = new SlowSampler(TimeSpan.Zero);
            using var service = new NightContentFeedbackService(
                EnabledSettings, sampler, TimeSpan.FromMilliseconds(100));
            var hdr = Monitor(1);
            hdr.IsHdrActive = true;
            service.UpdateMonitors(new[] { hdr });

            await Task.Delay(500);
            Assert.Equal(0, sampler.Calls);
            Assert.False(service.TryGetContentLinearRgb(hdr.HMonitor, out _));
        }

        [Fact]
        public async Task InactiveNightWindowNeverCaptures()
        {
            var sampler = new SlowSampler(TimeSpan.Zero);
            using var service = new NightContentFeedbackService(
                EnabledSettings, sampler, TimeSpan.FromMilliseconds(100),
                activityProvider: () => false);
            service.UpdateMonitors(new[] { Monitor(1) });

            await Task.Delay(500);
            Assert.Equal(0, sampler.Calls);
        }

        private static NightModeSettings EnabledSettings() => new()
        {
            Enabled = true,
            ContentAdaptiveDose = true,
            MelanopicEdiCeiling = 12
        };

        private static MonitorInfo Monitor(int id) => new()
        {
            HMonitor = new IntPtr(id),
            DeviceName = $"DISPLAY{id}",
            MonitorBounds = new Dxgi.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }
        };

        private sealed class SlowSampler : IScreenContentSampler
        {
            private readonly TimeSpan _delay;
            private int _active;
            private int _maximum;
            private int _calls;
            public TaskCompletionSource<bool> ThreeCalls { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public SlowSampler(TimeSpan delay) => _delay = delay;
            public int Calls => Volatile.Read(ref _calls);
            public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);

            public bool TrySample(MonitorInfo monitor, out ScreenContentSample sample)
            {
                int active = Interlocked.Increment(ref _active);
                UpdateMaximum(active);
                try
                {
                    if (_delay > TimeSpan.Zero) Thread.Sleep(_delay);
                    int call = Interlocked.Increment(ref _calls);
                    if (call >= 3) ThreeCalls.TrySetResult(true);
                    double blue = 0.2 + call % 5 * 0.03;
                    sample = new ScreenContentSample(0.3, 0.25, blue, 0.25, 1.0);
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref _active);
                }
            }

            private void UpdateMaximum(int active)
            {
                while (true)
                {
                    int current = Volatile.Read(ref _maximum);
                    if (active <= current || Interlocked.CompareExchange(ref _maximum, active, current) == current)
                        return;
                }
            }
        }
    }
}
