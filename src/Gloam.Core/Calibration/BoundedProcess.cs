using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Gloam.Core.Calibration
{
    /// <summary>Result of <see cref="BoundedProcess.RunAsync"/>.</summary>
    public sealed class BoundedProcessResult
    {
        public BoundedProcessResult(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout;
            Stderr = stderr;
        }

        public int ExitCode { get; }
        public string Stdout { get; }
        public string Stderr { get; }

        /// <summary>stdout followed by stderr, the shape the Argyll output parsers expect.</summary>
        public string Combined => Stdout + "\n" + Stderr;
    }

    /// <summary>
    /// Runs a console tool to completion with both streams captured and a hard time limit.
    /// Shared by the Argyll one-shot invocations (spotread -?, oeminst).
    /// </summary>
    public static class BoundedProcess
    {
        /// <summary>
        /// Starts <paramref name="psi"/> and waits for exit. Throws
        /// <see cref="OperationCanceledException"/> after killing the process tree when
        /// <paramref name="timeout"/> elapses or <paramref name="cancellationToken"/> fires.
        /// </summary>
        public static async Task<BoundedProcessResult> RunAsync(
            ProcessStartInfo psi, TimeSpan timeout, CancellationToken cancellationToken)
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"{psi.FileName} did not start.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = process.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return new BoundedProcessResult(process.ExitCode, await stdout, await stderr);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.DebugRateLimited(
                        "bounded-process-termination",
                        $"Could not terminate the timed-out process {psi.FileName}: {ex.Message}",
                        TimeSpan.FromMinutes(10));
                }
                throw;
            }
        }
    }
}
