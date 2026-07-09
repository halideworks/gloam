using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HDRGammaController.Core;
using HDRGammaController.Core.Calibration;
using Xunit;

namespace HDRGammaController.Tests
{
    /// <summary>
    /// Tier B of the golden-sample rig: MODEL-BASED closed-loop replay. Recorded data is
    /// open-loop — it cannot answer what the panel would emit under a counterfactual
    /// correction — so these tests fit an interpolating panel model from the recording,
    /// prove the model reproduces the recording (SelfCheck), and then assert loop
    /// INVARIANTS (convergence, keep-best) on it. Results here are model-based inferences
    /// about real panels, never presented as measured reality.
    /// </summary>
    public class GoldenClosedLoopModelTests
    {
        private static string FixturesRoot =>
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Golden");

        public static TheoryData<string> HdrFixtures()
        {
            var data = new TheoryData<string>();
            if (!Directory.Exists(FixturesRoot)) return data;
            foreach (string dir in Directory.EnumerateDirectories(FixturesRoot))
            {
                string manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                if (GoldenSampleManifest.Load(manifestPath).HdrMode)
                    data.Add(Path.GetFileName(dir));
            }
            return data;
        }

        /// <summary>
        /// The panel model: measured wire-ladder response (PQ position → nits), monotone
        /// piecewise-linear between recorded rungs, clamped at the ends.
        /// </summary>
        private sealed class FittedHdrPanel
        {
            private readonly List<(double P, double Nits)> _points;

            public FittedHdrPanel(IEnumerable<MeasurementResult> wireLadder)
            {
                _points = wireLadder
                    .Where(m => m.IsValid && m.Patch.Nits is double n && double.IsFinite(n) && n >= 0)
                    .Where(m => double.IsFinite(m.Xyz.Y) && m.Xyz.Y >= 0)
                    .GroupBy(m => m.Patch.Nits!.Value)
                    .Select(g => (P: TransferFunctions.PqInverseEotf(g.Key), Nits: g.Average(m => m.Xyz.Y)))
                    .OrderBy(t => t.P)
                    .ToList();

                // Monotone cleanup, same as the LUT builder: measurement noise can invert.
                for (int i = 1; i < _points.Count; i++)
                    if (_points[i].Nits < _points[i - 1].Nits)
                        _points[i] = (_points[i].P, _points[i - 1].Nits);
            }

            public int PointCount => _points.Count;
            public IReadOnlyList<(double P, double Nits)> Points => _points;

            public double NitsOfPq(double p)
            {
                if (_points.Count == 0) return 0;
                if (p <= _points[0].P) return _points[0].Nits;
                if (p >= _points[^1].P) return _points[^1].Nits;
                for (int i = 1; i < _points.Count; i++)
                {
                    if (p <= _points[i].P)
                    {
                        double span = _points[i].P - _points[i - 1].P;
                        double t = span <= 1e-12 ? 0 : (p - _points[i - 1].P) / span;
                        return _points[i - 1].Nits + (_points[i].Nits - _points[i - 1].Nits) * t;
                    }
                }
                return _points[^1].Nits;
            }
        }

        private static (FittedHdrPanel Panel, GoldenSampleManifest Manifest) LoadFittedPanel(string fixtureName)
        {
            string dir = Path.Combine(FixturesRoot, fixtureName);
            var manifest = GoldenSampleManifest.Load(Path.Combine(dir, "manifest.json"));
            var native = MeasurementCsvImporter.Load(Path.Combine(dir, manifest.NativeCsv)).Measurements;
            return (new FittedHdrPanel(native), manifest);
        }

        private static double SampleLut(double[] lut, double p)
        {
            double x = Math.Clamp(p, 0.0, 1.0) * (lut.Length - 1);
            int i = (int)Math.Floor(x);
            if (i >= lut.Length - 1) return lut[^1];
            double t = x - i;
            return lut[i] + (lut[i + 1] - lut[i]) * t;
        }

        private static double Chain(double[] lut, FittedHdrPanel panel, double contentNits, double driftGain = 1.0)
            => panel.NitsOfPq(SampleLut(lut, TransferFunctions.PqInverseEotf(contentNits))) * driftGain;

        [Theory]
        [MemberData(nameof(HdrFixtures))]
        public void FittedModel_ReproducesTheRecording_SelfCheck(string fixtureName)
        {
            // A model that cannot reproduce its own training data disqualifies every
            // conclusion drawn from it — this gate runs before any loop test means anything.
            var (panel, _) = LoadFittedPanel(fixtureName);
            Assert.True(panel.PointCount >= 5, "fixture has too few wire rungs to fit a panel model");

            foreach (var (p, nits) in panel.Points)
                Assert.Equal(nits, panel.NitsOfPq(p), 6);
        }

