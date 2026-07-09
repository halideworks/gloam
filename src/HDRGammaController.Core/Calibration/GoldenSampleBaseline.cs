using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// A committed snapshot of what the calibration pipeline computes from a recorded
    /// real-panel measurement set (a "golden sample"). Regression tests replay the recorded
    /// CSVs through the live pipeline and compare against this baseline with explicit
    /// tolerances, so modeling changes to characterization, metrics, uncertainty, or the
    /// HDR LUT build surface as diffs instead of shipping silently.
    /// </summary>
    /// <remarks>
    /// Baselines are regenerated deliberately via <c>cli golden-ingest</c> (never by the
    /// tests themselves) so every accepted numeric change is visible in git review.
    /// </remarks>
    public sealed record GoldenSampleBaseline
    {
        public int SchemaVersion { get; init; } = 1;

        // --- Characterization (fitted from the native-phase measurements) ---
        public double BlackLevelNits { get; init; }
        public double PeakLuminanceNits { get; init; }
        public double MeasuredGamma { get; init; }
        public double RedPrimaryX { get; init; }
        public double RedPrimaryY { get; init; }
        public double GreenPrimaryX { get; init; }
        public double GreenPrimaryY { get; init; }
        public double BluePrimaryX { get; init; }
        public double BluePrimaryY { get; init; }
        public double WhitePointX { get; init; }
        public double WhitePointY { get; init; }
        public bool HasPerChannelToneCurves { get; init; }

        /// <summary>Neutral tone curve sampled at signals 0.00, 0.05, …, 1.00 (21 values).</summary>
        public IReadOnlyList<double> NeutralToneSamples { get; init; } = Array.Empty<double>();

        // --- Verification metrics (graded against the recorded target) ---
        public double AverageDeltaE { get; init; }
        public double MaxDeltaE { get; init; }
        public double MedianDeltaE { get; init; }
        public double AverageGrayscaleDeltaE { get; init; }
        public double AveragePrimaryDeltaE { get; init; }
        public double AverageItpDeltaE { get; init; }

        /// <summary>Expanded (k=2) uncertainty on the average ΔE, when a context was available.</summary>
        public double? UncertaintyExpandedU { get; init; }

        // --- HDR wire LUT build (present only when the native phase has a wire ladder) ---
        public double? HdrMeasuredBlackNits { get; init; }
        public double? HdrMeasuredPeakNits { get; init; }
        public bool? HdrWireExact { get; init; }

        /// <summary>HDR tone LUT sampled at 17 evenly spaced input positions.</summary>
        public IReadOnlyList<double>? HdrLutSamples { get; init; }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public void Save(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);
        }

        public static GoldenSampleBaseline Load(string path)
            => JsonSerializer.Deserialize<GoldenSampleBaseline>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidDataException($"Baseline at '{path}' deserialized to null.");

        /// <summary>
        /// Runs the recorded measurements through the same pure pipeline stages the app uses
        /// (characterization fit, verification grading, uncertainty budget, HDR wire LUT
        /// build) and snapshots the results. This is the single code path shared by the
        /// ingest tool (writing baselines) and the regression tests (recomputing them).
        /// </summary>
        public static GoldenSampleBaseline Compute(
            IReadOnlyList<MeasurementResult> nativeMeasurements,
            IReadOnlyList<MeasurementResult> verificationMeasurements,
            CalibrationTarget target,
            bool hdrMode,
            double sdrWhiteNits,
            UncertaintyBudget.Context? uncertaintyContext)
        {
            if (nativeMeasurements == null) throw new ArgumentNullException(nameof(nativeMeasurements));
            if (verificationMeasurements == null) throw new ArgumentNullException(nameof(verificationMeasurements));
            if (target == null) throw new ArgumentNullException(nameof(target));

            var generator = new Lut3DGenerator(target, nativeMeasurements);
            var characterization = generator.BuildCharacterizationOnly(hdrMode);

            var metrics = CalibrationVerifier.ComputeMetrics(
                verificationMeasurements, target, uncertaintyContext, out var uncertainty);

            var neutralTone = characterization.NeutralToneCurve ?? characterization.GreenToneCurve;
            var toneSamples = Enumerable.Range(0, 21)
                .Select(i => neutralTone.Lookup(i / 20.0))
                .ToArray();

            HdrMhc2LutBuilder.Result? hdrLuts = null;
            if (hdrMode && nativeMeasurements.Any(m =>
                    m.IsValid && m.Patch.Nits is double nits && double.IsFinite(nits)))
            {
                hdrLuts = HdrMhc2LutBuilder.Build(nativeMeasurements, sdrWhiteNits);
            }

            return new GoldenSampleBaseline
            {
                BlackLevelNits = characterization.BlackLevel,
                PeakLuminanceNits = characterization.PeakLuminance,
                MeasuredGamma = characterization.MeasuredGamma,
                RedPrimaryX = characterization.RedPrimary.X,
                RedPrimaryY = characterization.RedPrimary.Y,
                GreenPrimaryX = characterization.GreenPrimary.X,
                GreenPrimaryY = characterization.GreenPrimary.Y,
                BluePrimaryX = characterization.BluePrimary.X,
                BluePrimaryY = characterization.BluePrimary.Y,
                WhitePointX = characterization.WhitePoint.X,
                WhitePointY = characterization.WhitePoint.Y,
                HasPerChannelToneCurves = characterization.HasPerChannelToneCurves,
                NeutralToneSamples = toneSamples,
                AverageDeltaE = metrics.AverageDeltaE,
                MaxDeltaE = metrics.MaxDeltaE,
                MedianDeltaE = metrics.MedianDeltaE,
                AverageGrayscaleDeltaE = metrics.AverageGrayscaleDeltaE,
                AveragePrimaryDeltaE = metrics.AveragePrimaryDeltaE,
                AverageItpDeltaE = metrics.AverageItpDeltaE,
                UncertaintyExpandedU = uncertainty?.ExpandedU,
                HdrMeasuredBlackNits = hdrLuts?.MeasuredBlackNits,
                HdrMeasuredPeakNits = hdrLuts?.MeasuredPeakNits,
                HdrWireExact = hdrLuts?.WireExact,
                HdrLutSamples = hdrLuts == null ? null : SampleLut(hdrLuts.LutR, 17),
            };
        }

        private static double[] SampleLut(IReadOnlyList<double> lut, int count)
            => Enumerable.Range(0, count)
                .Select(i => lut[(int)Math.Round(i * (lut.Count - 1) / (double)(count - 1))])
                .ToArray();
    }

    /// <summary>
    /// Per-fixture metadata for a golden sample: which panel, target, mode, and instrument
    /// the recording was made with. Lives next to the phase CSVs and the baseline JSON.
    /// </summary>
    public sealed record GoldenSampleManifest
    {
        public int SchemaVersion { get; init; } = 1;
        public string PanelLabel { get; init; } = string.Empty;
        public string TargetName { get; init; } = string.Empty;
        public bool HdrMode { get; init; }
        public double SdrWhiteNits { get; init; } = 200.0;
        public string? InstrumentModel { get; init; }
        public bool HasMeterCorrection { get; init; }
        public string NativeCsv { get; init; } = "native-measurements.csv";
        public string VerificationCsv { get; init; } = "verification-measurements.csv";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public void Save(string path)
            => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + Environment.NewLine);

        public static GoldenSampleManifest Load(string path)
            => JsonSerializer.Deserialize<GoldenSampleManifest>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidDataException($"Manifest at '{path}' deserialized to null.");

        /// <summary>The uncertainty context this recording supports (repeatability inherited
        /// from the verification reads themselves at compute time).</summary>
        public UncertaintyBudget.Context ToUncertaintyContext(
            IReadOnlyList<MeasurementResult> verificationMeasurements)
        {
            var instrument = UncertaintyBudget.ClassifyInstrument(InstrumentModel, HasMeterCorrection);
            var noise = LuminanceNoiseModel.FromMeasurements(verificationMeasurements);
            return new UncertaintyBudget.Context(instrument, noise, null, false);
        }
    }
}
