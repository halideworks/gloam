using System;
using System.Collections.Generic;

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

        private const double MinimumCctApproximationKelvin = 1667.0;
        private const double MaximumCctApproximationKelvin = 25000.0;

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
            refWhite = SafeReferenceWhite(refWhite);

            double fx = LabFunction(SafeFinite(xyz.X) / refWhite.X);
            double fy = LabFunction(SafeFinite(xyz.Y) / refWhite.Y);
            double fz = LabFunction(SafeFinite(xyz.Z) / refWhite.Z);

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
            refWhite = SafeReferenceWhite(refWhite);
            if (!double.IsFinite(lab.L) || !double.IsFinite(lab.A) || !double.IsFinite(lab.B))
                return new CieXyz(0.0, 0.0, 0.0);

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
            if (!double.IsFinite(t)) t = 0.0;
            return t > LabEpsilon
                ? Math.Pow(t, 1.0 / 3.0)
                : (LabKappa * t + 16.0) / 116.0;
        }

        /// <summary>
        /// CIE Lab inverse function.
        /// </summary>
        private static double LabFunctionInverse(double t)
        {
            if (!double.IsFinite(t)) return 0.0;
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
            xyz = SafeXyz(xyz);
            sourceWhite = SafeReferenceWhite(sourceWhite);
            destWhite = SafeReferenceWhite(destWhite);

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
            if (!IsUsableCone(srcCone) || !IsUsableCone(dstCone))
                return xyz;

            double scaleL = dstCone[0] / srcCone[0];
            double scaleM = dstCone[1] / srcCone[1];
            double scaleS = dstCone[2] / srcCone[2];
            if (!double.IsFinite(scaleL) || !double.IsFinite(scaleM) || !double.IsFinite(scaleS))
                return xyz;

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

            return SafeXyz(new CieXyz(result[0], result[1], result[2]));
        }

        /// <summary>
        /// Convenience method: Adapt from D50 to D65.
        /// </summary>
        public static CieXyz AdaptD50ToD65(CieXyz xyz) => ChromaticAdaptation(xyz, D50White, D65White);

        /// <summary>
        /// Convenience method: Adapt from D65 to D50.
        /// </summary>
        public static CieXyz AdaptD65ToD50(CieXyz xyz) => ChromaticAdaptation(xyz, D65White, D50White);

        /// <summary>
        /// CAT16 forward matrix M16: XYZ → sharpened cone response (RGB_c).
        /// Published values from Li et al. (2017), "Comprehensive color solutions:
        /// CAM16, CAT16, and CAM16-UCS"; standardized in CIE 248:2022.
        /// </summary>
        public static readonly double[,] Cat16Matrix = {
            {  0.401288, 0.650173, -0.051461 },
            { -0.250268, 1.204414,  0.045854 },
            { -0.002079, 0.048952,  0.953127 }
        };

        /// <summary>
        /// Inverse CAT16 matrix (cone response → XYZ). Computed from
        /// <see cref="Cat16Matrix"/> rather than hard-coding the published rounded
        /// inverse so the forward/inverse pair round-trips at double precision.
        /// </summary>
        public static readonly double[,] Cat16InverseMatrix = Invert3x3(Cat16Matrix);

        /// <summary>
        /// CAT16 corresponding-colour transform with an explicit degree of adaptation D
        /// (Li et al. 2017; CIE 248:2022). Maps <paramref name="source"/> (viewed under
        /// <paramref name="sourceWhite"/>) to its corresponding colour under
        /// <paramref name="destWhite"/>, with adaptation completeness
        /// <paramref name="degree"/> ∈ [0, 1].
        /// </summary>
        /// <remarks>
        /// Incomplete adaptation is applied as an ILLUMINANT BLEND in CAT16 cone space:
        /// the effective adopted white is
        ///
        ///     adoptedWhite_c = D · destWhite_c + (1 − D) · sourceWhite_c
        ///
        /// and the transform is a von Kries scaling by adoptedWhite_c / sourceWhite_c.
        /// The resulting per-channel gains are D · (destWhite_c / sourceWhite_c) + (1 − D),
        /// i.e. the D-interpolation between the full CAT16 von Kries factors and unity —
        /// the same convention CAM16 uses for its degree-of-adaptation factors
        /// (D_c = D · Yw / RGB_wc + 1 − D) applied to a two-illuminant corresponding-colour
        /// pair at equal white luminance, which is exactly the night-mode case here (both
        /// whites at Y = 1; only chromaticity moves). D = 1 reproduces the full CAT16
        /// transform (source white maps exactly onto destination white); D = 0 is the
        /// identity.
        /// </remarks>
        public static CieXyz Cat16Adapt(CieXyz source, CieXyz sourceWhite, CieXyz destWhite, double degree)
        {
            source = SafeXyz(source);
            sourceWhite = SafeReferenceWhite(sourceWhite);
            destWhite = SafeReferenceWhite(destWhite);
            degree = double.IsFinite(degree) ? Math.Clamp(degree, 0.0, 1.0) : 1.0;

            double[] srcCone = MatrixMultiply(Cat16Matrix, new[] { sourceWhite.X, sourceWhite.Y, sourceWhite.Z });
            double[] dstCone = MatrixMultiply(Cat16Matrix, new[] { destWhite.X, destWhite.Y, destWhite.Z });
            if (!IsUsableCone(srcCone) || !IsUsableCone(dstCone))
                return source;

            // Effective adopted white: degree-of-adaptation blend of the destination and
            // source whites in sharpened cone space (see remarks).
            double[] adoptedCone = {
                degree * dstCone[0] + (1.0 - degree) * srcCone[0],
                degree * dstCone[1] + (1.0 - degree) * srcCone[1],
                degree * dstCone[2] + (1.0 - degree) * srcCone[2]
            };

            double scaleR = adoptedCone[0] / srcCone[0];
            double scaleG = adoptedCone[1] / srcCone[1];
            double scaleB = adoptedCone[2] / srcCone[2];
            if (!double.IsFinite(scaleR) || !double.IsFinite(scaleG) || !double.IsFinite(scaleB))
                return source;

            double[] inputCone = MatrixMultiply(Cat16Matrix, new[] { source.X, source.Y, source.Z });
            double[] adaptedCone = {
                inputCone[0] * scaleR,
                inputCone[1] * scaleG,
                inputCone[2] * scaleB
            };

            double[] result = MatrixMultiply(Cat16InverseMatrix, adaptedCone);
            return SafeXyz(new CieXyz(result[0], result[1], result[2]));
        }

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
            rgb = SafeLinearRgb(rgb);
            double[] result = MatrixMultiply(SrgbToXyzMatrix, new[] { rgb.R, rgb.G, rgb.B });
            return new CieXyz(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts CIE XYZ to linear sRGB.
        /// </summary>
        public static LinearRgb XyzToLinearSrgb(CieXyz xyz)
        {
            xyz = SafeXyz(xyz);
            double[] result = MatrixMultiply(XyzToSrgbMatrix, new[] { xyz.X, xyz.Y, xyz.Z });
            return new LinearRgb(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts linear Rec.2020 RGB to CIE XYZ.
        /// </summary>
        public static CieXyz LinearRec2020ToXyz(LinearRgb rgb)
        {
            rgb = SafeLinearRgb(rgb);
            double[] result = MatrixMultiply(Rec2020ToXyzMatrix, new[] { rgb.R, rgb.G, rgb.B });
            return new CieXyz(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts CIE XYZ to linear Rec.2020 RGB.
        /// </summary>
        public static LinearRgb XyzToLinearRec2020(CieXyz xyz)
        {
            xyz = SafeXyz(xyz);
            double[] result = MatrixMultiply(XyzToRec2020Matrix, new[] { xyz.X, xyz.Y, xyz.Z });
            return new LinearRgb(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts linear DCI-P3 D65 RGB to CIE XYZ.
        /// </summary>
        public static CieXyz LinearP3D65ToXyz(LinearRgb rgb)
        {
            rgb = SafeLinearRgb(rgb);
            double[] result = MatrixMultiply(P3D65ToXyzMatrix, new[] { rgb.R, rgb.G, rgb.B });
            return new CieXyz(result[0], result[1], result[2]);
        }

        /// <summary>
        /// Converts CIE XYZ to linear DCI-P3 D65 RGB.
        /// </summary>
        public static LinearRgb XyzToLinearP3D65(CieXyz xyz)
        {
            xyz = SafeXyz(xyz);
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
            linear = Clamp01(linear);
            return linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        }

        /// <summary>
        /// sRGB EOTF: Converts sRGB signal to linear light (gamma decoding).
        /// </summary>
        public static double SrgbEotf(double signal)
        {
            signal = Clamp01(signal);
            return signal <= 0.04045
                ? signal / 12.92
                : Math.Pow((signal + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// Pure gamma encoding (no linear segment).
        /// </summary>
        public static double GammaEncode(double linear, double gamma)
        {
            linear = Clamp01(linear);
            gamma = SafeGamma(gamma);
            return Math.Pow(linear, 1.0 / gamma);
        }

        /// <summary>
        /// Pure gamma decoding (no linear segment).
        /// </summary>
        public static double GammaDecode(double signal, double gamma)
        {
            signal = Clamp01(signal);
            gamma = SafeGamma(gamma);
            return Math.Pow(signal, gamma);
        }

        /// <summary>
        /// Rec.2020 OETF (10-bit and 12-bit use slightly different curves, this is the standard).
        /// </summary>
        public static double Rec2020Oetf(double linear)
        {
            const double alpha = 1.09929682680944;
            const double beta = 0.018053968510807;

            linear = Clamp01(linear);
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

            signal = Clamp01(signal);
            double threshold = 4.5 * beta;
            return signal < threshold
                ? signal / 4.5
                : Math.Pow((signal + (alpha - 1)) / alpha, 1.0 / 0.45);
        }

        #endregion

        #region Correlated Color Temperature

        /// <summary>
        /// Calculates correlated color temperature (CCT) from chromaticity by finding the
        /// closest point on the app's CCT locus in CIE 1960 UCS.
        /// </summary>
        /// <remarks>
        /// This is slower than McCamy's cubic approximation but much more appropriate for
        /// calibration reports: the returned CCT and Duv are mutually consistent because both
        /// are computed in CIE 1960 UCS against the same locus.
        /// </remarks>
        public static double ChromaticityToCct(Chromaticity xy)
        {
            if (!IsPlausibleChromaticity(xy))
                return 6500.0;

            var targetUv = ToUcs1960(xy);
            if (!double.IsFinite(targetUv.U) || !double.IsFinite(targetUv.V))
                return 6500.0;

            // Golden-section search over the practical display-white range. The distance to
            // the Planckian/daylight locus is unimodal for normal near-white chromaticities.
            double lo = MinimumCctApproximationKelvin;
            double hi = MaximumCctApproximationKelvin;
            double gr = (Math.Sqrt(5.0) - 1.0) / 2.0;
            double c = hi - gr * (hi - lo);
            double d = lo + gr * (hi - lo);
            double fc = UcsDistanceSquared(targetUv, CctToChromaticity(c));
            double fd = UcsDistanceSquared(targetUv, CctToChromaticity(d));

            for (int i = 0; i < 60; i++)
            {
                if (fc < fd)
                {
                    hi = d;
                    d = c;
                    fd = fc;
                    c = hi - gr * (hi - lo);
                    fc = UcsDistanceSquared(targetUv, CctToChromaticity(c));
                }
                else
                {
                    lo = c;
                    c = d;
                    fc = fd;
                    d = lo + gr * (hi - lo);
                    fd = UcsDistanceSquared(targetUv, CctToChromaticity(d));
                }
            }

            return (lo + hi) * 0.5;
        }

        /// <summary>
        /// Calculates the Duv (distance from Planckian locus) for a chromaticity.
        /// Positive Duv = greenish, Negative Duv = pinkish/magenta.
        /// </summary>
        public static double CalculateDuv(Chromaticity xy)
        {
            if (!IsPlausibleChromaticity(xy))
                return 0.0;

            double cct = ChromaticityToCct(xy);
            var planckian = CctToChromaticity(cct);

            var uv = ToUcs1960(xy);
            var planckianUv = ToUcs1960(planckian);
            if (!double.IsFinite(uv.U) || !double.IsFinite(uv.V) ||
                !double.IsFinite(planckianUv.U) || !double.IsFinite(planckianUv.V))
            {
                return 0.0;
            }

            double offsetU = uv.U - planckianUv.U;
            double offsetV = uv.V - planckianUv.V;
            double duv = Math.Sqrt(offsetU * offsetU + offsetV * offsetV);

            double step = Math.Max(1.0, cct * 0.001);
            var lowerUv = ToUcs1960(CctToChromaticity(cct - step));
            var upperUv = ToUcs1960(CctToChromaticity(cct + step));
            double tangentU = upperUv.U - lowerUv.U;
            double tangentV = upperUv.V - lowerUv.V;
            double cross = tangentU * offsetV - tangentV * offsetU;

            // Positive Duv is conventionally above/green of the Planckian locus.
            return cross <= 0.0 ? duv : -duv;
        }

        private static (double U, double V) ToUcs1960(Chromaticity xy)
        {
            if (!IsPlausibleChromaticity(xy))
                return (double.NaN, double.NaN);

            double denom = -2.0 * xy.X + 12.0 * xy.Y + 3.0;
            if (Math.Abs(denom) < 1e-12)
                return (double.NaN, double.NaN);
            return (4.0 * xy.X / denom, 6.0 * xy.Y / denom);
        }

        private static double UcsDistanceSquared((double U, double V) targetUv, Chromaticity locusXy)
        {
            var locusUv = ToUcs1960(locusXy);
            double du = targetUv.U - locusUv.U;
            double dv = targetUv.V - locusUv.V;
            return du * du + dv * dv;
        }

        /// <summary>
        /// Calculates chromaticity from CCT using Kang et al. approximation.
        /// </summary>
        public static Chromaticity CctToChromaticity(double cct)
        {
            double x, y;
            double T = double.IsFinite(cct)
                ? Math.Clamp(cct, MinimumCctApproximationKelvin, MaximumCctApproximationKelvin)
                : 6500.0;

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
            t = Clamp01(t);
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
            t = Clamp01(t);
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
            ValidateChromaticity(red, nameof(red));
            ValidateChromaticity(green, nameof(green));
            ValidateChromaticity(blue, nameof(blue));
            ValidateChromaticity(white, nameof(white));

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
            Validate3x3Finite(m, nameof(m));

            double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                       - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                       + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

            if (!double.IsFinite(det) || Math.Abs(det) < 1e-10)
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
            Validate3x3Finite(a, nameof(a));
            Validate3x3Finite(b, nameof(b));

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

        #region Gamut Coverage

        /// <summary>sRGB / Rec.709 red primary in CIE xy.</summary>
        public static readonly Chromaticity SrgbRedPrimary = new(0.64, 0.33);

        /// <summary>sRGB / Rec.709 green primary in CIE xy.</summary>
        public static readonly Chromaticity SrgbGreenPrimary = new(0.30, 0.60);

        /// <summary>sRGB / Rec.709 blue primary in CIE xy.</summary>
        public static readonly Chromaticity SrgbBluePrimary = new(0.15, 0.06);

        /// <summary>
        /// Fraction of the sRGB gamut triangle (CIE xy) covered by the gamut triangle of the
        /// given measured primaries: area(measured ∩ sRGB) / area(sRGB). Returns 0..1+ scale
        /// values in [0, 1] (full coverage = 1.0).
        /// </summary>
        public static double GamutCoverage(Chromaticity red, Chromaticity green, Chromaticity blue)
            => GamutCoverage(red, green, blue, SrgbRedPrimary, SrgbGreenPrimary, SrgbBluePrimary);

        /// <summary>
        /// Fraction of the reference gamut triangle (CIE xy) covered by the measured gamut
        /// triangle: area(measured ∩ reference) / area(reference). The intersection is
        /// computed by Sutherland-Hodgman clipping of the measured triangle against the
        /// reference triangle. Vertex winding order does not matter. Returns 0 for a
        /// degenerate (zero-area) reference or measured triangle.
        /// </summary>
        public static double GamutCoverage(
            Chromaticity red, Chromaticity green, Chromaticity blue,
            Chromaticity refRed, Chromaticity refGreen, Chromaticity refBlue)
        {
            if (!IsPlausibleChromaticity(red) || !IsPlausibleChromaticity(green) ||
                !IsPlausibleChromaticity(blue) || !IsPlausibleChromaticity(refRed) ||
                !IsPlausibleChromaticity(refGreen) || !IsPlausibleChromaticity(refBlue))
            {
                return 0.0;
            }

            var subject = NormalizeWinding(new List<(double X, double Y)>
            {
                (red.X, red.Y), (green.X, green.Y), (blue.X, blue.Y)
            });
            var clip = NormalizeWinding(new List<(double X, double Y)>
            {
                (refRed.X, refRed.Y), (refGreen.X, refGreen.Y), (refBlue.X, refBlue.Y)
            });

            double refArea = Math.Abs(SignedArea(clip));
            if (refArea < 1e-12 || Math.Abs(SignedArea(subject)) < 1e-12)
                return 0;

            var intersection = ClipPolygon(subject, clip);
            if (intersection.Count < 3)
                return 0;

            double coverage = Math.Abs(SignedArea(intersection)) / refArea;
            return double.IsFinite(coverage) ? Math.Clamp(coverage, 0.0, 1.0) : 0.0;
        }

        private static double Clamp01(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

        private static double SafeGamma(double gamma) =>
            double.IsFinite(gamma) && gamma is >= 1.0 and <= 4.0 ? gamma : 2.2;

        private static double SafeFinite(double value) => double.IsFinite(value) ? value : 0.0;

        private static CieXyz SafeXyz(CieXyz xyz) => new(
            SafeFinite(xyz.X),
            SafeFinite(xyz.Y),
            SafeFinite(xyz.Z));

        private static LinearRgb SafeLinearRgb(LinearRgb rgb) => new(
            SafeFinite(rgb.R),
            SafeFinite(rgb.G),
            SafeFinite(rgb.B));

        private static CieXyz SafeReferenceWhite(CieXyz white) =>
            double.IsFinite(white.X) && double.IsFinite(white.Y) && double.IsFinite(white.Z) &&
            white.X > 0.0 && white.Y > 0.0 && white.Z > 0.0
                ? white
                : D65White;

        private static bool IsUsableCone(double[] cone) =>
            cone.Length == 3 &&
            double.IsFinite(cone[0]) && double.IsFinite(cone[1]) && double.IsFinite(cone[2]) &&
            Math.Abs(cone[0]) > 1e-12 && Math.Abs(cone[1]) > 1e-12 && Math.Abs(cone[2]) > 1e-12;

        private static bool IsPlausibleChromaticity(Chromaticity xy) =>
            double.IsFinite(xy.X) && double.IsFinite(xy.Y) &&
            xy.X > 0.0 && xy.Y > 0.0 && xy.X + xy.Y <= 1.000001;

        private static void ValidateChromaticity(Chromaticity xy, string name)
        {
            if (!IsPlausibleChromaticity(xy))
            {
                throw new ArgumentException(
                    $"{name} chromaticity must be finite, positive and inside the CIE xy chromaticity plane.",
                    name);
            }
        }

        private static void Validate3x3Finite(double[,] matrix, string name)
        {
            if (matrix == null || matrix.GetLength(0) != 3 || matrix.GetLength(1) != 3)
                throw new ArgumentException($"{name} must be a 3x3 matrix.", name);

            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                if (!double.IsFinite(matrix[r, c]))
                    throw new InvalidOperationException("Matrix contains non-finite values and cannot be used.");
            }
        }

        /// <summary>Shoelace signed area (positive for counter-clockwise winding).</summary>
        private static double SignedArea(IReadOnlyList<(double X, double Y)> polygon)
        {
            double area = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area / 2;
        }

        /// <summary>Returns the polygon in counter-clockwise winding.</summary>
        private static List<(double X, double Y)> NormalizeWinding(List<(double X, double Y)> polygon)
        {
            if (SignedArea(polygon) < 0)
                polygon.Reverse();
            return polygon;
        }

        /// <summary>
        /// Sutherland-Hodgman: clips the subject polygon against each edge of the convex
        /// clip polygon. Both polygons must wind counter-clockwise.
        /// </summary>
        private static List<(double X, double Y)> ClipPolygon(
            List<(double X, double Y)> subject, List<(double X, double Y)> clip)
        {
            var output = subject;
            for (int i = 0; i < clip.Count && output.Count > 0; i++)
            {
                var edgeA = clip[i];
                var edgeB = clip[(i + 1) % clip.Count];

                // Inside = on or left of the directed edge (CCW polygon interior).
                bool Inside((double X, double Y) p) =>
                    (edgeB.X - edgeA.X) * (p.Y - edgeA.Y) - (edgeB.Y - edgeA.Y) * (p.X - edgeA.X) >= 0;

                // Intersection of segment p→q with the (infinite) edge line.
                (double X, double Y) Intersect((double X, double Y) p, (double X, double Y) q)
                {
                    double a1 = edgeB.Y - edgeA.Y, b1 = edgeA.X - edgeB.X;
                    double c1 = a1 * edgeA.X + b1 * edgeA.Y;
                    double a2 = q.Y - p.Y, b2 = p.X - q.X;
                    double c2 = a2 * p.X + b2 * p.Y;
                    double det = a1 * b2 - a2 * b1;
                    if (Math.Abs(det) < 1e-15)
                        return p; // parallel: degenerate, keep an endpoint
                    return ((b2 * c1 - b1 * c2) / det, (a1 * c2 - a2 * c1) / det);
                }

                var input = output;
                output = new List<(double X, double Y)>(input.Count + 2);
                for (int j = 0; j < input.Count; j++)
                {
                    var current = input[j];
                    var previous = input[(j + input.Count - 1) % input.Count];
                    bool currentInside = Inside(current);
                    bool previousInside = Inside(previous);

                    if (currentInside)
                    {
                        if (!previousInside)
                            output.Add(Intersect(previous, current));
                        output.Add(current);
                    }
                    else if (previousInside)
                    {
                        output.Add(Intersect(previous, current));
                    }
                }
            }
            return output;
        }

        #endregion
    }
}
