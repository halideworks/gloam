using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class SpotreadSessionTests
    {
        /// <summary>
        /// Fake-process harness: builds a session wired to in-memory hooks instead of a
        /// real spotread.exe. Output lines are injected with SimulateOutputLine.
        /// </summary>
        private static SpotreadSession CreateFakeSession(
            out List<string> log, out List<string> written, Action? onKill = null)
        {
            var logLines = new List<string>();
            var writes = new List<string>();
            var session = new SpotreadSession(
                text => { lock (writes) writes.Add(text); return Task.CompletedTask; },
                onKill ?? (() => { }),
                line => { lock (logLines) logLines.Add(line); });
            log = logLines;
            written = writes;
            return session;
        }

        [Fact]
        public async Task MeasureAsync_XyzLineArrives_ReturnsParsedResult()
        {
            var session = CreateFakeSession(out _, out var written);

            var task = session.MeasureAsync(CancellationToken.None);
            Assert.Contains(" \r\n", written); // trigger was sent
            session.SimulateOutputLine(" Result is XYZ: 95.047 100.000 108.883, D50 Lab: 100 0 0");

            var xyz = await task;
            Assert.Equal(100.0, xyz.Y, 6);
            Assert.Equal(95.047, xyz.X, 6);
            Assert.False(session.IsPoisoned);
        }

        [Fact]
        public async Task MeasureAsync_Timeout_PoisonsSessionAndKillsProcess()
        {
            bool killed = false;
            var session = CreateFakeSession(out _, out _, () => killed = true);
            session.MeasureTimeout = TimeSpan.FromMilliseconds(50);

            await Assert.ThrowsAsync<TimeoutException>(
                () => session.MeasureAsync(CancellationToken.None));

            Assert.True(session.IsPoisoned);
            Assert.True(killed);
        }

        [Fact]
        public async Task MeasureAsync_LateXyzAfterTimeout_IsNotAttributedToNextMeasurement()
        {
            // C1: the timed-out trigger's XYZ line eventually arrives. It must NOT complete
            // the next measurement (off-by-one stale attribution); the poisoned session
            // refuses to measure until restarted.
            var session = CreateFakeSession(out var log, out _);
            session.MeasureTimeout = TimeSpan.FromMilliseconds(50);

            await Assert.ThrowsAsync<TimeoutException>(
                () => session.MeasureAsync(CancellationToken.None));

            // Late arrival from the timed-out trigger:
            session.SimulateOutputLine("Result is XYZ: 10.0 20.0 30.0");
            Assert.Contains(log, l => l.Contains("stale", StringComparison.OrdinalIgnoreCase));

            // The next measurement must not consume the stale value.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.MeasureAsync(CancellationToken.None));
            Assert.Contains("poisoned", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void StaleXyzLine_WithNoPendingMeasurement_IsIgnoredAndLogged()
        {
            var session = CreateFakeSession(out var log, out _);

            session.SimulateOutputLine("Result is XYZ: 1.0 2.0 3.0");

            Assert.Contains(log, l => l.Contains("stale", StringComparison.OrdinalIgnoreCase));
            Assert.Null(session.FatalErrorForTest);
            Assert.False(session.IsPoisoned);
        }

        [Fact]
        public async Task MeasureAsync_UserCancellation_DoesNotPoisonSession()
        {
            var session = CreateFakeSession(out _, out _);
            using var cts = new CancellationTokenSource();
            var task = session.MeasureAsync(cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(session.IsPoisoned);
        }

        [Fact]
        public void FatalMatching_MidLineErrorMention_IsNotFatal()
        {
            // m11: instrument-info lines that merely CONTAIN an error token must not
            // abort the run.
            var session = CreateFakeSession(out _, out _);

            session.SimulateOutputLine("Instrument info: supports error compensation and error- style diagnostics");
            session.SimulateOutputLine("Serial number ERROR-42-B (hardware revision)");

            Assert.Null(session.FatalErrorForTest);
        }

        [Fact]
        public void FatalMatching_ErrorAtLineStart_IsFatal()
        {
            var session = CreateFakeSession(out _, out _);

            session.SimulateOutputLine("Error - Opening the USB port failed");

            Assert.NotNull(session.FatalErrorForTest);
        }

        [Fact]
        public void FatalMatching_SpotreadPrefixedError_IsFatal()
        {
            var session = CreateFakeSession(out _, out _);

            session.SimulateOutputLine("spotread: Error - Communications failure with the instrument");

            Assert.NotNull(session.FatalErrorForTest);
        }

        [Fact]
        public void FatalMatching_SpecificFragments_RemainFatalAnywhereInLine()
        {
            var session = CreateFakeSession(out _, out _);

            session.SimulateOutputLine("diagnostic: instrument did not respond to init");

            Assert.NotNull(session.FatalErrorForTest);
        }

        [Fact]
        public void TryAcceptMeasuredXyz_FiniteNonNegativePhysicalSample_Passes()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(95.0, 100.0, 108.0), out var error);

            Assert.True(accepted, error);
        }

        [Fact]
        public void TryAcceptMeasuredXyz_NonFiniteSample_Fails()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(95.0, double.NaN, 108.0), out var error);

            Assert.False(accepted);
            Assert.Contains("non-finite", error);
        }

        [Fact]
        public void TryAcceptMeasuredXyz_NegativePhysicalSample_Fails()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(95.0, 100.0, -0.01), out var error);

            Assert.False(accepted);
            Assert.Contains("negative XYZ", error);
        }

        [Fact]
        public void TryAcceptMeasuredXyz_TinyNegativeRoundoff_Passes()
        {
            var accepted = SpotreadSession.TryAcceptMeasuredXyz(new CieXyz(-1e-7, 100.0, 108.0), out var error);

            Assert.True(accepted, error);
        }

        // --- spectral mode (spotread -s with a spectrometer) -------------------------

        [Fact]
        public async Task MeasureSpectralAsync_XyzThenWrappedSpectrum_ReturnsReading()
        {
            var session = CreateFakeSession(out _, out var written);

            var task = session.MeasureSpectralAsync(CancellationToken.None);
            Assert.Contains(" \r\n", written); // trigger was sent

            session.SimulateOutputLine(" Result is XYZ: 95.047 100.000 108.883, D50 Lab: 100 0 0");
            session.SimulateOutputLine("Spectrum from 380.000000 to 730.000000 nm in 6 steps");
            // Values wrapped over two lines, comma separated (with trailing comma).
            session.SimulateOutputLine("  0.1000, 0.2000, 0.3000,");
            session.SimulateOutputLine("  0.4000, 0.5000, 0.6000");

            var reading = await task;
            Assert.Equal(100.0, reading.Xyz.Y, 6);
            Assert.Equal(380.0, reading.Spectrum.StartNm, 6);
            Assert.Equal(730.0, reading.Spectrum.EndNm, 6);
            Assert.Equal(6, reading.Spectrum.Bands);
            Assert.Equal(new[] { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6 }, reading.Spectrum.Values);
            Assert.Equal(380.0, reading.Spectrum.WavelengthAt(0), 6);
            Assert.Equal(730.0, reading.Spectrum.WavelengthAt(5), 6);
            Assert.False(session.IsPoisoned);
        }

        [Fact]
        public async Task MeasureSpectralAsync_SpectrumBeforeXyz_StillCompletes()
        {
            // Argyll versions differ on ordering; the session must not assume XYZ-first.
            var session = CreateFakeSession(out _, out _);

            var task = session.MeasureSpectralAsync(CancellationToken.None);
            session.SimulateOutputLine("Spectrum from 400.0 to 700.0 nm in 4 steps");
            session.SimulateOutputLine("1.0 2.0 3.0 4.0");
            session.SimulateOutputLine("Result is XYZ: 50.0 52.0 54.0");

            var reading = await task;
            Assert.Equal(52.0, reading.Xyz.Y, 6);
            Assert.Equal(new[] { 1.0, 2.0, 3.0, 4.0 }, reading.Spectrum.Values);
        }

        [Fact]
        public async Task MeasureSpectralAsync_ValuesOnHeaderLine_AreParsed()
        {
            var session = CreateFakeSession(out _, out _);

            var task = session.MeasureSpectralAsync(CancellationToken.None);
            session.SimulateOutputLine("Result is XYZ: 10.0 11.0 12.0");
            session.SimulateOutputLine("Spectrum from 380.0 to 730.0 nm in 3 steps: 7.0, 8.0, 9.0");

            var reading = await task;
            Assert.Equal(new[] { 7.0, 8.0, 9.0 }, reading.Spectrum.Values);
        }

        [Fact]
        public async Task MeasureSpectralAsync_ChatterLineMidSpectrum_IsIgnored()
        {
            var session = CreateFakeSession(out _, out _);

            var task = session.MeasureSpectralAsync(CancellationToken.None);
            session.SimulateOutputLine("Result is XYZ: 10.0 11.0 12.0");
            session.SimulateOutputLine("Spectrum from 380.0 to 730.0 nm in 4 steps");
            session.SimulateOutputLine("1.5 2.5");
            session.SimulateOutputLine("Instrument status: OK"); // chatter must not corrupt collection
            session.SimulateOutputLine("3.5 4.5");

            var reading = await task;
            Assert.Equal(new[] { 1.5, 2.5, 3.5, 4.5 }, reading.Spectrum.Values);
        }

        [Fact]
        public async Task MeasureSpectralAsync_Timeout_PoisonsSessionAndKillsProcess()
        {
            bool killed = false;
            var session = CreateFakeSession(out _, out _, () => killed = true);
            session.MeasureTimeout = TimeSpan.FromMilliseconds(50);

            await Assert.ThrowsAsync<TimeoutException>(
                () => session.MeasureSpectralAsync(CancellationToken.None));

            Assert.True(session.IsPoisoned);
            Assert.True(killed);
        }

        [Fact]
        public void StaleSpectrumHeader_WithNoPendingMeasurement_IsIgnoredAndLogged()
        {
            var session = CreateFakeSession(out var log, out _);

            session.SimulateOutputLine("Spectrum from 380.0 to 730.0 nm in 4 steps");

            Assert.Contains(log, l => l.Contains("stale", StringComparison.OrdinalIgnoreCase));
            Assert.Null(session.FatalErrorForTest);
        }

        [Fact]
        public async Task MeasureSpectralAsync_UnparseableHeaderCount_FailsTheReading()
        {
            var session = CreateFakeSession(out _, out _);

            var task = session.MeasureSpectralAsync(CancellationToken.None);
            session.SimulateOutputLine("Spectrum from 730.0 to 380.0 nm in 4 steps"); // end < start

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
            Assert.Contains("spectrum header", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task MeasureSpectralAsync_BlocksConcurrentMeasurements()
        {
            var session = CreateFakeSession(out _, out _);

            var task = session.MeasureSpectralAsync(CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.MeasureAsync(CancellationToken.None));

            // Complete the spectral reading so the pending task doesn't leak.
            session.SimulateOutputLine("Result is XYZ: 1.0 2.0 3.0");
            session.SimulateOutputLine("Spectrum from 380.0 to 730.0 nm in 3 steps: 1 2 3");
            await task;
        }

        [Fact]
        public async Task MeasureSpectralAsync_FatalLine_FailsTheReading()
        {
            var session = CreateFakeSession(out _, out _);

            var task = session.MeasureSpectralAsync(CancellationToken.None);
            session.SimulateOutputLine("spotread: Error - Communications failure with the instrument");

            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }
    }
}
