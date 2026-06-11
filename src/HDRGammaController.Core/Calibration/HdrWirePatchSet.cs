using System;
using System.Collections.Generic;

namespace HDRGammaController.Core.Calibration
{
    /// <summary>
    /// Generates the HDR wire-ladder patch list appended to an HDR calibration run.
    ///
    /// Each patch carries an absolute luminance (ColorPatch.Nits) rendered through the FP16
    /// scRGB path, so its PQ wire position is EXACT - PQ⁻¹(nits) - independent of the OS's
    /// SDR-white mapping (measured ~7% off on the reference system) and reaching far above
    /// SDR white. HdrMhc2LutBuilder prefers these points over the SDR-mapped grayscale when
    /// building the PQ tone LUTs.
    /// </summary>
    public static class HdrWirePatchSet
    {
        /// <summary>
        /// Log-spaced rungs covering shadows through highlights. 0 anchors the black floor.
        /// Capped at 1000 nits: desktop/HDR content above that rides the panel's own
        /// rolloff/ABL (window-size dependent on OLED), which the LUT must not fight.
        /// </summary>
        private static readonly double[] LadderNits =
            { 0, 2, 4, 8, 16, 32, 64, 100, 150, 220, 320, 450, 650, 1000 };

        public static IReadOnlyList<ColorPatch> Build(double panelPeakNits)
        {
            // 0.9x head-room below the DXGI-declared peak; distrust tiny/absent values.
            double cap = panelPeakNits > 50 ? Math.Min(panelPeakNits * 0.9, 1000) : 1000;

            var patches = new List<ColorPatch>();
            foreach (double nits in LadderNits)
            {
                if (nits > cap) break;
                patches.Add(MakePatch(nits, patches.Count));
            }
            // Make sure the top of the usable range is actually measured when the cap fell
            // between rungs (e.g. cap 540 -> ladder tops out at 450 without this).
            if (cap - patches[^1].Nits!.Value > cap * 0.05)
                patches.Add(MakePatch(Math.Round(cap), patches.Count));

            return patches;
        }

        private static ColorPatch MakePatch(double nits, int index) => new()
        {
            Name = $"HDR wire {nits:F0} nits",
            // Midpoint placeholder - rendering uses Nits; 0.5 keeps these out of the
            // white(>=0.99)/black(<=0.01) classification heuristics.
            DisplayRgb = new LinearRgb(0.5, 0.5, 0.5),
            Nits = nits,
            Category = PatchCategory.General,
            Index = index,
        };
    }
}
