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
        /// CIE D65 reference white XYZ (Y=1 normalized). Derived at full double precision from the
        /// standards white point <see cref="Chromaticity.D65"/> = (0.3127, 0.3290) via
        /// X = x/y, Y = 1, Z = (1-x-y)/y, so it is exactly consistent with the RGB↔XYZ matrices
        /// below (which are built from the same white). Was the rounded literal (0.95047, 1.0,
        /// 1.08883); the derived value is (0.950455…, 1.0, 1.089058…).
        /// </summary>
        public static readonly CieXyz D65White = Chromaticity.D65.ToXyz(1.0);

        /// <summary>
        /// CIE D50 reference white XYZ (Y=1 normalized), derived from <see cref="Chromaticity.D50"/>
        /// = (0.34567, 0.35850). Horizon light, ~5003K; used in ICC profiles.
        /// </summary>
        public static readonly CieXyz D50White = Chromaticity.D50.ToXyz(1.0);

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

        // The RGB↔XYZ matrices below are DERIVED at full double precision in the static
        // constructor from the standard primaries and the shared D65 white (0.3127, 0.3290),
        // rather than stored as rounded 7-digit literals. Deriving forward and inverse from the
        // same source guarantees they are exact inverses of each other and share one white point
        // with D65White and every CalculateRgbToXyzMatrix caller. The derived values agree with
        // the previous published literals to < 1e-4 (the literals were rounded from these same
        // primaries), so downstream numerics are unchanged to that tolerance.

        /// <summary>RGB to XYZ conversion matrix for sRGB/Rec.709 (D65 white point).</summary>
        public static readonly double[,] SrgbToXyzMatrix;

        /// <summary>XYZ to RGB conversion matrix for sRGB/Rec.709 (D65 white point).</summary>
        public static readonly double[,] XyzToSrgbMatrix;

        /// <summary>RGB to XYZ conversion matrix for Rec.2020 (D65 white point).</summary>
        public static readonly double[,] Rec2020ToXyzMatrix;

        /// <summary>XYZ to RGB conversion matrix for Rec.2020 (D65 white point).</summary>
        public static readonly double[,] XyzToRec2020Matrix;

        /// <summary>RGB to XYZ conversion matrix for DCI-P3 (D65 white point).</summary>
        public static readonly double[,] P3D65ToXyzMatrix;

        /// <summary>XYZ to RGB conversion matrix for DCI-P3 (D65 white point).</summary>
        public static readonly double[,] XyzToP3D65Matrix;

        static ColorMath()
        {
            // sRGB / Rec.709 primaries (0.64,0.33)(0.30,0.60)(0.15,0.06) + D65.
            SrgbToXyzMatrix = CalculateRgbToXyzMatrix(
                Chromaticity.Rec709Red, Chromaticity.Rec709Green, Chromaticity.Rec709Blue, Chromaticity.D65);
            XyzToSrgbMatrix = Invert3x3(SrgbToXyzMatrix);

            // Rec.2020 primaries (0.708,0.292)(0.170,0.797)(0.131,0.046) + D65.
            Rec2020ToXyzMatrix = CalculateRgbToXyzMatrix(
                Chromaticity.Rec2020Red, Chromaticity.Rec2020Green, Chromaticity.Rec2020Blue, Chromaticity.D65);
            XyzToRec2020Matrix = Invert3x3(Rec2020ToXyzMatrix);

            // DCI-P3 primaries (0.680,0.320)(0.265,0.690)(0.150,0.060) + D65.
            P3D65ToXyzMatrix = CalculateRgbToXyzMatrix(
                Chromaticity.P3Red, Chromaticity.P3Green, Chromaticity.P3Blue, Chromaticity.D65);
            XyzToP3D65Matrix = Invert3x3(P3D65ToXyzMatrix);
        }

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
        /// Correlated color temperature (CCT, Kelvin) from chromaticity, using Ohno's (2014)
        /// triangular+parabolic solution against a Planckian locus tabulated from the CIE 1931
        /// 2° observer. CCT and <see cref="CalculateDuv"/> are mutually consistent because both
        /// are solved in CIE 1960 UCS (u,v) against the same locus table.
        /// </summary>
        /// <remarks>
        /// Reference: Y. Ohno, "Practical Use and Calculation of CCT and Duv," LEUKOS 10(1), 2014.
        /// </remarks>
        public static double ChromaticityToCct(Chromaticity xy)
        {
            if (!IsPlausibleChromaticity(xy))
                return 6500.0;
            return OhnoCctDuv(xy).Cct;
        }

        /// <summary>
        /// Signed Duv (distance from the Planckian locus in CIE 1960 UCS) for a chromaticity.
        /// Positive Duv = above the locus (greenish); negative = below (pink/magenta).
        /// Solved by Ohno (2014) parabolic refinement against the tabulated locus.
        /// </summary>
        public static double CalculateDuv(Chromaticity xy)
        {
            if (!IsPlausibleChromaticity(xy))
                return 0.0;
            return OhnoCctDuv(xy).Duv;
        }

        private static (double Cct, double Duv) OhnoCctDuv(Chromaticity xy)
        {
            var (u, v) = ToUcs1960(xy);
            if (!double.IsFinite(u) || !double.IsFinite(v))
                return (6500.0, 0.0);

            var locus = PlanckianLocus.Instance;
            if (locus == null || locus.T.Length < 3)
                return (6500.0, 0.0); // table build failed (does not happen in practice)

            int n = locus.T.Length;

            // 1) Nearest table point in (u,v).
            int m = 0;
            double bestSq = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double du = u - locus.U[i], dv = v - locus.V[i];
                double sq = du * du + dv * dv;
                if (sq < bestSq) { bestSq = sq; m = i; }
            }
            m = Math.Clamp(m, 1, n - 2);

            double d1 = Hypot(u - locus.U[m - 1], v - locus.V[m - 1]);
            double d2 = Hypot(u - locus.U[m],     v - locus.V[m]);
            double d3 = Hypot(u - locus.U[m + 1], v - locus.V[m + 1]);
            double t1 = locus.T[m - 1], t2 = locus.T[m], t3 = locus.T[m + 1];

            // 2) Triangular solution (Ohno 2014, eqs. for the chord between m-1 and m+1).
            double l = Hypot(locus.U[m + 1] - locus.U[m - 1], locus.V[m + 1] - locus.V[m - 1]);
            double cctTri = t2;
            double duvTri = 0.0;
            if (l > 1e-15)
            {
                double xproj = (d1 * d1 - d3 * d3 + l * l) / (2.0 * l);
                cctTri = t1 + (t3 - t1) * (xproj / l);
                duvTri = Math.Sqrt(Math.Max(0.0, d1 * d1 - xproj * xproj));
            }

            // 3) Parabolic refinement (better for larger |Duv|).
            double cctPar = cctTri, duvPar = duvTri;
            double denom = (t3 - t2) * (t1 - t3) * (t2 - t1);
            if (Math.Abs(denom) > 1e-30)
            {
                double a = (t1 * (d3 - d2) + t2 * (d1 - d3) + t3 * (d2 - d1)) / denom;
                double b = -(t1 * t1 * (d3 - d2) + t2 * t2 * (d1 - d3) + t3 * t3 * (d2 - d1)) / denom;
                double c = -(d1 * (t3 - t2) * t2 * t3 + d2 * (t1 - t3) * t1 * t3 + d3 * (t2 - t1) * t1 * t2) / denom;
                if (Math.Abs(a) > 1e-30)
                {
                    cctPar = -b / (2.0 * a);
                    duvPar = a * cctPar * cctPar + b * cctPar + c;
                }
            }

            // Ohno: triangular is accurate near the locus; switch to parabolic when Duv is large.
            double cct = duvTri < 0.002 ? cctTri : cctPar;
            double duvMag = duvTri < 0.002 ? duvTri : Math.Abs(duvPar);

            if (!double.IsFinite(cct)) cct = t2;
            cct = Math.Clamp(cct, PlanckianMinKelvin, PlanckianMaxKelvin);

            // 4) Sign: positive above the locus (green). Use the locus tangent at the foot point
            //    and the offset from that foot, matching the CIE 1960 "green is positive" convention.
            var foot = CctToChromaticity(cct);
            var (fu, fv) = ToUcs1960(foot);
            double tangentU = locus.U[m + 1] - locus.U[m - 1];
            double tangentV = locus.V[m + 1] - locus.V[m - 1];
            double cross = tangentU * (v - fv) - tangentV * (u - fu);
            double duv = cross <= 0.0 ? duvMag : -duvMag;

            return (cct, duv);
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

        private static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

        /// <summary>
        /// Chromaticity of a Planckian (blackbody) radiator at the given CCT, interpolated
        /// (linear in log-CCT) from a table computed at startup from CIE 1931 2° CMFs and
        /// Planck's law. Input is clamped to the app's supported approximation range
        /// [1667, 25000] K for backward compatibility with existing callers; the underlying
        /// locus table itself spans [1000, 25000] K for the CCT/Duv inversion above.
        /// </summary>
        public static Chromaticity CctToChromaticity(double cct)
        {
            double t = double.IsFinite(cct)
                ? Math.Clamp(cct, MinimumCctApproximationKelvin, MaximumCctApproximationKelvin)
                : 6500.0;

            var locus = PlanckianLocus.Instance;
            if (locus == null || locus.T.Length < 2)
                return KangChromaticity(t); // fallback (table build failed)

            return locus.Interpolate(t);
        }

        /// <summary>
        /// Legacy Kang et al. (2002) cubic fit for chromaticity from CCT. Retained only as a
        /// fallback if the Planckian locus table fails to build (which it does not in practice).
        /// </summary>
        private static Chromaticity KangChromaticity(double T)
        {
            double x, y;
            if (T <= 4000)
            {
                x = -0.2661239e9 / (T * T * T) - 0.2343589e6 / (T * T) + 0.8776956e3 / T + 0.179910;
            }
            else
            {
                x = -3.0258469e9 / (T * T * T) + 2.1070379e6 / (T * T) + 0.2226347e3 / T + 0.240390;
            }

            if (T <= 2222)
                y = -1.1063814 * x * x * x - 1.34811020 * x * x + 2.18555832 * x - 0.20219683;
            else if (T <= 4000)
                y = -0.9549476 * x * x * x - 1.37418593 * x * x + 2.09137015 * x - 0.16748867;
            else
                y = 3.0817580 * x * x * x - 5.87338670 * x * x + 3.75112997 * x - 0.37001483;

            return new Chromaticity(x, y);
        }

        #endregion

        #region Planckian Locus Table (CIE 1931 2° observer)

        private const double PlanckianMinKelvin = 1000.0;
        private const double PlanckianMaxKelvin = 25000.0;

        /// <summary>Second radiation constant c2 (m·K), CODATA/CIE value used for the locus.</summary>
        private const double PlanckC2 = 1.4388e-2;

        private const double CmfStartNm = 380.0;
        private const double CmfStepNm = 5.0;

        // CIE 1931 2° standard observer colour-matching functions at 5 nm, 380–780 nm (81 samples).
        // Public CIE 015 / Wyszecki & Stiles data (values from the CVRL 1nm tabulation sampled at
        // 5 nm multiples). ybar is identical to the CIE 1924 V(λ) luminous efficiency function.
        private static readonly double[] Cmf1931X =
        {
            1.368000e-03, 2.236000e-03, 4.243000e-03, 7.650000e-03, 1.431000e-02, 2.319000e-02,
            4.351000e-02, 7.763000e-02, 1.343800e-01, 2.147700e-01, 2.839000e-01, 3.285000e-01,
            3.482800e-01, 3.480600e-01, 3.362000e-01, 3.187000e-01, 2.908000e-01, 2.511000e-01,
            1.953600e-01, 1.421000e-01, 9.564000e-02, 5.795001e-02, 3.201000e-02, 1.470000e-02,
            4.900000e-03, 2.400000e-03, 9.300000e-03, 2.910000e-02, 6.327000e-02, 1.096000e-01,
            1.655000e-01, 2.257499e-01, 2.904000e-01, 3.597000e-01, 4.334499e-01, 5.120501e-01,
            5.945000e-01, 6.784000e-01, 7.621000e-01, 8.425000e-01, 9.163000e-01, 9.786000e-01,
            1.026300e+00, 1.056700e+00, 1.062200e+00, 1.045600e+00, 1.002600e+00, 9.384000e-01,
            8.544499e-01, 7.514000e-01, 6.424000e-01, 5.419000e-01, 4.479000e-01, 3.608000e-01,
            2.835000e-01, 2.187000e-01, 1.649000e-01, 1.212000e-01, 8.740000e-02, 6.360000e-02,
            4.677000e-02, 3.290000e-02, 2.270000e-02, 1.584000e-02, 1.135916e-02, 8.110916e-03,
            5.790346e-03, 4.109457e-03, 2.899327e-03, 2.049190e-03, 1.439971e-03, 9.999493e-04,
            6.900786e-04, 4.760213e-04, 3.323011e-04, 2.348261e-04, 1.661505e-04, 1.174130e-04,
            8.307527e-05, 5.870652e-05, 4.150994e-05,
        };

        private static readonly double[] Cmf1931Y =
        {
            3.900000e-05, 6.400000e-05, 1.200000e-04, 2.170000e-04, 3.960000e-04, 6.400000e-04,
            1.210000e-03, 2.180000e-03, 4.000000e-03, 7.300000e-03, 1.160000e-02, 1.684000e-02,
            2.300000e-02, 2.980000e-02, 3.800000e-02, 4.800000e-02, 6.000000e-02, 7.390000e-02,
            9.098000e-02, 1.126000e-01, 1.390200e-01, 1.693000e-01, 2.080200e-01, 2.586000e-01,
            3.230000e-01, 4.073000e-01, 5.030000e-01, 6.082000e-01, 7.100000e-01, 7.932000e-01,
            8.620000e-01, 9.148501e-01, 9.540000e-01, 9.803000e-01, 9.949501e-01, 1.000000e+00,
            9.950000e-01, 9.786000e-01, 9.520000e-01, 9.154000e-01, 8.700000e-01, 8.163000e-01,
            7.570000e-01, 6.949000e-01, 6.310000e-01, 5.668000e-01, 5.030000e-01, 4.412000e-01,
            3.810000e-01, 3.210000e-01, 2.650000e-01, 2.170000e-01, 1.750000e-01, 1.382000e-01,
            1.070000e-01, 8.160000e-02, 6.100000e-02, 4.458000e-02, 3.200000e-02, 2.320000e-02,
            1.700000e-02, 1.192000e-02, 8.210000e-03, 5.723000e-03, 4.102000e-03, 2.929000e-03,
            2.091000e-03, 1.484000e-03, 1.047000e-03, 7.400000e-04, 5.200000e-04, 3.611000e-04,
            2.492000e-04, 1.719000e-04, 1.200000e-04, 8.480000e-05, 6.000000e-05, 4.240000e-05,
            3.000000e-05, 2.120000e-05, 1.499000e-05,
        };

        private static readonly double[] Cmf1931Z =
        {
            6.450001e-03, 1.054999e-02, 2.005001e-02, 3.621000e-02, 6.785001e-02, 1.102000e-01,
            2.074000e-01, 3.713000e-01, 6.456000e-01, 1.039050e+00, 1.385600e+00, 1.622960e+00,
            1.747060e+00, 1.782600e+00, 1.772110e+00, 1.744100e+00, 1.669200e+00, 1.528100e+00,
            1.287640e+00, 1.041900e+00, 8.129501e-01, 6.162000e-01, 4.651800e-01, 3.533000e-01,
            2.720000e-01, 2.123000e-01, 1.582000e-01, 1.117000e-01, 7.824999e-02, 5.725001e-02,
            4.216000e-02, 2.984000e-02, 2.030000e-02, 1.340000e-02, 8.749999e-03, 5.749999e-03,
            3.900000e-03, 2.749999e-03, 2.100000e-03, 1.800000e-03, 1.650001e-03, 1.400000e-03,
            1.100000e-03, 1.000000e-03, 8.000000e-04, 6.000000e-04, 3.400000e-04, 2.400000e-04,
            1.900000e-04, 1.000000e-04, 4.999999e-05, 3.000000e-05, 2.000000e-05, 1.000000e-05,
            0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00,
            0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00,
            0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00,
            0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00, 0.000000e+00,
            0.000000e+00, 0.000000e+00, 0.000000e+00,
        };

        /// <summary>
        /// Planckian locus sampled in CCT (1000–25000 K, ~1% log steps) with chromaticity and
        /// CIE 1960 (u,v). Lazily built once from CIE 1931 CMFs + Planck's law.
        /// </summary>
        private sealed class PlanckianLocus
        {
            public readonly double[] T;
            public readonly double[] X;
            public readonly double[] Y;
            public readonly double[] U;
            public readonly double[] V;
            private readonly double[] _logT;

            private PlanckianLocus(double[] t, double[] x, double[] y, double[] u, double[] v)
            {
                T = t; X = x; Y = y; U = u; V = v;
                _logT = new double[t.Length];
                for (int i = 0; i < t.Length; i++) _logT[i] = Math.Log(t[i]);
            }

            private static readonly Lazy<PlanckianLocus?> Lazy = new(Build);
            public static PlanckianLocus? Instance => Lazy.Value;

            private static PlanckianLocus? Build()
            {
                try
                {
                    var ts = new List<double>();
                    for (double t = PlanckianMinKelvin; ; t *= 1.01)
                    {
                        ts.Add(t);
                        if (t >= PlanckianMaxKelvin) break;
                    }

                    int n = ts.Count;
                    var T = new double[n];
                    var X = new double[n];
                    var Y = new double[n];
                    var U = new double[n];
                    var V = new double[n];

                    for (int i = 0; i < n; i++)
                    {
                        double t = ts[i];
                        double sx = 0, sy = 0, sz = 0;
                        for (int k = 0; k < Cmf1931Y.Length; k++)
                        {
                            double lambdaM = (CmfStartNm + CmfStepNm * k) * 1e-9;
                            // Planck spectral radiance shape (c1 cancels in chromaticity).
                            double m = 1.0 / (Math.Pow(lambdaM, 5.0) * (Math.Exp(PlanckC2 / (lambdaM * t)) - 1.0));
                            sx += m * Cmf1931X[k];
                            sy += m * Cmf1931Y[k];
                            sz += m * Cmf1931Z[k];
                        }
                        double sum = sx + sy + sz;
                        double x = sx / sum, y = sy / sum;
                        double denom = -2.0 * x + 12.0 * y + 3.0;
                        T[i] = t; X[i] = x; Y[i] = y;
                        U[i] = 4.0 * x / denom;
                        V[i] = 6.0 * y / denom;
                    }

                    return new PlanckianLocus(T, X, Y, U, V);
                }
                catch
                {
                    return null;
                }
            }

            /// <summary>Chromaticity at a CCT, linear in log-CCT between table neighbours.</summary>
            public Chromaticity Interpolate(double cct)
            {
                double lt = Math.Log(cct);
                if (lt <= _logT[0]) return new Chromaticity(X[0], Y[0]);
                int last = T.Length - 1;
                if (lt >= _logT[last]) return new Chromaticity(X[last], Y[last]);

                int lo = 0, hi = last;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    if (_logT[mid] <= lt) lo = mid; else hi = mid;
                }
                double f = (lt - _logT[lo]) / (_logT[hi] - _logT[lo]);
                return new Chromaticity(X[lo] + f * (X[hi] - X[lo]), Y[lo] + f * (Y[hi] - Y[lo]));
            }
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

            return TriangleCoverage(
                new List<(double X, double Y)> { (red.X, red.Y), (green.X, green.Y), (blue.X, blue.Y) },
                new List<(double X, double Y)> { (refRed.X, refRed.Y), (refGreen.X, refGreen.Y), (refBlue.X, refBlue.Y) });
        }

        /// <summary>
        /// As <see cref="GamutCoverage(Chromaticity, Chromaticity, Chromaticity)"/> but computed in
        /// the perceptually-uniform CIE 1976 u′v′ plane (u′=4x/(-2x+12y+3), v′=9y/(-2x+12y+3))
        /// instead of raw CIE xy. u′v′ coverage is the figure most colour standards quote because
        /// area there is a better proxy for perceived gamut volume; it differs numerically from the
        /// xy figure for the same primaries.
        /// </summary>
        public static double GamutCoverageUv(Chromaticity red, Chromaticity green, Chromaticity blue)
            => GamutCoverageUv(red, green, blue, SrgbRedPrimary, SrgbGreenPrimary, SrgbBluePrimary);

        /// <summary>
        /// Fraction of the reference gamut triangle covered by the measured gamut triangle, both
        /// mapped into CIE 1976 u′v′ and clipped with the same Sutherland–Hodgman routine as the
        /// xy overload.
        /// </summary>
        public static double GamutCoverageUv(
            Chromaticity red, Chromaticity green, Chromaticity blue,
            Chromaticity refRed, Chromaticity refGreen, Chromaticity refBlue)
        {
            if (!IsPlausibleChromaticity(red) || !IsPlausibleChromaticity(green) ||
                !IsPlausibleChromaticity(blue) || !IsPlausibleChromaticity(refRed) ||
                !IsPlausibleChromaticity(refGreen) || !IsPlausibleChromaticity(refBlue))
            {
                return 0.0;
            }

            return TriangleCoverage(
                new List<(double X, double Y)> { ToUvPrime(red), ToUvPrime(green), ToUvPrime(blue) },
                new List<(double X, double Y)> { ToUvPrime(refRed), ToUvPrime(refGreen), ToUvPrime(refBlue) });
        }

        /// <summary>CIE 1976 u′v′ coordinates from CIE xy chromaticity.</summary>
        private static (double X, double Y) ToUvPrime(Chromaticity xy)
        {
            double denom = -2.0 * xy.X + 12.0 * xy.Y + 3.0;
            if (Math.Abs(denom) < 1e-12) return (double.NaN, double.NaN);
            return (4.0 * xy.X / denom, 9.0 * xy.Y / denom);
        }

        /// <summary>
        /// area(subject ∩ clip) / area(clip) for two triangles given as vertex lists in some 2D
        /// coordinate plane (xy or u′v′). Winding order does not matter. Returns 0 for degenerate
        /// or non-finite triangles.
        /// </summary>
        private static double TriangleCoverage(List<(double X, double Y)> subjectPts, List<(double X, double Y)> clipPts)
        {
            foreach (var p in subjectPts)
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return 0.0;
            foreach (var p in clipPts)
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return 0.0;

            var subject = NormalizeWinding(subjectPts);
            var clip = NormalizeWinding(clipPts);

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
