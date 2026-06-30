using System;
using System.Collections.Generic;
using System.Linq;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Generates a 3D color correction LUT from colorimeter measurements.
    /// </summary>
    /// <remarks>
    /// The generator builds a forward display model from measurements (RGB → XYZ),
    /// then creates a correction LUT that maps input RGB to corrected RGB such that
    /// the display produces the target color.
    ///
    /// Algorithm overview:
    /// 1. Analyze measurements to build a display characterization model
    /// 2. For each 3D LUT grid point:
    ///    a. Calculate target XYZ (what this RGB should produce per target color space)
    ///    b. Use inverse model to find corrected RGB that produces target XYZ
    /// 3. Apply gamut mapping for out-of-gamut colors
    ///
    /// References:
    /// - ICC.1:2022 - ICC Profile specification
    /// - Color Appearance Models (Fairchild)
    /// - Real-Time 3D LUT Color Correction (NVIDIA)
    /// </remarks>
    public class Lut3DGenerator
    {
        private readonly CalibrationTarget _target;
        private readonly IReadOnlyList<MeasurementResult> _measurements;
        private readonly int _lutSize;

        // Characterization data extracted from measurements
        private DisplayCharacterization? _characterization;

        /// <summary>
        /// Gets the generated display characterization from measurements.
        /// </summary>
        public DisplayCharacterization? Characterization => _characterization;

        /// <summary>
        /// Creates a new 3D LUT generator.
        /// </summary>
        /// <param name="target">The target color space to calibrate to</param>
        /// <param name="measurements">The colorimeter measurements</param>
        /// <param name="lutSize">LUT grid size (default 17 for 17x17x17)</param>
        public Lut3DGenerator(CalibrationTarget target, IReadOnlyList<MeasurementResult> measurements, int lutSize = 17)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            _measurements = measurements ?? throw new ArgumentNullException(nameof(measurements));
            _lutSize = Math.Clamp(lutSize, 9, 65);

            if (measurements.Count < 10)
                throw new ArgumentException("At least 10 measurements are required for calibration", nameof(measurements));
        }

        /// <summary>
        /// Generates the 3D correction LUT.
        /// </summary>
        /// <param name="progress">Optional progress callback (0-100)</param>
        /// <returns>The correction 3D LUT</returns>
        public Lut3D Generate(Action<double>? progress = null)
        {
            // Step 0: Reject obviously-broken measurement sets BEFORE building a profile
            // from them. Without this, a probe that connected but never actually read the
            // screen (ambient light, a sharing-violation returning stale/zero data) yields
            // a garbage LUT that the app would report as a successful calibration.
            progress?.Invoke(5);
            BuildCharacterizationOnly(hdrMode: false);

            // Step 2: Create the correction LUT
            progress?.Invoke(10);
            var lut = new Lut3D(_lutSize);

            int totalPoints = _lutSize * _lutSize * _lutSize;
            int processedPoints = 0;

            // For each LUT grid point, calculate the correction
            for (int ri = 0; ri < _lutSize; ri++)
            {
                double r = ri / (double)(_lutSize - 1);

                for (int gi = 0; gi < _lutSize; gi++)
                {
                    double g = gi / (double)(_lutSize - 1);

                    for (int bi = 0; bi < _lutSize; bi++)
                    {
                        double b = bi / (double)(_lutSize - 1);

                        // Calculate corrected RGB for this input
                        var inputRgb = new LinearRgb(r, g, b);
                        var correctedRgb = CalculateCorrection(inputRgb);

                        lut.SetEntry(ri, gi, bi,
                            (float)correctedRgb.R,
                            (float)correctedRgb.G,
                            (float)correctedRgb.B);

                        processedPoints++;
                    }
                }

                // Update progress (10% for characterization, 90% for LUT generation)
                progress?.Invoke(10 + (processedPoints * 90.0 / totalPoints));
            }

            progress?.Invoke(100);
            return lut;
        }

        /// <summary>
        /// Validates measurements and builds the display characterization without emitting a
        /// generic 3D LUT. HDR calibration uses this path because the installed correction is
        /// synthesized later as a Windows Advanced Color MHC2 profile from the measured PQ wire
        /// ladder, not from the SDR-oriented .cube correction model.
        /// </summary>
        public DisplayCharacterization BuildCharacterizationOnly(bool hdrMode = false)
        {
            var validation = CalibrationMeasurementValidator.ValidateForProfile(_measurements, _target, hdrMode);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.Error);

            _characterization = BuildCharacterization();
            return _characterization;
        }

        /// <summary>
        /// Builds a display characterization model from measurements.
        /// </summary>
        private DisplayCharacterization BuildCharacterization()
        {
            var char_ = new DisplayCharacterization();

            // Extract black and white points
            var blackMeasurement = FindMeasurementByRgb(0, 0, 0);
            var whiteMeasurement = FindMeasurementByRgb(1, 1, 1);

            char_.BlackXyz = blackMeasurement?.Xyz ?? new CieXyz(0, 0, 0);
            char_.WhiteXyz = whiteMeasurement?.Xyz ?? new CieXyz(0.95047, 1.0, 1.08883);
            char_.BlackLevel = char_.BlackXyz.Y;
            char_.PeakLuminance = char_.WhiteXyz.Y;

            // Extract primaries (100% saturated colors)
            var redMeasurement = FindMeasurementByRgb(1, 0, 0);
            var greenMeasurement = FindMeasurementByRgb(0, 1, 0);
            var blueMeasurement = FindMeasurementByRgb(0, 0, 1);

            char_.RedPrimary = redMeasurement?.Chromaticity ?? Chromaticity.Rec709Red;
            char_.GreenPrimary = greenMeasurement?.Chromaticity ?? Chromaticity.Rec709Green;
            char_.BluePrimary = blueMeasurement?.Chromaticity ?? Chromaticity.Rec709Blue;
            char_.WhitePoint = whiteMeasurement?.Chromaticity ?? Chromaticity.D65;

            // Build the tone response curve from grayscale measurements. Tone correction is
            // LUMINANCE-ONLY: only neutral (grayscale) patches are measured, and the curve's
            // output is total luminance Y, so a per-channel fit would produce three byte-for-byte
            // identical curves. We therefore fit a single shared luminance curve and reference it
            // from all three channels. Per-channel tracking (grayscale color cast) is NOT corrected
            // by the tone curves - only the RGB->XYZ matrix corrects white point and gamut.
            var luminanceToneCurve = ExtractToneCurve(PatchCategory.Grayscale, m => m.Patch.DisplayRgb.R);
            char_.RedToneCurve = luminanceToneCurve;
            char_.GreenToneCurve = luminanceToneCurve;
            char_.BlueToneCurve = luminanceToneCurve;

            // Calculate RGB to XYZ matrix from measured primaries
            char_.RgbToXyzMatrix = CalculateMeasuredMatrix(char_);

            // Calculate average gamma from grayscale
            char_.MeasuredGamma = CalculateAverageGamma();

            return char_;
        }

        /// <summary>
        /// Extracts a tone response curve from measurements.
        /// </summary>
        private ToneCurve ExtractToneCurve(PatchCategory category, Func<MeasurementResult, double> getChannelValue)
        {
            var grayscaleMeasurements = _measurements
                .Where(m => m.Patch.Category == category && m.IsValid)
                .OrderBy(m => getChannelValue(m))
                .ToList();

            if (grayscaleMeasurements.Count < 3)
            {
                // Fallback to 2.2 gamma curve
                return ToneCurve.CreateGamma(2.2);
            }

            // Build lookup table from measurements
            var points = new List<(double input, double output)>();

            // Anchor normalization to the measured black/white patches. Near-white
            // samples can legitimately land a percent or two above white from meter
            // noise, ABL/local-dimming behavior or settling; treating that sample as
            // reference white compresses the entire fitted curve.
            double whiteLuminance = FindMeasurementByRgb(1, 1, 1)?.Xyz.Y
                                    ?? grayscaleMeasurements.Max(m => m.Xyz.Y);
            if (whiteLuminance <= 0) whiteLuminance = 1;

            double blackLuminance = FindMeasurementByRgb(0, 0, 0)?.Xyz.Y
                                    ?? grayscaleMeasurements.Min(m => m.Xyz.Y);
            double luminanceRange = Math.Max(whiteLuminance - blackLuminance, 1e-6);

            foreach (var m in grayscaleMeasurements)
            {
                double inputLevel = getChannelValue(m);
                double normalizedLuminance = (m.Xyz.Y - blackLuminance) / luminanceRange;
                normalizedLuminance = Clamp01(normalizedLuminance);
                points.Add((inputLevel, normalizedLuminance));
            }

            return ToneCurve.CreateFromPoints(points);
        }

        /// <summary>
        /// Calculates the RGB to XYZ matrix from measured primaries.
        /// </summary>
        private double[,] CalculateMeasuredMatrix(DisplayCharacterization char_)
        {
            // Use ColorMath to calculate the matrix from measured chromaticities
            return ColorMath.CalculateRgbToXyzMatrix(
                char_.RedPrimary,
                char_.GreenPrimary,
                char_.BluePrimary,
                char_.WhitePoint);
        }

        /// <summary>
        /// Calculates average gamma from grayscale measurements.
        /// </summary>
        private double CalculateAverageGamma()
        {
            var grayscale = _measurements
                .Where(m => m.Patch.Category == PatchCategory.Grayscale && m.IsValid)
                .Where(m => m.Patch.DisplayRgb.R > 0.05 && m.Patch.DisplayRgb.R < 0.95) // Exclude extremes
                .ToList();

            if (grayscale.Count < 3)
                return 2.2; // Default

            double whiteLuminance = _measurements
                .Where(m => m.Patch.DisplayRgb.R >= 0.99 && m.Patch.DisplayRgb.G >= 0.99 && m.Patch.DisplayRgb.B >= 0.99)
                .Select(m => m.Xyz.Y)
                .FirstOrDefault();

            if (whiteLuminance <= 0) whiteLuminance = 1;

            double blackLuminance = _measurements
                .Where(m => m.Patch.DisplayRgb.R <= 0.01 && m.Patch.DisplayRgb.G <= 0.01 && m.Patch.DisplayRgb.B <= 0.01)
                .Select(m => m.Xyz.Y)
                .DefaultIfEmpty(0)
                .Min();

            double luminanceRange = Math.Max(whiteLuminance - blackLuminance, 1e-6);

            // Least-squares fit of gamma in log-log space. For a power law
            // output = input^gamma, taking logs gives ln(output) = gamma * ln(input),
            // a line through the origin whose best-fit slope is
            //   gamma = Σ(ln in * ln out) / Σ(ln in)^2.
            // This weights all points jointly instead of averaging per-point gamma
            // estimates (which over-weights the noisy near-black samples where small
            // luminance errors swing the ratio wildly).
            double sumXY = 0, sumXX = 0;
            int n = 0;
            foreach (var m in grayscale)
            {
                double input = m.Patch.DisplayRgb.R;
                double output = Clamp01((m.Xyz.Y - blackLuminance) / luminanceRange);

                if (output > 0 && input > 0)
                {
                    double lx = Math.Log(input);
                    double ly = Math.Log(output);
                    sumXY += lx * ly;
                    sumXX += lx * lx;
                    n++;
                }
            }

            if (n == 0 || sumXX <= 0) return 2.2;

            double gamma = sumXY / sumXX;
            // Clamp to a physically-plausible display range; outside it the fit is
            // dominated by bad data and the 2.2 default is safer.
            return (gamma > 1.5 && gamma < 3.5) ? gamma : 2.2;
        }

        /// <summary>
        /// Calculates the corrected RGB for an input RGB to achieve the target color.
        /// </summary>
        private LinearRgb CalculateCorrection(LinearRgb inputRgb)
        {
            if (_characterization == null)
                return inputRgb;

            // Step 1: Calculate target XYZ (what this input should produce)
            CieXyz targetXyz = CalculateTargetXyz(inputRgb);

            // Step 2: Use inverse display model to find RGB that produces target XYZ
            LinearRgb correctedRgb = InverseDisplayModel(targetXyz);

            // Step 3: Apply gamut mapping if needed
            correctedRgb = ApplyGamutMapping(correctedRgb);

            return correctedRgb;
        }

        /// <summary>
        /// Calculates what XYZ the target color space says this RGB should produce.
        /// </summary>
        private CieXyz CalculateTargetXyz(LinearRgb rgb)
        {
            // Apply target transfer function (decode to linear light)
            double linearR = _target.ApplyEotf(rgb.R);
            double linearG = _target.ApplyEotf(rgb.G);
            double linearB = _target.ApplyEotf(rgb.B);

            // Convert to XYZ using target color space matrix
            return _target.LinearRgbToXyz(new LinearRgb(linearR, linearG, linearB));
        }

        /// <summary>
        /// Applies the inverse display model to find RGB that produces the target XYZ.
        /// </summary>
        private LinearRgb InverseDisplayModel(CieXyz targetXyz)
        {
            if (_characterization == null)
                return new LinearRgb(0, 0, 0);

            // Step 1: Adapt XYZ from target white point to display white point if needed
            CieXyz adaptedXyz = targetXyz;
            if (_characterization.WhitePoint != _target.WhitePoint)
            {
                var targetWhiteXyz = _target.WhitePoint.ToXyz(1.0);
                var displayWhiteXyz = _characterization.WhitePoint.ToXyz(1.0);
                adaptedXyz = ColorMath.ChromaticAdaptation(targetXyz, targetWhiteXyz, displayWhiteXyz);
            }

            // Step 2: Convert XYZ to linear RGB using display's inverse matrix
            var displayXyzToRgb = ColorMath.Invert3x3(_characterization.RgbToXyzMatrix);
            double[] xyzVec = { adaptedXyz.X, adaptedXyz.Y, adaptedXyz.Z };
            double linearR = displayXyzToRgb[0, 0] * xyzVec[0] + displayXyzToRgb[0, 1] * xyzVec[1] + displayXyzToRgb[0, 2] * xyzVec[2];
            double linearG = displayXyzToRgb[1, 0] * xyzVec[0] + displayXyzToRgb[1, 1] * xyzVec[1] + displayXyzToRgb[1, 2] * xyzVec[2];
            double linearB = displayXyzToRgb[2, 0] * xyzVec[0] + displayXyzToRgb[2, 1] * xyzVec[1] + displayXyzToRgb[2, 2] * xyzVec[2];

            // Step 3: Apply inverse tone curves to get signal values
            double signalR = _characterization.RedToneCurve.InverseLookup(linearR);
            double signalG = _characterization.GreenToneCurve.InverseLookup(linearG);
            double signalB = _characterization.BlueToneCurve.InverseLookup(linearB);

            return new LinearRgb(signalR, signalG, signalB);
        }

        /// <summary>
        /// Applies gamut mapping for out-of-gamut colors.
        /// </summary>
        private LinearRgb ApplyGamutMapping(LinearRgb rgb)
        {
            return CompressToGamut(rgb);
        }

        /// <summary>
        /// Hue-preserving gamut compression: keep the corrected color on the same line from
        /// neutral gray to the requested RGB and reduce saturation until every channel fits.
        /// This avoids the hue skews caused by independent channel clipping.
        /// </summary>
        internal static LinearRgb CompressToGamut(LinearRgb rgb)
        {
            if (rgb.IsInGamut)
                return rgb;

            double gray = Clamp01(0.2126 * rgb.R + 0.7152 * rgb.G + 0.0722 * rgb.B);
            var neutral = new LinearRgb(gray, gray, gray);
            double lo = 0;
            double hi = 1;

            for (int i = 0; i < 32; i++)
            {
                double mid = (lo + hi) * 0.5;
                var candidate = neutral + (rgb - neutral) * mid;
                if (candidate.IsInGamut)
                    lo = mid;
                else
                    hi = mid;
            }

            var compressed = neutral + (rgb - neutral) * lo;
            return compressed.Clamp();
        }

        private static double Clamp01(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

        /// <summary>
        /// Finds a measurement matching the specified RGB values.
        /// </summary>
        private MeasurementResult? FindMeasurementByRgb(double r, double g, double b, double tolerance = 0.02)
        {
            return _measurements
                .Where(m => m.IsValid)
                .Where(m =>
                    Math.Abs(m.Patch.DisplayRgb.R - r) < tolerance &&
                    Math.Abs(m.Patch.DisplayRgb.G - g) < tolerance &&
                    Math.Abs(m.Patch.DisplayRgb.B - b) < tolerance)
                .FirstOrDefault();
        }

        /// <summary>
        /// Calculates verification metrics by comparing measured values to target. Shares its
        /// math (including the absolute→normalized luminance handling) with the post-apply
        /// verify pass, so the report's before and after numbers are directly comparable.
        /// </summary>
        public CalibrationMetrics CalculateMetrics() =>
            CalibrationVerifier.ComputeMetrics(_measurements, _target);
    }

    /// <summary>
    /// Display characterization data extracted from measurements.
    /// </summary>
    public class DisplayCharacterization
    {
        /// <summary>Black point XYZ (minimum luminance).</summary>
        public CieXyz BlackXyz { get; set; }

        /// <summary>White point XYZ (maximum luminance).</summary>
        public CieXyz WhiteXyz { get; set; }

        /// <summary>Measured red primary chromaticity.</summary>
        public Chromaticity RedPrimary { get; set; }

        /// <summary>Measured green primary chromaticity.</summary>
        public Chromaticity GreenPrimary { get; set; }

        /// <summary>Measured blue primary chromaticity.</summary>
        public Chromaticity BluePrimary { get; set; }

        /// <summary>Measured white point chromaticity.</summary>
        public Chromaticity WhitePoint { get; set; }

        /// <summary>Black level in cd/m².</summary>
        public double BlackLevel { get; set; }

        /// <summary>Peak luminance in cd/m².</summary>
        public double PeakLuminance { get; set; }

        /// <summary>Measured average gamma.</summary>
        public double MeasuredGamma { get; set; }

        /// <summary>Red channel tone response curve.</summary>
        public ToneCurve RedToneCurve { get; set; } = ToneCurve.CreateGamma(2.2);

        /// <summary>Green channel tone response curve.</summary>
        public ToneCurve GreenToneCurve { get; set; } = ToneCurve.CreateGamma(2.2);

        /// <summary>Blue channel tone response curve.</summary>
        public ToneCurve BlueToneCurve { get; set; } = ToneCurve.CreateGamma(2.2);

        /// <summary>RGB to XYZ conversion matrix for this display.</summary>
        public double[,] RgbToXyzMatrix { get; set; } = ColorMath.SrgbToXyzMatrix;

        /// <summary>Contrast ratio (peak/black).</summary>
        public double ContrastRatio => BlackLevel > 0 ? PeakLuminance / BlackLevel : double.PositiveInfinity;
    }

    /// <summary>
    /// Represents a 1D tone response curve (gamma/transfer function).
    /// </summary>
    public class ToneCurve
    {
        private readonly double[]? _lookupTable;
        private readonly double _gamma;
        private readonly bool _useLut;
        private readonly bool _isMonotonic;

        /// <summary>LUT size for interpolated curves.</summary>
        public const int LutSize = 4096;

        /// <summary>
        /// Gets whether the tone curve is monotonically increasing.
        /// Non-monotonic curves may produce incorrect results in InverseLookup.
        /// </summary>
        public bool IsMonotonic => _isMonotonic;

        private ToneCurve(double gamma)
        {
            _gamma = gamma;
            _useLut = false;
            _lookupTable = null;
            _isMonotonic = true; // Gamma curves are always monotonic
        }

        private ToneCurve(double[] lut, bool isMonotonic)
        {
            _lookupTable = lut;
            _useLut = true;
            _gamma = 2.2; // Not used
            _isMonotonic = isMonotonic;
        }

        /// <summary>
        /// Creates a simple gamma curve.
        /// </summary>
        public static ToneCurve CreateGamma(double gamma)
        {
            return new ToneCurve(SafeGamma(gamma));
        }

        /// <summary>
        /// Creates a tone curve from measured points.
        /// </summary>
        /// <param name="points">Measured (input, output) pairs</param>
        /// <param name="enforceMonotonic">If true, corrects non-monotonic segments</param>
        public static ToneCurve CreateFromPoints(IEnumerable<(double input, double output)> points, bool enforceMonotonic = true)
        {
            var sortedPoints = points
                .Where(p => double.IsFinite(p.input) && double.IsFinite(p.output))
                .Select(p => (input: Clamp01(p.input), output: Clamp01(p.output)))
                .OrderBy(p => p.input)
                .ToList();

            if (sortedPoints.Count < 2)
                return CreateGamma(2.2);

            // Build interpolated LUT
            var lut = new double[LutSize];

            for (int i = 0; i < LutSize; i++)
            {
                double x = i / (double)(LutSize - 1);
                lut[i] = InterpolatePoints(sortedPoints, x);
            }

            // Check and optionally fix monotonicity
            bool isMonotonic = CheckMonotonicity(lut);

            if (!isMonotonic && enforceMonotonic)
            {
                EnforceMonotonicity(lut);
                isMonotonic = true; // After enforcement
            }

            return new ToneCurve(lut, isMonotonic);
        }

        /// <summary>
        /// Checks if a LUT is monotonically non-decreasing.
        /// </summary>
        private static bool CheckMonotonicity(double[] lut)
        {
            for (int i = 1; i < lut.Length; i++)
            {
                if (lut[i] < lut[i - 1] - 1e-10) // Small epsilon for floating point
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Enforces monotonicity by smoothing non-monotonic segments.
        /// Uses the "pool adjacent violators" algorithm (isotonic regression).
        /// </summary>
        private static void EnforceMonotonicity(double[] lut)
        {
            // Pool Adjacent Violators Algorithm (PAVA)
            // This produces the best monotonic approximation in L2 sense
            int n = lut.Length;
            var pooled = new double[n];
            var weight = new int[n];

            for (int i = 0; i < n; i++)
            {
                pooled[i] = lut[i];
                weight[i] = 1;
            }

            int j = 0;
            for (int i = 1; i < n; i++)
            {
                // Check if current value violates monotonicity
                if (pooled[i] < pooled[j])
                {
                    // Merge current block with previous block
                    double totalWeight = weight[j] + weight[i];
                    pooled[j] = (pooled[j] * weight[j] + pooled[i] * weight[i]) / totalWeight;
                    weight[j] = (int)totalWeight;

                    // Check if we need to merge further back
                    while (j > 0 && pooled[j] < pooled[j - 1])
                    {
                        j--;
                        totalWeight = weight[j] + weight[j + 1];
                        pooled[j] = (pooled[j] * weight[j] + pooled[j + 1] * weight[j + 1]) / totalWeight;
                        weight[j] = (int)totalWeight;
                    }
                }
                else
                {
                    j++;
                    pooled[j] = pooled[i];
                    weight[j] = 1;
                }
            }

            // Expand pooled values back to original LUT
            int idx = 0;
            for (int block = 0; block <= j; block++)
            {
                for (int k = 0; k < weight[block]; k++)
                {
                    lut[idx++] = pooled[block];
                }
            }

            // Ensure endpoints are correct
            lut[0] = Math.Max(0, lut[0]);
            lut[n - 1] = Math.Min(1, lut[n - 1]);
        }

        /// <summary>
        /// Looks up the output value for a given input.
        /// </summary>
        public double Lookup(double input)
        {
            input = Clamp01(input);

            if (!_useLut || _lookupTable == null)
                return Math.Pow(input, _gamma);

            // LUT interpolation
            double index = input * (LutSize - 1);
            int i0 = (int)index;
            int i1 = Math.Min(i0 + 1, LutSize - 1);
            double frac = index - i0;

            return _lookupTable[i0] + frac * (_lookupTable[i1] - _lookupTable[i0]);
        }

        /// <summary>
        /// Looks up the input value that produces the given output (inverse).
        /// </summary>
        public double InverseLookup(double output)
        {
            output = Clamp01(output);

            if (!_useLut || _lookupTable == null)
                return Math.Pow(output, 1.0 / _gamma);

            // Binary search for inverse
            int lo = 0, hi = LutSize - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_lookupTable[mid] < output)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            // Interpolate between adjacent entries. Only short-circuit to the
            // endpoints when the target is at/outside the curve's actual range;
            // otherwise interpolate the first and last intervals exactly like the
            // interior. (The old code snapped any target in the last interval
            // [lut[LutSize-2], lut[LutSize-1]] straight to full scale.)
            if (output <= _lookupTable[0]) return 0;
            if (output >= _lookupTable[LutSize - 1]) return 1;

            double y0 = _lookupTable[lo - 1];
            double y1 = _lookupTable[lo];

            if (Math.Abs(y1 - y0) < 1e-10)
                return lo / (double)(LutSize - 1);

            double frac = (output - y0) / (y1 - y0);
            return ((lo - 1) + frac) / (LutSize - 1);
        }

        private static double InterpolatePoints(List<(double input, double output)> points, double x)
        {
            if (points.Count == 0) return x;
            if (points.Count == 1) return points[0].output;

            // Find surrounding points
            int i = 0;
            while (i < points.Count - 1 && points[i + 1].input < x)
                i++;

            if (i >= points.Count - 1)
                return points[^1].output;

            if (x <= points[0].input)
                return points[0].output;

            // Linear interpolation
            var p0 = points[i];
            var p1 = points[i + 1];

            if (Math.Abs(p1.input - p0.input) < 1e-10)
                return p0.output;

            double t = (x - p0.input) / (p1.input - p0.input);
            return p0.output + t * (p1.output - p0.output);
        }

        #region Serialization

        /// <summary>
        /// Exports the tone curve to an array for serialization.
        /// For gamma-based curves, generates the equivalent LUT.
        /// </summary>
        public double[] ToArray()
        {
            if (_useLut && _lookupTable != null)
            {
                return (double[])_lookupTable.Clone();
            }

            // Generate LUT from gamma curve
            var lut = new double[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                double x = i / (double)(LutSize - 1);
                lut[i] = Math.Pow(x, _gamma);
            }
            return lut;
        }

        /// <summary>
        /// Creates a tone curve from a serialized array.
        /// </summary>
        /// <param name="data">The LUT data (expected to be normalized 0-1 values).</param>
        /// <param name="enforceMonotonic">If true, corrects non-monotonic segments.</param>
        public static ToneCurve CreateFromArray(double[] data, bool enforceMonotonic = false)
        {
            if (data == null || data.Length < 2)
                return CreateGamma(2.2);
            if (data.Any(v => !double.IsFinite(v)))
                return CreateGamma(2.2);

            // Resample if needed to match LutSize
            double[] lut;
            if (data.Length == LutSize)
            {
                lut = data.Select(Clamp01).ToArray();
            }
            else
            {
                lut = new double[LutSize];
                for (int i = 0; i < LutSize; i++)
                {
                    double srcIndex = i * (data.Length - 1) / (double)(LutSize - 1);
                    int i0 = (int)srcIndex;
                    int i1 = Math.Min(i0 + 1, data.Length - 1);
                    double frac = srcIndex - i0;
                    lut[i] = Clamp01(data[i0] + frac * (data[i1] - data[i0]));
                }
            }

            bool isMonotonic = CheckMonotonicity(lut);

            if (!isMonotonic && enforceMonotonic)
            {
                EnforceMonotonicity(lut);
                isMonotonic = true;
            }

            return new ToneCurve(lut, isMonotonic);
        }

        private static double Clamp01(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

        private static double SafeGamma(double gamma) =>
            double.IsFinite(gamma) ? Math.Clamp(gamma, 1.0, 4.0) : 2.2;

        #endregion
    }

    /// <summary>
    /// Calibration quality metrics.
    /// </summary>
    public class CalibrationMetrics
    {
        /// <summary>Average Delta E 2000 across all patches.</summary>
        public double AverageDeltaE { get; set; }

        /// <summary>Maximum Delta E 2000.</summary>
        public double MaxDeltaE { get; set; }

        /// <summary>Minimum Delta E 2000.</summary>
        public double MinDeltaE { get; set; }

        /// <summary>Median Delta E 2000.</summary>
        public double MedianDeltaE { get; set; }

        /// <summary>Delta E values for grayscale patches.</summary>
        public List<double> GrayscaleDeltaEs { get; } = new();

        /// <summary>Delta E values for primary color patches.</summary>
        public List<double> PrimaryDeltaEs { get; } = new();

        /// <summary>Grayscale error decomposed: lightness-axis component per gray patch.</summary>
        public List<double> GrayscaleToneDeltaEs { get; } = new();

        /// <summary>Grayscale error decomposed: chromatic component per gray patch.</summary>
        public List<double> GrayscaleColorDeltaEs { get; } = new();

        /// <summary>ΔE ITP (BT.2124) per patch, absolute-luminance HDR metric. ~3× ΔE2000 scale.</summary>
        public List<double> ItpDeltaEs { get; } = new();

        /// <summary>
        /// Per-patch results in measurement order (name, category, ΔE2000). Populated by
        /// <see cref="CalibrationVerifier.ComputeMetrics"/> for the detailed-verification
        /// analysis; other metric producers may leave it empty.
        /// </summary>
        public List<PatchDeltaE> PatchResults { get; } = new();

        /// <summary>Average grayscale tone-axis (lightness) error.</summary>
        public double AverageGrayscaleToneDeltaE => GrayscaleToneDeltaEs.Count > 0 ? GrayscaleToneDeltaEs.Average() : 0;

        /// <summary>Average grayscale chromatic (cast) error.</summary>
        public double AverageGrayscaleColorDeltaE => GrayscaleColorDeltaEs.Count > 0 ? GrayscaleColorDeltaEs.Average() : 0;

        /// <summary>Average ΔE ITP across all patches.</summary>
        public double AverageItpDeltaE => ItpDeltaEs.Count > 0 ? ItpDeltaEs.Average() : 0;

        /// <summary>Maximum ΔE ITP across all patches.</summary>
        public double MaxItpDeltaE => ItpDeltaEs.Count > 0 ? ItpDeltaEs.Max() : 0;

        /// <summary>Average grayscale Delta E.</summary>
        public double AverageGrayscaleDeltaE => GrayscaleDeltaEs.Count > 0 ? GrayscaleDeltaEs.Average() : 0;

        /// <summary>Average primary Delta E.</summary>
        public double AveragePrimaryDeltaE => PrimaryDeltaEs.Count > 0 ? PrimaryDeltaEs.Average() : 0;

        /// <summary>
        /// Quality grade from average ΔE2000. Scaled to perceptual reality, not exam scores:
        /// ΔE &lt; 1 is at/below the just-noticeable-difference threshold (reference), 1–2 is
        /// excellent (visible only to trained eyes in direct A/B), 2–3 good for general use,
        /// 3–4 noticeable in side-by-side. A consumer panel averaging 1.5 after calibration
        /// is performing very well and the grade should say so.
        /// </summary>
        public CalibrationGrade GetGrade()
        {
            return AverageDeltaE switch
            {
                < 0.6 => CalibrationGrade.APLus,  // Reference — below the JND
                < 1.2 => CalibrationGrade.A,      // Excellent
                < 1.8 => CalibrationGrade.AMinus, // Very good
                < 2.5 => CalibrationGrade.BPlus,  // Good
                < 3.25 => CalibrationGrade.B,
                < 4.0 => CalibrationGrade.BMinus,
                < 5.0 => CalibrationGrade.CPlus,  // Acceptable for general use
                < 6.5 => CalibrationGrade.C,
                < 8.0 => CalibrationGrade.CMinus,
                < 10.0 => CalibrationGrade.D,
                _ => CalibrationGrade.F           // Poor
            };
        }
    }

}
