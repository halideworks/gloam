using System;
using System.Collections.Generic;
using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tests for the monotone-cubic (Fritsch–Carlson PCHIP) tone-curve fit that replaced the
    /// piecewise-linear interpolation in <see cref="ToneCurve.CreateFromPoints"/>.
    /// </summary>
    public class ToneCurvePchipTests
    {
        private static double PowerLaw(double x, double gamma) => Math.Pow(x, gamma);

        [Fact]
        public void Pchip_ReproducesPowerLaw_MuchBetterThanLinear()
        {
            const double gamma = 2.4;

            // Sparse measured samples of a power-law tone response.
            var sampleInputs = new[] { 0.0, 0.2, 0.4, 0.6, 0.8, 1.0 };
            var points = new List<(double input, double output)>();
            foreach (var x in sampleInputs)
                points.Add((x, PowerLaw(x, gamma)));

            var curve = ToneCurve.CreateFromPoints(points); // PCHIP (>= 4 points)

            double pchipMaxErr = 0.0;
            double linearMaxErr = 0.0;
            const int dense = 1000;
            for (int i = 0; i <= dense; i++)
            {
                double x = i / (double)dense;
                double truth = PowerLaw(x, gamma);
                pchipMaxErr = Math.Max(pchipMaxErr, Math.Abs(curve.Lookup(x) - truth));
                linearMaxErr = Math.Max(linearMaxErr, Math.Abs(LinearInterp(sampleInputs, points, x) - truth));
            }

            // PCHIP tracks the curved response markedly better than straight segments.
            Assert.True(pchipMaxErr < linearMaxErr,
                $"PCHIP max error {pchipMaxErr:E3} should beat linear {linearMaxErr:E3}");
            Assert.True(pchipMaxErr < 0.5 * linearMaxErr,
                $"PCHIP max error {pchipMaxErr:E3} should be well under half of linear {linearMaxErr:E3}");
        }

        [Fact]
        public void Pchip_MonotoneInput_ProducesMonotoneOutput_WithoutEnforcement()
        {
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.2, 0.02),
                (0.4, 0.10),
                (0.6, 0.30),
                (0.8, 0.62),
                (1.0, 1.0),
            };

            // enforceMonotonic:false — monotonicity must come from the PCHIP fit itself.
            var curve = ToneCurve.CreateFromPoints(points, enforceMonotonic: false);

            Assert.True(curve.IsMonotonic);

            double prev = curve.Lookup(0.0);
            for (int i = 1; i <= 500; i++)
            {
                double v = curve.Lookup(i / 500.0);
                Assert.True(v >= prev - 1e-9, $"Non-monotone at {i / 500.0}: {v} < {prev}");
                prev = v;
            }
        }

        [Fact]
        public void Pchip_EndpointsAreExact()
        {
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.3, 0.08),
                (0.7, 0.45),
                (1.0, 1.0),
            };

            var curve = ToneCurve.CreateFromPoints(points);

            Assert.Equal(0.0, curve.Lookup(0.0), 6);
            Assert.Equal(1.0, curve.Lookup(1.0), 6);
        }

        [Fact]
        public void Pchip_DoesNotOvershootBetweenSamples()
        {
            // A sharp step in the middle must not induce PCHIP overshoot beyond the local samples.
            var points = new List<(double input, double output)>
            {
                (0.0, 0.0),
                (0.25, 0.02),
                (0.5, 0.05),
                (0.55, 0.9),
                (0.75, 0.95),
                (1.0, 1.0),
            };

            var curve = ToneCurve.CreateFromPoints(points, enforceMonotonic: false);

            for (int i = 0; i <= 1000; i++)
            {
                double v = curve.Lookup(i / 1000.0);
                Assert.InRange(v, -1e-9, 1.0 + 1e-9);
            }
        }

        private static double LinearInterp(
            double[] xs, List<(double input, double output)> pts, double x)
        {
            if (x <= pts[0].input) return pts[0].output;
            if (x >= pts[^1].input) return pts[^1].output;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (x <= pts[i + 1].input)
                {
                    double t = (x - pts[i].input) / (pts[i + 1].input - pts[i].input);
                    return pts[i].output + t * (pts[i + 1].output - pts[i].output);
                }
            }
            return pts[^1].output;
        }
    }
}
