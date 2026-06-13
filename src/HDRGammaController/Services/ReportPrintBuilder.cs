using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRGammaController.ViewModels;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Builds the printer-friendly version of the calibration report: a light FlowDocument
    /// (white page, near-black text, #0078D4 accents) assembled from the same view-model
    /// strings the on-screen report binds to. FlowDocument's paginator handles page breaks,
    /// so the window's export handler only has to size the page and hand it to PrintDialog.
    /// Nothing of the dark on-screen theme is printed: the charts are re-rendered offscreen
    /// with the light chart palette rather than captured from the window.
    /// </summary>
    public static class ReportPrintBuilder
    {
        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // Print palette: ink on white with one accent, rules in light gray.
        private static readonly Brush Ink = Frozen(0x1A, 0x1A, 0x1A);
        private static readonly Brush Accent = Frozen(0xE2, 0x2B, 0x1F);
        private static readonly Brush Muted = Frozen(0x55, 0x55, 0x55);
        private static readonly Brush Rule = Frozen(0xDD, 0xDD, 0xDD);

        // Print figures are drawn at this fixed size - larger than the on-screen canvases,
        // with the light palette's roomier pads, so tick labels and titles never clip.
        public const double PrintChartWidth = 560;
        public const double PrintChartHeight = 340;

        /// <summary>One chart figure: its caption and the rendered bitmap.</summary>
        public readonly record struct ChartFigure(string Title, ImageSource Image);

        /// <summary>
        /// Everything the printed Detailed Verification section needs: the two re-rendered
        /// light-palette figures (histogram + per-patch), the worst and best patches and the
        /// category breakdown line. Null when no detailed sweep data exists.
        /// </summary>
        public sealed record DetailedPrintSection(
            IReadOnlyList<ChartFigure> Charts,
            IReadOnlyList<HDRGammaController.Core.Calibration.PatchDeltaE> WorstPatches,
            IReadOnlyList<HDRGammaController.Core.Calibration.PatchDeltaE> BestPatches,
            string CategoryBreakdownText);

        /// <summary>
        /// Creates a fresh offscreen canvas at the print figure size, already measured and
        /// arranged so the chart code sees a real ActualWidth/ActualHeight. Draw into it,
        /// then hand it to <see cref="RenderPrintCanvas"/>.
        /// </summary>
        public static System.Windows.Controls.Canvas CreatePrintCanvas()
        {
            var canvas = new System.Windows.Controls.Canvas
            {
                Width = PrintChartWidth,
                Height = PrintChartHeight,
            };
            LayoutOffscreen(canvas);
            return canvas;
        }

        /// <summary>Renders an offscreen print canvas to a frozen bitmap at 2x DPI.</summary>
        public static ImageSource RenderPrintCanvas(System.Windows.Controls.Canvas canvas)
        {
            // Re-layout after drawing: the children were added outside a live visual tree.
            LayoutOffscreen(canvas);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(canvas.Width * 2), (int)Math.Ceiling(canvas.Height * 2),
                192, 192, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            bitmap.Freeze();
            return bitmap;
        }

        private static void LayoutOffscreen(FrameworkElement element)
        {
            element.Measure(new Size(element.Width, element.Height));
            element.Arrange(new Rect(0, 0, element.Width, element.Height));
        }

        /// <summary>
        /// Builds the print document from the view-model's already-formatted strings.
        /// <paramref name="charts"/> is null for historical reports (or when the charts never
        /// rendered), in which case a short note replaces the figures.
        /// </summary>
        public static FlowDocument Build(
            CalibrationReportViewModel vm,
            bool isHistorical,
            IReadOnlyList<ChartFigure>? charts,
            DetailedPrintSection? detailed = null)
        {
            var doc = new FlowDocument
            {
                Background = Brushes.White,
                Foreground = Ink,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11.5,
                PagePadding = new Thickness(48),
            };

            AddHeader(doc, vm);
            AddAccuracySection(doc, vm);
            AddCharacteristicsSection(doc, vm);
            AddPrimariesSection(doc, vm);
            AddChartsSection(doc, charts, isHistorical);
            if (detailed != null)
                AddDetailedSection(doc, detailed);
            AddDetailsSection(doc, vm);
            AddRecommendationsSection(doc, vm);
            AddFooter(doc);

            return doc;
        }

        // ------------------------------------------------------------------ sections

        private static void AddHeader(FlowDocument doc, CalibrationReportViewModel vm)
        {
            var table = BareTable(new[] { 3.0, 1.0 });
            var row = new TableRow();

            var titleCell = new TableCell { Padding = new Thickness(0) };
            titleCell.Blocks.Add(new Paragraph(new Run("Display Calibration Report"))
            {
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = Accent,
                Margin = new Thickness(0),
            });
            titleCell.Blocks.Add(new Paragraph(new Run(vm.MonitorNameText))
            {
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            titleCell.Blocks.Add(new Paragraph(new Run(vm.CalibrationDateText))
            {
                FontSize = 11,
                Foreground = Muted,
                Margin = new Thickness(0, 2, 0, 0),
            });
            row.Cells.Add(titleCell);

            var gradeCell = new TableCell { Padding = new Thickness(0), TextAlignment = TextAlignment.Right };
            gradeCell.Blocks.Add(new Paragraph(new Run(vm.GradeText))
            {
                FontSize = 42,
                FontWeight = FontWeights.Bold,
                Foreground = GradePrintBrush(vm.GradeText),
                Margin = new Thickness(0),
            });
            gradeCell.Blocks.Add(new Paragraph(new Run(vm.GradeScopeText))
            {
                FontSize = 9.5,
                Foreground = Muted,
                Margin = new Thickness(0),
            });
            row.Cells.Add(gradeCell);

            table.RowGroups[0].Rows.Add(row);
            doc.Blocks.Add(table);

            // Accent rule under the header. No content and no FontSize hack: PTS computes
            // line metrics for every paragraph, and a degenerate FontSize is exactly the
            // kind of edge case its native pagination chokes on. A default-size empty
            // paragraph with only a bottom border prints as a clean rule.
            doc.Blocks.Add(new Paragraph
            {
                BorderBrush = Accent,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Margin = new Thickness(0, 0, 0, 14),
            });
        }

        /// <summary>Print-safe color for the big grade letter, keyed off its first character.</summary>
        private static Brush GradePrintBrush(string grade) => (grade.Length > 0 ? grade[0] : '?') switch
        {
            'A' => CalibrationReportViewModel.PrintGoodBrush,
            'B' => Accent,
            'C' or 'D' => CalibrationReportViewModel.PrintMediumBrush,
            _ => CalibrationReportViewModel.PrintBadBrush,
        };

        private static void AddAccuracySection(FlowDocument doc, CalibrationReportViewModel vm)
        {
            doc.Blocks.Add(SectionHeading("Accuracy (Delta E 2000)"));

            var table = BareTable(new[] { 2.0, 1.0, 1.0 });
            var group = table.RowGroups[0];
            group.Rows.Add(HeaderRow("Metric", "Native", "After correction"));
            group.Rows.Add(MetricRow("Average dE", vm.AvgDeltaEText, vm.AfterAvgText));
            group.Rows.Add(MetricRow("Maximum dE", vm.MaxDeltaEText, vm.AfterMaxText));
            group.Rows.Add(MetricRow("Grayscale dE", vm.GrayscaleDeltaEText, vm.AfterGrayscaleText));
            group.Rows.Add(MetricRow("Primary dE", vm.PrimaryDeltaEText, vm.AfterPrimaryText));
            doc.Blocks.Add(table);

            doc.Blocks.Add(new Paragraph(new Run(vm.SummaryText))
            {
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        private static TableRow MetricRow(string label, string native, string after)
        {
            var row = new TableRow();
            row.Cells.Add(Cell(label, Muted, size: 10.5));
            row.Cells.Add(DeltaECell(native));
            row.Cells.Add(DeltaECell(after));
            return row;
        }

        /// <summary>
        /// A ΔE value cell, color coded with the print-safe palette using the same thresholds
        /// as the on-screen table. Non-numeric values ("-") print in plain ink.
        /// </summary>
        private static TableCell DeltaECell(string text)
        {
            Brush brush = Ink;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
                brush = CalibrationReportViewModel.DeltaEPrintBrush(value);
            return Cell(text, brush, bold: true, align: TextAlignment.Center, size: 13);
        }

        private static void AddCharacteristicsSection(FlowDocument doc, CalibrationReportViewModel vm)
        {
            doc.Blocks.Add(SectionHeading("Display Characteristics"));

            var pairs = new (string Label, string Value)[]
            {
                ("Peak Luminance", vm.PeakLuminanceText),
                ("Black Level", vm.BlackLevelText),
                ("Contrast Ratio", vm.ContrastRatioText),
                ("Measured Gamma", vm.MeasuredGammaText),
                ("White Point CCT", vm.WhitePointCctText),
                ("White Point Duv", vm.WhitePointDuvText),
                ("sRGB Coverage", vm.SrgbCoverageText),
                ("Calibration Target", vm.TargetText),
            };

            // Two label/value pairs per row keeps this section compact.
            var table = BareTable(new[] { 1.2, 1.0, 1.2, 1.0 });
            var group = table.RowGroups[0];
            for (int i = 0; i < pairs.Length; i += 2)
            {
                var row = new TableRow();
                row.Cells.Add(Cell(pairs[i].Label, Muted, size: 10.5));
                row.Cells.Add(Cell(pairs[i].Value, Ink, bold: true));
                row.Cells.Add(Cell(pairs[i + 1].Label, Muted, size: 10.5));
                row.Cells.Add(Cell(pairs[i + 1].Value, Ink, bold: true));
                group.Rows.Add(row);
            }
            doc.Blocks.Add(table);
        }

        private static void AddPrimariesSection(FlowDocument doc, CalibrationReportViewModel vm)
        {
            doc.Blocks.Add(SectionHeading("Color Primaries"));

            var table = BareTable(new[] { 1.0, 1.4, 1.4, 1.0 });
            var group = table.RowGroups[0];
            group.Rows.Add(HeaderRow("Channel", "Measured (x, y)", "Target (x, y)", "Error"));

            void Primary(string name, string measured, string target, string error)
            {
                var row = new TableRow();
                row.Cells.Add(Cell(name, Ink, bold: true));
                row.Cells.Add(Cell(measured, Ink, align: TextAlignment.Center));
                row.Cells.Add(Cell(target, Ink, align: TextAlignment.Center));
                row.Cells.Add(Cell(error, Ink, align: TextAlignment.Center));
                group.Rows.Add(row);
            }

            Primary("Red", vm.RedMeasuredText, vm.RedTargetText, vm.RedErrorText);
            Primary("Green", vm.GreenMeasuredText, vm.GreenTargetText, vm.GreenErrorText);
            Primary("Blue", vm.BlueMeasuredText, vm.BlueTargetText, vm.BlueErrorText);
            Primary("White", vm.WhiteMeasuredText, vm.WhiteTargetText, vm.WhiteErrorText);
            doc.Blocks.Add(table);
        }

        private static void AddChartsSection(
            FlowDocument doc, IReadOnlyList<ChartFigure>? charts, bool isHistorical)
        {
            // No KeepWithNext: a figure table may follow (see SectionHeading remarks).
            doc.Blocks.Add(SectionHeading("Measurement Charts", keepWithNext: false));

            if (charts == null || charts.Count == 0)
            {
                doc.Blocks.Add(new Paragraph(new Run("Charts are only generated at calibration time."))
                {
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Foreground = Muted,
                    Margin = new Thickness(0, 0, 0, 4),
                });
                return;
            }

            doc.Blocks.Add(FigureGrid(charts));
        }

        /// <summary>
        /// Two-up figure grid. Each cell carries its caption above the captured chart so a
        /// page break between rows never separates a figure from its title. An odd figure
        /// count gets a filler cell with an empty Paragraph - PTS dislikes cells with zero
        /// blocks.
        /// </summary>
        private static Table FigureGrid(IReadOnlyList<ChartFigure> charts)
        {
            var table = BareTable(new[] { 1.0, 1.0 });
            var group = table.RowGroups[0];
            for (int i = 0; i < charts.Count; i += 2)
            {
                var row = new TableRow();
                row.Cells.Add(FigureCell(charts[i]));
                row.Cells.Add(i + 1 < charts.Count
                    ? FigureCell(charts[i + 1])
                    : new TableCell(new Paragraph()));
                group.Rows.Add(row);
            }
            return table;
        }

        private static TableCell FigureCell(ChartFigure figure)
        {
            var cell = new TableCell { Padding = new Thickness(0, 0, 8, 10) };
            cell.Blocks.Add(new Paragraph(new Run(figure.Title))
            {
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Muted,
                Margin = new Thickness(0, 0, 0, 3),
            });
            // Explicit size: auto-shrink (DownOnly) inside star-sized table cells makes
            // the PTS pagination engine throw non-CLS exceptions during printing
            // (surfaces as RuntimeWrappedException). Half a letter-width column always
            // fits two-up; Uniform keeps the 560x340 aspect.
            var image = new System.Windows.Controls.Image
            {
                Source = figure.Image,
                Stretch = Stretch.Uniform,
                Width = 300,
                Height = 300 * 340.0 / PrintChartWidth,
            };
            cell.Blocks.Add(new BlockUIContainer(image) { Margin = new Thickness(0) });
            return cell;
        }

        private static void AddDetailedSection(FlowDocument doc, DetailedPrintSection detailed)
        {
            // No KeepWithNext on this heading: figure rows (BlockUIContainers) follow, and
            // keep chains around images are a known PTS pagination hazard.
            doc.Blocks.Add(SectionHeading("Detailed Verification", keepWithNext: false));

            // Two-up figure grid, same layout as the main charts section.
            if (detailed.Charts.Count > 0)
                doc.Blocks.Add(FigureGrid(detailed.Charts));

            doc.Blocks.Add(new Paragraph(new Run(detailed.CategoryBreakdownText))
            {
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 8),
            });

            if (detailed.WorstPatches.Count > 0)
                AddPatchTable(doc, "Worst patches", detailed.WorstPatches);
            if (detailed.BestPatches.Count > 0)
                AddPatchTable(doc, "Best patches", detailed.BestPatches);
        }

        /// <summary>One ranked patch table (worst or best) with its small heading.</summary>
        private static void AddPatchTable(
            FlowDocument doc, string title,
            IReadOnlyList<HDRGammaController.Core.Calibration.PatchDeltaE> patches)
        {
            doc.Blocks.Add(new Paragraph(new Run(title))
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Muted,
                Margin = new Thickness(0, 6, 0, 2),
                KeepWithNext = true,
            });

            var table = BareTable(new[] { 0.4, 2.6, 1.0 });
            var group = table.RowGroups[0];
            var header = HeaderRow("#", "Patch", "Delta E");
            header.Cells[1].TextAlignment = TextAlignment.Left; // patch names print left-aligned
            group.Rows.Add(header);
            int rank = 1;
            foreach (var patch in patches)
            {
                var row = new TableRow();
                row.Cells.Add(Cell($"{rank++}", Muted, size: 10.5));
                row.Cells.Add(Cell(patch.Name, Ink));
                row.Cells.Add(Cell($"{patch.DeltaE:F2}",
                    CalibrationReportViewModel.DeltaEPrintBrush(patch.DeltaE),
                    bold: true, align: TextAlignment.Center));
                group.Rows.Add(row);
            }
            doc.Blocks.Add(table);
        }

        private static void AddDetailsSection(FlowDocument doc, CalibrationReportViewModel vm)
        {
            doc.Blocks.Add(SectionHeading("Calibration Details"));

            var table = BareTable(new[] { 1.0, 2.6 });
            var group = table.RowGroups[0];

            void Detail(string label, string value)
            {
                var row = new TableRow();
                row.Cells.Add(Cell(label, Muted, size: 10.5));
                row.Cells.Add(Cell(value, Ink));
                group.Rows.Add(row);
            }

            Detail("Patches measured", vm.PatchCountText);
            Detail("Colorimeter", vm.ColorimeterText);
            Detail("Correction LUT", vm.LutSizeText);
            Detail("Target", vm.TargetText);
            Detail("Profile path", vm.ProfilePathText);
            doc.Blocks.Add(table);
        }

        private static void AddRecommendationsSection(FlowDocument doc, CalibrationReportViewModel vm)
        {
            if (vm.Recommendations.Count == 0) return;

            doc.Blocks.Add(SectionHeading("Recommendations"));

            var list = new List
            {
                MarkerStyle = TextMarkerStyle.Disc,
                Margin = new Thickness(12, 0, 0, 0),
                Padding = new Thickness(8, 0, 0, 0),
            };
            foreach (string recommendation in vm.Recommendations)
            {
                list.ListItems.Add(new ListItem(new Paragraph(new Run(recommendation))
                {
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4),
                }));
            }
            doc.Blocks.Add(list);
        }

        private static void AddFooter(FlowDocument doc)
        {
            doc.Blocks.Add(new Paragraph(
                new Run($"Generated by Gloam - {DateTime.Now.ToLocalTime():g}"))
            {
                FontSize = 9.5,
                Foreground = Muted,
                BorderBrush = Rule,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 6, 0, 0),
                Margin = new Thickness(0, 18, 0, 0),
            });
        }

        // ------------------------------------------------------------------ primitives

        // keepWithNext defaults on so a heading never strands at a page bottom, but MUST be
        // off for headings followed by figure tables: keep chains around BlockUIContainers
        // (images) are a known PTS pagination hazard.
        private static Paragraph SectionHeading(string text, bool keepWithNext = true) => new(new Run(text))
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Accent,
            BorderBrush = Rule,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 3),
            Margin = new Thickness(0, 16, 0, 8),
            KeepWithNext = keepWithNext,
        };

        private static Table BareTable(IReadOnlyList<double> starWidths)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };
            foreach (double width in starWidths)
                table.Columns.Add(new TableColumn { Width = new GridLength(width, GridUnitType.Star) });
            table.RowGroups.Add(new TableRowGroup());
            return table;
        }

        private static TableRow HeaderRow(params string[] headers)
        {
            var row = new TableRow();
            foreach (string header in headers)
                row.Cells.Add(Cell(header, Muted, bold: true, size: 10));
            // Column headers after the first read centered like their values.
            for (int i = 1; i < row.Cells.Count; i++)
                row.Cells[i].TextAlignment = TextAlignment.Center;
            return row;
        }

        private static TableCell Cell(
            string text, Brush foreground, bool bold = false,
            TextAlignment align = TextAlignment.Left, double size = 11)
        {
            return new TableCell(new Paragraph(new Run(text))
            {
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = foreground,
                Margin = new Thickness(0),
            })
            {
                Padding = new Thickness(4, 4, 8, 4),
                TextAlignment = align,
                BorderBrush = Rule,
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
        }
    }
}
