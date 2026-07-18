using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using HDRGammaController.Interop;

namespace HDRGammaController.Core
{
    /// <summary>
    /// A deliberately lossy screen-content summary. No screenshot, pixel buffer, window
    /// title, process identity, or histogram leaves the synchronous sampling call.
    /// </summary>
    public readonly record struct ScreenContentSample(
        double LinearR, double LinearG, double LinearB, double LinearLuminance,
        double NonBlackFraction);

    public interface IScreenContentSampler
    {
        bool TrySample(MonitorInfo monitor, out ScreenContentSample sample);
    }

    /// <summary>
    /// Samples the virtual desktop into a 48x27 bitmap, reduces it immediately to five
    /// scalars, then zeroes the temporary pixel buffer. Protected/all-black captures are
    /// rejected so a capture failure can never relax a circadian dose limit.
    /// </summary>
    public sealed class GdiScreenContentSampler : IScreenContentSampler
    {
        private const int SampleWidth = 48;
        private const int SampleHeight = 27;
        private const double SamplingSafetyFactor = 1.05;

        public bool TrySample(MonitorInfo monitor, out ScreenContentSample sample)
        {
            sample = default;
            if (!OperatingSystem.IsWindows()) return false;

            var bounds = monitor.MonitorBounds;
            int sourceWidth = bounds.Right - bounds.Left;
            int sourceHeight = bounds.Bottom - bounds.Top;
            if (sourceWidth <= 0 || sourceHeight <= 0) return false;

            IntPtr sourceDc = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            byte[]? pixels = null;
            try
            {
                sourceDc = User32.GetDC(IntPtr.Zero);
                if (sourceDc == IntPtr.Zero) return false;

                memoryDc = Gdi32.CreateCompatibleDC(sourceDc);
                if (memoryDc == IntPtr.Zero) return false;
                bitmap = Gdi32.CreateCompatibleBitmap(sourceDc, SampleWidth, SampleHeight);
                if (bitmap == IntPtr.Zero) return false;
                previous = Gdi32.SelectObject(memoryDc, bitmap);
                if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return false;

                Gdi32.SetStretchBltMode(memoryDc, Gdi32.ColorOnColor);
                if (!Gdi32.StretchBlt(
                        memoryDc, 0, 0, SampleWidth, SampleHeight,
                        sourceDc, bounds.Left, bounds.Top, sourceWidth, sourceHeight,
                        Gdi32.Srccopy | Gdi32.CaptureBlt))
                    return false;

                // GetDIBits requires the bitmap not to be selected into a DC. Restore the
                // memory DC's stock bitmap before reading and clear the cleanup sentinel.
                IntPtr deselected = Gdi32.SelectObject(memoryDc, previous);
                if (deselected == IntPtr.Zero || deselected == new IntPtr(-1)) return false;
                previous = IntPtr.Zero;

                var info = new Gdi32.BitmapInfo
                {
                    Header = new Gdi32.BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf<Gdi32.BitmapInfoHeader>(),
                        Width = SampleWidth,
                        // Negative height requests a top-down DIB and avoids a second buffer.
                        Height = -SampleHeight,
                        Planes = 1,
                        BitCount = 32,
                        Compression = 0,
                        SizeImage = SampleWidth * SampleHeight * 4u
                    }
                };
                pixels = new byte[SampleWidth * SampleHeight * 4];
                if (Gdi32.GetDIBits(memoryDc, bitmap, 0, SampleHeight, pixels, ref info, Gdi32.DibRgbColors) != SampleHeight)
                    return false;

                double r = 0, g = 0, b = 0;
                int nonBlack = 0;
                int count = SampleWidth * SampleHeight;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    double sb = pixels[i] / 255.0;
                    double sg = pixels[i + 1] / 255.0;
                    double sr = pixels[i + 2] / 255.0;
                    if (Math.Max(sr, Math.Max(sg, sb)) > 0.018) nonBlack++;
                    r += DecodeSrgb(sr);
                    g += DecodeSrgb(sg);
                    b += DecodeSrgb(sb);
                }

