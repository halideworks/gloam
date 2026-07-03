using HDRGammaController.ViewModels;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationReportViewModelTests
    {
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void DeltaEBrush_NonFiniteValues_UseDefaultBrush(double deltaE)
        {
            Assert.Same(
                CalibrationReportViewModel.DefaultValueBrush,
                CalibrationReportViewModel.DeltaEBrush(deltaE));
            Assert.Same(
                CalibrationReportViewModel.DefaultValueBrush,
                CalibrationReportViewModel.DeltaEPrintBrush(deltaE));
        }

        [Fact]
        public void AvgDeltaEText_WithUncertaintyTail_SplitsIntoValueAndDimTail()
        {
            var vm = new CalibrationReportViewModel();

            vm.AvgDeltaEText = "1.23 ± 0.45";

            // The full string is preserved (the print export consumes it whole)...
            Assert.Equal("1.23 ± 0.45", vm.AvgDeltaEText);
            // ...while the window renders the value and the ± tail as separate Runs so the
            // uncertainty can be dim/small instead of alarm-colored at headline size.
            Assert.Equal("1.23", vm.AvgDeltaEValueText);
            Assert.Equal(" ± 0.45", vm.AvgDeltaEUncertaintyText);
        }

        [Fact]
        public void AvgDeltaEText_WithoutUncertainty_LeavesTailEmpty()
        {
            var vm = new CalibrationReportViewModel();

            vm.AvgDeltaEText = "2.10";

            Assert.Equal("2.10", vm.AvgDeltaEValueText);
            Assert.Equal("", vm.AvgDeltaEUncertaintyText);
        }

        [Fact]
        public void AfterAvgText_WithUncertaintyTail_SplitsIntoValueAndDimTail()
        {
            var vm = new CalibrationReportViewModel();

            vm.AfterAvgText = "0.80 ± 0.30";

            Assert.Equal("0.80", vm.AfterAvgValueText);
            Assert.Equal(" ± 0.30", vm.AfterAvgUncertaintyText);
        }

        [Fact]
        public void AfterAvgText_Placeholder_HasNoTail()
        {
            var vm = new CalibrationReportViewModel();

            vm.AfterAvgText = "-";

            Assert.Equal("-", vm.AfterAvgValueText);
            Assert.Equal("", vm.AfterAvgUncertaintyText);
        }
    }
}
