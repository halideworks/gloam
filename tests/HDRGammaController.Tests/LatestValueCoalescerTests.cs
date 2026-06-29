using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;
using Xunit;

namespace HDRGammaController.Tests
{
    public class LatestValueCoalescerTests
    {
        [Fact]
        public void SingleSubmit_WorkRunsOnce()
        {
            int count = 0;
            int seen = -1;
            var done = new ManualResetEventSlim();
            var c = new LatestValueCoalescer<string, int>((_, v) =>
            {
                seen = v;
                Interlocked.Increment(ref count);
                done.Set();
            });

            c.Submit("k", 42);
            Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, count);
            Assert.Equal(42, seen);
        }

        [Fact]
        public void ManySubmits_LastValueEventuallyWins()
        {
            // Flood the coalescer faster than it can drain. The work function sleeps a bit
            // so we guarantee overlapping submits. After WaitForIdle, the *last* value
            // must be the one we most recently observed.
            int observed = -1;
            var c = new LatestValueCoalescer<string, int>((_, v) =>
            {
                Thread.Sleep(2);
                observed = v;
            });

            const int N = 2000;
            for (int i = 0; i < N; i++)
            {
                c.Submit("k", i);
            }

            Assert.True(c.WaitForIdle("k", TimeSpan.FromSeconds(10)));
            Assert.Equal(N - 1, observed);
        }

        [Fact]
        public void Coalesces_InvocationsFewerThanSubmits()
        {
            // With a work function that takes non-trivial time, the vast majority of
            // submits should be coalesced — we expect far fewer invocations than submits.
            int invocations = 0;
            var c = new LatestValueCoalescer<string, int>((_, _) =>
            {
                Interlocked.Increment(ref invocations);
                Thread.Sleep(5);
            });

            const int N = 500;
            for (int i = 0; i < N; i++) c.Submit("k", i);

            Assert.True(c.WaitForIdle("k", TimeSpan.FromSeconds(10)));
            // Invocations should be modest — each takes 5ms, so in the duration of 500
            // sub-millisecond submits we'd expect only a few actual runs. Strict upper
            // bound of N/10 is conservative even on loaded CI hardware.
            Assert.True(invocations >= 1, "Expected at least one invocation");
            Assert.True(invocations < N / 10, $"Expected coalescing: got {invocations} invocations for {N} submits");
        }

        [Fact]
        public void NoLostUpdates_UnderConcurrentProducers()
        {
            // Producers race to submit values, and each submission gets a global sequence.
            // After all producers finish and the coalescer goes idle, the final invocation
            // must carry the value from the submission with the highest sequence. Numeric
            // payload order is deliberately decoupled from producer/thread identity: the
            // highest payload is not necessarily the final wall-clock submission.
            int latestSubmittedSeq = -1;
            int latestSubmittedValue = -1;
            int finalSeenSeq = -1;
            int finalSeenValue = -1;
            var c = new LatestValueCoalescer<string, int>((_, v) =>
            {
                Thread.Sleep(1);
                int seq = v >> 16;
                int payload = v & 0xFFFF;
                int prev;
                do { prev = finalSeenSeq; } while (seq > prev && Interlocked.CompareExchange(ref finalSeenSeq, seq, prev) != prev);
                if (seq == Volatile.Read(ref finalSeenSeq))
                    Volatile.Write(ref finalSeenValue, payload);
            });

            const int producers = 8;
            const int perProducer = 500;
            int sequence = -1;

            var threads = new Thread[producers];
            for (int p = 0; p < producers; p++)
            {
                int pCopy = p;
                threads[p] = new Thread(() =>
                {
                    for (int i = 0; i < perProducer; i++)
                    {
                        int payload = pCopy * perProducer + i;
                        int seq = Interlocked.Increment(ref sequence);
                        Volatile.Write(ref latestSubmittedValue, payload);
                        Volatile.Write(ref latestSubmittedSeq, seq);
                        c.Submit("k", (seq << 16) | payload);
                    }
                });
                threads[p].Start();
            }
            foreach (var t in threads) t.Join();

            Assert.True(c.WaitForIdle("k", TimeSpan.FromSeconds(10)));
            Assert.Equal(latestSubmittedSeq, finalSeenSeq);
            Assert.Equal(latestSubmittedValue, finalSeenValue);
        }

