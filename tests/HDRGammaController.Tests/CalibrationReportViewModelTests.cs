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
    }
}
