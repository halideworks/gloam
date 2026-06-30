using Xunit;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Unit tests for ColorMath.GamutCoverage (Sutherland-Hodgman triangle clipping
    /// in CIE xy, used for the report's sRGB coverage figure).
    /// </summary>
    public class GamutCoverageTests
    {
        private static readonly Chromaticity SrgbR = new(0.64, 0.33);
        private static readonly Chromaticity SrgbG = new(0.30, 0.60);
        private static readonly Chromaticity SrgbB = new(0.15, 0.06);

        // DCI-P3 (D65) primaries: a strict superset of sRGB.
        private static readonly Chromaticity P3R = new(0.680, 0.320);
        private static readonly Chromaticity P3G = new(0.265, 0.690);
        private static readonly Chromaticity P3B = new(0.150, 0.060);

        [Fact]
        public void GamutCoverage_IdenticalToSrgb_Returns100Percent()
        {
            double coverage = ColorMath.GamutCoverage(SrgbR, SrgbG, SrgbB);
            Assert.Equal(1.0, coverage, 9);
        }

        [Fact]
        public void GamutCoverage_P3CoversSrgb_Returns100Percent()
        {
            double coverage = ColorMath.GamutCoverage(P3R, P3G, P3B);
            Assert.Equal(1.0, coverage, 9);
        }

        [Fact]
        public void GamutCoverage_DisjointTriangle_ReturnsZero()
        {
            // A small triangle far outside the sRGB gamut region.
            var r = new Chromaticity(0.70, 0.05);
            var g = new Chromaticity(0.73, 0.05);
            var b = new Chromaticity(0.715, 0.02);

            double coverage = ColorMath.GamutCoverage(r, g, b);
            Assert.Equal(0.0, coverage, 9);
        }

        [Fact]
        public void GamutCoverage_MidpointTriangle_ReturnsQuarter()
        {
            // The medial triangle (edge midpoints) lies fully inside sRGB
            // and has exactly one quarter of its area.
            var r = new Chromaticity((SrgbR.X + SrgbG.X) / 2, (SrgbR.Y + SrgbG.Y) / 2);
            var g = new Chromaticity((SrgbG.X + SrgbB.X) / 2, (SrgbG.Y + SrgbB.Y) / 2);
            var b = new Chromaticity((SrgbB.X + SrgbR.X) / 2, (SrgbB.Y + SrgbR.Y) / 2);

            double coverage = ColorMath.GamutCoverage(r, g, b);
            Assert.Equal(0.25, coverage, 9);
        }

        [Fact]
        public void GamutCoverage_WindingOrderDoesNotMatter()
        {
            double forward = ColorMath.GamutCoverage(P3R, P3G, P3B);
            double reversed = ColorMath.GamutCoverage(P3B, P3G, P3R);
            Assert.Equal(forward, reversed, 12);
        }

        [Fact]
        public void GamutCoverage_PartialOverlap_IsBetweenZeroAndOne()
        {
            // A physically plausible triangle shifted toward red: overlaps, but no
            // containment either way.
            var r = new Chromaticity(0.72, 0.28);
            var g = new Chromaticity(0.38, 0.55);
            var b = new Chromaticity(0.23, 0.01);

            double coverage = ColorMath.GamutCoverage(r, g, b);
            Assert.InRange(coverage, 0.05, 0.95);
        }

        [Fact]
        public void GamutCoverage_DegenerateMeasuredTriangle_ReturnsZero()
        {
            // All three "primaries" identical (e.g. unset/zeroed historical data).
            var point = new Chromaticity(0.3127, 0.3290);
            double coverage = ColorMath.GamutCoverage(point, point, point);
            Assert.Equal(0.0, coverage, 9);
        }

        [Fact]
        public void GamutCoverage_SrgbInsideP3Reference_ReturnsSrgbToP3Ratio()
        {
            // sRGB measured against a P3 reference: coverage is area(sRGB)/area(P3)
            // since sRGB sits fully inside P3. Shoelace: sRGB = 0.11205, P3 = 0.1520.
            double coverage = ColorMath.GamutCoverage(SrgbR, SrgbG, SrgbB, P3R, P3G, P3B);
            Assert.Equal(0.11205 / 0.1520, coverage, 6);
        }
    }
}
