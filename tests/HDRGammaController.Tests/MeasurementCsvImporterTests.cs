using System;
using System.IO;
using System.Linq;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    public class MeasurementCsvImporterTests
    {
        [Fact]
        public void Parse_RoundTripsExporterOutputFieldForField()
        {
            var original = new[]
            {
                Measurement(0, "White", PatchCategory.Grayscale, 1.0, 1.0, 1.0,
                    new CieXyz(95.047, 100.0, 108.883), sequenceIndex: 0,
                    target: new CieXyz(95.047, 100.0, 108.883), integrationMs: 220.25),
                Measurement(1, "Skin, bright", PatchCategory.MemoryColor, 0.7, 0.5, 0.4,
                    new CieXyz(0.38123456789012345, 0.35, 0.22), sequenceIndex: 1,
                    target: new CieXyz(0.4, 0.36, 0.24), isValid: false, error: "low signal, retry"),
                Measurement(2, "PQ 320 nits", PatchCategory.General, 0.5, 0.5, 0.5,
                    new CieXyz(303, 319, 347), sequenceIndex: 2, nits: 320.0),
            };

            string csv = MeasurementCsvExporter.BuildCsv("report-rt", "verification", original);
            var imported = MeasurementCsvImporter.Parse(new StringReader(csv));

            Assert.Equal("report-rt", imported.ReportId);
            Assert.Equal("verification", imported.Phase);
            Assert.Equal(original.Length, imported.Measurements.Count);

            for (int i = 0; i < original.Length; i++)
            {
                var expected = original[i];
                var actual = imported.Measurements[i];
                Assert.Equal(expected.Id, actual.Id);
                Assert.Equal(expected.Timestamp, actual.Timestamp);
                Assert.Equal(expected.SequenceIndex, actual.SequenceIndex);
                Assert.Equal(expected.Xyz.X, actual.Xyz.X);
                Assert.Equal(expected.Xyz.Y, actual.Xyz.Y);
                Assert.Equal(expected.Xyz.Z, actual.Xyz.Z);
                Assert.Equal(expected.IntegrationTimeMs, actual.IntegrationTimeMs);
                Assert.Equal(expected.IsValid, actual.IsValid);
                Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
                Assert.Equal(expected.Patch.Index, actual.Patch.Index);
                Assert.Equal(expected.Patch.Name, actual.Patch.Name);
                Assert.Equal(expected.Patch.Category, actual.Patch.Category);
                Assert.Equal(expected.Patch.DisplayRgb.R, actual.Patch.DisplayRgb.R);
                Assert.Equal(expected.Patch.DisplayRgb.G, actual.Patch.DisplayRgb.G);
                Assert.Equal(expected.Patch.DisplayRgb.B, actual.Patch.DisplayRgb.B);
                Assert.Equal(expected.Patch.Nits, actual.Patch.Nits);
                Assert.Equal(expected.Patch.TargetXyz?.X, actual.Patch.TargetXyz?.X);
                Assert.Equal(expected.Patch.TargetXyz?.Y, actual.Patch.TargetXyz?.Y);
                Assert.Equal(expected.Patch.TargetXyz?.Z, actual.Patch.TargetXyz?.Z);
            }
        }

        [Fact]
        public void Parse_HandlesQuotedCommasQuotesAndNewlines()
        {
            var withQuotes = Measurement(0, "Patch \"A\", first\nline two", PatchCategory.General,
                0.1, 0.2, 0.3, new CieXyz(1, 2, 3), error: "contains, comma and \"quotes\"", isValid: false);

            string csv = MeasurementCsvExporter.BuildCsv("r", "native", new[] { withQuotes });
            var imported = MeasurementCsvImporter.Parse(new StringReader(csv));

            var m = Assert.Single(imported.Measurements);
            Assert.Equal("Patch \"A\", first\nline two", m.Patch.Name);
            Assert.Equal("contains, comma and \"quotes\"", m.ErrorMessage);
        }

        [Fact]
        public void Parse_IgnoresUnknownExtraColumns()
        {
            string csv =
                "report_id,phase,sequence_index,measurement_id,timestamp_utc,patch_index,patch_name," +
                "patch_category,display_r,display_g,display_b,patch_nits,target_x,target_y,target_z," +
                "measured_x,measured_y_nits,measured_z,measured_xy_x,measured_xy_y,cct_k,duv," +
                "integration_time_ms,is_valid,error_message,future_column\n" +
                "r1,native,0,11111111-1111-1111-1111-111111111111,2026-07-09T00:00:00.0000000Z," +
                "0,White,Grayscale,1,1,1,,,,,95,100,108,,,,,,True,,surprise\n";

            var imported = MeasurementCsvImporter.Parse(new StringReader(csv));

            var m = Assert.Single(imported.Measurements);
            Assert.Equal("White", m.Patch.Name);
            Assert.Equal(100.0, m.Xyz.Y);
        }

        [Fact]
        public void Parse_MissingRequiredColumn_ThrowsWithColumnName()
        {
            string csv = "report_id,phase,sequence_index,patch_index,patch_name\nr,native,0,0,White\n";

            var ex = Assert.Throws<InvalidDataException>(
                () => MeasurementCsvImporter.Parse(new StringReader(csv)));
            Assert.Contains("measured_y_nits", ex.Message);
        }

        [Fact]
        public void Parse_InvalidMeasurementRow_PreservedAsInvalid()
        {
            var invalid = Measurement(0, "Invalid retry", PatchCategory.Grayscale, 1, 1, 1,
                new CieXyz(95, 100, 108), isValid: false, error: "retry");

            string csv = MeasurementCsvExporter.BuildCsv("r", "native", new[] { invalid });
            var imported = MeasurementCsvImporter.Parse(new StringReader(csv));

            var m = Assert.Single(imported.Measurements);
            Assert.False(m.IsValid);
            Assert.Equal("retry", m.ErrorMessage);
        }

        [Fact]
        public void Parse_NonFiniteBlankedXyz_RoundTripsAsNaN()
        {
            var corrupt = Measurement(0, "Corrupt", PatchCategory.Grayscale, 1, 1, 1,
                new CieXyz(double.NaN, double.PositiveInfinity, double.NegativeInfinity));

            string csv = MeasurementCsvExporter.BuildCsv("r", "native", new[] { corrupt });
            var imported = MeasurementCsvImporter.Parse(new StringReader(csv));

            var m = Assert.Single(imported.Measurements);
            Assert.True(double.IsNaN(m.Xyz.X));
            Assert.True(double.IsNaN(m.Xyz.Y));
            Assert.True(double.IsNaN(m.Xyz.Z));
        }

        [Fact]
        public void Parse_WireLadderNits_Reconstructed()
        {
            var rungs = new[] { 2.0, 100.0, 650.0 }
                .Select((nits, i) => Measurement(i, $"PQ {nits} nits", PatchCategory.General,
                    0.5, 0.5, 0.5, new CieXyz(nits * 0.95, nits, nits * 1.08),
                    sequenceIndex: i, nits: nits))
                .ToArray();

            string csv = MeasurementCsvExporter.BuildCsv("r-hdr", "hdr-ladder", rungs);
            var imported = MeasurementCsvImporter.Parse(new StringReader(csv));

            Assert.Equal(new double?[] { 2.0, 100.0, 650.0 },
                imported.Measurements.Select(m => m.Patch.Nits).ToArray());
        }

        [Fact]
        public void Load_ReadsFileWrittenBySave()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"gloam-csv-import-{Guid.NewGuid():N}");
            string path = Path.Combine(dir, "m.csv");
            try
            {
                MeasurementCsvExporter.Save(path, "r-file", "native", new[]
                {
                    Measurement(0, "Black", PatchCategory.Shadow, 0, 0, 0, new CieXyz(0.001, 0.002, 0.003))
                });

                var imported = MeasurementCsvImporter.Load(path);

                Assert.Equal("r-file", imported.ReportId);
                var m = Assert.Single(imported.Measurements);
                Assert.Equal(0.002, m.Xyz.Y);
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void Parse_EmptyInput_Throws()
        {
            Assert.Throws<InvalidDataException>(
                () => MeasurementCsvImporter.Parse(new StringReader(string.Empty)));
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
                Timestamp = new DateTime(2026, 7, 9, 12, patchIndex, 0, DateTimeKind.Utc),
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
