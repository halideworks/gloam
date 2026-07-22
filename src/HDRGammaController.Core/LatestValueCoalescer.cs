using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Per-key "latest value wins" coalescer. Designed for rapid slider-drag scenarios
    /// where submitting N updates faster than the work function can consume them should
    /// result in far fewer than N work invocations — each invocation picks up the most
    /// recent value submitted for its key.
    ///
    /// Guarantees:
    ///  1. Every <see cref="Submit"/> call causes the work function to run at least once
    ///     with *some* value submitted on or after that call (no lost updates).
    ///  2. At most one work invocation per key runs concurrently.
    ///  3. The value passed to a work invocation is the most recent value submitted for
    ///     that key at the moment of dequeue.
    ///
    /// Concurrency model: each key owns a <see cref="SemaphoreSlim"/> (used as a 0/1 gate)
    /// plus a lock-protected Pending slot. Producers update Pending under the lock; if no
    /// runner is active they try to acquire the gate and spawn one. The runner drains
    /// Pending in a loop, then releases the gate and double-checks Pending after release
    /// to close the race where a producer's update landed between the runner's null-check
    /// and its release.
    ///
    /// Work callbacks may optionally accept a cancellation token. A newer submit for the
    /// same key cancels the active token so slow external work can stand down before the
    /// freshest pending value applies. Different keys never cancel each other.
    /// </summary>
    public sealed class LatestValueCoalescer<TKey, TValue> : IDisposable where TKey : notnull
    {
        private sealed class Slot : IDisposable
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public TValue? Pending;
            public bool HasPending;
            public CancellationTokenSource? ActiveCts;
            public bool RunnerActive;
            // TickCount64 of the last completed work invocation for this key (0 = never).
            // Only the single active runner reads/writes it, so no extra synchronization.
            public long LastWorkTicks;

            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                ActiveCts?.Dispose();
                ActiveCts = null;
                Gate.Dispose();
            }
        }

        private readonly ConcurrentDictionary<TKey, Slot> _slots = new();
        private readonly Action<TKey, TValue, CancellationToken> _work;
        private readonly int _minIntervalMs;
        private readonly object _lifetimeLock = new();
        private int _disposed;

        /// <param name="minIntervalMs">
        /// Optional floor on how often the work runs per key. When &gt; 0 the runner waits so
        /// successive invocations for a key are at least this far apart, always applying the
        /// freshest pending value. Caps hardware write rates so a runaway producer cannot
        /// hammer the work (e.g. SetDeviceGammaRamp) fast enough to stall the display system.
        /// </param>
        public LatestValueCoalescer(Action<TKey, TValue> work, int minIntervalMs = 0)
            : this(work == null
                ? throw new ArgumentNullException(nameof(work))
                : (key, value, _) => work(key, value), minIntervalMs)
        {
        }

        /// <summary>
        /// Creates a coalescer whose active work token is cancelled when a newer value is
        /// submitted for the same key, or when the coalescer is disposed.
        /// </summary>
        public LatestValueCoalescer(Action<TKey, TValue, CancellationToken> work, int minIntervalMs = 0)
        {
            _work = work ?? throw new ArgumentNullException(nameof(work));
            _minIntervalMs = Math.Max(0, minIntervalMs);
        }

        /// <summary>Submit a value for the given key. Returns immediately.</summary>
        public void Submit(TKey key, TValue value)
        {
            Slot slot;
            lock (_lifetimeLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                slot = _slots.GetOrAdd(key, _ => new Slot());
            }

            lock (slot)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                slot.Pending = value;
                slot.HasPending = true;
                slot.ActiveCts?.Cancel();
            }
            TryStartRunner(key, slot);
        }

        /// <summary>
        /// Cancels active work and drops a pending value for one key. Future submissions
        /// remain valid. This is used by immediate safety boundaries such as pausing Game
        /// Mode, where an older stabilized foreground candidate must never re-activate.
        /// </summary>
        public void Cancel(TKey key)
        {
            lock (_lifetimeLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_slots.TryGetValue(key, out var slot))
                    return;
                lock (slot)
                {
                    slot.Pending = default;
                    slot.HasPending = false;
                    slot.ActiveCts?.Cancel();
                }
            }
        }

        /// <summary>
        /// Waits (up to timeout) until no runner is actively holding the gate for the given key
        /// AND no Pending value remains. Useful for tests; not intended for production use.
        /// </summary>
        public bool WaitForIdle(TKey key, TimeSpan timeout)
        {
            if (Volatile.Read(ref _disposed) != 0) return true;
            if (!_slots.TryGetValue(key, out var slot)) return true;
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Volatile.Read(ref _disposed) != 0) return true;
                lock (slot)
                {
                    if (!slot.RunnerActive && !slot.HasPending) return true;
                }
                Thread.Sleep(1);
            }
            return false;
        }

        private void TryStartRunner(TKey key, Slot slot)
        {
            // Keep the disposal boundary closed across the state check and gate acquire.
            // Otherwise Dispose could release an idle slot's semaphore between those two
            // operations and a racing Submit would call Wait on a disposed semaphore.
            lock (_lifetimeLock)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                if (!slot.Gate.Wait(0)) return;
                lock (slot) { slot.RunnerActive = true; }
            }
            _ = Task.Run(() =>
            {
                try
                {
                    RunnerLoop(key, slot);
                }
                finally
                {
                    // Dispose owns each per-key semaphore. If shutdown raced this runner,
                    // leave the gate acquired and release its resources here once work has
                    // stopped. Idle slots are disposed directly by Dispose() below.
                    bool disposed;
                    lock (_lifetimeLock)
                    {
                        disposed = Volatile.Read(ref _disposed) != 0;
                        if (!disposed)
                        {
                            lock (slot) { slot.RunnerActive = false; }
                            slot.Gate.Release();
                        }
                    }

                    if (disposed)
                    {
                        slot.Dispose();
                    }
                    else
                    {
                        // Race recovery: a producer whose Submit landed between our last
                        // null-check and the release above would have seen the gate held and
                        // bailed out of its own TryStartRunner. Detect that case here and
                        // re-kick ourselves — otherwise the pending update is orphaned.
                        bool stillPending;
                        lock (slot) { stillPending = slot.HasPending; }
                        if (stillPending && Volatile.Read(ref _disposed) == 0) TryStartRunner(key, slot);
                    }
                }
            });
        }

        private void RunnerLoop(TKey key, Slot slot)
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    lock (slot)
                    {
                        slot.Pending = default;
                        slot.HasPending = false;
                        slot.ActiveCts?.Cancel();
                    }
                    return;
                }

                // Nothing queued: return without waiting (don't penalize a lone apply).
                lock (slot) { if (!slot.HasPending) return; }

                // Rate-limit: keep successive invocations for this key at least _minIntervalMs
                // apart. We hold the gate while sleeping, so producers only update Pending and
                // we pick up the freshest value afterward - no lost updates, capped rate.
                if (_minIntervalMs > 0 && slot.LastWorkTicks != 0)
                {
                    long sinceLast = Environment.TickCount64 - slot.LastWorkTicks;
                    if (sinceLast < _minIntervalMs)
                        Thread.Sleep((int)(_minIntervalMs - sinceLast));
                }

                TValue value;
                CancellationTokenSource workCts;
                lock (slot)
                {
                    if (!slot.HasPending) return;
                    value = slot.Pending!;
                    slot.Pending = default;
                    slot.HasPending = false;
                    slot.ActiveCts?.Dispose();
                    slot.ActiveCts = new CancellationTokenSource();
                    workCts = slot.ActiveCts;
                }

                try { _work(key, value, workCts.Token); }
                catch (OperationCanceledException) when (workCts.IsCancellationRequested)
                {
                    // Replacement/disposal cancellation is an expected coalescing outcome.
                }
                catch (Exception ex)
                {
                    // Keep the runner alive so a bad value does not orphan future work, but
                    // retain evidence instead of silently hiding callback defects.
                    Log.Error($"LatestValueCoalescer: work callback failed: {ex.Message}");
                }
                finally
                {
                    lock (slot)
                    {
                        if (ReferenceEquals(slot.ActiveCts, workCts))
                        {
                            slot.ActiveCts = null;
                        }
                    }
                    workCts.Dispose();
                }
                slot.LastWorkTicks = Environment.TickCount64;
            }
        }

        public void Dispose()
        {
            Slot[] slots;
            lock (_lifetimeLock)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                slots = _slots.Values.ToArray();
                _slots.Clear();
            }

            foreach (var slot in slots)
            {
                bool runnerActive;
                lock (slot)
                {
                    slot.Pending = default;
                    slot.HasPending = false;
                    slot.ActiveCts?.Cancel();
                    runnerActive = slot.RunnerActive;
                }

                // An active runner owns the gate and disposes the slot from its finally
                // block after the callback exits. No new runner can start past _disposed.
                if (!runnerActive)
                    slot.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
