using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tests for the spectrometer capture pipeline: CCSS generation (CcssWriter),
    /// the four-patch capture orchestration (SpectralCaptureService), and
    /// spectrometer-vs-colorimeter instrument classification.
    /// </summary>
    public class SpectralCcssTests
    {
        private static SpectralSample Sample(double start, double end, params double[] values) =>
            new(start, end, values);

        private static CcssWriter.SpectralSet MakeSet()
        {
            // 6-band toy grid 380..730 nm; values chosen so each channel is identifiable.
            return new CcssWriter.SpectralSet(
                White: Sample(380, 730, 0.10, 0.30, 0.50, 0.50, 0.30, 0.10),
                Red:   Sample(380, 730, 0.00, 0.00, 0.01, 0.05, 0.40, 0.30),
                Green: Sample(380, 730, 0.01, 0.05, 0.40, 0.30, 0.05, 0.01),
                Blue:  Sample(380, 730, 0.30, 0.40, 0.05, 0.01, 0.00, 0.00));
        }

        // --- CcssWriter format ---------------------------------------------------

        [Fact]
        public void Build_EmitsExpectedKeywordsAndStructure()
        {
            string content = CcssWriter.Build(
                "DELL U2720Q", "i1 Pro 2", MakeSet(),
                created: new DateTime(2026, 7, 3, 12, 0, 0));

            Assert.StartsWith("CCSS", content);
            Assert.Contains("ORIGINATOR \"Gloam\"", content);
            Assert.Contains("DESCRIPTOR \"DELL U2720Q - Gloam spectral capture\"", content);
            Assert.Contains("KEYWORD \"DISPLAY\"", content);
            Assert.Contains("DISPLAY \"DELL U2720Q\"", content);
            Assert.Contains("KEYWORD \"REFERENCE\"", content);
            Assert.Contains("REFERENCE \"i1 Pro 2\"", content);
            Assert.Contains("DEVICE_CLASS \"DISPLAY\"", content);
            Assert.Contains("SPECTRAL_BANDS \"6\"", content);
            Assert.Contains("SPECTRAL_START_NM \"380.000000\"", content);
            Assert.Contains("SPECTRAL_END_NM \"730.000000\"", content);
            Assert.Contains("SPECTRAL_NORM \"1.000000\"", content);
            Assert.Contains("CREATED \"Fri Jul 3 12:00:00 2026\"", content);
            Assert.Contains("NUMBER_OF_FIELDS 7", content);
            Assert.Contains("SAMPLE_ID SPEC_380 SPEC_450 SPEC_520 SPEC_590 SPEC_660 SPEC_730", content);
            Assert.Contains("KEYWORD \"SPEC_380\"", content); // per-field KEYWORD declarations
            Assert.Contains("NUMBER_OF_SETS 4", content);
            // TECHNOLOGY omitted when unknown.
            Assert.DoesNotContain("TECHNOLOGY", content);
        }

        [Fact]
        public void Build_PassesAppCgatsValidator()
        {
            string content = CcssWriter.Build("Panel X", "ColorMunki Design", MakeSet());
            var result = CgatsValidator.Validate(content, "ccss");
            Assert.True(result.IsValid, result.Error);
        }

        [Fact]
        public void Build_RoundTrips_SpectraSurviveParsing()
        {
            var set = MakeSet();
            string content = CcssWriter.Build("Panel X", "i1 Pro 3", set);

            var rows = ParseDataRows(content, out var wavelengths);
            Assert.Equal(4, rows.Count);
            Assert.Equal(new double[] { 380, 450, 520, 590, 660, 730 }, wavelengths);

            // ccxxmake row order: white first, then R, G, B.
            var expected = new[] { set.White, set.Red, set.Green, set.Blue };
            for (int r = 0; r < 4; r++)
            {
                Assert.Equal(expected[r].Values.Count, rows[r].Count);
                for (int i = 0; i < rows[r].Count; i++)
                    Assert.Equal(expected[r].Values[i], rows[r][i], 6);
            }
        }

        [Fact]
        public void Build_IsParsedByCcssMelanopicEstimator()
        {
            // The estimator is the app's existing CCSS consumer: it must find R/G/B rows.
            string content = CcssWriter.Build("Panel X", "i1 Pro 2", MakeSet());
            var coefficients = CcssMelanopicEstimator.TryEstimate(content, "generated");
            Assert.NotNull(coefficients);
        }

        [Fact]
        public void Build_HighResGrid_ProducesUniqueFieldNames()
        {
            // i1 Pro high-res mode: ~3.33 nm bins. Field names are rounded to whole nm
            // and must stay unique.
            int bands = 106;
            var values = Enumerable.Repeat(0.5, bands).ToArray();
            var sample = new SpectralSample(380, 730, values);
            var names = CcssWriter.BuildSpectralFieldNames(sample);
            Assert.Equal(bands, names.Distinct(StringComparer.Ordinal).Count());

            var set = new CcssWriter.SpectralSet(sample, sample, sample, sample);
            string content = CcssWriter.Build("HiRes Panel", "i1 Pro 2", set);
            Assert.True(CgatsValidator.Validate(content, "ccss").IsValid);
        }

        [Fact]
        public void Build_MismatchedGrids_Throws()
        {
            var set = new CcssWriter.SpectralSet(
                Sample(380, 730, 1, 2, 3, 4, 5, 6),
                Sample(380, 730, 1, 2, 3, 4, 5, 6),
                Sample(400, 700, 1, 2, 3, 4, 5, 6), // different range
                Sample(380, 730, 1, 2, 3, 4, 5, 6));
            Assert.Throws<ArgumentException>(() => CcssWriter.Build("P", "I", set));
        }

        [Fact]
        public void Build_EscapesQuotesInDisplayName()
        {
            string content = CcssWriter.Build("Panel \"27\"", "i1 Pro", MakeSet());
            Assert.True(CgatsValidator.Validate(content, "ccss").IsValid);
            Assert.Contains("DISPLAY \"Panel '27'\"", content);
        }

        [Fact]
        public void SaveToFolder_FileIsListedByCcssBrowser_LocalRegistration()
        {
            // Pairing path: the generated file must land in the corrections folder and be
            // picked up by the same lister the CCSS browser and setup picker use.
            string folder = Path.Combine(Path.GetTempPath(), "gloam-ccss-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = CcssWriter.SaveToFolder("LG OLED42C2", "i1 Pro 2", MakeSet(), folder);
                Assert.True(File.Exists(path));
                Assert.EndsWith(".ccss", path);

                var listed = CcssDatabaseClient.ListSaved(folder, "OLED42C2", "ccss");
                Assert.Single(listed);
                Assert.Equal("LG OLED42C2", listed[0].Display);
                Assert.Equal("i1 Pro 2", listed[0].Reference);
                Assert.Equal(path, listed[0].LocalPath);

                // A second capture of the same panel gets a uniquified name, not a clobber.
                string path2 = CcssWriter.SaveToFolder("LG OLED42C2", "i1 Pro 2", MakeSet(), folder);
                Assert.NotEqual(path, path2);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        }

        [Fact]
        public void ArgyllStyleCcssFixture_StillParses()
        {
            // Shape check against a hand-reduced ccxxmake/DisplayCAL-style file: our
            // validator and estimator must keep accepting the real-world format.
            const string fixture = "CCSS   \n\n" +
                "DESCRIPTOR \"Some Panel\"\n" +
                "ORIGINATOR \"Argyll ccxxmake\"\n" +
                "CREATED \"Fri Feb 21 18:52:31 2014\"\n" +
                "KEYWORD \"DEVICE_CLASS\"\n" +
                "DEVICE_CLASS \"DISPLAY\"\n" +
                "KEYWORD \"DISPLAY\"\n" +
                "DISPLAY \"Some Panel\"\n" +
                "KEYWORD \"TECHNOLOGY\"\n" +
                "TECHNOLOGY \"LCD White LED IPS\"\n" +
                "KEYWORD \"DISPLAY_TYPE_REFRESH\"\n" +
                "DISPLAY_TYPE_REFRESH \"NO\"\n" +
                "KEYWORD \"REFERENCE\"\n" +
                "REFERENCE \"X-Rite i1 Pro\"\n" +
                "KEYWORD \"SPECTRAL_BANDS\"\n" +
                "SPECTRAL_BANDS \"6\"\n" +
                "KEYWORD \"SPECTRAL_START_NM\"\n" +
                "SPECTRAL_START_NM \"380.000000\"\n" +
                "KEYWORD \"SPECTRAL_END_NM\"\n" +
                "SPECTRAL_END_NM \"730.000000\"\n" +
                "KEYWORD \"SPECTRAL_NORM\"\n" +
                "SPECTRAL_NORM \"1.000000\"\n" +
                "KEYWORD \"SPEC_380\"\nKEYWORD \"SPEC_450\"\nKEYWORD \"SPEC_520\"\n" +
                "KEYWORD \"SPEC_590\"\nKEYWORD \"SPEC_660\"\nKEYWORD \"SPEC_730\"\n" +
                "NUMBER_OF_FIELDS 7\n" +
                "BEGIN_DATA_FORMAT\n" +
                "SAMPLE_ID SPEC_380 SPEC_450 SPEC_520 SPEC_590 SPEC_660 SPEC_730\n" +
                "END_DATA_FORMAT\n\n" +
                "NUMBER_OF_SETS 4\n" +
                "BEGIN_DATA\n" +
                "1 1.000000e-01 3.000000e-01 5.000000e-01 5.000000e-01 3.000000e-01 1.000000e-01\n" +
                "2 0.000000e+00 0.000000e+00 1.000000e-02 5.000000e-02 4.000000e-01 3.000000e-01\n" +
                "3 1.000000e-02 5.000000e-02 4.000000e-01 3.000000e-01 5.000000e-02 1.000000e-02\n" +
                "4 3.000000e-01 4.000000e-01 5.000000e-02 1.000000e-02 0.000000e+00 0.000000e+00\n" +
                "END_DATA\n";

            Assert.True(CgatsValidator.Validate(fixture, "ccss").IsValid);
            Assert.NotNull(CcssMelanopicEstimator.TryEstimate(fixture, "fixture"));
        }

        // --- SpectralCaptureService ------------------------------------------------

        [Fact]
        public async Task CaptureAsync_ShowsPatchesInCcssOrder_AndMediansReads()
        {
            var shown = new List<(double R, double G, double B)>();
            int readIndex = 0;

            // Per-read spectra: base value depends on patch, jitter depends on read index
            // (0, +10, -10) so the median is exactly the base value.
            double[] jitter = { 0.0, 10.0, -10.0 };
            Task<SpectralReading> Measure(CancellationToken _)
            {
                int patch = readIndex / 3;
                double baseValue = (patch + 1) * 100.0;
                double v = baseValue + jitter[readIndex % 3];
                readIndex++;
                // White luminance 50 cd/m2 -> normalization scale 2.0.
                var xyz = new CieXyz(40.0, 50.0 + jitter[(readIndex - 1) % 3], 60.0);
                return Task.FromResult(new SpectralReading(
                    xyz, new SpectralSample(380, 730, new[] { v, v * 2 })));
            }

            var service = new SpectralCaptureService(
                rgb => { shown.Add(rgb); return Task.CompletedTask; },
                Measure)
            {
                ReadsPerPatch = 3,
                SettleDelay = TimeSpan.Zero,
            };

            // 2 bands only -> CcssWriter would reject, but capture itself is band-agnostic.
            var set = await service.CaptureAsync();

            Assert.Equal(new[]
            {
                (1.0, 1.0, 1.0), // White first (CCSS convention)
                (1.0, 0.0, 0.0),
                (0.0, 1.0, 0.0),
                (0.0, 0.0, 1.0),
            }, shown);
            Assert.Equal(12, readIndex);

            // Median white Y = 50 -> scale = 2. White base 100 -> 200 after normalization.
            Assert.Equal(200.0, set.White.Values[0], 6);
            Assert.Equal(400.0, set.White.Values[1], 6);
            Assert.Equal(400.0, set.Red.Values[0], 6);   // base 200 * 2
            Assert.Equal(600.0, set.Green.Values[0], 6); // base 300 * 2
            Assert.Equal(800.0, set.Blue.Values[0], 6);  // base 400 * 2
        }

        [Fact]
        public async Task CaptureAsync_ZeroWhiteLuminance_Throws()
        {
            var service = new SpectralCaptureService(
                _ => Task.CompletedTask,
                _ => Task.FromResult(new SpectralReading(
                    new CieXyz(0, 0, 0), new SpectralSample(380, 730, new[] { 0.0, 0.0 }))))
            {
                SettleDelay = TimeSpan.Zero,
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());
            Assert.Contains("no luminance", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MedianSpectrum_GridChangeMidCapture_Throws()
        {
            var spectra = new List<SpectralSample>
            {
                new(380, 730, new[] { 1.0, 2.0 }),
                new(380, 720, new[] { 1.0, 2.0 }), // grid changed
            };
            Assert.Throws<InvalidOperationException>(() => SpectralCaptureService.MedianSpectrum(spectra));
        }

        [Fact]
        public void MedianSpectrum_EvenCount_AveragesMiddlePair()
        {
            var spectra = new List<SpectralSample>
            {
                new(380, 730, new[] { 1.0 }),
                new(380, 730, new[] { 5.0 }),
                new(380, 730, new[] { 2.0 }),
                new(380, 730, new[] { 4.0 }),
            };
            var median = SpectralCaptureService.MedianSpectrum(spectra);
            Assert.Equal(3.0, median.Values[0], 9);
        }

        // --- spectrometer identification --------------------------------------------

        [Theory]
        [InlineData("usb:/1 (X-Rite i1 Pro 2)", true)]
        [InlineData("X-Rite i1 Pro 3", true)]
        [InlineData("GretagMacbeth Eye-One Pro", true)]
        [InlineData("X-Rite ColorMunki Design", true)]
        [InlineData("X-Rite ColorMunki Photo", true)]
        [InlineData("X-Rite i1Studio", true)]
        [InlineData("hid:/10 (X-Rite i1 DisplayPro, ColorMunki Display)", false)] // colorimeter
        [InlineData("X-Rite ColorMunki Smile", false)] // colorimeter despite the family name
        [InlineData("Datacolor SpyderX2", false)]
        [InlineData("i1 Display Plus", false)]
        [InlineData("DTP94", false)]
        [InlineData("Some Unknown Meter", false)] // conservative: unknown -> not a spectro
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsSpectrometerName_ClassifiesInstruments(string? name, bool expected)
        {
            Assert.Equal(expected, ColorimeterService.IsSpectrometerName(name));
        }

        // --- helpers -----------------------------------------------------------------

        private static List<List<double>> ParseDataRows(string content, out List<double> wavelengths)
        {
            var lines = content.Replace("\r\n", "\n").Split('\n');
            wavelengths = new List<double>();
            var rows = new List<List<double>>();
            bool inFormat = false, inData = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line == "BEGIN_DATA_FORMAT") { inFormat = true; continue; }
                if (line == "END_DATA_FORMAT") { inFormat = false; continue; }
                if (line == "BEGIN_DATA") { inData = true; continue; }
                if (line == "END_DATA") break;

                if (inFormat)
                {
                    foreach (string field in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (field.StartsWith("SPEC_", StringComparison.OrdinalIgnoreCase))
                            wavelengths.Add(double.Parse(field[5..], CultureInfo.InvariantCulture));
                    }
                }
                else if (inData && line.Length > 0)
                {
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    rows.Add(tokens.Skip(1) // SAMPLE_ID
                        .Select(t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture))
                        .ToList());
                }
            }
            return rows;
        }
    }
}
