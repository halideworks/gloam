using System;
using System.Threading.Tasks;
using Gloam.Core;

namespace Gloam.Services
{
    /// <summary>
    /// Backstop for fire-and-forget async work started from an event handler.
    ///
    /// An <c>async void</c> method that lets an exception escape rethrows it on the
    /// dispatcher, where nothing is awaiting it — that is a process crash, not a failed
    /// click. Every such handler in this app is therefore written as
    ///
    ///     private void Foo_Click(object sender, RoutedEventArgs e)
    ///         => SafeAsync.FireAndForget(() => Foo_ClickAsync(sender, e), nameof(Foo_Click));
    ///
    ///     private async Task Foo_ClickAsync(...) { ...the real body... }
    ///
    /// which is airtight rather than merely careful: a C# <c>async Task</c> method never
    /// throws synchronously — including for arguments validated before the first await —
    /// so every failure is delivered on the returned Task, and the Task is awaited inside
    /// the try below. There is no path around it.
    ///
    /// This is a BACKSTOP, not the error-reporting path. Handlers that have a natural
    /// place to show a failure (a status strip, a dialog, a toast) keep doing that in
    /// their own try/catch, which runs first; what reaches here is the unanticipated
    /// remainder. So this logs and stops, and deliberately does not raise a dialog of its
    /// own: an unexpected failure often arrives while the window or dispatcher is already
    /// tearing down, and showing UI from that state is a good way to turn one swallowed
    /// bug into a second crash. Pass <paramref name="onError"/> when a caller does have a
    /// safe surface for it.
    /// </summary>
    internal static class SafeAsync
    {
        /// <summary>
        /// Runs <paramref name="work"/> and guarantees no exception reaches the dispatcher.
        /// </summary>
        /// <param name="work">The handler body, as an async Task method.</param>
        /// <param name="context">Handler name, used as the log prefix.</param>
        /// <param name="onError">
        /// Optional user-visible surface. Invoked on the dispatcher thread the handler
        /// started on; its own failures are caught and logged rather than rethrown.
        /// </param>
        internal static async void FireAndForget(
            Func<Task> work,
            string context,
            Action<Exception>? onError = null)
        {
            ArgumentNullException.ThrowIfNull(work);

            try
            {
                await work();
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an ordinary outcome in the measurement and download
                // flows (user cancels, window closes mid-probe). Not a failure.
                Log.Info($"{context}: cancelled.");
            }
            catch (Exception ex)
            {
                Log.Error($"{context}: unhandled exception escaped the handler: {ex}");
                if (onError == null) return;
                try
                {
                    onError(ex);
                }
                catch (Exception reportEx)
                {
                    // The reporting surface itself failed - log and stop. Rethrowing here
                    // would defeat the entire purpose of this method.
                    Log.Error($"{context}: error surface also failed: {reportEx}");
                }
            }
        }
    }
}
