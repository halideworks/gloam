using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HDRGammaController.Core.Calibration;

namespace HDRGammaController.Services
{
    /// <summary>
    /// Builds and writes the durable report snapshot. Keeping this serialization boundary
    /// outside the report window makes every post-calibration operation save the same set
    /// of numbers, diagnostics, raw readings, and HDR characterization data.
    /// </summary>
    internal static class ReportSnapshotBuilder
    {
        internal sealed record Request(
            CalibrationProfile Profile,
            CalibrationMetrics? NativeMetrics,
            CalibrationMetrics? VerificationMetrics,
            IReadOnlyList<PatchDeltaE>? DetailedPatches,
            string GradeScopeLabel,
            string SummaryText,
            string? VerificationDetailText,
            string? PqTrackingDetailText,
            string? ColoredHdrDetailText,
            ToneMappingCharacterization? ToneMapping,
            IReadOnlyList<MeasurementResult>? NativeMeasurements,
            IReadOnlyList<MeasurementResult>? VerificationMeasurements,
            string? ExistingPath);

        internal sealed record Result(string Path, CalibrationReportSummary Summary);

        internal static Result Save(Request request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Profile);

            var detailed = request.DetailedPatches;
            CategoryBreakdown? breakdown = detailed != null
                ? VerificationAnalysis.ComputeCategoryBreakdown(detailed)
                : null;

            var summary = new CalibrationReportSummary
            {
                AvgDeltaE = request.NativeMetrics?.AverageDeltaE,
                MaxDeltaE = request.NativeMetrics?.MaxDeltaE,
                GrayscaleDeltaE = request.NativeMetrics?.AverageGrayscaleDeltaE,
                PrimaryDeltaE = request.NativeMetrics?.AveragePrimaryDeltaE,
                AfterAvgDeltaE = request.VerificationMetrics?.AverageDeltaE,
                AfterMaxDeltaE = request.VerificationMetrics?.MaxDeltaE,
                AfterGrayscaleDeltaE = request.VerificationMetrics?.AverageGrayscaleDeltaE,
                AfterPrimaryDeltaE = request.VerificationMetrics?.AveragePrimaryDeltaE,
                GradeScopeLabel = request.GradeScopeLabel,
                SummaryText = request.SummaryText,
                DetailedPatches = detailed?
                    .Take(VerificationPatchSets.DetailedPatchCount)
                    .Select(p => new VerifiedPatchResult
                    {
                        Name = p.Name,
                        Category = p.Category.ToString(),
                        DeltaE = Math.Round(p.DeltaE, 3),
                    })
                    .ToList(),
                DetailedHistogram = detailed != null
                    ? VerificationAnalysis.HistogramCounts(detailed.Select(p => p.DeltaE))
                    : null,
                DetailedGrayscaleDeltaE = breakdown?.GrayscaleDeltaE,
                DetailedPrimariesDeltaE = breakdown?.PrimariesDeltaE,
                DetailedSaturationDeltaE = breakdown?.SaturationDeltaE,
                DetailedMemoryColorsDeltaE = breakdown?.MemoryColorsDeltaE,
                VerificationDetailText = request.VerificationDetailText,
                PqTrackingDetailText = request.PqTrackingDetailText,
                ColoredHdrDetailText = request.ColoredHdrDetailText,
                ToneMapping = request.ToneMapping,
            };

            string path = request.ExistingPath ?? BuildPath(request.Profile);
            request.Profile.ReportSummary = summary;
            request.Profile.SaveToFile(path);
            SaveMeasurements(request, path);
            return new Result(path, summary);
        }

        private static string BuildPath(CalibrationProfile profile)
        {
            string safeName = string.Join("_", profile.MonitorName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(
                CalibrationProfile.GetReportsDirectory(),
                $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        }

        private static void SaveMeasurements(Request request, string path)
        {
            string reportDir = Path.GetDirectoryName(path) ?? CalibrationProfile.GetReportsDirectory();
            string measurementDir = Path.Combine(reportDir, "measurements");
            string baseName = Path.GetFileNameWithoutExtension(path);
            string reportId = request.Profile.Id.ToString();

            if (request.NativeMeasurements is { Count: > 0 })
            {
                MeasurementCsvExporter.Save(
                    Path.Combine(measurementDir, $"{baseName}_native-measurements.csv"),
                    reportId,
                    "native",
                    request.NativeMeasurements);
            }

            if (request.VerificationMeasurements is { Count: > 0 })
            {
                MeasurementCsvExporter.Save(
                    Path.Combine(measurementDir, $"{baseName}_verification-measurements.csv"),
                    reportId,
                    "verification",
                    request.VerificationMeasurements);
            }
        }
    }
}