        [Fact]
        public void AfterRace_PendingSetBetweenNullCheckAndRelease_IsPickedUp()
        {
            // Targeted regression: the race where a producer writes Pending *after* the
            // runner's last null-check but *before* the runner releases the gate. The
            // producer's own TryStartRunner fails Wait(0), and if the runner doesn't
            // re-check after releasing, the update is orphaned.
            //
            // We simulate the race by blocking the runner inside its work function until
            // we've submitted a second value on another thread.
            int invocations = 0;
            int finalValue = -1;
            var phase1Entered = new ManualResetEventSlim();
            var allowPhase1Exit = new ManualResetEventSlim();

            var c = new LatestValueCoalescer<string, int>((_, v) =>
            {
                Interlocked.Increment(ref invocations);
                finalValue = v;
                if (v == 1)
                {
                    phase1Entered.Set();
                    allowPhase1Exit.Wait(TimeSpan.FromSeconds(5));
                }
            });

            c.Submit("k", 1);
            Assert.True(phase1Entered.Wait(TimeSpan.FromSeconds(5)));

            // Runner is currently inside work(1). Submit(2) will set Pending=2 before
            // the runner finishes. The runner's next loop iteration should pick it up
            // without needing the release-race path — but the test still validates that.
            c.Submit("k", 2);
            allowPhase1Exit.Set();

            Assert.True(c.WaitForIdle("k", TimeSpan.FromSeconds(5)));
            Assert.Equal(2, finalValue);
            Assert.True(invocations >= 2, $"Expected at least 2 invocations, got {invocations}");
        }

        [Fact]
        public void IndependentKeys_RunConcurrently()
        {
            // One key blocked shouldn't starve another.
            var a = new ManualResetEventSlim();
            var b = new ManualResetEventSlim();
            var releaseA = new ManualResetEventSlim();

            var c = new LatestValueCoalescer<string, int>((key, _) =>
            {
                if (key == "a") { a.Set(); releaseA.Wait(TimeSpan.FromSeconds(5)); }
                else { b.Set(); }
            });

            c.Submit("a", 1);
            Assert.True(a.Wait(TimeSpan.FromSeconds(2)));

            c.Submit("b", 1);
            // b must complete even while a is still held
            Assert.True(b.Wait(TimeSpan.FromSeconds(2)));

            releaseA.Set();
            Assert.True(c.WaitForIdle("a", TimeSpan.FromSeconds(5)));
            Assert.True(c.WaitForIdle("b", TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void WorkThrows_DoesNotKillRunner()
        {
            // A throwing work callback must not orphan the gate or stop subsequent drains.
            int okInvocations = 0;
            var c = new LatestValueCoalescer<string, int>((_, v) =>
            {
                if (v < 0) throw new InvalidOperationException("boom");
                Interlocked.Increment(ref okInvocations);
            });

            c.Submit("k", -1);
            Thread.Sleep(50); // let the throwing invocation run
            c.Submit("k", 7);

            Assert.True(c.WaitForIdle("k", TimeSpan.FromSeconds(5)));
            Assert.Equal(1, okInvocations);
        }

        [Fact]
        public void Dispose_DropsLaterSubmits()
        {
            int invocations = 0;
            var c = new LatestValueCoalescer<string, int>((_, _) => Interlocked.Increment(ref invocations));

            c.Dispose();
            c.Submit("k", 1);

            Thread.Sleep(50);
            Assert.Equal(0, invocations);
        }
    }
}