        [Theory]
        [MemberData(nameof(HdrFixtures))]
        public void GoldenLutBuild_DrivesFittedPanel_OntoAbsolutePq(string fixtureName)
        {
            // The open-loop LUT built from the REAL recording must drive the fitted panel
            // onto absolute PQ within a few percent across the corrected range — on real
            // measured data, not a synthetic panel.
            var (panel, manifest) = LoadFittedPanel(fixtureName);
            string dir = Path.Combine(FixturesRoot, fixtureName);
            var native = MeasurementCsvImporter.Load(Path.Combine(dir, manifest.NativeCsv)).Measurements;
            var luts = HdrMhc2LutBuilder.Build(native, manifest.SdrWhiteNits);
            Assert.True(luts.WireExact, "golden recordings should carry a usable wire ladder");

            foreach (double nits in new[] { 4.0, 16, 64, 100, 150 })
            {
                if (nits > luts.MeasuredPeakNits * 0.5) continue; // stay inside the fully-corrected range
                double y = Chain(luts.LutR, panel, nits);
                Assert.True(Math.Abs(y / nits - 1.0) < 0.05,
                    $"{fixtureName}: at {nits:F0} nits the corrected chain emits {y:F2} " +
                    $"({y / nits - 1.0:+0.0%;-0.0%}); expected <5% on the fitted golden panel");
            }
        }

        [Theory]
        [MemberData(nameof(HdrFixtures))]
        public async Task HdrRefinementLoop_OnDriftedGoldenPanel_ConvergesKeepingInvariants(string fixtureName)
        {
            // Ties feature 2.1 to real panel shapes: synthetic drift (model-based, labeled)
            // on the fitted golden panel; the loop must converge within 3 passes and end
            // with its best pass installed.
            var (panel, manifest) = LoadFittedPanel(fixtureName);
            string dir = Path.Combine(FixturesRoot, fixtureName);
            var native = MeasurementCsvImporter.Load(Path.Combine(dir, manifest.NativeCsv)).Measurements;
            var initial = HdrMhc2LutBuilder.Build(native, manifest.SdrWhiteNits);

            // Post-calibration drift: +6% shadows sliding to −3% highlights (log-nit smooth).
            double Drift(double nits)
            {
                double t = Math.Clamp((Math.Log10(Math.Max(nits, 0.01)) - 1.0) / (Math.Log10(300) - 1.0), 0, 1);
                double sm = t * t * (3 - 2 * t);
                return 1.06 + (0.97 - 1.06) * sm;
            }

            var rungs = new[] { 2.0, 4, 8, 16, 32, 64, 100, 150, 220 }
                .Where(n => n <= initial.MeasuredPeakNits * 0.85)
                .ToList();
            Assert.True(rungs.Count >= 4, "fitted golden panel leaves too few refinable rungs");

            double[] installedLut = initial.LutR;
            var installs = new List<HdrMhc2LutBuilder.Result>();

            Task<IReadOnlyList<MeasurementResult>> Measure(
                IReadOnlyList<double> ladder, int offset, CancellationToken ct)
            {
                IReadOnlyList<MeasurementResult> result = ladder.Select((n, i) =>
                {
                    double baseNits = Chain(installedLut, panel, n);
                    double y = baseNits * Drift(baseNits);
                    return new MeasurementResult
                    {
                        Patch = new ColorPatch
                        {
                            Name = $"PQ {n:F0} nits",
                            DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
                            Nits = n,
                            Index = offset + i,
                        },
                        Xyz = new CieXyz(y * 0.95, y, y * 1.08),
                        SequenceIndex = offset + i,
                    };
                }).ToList();
                return Task.FromResult(result);
            }

            Task<(HdrMhc2LutBuilder.Result, string)> Install(
                HdrMhc2LutBuilder.Result candidate, CancellationToken ct)
            {
                installedLut = candidate.LutR;
                installs.Add(candidate);
                return Task.FromResult((candidate, $"golden-refined-{installs.Count}"));
            }

            var outcome = await HdrRefinementLoop.RunAsync(new HdrRefinementLoop.Config
            {
                InitialLuts = initial,
                RungNits = rungs,
                MeasureLadderAsync = Measure,
                InstallAsync = Install,
            }, CancellationToken.None);

            Assert.True(outcome.AnyInstall, $"{fixtureName}: drift should trigger at least one pass ({outcome.StopReason})");
            Assert.True(outcome.FinalAvgAbsError < outcome.InitialAvgAbsError,
                $"{fixtureName}: loop must improve on the drifted state " +
                $"({outcome.InitialAvgAbsError:P2} → {outcome.FinalAvgAbsError:P2})");
            Assert.True(outcome.Converged && outcome.FinalAvgAbsError < 0.01,
                $"{fixtureName}: expected convergence under 1% on the fitted golden panel, " +
                $"got {outcome.FinalAvgAbsError:P2} ({outcome.StopReason})");
            // Keep-best invariant: the display ends on the LUTs the outcome reports.
            Assert.Same(outcome.FinalLuts.LutR, installedLut);
        }
    }
}
