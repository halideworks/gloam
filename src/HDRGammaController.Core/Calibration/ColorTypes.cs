using System;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// CIE 1931 xy chromaticity coordinates.
    /// Represents a color's hue and saturation independent of luminance.
    /// </summary>
    /// <remarks>
    /// Chromaticity coordinates are derived from CIE XYZ tristimulus values:
    /// x = X / (X + Y + Z)
    /// y = Y / (X + Y + Z)
    ///
    /// The third coordinate z = 1 - x - y is implicit.
    ///
    /// Reference: CIE 015:2018 - Colorimetry, 4th Edition
    /// </remarks>
    public readonly struct Chromaticity : IEquatable<Chromaticity>
    {
        /// <summary>CIE x coordinate (0.0 to ~0.8)</summary>
        public double X { get; }

        /// <summary>CIE y coordinate (0.0 to ~0.9)</summary>
        public double Y { get; }

        /// <summary>Implicit z coordinate: z = 1 - x - y</summary>
        public double Z => 1.0 - X - Y;

        public Chromaticity(double x, double y)
        {
            X = x;
            Y = y;
        }

        /// <summary>
        /// Calculates the Euclidean distance between two chromaticity coordinates.
        /// </summary>
        public double DistanceTo(Chromaticity other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Converts to CIE XYZ with specified luminance Y.
        /// </summary>
        /// <param name="luminance">The Y (luminance) value in cd/m² or normalized</param>
        public CieXyz ToXyz(double luminance)
        {
            if (Y <= 0) return new CieXyz(0, 0, 0);

            double bigX = (X * luminance) / Y;
            double bigY = luminance;
            double bigZ = (Z * luminance) / Y;

            return new CieXyz(bigX, bigY, bigZ);
        }

        // Standard illuminants and color space primaries

        /// <summary>CIE Standard Illuminant D65 (daylight, ~6500K)</summary>
        public static readonly Chromaticity D65 = new(0.31272, 0.32903);

        /// <summary>CIE Standard Illuminant D50 (horizon light, ~5000K)</summary>
        public static readonly Chromaticity D50 = new(0.34567, 0.35850);

        /// <summary>CIE Standard Illuminant A (incandescent, ~2856K)</summary>
        public static readonly Chromaticity IlluminantA = new(0.44758, 0.40745);

        // ITU-R BT.709 / sRGB primaries
        /// <summary>sRGB/Rec.709 Red primary</summary>
        public static readonly Chromaticity Rec709Red = new(0.64, 0.33);
        /// <summary>sRGB/Rec.709 Green primary</summary>
        public static readonly Chromaticity Rec709Green = new(0.30, 0.60);
        /// <summary>sRGB/Rec.709 Blue primary</summary>
        public static readonly Chromaticity Rec709Blue = new(0.15, 0.06);

        // ITU-R BT.2020 primaries
        /// <summary>Rec.2020 Red primary</summary>
        public static readonly Chromaticity Rec2020Red = new(0.708, 0.292);
        /// <summary>Rec.2020 Green primary</summary>
        public static readonly Chromaticity Rec2020Green = new(0.170, 0.797);
        /// <summary>Rec.2020 Blue primary</summary>
        public static readonly Chromaticity Rec2020Blue = new(0.131, 0.046);

        // DCI-P3 primaries
        /// <summary>DCI-P3 Red primary</summary>
        public static readonly Chromaticity P3Red = new(0.680, 0.320);
        /// <summary>DCI-P3 Green primary</summary>
        public static readonly Chromaticity P3Green = new(0.265, 0.690);
        /// <summary>DCI-P3 Blue primary</summary>
        public static readonly Chromaticity P3Blue = new(0.150, 0.060);

        public bool Equals(Chromaticity other) =>
            Math.Abs(X - other.X) < 1e-10 && Math.Abs(Y - other.Y) < 1e-10;

        public override bool Equals(object? obj) => obj is Chromaticity other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public static bool operator ==(Chromaticity left, Chromaticity right) => left.Equals(right);
        public static bool operator !=(Chromaticity left, Chromaticity right) => !left.Equals(right);

        public override string ToString() => $"xy({X:F5}, {Y:F5})";
    }

    /// <summary>
    /// CIE 1931 XYZ tristimulus values.
    /// The fundamental device-independent color representation.
    /// </summary>
    /// <remarks>
    /// XYZ is the master color space from which all other spaces derive.
    /// Y represents luminance (in cd/m² when absolute, or normalized 0-100).
    /// X and Z are non-physical "super-saturated" primaries chosen for
    /// computational convenience (all visible colors have positive XYZ).
    ///
    /// Reference: CIE 015:2018 - Colorimetry, 4th Edition
    /// </remarks>
    public readonly struct CieXyz : IEquatable<CieXyz>
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public CieXyz(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>
        /// Luminance value (same as Y component).
        /// </summary>
        public double Luminance => Y;

        /// <summary>
        /// Converts XYZ to chromaticity coordinates.
        /// </summary>
        public Chromaticity ToChromaticity()
        {
            double sum = X + Y + Z;
            if (sum <= 0) return new Chromaticity(0.31272, 0.32903); // D65 default
            return new Chromaticity(X / sum, Y / sum);
        }

        /// <summary>
        /// Scales the XYZ values by a factor (for luminance adjustment).
        /// </summary>
        public CieXyz Scale(double factor) => new(X * factor, Y * factor, Z * factor);

        /// <summary>
        /// Returns the Euclidean distance between two XYZ values.
        /// Note: For perceptual color difference, use Lab and Delta E instead.
        /// </summary>
        public double DistanceTo(CieXyz other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            double dz = Z - other.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static CieXyz operator +(CieXyz a, CieXyz b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static CieXyz operator -(CieXyz a, CieXyz b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static CieXyz operator *(CieXyz a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public static CieXyz operator *(double s, CieXyz a) => a * s;

        public bool Equals(CieXyz other) =>
            Math.Abs(X - other.X) < 1e-10 &&
            Math.Abs(Y - other.Y) < 1e-10 &&
            Math.Abs(Z - other.Z) < 1e-10;

        public override bool Equals(object? obj) => obj is CieXyz other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public static bool operator ==(CieXyz left, CieXyz right) => left.Equals(right);
        public static bool operator !=(CieXyz left, CieXyz right) => !left.Equals(right);

        public override string ToString() => $"XYZ({X:F4}, {Y:F4}, {Z:F4})";
    }

    /// <summary>
    /// CIE 1976 L*a*b* color space (CIELAB).
    /// A perceptually uniform color space for color difference calculations.
    /// </summary>
    /// <remarks>
    /// Lab is designed so that equal numerical changes correspond to roughly
    /// equal perceived color differences. This makes it ideal for:
    /// - Color difference calculations (Delta E)
    /// - Color interpolation
    /// - Gamut mapping
    ///
    /// L* = Lightness (0 = black, 100 = white)
    /// a* = Green-Red axis (negative = green, positive = red)
    /// b* = Blue-Yellow axis (negative = blue, positive = yellow)
    ///
    /// Reference: CIE 015:2018 - Colorimetry, 4th Edition, Section 8.2
    /// </remarks>
    public readonly struct CieLab : IEquatable<CieLab>
    {
        /// <summary>Lightness (0 to 100)</summary>
        public double L { get; }

        /// <summary>Green-Red axis (typically -128 to +128)</summary>
        public double A { get; }

        /// <summary>Blue-Yellow axis (typically -128 to +128)</summary>
        public double B { get; }

        public CieLab(double l, double a, double b)
        {
            L = l;
            A = a;
            B = b;
        }

        /// <summary>
        /// Chroma (saturation): C* = sqrt(a*² + b*²)
        /// </summary>
        public double Chroma => Math.Sqrt(A * A + B * B);

        /// <summary>
        /// Hue angle in degrees (0-360): h = atan2(b*, a*)
        /// </summary>
        public double HueAngle
        {
            get
            {
                double h = Math.Atan2(B, A) * (180.0 / Math.PI);
                return h < 0 ? h + 360.0 : h;
            }
        }

        /// <summary>
        /// CIE76 Delta E (Euclidean distance in Lab space).
        /// A Delta E of 1.0 is approximately the smallest perceptible difference.
        /// </summary>
        public double DeltaE76(CieLab other)
        {
            double dL = L - other.L;
            double dA = A - other.A;
            double dB = B - other.B;
            return Math.Sqrt(dL * dL + dA * dA + dB * dB);
        }

        /// <summary>
        /// CIE94 Delta E (improved perceptual uniformity).
        /// </summary>
        /// <param name="other">The color to compare against</param>
        /// <param name="textiles">If true, uses textile weighting factors; otherwise graphic arts</param>
        public double DeltaE94(CieLab other, bool textiles = false)
        {
            double kL = textiles ? 2.0 : 1.0;
            double k1 = textiles ? 0.048 : 0.045;
            double k2 = textiles ? 0.014 : 0.015;

            double dL = L - other.L;
            double c1 = Chroma;
            double c2 = other.Chroma;
            double dC = c1 - c2;

            double dA = A - other.A;
            double dB = B - other.B;
            double dH2 = dA * dA + dB * dB - dC * dC;
            double dH = dH2 > 0 ? Math.Sqrt(dH2) : 0;

            double sL = 1.0;
            double sC = 1.0 + k1 * c1;
            double sH = 1.0 + k2 * c1;

            double termL = dL / (kL * sL);
            double termC = dC / sC;
            double termH = dH / sH;

            return Math.Sqrt(termL * termL + termC * termC + termH * termH);
        }

        /// <summary>
        /// CIE2000 Delta E (most accurate perceptual color difference formula).
        /// This is the recommended formula for critical color evaluation.
        /// </summary>
        /// <remarks>
        /// Reference: CIE 142-2001 - Improvement to industrial colour-difference evaluation
        /// </remarks>
        public double DeltaE2000(CieLab other)
        {
            // Parametric weighting factors (standard values)
            const double kL = 1.0;
            const double kC = 1.0;
            const double kH = 1.0;

            double l1 = L, a1 = A, b1 = B;
            double l2 = other.L, a2 = other.A, b2 = other.B;

            double c1 = Math.Sqrt(a1 * a1 + b1 * b1);
            double c2 = Math.Sqrt(a2 * a2 + b2 * b2);
            double cBar = (c1 + c2) / 2.0;

            double cBar7 = Math.Pow(cBar, 7);
            double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + Math.Pow(25, 7))));

            double a1Prime = a1 * (1.0 + g);
            double a2Prime = a2 * (1.0 + g);

            double c1Prime = Math.Sqrt(a1Prime * a1Prime + b1 * b1);
            double c2Prime = Math.Sqrt(a2Prime * a2Prime + b2 * b2);

            double h1Prime = Atan2Degrees(b1, a1Prime);
            double h2Prime = Atan2Degrees(b2, a2Prime);

            double dLPrime = l2 - l1;
            double dCPrime = c2Prime - c1Prime;

            double dhPrime;
            if (c1Prime * c2Prime == 0)
                dhPrime = 0;
            else if (Math.Abs(h2Prime - h1Prime) <= 180)
                dhPrime = h2Prime - h1Prime;
            else if (h2Prime - h1Prime > 180)
                dhPrime = h2Prime - h1Prime - 360;
            else
                dhPrime = h2Prime - h1Prime + 360;

            double dHPrime = 2.0 * Math.Sqrt(c1Prime * c2Prime) *
                            Math.Sin(dhPrime * Math.PI / 360.0);

            double lBarPrime = (l1 + l2) / 2.0;
            double cBarPrime = (c1Prime + c2Prime) / 2.0;

            double hBarPrime;
            if (c1Prime * c2Prime == 0)
                hBarPrime = h1Prime + h2Prime;
            else if (Math.Abs(h1Prime - h2Prime) <= 180)
                hBarPrime = (h1Prime + h2Prime) / 2.0;
            else if (h1Prime + h2Prime < 360)
                hBarPrime = (h1Prime + h2Prime + 360) / 2.0;
            else
                hBarPrime = (h1Prime + h2Prime - 360) / 2.0;

            double t = 1.0 - 0.17 * Math.Cos((hBarPrime - 30) * Math.PI / 180.0)
                          + 0.24 * Math.Cos(2 * hBarPrime * Math.PI / 180.0)
                          + 0.32 * Math.Cos((3 * hBarPrime + 6) * Math.PI / 180.0)
                          - 0.20 * Math.Cos((4 * hBarPrime - 63) * Math.PI / 180.0);

            double lBarPrimeMinus50Sq = (lBarPrime - 50) * (lBarPrime - 50);
            double sL = 1.0 + (0.015 * lBarPrimeMinus50Sq) / Math.Sqrt(20 + lBarPrimeMinus50Sq);
            double sC = 1.0 + 0.045 * cBarPrime;
            double sH = 1.0 + 0.015 * cBarPrime * t;

            double cBarPrime7 = Math.Pow(cBarPrime, 7);
            double rC = 2.0 * Math.Sqrt(cBarPrime7 / (cBarPrime7 + Math.Pow(25, 7)));
            double dTheta = 30 * Math.Exp(-Math.Pow((hBarPrime - 275) / 25, 2));
            double rT = -Math.Sin(2 * dTheta * Math.PI / 180.0) * rC;

            double termL = dLPrime / (kL * sL);
            double termC = dCPrime / (kC * sC);
            double termH = dHPrime / (kH * sH);

            return Math.Sqrt(termL * termL + termC * termC + termH * termH + rT * termC * termH);
        }

        private static double Atan2Degrees(double y, double x)
        {
            double h = Math.Atan2(y, x) * (180.0 / Math.PI);
            return h < 0 ? h + 360.0 : h;
        }

        public bool Equals(CieLab other) =>
            Math.Abs(L - other.L) < 1e-10 &&
            Math.Abs(A - other.A) < 1e-10 &&
            Math.Abs(B - other.B) < 1e-10;

        public override bool Equals(object? obj) => obj is CieLab other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(L, A, B);
        public static bool operator ==(CieLab left, CieLab right) => left.Equals(right);
        public static bool operator !=(CieLab left, CieLab right) => !left.Equals(right);

        public override string ToString() => $"Lab({L:F2}, {A:F2}, {B:F2})";
    }

    /// <summary>
    /// Linear RGB values in a specified color space (sRGB, Rec.2020, etc.).
    /// Values are in linear light (no gamma), range 0.0 to 1.0 for in-gamut colors.
    /// </summary>
    public readonly struct LinearRgb : IEquatable<LinearRgb>
    {
        public double R { get; }
        public double G { get; }
        public double B { get; }

        public LinearRgb(double r, double g, double b)
        {
            R = r;
            G = g;
            B = b;
        }

        /// <summary>
        /// Returns true if all components are within the valid gamut [0, 1].
        /// </summary>
        public bool IsInGamut => R >= 0 && R <= 1 && G >= 0 && G <= 1 && B >= 0 && B <= 1;

        /// <summary>
        /// Clamps all components to [0, 1].
        /// </summary>
        public LinearRgb Clamp() => new(
            Math.Clamp(R, 0, 1),
            Math.Clamp(G, 0, 1),
            Math.Clamp(B, 0, 1));

        /// <summary>
        /// Scales all components by a factor.
        /// </summary>
        public LinearRgb Scale(double factor) => new(R * factor, G * factor, B * factor);

        /// <summary>
        /// Returns the maximum component value.
        /// </summary>
        public double Max => Math.Max(R, Math.Max(G, B));

        /// <summary>
        /// Returns the minimum component value.
        /// </summary>
        public double Min => Math.Min(R, Math.Min(G, B));

        public static LinearRgb operator +(LinearRgb a, LinearRgb b) => new(a.R + b.R, a.G + b.G, a.B + b.B);
        public static LinearRgb operator -(LinearRgb a, LinearRgb b) => new(a.R - b.R, a.G - b.G, a.B - b.B);
        public static LinearRgb operator *(LinearRgb a, double s) => new(a.R * s, a.G * s, a.B * s);
        public static LinearRgb operator *(double s, LinearRgb a) => a * s;

        public bool Equals(LinearRgb other) =>
            Math.Abs(R - other.R) < 1e-10 &&
            Math.Abs(G - other.G) < 1e-10 &&
            Math.Abs(B - other.B) < 1e-10;

        public override bool Equals(object? obj) => obj is LinearRgb other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(R, G, B);
        public static bool operator ==(LinearRgb left, LinearRgb right) => left.Equals(right);
        public static bool operator !=(LinearRgb left, LinearRgb right) => !left.Equals(right);

        public override string ToString() => $"RGB({R:F4}, {G:F4}, {B:F4})";
    }
}
