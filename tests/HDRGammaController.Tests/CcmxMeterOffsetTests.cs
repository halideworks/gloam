using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tests for the two-instrument CCMX workflow: the weighted least-squares matrix
    /// solver, the Argyll-shaped CCMX writer/parser, and the two-phase capture
    /// orchestration (MeterOffsetCaptureService) with its stability refusals.
    /// </summary>
    public class CcmxMeterOffsetTests
    {
        // Realistic full-drive W/R/G/B colorimeter readings (cd/m2-scaled XYZ, sRGB-ish).
        private static readonly CieXyz[] ColorimeterPatches =
        {
            new(95.047, 100.000, 108.883), // White
            new(41.240, 21.260, 1.930),    // Red
            new(35.760, 71.520, 11.920),   // Green
            new(18.050, 7.220, 95.050),    // Blue
        };

        private static readonly double[,] KnownMatrix =
        {
            { 1.0421, 0.0312, -0.0154 },
            { 0.0087, 0.9873, 0.0065 },
            { -0.0032, 0.0121, 1.0958 },
        };

        private static CieXyz[] MapAll(double[,] m, IReadOnlyList<CieXyz> inputs) =>
            inputs.Select(c => CcmxWriter.Apply(m, c)).ToArray();

        private static double SumSquaredError(double[,] m, IReadOnlyList<CieXyz> c, IReadOnlyList<CieXyz> r)
        {
            double sse = 0;
            for (int i = 0; i < c.Count; i++)
            {
                var mapped = CcmxWriter.Apply(m, c[i]);
                sse += Sq(mapped.X - r[i].X) + Sq(mapped.Y - r[i].Y) + Sq(mapped.Z - r[i].Z);
            }
            return sse;
        }

        private static double Sq(double v) => v * v;

        // --- Solver ---------------------------------------------------------------

        [Fact]
        public void Solve_PerfectData_RecoversKnownMatrixExactly()
        {
            var reference = MapAll(KnownMatrix, ColorimeterPatches);

            var solved = CcmxWriter.SolveCorrectionMatrix(ColorimeterPatches, reference);

            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(KnownMatrix[r, c], solved[r, c], 8);
        }

        [Fact]
        public void Solve_PerfectData_WhiteWeightDoesNotBiasExactSolution()
        {
            // With consistent data the residual is zero regardless of weighting, so the
            // white emphasis must not distort an exact solve.
            var reference = MapAll(KnownMatrix, ColorimeterPatches);

            var w1 = CcmxWriter.SolveCorrectionMatrix(ColorimeterPatches, reference, whiteWeight: 1.0);
            var w5 = CcmxWriter.SolveCorrectionMatrix(ColorimeterPatches, reference, whiteWeight: 5.0);

            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(w1[r, c], w5[r, c], 8);
        }

        [Fact]
        public void Solve_NoisyData_BeatsSinglePatchWhiteScaling()
        {
            // Perturb the reference readings with small deterministic noise, then compare
            // the least-squares fit against the naive single-patch alternative (a diagonal
            // matrix scaled so the white patch alone maps exactly).
            var rng = new Random(42);
            var reference = MapAll(KnownMatrix, ColorimeterPatches)
                .Select(x => new CieXyz(
                    x.X * (1 + (rng.NextDouble() - 0.5) * 0.02),
                    x.Y * (1 + (rng.NextDouble() - 0.5) * 0.02),
                    x.Z * (1 + (rng.NextDouble() - 0.5) * 0.02)))
                .ToArray();

            var leastSquares = CcmxWriter.SolveCorrectionMatrix(ColorimeterPatches, reference);

            var whiteOnly = new double[3, 3];
            whiteOnly[0, 0] = reference[0].X / ColorimeterPatches[0].X;
            whiteOnly[1, 1] = reference[0].Y / ColorimeterPatches[0].Y;
            whiteOnly[2, 2] = reference[0].Z / ColorimeterPatches[0].Z;

            double lsError = SumSquaredError(leastSquares, ColorimeterPatches, reference);
            double whiteOnlyError = SumSquaredError(whiteOnly, ColorimeterPatches, reference);

            Assert.True(lsError < whiteOnlyError,
                $"Least-squares SSE {lsError:F4} should beat single-patch white scaling SSE {whiteOnlyError:F4}.");
        }

        [Fact]
        public void Solve_WhiteWeight_PullsFitTowardWhitePatch()
        {
            // Make the data inconsistent by perturbing ONLY the white reference reading;
            // increasing the white weight must then reduce the white patch's residual.
            var reference = MapAll(KnownMatrix, ColorimeterPatches);
            reference[0] = new CieXyz(reference[0].X + 1.5, reference[0].Y - 1.0, reference[0].Z + 0.8);

            double WhiteError(double weight)
            {
                var m = CcmxWriter.SolveCorrectionMatrix(ColorimeterPatches, reference, whiteWeight: weight);
                var mapped = CcmxWriter.Apply(m, ColorimeterPatches[0]);
                return Math.Sqrt(
                    Sq(mapped.X - reference[0].X) + Sq(mapped.Y - reference[0].Y) + Sq(mapped.Z - reference[0].Z));
            }

            double unweighted = WhiteError(1.0);
            double weighted = WhiteError(2.0);

            Assert.True(unweighted > 1e-6, "Test setup should be inconsistent enough to leave a white residual.");
            Assert.True(weighted < unweighted,
                $"White weight 2x should reduce the white residual ({weighted:F6} vs {unweighted:F6}).");
        }

        [Fact]
        public void Solve_NearCoplanarReadings_Refuses()
        {
            // All colorimeter vectors in the X=Y plane: rank 2, no 3x3 fit possible.
            var coplanar = new[]
            {
                new CieXyz(100, 100, 40),
                new CieXyz(50, 50, 10),
                new CieXyz(70, 70, 90),
                new CieXyz(20, 20, 60),
            };
            var reference = MapAll(KnownMatrix, coplanar);

            var ex = Assert.Throws<InvalidOperationException>(
                () => CcmxWriter.SolveCorrectionMatrix(coplanar, reference));
            Assert.Contains("coplanar", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Solve_RealisticReadings_AreNotFlaggedCoplanar()
        {
            // Guard against an over-eager conditioning threshold: normalized-scale readings
            // (white Y = 1) of a real panel must pass exactly like cd/m2-scale ones.
            var scaled = ColorimeterPatches.Select(c => new CieXyz(c.X / 100, c.Y / 100, c.Z / 100)).ToArray();
            var reference = MapAll(KnownMatrix, scaled);
            var solved = CcmxWriter.SolveCorrectionMatrix(scaled, reference);
            Assert.Equal(KnownMatrix[0, 0], solved[0, 0], 8);
        }

        [Fact]
        public void Solve_NonPhysicalReading_Refuses()
        {
            var readings = (CieXyz[])ColorimeterPatches.Clone();
            readings[2] = new CieXyz(35.76, -5.0, 11.92); // negative Y is non-physical
            var reference = MapAll(KnownMatrix, ColorimeterPatches);

            var ex = Assert.Throws<InvalidOperationException>(
                () => CcmxWriter.SolveCorrectionMatrix(readings, reference));
            Assert.Contains("non-physical", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Solve_TooFewOrMismatchedPatches_Refuses()
        {
            var three = ColorimeterPatches.Take(3).ToArray();
            Assert.Throws<ArgumentException>(
                () => CcmxWriter.SolveCorrectionMatrix(three, MapAll(KnownMatrix, three)));

            Assert.Throws<ArgumentException>(
                () => CcmxWriter.SolveCorrectionMatrix(ColorimeterPatches, MapAll(KnownMatrix, three)));
        }

        // --- CCMX format ------------------------------------------------------------

        [Fact]
        public void Build_EmitsArgyllCcmxStructure()
        {
            string content = CcmxWriter.Build(
                "DELL U2720Q", "i1 Display Plus", "i1 Pro 2", KnownMatrix,
                created: new DateTime(2026, 7, 3, 12, 0, 0));

            Assert.StartsWith("CCMX", content);
            Assert.Contains("ORIGINATOR \"Gloam\"", content);
            Assert.Contains("CREATED \"Fri Jul 3 12:00:00 2026\"", content);
            Assert.Contains("KEYWORD \"INSTRUMENT\"", content);
            Assert.Contains("INSTRUMENT \"i1 Display Plus\"", content);
            Assert.Contains("KEYWORD \"REFERENCE\"", content);
            Assert.Contains("REFERENCE \"i1 Pro 2\"", content);
            Assert.Contains("KEYWORD \"DISPLAY\"", content);
            Assert.Contains("DISPLAY \"DELL U2720Q\"", content);
            Assert.Contains("COLOR_REP \"XYZ\"", content);
            Assert.Contains("NUMBER_OF_FIELDS 3", content);
            Assert.Contains("XYZ_X XYZ_Y XYZ_Z", content);
            Assert.Contains("NUMBER_OF_SETS 3", content);
            // TECHNOLOGY omitted when unknown.
            Assert.DoesNotContain("TECHNOLOGY", content);

            var result = CgatsValidator.Validate(content, "ccmx");
            Assert.True(result.IsValid, result.Error);
        }

        [Fact]
        public void Build_RoundTrips_MatrixSurvivesParsing()
        {
            string content = CcmxWriter.Build("Panel X", "SpyderX", "i1 Pro 3", KnownMatrix);

            Assert.True(CcmxWriter.TryParseMatrix(content, out var parsed));
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(KnownMatrix[r, c], parsed[r, c], 6); // writer emits 6 decimals
        }

        [Fact]
        public void ArgyllStyleFixture_ValidatesAndParses()
        {
            // Hand-reduced fixture matching the shape ArgyllCMS's ccxxmake writes and the
            // DisplayCAL corrections database serves.
            const string fixture = "CCMX   \n" +
                "\n" +
                "DESCRIPTOR \"Dell 2408WFP\"\n" +
                "ORIGINATOR \"ArgyllCMS ccxxmake\"\n" +
                "CREATED \"Fri Feb 21 18:52:31 2014\"\n" +
                "KEYWORD \"INSTRUMENT\"\n" +
                "INSTRUMENT \"X-Rite i1 DisplayPro, ColorMunki Display\"\n" +
                "KEYWORD \"DISPLAY\"\n" +
                "DISPLAY \"Dell 2408WFP\"\n" +
                "KEYWORD \"TECHNOLOGY\"\n" +
                "TECHNOLOGY \"LCD CCFL\"\n" +
                "KEYWORD \"REFERENCE\"\n" +
                "REFERENCE \"X-Rite i1 Pro\"\n" +
                "KEYWORD \"COLOR_REP\"\n" +
                "COLOR_REP \"XYZ\"\n" +
                "\n" +
                "NUMBER_OF_FIELDS 3\n" +
                "BEGIN_DATA_FORMAT\n" +
                "XYZ_X XYZ_Y XYZ_Z\n" +
                "END_DATA_FORMAT\n" +
                "\n" +
                "NUMBER_OF_SETS 3\n" +
                "BEGIN_DATA\n" +
                "1.052749 0.014164 -0.033923\n" +
                "0.021653 1.012544 -0.021666\n" +
                "-0.011799 0.019810 1.101638\n" +
                "END_DATA\n";

            var result = CgatsValidator.Validate(fixture, "ccmx");
            Assert.True(result.IsValid, result.Error);

            Assert.True(CcmxWriter.TryParseMatrix(fixture, out var matrix));
            Assert.Equal(1.052749, matrix[0, 0], 9);
            Assert.Equal(-0.033923, matrix[0, 2], 9);
            Assert.Equal(1.012544, matrix[1, 1], 9);
            Assert.Equal(-0.011799, matrix[2, 0], 9);
            Assert.Equal(1.101638, matrix[2, 2], 9);
        }

        [Fact]
        public void TryParseMatrix_RejectsMalformedBodies()
        {
            Assert.False(CcmxWriter.TryParseMatrix(null, out _));
            Assert.False(CcmxWriter.TryParseMatrix("", out _));
            Assert.False(CcmxWriter.TryParseMatrix("CCMX\nBEGIN_DATA\n1 2 3\nEND_DATA\n", out _)); // no data format
            Assert.False(CcmxWriter.TryParseMatrix(
                "CCMX\nBEGIN_DATA_FORMAT\nXYZ_X XYZ_Y XYZ_Z\nEND_DATA_FORMAT\nBEGIN_DATA\n1 2 3\n4 5 6\nEND_DATA\n",
                out _)); // only 2 rows
        }

        [Fact]
        public void SaveToFolder_WritesFileListedAlongsideCcss()
        {
            string folder = Path.Combine(Path.GetTempPath(), "gloam-ccmx-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                string ccmxPath = CcmxWriter.SaveToFolder(
                    "Panel X", "i1 Display Plus", "i1 Pro 2", KnownMatrix, folder);
                Assert.True(File.Exists(ccmxPath));
                Assert.EndsWith(".ccmx", ccmxPath);

                var set = new CcssWriter.SpectralSet(
                    White: new SpectralSample(380, 730, new double[] { 0.1, 0.3, 0.5, 0.5, 0.3, 0.1 }),
                    Red: new SpectralSample(380, 730, new double[] { 0, 0, 0.01, 0.05, 0.4, 0.3 }),
                    Green: new SpectralSample(380, 730, new double[] { 0.01, 0.05, 0.4, 0.3, 0.05, 0.01 }),
                    Blue: new SpectralSample(380, 730, new double[] { 0.3, 0.4, 0.05, 0.01, 0, 0 }));
                string ccssPath = CcssWriter.SaveToFolder("Panel X", "i1 Pro 2", set, folder);
                Assert.True(File.Exists(ccssPath));

                // The corrections picker source must list BOTH correction types with tags.
                var listed = CcssDatabaseClient.ListSaved(folder, "");
                Assert.Equal(2, listed.Count);
                Assert.Contains(listed, e => e.Type == "ccmx" && e.LocalPath == ccmxPath);
                Assert.Contains(listed, e => e.Type == "ccss" && e.LocalPath == ccssPath);

                // Both keyword sets survive the local metadata parse.
                var ccmxEntry = listed.Single(e => e.Type == "ccmx");
                Assert.Equal("Panel X", ccmxEntry.Display);
                Assert.Equal("i1 Display Plus", ccmxEntry.Instrument);
                Assert.Equal("i1 Pro 2", ccmxEntry.Reference);

                // And a type-filtered listing still isolates each kind.
                Assert.Single(CcssDatabaseClient.ListSaved(folder, "", "ccmx"));
                Assert.Single(CcssDatabaseClient.ListSaved(folder, "", "ccss"));
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }
            }
        }

        // --- Two-phase capture orchestration -----------------------------------------

        /// <summary>
        /// Fake instrument harness: values are generated per (phase, patch slot, read) so
        /// ordering and median behavior are fully deterministic. Patch slots per phase:
        /// 0=W, 1=R, 2=G, 3=B, 4=white stability recheck.
        /// </summary>
        private sealed class FakeRig
        {
            public readonly List<(double R, double G, double B)> Shown = new();
            public int SwapCalls;
            public int Phase1Reads;
            public int Phase2Reads;
            public bool SwapResult = true;
            public int Phase1ReadsAtSwap = -1;

            // Per-slot base Y; recheck slot (4) reuses the white slot base unless overridden.
            public Func<int, double> Phase1BaseY = slot => slot == 4 ? 100 : 100 * (slot + 1);
            public Func<int, double> Phase2BaseY = slot => slot == 4 ? 50 : 50 * (slot + 1);

            // Read offsets so the median (middle value) is base + 1.
            private static readonly double[] Offsets = { 1, 0, 2 };

            public MeterOffsetCaptureService CreateService(int readsPerPatch = 3)
            {
                var service = new MeterOffsetCaptureService(
                    rgb => { Shown.Add(rgb); return Task.CompletedTask; },
                    _ =>
                    {
                        int slot = Phase1Reads / readsPerPatch;
                        double y = Phase1BaseY(slot) + Offsets[Phase1Reads % readsPerPatch];
                        Phase1Reads++;
                        return Task.FromResult(new CieXyz(y * 0.9, y, y * 1.1));
                    },
                    _ =>
                    {
                        SwapCalls++;
                        Phase1ReadsAtSwap = Phase1Reads;
                        return Task.FromResult(SwapResult);
                    },
                    _ =>
                    {
                        int slot = Phase2Reads / readsPerPatch;
                        double y = Phase2BaseY(slot) + Offsets[Phase2Reads % readsPerPatch];
                        Phase2Reads++;
                        return Task.FromResult(new CieXyz(y * 0.9, y, y * 1.1));
                    });
                service.ReadsPerPatch = readsPerPatch;
                service.SettleDelay = TimeSpan.Zero;
                return service;
            }
        }

        [Fact]
        public async Task Capture_RunsPhasesInOrder_WithMediansAndOneSwap()
        {
            var rig = new FakeRig();
            var service = rig.CreateService();

            var readings = await service.CaptureAsync();

            // Patch sequence per phase: W, R, G, B, then the white recheck. Twice.
            var expectedSequence = new[]
            {
                (1.0, 1.0, 1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 1.0, 1.0),
                (1.0, 1.0, 1.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 1.0, 1.0),
            };
            Assert.Equal(expectedSequence, rig.Shown.ToArray());

            // The swap happened exactly once, after ALL phase-1 reads (incl. recheck) and
            // before any phase-2 read.
            Assert.Equal(1, rig.SwapCalls);
            Assert.Equal(15, rig.Phase1ReadsAtSwap);
            Assert.Equal(15, rig.Phase1Reads);
            Assert.Equal(15, rig.Phase2Reads);

            // Medians: reads are base+{1,0,2} so each patch's median Y is base+1; the
            // recheck read is not part of the returned data.
            Assert.Equal(4, readings.FirstInstrument.Count);
            Assert.Equal(4, readings.SecondInstrument.Count);
            Assert.Equal(new[] { 101.0, 201.0, 301.0, 401.0 }, readings.FirstInstrument.Select(x => x.Y).ToArray());
            Assert.Equal(new[] { 51.0, 101.0, 151.0, 201.0 }, readings.SecondInstrument.Select(x => x.Y).ToArray());
            Assert.Equal(101.0 * 0.9, readings.FirstInstrument[0].X, 9);
            Assert.Equal(101.0 * 1.1, readings.FirstInstrument[0].Z, 9);
        }

        [Fact]
        public async Task Capture_WhiteDriftBeyondLimit_RefusesBeforeSwap()
        {
            var rig = new FakeRig
            {
                // Recheck white comes back 10% darker than the starting white.
                Phase1BaseY = slot => slot == 4 ? 90 : 100 * (slot + 1),
            };
            var service = rig.CreateService();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());
            Assert.Contains("drift", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, rig.SwapCalls); // refused before the user was asked to swap
            Assert.Equal(0, rig.Phase2Reads);
        }

        [Fact]
        public async Task Capture_WhiteDriftWithinLimit_Passes()
        {
            var rig = new FakeRig
            {
                // 2% drift on the recheck: inside the default 3% limit.
                Phase1BaseY = slot => slot == 4 ? 98 : 100 * (slot + 1),
            };
            var service = rig.CreateService();

            var readings = await service.CaptureAsync();
            Assert.Equal(4, readings.FirstInstrument.Count);
            Assert.Equal(1, rig.SwapCalls);
        }

        [Fact]
        public async Task Capture_SecondPhaseDrift_AlsoRefuses()
        {
            var rig = new FakeRig
            {
                Phase2BaseY = slot => slot == 4 ? 40 : 50 * (slot + 1), // 20% drift in phase 2
            };
            var service = rig.CreateService();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());
            Assert.Contains("drift", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, rig.SwapCalls); // phase 1 was fine, so the swap did happen
        }

        [Fact]
        public async Task Capture_SwapDeclined_CancelsWithoutSecondPhase()
        {
            var rig = new FakeRig { SwapResult = false };
            var service = rig.CreateService();

            await Assert.ThrowsAsync<OperationCanceledException>(() => service.CaptureAsync());
            Assert.Equal(15, rig.Phase1Reads);
            Assert.Equal(0, rig.Phase2Reads);
        }

        [Fact]
        public async Task Capture_NonPhysicalReading_Refuses()
        {
            int reads = 0;
            var service = new MeterOffsetCaptureService(
                _ => Task.CompletedTask,
                _ => Task.FromResult(reads++ == 4
                    ? new CieXyz(10, -5, 10) // negative Y mid-capture
                    : new CieXyz(90, 100, 110)),
                _ => Task.FromResult(true),
                _ => Task.FromResult(new CieXyz(90, 100, 110)))
            {
                SettleDelay = TimeSpan.Zero,
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());
            Assert.Contains("non-physical", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Capture_Cancellation_StopsPromptly()
        {
            using var cts = new CancellationTokenSource();
            int reads = 0;
            var service = new MeterOffsetCaptureService(
                _ => Task.CompletedTask,
                _ =>
                {
                    if (++reads == 2) cts.Cancel();
                    return Task.FromResult(new CieXyz(90, 100, 110));
                },
                _ => Task.FromResult(true),
                _ => Task.FromResult(new CieXyz(90, 100, 110)))
            {
                SettleDelay = TimeSpan.Zero,
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureAsync(cts.Token));
            Assert.True(reads <= 3, $"Capture should stop promptly after cancellation (took {reads} reads).");
        }

        [Fact]
        public async Task Capture_EndToEnd_SolvedMatrixRoundTripsThroughCcmxFile()
        {
            // Full pipeline on synthetic instruments: instrument readings differ from the
            // reference by the known matrix; capture, solve, write, reparse, compare.
            var patches = MeterOffsetCaptureService.Patches;
            CieXyz ColorimeterValue(int slot) => ColorimeterPatches[slot == 4 ? 0 : slot];

            int firstReads = 0, secondReads = 0;
            var service = new MeterOffsetCaptureService(
                _ => Task.CompletedTask,
                _ =>
                {
                    int slot = firstReads++ / 3;
                    return Task.FromResult(ColorimeterValue(slot)); // phase 1: the colorimeter
                },
                _ => Task.FromResult(true),
                _ =>
                {
                    int slot = secondReads++ / 3;
                    return Task.FromResult(CcmxWriter.Apply(KnownMatrix, ColorimeterValue(slot))); // phase 2: the spectro
                })
            {
                SettleDelay = TimeSpan.Zero,
            };

            var readings = await service.CaptureAsync();
            Assert.Equal(patches.Count, readings.FirstInstrument.Count);

            var solved = CcmxWriter.SolveCorrectionMatrix(readings.FirstInstrument, readings.SecondInstrument);
            string content = CcmxWriter.Build("Panel X", "i1 Display Plus", "i1 Pro 2", solved);
            Assert.True(CgatsValidator.Validate(content, "ccmx").IsValid);
            Assert.True(CcmxWriter.TryParseMatrix(content, out var reparsed));
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(KnownMatrix[r, c], reparsed[r, c], 5);
        }
    }
}
