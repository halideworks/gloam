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

        private static readonly Brush AxisBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
        private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x2c, 0x2c, 0x2c));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));

        /// <summary>Draws a multi-series line chart with axes, gridlines, and a legend.</summary>
        public static void DrawLineChart(
            Canvas canvas, IReadOnlyList<Series> series,
            double xMin, double xMax, double yMin, double yMax,
            string xLabel, string yLabel, int gridLines = 4)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 40) return;

            const double padL = 40, padR = 12, padT = 10, padB = 26;
            double plotW = w - padL - padR, plotH = h - padT - padB;
            double X(double x) => padL + (x - xMin) / (xMax - xMin) * plotW;
            double Y(double y) => padT + (1 - (y - yMin) / (yMax - yMin)) * plotH;

            // Gridlines + y labels
            for (int i = 0; i <= gridLines; i++)
            {
                double gy = yMin + (yMax - yMin) * i / gridLines;
                double py = Y(gy);
                canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = py, Y2 = py, Stroke = GridBrush, StrokeThickness = 1 });
                canvas.Children.Add(Label($"{gy:0.##}", 2, py - 8, 34, TextAlignment.Right));
            }

            // Axes
            canvas.Children.Add(new Line { X1 = padL, X2 = padL, Y1 = padT, Y2 = padT + plotH, Stroke = AxisBrush, StrokeThickness = 1.5 });
            canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = padT + plotH, Y2 = padT + plotH, Stroke = AxisBrush, StrokeThickness = 1.5 });
            canvas.Children.Add(Label(xLabel, padL, h - 16, plotW, TextAlignment.Center));

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
                canvas.Children.Add(Label(s.Name, padL + 22, ly, 120, TextAlignment.Left));
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
            (double x, double y) mR, (double x, double y) mG, (double x, double y) mB, (double x, double y) mW)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w < 40 || h < 40) return;

            const double padL = 34, padR = 10, padT = 8, padB = 22;
            double plotW = w - padL - padR, plotH = h - padT - padB;
            // CIE x in [0,0.75], y in [0,0.85]
            double X(double x) => padL + x / 0.75 * plotW;
            double Y(double y) => padT + (1 - y / 0.85) * plotH;

            // grid
            for (int i = 0; i <= 3; i++)
            {
                double gx = 0.75 * i / 3, gy = 0.85 * i / 3;
                canvas.Children.Add(new Line { X1 = X(gx), X2 = X(gx), Y1 = padT, Y2 = padT + plotH, Stroke = GridBrush, StrokeThickness = 1 });
                canvas.Children.Add(new Line { X1 = padL, X2 = padL + plotW, Y1 = Y(gy), Y2 = Y(gy), Stroke = GridBrush, StrokeThickness = 1 });
            }
            canvas.Children.Add(Label("CIE x", padL, h - 14, plotW, TextAlignment.Center));

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
            Triangle(mR, mG, mB, Color.FromRgb(0x22, 0xc5, 0x5e), dashed: false);  // measured gamut
            Triangle(tR, tG, tB, Color.FromRgb(0xaa, 0xaa, 0xaa), dashed: true);   // target gamut
            Dot(tW, Color.FromRgb(0xbb, 0xbb, 0xbb));                              // target white
            Dot(mW, Color.FromRgb(0xf9, 0x73, 0x16));                             // measured white
        }

        private static TextBlock Label(string text, double left, double top, double width, TextAlignment align)
        {
            var tb = new TextBlock { Text = text, Foreground = LabelBrush, FontSize = 10, Width = width, TextAlignment = align };
            Canvas.SetLeft(tb, left); Canvas.SetTop(tb, top);
            return tb;
        }
    }
}
