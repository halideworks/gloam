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

        /// <summary>
        /// Most-negative primary drive tolerated for a reachable target. A target primary that
        /// sits OUTSIDE the display gamut forces a negative display-channel coefficient — light
        /// the panel physically cannot subtract — so the MHC2 matrix would clip it to black and
        /// desaturate/cast that hue. We allow a small negative slack (mirroring the +0.3 slack
        /// above full scale in <see cref="MaxReachablePrimaryDrive"/>) for EDID/measurement noise,
        /// but anything more negative means the target gamut is genuinely wider than the display.
        /// </summary>
        public const double MinReachablePrimaryDrive = -0.05;

        /// <summary>
        /// A target is reachable only when every primary channel drive lies within the tolerated
        /// band [<see cref="MinReachablePrimaryDrive"/>, <see cref="MaxReachablePrimaryDrive"/>].
        /// The single-argument overload (positive drive only) is kept for backward compatibility;
        /// prefer the two-argument overload so out-of-gamut (negative-drive) targets are rejected.
        /// </summary>
        public static bool IsReachable(double maxPrimaryDrive)
            => IsReachable(maxPrimaryDrive, 0.0);

        public static bool IsReachable(double maxPrimaryDrive, double minPrimaryDrive)
            => double.IsFinite(maxPrimaryDrive) && double.IsFinite(minPrimaryDrive) &&
               minPrimaryDrive >= MinReachablePrimaryDrive &&
               maxPrimaryDrive <= MaxReachablePrimaryDrive;

        /// <summary>
        /// Largest display drive value the matrix demands for target primaries only. White is
        /// excluded because absolute white-point/luminance differences are handled by uniform
        /// matrix scaling, not by rejecting an otherwise reachable gamut.
        /// </summary>
        public static double MaxPrimaryDrive(double[,] contentToDisplayMatrix)
            => PrimaryDriveExtent(contentToDisplayMatrix).Max;

        /// <summary>
        /// Most-negative display drive value the matrix demands for target primaries only.
        /// A value below <see cref="MinReachablePrimaryDrive"/> means a target primary is outside
        /// the display gamut. Returns negative infinity on a non-finite matrix.
        /// </summary>
        public static double MinPrimaryDrive(double[,] contentToDisplayMatrix)
            => PrimaryDriveExtent(contentToDisplayMatrix).Min;

        /// <summary>
        /// Both the largest and most-negative primary-channel drives in one pass. Non-finite
        /// entries yield (+∞, -∞) so the target is treated as unreachable.
        /// </summary>
        public static (double Min, double Max) PrimaryDriveExtent(double[,] contentToDisplayMatrix)
        {
            if (contentToDisplayMatrix.GetLength(0) < 3 || contentToDisplayMatrix.GetLength(1) < 3)
                throw new ArgumentException("Matrix must be at least 3x3.", nameof(contentToDisplayMatrix));

            double max = 0;
            double min = 0;
            (double, double, double)[] primaries = { (1, 0, 0), (0, 1, 0), (0, 0, 1) };
            foreach (var (a, b, c) in primaries)
                for (int r = 0; r < 3; r++)
                {
                    double drive = contentToDisplayMatrix[r, 0] * a +
                                   contentToDisplayMatrix[r, 1] * b +
                                   contentToDisplayMatrix[r, 2] * c;
                    if (!double.IsFinite(drive))
                        return (double.NegativeInfinity, double.PositiveInfinity);
                    max = Math.Max(max, drive);
                    min = Math.Min(min, drive);
                }
            return (min, max);
        }

        public static bool TargetFitsEdidGamut(CalibrationTarget target, EdidColorInfo edid)
        {
            try
            {
                var (min, max) = PrimaryDriveExtent(BuildEdidTargetMatrix(target, edid));
                return IsReachable(max, min);
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
