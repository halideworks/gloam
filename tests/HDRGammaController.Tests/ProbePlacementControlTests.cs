using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Input;
using Xunit;

namespace HDRGammaController.Tests
{
    public sealed class ProbePlacementControlTests
    {
        [Fact]
        public void ConfigureAndNudge_MaintainOneSharedPlacementState()
        {
            RunSta(() =>
            {
                var control = new ProbePlacementControl();
                control.Configure(600, 15, -10, "Verification", secondaryLabel: "Cancel");

                Assert.Equal(15, control.OffsetX);
                Assert.Equal(-10, control.OffsetY);
                Assert.True(control.TryNudge(Key.Right, largerStep: false));
                Assert.True(control.TryNudge(Key.Down, largerStep: true));
                Assert.Equal(20, control.OffsetX);
                Assert.Equal(15, control.OffsetY);
                Assert.False(control.TryNudge(Key.A, largerStep: false));
            });
        }

        private static void RunSta(Action body)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }
    }
}