                double fraction = nonBlack / (double)count;
                // An all-black protected surface is indistinguishable from real black via
                // GDI. Reject both: the governor falls back to a full-white worst case.
                if (fraction < 0.01) return false;

                r = Math.Min(1.0, r / count * SamplingSafetyFactor);
                g = Math.Min(1.0, g / count * SamplingSafetyFactor);
                b = Math.Min(1.0, b / count * SamplingSafetyFactor);
                // A protected video rectangle is commonly returned as black while the rest
                // of the desktop remains visible. Treat every near-black sample as unknown
                // white for the DOSE estimate. This intentionally forfeits dark-content
                // relaxation rather than allowing partial capture failure to understate dose.
                // The 5% sampling margin above also covers 8-bit quantization and the small
                // transfer-function spread among the SDR gamma modes without storing pixels.
                double unknownFraction = 1.0 - fraction;
                r = Math.Min(1.0, r + unknownFraction);
                g = Math.Min(1.0, g + unknownFraction);
                b = Math.Min(1.0, b + unknownFraction);
                sample = new ScreenContentSample(r, g, b, 0.2126 * r + 0.7152 * g + 0.0722 * b, fraction);
                return double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b);
            }
            catch (Exception ex)
            {
                Log.Info($"GdiScreenContentSampler: sample failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (pixels != null) Array.Clear(pixels, 0, pixels.Length);
                if (previous != IntPtr.Zero && previous != new IntPtr(-1) && memoryDc != IntPtr.Zero)
                    Gdi32.SelectObject(memoryDc, previous);
                if (bitmap != IntPtr.Zero) Gdi32.DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero) Gdi32.DeleteDC(memoryDc);
                if (sourceDc != IntPtr.Zero) User32.ReleaseDC(IntPtr.Zero, sourceDc);
            }
        }

        private static double DecodeSrgb(double value) =>
            value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Non-overlapping, rate-limited content feedback. Timer callbacks never wait for one
    /// another, monitor enumeration is snapshot-based, and disposal suppresses all future
    /// notifications even when a GDI call was already in flight.
    /// </summary>
    public sealed class NightContentFeedbackService : IDisposable
    {
        private readonly Func<NightModeSettings> _settingsProvider;
        private readonly Func<bool> _activityProvider;
        private readonly IScreenContentSampler _sampler;
        private readonly Timer _timer;
        private readonly TimeSpan _period;
        private readonly object _sampleLock = new();
        private readonly Dictionary<IntPtr, ScreenContentSample> _smoothed = new();
        private readonly Dictionary<IntPtr, DateTime> _sampleTimes = new();
        private readonly Dictionary<IntPtr, DateTime> _lastNotified = new();
        private MonitorInfo[] _monitors = Array.Empty<MonitorInfo>();
        private int _samplingGeneration;
        private int _callbackActive;
        private int _suspended;
        private int _disposed;

        public event Action? ContentEstimateChanged;

        public NightContentFeedbackService(
            SettingsManager settingsManager,
            NightModeService nightModeService,
            IScreenContentSampler sampler,
            TimeSpan? period = null)
            : this(
                CreateSettingsProvider(settingsManager), sampler, period,
                CreateActivityProvider(nightModeService))
        {
        }

        internal NightContentFeedbackService(
            Func<NightModeSettings> settingsProvider,
            IScreenContentSampler sampler,
            TimeSpan? period = null,
            Func<bool>? activityProvider = null)
        {
            _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
            _activityProvider = activityProvider ?? (() => true);
            _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
            _period = period.GetValueOrDefault(TimeSpan.FromSeconds(3));
            if (_period < TimeSpan.FromMilliseconds(100)) _period = TimeSpan.FromMilliseconds(100);
            _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private static Func<NightModeSettings> CreateSettingsProvider(SettingsManager settingsManager)
        {
            ArgumentNullException.ThrowIfNull(settingsManager);
            return () => settingsManager.NightMode;
        }

        private static Func<bool> CreateActivityProvider(NightModeService nightModeService)
        {
            ArgumentNullException.ThrowIfNull(nightModeService);
            return () => nightModeService.IsNightModeActive;
        }

        public void UpdateMonitors(IEnumerable<MonitorInfo> monitors)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var copy = new List<MonitorInfo>();
            foreach (var monitor in monitors)
            {
                copy.Add(new MonitorInfo
                {
                    HMonitor = monitor.HMonitor,
                    DeviceName = monitor.DeviceName,
                    FriendlyName = monitor.FriendlyName,
                    MonitorDevicePath = monitor.MonitorDevicePath,
                    MonitorBounds = monitor.MonitorBounds,
                    IsHdrActive = monitor.IsHdrActive
                });
            }
            var activeHandles = new HashSet<IntPtr>();
            foreach (var monitor in copy)
                if (!monitor.IsHdrActive) activeHandles.Add(monitor.HMonitor);
            lock (_sampleLock)
            {
                // Dispose and publication share this lock. A refresh that began before
                // disposal can finish cloning, but can never republish afterward.
                if (Volatile.Read(ref _disposed) != 0) return;
                Interlocked.Exchange(ref _monitors, copy.ToArray());
                Interlocked.Increment(ref _samplingGeneration);
                var stale = new List<IntPtr>();
                foreach (var handle in _smoothed.Keys)
                    if (!activeHandles.Contains(handle)) stale.Add(handle);
                foreach (var handle in stale)
                {
                    _smoothed.Remove(handle);
                    _sampleTimes.Remove(handle);
                    _lastNotified.Remove(handle);
                }
            }
            try
            {
                _timer.Change(copy.Count == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(250), _period);
            }
            catch (ObjectDisposedException)
            {
                // Dispose may win between the optimistic _disposed check and Change.
            }
        }

        public bool TryGetContentLinearRgb(IntPtr hMonitor, out (double R, double G, double B) rgb)
        {
            if (Volatile.Read(ref _suspended) != 0 || Volatile.Read(ref _disposed) != 0)
            {
                rgb = default;
                return false;
            }
            lock (_sampleLock)
            {
                if (_smoothed.TryGetValue(hMonitor, out var sample) &&
                    _sampleTimes.TryGetValue(hMonitor, out var sampledAt) &&
                    DateTime.UtcNow - sampledAt <= TimeSpan.FromTicks(_period.Ticks * 3))
                {
                    rgb = (sample.LinearR, sample.LinearG, sample.LinearB);
                    return true;
                }
            }
            rgb = default;
            return false;
        }

        public void SetSuspended(bool suspended)
        {
            int value = suspended ? 1 : 0;
            if (Interlocked.Exchange(ref _suspended, value) == value) return;
            // Invalidates a sample already in GDI even if suspend is toggled off again
            // before that call returns.
            Interlocked.Increment(ref _samplingGeneration);
            if (!suspended) return;
            lock (_sampleLock)
            {
                _smoothed.Clear();
                _sampleTimes.Clear();
                _lastNotified.Clear();
            }
        }

        private void OnTimer(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                Interlocked.CompareExchange(ref _callbackActive, 1, 0) != 0)
                return;

            bool notify = false;
            try
            {
                var settings = _settingsProvider();
                if (Volatile.Read(ref _suspended) != 0 || !_activityProvider() || !settings.Enabled ||
                    !settings.ContentAdaptiveDose || settings.MelanopicEdiCeiling <= 0)
                {
                    lock (_sampleLock)
                    {
                        _smoothed.Clear();
                        _sampleTimes.Clear();
                        _lastNotified.Clear();
                    }
                    return;
                }

                var now = DateTime.UtcNow;
                int samplingGeneration = Volatile.Read(ref _samplingGeneration);
                var monitors = Volatile.Read(ref _monitors);
                foreach (var monitor in monitors)
                {
                    if (Volatile.Read(ref _disposed) != 0) break;
                    // GDI desktop capture is an 8-bit compositor representation and cannot
                    // reconstruct the absolute PQ scene nits needed for a valid HDR dose.
                    // Leave HDR monitors on the full-white conservative governor model.
                    if (monitor.IsHdrActive) continue;
                    if (!_sampler.TrySample(monitor, out var fresh)) continue;
                    // A display refresh may have replaced this HMONITOR/bounds while GDI
                    // was sampling. Never publish a result from the superseded snapshot.
                    if (Volatile.Read(ref _suspended) != 0 ||
                        samplingGeneration != Volatile.Read(ref _samplingGeneration)) break;

                    lock (_sampleLock)
                    {
                        if (Volatile.Read(ref _suspended) != 0 ||
                            samplingGeneration != Volatile.Read(ref _samplingGeneration)) break;
                        bool existed = _smoothed.TryGetValue(monitor.HMonitor, out var old);
                        var smooth = existed ? ConservativeAttackRelease(old, fresh, 0.25) : fresh;
                        _smoothed[monitor.HMonitor] = smooth;
                        _sampleTimes[monitor.HMonitor] = now;

                        double change = existed
                            ? Math.Max(Math.Abs(smooth.LinearR - old.LinearR),
                                Math.Max(Math.Abs(smooth.LinearG - old.LinearG), Math.Abs(smooth.LinearB - old.LinearB)))
                            : 1.0;
                        bool attack = !existed || smooth.LinearR > old.LinearR + 1e-9 ||
                                      smooth.LinearG > old.LinearG + 1e-9 ||
                                      smooth.LinearB > old.LinearB + 1e-9;
                        _lastNotified.TryGetValue(monitor.HMonitor, out var last);
                        // Risk increases bypass the release-side notification throttle;
                        // the sampler period still caps this at one apply per three seconds.
                        if (change >= 0.015 && (attack || now - last >= TimeSpan.FromSeconds(5)))
                        {
                            _lastNotified[monitor.HMonitor] = now;
                            notify = true;
                        }
                    }
                }

                if (notify && Volatile.Read(ref _disposed) == 0 &&
                    Volatile.Read(ref _suspended) == 0)
                {
                    try { ContentEstimateChanged?.Invoke(); }
                    catch (Exception ex) { Log.Info($"NightContentFeedbackService: subscriber failed: {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"NightContentFeedbackService: tick failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _callbackActive, 0);
            }
        }

        private static ScreenContentSample ConservativeAttackRelease(
            ScreenContentSample old, ScreenContentSample fresh, double releaseAlpha)
        {
            // Channelwise instantaneous attack prevents smoothing from understating a
            // sudden bright/blue screen. Slow release requires sustained darker evidence
            // before dose is relaxed. Channelwise maxima may form a slightly impossible
            // colour during transitions, which is intentional: it is the safe envelope.
            double r = fresh.LinearR >= old.LinearR
                ? fresh.LinearR
                : old.LinearR + releaseAlpha * (fresh.LinearR - old.LinearR);
            double g = fresh.LinearG >= old.LinearG
                ? fresh.LinearG
                : old.LinearG + releaseAlpha * (fresh.LinearG - old.LinearG);
            double b = fresh.LinearB >= old.LinearB
                ? fresh.LinearB
                : old.LinearB + releaseAlpha * (fresh.LinearB - old.LinearB);
            double nonBlack = old.NonBlackFraction +
                              releaseAlpha * (fresh.NonBlackFraction - old.NonBlackFraction);
            return new ScreenContentSample(
                r, g, b, 0.2126 * r + 0.7152 * g + 0.0722 * b, nonBlack);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            using var drained = new ManualResetEvent(false);
            if (_timer.Dispose(drained))
                drained.WaitOne(TimeSpan.FromSeconds(1));
            _timer.Dispose();
            lock (_sampleLock)
            {
                Interlocked.Exchange(ref _monitors, Array.Empty<MonitorInfo>());
                Interlocked.Increment(ref _samplingGeneration);
                _smoothed.Clear();
                _sampleTimes.Clear();
                _lastNotified.Clear();
            }
            ContentEstimateChanged = null;
        }
    }
}
