using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using HDRGammaController.Services;
using Xunit;

namespace HDRGammaController.Tests
{
    public class CalibrationChartsTests
    {
        private static void RunSta(Action body)
        {
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        private static Canvas ChartCanvas()
        {
            var canvas = new Canvas
            {
                Width = 320,
                Height = 180,
            };
            var size = new Size(canvas.Width, canvas.Height);
            canvas.Measure(size);
            canvas.Arrange(new Rect(size));
            canvas.UpdateLayout();
            return canvas;
        }

        [Fact]
        public void DrawLineChart_IgnoresNonFiniteSeriesPoints()
        {
            RunSta(() =>
            {
                var canvas = ChartCanvas();
                var series = new[]
                {
                    new CalibrationCharts.Series(
                        "Measured",
                        System.Windows.Media.Colors.Orange,
                        new[] { (0.0, 0.0), (0.25, double.NaN), (0.5, 0.5), (double.PositiveInfinity, 0.8) }),
                };

                CalibrationCharts.DrawLineChart(canvas, series, 0, 1, 0, 1, "Input", "Output");

                var polyline = Assert.Single(canvas.Children.OfType<Polyline>());
                Assert.Equal(2, polyline.Points.Count);
                Assert.All(polyline.Points, point =>
                {
                    Assert.True(double.IsFinite(point.X));
                    Assert.True(double.IsFinite(point.Y));
                });
            });
        }

        [Fact]
        public void DrawGamutDiagram_IgnoresInvalidMeasuredTriangle()
        {
            RunSta(() =>
            {
                var canvas = ChartCanvas();

                CalibrationCharts.DrawGamutDiagram(
                    canvas,
                    (0.64, 0.33), (0.30, 0.60), (0.15, 0.06), (0.3127, 0.3290),
                    (double.NaN, 0.33), (0.30, 0.60), (0.15, 0.06), (0.3127, 0.3290));

                var polygons = canvas.Children.OfType<Polygon>().ToList();
                Assert.Single(polygons);
                Assert.All(polygons[0].Points, point =>
                {
                    Assert.True(double.IsFinite(point.X));
                    Assert.True(double.IsFinite(point.Y));
                });
            });
        }

        [Fact]
        public void DrawDeltaEHistogram_ClampsNegativeCountsToZero()
        {
            RunSta(() =>
            {
                var canvas = ChartCanvas();

                CalibrationCharts.DrawDeltaEHistogram(canvas, new[] { 2, -5, 1 }, new[] { "<0.5", "0.5-1", "1-2" });

                Assert.Equal(3, canvas.Children.OfType<Rectangle>().Count());
                Assert.DoesNotContain(canvas.Children.OfType<TextBlock>(), label => label.Text == "-5");
                Assert.All(canvas.Children.OfType<Rectangle>(), rectangle =>
                {
                    Assert.True(rectangle.Height >= 0);
                    Assert.True(double.IsFinite(rectangle.Height));
                });
            });
        }

        [Fact]
        public void DrawPerPatchDeltaE_IgnoresImpossibleDeltaEValues()
        {
            RunSta(() =>
            {
                var canvas = ChartCanvas();
                var patches = new[]
                {
                    ("Good", 1.2),
                    ("NaN", double.NaN),
                    ("Inf", double.PositiveInfinity),
                    ("Negative", -0.5),
                    ("High", 6.0),
                };

                CalibrationCharts.DrawPerPatchDeltaE(canvas, patches);

                var bars = canvas.Children.OfType<Rectangle>().ToList();
                Assert.Equal(2, bars.Count);
                Assert.All(bars, bar =>
                {
                    Assert.True(bar.Height >= 0);
                    Assert.True(double.IsFinite(bar.Height));
                    Assert.True(double.IsFinite(Canvas.GetLeft(bar)));
                    Assert.True(double.IsFinite(Canvas.GetTop(bar)));
                });
            });
        }
    }
}
