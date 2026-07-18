using System;
using System.Collections.Generic;
using System.Linq;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Core
{
    /// <summary>
    /// Compiles night mode for the hardware that actually receives it: three monotone
    /// 256-entry, 16-bit ramps. The ordinary generator remains the baseline; every optional
    /// refinement is scored after quantization and rejected on any perceptual/structural
    /// regression. Returned arrays are expanded back to the caller's length such that the
    /// native 256-entry resampler recovers the compiled knots exactly.
    /// </summary>
    public static class NightModeRampCompiler
    {
        public const int HardwareEntries = 256;
        public const double SubJndBudget = 0.20;
        private const int AppearanceGrid = 7;

        public sealed record Result(
            double[] R,
            double[] G,
            double[] B,
            bool AppearanceProjectionApplied,
            bool SubJndHarvestApplied,
            double BaselineAverageDeltaEPrime,
            double CompiledAverageDeltaEPrime,
            double EstimatedMelanopicReduction);

        private sealed record Curves(double[] R, double[] G, double[] B);
        private readonly record struct AppearanceScore(double Average, double Maximum);

        public static Result Compile(
            double[] sourceR,
            double[] sourceG,
            double[] sourceB,
            CalibrationSettings settings,
            bool isHdr,
            double sdrWhiteLevel)
        {
            Validate(sourceR, sourceG, sourceB);
            settings = (settings ?? CalibrationSettings.Default).Sanitized();
            sdrWhiteLevel = MonitorInfo.SanitizeSdrWhiteLevel(sdrWhiteLevel);

            var baseline = new Curves(
                QuantizedHardwareKnots(sourceR),
                QuantizedHardwareKnots(sourceG),
                QuantizedHardwareKnots(sourceB));
            var best = Clone(baseline);

            bool appearanceApplied = false;
            bool harvestApplied = false;
            double baselineAppearance = 0.0;
            double compiledAppearance = 0.0;

            if (settings.Algorithm is NightModeAlgorithm.Perceptual or NightModeAlgorithm.AccurateCIE1931)
            {
                var projected = ProjectFullCat16(best, settings, isHdr, sdrWhiteLevel);
                if (isHdr && settings.NightHdrHighlightPolicy != NightHdrHighlightPolicy.DoseBound)
                    projected = PreserveHdrHeadroomPolicy(projected, baseline, sdrWhiteLevel);
                var baselineScore = AppearanceObjective(best, settings, isHdr, sdrWhiteLevel);
                var projectedScore = AppearanceObjective(projected, settings, isHdr, sdrWhiteLevel);
                baselineAppearance = baselineScore.Average;
                if (IsStructurallySafe(projected, best) &&
                    projectedScore.Average + 1e-5 < baselineScore.Average * 0.997 &&
                    projectedScore.Maximum <= baselineScore.Maximum + 0.05)
                {
                    best = projected;
                    appearanceApplied = true;
                }
            }

            double beforeMel = EstimatedMelanopicEnergy(best, settings, isHdr, sdrWhiteLevel);
            if (settings.HarvestNightSubJndBudget)
            {
                var harvested = HarvestSubJnd(best, settings, isHdr, sdrWhiteLevel);
                if (isHdr && settings.NightHdrHighlightPolicy == NightHdrHighlightPolicy.Creative)
                    harvested = PreserveTopKnot(harvested, baseline);
                harvested = ContractToPerceptualBudget(
                    best, harvested, isHdr, sdrWhiteLevel, SubJndBudget + 0.025);
                double afterMel = EstimatedMelanopicEnergy(harvested, settings, isHdr, sdrWhiteLevel);
                if (IsStructurallySafe(harvested, best) &&
                    afterMel < beforeMel - 1e-8 &&
                    MaxRepresentativeDeltaE(best, harvested, isHdr, sdrWhiteLevel) <= SubJndBudget + 0.035)
                {
                    best = harvested;
                    harvestApplied = true;
                }
            }

            double finalMel = EstimatedMelanopicEnergy(best, settings, isHdr, sdrWhiteLevel);
            double melReduction = beforeMel > 1e-12
                ? Math.Clamp(1.0 - finalMel / beforeMel, 0.0, 1.0)
                : 0.0;

            var finalAppearance = AppearanceObjective(best, settings, isHdr, sdrWhiteLevel);
            if (baselineAppearance == 0.0)
                baselineAppearance = AppearanceObjective(baseline, settings, isHdr, sdrWhiteLevel).Average;
            compiledAppearance = finalAppearance.Average;

            return new Result(
                Expand(best.R, sourceR.Length),
                Expand(best.G, sourceG.Length),
                Expand(best.B, sourceB.Length),
                appearanceApplied,
                harvestApplied,
                baselineAppearance,
                compiledAppearance,
                melReduction);
        }

        private static Curves ProjectFullCat16(
            Curves baseline, CalibrationSettings settings, bool isHdr, double whiteNits)
        {
            double effectiveScale = ColorAdjustments.ComposeTemperatureScaleMired(
                settings.Temperature, settings.TemperatureOffset);
            int kelvin = ColorAdjustments.TemperatureScaleToKelvin(effectiveScale);
            double degree = settings.Algorithm == NightModeAlgorithm.Perceptual
                ? settings.PerceptualStrength
                : 1.0;
            var basis = isHdr ? NightBasis.Rec2020 : NightBasis.Srgb;
            var diagonal = ColorAdjustments.GetTemperatureMultipliers(
                effectiveScale, settings.Algorithm, settings.UseUltraWarmMode,
                settings.PerceptualStrength, settings.NightMelanopicCoefficients, basis);
            if (settings.PreserveNightLuminance && settings.Algorithm != NightModeAlgorithm.UltraNight)
            {
                diagonal = ColorAdjustments.RescaleToConstantLuminance(
                    diagonal, basis, settings.NightLuminanceCeiling,
                    ColorAdjustments.ApplyDimming(1.0, settings.Brightness, settings.UseLinearBrightness));
            }

            var sum = new[] { new double[AppearanceGrid], new double[AppearanceGrid], new double[AppearanceGrid] };
            var weight = new[] { new double[AppearanceGrid], new double[AppearanceGrid], new double[AppearanceGrid] };
            var sourceWhite = ColorMath.CctToChromaticity(6500).ToXyz(1.0);
            var targetWhite = ColorMath.CctToChromaticity(kelvin).ToXyz(1.0);

            for (int ri = 0; ri < AppearanceGrid; ri++)
            for (int gi = 0; gi < AppearanceGrid; gi++)
            for (int bi = 0; bi < AppearanceGrid; bi++)
            {
                double xr = ri / (double)(AppearanceGrid - 1);
                double xg = gi / (double)(AppearanceGrid - 1);
                double xb = bi / (double)(AppearanceGrid - 1);
                if (!ShouldScoreAppearance(xr, xg, xb, settings, isHdr, whiteNits))
                    continue;
                var encoded = (
                    Lookup(baseline.R, xr),
                    Lookup(baseline.G, xg),
                    Lookup(baseline.B, xb));
                var linear = Decode(encoded, isHdr, whiteNits);

                // Remove only the existing diagonal night shift. Dimming, user gains and
                // gamma preference remain in the source and therefore remain represented in
                // the compiled result.
                var unshifted = new LinearRgb(
                    SafeDivide(linear.R, diagonal.R),
                    SafeDivide(linear.G, diagonal.G),
                    SafeDivide(linear.B, diagonal.B));
                var xyz = basis == NightBasis.Rec2020
                    ? ColorMath.LinearRec2020ToXyz(unshifted)
                    : ColorMath.LinearSrgbToXyz(unshifted);
                var adapted = ColorMath.Cat16Adapt(xyz, sourceWhite, targetWhite, degree);
                var desiredLinear = basis == NightBasis.Rec2020
                    ? ColorMath.XyzToLinearRec2020(adapted)
                    : ColorMath.XyzToLinearSrgb(adapted);
                var desired = Encode((desiredLinear.R, desiredLinear.G, desiredLinear.B), isHdr, whiteNits);

                bool neutral = ri == gi && gi == bi;
                bool corner = (ri == 0 || ri == AppearanceGrid - 1) &&
                              (gi == 0 || gi == AppearanceGrid - 1) &&
                              (bi == 0 || bi == AppearanceGrid - 1);
                double w = neutral ? 12.0 : corner ? 3.0 : 1.0;
                Accumulate(0, ri, desired.R, w);
                Accumulate(1, gi, desired.G, w);
                Accumulate(2, bi, desired.B, w);

                void Accumulate(int channel, int index, double value, double sampleWeight)
                {
                    sum[channel][index] += value * sampleWeight;
                    weight[channel][index] += sampleWeight;
                }
            }

            var targetR = FitAnchorCurve(sum[0], weight[0]);
            var targetG = FitAnchorCurve(sum[1], weight[1]);
            var targetB = FitAnchorCurve(sum[2], weight[2]);
            const double blend = 0.35;
            var candidate = new Curves(
                BlendAndProject(baseline.R, targetR, blend),
                BlendAndProject(baseline.G, targetG, blend),
                BlendAndProject(baseline.B, targetB, blend));
            return Quantize(candidate);
        }

        private static Curves HarvestSubJnd(
            Curves baseline, CalibrationSettings settings, bool isHdr, double whiteNits)
        {
            var candidate = Clone(baseline);
            var mel = MelanopicVector(settings.NightMelanopicCoefficients);
            double effectiveScale = ColorAdjustments.ComposeTemperatureScaleMired(
                settings.Temperature, settings.TemperatureOffset);
            int kelvin = ColorAdjustments.TemperatureScaleToKelvin(effectiveScale);
            double warmth = Math.Clamp((6500.0 - kelvin) / 4600.0, 0.0, 1.0);
            if (warmth <= 1e-6) return candidate;

            var conditions = Cam16Ucs.DisplayConditions(
                new CieXyz(ColorMath.D65White.X * whiteNits, whiteNits, ColorMath.D65White.Z * whiteNits));

            for (int i = 1; i < HardwareEntries; i++)
            {
                double x = i / 255.0;
                // Shadows carry little absolute melanopic energy and are where one lost code
                // is most damaging. Spend the visibility budget progressively above the toe.
                double allocation = SmoothStep(0.06, 0.72, x);
                double budget = SubJndBudget * warmth * allocation;
                if (budget < 0.005) continue;

                var encoded = (candidate.R[i], candidate.G[i], candidate.B[i]);
                var linear = Decode(encoded, isHdr, whiteNits);
                if (linear.R + linear.G + linear.B < 1e-7) continue;

                var direction = LeastVisibleMelanopicDirection(linear, mel, isHdr, whiteNits, conditions);
                if (direction is null) continue;

                var baseJab = Jab(linear, isHdr, whiteNits, conditions);
                double maxStep = MaximumSafeStep(linear, direction.Value, isHdr, whiteNits);
                if (maxStep <= 0) continue;

                double low = 0.0, high = maxStep;
                for (int iteration = 0; iteration < 11; iteration++)
                {
                    double step = (low + high) * 0.5;
                    var trial = Add(linear, direction.Value, step);
                    double delta = Cam16Ucs.DeltaEPrime(baseJab, Jab(trial, isHdr, whiteNits, conditions));
                    if (delta <= budget) low = step;
                    else high = step;
                }

                var changed = Add(linear, direction.Value, low);
                if (Melanopic(changed, mel) >= Melanopic(linear, mel) - 1e-12) continue;
                var encodedChanged = Encode(changed, isHdr, whiteNits);
                candidate.R[i] = encodedChanged.R;
                candidate.G[i] = encodedChanged.G;
                candidate.B[i] = encodedChanged.B;
            }

            candidate = new Curves(
                Pava(candidate.R, endpoint0: baseline.R[0]),
                Pava(candidate.G, endpoint0: baseline.G[0]),
                Pava(candidate.B, endpoint0: baseline.B[0]));
            return Quantize(candidate);
        }

        private static (double R, double G, double B)? LeastVisibleMelanopicDirection(
            (double R, double G, double B) linear,
            (double R, double G, double B) mel,
            bool isHdr,
            double whiteNits,
            Cam16Ucs.ViewingConditions conditions)
        {
            var origin = Jab(linear, isHdr, whiteNits, conditions);
            var jr = JacobianColumn(linear, origin, 0, isHdr, whiteNits, conditions);
            var jg = JacobianColumn(linear, origin, 1, isHdr, whiteNits, conditions);
            var jb = JacobianColumn(linear, origin, 2, isHdr, whiteNits, conditions);

            // Q = JᵀJ + λI is symmetric positive definite. Solve Qd = -m directly with
            // scalar Cholesky rather than allocating two matrices and three vectors at every
            // one of the 255 hardware knots. Cholesky is also better conditioned than forming
            // an explicit inverse, with exactly the same least-visible quadratic objective.
            const double regularization = 1e-5;
            double q00 = Dot(jr, jr) + regularization;
            double q01 = Dot(jr, jg);
            double q02 = Dot(jr, jb);
            double q11 = Dot(jg, jg) + regularization;
            double q12 = Dot(jg, jb);
            double q22 = Dot(jb, jb) + regularization;

            if (TrySolveSymmetricPositiveDefinite(
                    q00, q01, q02, q11, q12, q22,
                    -mel.R, -mel.G, -mel.B,
                    out double d0, out double d1, out double d2))
            {
                // Permit a small compensating increase in a low-melanopic channel, but never
                // let the optimizer create a bright red flare merely to hide a blue cut.
                double largestCut = Math.Max(0.0, -Math.Min(d0, Math.Min(d1, d2)));
                if (largestCut <= 1e-12) return null;
                double positiveCap = largestCut * 0.20;
                d0 = Math.Min(d0, positiveCap);
                d1 = Math.Min(d1, positiveCap);
                d2 = Math.Min(d2, positiveCap);
                double norm = Math.Max(Math.Abs(d0), Math.Max(Math.Abs(d1), Math.Abs(d2)));
                if (!double.IsFinite(norm) || norm <= 1e-12) return null;
                return (d0 / norm, d1 / norm, d2 / norm);
            }

            // Stable conservative fallback: blue first, a small green contribution.
            return (0.0, -0.15, -1.0);
        }

        private static (double J, double A, double B) JacobianColumn(
            (double R, double G, double B) linear,
            Cam16Ucs.JabPrime origin,
            int channel,
            bool isHdr,
            double whiteNits,
            Cam16Ucs.ViewingConditions conditions)
        {
            double value = channel == 0 ? linear.R : channel == 1 ? linear.G : linear.B;
            double step = Math.Max(1e-5, Math.Abs(value) * 0.005);
            var probe = linear;
            if (channel == 0) probe.R += step;
            else if (channel == 1) probe.G += step;
            else probe.B += step;
            var j = Jab(probe, isHdr, whiteNits, conditions);
            return ((j.J - origin.J) / step, (j.A - origin.A) / step, (j.B - origin.B) / step);
        }

        private static double Dot(
            (double J, double A, double B) x,
            (double J, double A, double B) y) =>
            x.J * y.J + x.A * y.A + x.B * y.B;

        private static bool TrySolveSymmetricPositiveDefinite(
            double q00, double q01, double q02,
            double q11, double q12, double q22,
            double b0, double b1, double b2,
            out double x0, out double x1, out double x2)
        {
            x0 = x1 = x2 = 0.0;
            if (!(q00 > 0.0) || !double.IsFinite(q00)) return false;
            double l00 = Math.Sqrt(q00);
            double l10 = q01 / l00;
            double l20 = q02 / l00;
            double diagonal1 = q11 - l10 * l10;
            if (!(diagonal1 > 1e-15) || !double.IsFinite(diagonal1)) return false;
            double l11 = Math.Sqrt(diagonal1);
            double l21 = (q12 - l20 * l10) / l11;
            double diagonal2 = q22 - l20 * l20 - l21 * l21;
            if (!(diagonal2 > 1e-15) || !double.IsFinite(diagonal2)) return false;
            double l22 = Math.Sqrt(diagonal2);

            // L y = b, followed by Lᵀ x = y.
            double y0 = b0 / l00;
            double y1 = (b1 - l10 * y0) / l11;
            double y2 = (b2 - l20 * y0 - l21 * y1) / l22;
            x2 = y2 / l22;
            x1 = (y1 - l21 * x2) / l11;
            x0 = (y0 - l10 * x1 - l20 * x2) / l00;
            return double.IsFinite(x0) && double.IsFinite(x1) && double.IsFinite(x2);
        }

        private static AppearanceScore AppearanceObjective(
            Curves curves, CalibrationSettings settings, bool isHdr, double whiteNits)
        {
            if (settings.Algorithm is not (NightModeAlgorithm.Perceptual or NightModeAlgorithm.AccurateCIE1931))
                return default;

            double effectiveScale = ColorAdjustments.ComposeTemperatureScaleMired(
                settings.Temperature, settings.TemperatureOffset);
            int kelvin = ColorAdjustments.TemperatureScaleToKelvin(effectiveScale);
            double degree = settings.Algorithm == NightModeAlgorithm.Perceptual
                ? settings.PerceptualStrength
                : 1.0;
            var basis = isHdr ? NightBasis.Rec2020 : NightBasis.Srgb;
            var diagonal = ColorAdjustments.GetTemperatureMultipliers(
                effectiveScale, settings.Algorithm, settings.UseUltraWarmMode,
                settings.PerceptualStrength, settings.NightMelanopicCoefficients, basis);
            if (settings.PreserveNightLuminance)
            {
                diagonal = ColorAdjustments.RescaleToConstantLuminance(
                    diagonal, basis, settings.NightLuminanceCeiling,
                    ColorAdjustments.ApplyDimming(1.0, settings.Brightness, settings.UseLinearBrightness));
            }

            var sourceWhite = ColorMath.CctToChromaticity(6500).ToXyz(1.0);
            var targetWhite = ColorMath.CctToChromaticity(kelvin).ToXyz(1.0);
            var conditions = Cam16Ucs.DisplayConditions(
                new CieXyz(ColorMath.D65White.X * whiteNits, whiteNits, ColorMath.D65White.Z * whiteNits));
            double sum = 0.0, weights = 0.0, maximum = 0.0;

            for (int ri = 0; ri < AppearanceGrid; ri++)
            for (int gi = 0; gi < AppearanceGrid; gi++)
            for (int bi = 0; bi < AppearanceGrid; bi++)
            {
                double xr = ri / (double)(AppearanceGrid - 1);
                double xg = gi / (double)(AppearanceGrid - 1);
                double xb = bi / (double)(AppearanceGrid - 1);
                if (!ShouldScoreAppearance(xr, xg, xb, settings, isHdr, whiteNits))
                    continue;
                var actual = Decode((Lookup(curves.R, xr), Lookup(curves.G, xg), Lookup(curves.B, xb)), isHdr, whiteNits);
                var unshifted = new LinearRgb(
                    SafeDivide(actual.R, diagonal.R),
                    SafeDivide(actual.G, diagonal.G),
                    SafeDivide(actual.B, diagonal.B));
                var xyz = basis == NightBasis.Rec2020
                    ? ColorMath.LinearRec2020ToXyz(unshifted)
                    : ColorMath.LinearSrgbToXyz(unshifted);
                var desired = ColorMath.Cat16Adapt(xyz, sourceWhite, targetWhite, degree);
                var actualXyz = basis == NightBasis.Rec2020
                    ? ColorMath.LinearRec2020ToXyz(new LinearRgb(actual.R, actual.G, actual.B))
                    : ColorMath.LinearSrgbToXyz(new LinearRgb(actual.R, actual.G, actual.B));
                bool neutral = ri == gi && gi == bi;
                double w = neutral ? 8.0 : 1.0;
                double error = Cam16Ucs.DeltaEPrime(
                    ScaleXyz(actualXyz, whiteNits), ScaleXyz(desired, whiteNits), conditions);
                sum += w * error;
                weights += w;
                maximum = Math.Max(maximum, error);
            }
            return new AppearanceScore(weights > 0 ? sum / weights : 0.0, maximum);
        }

        private static double MaxRepresentativeDeltaE(
            Curves baseline, Curves candidate, bool isHdr, double whiteNits)
        {
            var conditions = Cam16Ucs.DisplayConditions(
                new CieXyz(ColorMath.D65White.X * whiteNits, whiteNits, ColorMath.D65White.Z * whiteNits));
            double max = 0.0;
            // A per-knot neutral check is insufficient: an arbitrary pixel draws different
            // indices from the three channel ramps, and those errors can combine. Gate the
            // finished transform over a full 7³ encoded RGB lattice after quantization.
            for (int ri = 0; ri < AppearanceGrid; ri++)
            for (int gi = 0; gi < AppearanceGrid; gi++)
            for (int bi = 0; bi < AppearanceGrid; bi++)
            {
                double r = ri / (double)(AppearanceGrid - 1);
                double g = gi / (double)(AppearanceGrid - 1);
                double bInput = bi / (double)(AppearanceGrid - 1);
                var a = Decode((Lookup(baseline.R, r), Lookup(baseline.G, g), Lookup(baseline.B, bInput)), isHdr, whiteNits);
                var b = Decode((Lookup(candidate.R, r), Lookup(candidate.G, g), Lookup(candidate.B, bInput)), isHdr, whiteNits);
                max = Math.Max(max, Cam16Ucs.DeltaEPrime(
                    Jab(a, isHdr, whiteNits, conditions), Jab(b, isHdr, whiteNits, conditions)));
            }
            return max;
        }

        private static Curves PreserveTopKnot(Curves candidate, Curves baseline)
        {
            candidate.R[^1] = baseline.R[^1];
            candidate.G[^1] = baseline.G[^1];
            candidate.B[^1] = baseline.B[^1];
            return candidate;
        }

        private static Curves PreserveHdrHeadroomPolicy(
            Curves candidate, Curves baseline, double sdrWhiteNits)
        {
            double diffuseSignal = TransferFunctions.PqInverseEotf(sdrWhiteNits);
            for (int i = 0; i < HardwareEntries; i++)
            {
                if (i / 255.0 <= diffuseSignal) continue;
                candidate.R[i] = baseline.R[i];
                candidate.G[i] = baseline.G[i];
                candidate.B[i] = baseline.B[i];
            }
            return candidate;
        }

        private static bool ShouldScoreAppearance(
            double r, double g, double b,
            CalibrationSettings settings, bool isHdr, double sdrWhiteNits)
        {
            if (!isHdr || settings.NightHdrHighlightPolicy == NightHdrHighlightPolicy.DoseBound)
                return true;

            // Creative and Comfort explicitly define their own HDR headroom rendering.
            // Optimize CAT16 appearance only inside diffuse white, so the compiler cannot
            // score an intentional neutral/partly-warm specular as an error and undo it.
            double diffuseSignal = TransferFunctions.PqInverseEotf(sdrWhiteNits);
            return r <= diffuseSignal && g <= diffuseSignal && b <= diffuseSignal;
        }

        private static Curves ContractToPerceptualBudget(
            Curves baseline, Curves candidate, bool isHdr, double whiteNits, double budget)
        {
            if (MaxRepresentativeDeltaE(baseline, candidate, isHdr, whiteNits) <= budget)
                return candidate;

            // The three independent ramp changes can combine on saturated colours. Find the
            // largest globally contracted candidate that stays below the post-quantization
            // 3-D appearance budget. The acceptance gate below rechecks with extra slack.
            double low = 0.0, high = 1.0;
            var best = Clone(baseline);
            for (int iteration = 0; iteration < 9; iteration++)
            {
                double amount = (low + high) * 0.5;
                var trial = Quantize(new Curves(
                    BlendCurve(baseline.R, candidate.R, amount),
                    BlendCurve(baseline.G, candidate.G, amount),
                    BlendCurve(baseline.B, candidate.B, amount)));
                if (MaxRepresentativeDeltaE(baseline, trial, isHdr, whiteNits) <= budget)
                {
                    low = amount;
                    best = trial;
                }
                else
                {
                    high = amount;
                }
            }
            return best;
        }

        private static double[] BlendCurve(double[] baseline, double[] candidate, double amount)
        {
            var result = new double[baseline.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = baseline[i] + (candidate[i] - baseline[i]) * amount;
            return result;
        }

        private static bool IsStructurallySafe(Curves candidate, Curves baseline)
        {
            foreach (var (c, b) in new[]
                     {
                         (candidate.R, baseline.R), (candidate.G, baseline.G), (candidate.B, baseline.B)
                     })
            {
                if (!IsMonotone(c)) return false;
                if (Plateaus(c) > Plateaus(b) + 8) return false;
                for (int i = 0; i < c.Length; i++)
                {
                    if (!double.IsFinite(c[i]) || c[i] < 0.0 || c[i] > 1.0) return false;
                    if (i > 0 && c[i] - c[i - 1] > 0.08) return false;
                }
            }
            return true;
        }

        private static double EstimatedMelanopicEnergy(
            Curves curves, CalibrationSettings settings, bool isHdr, double whiteNits)
        {
            var mel = MelanopicVector(settings.NightMelanopicCoefficients);
            double total = 0.0, weight = 0.0;
            for (int i = 1; i < HardwareEntries; i++)
            {
                double x = i / 255.0;
                // Natural desktop prior: more dark/mid pixels than highlights, but energy
                // still grows with light. This is a compiler objective, not a dose claim.
                double w = 1.0 - 0.55 * x;
                var linear = Decode((curves.R[i], curves.G[i], curves.B[i]), isHdr, whiteNits);
                total += w * Melanopic(linear, mel);
                weight += w;
            }
            return weight > 0 ? total / weight : 0.0;
        }

        private static (double R, double G, double B) MelanopicVector(NightMelanopicCoefficients? c)
        {
            if (c != null && c.RedMelanopic > 0 && c.GreenMelanopic > 0 && c.BlueMelanopic > 0)
            {
                double max = Math.Max(c.RedMelanopic, Math.Max(c.GreenMelanopic, c.BlueMelanopic));
                return (c.RedMelanopic / max, c.GreenMelanopic / max, c.BlueMelanopic / max);
            }
            // Conservative typical LED-primary ordering. Absolute scale cancels.
            return (0.05, 0.42, 1.0);
        }

        private static double Melanopic((double R, double G, double B) linear,
            (double R, double G, double B) m) =>
            Math.Max(0.0, linear.R) * m.R + Math.Max(0.0, linear.G) * m.G + Math.Max(0.0, linear.B) * m.B;

        private static double MaximumSafeStep(
            (double R, double G, double B) x, (double R, double G, double B) d,
            bool isHdr, double whiteNits)
        {
            double upper = isHdr ? 10000.0 / whiteNits : 1.0;
            double max = Math.Max(0.01, (x.R + x.G + x.B) / 3.0 * 0.08);
            Limit(x.R, d.R); Limit(x.G, d.G); Limit(x.B, d.B);
            return Math.Max(0.0, max);

            void Limit(double value, double direction)
            {
                if (direction < -1e-12) max = Math.Min(max, value / -direction);
                else if (direction > 1e-12) max = Math.Min(max, (upper - value) / direction);
            }
        }

        private static (double R, double G, double B) Add(
            (double R, double G, double B) x, (double R, double G, double B) d, double step) =>
            (Math.Max(0.0, x.R + d.R * step),
             Math.Max(0.0, x.G + d.G * step),
             Math.Max(0.0, x.B + d.B * step));

        private static Cam16Ucs.JabPrime Jab(
            (double R, double G, double B) linear, bool isHdr, double whiteNits,
            Cam16Ucs.ViewingConditions conditions)
        {
            var xyz = isHdr
                ? ColorMath.LinearRec2020ToXyz(new LinearRgb(linear.R, linear.G, linear.B))
                : ColorMath.LinearSrgbToXyz(new LinearRgb(linear.R, linear.G, linear.B));
            return Cam16Ucs.ToJabPrime(ScaleXyz(xyz, whiteNits), conditions);
        }

        private static CieXyz ScaleXyz(CieXyz xyz, double whiteNits) =>
            new(xyz.X * whiteNits, xyz.Y * whiteNits, xyz.Z * whiteNits);

        private static (double R, double G, double B) Decode(
            (double R, double G, double B) encoded, bool isHdr, double whiteNits)
        {
            if (isHdr)
                return (TransferFunctions.PqEotf(encoded.R) / whiteNits,
                        TransferFunctions.PqEotf(encoded.G) / whiteNits,
                        TransferFunctions.PqEotf(encoded.B) / whiteNits);
            return (Math.Pow(Clamp01(encoded.R), 2.2),
                    Math.Pow(Clamp01(encoded.G), 2.2),
                    Math.Pow(Clamp01(encoded.B), 2.2));
        }

        private static (double R, double G, double B) Encode(
            (double R, double G, double B) linear, bool isHdr, double whiteNits)
        {
            if (isHdr)
                return (TransferFunctions.PqInverseEotf(Math.Clamp(linear.R * whiteNits, 0.0, 10000.0)),
                        TransferFunctions.PqInverseEotf(Math.Clamp(linear.G * whiteNits, 0.0, 10000.0)),
                        TransferFunctions.PqInverseEotf(Math.Clamp(linear.B * whiteNits, 0.0, 10000.0)));
            return (Math.Pow(Clamp01(linear.R), 1.0 / 2.2),
                    Math.Pow(Clamp01(linear.G), 1.0 / 2.2),
                    Math.Pow(Clamp01(linear.B), 1.0 / 2.2));
        }

        private static double[] FitAnchorCurve(double[] sum, double[] weight)
        {
            var anchors = new double[sum.Length];
            for (int i = 0; i < anchors.Length; i++)
                anchors[i] = weight[i] > 0 ? Clamp01(sum[i] / weight[i]) : i / (double)(anchors.Length - 1);
            anchors = Pava(anchors, endpoint0: 0.0);
            var result = new double[HardwareEntries];
            for (int i = 0; i < result.Length; i++)
            {
                double pos = i / 255.0 * (anchors.Length - 1);
                int lo = (int)Math.Floor(pos), hi = Math.Min(lo + 1, anchors.Length - 1);
                result[i] = anchors[lo] + (anchors[hi] - anchors[lo]) * (pos - lo);
            }
            return result;
        }

        private static double[] BlendAndProject(double[] baseline, double[] target, double amount)
        {
            var result = new double[baseline.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = baseline[i] + (target[i] - baseline[i]) * amount;
            return Pava(result, endpoint0: baseline[0]);
        }

        private static double[] Pava(double[] values, double endpoint0)
        {
            int n = values.Length;
            var level = new double[n];
            var weight = new double[n];
            var start = new int[n];
            int blocks = 0;
            for (int i = 0; i < n; i++)
            {
                level[blocks] = Clamp01(values[i]);
                weight[blocks] = i == 0 ? 1_000_000.0 : 1.0;
                if (i == 0) level[blocks] = Clamp01(endpoint0);
                start[blocks] = i;
                blocks++;
                while (blocks >= 2 && level[blocks - 2] > level[blocks - 1])
                {
                    double w = weight[blocks - 2] + weight[blocks - 1];
                    level[blocks - 2] = (level[blocks - 2] * weight[blocks - 2] +
                                         level[blocks - 1] * weight[blocks - 1]) / w;
                    weight[blocks - 2] = w;
                    blocks--;
                }
            }

            var result = new double[n];
            for (int b = 0; b < blocks; b++)
            {
                int end = b + 1 < blocks ? start[b + 1] : n;
                for (int i = start[b]; i < end; i++) result[i] = Clamp01(level[b]);
            }
            result[0] = Clamp01(endpoint0);
            return result;
        }

        private static Curves Quantize(Curves value) => new(
            value.R.Select(Quantize16).ToArray(),
            value.G.Select(Quantize16).ToArray(),
            value.B.Select(Quantize16).ToArray());

        private static double[] QuantizedHardwareKnots(double[] source)
        {
            var result = new double[HardwareEntries];
            for (int i = 0; i < result.Length; i++)
                result[i] = Quantize16(Lookup(source, i / 255.0));
            return result;
        }

        private static double[] Expand(double[] knots, int length)
        {
            if (length == knots.Length) return (double[])knots.Clone();
            var result = new double[length];
            for (int i = 0; i < length; i++) result[i] = Lookup(knots, i / (double)(length - 1));
            return result;
        }

        private static Curves Clone(Curves value) => new(
            (double[])value.R.Clone(), (double[])value.G.Clone(), (double[])value.B.Clone());

        private static double Lookup(double[] values, double input)
        {
            input = Clamp01(input);
            double pos = input * (values.Length - 1);
            int lo = (int)Math.Floor(pos), hi = Math.Min(lo + 1, values.Length - 1);
            return values[lo] + (values[hi] - values[lo]) * (pos - lo);
        }

        private static int Plateaus(double[] values)
        {
            int count = 0;
            for (int i = 1; i < values.Length; i++)
                if (Math.Abs(values[i] - values[i - 1]) < 0.5 / 65535.0) count++;
            return count;
        }

        private static bool IsMonotone(double[] values)
        {
            for (int i = 1; i < values.Length; i++)
                if (values[i] + 1e-12 < values[i - 1]) return false;
            return true;
        }

        private static double SafeDivide(double value, double denominator) =>
            double.IsFinite(denominator) && denominator > 1e-8 ? value / denominator : value;

        private static double SmoothStep(double edge0, double edge1, double x)
        {
            double t = Math.Clamp((x - edge0) / Math.Max(edge1 - edge0, 1e-9), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        private static double Quantize16(double value) =>
            Math.Round(Clamp01(value) * 65535.0) / 65535.0;

        private static double Clamp01(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

        private static void Validate(double[] r, double[] g, double[] b)
        {
            ArgumentNullException.ThrowIfNull(r);
            ArgumentNullException.ThrowIfNull(g);
            ArgumentNullException.ThrowIfNull(b);
            if (r.Length < 2 || g.Length != r.Length || b.Length != r.Length)
                throw new ArgumentException("Night-mode LUT channels must have the same length of at least two entries.");
            if (r.Any(v => !double.IsFinite(v)) || g.Any(v => !double.IsFinite(v)) || b.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("Night-mode LUT channels must be finite.");
        }
    }
}
