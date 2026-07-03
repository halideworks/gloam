using System;
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

        // --- Gamut reachability guard (negative-drive rejection) ---
        // The content→display matrix maps a target primary to display-channel drives. A target
        // primary OUTSIDE the display gamut forces a negative drive (light the panel cannot
        // subtract); the guard must reject it, not silently clip it as MaxPrimaryDrive's old
        // Math.Max(0, drive) did.

        private static double[,] ContentToDisplay(
            Chromaticity tR, Chromaticity tG, Chromaticity tB,
            Chromaticity dR, Chromaticity dG, Chromaticity dB, Chromaticity white)
        {
            var target = ColorMath.CalculateRgbToXyzMatrix(tR, tG, tB, white);
            var display = ColorMath.CalculateRgbToXyzMatrix(dR, dG, dB, white);
            return ColorMath.MultiplyMatrices(ColorMath.Invert3x3(display), target);
        }

        [Fact]
        public void Reachability_Rec2020TargetOnSrgbDisplay_IsUnreachable()
        {
            var m = ContentToDisplay(
                Chromaticity.Rec2020Red, Chromaticity.Rec2020Green, Chromaticity.Rec2020Blue,
                SrgbR, SrgbG, SrgbB, Chromaticity.D65);

            var (min, max) = GamutReachability.PrimaryDriveExtent(m);
            // Rec.2020 is far wider than sRGB, so at least one channel drive goes strongly negative.
            Assert.True(min < GamutReachability.MinReachablePrimaryDrive);
            Assert.False(GamutReachability.IsReachable(max, min));
        }

        [Fact]
        public void Reachability_SrgbTargetOnP3Display_IsReachable()
        {
            var m = ContentToDisplay(
                SrgbR, SrgbG, SrgbB,
                P3R, P3G, P3B, Chromaticity.D65);

            var (min, max) = GamutReachability.PrimaryDriveExtent(m);
            // sRGB sits fully inside P3: no negative drive, no channel far above full scale.
            Assert.True(min >= GamutReachability.MinReachablePrimaryDrive);
            Assert.True(GamutReachability.IsReachable(max, min));
        }

        [Fact]
        public void Reachability_NegativeDriveWithinSlack_StillReachable()
        {
            // The small negative tolerance absorbs EDID/measurement noise.
            Assert.True(GamutReachability.IsReachable(1.0, -0.04));
            Assert.False(GamutReachability.IsReachable(1.0, -0.06));
        }

        [Fact]
        public void GamutCoverageUv_SrgbInsideP3_DiffersFromXyAndIsInUnitInterval()
        {
            double xy = ColorMath.GamutCoverage(SrgbR, SrgbG, SrgbB, P3R, P3G, P3B);
            double uv = ColorMath.GamutCoverageUv(SrgbR, SrgbG, SrgbB, P3R, P3G, P3B);

            // Both are valid coverage fractions in (0, 1].
            Assert.InRange(xy, 0.0 + 1e-9, 1.0);
            Assert.InRange(uv, 0.0 + 1e-9, 1.0);

            // sRGB sits inside P3 in both planes, but the perceptual (u'v') ratio is not the
            // same number as the xy ratio.
            Assert.True(Math.Abs(uv - xy) > 1e-3,
                $"u'v' coverage {uv:F4} should differ from xy coverage {xy:F4}");
        }

        [Fact]
        public void GamutCoverageUv_IdenticalPrimaries_Returns100Percent()
        {
            Assert.Equal(1.0, ColorMath.GamutCoverageUv(SrgbR, SrgbG, SrgbB), 9);
        }
    }
}
