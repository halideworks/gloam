using System;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// CAM16 colour appearance model (Li et al. 2017, "Comprehensive color solutions:
    /// CAM16, CAT16, and CAM16-UCS") reduced to the CAM16-UCS J′a′b′ coordinates and their
    /// Euclidean colour difference ΔE′. This is the perceptual yardstick roadmap 3.2 uses
    /// to pick the LEAST VISIBLE (CCT, brightness) operating point meeting a melanopic
    /// ceiling: unlike ΔE ITP it models the viewing-condition-dependent appearance
    /// (adapting luminance, surround), which matters when the candidate states differ in
    /// brightness, not just chromaticity.
    /// </summary>
    /// <remarks>
    /// Only the forward model is implemented (XYZ → J′a′b′); the solver never needs the
    /// inverse. Viewing conditions default to a dim-surround desktop at night: the
    /// adapting field is taken as 20% of the reference white's luminance (the gray-world
    /// convention), background Yb = 20.
    /// </remarks>
    public static class Cam16Ucs
    {
        public sealed record ViewingConditions(
            CieXyz WhitePoint,          // reference white, absolute (Y in cd/m²)
            double AdaptingLuminanceCdM2,  // L_A
            double BackgroundYRelative = 20.0, // Y_b (relative to white's Y = 100)
            Surround Surround = Surround.Dim);

        public enum Surround { Average, Dim, Dark }

        /// <summary>CAM16-UCS coordinates (J′ lightness, a′/b′ opponent axes).</summary>
        public readonly record struct JabPrime(double J, double A, double B);

        /// <summary>
        /// Standard desktop-at-night conditions for a display white of the given absolute
        /// luminance: L_A = Y_white/5 (gray-world), dim surround.
        /// </summary>
        public static ViewingConditions DisplayConditions(CieXyz whitePoint)
            => new(whitePoint, Math.Max(whitePoint.Y, 1e-6) / 5.0);

        private static (double F, double c, double Nc) SurroundParams(Surround s) => s switch
        {
            Surround.Average => (1.0, 0.69, 1.0),
            Surround.Dim => (0.9, 0.59, 0.9),
            _ => (0.8, 0.525, 0.8),
        };

        /// <summary>
        /// Forward CAM16 to UCS J′a′b′. <paramref name="xyz"/> must be in the same absolute
        /// scale as the conditions' white point.
        /// </summary>
        public static JabPrime ToJabPrime(CieXyz xyz, ViewingConditions vc)
        {
            ArgumentNullException.ThrowIfNull(vc);
            var white = vc.WhitePoint;
            if (white.Y <= 0) throw new ArgumentException("White point luminance must be positive.", nameof(vc));

            // Work in the conventional Y_white = 100 relative scale.
            double scale = 100.0 / white.Y;
            double x = xyz.X * scale;
            double y = xyz.Y * scale;
            double zInput = xyz.Z * scale;
            double xw = white.X * scale;
            double yw = white.Y * scale;
            double zw = white.Z * scale;

            var (f, c, nc) = SurroundParams(vc.Surround);
            double la = Math.Max(vc.AdaptingLuminanceCdM2, 1e-6);
            double yb = Math.Clamp(vc.BackgroundYRelative, 1e-6, 100.0);

            // Degree of adaptation.
            double d = Math.Clamp(f * (1.0 - (1.0 / 3.6) * Math.Exp((-la - 42.0) / 92.0)), 0.0, 1.0);

            // Luminance-level adaptation factors.
            double k = 1.0 / (5.0 * la + 1.0);
            double k4 = k * k * k * k;
            double fl = k4 * la + 0.1 * (1.0 - k4) * (1.0 - k4) * Math.Cbrt(5.0 * la);
            double n = yb / 100.0;
            double z = 1.48 + Math.Sqrt(n);
            double nbb = 0.725 * Math.Pow(1.0 / n, 0.2);
            double ncb = nbb;

            // CAT16 cone responses. Keep these scalar: this routine sits inside every
            // perceptual compiler and fade sample, and allocating nine three-element arrays
            // per call creates far more GC work than useful arithmetic on x64.
            double r = 0.401288 * x + 0.650173 * y - 0.051461 * zInput;
            double g = -0.250268 * x + 1.204414 * y + 0.045854 * zInput;
            double bCone = -0.002079 * x + 0.048952 * y + 0.953127 * zInput;
            double rw = 0.401288 * xw + 0.650173 * yw - 0.051461 * zw;
            double gw = -0.250268 * xw + 1.204414 * yw + 0.045854 * zw;
            double bw = -0.002079 * xw + 0.048952 * yw + 0.953127 * zw;

            // Von Kries with degree of adaptation, gain referenced to the white.
            double dr = d * (100.0 / rw) + 1.0 - d;
            double dg = d * (100.0 / gw) + 1.0 - d;
            double db = d * (100.0 / bw) + 1.0 - d;
            double rc = r * dr;
            double gc = g * dg;
            double bc = bCone * db;
            double rcw = rw * dr;
            double gcw = gw * dg;
            double bcw = bw * db;

            // Post-adaptation nonlinear compression.
            double ra = Compress(rc, fl);
            double ga = Compress(gc, fl);
            double ba = Compress(bc, fl);
            double raw = Compress(rcw, fl);
            double gaw = Compress(gcw, fl);
            double baw = Compress(bcw, fl);

            // Opponent dimensions.
            double a = ra - 12.0 * ga / 11.0 + ba / 11.0;
            double b = (ra + ga - 2.0 * ba) / 9.0;
            double hRad = Math.Atan2(b, a);
            double hDeg = hRad * 180.0 / Math.PI;
            if (hDeg < 0) hDeg += 360.0;

            // Achromatic responses.
            double aResp = (2.0 * ra + ga + ba / 20.0 - 0.305) * nbb;
            double awResp = (2.0 * raw + gaw + baw / 20.0 - 0.305) * nbb;

            // Lightness J.
            double j = aResp <= 0
                ? 0.0
                : 100.0 * Math.Pow(aResp / awResp, c * z);

            // Eccentricity and chroma.
            double hPrimeRad = (hDeg < 20.14 ? hDeg + 360.0 : hDeg) * Math.PI / 180.0;
            double et = 0.25 * (Math.Cos(hPrimeRad + 2.0) + 3.8);
            double t = (50000.0 / 13.0) * nc * ncb * et * Math.Sqrt(a * a + b * b)
                       / (ra + ga + 21.0 * ba / 20.0 + 1e-12);
            double chroma = Math.Pow(t, 0.9) * Math.Sqrt(j / 100.0)
                            * Math.Pow(1.64 - Math.Pow(0.29, n), 0.73);

            // Colourfulness M, then the UCS mapping (Li et al. 2017 eq. for CAM16-UCS).
            double m = chroma * Math.Pow(fl, 0.25);
            double jPrime = 1.7 * j / (1.0 + 0.007 * j);
            double mPrime = m <= 0 ? 0.0 : Math.Log(1.0 + 0.0228 * m) / 0.0228;

            return new JabPrime(
                jPrime,
                mPrime * Math.Cos(hRad),
                mPrime * Math.Sin(hRad));
        }

        /// <summary>Euclidean ΔE′ in CAM16-UCS (~1 unit ≈ 1 JND under the model's conditions).</summary>
        public static double DeltaEPrime(JabPrime x, JabPrime y)
        {
            double dj = x.J - y.J;
            double da = x.A - y.A;
            double db = x.B - y.B;
            return Math.Sqrt(dj * dj + da * da + db * db);
        }

        /// <summary>Convenience: ΔE′ between two absolute XYZ states under shared conditions.</summary>
        public static double DeltaEPrime(CieXyz a, CieXyz b, ViewingConditions vc)
            => DeltaEPrime(ToJabPrime(a, vc), ToJabPrime(b, vc));

        private static double Compress(double component, double fl)
        {
            // Sign-preserving post-adaptation compression (components can be negative for
            // out-of-gamut stimuli).
            double abs = Math.Abs(component);
            double p = Math.Pow(fl * abs / 100.0, 0.42);
            double v = 400.0 * p / (p + 27.13) + 0.1;
            return component < 0 ? -(v - 0.1) + 0.1 : v;
        }
    }
}
