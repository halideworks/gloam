using System;
using System.IO;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class MeasurementCsvExporterTests
    {
        [Fact]
        public void BuildCsv_ExportsRawMeasurementDataInPatchOrder()
        {
            var first = Measurement(2, "Blue Primary", PatchCategory.Primary, 0.0, 0.0, 1.0,
                new CieXyz(0.10, 0.08, 0.90), sequenceIndex: 20);
            var second = Measurement(1, "Skin, bright", PatchCategory.MemoryColor, 0.7, 0.5, 0.4,
                new CieXyz(0.38, 0.35, 0.22), sequenceIndex: 10,
                target: new CieXyz(0.4, 0.36, 0.24), nits: 120.0, integrationMs: 150.5,
                isValid: false, error: "low signal, retry");

            string csv = MeasurementCsvExporter.BuildCsv("report-1", "verification", new[] { first, second });

            Assert.StartsWith("report_id,phase,sequence_index,measurement_id,timestamp_utc", csv);
            Assert.Contains("report-1,verification,10,", csv);
            Assert.Contains("report-1,verification,20,", csv);
            Assert.True(csv.IndexOf("Skin, bright", StringComparison.Ordinal) < csv.IndexOf("Blue Primary", StringComparison.Ordinal));
            Assert.Contains("\"Skin, bright\"", csv);
            Assert.Contains("MemoryColor", csv);
            Assert.Contains("120", csv);
            Assert.Contains("150.5", csv);
            Assert.Contains("False,\"low signal, retry\"", csv);
        }

        [Fact]
        public void BuildCsv_ExportsHdrPqTrackingRowsWithPatchNits()
        {
            var accuracy = Measurement(0, "White", PatchCategory.Grayscale, 1.0, 1.0, 1.0,
                new CieXyz(95, 100, 108), sequenceIndex: 0);
            var pq = Measurement(1, "PQ 320 nits", PatchCategory.General, 0.5, 0.5, 0.5,
                new CieXyz(303, 319, 347), sequenceIndex: 100, nits: 320.0);

            string csv = MeasurementCsvExporter.BuildCsv("report-hdr", "verification", new[] { accuracy, pq });

            Assert.Contains("patch_nits", csv);
            Assert.Contains("PQ 320 nits", csv);
            Assert.Contains(",320,", csv);
            Assert.True(csv.IndexOf("White", StringComparison.Ordinal) < csv.IndexOf("PQ 320 nits", StringComparison.Ordinal));
        }

        [Fact]
        public void ComputeMetrics_IgnoresHdrPqTrackingRows()
        {
            var accuracy = Measurement(0, "White", PatchCategory.Grayscale, 1.0, 1.0, 1.0,
                new CieXyz(95, 100, 108), sequenceIndex: 0);
            var pq = Measurement(1, "PQ 320 nits", PatchCategory.General, 0.5, 0.5, 0.5,
                new CieXyz(303, 319, 347), sequenceIndex: 100, nits: 320.0);

            var metrics = CalibrationVerifier.ComputeMetrics(new[] { accuracy, pq }, StandardTargets.Rec709Pq);

            Assert.Single(metrics.PatchResults);
            Assert.Equal("White", metrics.PatchResults[0].Name);
        }

        [Fact]
        public void BuildCsv_BlanksNonFiniteNumericValues()
        {
            var measurement = Measurement(0, "Corrupt", PatchCategory.Grayscale,
                double.NaN, 1.0, double.PositiveInfinity,
                new CieXyz(double.NaN, double.PositiveInfinity, double.NegativeInfinity),
                nits: double.NaN,
                integrationMs: double.PositiveInfinity);

            string csv = MeasurementCsvExporter.BuildCsv("report-corrupt", "native", new[] { measurement });

            Assert.DoesNotContain("NaN", csv);
            Assert.DoesNotContain("Infinity", csv);
            Assert.Contains("report-corrupt,native", csv);
            Assert.DoesNotContain("0.31272", csv);
            Assert.DoesNotContain("6500", csv);
        }

        [Fact]
        public void BuildCsv_BlanksDerivedChromaticityForInvalidMeasurements()
        {
            var measurement = Measurement(0, "Invalid retry", PatchCategory.Grayscale,
                1.0, 1.0, 1.0,
                new CieXyz(95, 100, 108),
                isValid: false,
                error: "retry");

            string csv = MeasurementCsvExporter.BuildCsv("report-invalid", "native", new[] { measurement });
            string row = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1];

            Assert.Contains("95,100,108,,,,", row);
            Assert.Contains("False,retry", row);
        }

        [Fact]
        public void Save_CreatesParentDirectoryAndUtf8Csv()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"gloam-csv-test-{Guid.NewGuid():N}");
            string path = Path.Combine(dir, "nested", "measurements.csv");

            try
            {
                MeasurementCsvExporter.Save(path, "report-2", "native", new[]
                {
                    Measurement(0, "Black", PatchCategory.Shadow, 0, 0, 0, new CieXyz(0.001, 0.002, 0.003))
                });

                Assert.True(File.Exists(path));
                string text = File.ReadAllText(path);
                Assert.Contains("report-2,native", text);
                Assert.Contains("Black", text);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }

        private static MeasurementResult Measurement(
            int patchIndex,
            string name,
            PatchCategory category,
            double r,
            double g,
            double b,
            CieXyz xyz,
            int sequenceIndex = 0,
            CieXyz? target = null,
            double? nits = null,
            double? integrationMs = null,
            bool isValid = true,
            string? error = null)
        {
            return new MeasurementResult
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{patchIndex + 1:000000000000}"),
                Timestamp = new DateTime(2026, 6, 28, 12, patchIndex, 0, DateTimeKind.Utc),
                SequenceIndex = sequenceIndex,
                Xyz = xyz,
                IntegrationTimeMs = integrationMs,
                IsValid = isValid,
                ErrorMessage = error,
                Patch = new ColorPatch
                {
                    Index = patchIndex,
                    Name = name,
                    Category = category,
                    DisplayRgb = new LinearRgb(r, g, b),
                    TargetXyz = target,
                    Nits = nits
                }
            };
        }
    }
}
