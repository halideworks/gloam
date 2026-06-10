using System;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Core color mathematics for colorimetric calculations.
    /// Implements CIE standard color space conversions and chromatic adaptation.
    /// </summary>
    /// <remarks>
    /// References:
    /// - CIE 015:2018 - Colorimetry, 4th Edition
    /// - CIE 142-2001 - Improvement to industrial colour-difference evaluation
    /// - Bruce Lindbloom's color math: http://brucelindbloom.com/
    /// - IEC 61966-2-1:1999 - sRGB colour space
    /// - ITU-R BT.2100-2 - HDR television (PQ and HLG)
    /// </remarks>
    public static class ColorMath
    {
        #region Constants

        /// <summary>
        /// CIE Lab function threshold ε (epsilon): (6/29)³ = 216/24389 ≈ 0.008856
        /// Below this, the linear segment is used instead of the cube root.
        /// </summary>
        private const double LabEpsilon = 216.0 / 24389.0; // 0.008856451679...

        /// <summary>
        /// CIE Lab function scaling factor κ (kappa): (29/6)³ = 24389/27 ≈ 903.3
        /// Used in the linear segment: f(t) = (κt + 16) / 116
        /// </summary>
        private const double LabKappa = 24389.0 / 27.0; // 903.2962962...

        /// <summary>
        /// Lab offset: 16/116 ≈ 0.137931 (not currently used but documented for reference)
        /// </summary>
        private const double LabOffset = 16.0 / 116.0;

        #endregion

        #region XYZ Reference White Points

        /// <summary>
        /// CIE D65 reference white XYZ (Y=1 normalized).
        /// Standard daylight illuminant, approximately 6504K.
        /// </summary>
        public static readonly CieXyz D65White = new(0.95047, 1.0, 1.08883);

        /// <summary>
        /// CIE D50 reference white XYZ (Y=1 normalized).
        /// Horizon light, approximately 5003K. Used in ICC profiles.
        /// </summary>
        public static readonly CieXyz D50White = new(0.96422, 1.0, 0.82521);

        #endregion

        #region XYZ ↔ Lab Conversions

        /// <summary>
        /// Converts CIE XYZ to CIE L*a*b* using D65 reference white.
        /// </summary>
        public static CieLab XyzToLab(CieXyz xyz) => XyzToLab(xyz, D65White);

        /// <summary>
        /// Converts CIE XYZ to CIE L*a*b* with specified reference white.
        /// </summary>
        /// <remarks>
        /// Implements the CIE 1976 L*a*b* color space conversion.
        ///
        /// f(t) = t^(1/3)           if t > ε
        ///      = (κt + 16)/116     otherwise
        ///
        /// L* = 116 * f(Y/Yn) - 16
        /// a* = 500 * (f(X/Xn) - f(Y/Yn))
        /// b* = 200 * (f(Y/Yn) - f(Z/Zn))
        /// </remarks>
        public static CieLab XyzToLab(CieXyz xyz, CieXyz refWhite)
        {
            double fx = LabFunction(xyz.X / refWhite.X);
            double fy = LabFunction(xyz.Y / refWhite.Y);
            double fz = LabFunction(xyz.Z / refWhite.Z);

            double l = 116.0 * fy - 16.0;
            double a = 500.0 * (fx - fy);
            double b = 200.0 * (fy - fz);

            return new CieLab(l, a, b);
        }

        /// <summary>
        /// Converts CIE L*a*b* to CIE XYZ using D65 reference white.
        /// </summary>
        public static CieXyz LabToXyz(CieLab lab) => LabToXyz(lab, D65White);

        /// <summary>
        /// Converts CIE L*a*b* to CIE XYZ with specified reference white.
        /// </summary>
        public static CieXyz LabToXyz(CieLab lab, CieXyz refWhite)
        {
            double fy = (lab.L + 16.0) / 116.0;
            double fx = lab.A / 500.0 + fy;
            double fz = fy - lab.B / 200.0;

            double x = refWhite.X * LabFunctionInverse(fx);
            double y = refWhite.Y * LabFunctionInverse(fy);
            double z = refWhite.Z * LabFunctionInverse(fz);

            return new CieXyz(x, y, z);
        }

        /// <summary>
        /// CIE Lab forward function: f(t) = t^(1/3) or linear segment.
        /// </summary>
        private static double LabFunction(double t)
        {
            return t > LabEpsilon
                ? Math.Pow(t, 1.0 / 3.0)
                : (LabKappa * t + 16.0) / 116.0;
        }

        /// <summary>
        /// CIE Lab inverse function.
        /// </summary>
        private static double LabFunctionInverse(double t)
        {
            double t3 = t * t * t;
            return t3 > LabEpsilon
                ? t3
                : (116.0 * t - 16.0) / LabKappa;
        }

        #endregion

        #region Chromatic Adaptation

        /// <summary>
        /// Adapts XYZ from one illuminant to another using Bradford transform.
        /// </summary>
        /// <remarks>
        /// The Bradford chromatic adaptation transform is widely considered
        /// the most accurate for corresponding color predictions.
        ///
        /// Reference: Lam, K. M. (1985). Metamerism and Colour Constancy.
        /// </remarks>
        public static CieXyz ChromaticAdaptation(CieXyz xyz, CieXyz sourceWhite, CieXyz destWhite)
        {
            // Bradford cone response matrix
            // Transforms XYZ to "sharpened" cone responses (LMS-like)
            double[,] mBradford = {
                {  0.8951000,  0.2664000, -0.1614000 },
                { -0.7502000,  1.7135000,  0.0367000 },
                {  0.0389000, -0.0685000,  1.0296000 }
            };

            // Inverse Bradford matrix
            double[,] mBradfordInv = {
                {  0.9869929, -0.1470543,  0.1599627 },
                {  0.4323053,  0.5183603,  0.0492912 },
                { -0.0085287,  0.0400428,  0.9684867 }
            };

            // Transform reference whites to cone responses
            double[] srcCone = MatrixMultiply(mBradford, new[] { sourceWhite.X, sourceWhite.Y, sourceWhite.Z });
            double[] dstCone = MatrixMultiply(mBradford, new[] { destWhite.X, destWhite.Y, destWhite.Z });

            // Scaling factors
            double scaleL = dstCone[0] / srcCone[0];
            double scaleM = dstCone[1] / srcCone[1];
            double scaleS = dstCone[2] / srcCone[2];

            // Transform input XYZ to cone response
            double[] inputCone = MatrixMultiply(mBradford, new[] { xyz.X, xyz.Y, xyz.Z });

            // Apply scaling
            double[] adaptedCone = {
                inputCone[0] * scaleL,
                inputCone[1] * scaleM,
                inputCone[2] * scaleS
            };

            // Transform back to XYZ
            double[] result = MatrixMultiply(mBradfordInv, adaptedCone);

            return new CieXyz(result[0], result[1], result[2]);
        }

        /// <summary>
        /// The Bradford adaptation as a reusable 3x3 XYZ→XYZ matrix (same math as
        /// <see cref="ChromaticAdaptation"/>, but composable with other matrices).
        /// Maps <paramref name="sourceWhite"/> exactly onto <paramref name="destWhite"/>.
        /// </summary>
        public static double[,] ChromaticAdaptationMatrix(CieXyz sourceWhite, CieXyz destWhite)
        {
            double[,] mBradford = {
                {  0.8951000,  0.2664000, -0.1614000 },
                { -0.7502000,  1.7135000,  0.0367000 },
                {  0.0389000, -0.0685000,  1.0296000 }
            };
            double[,] mBradfordInv = {
                {  0.9869929, -0.1470543,  0.1599627 },
                {  0.4323053,  0.5183603,  0.0492912 },
                { -0.0085287,  0.0400428,  0.9684867 }
            };

            double[] srcCone = MatrixMultiply(mBradford, new[] { sourceWhite.X, sourceWhite.Y, sourceWhite.Z });
            double[] dstCone = MatrixMultiply(mBradford, new[] { destWhite.X, destWhite.Y, destWhite.Z });

            var scale = new double[3, 3];
            for (int i = 0; i < 3; i++)
                scale[i, i] = dstCone[i] / srcCone[i];

            return MultiplyMatrices(mBradfordInv, MultiplyMatrices(scale, mBradford));
        }

        /// <summary>
        /// Convenience method: Adapt from D50 to D65.
        /// </summary>
        public static CieXyz AdaptD50ToD65(CieXyz xyz) => ChromaticAdaptation(xyz, D50White, D65White);

        /// <summary>
        /// Convenience method: Adapt from D65 to D50.
        /// </summary>
        public static CieXyz AdaptD65ToD50(CieXyz xyz) => ChromaticAdaptation(xyz, D65White, D50White);

        #endregion

        #region RGB ↔ XYZ Conversions

        /// <summary>
        /// RGB to XYZ conversion matrix for sRGB/Rec.709 (D65 white point).
        /// </summary>
        public static readonly double[,] SrgbToXyzMatrix = {
            { 0.4124564, 0.3575761, 0.1804375 },
            { 0.2126729, 0.7151522, 0.0721750 },
            { 0.0193339, 0.1191920, 0.9503041 }
        };

        /// <summary>
        /// XYZ to RGB conversion matrix for sRGB/Rec.709 (D65 white point).
        /// </summary>
        public static readonly double[,] XyzToSrgbMatrix = {
            {  3.2404542, -1.5371385, -0.4985314 },
            { -0.9692660,  1.8760108,  0.0415560 },
            {  0.0556434, -0.2040259,  1.0572252 }
        };

        /// <summary>
        /// RGB to XYZ conversion matrix for Rec.2020 (D65 white point).
        /// </summary>
        public static readonly double[,] Rec2020ToXyzMatrix = {
            { 0.6369580, 0.1446169, 0.1688810 },
            { 0.2627002, 0.6779981, 0.0593017 },
            { 0.0000000, 0.0280727, 1.0609851 }
        };

        /// <summary>
        /// XYZ to RGB conversion matrix for Rec.2020 (D65 white point).
        /// </summary>
        public static readonly double[,] XyzToRec2020Matrix = {
            {  1.7166512, -0.3556708, -0.2533663 },
            { -0.6666844,  1.6164812,  0.0157685 },
            {  0.0176399, -0.0427706,  0.9421031 }
        };

        /// <summary>
        /// RGB to XYZ conversion matrix for DCI-P3 (D65 white point).
        /// </summary>
        public static readonly double[,] P3D65ToXyzMatrix = {
            { 0.4865709, 0.2656677, 0.1982173 },
            { 0.2289746, 0.6917385, 0.0792869 },
            { 0.0000000, 0.0451134, 1.0439444 }
        };

        /// <summary>
        /// XYZ to RGB conversion matrix for DCI-P3 (D65 white point).
        /// </summary>
        public static readonly double[,] XyzToP3D65Matrix = {
            {  2.4934969, -0.9313836, -0.4027108 },
            { -0.8294890,  1.7626641,  0.0236247 },
            {  0.0358458, -0.0761724,  0.9568845 }
        };

        /// <summary>
        /// Converts linear sRGB to CIE XYZ.
        /// </summary>
        public static CieXyz LinearSrgbToXyz(LinearRgb rgb)
        {
            double[] result = MatrixMultiply(SrgbToXyzMatrix, new[] { rgb.R, rgb.G, rgb.B });
            return new CieXyz(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts CIE XYZ to linear sRGB.
        /// </summary>
        public static LinearRgb XyzToLinearSrgb(CieXyz xyz)
        {
            double[] result = MatrixMultiply(XyzToSrgbMatrix, new[] { xyz.X, xyz.Y, xyz.Z });
            return new LinearRgb(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts linear Rec.2020 RGB to CIE XYZ.
        /// </summary>
        public static CieXyz LinearRec2020ToXyz(LinearRgb rgb)
        {
            double[] result = MatrixMultiply(Rec2020ToXyzMatrix, new[] { rgb.R, rgb.G, rgb.B });
            return new CieXyz(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts CIE XYZ to linear Rec.2020 RGB.
        /// </summary>
        public static LinearRgb XyzToLinearRec2020(CieXyz xyz)
        {
            double[] result = MatrixMultiply(XyzToRec2020Matrix, new[] { xyz.X, xyz.Y, xyz.Z });
            return new LinearRgb(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts linear DCI-P3 D65 RGB to CIE XYZ.
        /// </summary>
        public static CieXyz LinearP3D65ToXyz(LinearRgb rgb)
        {
            double[] result = MatrixMultiply(P3D65ToXyzMatrix, new[] { rgb.R, rgb.G, rgb.B });
            return new CieXyz(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts CIE XYZ to linear DCI-P3 D65 RGB.
        /// </summary>
        public static LinearRgb XyzToLinearP3D65(CieXyz xyz)
        {
            double[] result = MatrixMultiply(XyzToP3D65Matrix, new[] { xyz.X, xyz.Y, xyz.Z });
            return new LinearRgb(result[0], result[1], result[2]);
        }

        #endregion

        #region Transfer Functions

        /// <summary>
        /// sRGB OETF: Converts linear light to sRGB signal (gamma encoding).
        /// IEC 61966-2-1:1999 specified transfer function.
        /// </summary>
        public static double SrgbOetf(double linear)
        {
            linear = Math.Clamp(linear, 0, 1);
            return linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        }

        /// <summary>
        /// sRGB EOTF: Converts sRGB signal to linear light (gamma decoding).
        /// </summary>
        public static double SrgbEotf(double signal)
        {
            signal = Math.Clamp(signal, 0, 1);
            return signal <= 0.04045
                ? signal / 12.92
                : Math.Pow((signal + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// Pure gamma encoding (no linear segment).
        /// </summary>
        public static double GammaEncode(double linear, double gamma)
        {
            linear = Math.Clamp(linear, 0, 1);
            return Math.Pow(linear, 1.0 / gamma);
        }

        /// <summary>
        /// Pure gamma decoding (no linear segment).
        /// </summary>
        public static double GammaDecode(double signal, double gamma)
        {
            signal = Math.Clamp(signal, 0, 1);
            return Math.Pow(signal, gamma);
        }

        /// <summary>
        /// Rec.2020 OETF (10-bit and 12-bit use slightly different curves, this is the standard).
        /// </summary>
        public static double Rec2020Oetf(double linear)
        {
            const double alpha = 1.09929682680944;
            const double beta = 0.018053968510807;

            linear = Math.Clamp(linear, 0, 1);
            return linear < beta
                ? 4.5 * linear
                : alpha * Math.Pow(linear, 0.45) - (alpha - 1);
        }

        /// <summary>
        /// Rec.2020 EOTF.
        /// </summary>
        public static double Rec2020Eotf(double signal)
        {
            const double alpha = 1.09929682680944;
            const double beta = 0.018053968510807;

            signal = Math.Clamp(signal, 0, 1);
            double threshold = 4.5 * beta;
            return signal < threshold
                ? signal / 4.5
                : Math.Pow((signal + (alpha - 1)) / alpha, 1.0 / 0.45);
        }

        #endregion

        #region Correlated Color Temperature

        /// <summary>
        /// Calculates correlated color temperature (CCT) from chromaticity using McCamy's approximation.
        /// </summary>
        /// <remarks>
        /// Valid for CCT range ~3000K to ~50000K.
        /// Accuracy: ±2K for most of the range.
        /// Reference: McCamy, C. S. (1992). Correlated color temperature as an explicit function of
        /// chromaticity coordinates. Color Research &amp; Application, 17(2), 142-144.
        /// </remarks>
        public static double ChromaticityToCct(Chromaticity xy)
        {
            // McCamy's approximation
            double n = (xy.X - 0.3320) / (0.1858 - xy.Y);
            double cct = 449.0 * n * n * n + 3525.0 * n * n + 6823.3 * n + 5520.33;
            return cct;
        }

        /// <summary>
        /// Calculates the Duv (distance from Planckian locus) for a chromaticity.
        /// Positive Duv = greenish, Negative Duv = pinkish/magenta.
        /// </summary>
        public static double CalculateDuv(Chromaticity xy)
        {
            // Convert to CIE 1960 UCS (u, v) coordinates
            double u = 4 * xy.X / (-2 * xy.X + 12 * xy.Y + 3);
            double v = 6 * xy.Y / (-2 * xy.X + 12 * xy.Y + 3);

            // Get CCT and corresponding Planckian locus point
            double cct = ChromaticityToCct(xy);
            var planckian = CctToChromaticity(cct);

            // Planckian locus in UCS
            double up = 4 * planckian.X / (-2 * planckian.X + 12 * planckian.Y + 3);
            double vp = 6 * planckian.Y / (-2 * planckian.X + 12 * planckian.Y + 3);

            // Distance (signed based on which side of locus)
            double duv = Math.Sqrt((u - up) * (u - up) + (v - vp) * (v - vp));

            // Sign: positive if above locus (more green), negative if below (more magenta)
            return v > vp ? duv : -duv;
        }

        /// <summary>
        /// Calculates chromaticity from CCT using Kang et al. approximation.
        /// </summary>
        public static Chromaticity CctToChromaticity(double cct)
        {
            double x, y;
            double T = cct;

            if (T <= 4000)
            {
                x = -0.2661239e9 / (T * T * T)
                  - 0.2343589e6 / (T * T)
                  + 0.8776956e3 / T
                  + 0.179910;
            }
            else
            {
                x = -3.0258469e9 / (T * T * T)
                  + 2.1070379e6 / (T * T)
                  + 0.2226347e3 / T
                  + 0.240390;
            }

            if (T <= 2222)
            {
                y = -1.1063814 * x * x * x
                  - 1.34811020 * x * x
                  + 2.18555832 * x
                  - 0.20219683;
            }
            else if (T <= 4000)
            {
                y = -0.9549476 * x * x * x
                  - 1.37418593 * x * x
                  + 2.09137015 * x
                  - 0.16748867;
            }
            else
            {
                y = 3.0817580 * x * x * x
                  - 5.87338670 * x * x
                  + 3.75112997 * x
                  - 0.37001483;
            }

            return new Chromaticity(x, y);
        }

        #endregion

        #region Utility Functions

        /// <summary>
        /// Interpolates between two Lab colors in perceptually uniform space.
        /// </summary>
        public static CieLab LerpLab(CieLab a, CieLab b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return new CieLab(
                a.L + (b.L - a.L) * t,
                a.A + (b.A - a.A) * t,
                a.B + (b.B - a.B) * t
            );
        }

        /// <summary>
        /// Interpolates between two XYZ colors.
        /// </summary>
        public static CieXyz LerpXyz(CieXyz a, CieXyz b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return new CieXyz(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t
            );
        }

        /// <summary>
        /// Multiplies a 3x3 matrix by a 3-element vector.
        /// </summary>
        private static double[] MatrixMultiply(double[,] m, double[] v)
        {
            return new[]
            {
                m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2],
                m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2],
                m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2]
            };
        }

        /// <summary>
        /// Calculates the 3x3 RGB to XYZ matrix for given primaries and white point.
        /// </summary>
        /// <remarks>
        /// This is used to create custom color space matrices from measured primaries.
        /// </remarks>
        public static double[,] CalculateRgbToXyzMatrix(
            Chromaticity red, Chromaticity green, Chromaticity blue, Chromaticity white)
        {
            // Convert primaries to XYZ (Y=1)
            var xyzR = red.ToXyz(1);
            var xyzG = green.ToXyz(1);
            var xyzB = blue.ToXyz(1);
            var xyzW = white.ToXyz(1);

            // Build the primaries matrix
            double[,] primaries = {
                { xyzR.X, xyzG.X, xyzB.X },
                { xyzR.Y, xyzG.Y, xyzB.Y },
                { xyzR.Z, xyzG.Z, xyzB.Z }
            };

            // Invert to solve for S (scaling factors)
            double[,] inverse = Invert3x3(primaries);
            double[] s = MatrixMultiply(inverse, new[] { xyzW.X, xyzW.Y, xyzW.Z });

            // Final matrix = primaries * diag(S)
            return new double[,] {
                { s[0] * xyzR.X, s[1] * xyzG.X, s[2] * xyzB.X },
                { s[0] * xyzR.Y, s[1] * xyzG.Y, s[2] * xyzB.Y },
                { s[0] * xyzR.Z, s[1] * xyzG.Z, s[2] * xyzB.Z }
            };
        }

        /// <summary>
        /// Inverts a 3x3 matrix.
        /// </summary>
        public static double[,] Invert3x3(double[,] m)
        {
            double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                       - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                       + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

            if (Math.Abs(det) < 1e-10)
                throw new InvalidOperationException("Matrix is singular and cannot be inverted.");

            double invDet = 1.0 / det;

            return new double[,] {
                {
                    (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) * invDet,
                    (m[0, 2] * m[2, 1] - m[0, 1] * m[2, 2]) * invDet,
                    (m[0, 1] * m[1, 2] - m[0, 2] * m[1, 1]) * invDet
                },
                {
                    (m[1, 2] * m[2, 0] - m[1, 0] * m[2, 2]) * invDet,
                    (m[0, 0] * m[2, 2] - m[0, 2] * m[2, 0]) * invDet,
                    (m[0, 2] * m[1, 0] - m[0, 0] * m[1, 2]) * invDet
                },
                {
                    (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]) * invDet,
                    (m[0, 1] * m[2, 0] - m[0, 0] * m[2, 1]) * invDet,
                    (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]) * invDet
                }
            };
        }

        /// <summary>
        /// Multiplies two 3x3 matrices.
        /// </summary>
        public static double[,] MultiplyMatrices(double[,] a, double[,] b)
        {
            var result = new double[3, 3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    result[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
                }
            }
            return result;
        }

        #endregion
    }
}
