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
    /// </summary>
    public sealed class LatestValueCoalescer<TKey, TValue> where TKey : notnull
    {
        private sealed class Slot
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public TValue? Pending;
            public bool HasPending;
        }

        private readonly ConcurrentDictionary<TKey, Slot> _slots = new();
        private readonly Action<TKey, TValue> _work;

        public LatestValueCoalescer(Action<TKey, TValue> work)
        {
            _work = work ?? throw new ArgumentNullException(nameof(work));
        }

        /// <summary>Submit a value for the given key. Returns immediately.</summary>
        public void Submit(TKey key, TValue value)
        {
            var slot = _slots.GetOrAdd(key, _ => new Slot());
            lock (slot)
            {
                slot.Pending = value;
                slot.HasPending = true;
            }
            TryStartRunner(key, slot);
        }

        /// <summary>
        /// Waits (up to timeout) until no runner is actively holding the gate for the given key
        /// AND no Pending value remains. Useful for tests; not intended for production use.
        /// </summary>
        public bool WaitForIdle(TKey key, TimeSpan timeout)
        {
            if (!_slots.TryGetValue(key, out var slot)) return true;
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (slot.Gate.Wait(0))
                {
                    try
                    {
                        lock (slot)
                        {
                            if (!slot.HasPending) return true;
                        }
                    }
                    finally { slot.Gate.Release(); }
                }
                Thread.Sleep(1);
            }
            return false;
        }

        private void TryStartRunner(TKey key, Slot slot)
        {
            if (!slot.Gate.Wait(0)) return;
            _ = Task.Run(() =>
            {
                try
                {
                    RunnerLoop(key, slot);
                }
                finally
                {
                    slot.Gate.Release();

                    // Race recovery: a producer whose Submit landed between our last
                    // null-check and the release above would have seen the gate held and
                    // bailed out of its own TryStartRunner. Detect that case here and
                    // re-kick ourselves — otherwise the pending update is orphaned.
                    bool stillPending;
                    lock (slot) { stillPending = slot.HasPending; }
                    if (stillPending) TryStartRunner(key, slot);
                }
            });
        }

        private void RunnerLoop(TKey key, Slot slot)
        {
            while (true)
            {
                TValue value;
                lock (slot)
                {
                    if (!slot.HasPending) return;
                    value = slot.Pending!;
                    slot.Pending = default;
                    slot.HasPending = false;
                }

                try { _work(key, value); }
                catch
                {
                    // Swallowing is deliberate: the coalescer is a dispatcher, not an
                    // error handler. Work callbacks are expected to handle their own
                    // failure modes. An unhandled exception here would orphan the gate
                    // via finally, but we'd prefer not to tear down the runner entirely.
                }
            }
        }
    }
}
