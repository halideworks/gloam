using System;
using HDRGammaController.Core;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Shared reachability guard for MHC2 gamut correction. It answers whether a target's
    /// primaries can be driven by a display without asking any display channel for far more
    /// than full-scale output.
    /// </summary>
    public static class GamutReachability
    {
        /// <summary>
        /// Maximum primary drive tolerated for a reachable target. A little overshoot covers
        /// EDID/measurement noise and near-P3 panels; large overshoot means the target gamut is
        /// physically wider than the display and the MHC2 matrix would clip/cast.
        /// </summary>
        public const double MaxReachablePrimaryDrive = 1.3;
        public static bool IsReachable(double maxPrimaryDrive)
            => double.IsFinite(maxPrimaryDrive) &&
               maxPrimaryDrive >= 0.0 &&
               maxPrimaryDrive <= MaxReachablePrimaryDrive;

        /// <summary>
        /// Largest display drive value the matrix demands for target primaries only. White is
        /// excluded because absolute white-point/luminance differences are handled by uniform
        /// matrix scaling, not by rejecting an otherwise reachable gamut.
        /// </summary>
        public static double MaxPrimaryDrive(double[,] contentToDisplayMatrix)
        {
            if (contentToDisplayMatrix.GetLength(0) < 3 || contentToDisplayMatrix.GetLength(1) < 3)
                throw new ArgumentException("Matrix must be at least 3x3.", nameof(contentToDisplayMatrix));

            double max = 0;
            (double, double, double)[] primaries = { (1, 0, 0), (0, 1, 0), (0, 0, 1) };
            foreach (var (a, b, c) in primaries)
                for (int r = 0; r < 3; r++)
                {
                    double drive = contentToDisplayMatrix[r, 0] * a +
                                   contentToDisplayMatrix[r, 1] * b +
                                   contentToDisplayMatrix[r, 2] * c;
                    if (!double.IsFinite(drive))
                        return double.PositiveInfinity;
                    max = Math.Max(max, Math.Max(0.0, drive));
                }
            return max;
        }

        public static bool TargetFitsEdidGamut(CalibrationTarget target, EdidColorInfo edid)
        {
            try
            {
                return IsReachable(MaxPrimaryDrive(BuildEdidTargetMatrix(target, edid)));
            }
            catch
            {
                return true; // If EDID is unusable, don't block setup; install-time guard remains authoritative.
            }
        }

        internal static double[,] BuildEdidTargetMatrix(CalibrationTarget target, EdidColorInfo edid)
        {
            var displayRgbToXyz = ColorMath.CalculateRgbToXyzMatrix(
                new Chromaticity(edid.RedX, edid.RedY),
                new Chromaticity(edid.GreenX, edid.GreenY),
                new Chromaticity(edid.BlueX, edid.BlueY),
                new Chromaticity(edid.WhiteX, edid.WhiteY));

            return ColorMath.MultiplyMatrices(
                ColorMath.Invert3x3(displayRgbToXyz),
                target.RgbToXyzMatrix);
        }
    }
}
