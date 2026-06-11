using System;
using System.Collections.Generic;
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
                if (s.Scatter)
                {
                    var fill = new SolidColorBrush(s.Color);
                    foreach (var (x, y) in s.Points)
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
                var pts = new PointCollection(s.Points.Count);
                foreach (var (x, y) in s.Points) pts.Add(new Point(X(x), Y(Math.Clamp(y, yMin, yMax))));
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
                var poly = new Polygon { Stroke = new SolidColorBrush(c), StrokeThickness = 2, Fill = Brushes.Transparent };
                if (dashed) poly.StrokeDashArray = new DoubleCollection { 4, 3 };
                poly.Points = new PointCollection { new(X(r.x), Y(r.y)), new(X(g.x), Y(g.y)), new(X(b.x), Y(b.y)) };
                canvas.Children.Add(poly);
            }
            void Dot((double x, double y) p, Color c)
            {
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
