using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using HDRGammaController.Core.Calibration;
using HDRGammaController.Services;
using HDRGammaController.ViewModels;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Regression guard for the Export Report crash: drives the FlowDocument paginator
    /// (PTS, WPF's native pagination engine) headlessly over a fully populated print
    /// document - exactly what PrintDialog.PrintDocument does - so any PTS fault
    /// (RuntimeWrappedException at print time) reproduces deterministically in CI instead
    /// of on the user's printer. Letter page at 96 DPI: 816 x 1056.
    /// </summary>
    public class ReportPrintPaginationTests
    {
        private const double PageWidth = 816;
        private const double PageHeight = 1056;
        private const double Margin = 48;

        /// <summary>Runs the body on an STA thread (WPF requirement) and rethrows failures.</summary>
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

        /// <summary>
        /// A synthetic chart figure produced through the SAME offscreen-canvas pipeline the
        /// report window uses for printing (CreatePrintCanvas + RenderPrintCanvas), so the
        /// bitmaps have the real print figure size and DPI.
        /// </summary>
        private static ReportPrintBuilder.ChartFigure Figure(string title)
        {
            var canvas = ReportPrintBuilder.CreatePrintCanvas();
            canvas.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = ReportPrintBuilder.PrintChartWidth,
                Height = ReportPrintBuilder.PrintChartHeight,
                Fill = Brushes.WhiteSmoke,
            });
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0,
                Y1 = ReportPrintBuilder.PrintChartHeight,
                X2 = ReportPrintBuilder.PrintChartWidth,
                Y2 = 0,
                Stroke = Brushes.DarkSlateGray,
                StrokeThickness = 2,
            });
            return new ReportPrintBuilder.ChartFigure(title, ReportPrintBuilder.RenderPrintCanvas(canvas));
        }

        /// <summary>A view model with every display string the print layout consumes.</summary>
        private static CalibrationReportViewModel RepresentativeVm()
        {
            var vm = new CalibrationReportViewModel
            {
                MonitorNameText = "Dell U2723QE (DP-1)",
                CalibrationDateText = "Calibrated: 6/11/2026 10:30 AM",
                GradeText = "A-",
                GradeScopeText = "after correction",
                AvgDeltaEText = "2.31",
                MaxDeltaEText = "6.04",
                GrayscaleDeltaEText = "1.88",
                PrimaryDeltaEText = "3.12",
                AfterAvgText = "0.74",
                AfterMaxText = "2.05",
                AfterGrayscaleText = "0.61",
                AfterPrimaryText = "0.96",
                SummaryText = "Very good calibration. Your display shows excellent color accuracy " +
                              "suitable for color-critical work. Minor variations may exist in edge cases.",
                PeakLuminanceText = "412.6 cd/m2",
                BlackLevelText = "0.0489 cd/m2",
                ContrastRatioText = "8438:1",
                MeasuredGammaText = "2.21",
                WhitePointCctText = "6447 K",
                WhitePointDuvText = "0.0021",
                SrgbCoverageText = "99.2%",
                TargetText = "sRGB / Gamma 2.2 / D65",
                RedMeasuredText = "(0.672, 0.312)",
                RedTargetText = "(0.640, 0.330)",
                RedErrorText = "0.0367",
                GreenMeasuredText = "(0.284, 0.618)",
                GreenTargetText = "(0.300, 0.600)",
                GreenErrorText = "0.0241",
                BlueMeasuredText = "(0.149, 0.052)",
                BlueTargetText = "(0.150, 0.060)",
                BlueErrorText = "0.0081",
                WhiteMeasuredText = "(0.309, 0.325)",
                WhiteTargetText = "(0.313, 0.329)",
                WhiteErrorText = "0.0057",
                PatchCountText = "118",
                MeasurementTimeText = "4:37",
                ColorimeterText = "X-Rite i1Display Pro",
                LutSizeText = "33x33x33",
                ProfilePathText = @"C:\Users\Test\AppData\Local\HDRGammaController\Profiles\Dell U2723QE.json",
            };
            vm.Recommendations.Add("Your display is calibrated to professional standards.");
            vm.Recommendations.Add("Re-calibrate every 2-4 weeks to maintain accuracy.");
            vm.Recommendations.Add("Native white point (6447K) sits close to D65; no OSD change needed.");
            return vm;
        }

        private static ReportPrintBuilder.DetailedPrintSection RepresentativeDetailedSection()
        {
            // 39 patches matching the real detailed sweep's composition and naming.
            var rng = new Random(7);
            var patches = VerificationPatchSets
                .Detailed(StandardTargets.SrgbGamma22, hdrMode: false)
                .Select(p => new PatchDeltaE(p.Name, p.Category, rng.NextDouble() * 4.5))
                .ToList();

            return new ReportPrintBuilder.DetailedPrintSection(
                new List<ReportPrintBuilder.ChartFigure>
                {
                    Figure("Delta E Distribution"),
                    Figure("Per-patch Delta E"),
                },
                VerificationAnalysis.WorstPatches(patches),
                VerificationAnalysis.BestPatches(patches),
                VerificationAnalysis.ComputeCategoryBreakdown(patches).ToDisplayText());
        }

        /// <summary>
        /// Paginates the document the same way printing does: page size set on the
        /// paginator, full page count computed, then EVERY page materialized. With
        /// <paramref name="serializeToXps"/> the paginator is additionally written through
        /// the XPS document writer - the exact engine PrintDialog.PrintDocument drives.
        /// </summary>
        private static void PaginateFully(FlowDocument document, bool serializeToXps = false)
        {
            document.PageWidth = PageWidth;
            document.PageHeight = PageHeight;
            document.PagePadding = new Thickness(Margin);
            document.ColumnWidth = PageWidth - Margin * 2; // single column, like the export handler

            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.PageSize = new Size(PageWidth, PageHeight);
            paginator.ComputePageCount();

            Assert.True(paginator.PageCount > 0, "paginated report must have at least one page");
            for (int i = 0; i < paginator.PageCount; i++)
            {
                using var page = paginator.GetPage(i);
                Assert.NotEqual(DocumentPage.Missing, page);
                Assert.NotNull(page.Visual);
            }

            if (!serializeToXps) return;
            string xpsPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"hdrgc_print_test_{Guid.NewGuid():N}.xps");
            try
            {
                using var xps = new System.Windows.Xps.Packaging.XpsDocument(
                    xpsPath, System.IO.FileAccess.Write);
                var writer = System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(xps);
                writer.Write(paginator);
            }
            finally
            {
                try { System.IO.File.Delete(xpsPath); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void FullReport_WithChartsAndDetailedSection_PaginatesEveryPage()
        {
            RunSta(() =>
            {
                var charts = new List<ReportPrintBuilder.ChartFigure>
                {
                    Figure("Tone Response"),
                    Figure("Gamma"),
                    Figure("RGB Balance"),
                    Figure("Gamut"),
                };

                var document = ReportPrintBuilder.Build(
                    RepresentativeVm(), isHistorical: false, charts, RepresentativeDetailedSection());

                PaginateFully(document, serializeToXps: true);
            });
        }

        [Fact]
        public void FullReport_OddChartCount_PaginatesEveryPage()
        {
            // An odd figure count exercises the filler cell in the two-up figure grid.
            RunSta(() =>
            {
                var charts = new List<ReportPrintBuilder.ChartFigure>
                {
                    Figure("Tone Response"),
                    Figure("Gamma"),
                    Figure("RGB Balance"),
                };

                var document = ReportPrintBuilder.Build(
                    RepresentativeVm(), isHistorical: false, charts, detailed: null);

                PaginateFully(document);
            });
        }

        [Fact]
        public void HistoricalReport_NoCharts_WithDetailedSection_PaginatesEveryPage()
        {
            // Historical reports print the charts note instead of figures, but the detailed
            // section still prints (rebuilt from the persisted per-patch list).
            RunSta(() =>
            {
                var document = ReportPrintBuilder.Build(
                    RepresentativeVm(), isHistorical: true, charts: null, RepresentativeDetailedSection());

                PaginateFully(document);
            });
        }
    }
}
