## What this changes

<!-- What it does and why. If it fixes an issue, link it. -->

## Checklist

- [ ] `dotnet test src/Gloam.sln -c Release` passes.
- [ ] `dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true` is warning-free.
- [ ] No existing test was weakened, skipped, or deleted to make this pass.
- [ ] Changes under `src/Gloam.Core` have tests. (Presentation-only changes are exempt.)

## Calibration math

Only if this touches `LutGenerator`, `ColorMath`, `HdrMhc2LutBuilder`, `DriftCompensator`,
or the refinement/planner classes — delete this section otherwise.

- [ ] Pinning tests were added **before** the behavioral change, so the diff shows what moved.
- [ ] The whitepaper is updated if documented behavior changed (`site/whitepaper.html` is the
      source; regenerate `site/whitepaper.md` with `python scripts/build-whitepaper-md.py`).
- [ ] The golden fixtures still pass, or the baseline was regenerated deliberately and the
      diff is explained below.

**What was measured?** Before/after numbers if you have hardware and a meter. If you do not,
say so — it will be reviewed as a proposal rather than a result, which is fine.

<!-- measurements or "not measured; no hardware" -->
