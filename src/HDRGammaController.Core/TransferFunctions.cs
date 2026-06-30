using System;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Electro-optical and opto-electronic transfer functions for HDR and SDR signals.
    /// Implements SMPTE ST 2084 (PQ) and IEC 61966-2-1 (sRGB) standards.
    /// </summary>
    /// <remarks>
    /// References:
    /// - SMPTE ST 2084:2014 - High Dynamic Range Electro-Optical Transfer Function (PQ)
    /// - IEC 61966-2-1:1999 - sRGB colour space
    /// - ITU-R BT.1886 - Reference electro-optical transfer function for flat panel displays
    /// - ITU-R BT.2100-2 - HDR television
    /// </remarks>
    public static class TransferFunctions
    {
        // ST.2084 (PQ) Constants as defined in SMPTE ST 2084:2014
        // These constants define the Perceptual Quantizer transfer function
        // which is optimized for human visual perception of luminance

        /// <summary>m1 = 2610/16384 = 0.1593017578125</summary>
        private const double M1 = 2610.0 / 4096.0 / 4.0;

        /// <summary>m2 = 2523/4096 * 128 = 78.84375</summary>
        private const double M2 = 2523.0 / 4096.0 * 128.0;

        /// <summary>c1 = c3 - c2 + 1 = 3424/4096 = 0.8359375</summary>
        private const double C1 = 3424.0 / 4096.0;

        /// <summary>c2 = 2413/4096 * 32 = 18.8515625</summary>
        private const double C2 = 2413.0 / 4096.0 * 32.0;

        /// <summary>c3 = 2392/4096 * 32 = 18.6875</summary>
        private const double C3 = 2392.0 / 4096.0 * 32.0;

        /// <summary>
        /// ST.2084 PQ EOTF (Electro-Optical Transfer Function):
        /// Converts normalized PQ signal [0-1] to linear luminance in nits [0-10000].
        /// </summary>
        /// <remarks>
        /// The PQ EOTF is designed to match the human visual system's perception of light.
        /// It can represent luminance from 0 to 10,000 cd/m² (nits) with perceptually
        /// uniform quantization.
        ///
        /// Formula: L = ((max(N^(1/m2) - c1, 0)) / (c2 - c3 * N^(1/m2)))^(1/m1) * 10000
        /// where N is the normalized signal value [0,1]
        /// </remarks>
        /// <param name="signal">Normalized PQ signal value [0.0 - 1.0]</param>
        /// <returns>Linear luminance in nits [0 - 10000]</returns>
        public static double PqEotf(double signal)
        {
            // Clamp input
            signal = ClampFinite(signal, 0.0, 1.0, 0.0);

            // N = signal ^ (1/m2)
            double N = Math.Pow(signal, 1.0 / M2);

            // L = (max(0, N - c1) / (c2 - c3 * N)) ^ (1/m1)
            double numerator = Math.Max(0, N - C1);
            double denominator = C2 - C3 * N;

            // Avoid division by zero
            if (denominator == 0) return 10000.0; // Should practically not happen for valid range

            double L = Math.Pow(numerator / denominator, 1.0 / M1);

            return L * 10000.0;
        }

        /// <summary>
        /// ST.2084 PQ Inverse EOTF (OETF): Converts linear luminance in nits [0-10000]
        /// to normalized PQ signal [0-1].
        /// </summary>
        /// <remarks>
        /// This is the inverse of PqEotf, used for encoding linear light values
        /// into the PQ domain for display or storage.
        ///
        /// Formula: N = ((c1 + c2 * L^m1) / (1 + c3 * L^m1))^m2
        /// where L = nits / 10000
        /// </remarks>
        /// <param name="nits">Linear luminance in nits [0 - 10000]</param>
        /// <returns>Normalized PQ signal value [0.0 - 1.0]</returns>
        public static double PqInverseEotf(double nits)
        {
            // Clamp input
            nits = ClampFinite(nits, 0.0, 10000.0, 0.0);
            if (nits <= 0.0) return 0.0;
            
            double L = nits / 10000.0;

            // Y = ( (c1 + c2 * L^m1) / (1 + c3 * L^m1) )^m2
            double Lm1 = Math.Pow(L, M1);
            
            double num = C1 + C2 * Lm1;
            double den = 1.0 + C3 * Lm1;

            double N = Math.Pow(num / den, M2);
            
            return N;
        }

        /// <summary>
        /// IEC 61966-2-1 sRGB Inverse EOTF: Converts linear light (nits) to sRGB signal [0-1].
        /// </summary>
        /// <remarks>
        /// Implements the sRGB encoding transfer function with piecewise linear and
        /// power-law segments as defined in IEC 61966-2-1:1999.
        ///
        /// The function has two segments:
        /// - Linear: For L ≤ 0.0031308, S = 12.92 * L
        /// - Power:  For L > 0.0031308, S = 1.055 * L^(1/2.4) - 0.055
        ///
        /// The threshold 0.0031308 ensures continuity between the two segments.
        /// </remarks>
        /// <param name="linearNits">Absolute linear brightness in nits</param>
        /// <param name="whiteLevel">SDR reference white level in nits (e.g., 80, 200)</param>
        /// <param name="blackLevel">Black level in nits (default: 0)</param>
        /// <returns>Normalized sRGB signal value [0.0 - 1.0]</returns>
        public static double SrgbInverseEotf(double linearNits, double whiteLevel, double blackLevel = 0.0)
        {
            linearNits = double.IsFinite(linearNits) ? linearNits : 0.0;
            whiteLevel = double.IsFinite(whiteLevel) ? Math.Max(whiteLevel, 0.0) : 0.0;
            blackLevel = ClampFinite(blackLevel, 0.0, Math.Max(whiteLevel - 1e-12, 0.0), 0.0);

            if (whiteLevel <= blackLevel) return 0.0;

            // Normalize linear light to [0, 1]
            double linear = (linearNits - blackLevel) / (whiteLevel - blackLevel);
            return SrgbOetf(linear);
        }

        /// <summary>
        /// IEC 61966-2-1 sRGB OETF: encodes a normalized linear value [0,1] to sRGB signal [0,1].
        /// </summary>
        public static double SrgbOetf(double linear)
        {
            linear = ClampFinite(linear, 0.0, 1.0, 0.0);
            if (linear <= 0.0031308)
            {
                return 12.92 * linear;
            }
            return 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        }

        /// <summary>
        /// IEC 61966-2-1 sRGB EOTF: decodes sRGB signal [0,1] to normalized linear value [0,1].
        /// Inverse of <see cref="SrgbOetf"/>.
        /// </summary>
        public static double SrgbEotf(double signal)
        {
            signal = ClampFinite(signal, 0.0, 1.0, 0.0);
            if (signal <= 0.04045)
            {
                return signal / 12.92;
            }
            return Math.Pow((signal + 0.055) / 1.055, 2.4);
        }

        /// <summary>
        /// ITU-R BT.1886 EOTF. Converts normalized video signal [0,1] to absolute
        /// luminance using the display/reference white and black levels.
        /// </summary>
        public static double Bt1886Eotf(double signal, double whiteLevel, double blackLevel = 0.0)
        {
            const double gamma = 2.4;

            signal = ClampFinite(signal, 0.0, 1.0, 0.0);
            whiteLevel = double.IsFinite(whiteLevel) ? Math.Max(whiteLevel, 0.0) : 0.0;
            blackLevel = ClampFinite(blackLevel, 0.0, Math.Max(whiteLevel - 1e-12, 0.0), 0.0);

            if (whiteLevel <= 0)
                return 0.0;
            if (blackLevel <= 0)
                return whiteLevel * Math.Pow(signal, gamma);

            double whiteRoot = Math.Pow(whiteLevel, 1.0 / gamma);
            double blackRoot = Math.Pow(blackLevel, 1.0 / gamma);
            double denominator = whiteRoot - blackRoot;
            if (denominator <= 1e-12)
                return whiteLevel * Math.Pow(signal, gamma);

            double a = Math.Pow(denominator, gamma);
            double b = blackRoot / denominator;
            return a * Math.Pow(signal + b, gamma);
        }

        /// <summary>
        /// Inverse ITU-R BT.1886 EOTF. Converts absolute luminance to normalized
        /// video signal [0,1] for the specified white and black levels.
        /// </summary>
        public static double Bt1886InverseEotf(double luminance, double whiteLevel, double blackLevel = 0.0)
        {
            const double gamma = 2.4;

            whiteLevel = double.IsFinite(whiteLevel) ? Math.Max(whiteLevel, 0.0) : 0.0;
            blackLevel = ClampFinite(blackLevel, 0.0, Math.Max(whiteLevel - 1e-12, 0.0), 0.0);
            luminance = ClampFinite(luminance, blackLevel, whiteLevel, blackLevel);

            if (whiteLevel <= 0)
                return 0.0;
            if (blackLevel <= 0)
                return Math.Pow(luminance / whiteLevel, 1.0 / gamma);

            double whiteRoot = Math.Pow(whiteLevel, 1.0 / gamma);
            double blackRoot = Math.Pow(blackLevel, 1.0 / gamma);
            double denominator = whiteRoot - blackRoot;
            if (denominator <= 1e-12)
                return Math.Pow(luminance / whiteLevel, 1.0 / gamma);

            double a = Math.Pow(denominator, gamma);
            double b = blackRoot / denominator;
            return ClampFinite(Math.Pow(luminance / a, 1.0 / gamma) - b, 0.0, 1.0, 0.0);
        }

        private static double ClampFinite(double value, double min, double max, double fallback)
        {
            if (!double.IsFinite(value))
                return fallback;

            if (max < min)
                return fallback;

            return Math.Clamp(value, min, max);
        }
    }
}
