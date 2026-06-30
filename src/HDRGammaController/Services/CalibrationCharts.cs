using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Lightweight, dependency-free chart rendering onto a WPF <see cref="Canvas"/> for the
    /// calibration report: line charts (tone response, gamma tracking) and a CIE xy gamut
    /// diagram. Renders in device-independent pixels using the canvas's actual size, so call
    /// after layout (Loaded / SizeChanged).
    /// </summary>
    public static class CalibrationCharts
    {
        public sealed record Series(
            string Name, Color Color, IReadOnlyList<(double X, double Y)> Points,
            bool Dashed = false, bool Scatter = false);

        /// <summary>One set of pads around the plot area, in device-independent pixels.</summary>
        public readonly record struct Pads(double Left, double Right, double Top, double Bottom);

        /// <summary>
        /// Everything theme-dependent about a chart: colors for the chrome (background, grid,
        /// axes, labels) and for the series the report draws, plus layout (pads, axis titles,
        /// font size). <see cref="Dark"/> reproduces the on-screen report exactly;
        /// <see cref="Light"/> is the print/PDF rendition: white background, darkened series
        /// hues, roomier pads so tick labels never clip, and rotated y-axis titles.
        /// </summary>
        public sealed record ChartPalette
        {
            /// <summary>Fill behind the whole chart; null leaves the canvas transparent
            /// (on screen the dark backdrop comes from the parent Border).</summary>
            public Brush? Background { get; init; }
            public required Brush Axis { get; init; }
            public required Brush Grid { get; init; }
            public required Brush Label { get; init; }

            // Series colors (the report picks from these so print can darken the hues).
            public required Color Neutral { get; init; }      // dashed target / neutral lines
            public required Color Cyan { get; init; }         // fitted panel curve
            public required Color Green { get; init; }        // corrected / measured gamut
            public required Color Orange { get; init; }       // measured points / measured white
            public required Color Amber { get; init; }        // marginal ΔE (3-5) in the detailed charts
            public required Color Red { get; init; }          // poor ΔE (5+) in the detailed charts
            public required Color BalanceRed { get; init; }
            public required Color BalanceGreen { get; init; }
            public required Color BalanceBlue { get; init; }
            public required Color GamutTargetLine { get; init; }
            public required Color GamutTargetDot { get; init; }

            // Layout
            public required Pads LinePads { get; init; }
            public required Pads GamutPads { get; init; }
            /// <summary>Draw rotated y-axis titles (print only; the on-screen cards already
            /// caption each chart).</summary>
            public bool DrawAxisTitles { get; init; }
            public double FontSize { get; init; } = 10;

            private static SolidColorBrush Frozen(byte r, byte g, byte b)
            {
                var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }

            /// <summary>The on-screen report theme. These are the historical hardcoded values;
            /// the window render must stay pixel-identical to before parameterization.</summary>
            public static ChartPalette Dark { get; } = new()
            {
                Background = null,
                Axis = Frozen(0x60, 0x60, 0x60),
                Grid = Frozen(0x2c, 0x2c, 0x2c),
                Label = Frozen(0x90, 0x90, 0x90),
                Neutral = Color.FromRgb(0x99, 0x99, 0x99),
                Cyan = Color.FromRgb(0x22, 0xd3, 0xee),
                Green = Color.FromRgb(0x22, 0xc5, 0x5e),
                Orange = Color.FromRgb(0xf9, 0x73, 0x16),
                Amber = Color.FromRgb(0xf5, 0x9e, 0x0b),
                Red = Color.FromRgb(0xef, 0x44, 0x44),
                BalanceRed = Color.FromRgb(0xff, 0x5a, 0x5a),
                BalanceGreen = Color.FromRgb(0x55, 0xdd, 0x77),
                BalanceBlue = Color.FromRgb(0x5a, 0x9c, 0xff),
                GamutTargetLine = Color.FromRgb(0xaa, 0xaa, 0xaa),
                GamutTargetDot = Color.FromRgb(0xbb, 0xbb, 0xbb),
                LinePads = new Pads(40, 12, 10, 26),
                GamutPads = new Pads(34, 10, 8, 22),
                DrawAxisTitles = false,
                FontSize = 10,
            };

            /// <summary>Print theme: ink on white paper. Series hues stay semantic but are
            /// darkened so they hold up on white; pads grow so the top tick label and the
            /// y-axis title have room.</summary>
            public static ChartPalette Light { get; } = new()
            {
                Background = Brushes.White,
                Axis = Frozen(0x44, 0x44, 0x44),
                Grid = Frozen(0x66, 0x66, 0x66),
                Label = Frozen(0x33, 0x33, 0x33),
                Neutral = Color.FromRgb(0x55, 0x55, 0x55),
                Cyan = Color.FromRgb(0x0E, 0x74, 0x90),
                Green = Color.FromRgb(0x15, 0x80, 0x3D),
                Orange = Color.FromRgb(0xEA, 0x58, 0x0C),
                Amber = Color.FromRgb(0x9A, 0x67, 0x00),
                Red = Color.FromRgb(0xC4, 0x2B, 0x1C),
                BalanceRed = Color.FromRgb(0xDC, 0x26, 0x26),
                BalanceGreen = Color.FromRgb(0x15, 0x80, 0x3D),
                BalanceBlue = Color.FromRgb(0x25, 0x63, 0xEB),
                GamutTargetLine = Color.FromRgb(0x55, 0x55, 0x55),
                GamutTargetDot = Color.FromRgb(0x55, 0x55, 0x55),
                LinePads = new Pads(58, 16, 20, 30),
                GamutPads = new Pads(40, 16, 16, 28),
                DrawAxisTitles = true,
                FontSize = 11,
            };
        }

        /// <summary>Draws a multi-series line chart with axes, gridlines, and a legend.</summary>
        public static void DrawLineChart(
            Canvas canvas, IReadOnlyList<Series> series,
            double xMin, double xMax, double yMin, double yMax,
            string xLabel, string yLabel, int gridLines = 4, ChartPalette? palette = null)
        {
            var p = palette ?? ChartPalette.Dark;
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 40) return;
            if (!IsFiniteRange(xMin, xMax) || !IsFiniteRange(yMin, yMax) || gridLines <= 0) return;

            double padL = p.LinePads.Left, padR = p.LinePads.Right, padT = p.LinePads.Top, padB = p.LinePads.Bottom;
            double plotW = w - padL - padR, plotH = h - padT - padB;
            double X(double x) => padL + (x - xMin) / (xMax - xMin) * plotW;
            double Y(double y) => padT + (1 - (y - yMin) / (yMax - yMin)) * plotH;

            if (p.Background is { } bg)
                canvas.Children.Add(new Rectangle { Width = w, Height = h, Fill = bg });

            // Gridlines + y tick labels (right-aligned, ending just left of the y axis)
            for (int i = 0; i <= gridLines; i++)
            {
                double gy = yMin + (yMax - yMin) * i / gridLines;
                double py = Y(gy);
                canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = py, Y2 = py, Stroke = p.Grid, StrokeThickness = 1 });
                canvas.Children.Add(Label(p, $"{gy:0.##}", padL - 38, py - 8, 34, TextAlignment.Right));
            }

            // Axes
            canvas.Children.Add(new Line { X1 = padL, X2 = padL, Y1 = padT, Y2 = padT + plotH, Stroke = p.Axis, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = padT + plotH, Y2 = padT + plotH, Stroke = p.Axis, StrokeThickness = 1.5 });
            // 10px below the x axis (== h - 16 with the dark pads, the historical position).
            canvas.Children.Add(Label(p, xLabel, padL, padT + plotH + 10, plotW, TextAlignment.Center));
            if (p.DrawAxisTitles && !string.IsNullOrEmpty(yLabel))
                canvas.Children.Add(YAxisTitle(p, yLabel, padT, plotH));

            // Series
            foreach (var s in series)
            {
                var points = s.Points.Where(pt => IsFinitePoint(pt.X, pt.Y)).ToList();
                if (s.Scatter)
                {
                    var fill = new SolidColorBrush(s.Color);
                    foreach (var (x, y) in points)
                    {
                        var dot = new Ellipse { Width = 6, Height = 6, Fill = fill, Opacity = 0.9 };
                        Canvas.SetLeft(dot, X(x) - 3);
                        Canvas.SetTop(dot, Y(Math.Clamp(y, yMin, yMax)) - 3);
                        canvas.Children.Add(dot);
                    }
                    continue;
                }

                var pl = new Polyline { Stroke = new SolidColorBrush(s.Color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                if (s.Dashed) pl.StrokeDashArray = new DoubleCollection { 4, 3 };
                var pts = new PointCollection(points.Count);
                foreach (var (x, y) in points) pts.Add(new Point(X(x), Y(Math.Clamp(y, yMin, yMax))));
                pl.Points = pts;
                canvas.Children.Add(pl);
            }

            // Legend (top-left inside plot; series with empty names — e.g. scatter overlays of
            // an already-named line — are skipped)
            double ly = padT + 2;
            foreach (var s in series)
            {
                if (string.IsNullOrEmpty(s.Name)) continue;
                if (s.Scatter)
                {
                    var dot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(s.Color) };
                    Canvas.SetLeft(dot, padL + 9); Canvas.SetTop(dot, ly + 4);
                    canvas.Children.Add(dot);
                }
                else
                {
                    canvas.Children.Add(new Rectangle { Width = 12, Height = 3, Fill = new SolidColorBrush(s.Color), });
                    Canvas.SetLeft(canvas.Children[^1], padL + 6); Canvas.SetTop(canvas.Children[^1], ly + 6);
                }
                canvas.Children.Add(Label(p, s.Name, padL + 22, ly, 120, TextAlignment.Left));
                ly += 16;
            }
        }

        /// <summary>
        /// Draws a CIE 1931 xy chromaticity diagram with the sRGB/target gamut triangle and the
        /// measured gamut triangle + white points overlaid.
        /// </summary>
        public static void DrawGamutDiagram(
            Canvas canvas,
            (double x, double y) tR, (double x, double y) tG, (double x, double y) tB, (double x, double y) tW,
            (double x, double y) mR, (double x, double y) mG, (double x, double y) mB, (double x, double y) mW,
            ChartPalette? palette = null)
        {
            var p = palette ?? ChartPalette.Dark;
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 40) return;

            double padL = p.GamutPads.Left, padR = p.GamutPads.Right, padT = p.GamutPads.Top, padB = p.GamutPads.Bottom;
            double plotW = w - padL - padR, plotH = h - padT - padB;
            // CIE x in [0,0.75], y in [0,0.85]
            double X(double x) => padL + x / 0.75 * plotW;
            double Y(double y) => padT + (1 - y / 0.85) * plotH;

            if (p.Background is { } bg)
                canvas.Children.Add(new Rectangle { Width = w, Height = h, Fill = bg });

            // grid
            for (int i = 0; i <= 3; i++)
            {
                double gx = 0.75 * i / 3, gy = 0.85 * i / 3;
                canvas.Children.Add(new Line { X1 = X(gx), X2 = X(gx), Y1 = padT, Y2 = padT + plotH, Stroke = p.Grid, StrokeThickness = 1 });
                canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = Y(gy), Y2 = Y(gy), Stroke = p.Grid, StrokeThickness = 1 });
            }
            // 8px below the plot (== h - 14 with the dark pads, the historical position).
            canvas.Children.Add(Label(p, "CIE x", padL, padT + plotH + 8, plotW, TextAlignment.Center));
            if (p.DrawAxisTitles)
                canvas.Children.Add(YAxisTitle(p, "CIE y", padT, plotH));

            void Triangle((double x, double y) r, (double x, double y) g, (double x, double y) b, Color c, bool dashed)
            {
                if (!IsPhysicalChromaticity(r) || !IsPhysicalChromaticity(g) || !IsPhysicalChromaticity(b))
                    return;
                var poly = new Polygon { Stroke = new SolidColorBrush(c), StrokeThickness = 2, Fill = Brushes.Transparent };
                if (dashed) poly.StrokeDashArray = new DoubleCollection { 4, 3 };
                poly.Points = new PointCollection { new(X(r.x), Y(r.y)), new(X(g.x), Y(g.y)), new(X(b.x), Y(b.y)) };
                canvas.Children.Add(poly);
            }
            void Dot((double x, double y) p, Color c)
            {
                if (!IsPhysicalChromaticity(p)) return;
                var e = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(c), Stroke = Brushes.White, StrokeThickness = 1 };
                Canvas.SetLeft(e, X(p.x) - 3.5); Canvas.SetTop(e, Y(p.y) - 3.5);
                canvas.Children.Add(e);
            }

            // Measured first, target dashed ON TOP — when the two gamuts (nearly) coincide
            // the target triangle must stay visible, not vanish under the measured one.
            Triangle(mR, mG, mB, p.Green, dashed: false);            // measured gamut
            Triangle(tR, tG, tB, p.GamutTargetLine, dashed: true);   // target gamut
            Dot(tW, p.GamutTargetDot);                               // target white
            Dot(mW, p.Orange);                                       // measured white
        }

        /// <summary>The detailed charts' ΔE quality color, from the report's usual thresholds.</summary>
        private static Color DeltaEColor(ChartPalette p, double deltaE) => deltaE switch
        {
            < 1.0 => p.Green,
            < 2.0 => p.Cyan,
            < 3.0 => p.Orange,
            < 5.0 => p.Amber,
            _ => p.Red,
        };

        /// <summary>
        /// ΔE distribution histogram for the detailed verification sweep: one bar per bucket,
        /// colored with the report's ΔE quality scale, count printed above each bar.
        /// </summary>
        public static void DrawDeltaEHistogram(
            Canvas canvas, IReadOnlyList<int> counts, IReadOnlyList<string> bucketLabels,
            ChartPalette? palette = null)
        {
            var p = palette ?? ChartPalette.Dark;
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 40 || counts.Count == 0) return;
            var safeCounts = counts.Select(c => Math.Max(0, c)).ToList();

            double padL = p.LinePads.Left, padR = p.LinePads.Right, padT = p.LinePads.Top, padB = p.LinePads.Bottom;
            double plotW = w - padL - padR, plotH = h - padT - padB;
            int maxCount = Math.Max(safeCounts.Max(), 1);

            if (p.Background is { } bg)
                canvas.Children.Add(new Rectangle { Width = w, Height = h, Fill = bg });

            // Integer y gridlines + labels (at most 4 lines above the axis).
            int gridLines = Math.Min(4, maxCount);
            for (int i = 0; i <= gridLines; i++)
            {
                double gy = maxCount * i / (double)gridLines;
                double py = padT + (1 - gy / maxCount) * plotH;
                canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = py, Y2 = py, Stroke = p.Grid, StrokeThickness = 1 });
                canvas.Children.Add(Label(p, $"{gy:0}", padL - 38, py - 8, 34, TextAlignment.Right));
            }

            // Axes
            canvas.Children.Add(new Line { X1 = padL, X2 = padL, Y1 = padT, Y2 = padT + plotH, Stroke = p.Axis, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = padT + plotH, Y2 = padT + plotH, Stroke = p.Axis, StrokeThickness = 1.5 });
            if (p.DrawAxisTitles)
                canvas.Children.Add(YAxisTitle(p, "Patches", padT, plotH));

            // Representative ΔE per bucket for the quality color (midpoint-ish values).
            double[] bucketColorKeys = { 0.25, 0.75, 1.5, 2.5, 4.0, 6.0 };
            double slot = plotW / safeCounts.Count;
            for (int i = 0; i < safeCounts.Count; i++)
            {
                int count = safeCounts[i];
                double barW = slot * 0.66;
                double x = padL + slot * i + (slot - barW) / 2;
                double barH = count / (double)maxCount * plotH;
                double key = i < bucketColorKeys.Length ? bucketColorKeys[i] : 6.0;
                var fill = new SolidColorBrush(DeltaEColor(p, key));
                var bar = new Rectangle { Width = barW, Height = Math.Max(barH, count > 0 ? 2 : 0), Fill = fill, Opacity = 0.9 };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, padT + plotH - bar.Height);
                canvas.Children.Add(bar);

                // Count above the bar, bucket range below the axis.
                canvas.Children.Add(Label(p, count.ToString(),
                    padL + slot * i, Math.Max(padT + plotH - bar.Height - 16, padT), slot, TextAlignment.Center));
                // Bucket ΔE range under the axis (these double as the x-axis caption).
                if (i < bucketLabels.Count)
                    canvas.Children.Add(Label(p, bucketLabels[i], padL + slot * i, padT + plotH + 6, slot, TextAlignment.Center));
            }
        }

        /// <summary>
        /// Per-patch ΔE strip chart in measurement order: one bar per patch, colored with the
        /// report's ΔE quality scale, with dashed threshold lines at 2.0 and 5.0. On screen
        /// each bar carries a tooltip naming the patch.
        /// </summary>
        public static void DrawPerPatchDeltaE(
            Canvas canvas, IReadOnlyList<(string Name, double DeltaE)> patches,
            ChartPalette? palette = null)
        {
            var p = palette ?? ChartPalette.Dark;
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 40 || patches.Count == 0) return;
            var drawablePatches = patches
                .Where(patch => double.IsFinite(patch.DeltaE) && patch.DeltaE >= 0)
                .ToList();
            if (drawablePatches.Count == 0) return;

            double padL = p.LinePads.Left, padR = p.LinePads.Right, padT = p.LinePads.Top, padB = p.LinePads.Bottom;
            double plotW = w - padL - padR, plotH = h - padT - padB;
            // Headroom above the worst patch, never below the 5.0 threshold line.
            double yMax = Math.Max(5.5, drawablePatches.Max(d => d.DeltaE) * 1.1);
            double Y(double de) => padT + (1 - Math.Clamp(de, 0, yMax) / yMax) * plotH;

            if (p.Background is { } bg)
                canvas.Children.Add(new Rectangle { Width = w, Height = h, Fill = bg });

            // Gridlines + y tick labels.
            const int gridLines = 4;
            for (int i = 0; i <= gridLines; i++)
            {
                double gy = yMax * i / gridLines;
                double py = Y(gy);
                canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = py, Y2 = py, Stroke = p.Grid, StrokeThickness = 1 });
                canvas.Children.Add(Label(p, $"{gy:0.#}", padL - 38, py - 8, 34, TextAlignment.Right));
            }

            // Axes
            canvas.Children.Add(new Line { X1 = padL, X2 = padL, Y1 = padT, Y2 = padT + plotH, Stroke = p.Axis, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = padT + plotH, Y2 = padT + plotH, Stroke = p.Axis, StrokeThickness = 1.5 });
            canvas.Children.Add(Label(p, "Patches (measurement order)", padL, padT + plotH + 10, plotW, TextAlignment.Center));
            if (p.DrawAxisTitles)
                canvas.Children.Add(YAxisTitle(p, "ΔE2000", padT, plotH));

            // Bars (tooltips carry the patch names; the x axis has no room for 39 labels).
            double slot = plotW / drawablePatches.Count;
            double barW = Math.Max(slot * 0.6, 1.5);
            for (int i = 0; i < drawablePatches.Count; i++)
            {
                var (name, de) = drawablePatches[i];
                var bar = new Rectangle
                {
                    Width = barW,
                    Height = Math.Max(padT + plotH - Y(de), de > 0 ? 1.5 : 0),
                    Fill = new SolidColorBrush(DeltaEColor(p, de)),
                    Opacity = 0.9,
                    ToolTip = $"{name}: ΔE {de:F2}",
                };
                Canvas.SetLeft(bar, padL + slot * i + (slot - barW) / 2);
                Canvas.SetTop(bar, padT + plotH - bar.Height);
                canvas.Children.Add(bar);
            }

            // Threshold lines at the report's "good" (2.0) and "poor" (5.0) boundaries,
            // drawn over the bars so they stay readable.
            foreach (double threshold in new[] { 2.0, 5.0 })
            {
                double py = Y(threshold);
                canvas.Children.Add(new Line
                {
                    X1 = padL, X2 = padL + plotW, Y1 = py, Y2 = py,
                    Stroke = new SolidColorBrush(threshold >= 5.0 ? p.Red : p.Amber),
                    StrokeThickness = 1.2,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Opacity = 0.8,
                });
                canvas.Children.Add(Label(p, $"{threshold:0.0}", padL + plotW - 36, py - 14, 34, TextAlignment.Right));
            }
        }

        private static bool IsFiniteRange(double min, double max)
            => double.IsFinite(min) && double.IsFinite(max) && max > min;

        private static bool IsFinitePoint(double x, double y)
            => double.IsFinite(x) && double.IsFinite(y);

        private static bool IsPhysicalChromaticity((double x, double y) point)
            => IsFinitePoint(point.x, point.y) && point.x >= 0 && point.y >= 0;

        private static TextBlock Label(ChartPalette p, string text, double left, double top, double width, TextAlignment align)
        {
            var tb = new TextBlock { Text = text, Foreground = p.Label, FontSize = p.FontSize, Width = width, TextAlignment = align };
            Canvas.SetLeft(tb, left); Canvas.SetTop(tb, top);
            return tb;
        }

        /// <summary>Rotated y-axis title hugging the left edge, centered along the plot height.</summary>
        private static TextBlock YAxisTitle(ChartPalette p, string text, double padT, double plotH)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = p.Label,
                FontSize = p.FontSize,
                Width = plotH,
                TextAlignment = TextAlignment.Center,
                LayoutTransform = new RotateTransform(-90),
            };
            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, padT);
            return tb;
        }
    }
}
