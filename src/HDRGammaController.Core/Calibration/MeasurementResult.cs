using System;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// A single colorimeter measurement result containing XYZ tristimulus values
    /// and derived colorimetric data.
    /// </summary>
    /// <remarks>
    /// This represents the raw output from a colorimeter measurement, plus
    /// computed values useful for calibration analysis. All measurements are
    /// referenced to CIE D65 illuminant unless otherwise specified.
    /// </remarks>
    public class MeasurementResult
    {
        /// <summary>
        /// Unique identifier for this measurement.
        /// </summary>
        public Guid Id { get; init; } = Guid.NewGuid();

        /// <summary>
        /// UTC timestamp when the measurement was taken.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// The patch that was displayed for this measurement.
        /// </summary>
        public required ColorPatch Patch { get; init; }

        /// <summary>
        /// Measured CIE XYZ tristimulus values (Y in cd/m²).
        /// </summary>
        public required CieXyz Xyz { get; init; }

        /// <summary>
        /// Measured luminance in cd/m² (nits). Same as Xyz.Y.
        /// </summary>
        public double Luminance => Xyz.Y;

        /// <summary>
        /// Measured chromaticity (derived from XYZ).
        /// </summary>
        public Chromaticity Chromaticity => Xyz.ToChromaticity();

        /// <summary>
        /// Measured CIE L*a*b* (derived from XYZ with D65 reference).
        /// </summary>
        public CieLab Lab => ColorMath.XyzToLab(Xyz);

        /// <summary>
        /// Correlated Color Temperature in Kelvin (only meaningful for neutrals).
        /// </summary>
        public double Cct => ColorMath.ChromaticityToCct(Chromaticity);

        /// <summary>
        /// Distance from Planckian locus (Duv). Positive = greenish, Negative = magenta.
        /// Only meaningful for neutral/gray patches.
        /// </summary>
        public double Duv => ColorMath.CalculateDuv(Chromaticity);

        /// <summary>
        /// Measurement integration time in milliseconds (if reported by colorimeter).
        /// </summary>
        public double? IntegrationTimeMs { get; init; }

        /// <summary>
        /// Whether this measurement passed validation checks.
        /// </summary>
        public bool IsValid { get; init; } = true;

        /// <summary>
        /// Error message if measurement failed or has issues.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Raw output from the colorimeter (for debugging/logging).
        /// </summary>
        public string? RawOutput { get; init; }

        /// <summary>
        /// Index of this measurement in the patch sequence.
        /// </summary>
        public int SequenceIndex { get; init; }

        /// <summary>
        /// Calculates Delta E 2000 between the measured color and a target XYZ.
        /// </summary>
        public double DeltaE2000To(CieXyz targetXyz)
        {
            var targetLab = ColorMath.XyzToLab(targetXyz);
            return Lab.DeltaE2000(targetLab);
        }

        /// <summary>
        /// Calculates Delta E 2000 between the measured color and a target Lab.
        /// </summary>
        public double DeltaE2000To(CieLab targetLab)
        {
            return Lab.DeltaE2000(targetLab);
        }

        public override string ToString() =>
            $"Measurement[{Patch.Name}]: XYZ=({Xyz.X:F3}, {Xyz.Y:F3}, {Xyz.Z:F3}), " +
            $"L*={Lab.L:F1}, CCT={Cct:F0}K";
    }

    /// <summary>
    /// Represents a color patch to be displayed and measured.
    /// </summary>
    public class ColorPatch
    {
        /// <summary>
        /// Human-readable name for this patch (e.g., "White", "Red Primary", "50% Gray").
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// The RGB signal values to display (0.0-1.0 range, gamma-encoded).
        /// These are the normalized signal values that would be sent to the display
        /// (e.g., 0.5 represents 50% signal level, which after gamma decoding
        /// produces approximately 21.8% linear light output with gamma 2.2).
        /// Note: Although the type is LinearRgb, these values represent signal
        /// levels rather than linear light - the EOTF must be applied to convert
        /// to actual linear light values.
        /// </summary>
        public required LinearRgb DisplayRgb { get; init; }

        /// <summary>
        /// The expected/target XYZ for this patch in the target color space.
        /// Used for calculating color error (Delta E).
        /// </summary>
        public CieXyz? TargetXyz { get; init; }

        /// <summary>
        /// The expected/target Lab for this patch.
        /// </summary>
        public CieLab? TargetLab { get; init; }

        /// <summary>
        /// When set, this is an HDR WIRE patch: the display layer must emit this absolute
        /// luminance (equal on all channels) through an FP16 scRGB surface (scRGB value =
        /// nits/80), which places the stimulus at the exact PQ wire position PQ⁻¹(nits)
        /// with no SDR-mapping assumption. DisplayRgb is ignored for rendering; keep it at
        /// midpoint (0.5) so the white/black classification heuristics elsewhere never
        /// mistake wire patches for the SDR white or black patch.
        /// </summary>
        public double? Nits { get; init; }

        /// <summary>
        /// Category of this patch for analysis grouping.
        /// </summary>
        public PatchCategory Category { get; init; } = PatchCategory.General;

        /// <summary>
        /// Index in the measurement sequence.
        /// </summary>
        public int Index { get; init; }

        /// <summary>
        /// Whether this is a critical patch (e.g., primaries, neutrals) that
        /// strongly affects profile accuracy.
        /// </summary>
        public bool IsCritical { get; init; }

        public override string ToString() => $"Patch[{Index}]: {Name} RGB({DisplayRgb})";
    }

    /// <summary>
    /// Categories of calibration patches for analysis and reporting.
    /// </summary>
    public enum PatchCategory
    {
        /// <summary>General/uncategorized patches.</summary>
        General,

        /// <summary>Grayscale/neutral patches (R=G=B).</summary>
        Grayscale,

        /// <summary>Primary colors at full saturation.</summary>
        Primary,

        /// <summary>Secondary colors (cyan, magenta, yellow).</summary>
        Secondary,

        /// <summary>Near-neutral colors for white point accuracy.</summary>
        NearNeutral,

        /// <summary>Dark/shadow region patches.</summary>
        Shadow,

        /// <summary>Highlight/bright region patches.</summary>
        Highlight,

        /// <summary>Skin tone reference patches.</summary>
        SkinTone,

        /// <summary>Saturated color patches throughout the gamut.</summary>
        Saturated,

        /// <summary>Memory colors (ColorChecker-style skin, sky, foliage) for verification.</summary>
        MemoryColor,

        /// <summary>
        /// Periodic white/black re-reads interleaved into long runs for drift analysis
        /// (panel warm-up, ABL). Excluded from model building: the tone-curve, gamma and
        /// anchor extraction paths all filter on <see cref="Grayscale"/>/<see cref="Primary"/>,
        /// so DriftCheck patches never enter the fitted characterization. They ARE consumed
        /// by <see cref="DriftCompensator"/> (multiplicative luminance normalization) and by
        /// the measurement validator's repeated-white/black drift gates.
        /// </summary>
        DriftCheck
    }
}
