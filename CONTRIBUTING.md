# Contributing to Gloam

Thanks for looking. Bug reports with a diagnostics bundle, golden fixtures from panels we
have not measured, and small focused pull requests are all genuinely useful.

## Build

```powershell
git clone https://github.com/halideworks/gloam.git
cd gloam
dotnet run --project src/Gloam
```

You need the .NET 8 SDK. `global.json` pins it, so a newer major SDK will refuse rather
than silently building against something else.

Package a local installer:

```powershell
.\package.ps1 -Version X.Y.Z
```

## Run the tests

```powershell
dotnet test src/Gloam.sln -c Release
```

This must pass before you open a pull request. Some tests drive real WPF on an STA thread,
so they need a desktop session. They will not run headless.

Before submitting, also check the build is warning-free under the strict configuration,
which is what CI enforces:

```powershell
dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true
```

## Golden fixtures

Gloam's color math is pinned by *golden fixtures*: real measurement recordings from real
panels, replayed through the live pipeline on every CI run and compared against committed
numbers with explicit tolerances. They live in
[`tests/Gloam.Tests/Fixtures/Golden`](tests/Gloam.Tests/Fixtures/Golden), and the
computation they pin is in `GoldenSampleBaseline`.

A failure there means a change altered what Gloam computes from real data. That is
sometimes correct and sometimes a regression, and the point of the rig is that you have to
decide which on purpose, in a diff a reviewer can see.

**If you own a display we have not pinned (OLED, QD-OLED, wide-gamut LCD, mini-LED) and a
colorimeter, contributing a fixture is the most valuable thing you can send.** It needs no
code. See [docs/golden-fixture-contribution.md](docs/golden-fixture-contribution.md).

## Pull requests

- **Changes under `src/Gloam.Core` need tests.** That is where the color math, measurement
  handling, settings persistence, and scheduling live, and it is all testable without a
  display.
- **Presentation-layer changes are exempt.** Window layout, styling, and view wiring under
  `src/Gloam` do not need tests unless they carry real logic. If you find yourself writing
  a loop or a conditional in a code-behind, that is logic, and it belongs somewhere
  testable.
- **Keep it focused.** One change per pull request. Drive-by refactors in a bug-fix PR make
  the fix harder to review and harder to revert.
- **Match the surrounding comment voice.** Comments in this codebase explain *why* and
  record invariants, not what the next line does. Please do not strip existing ones.

### Calibration math

Changes to `LutGenerator`, `ColorMath`, `HdrMhc2LutBuilder`, `DriftCompensator`, and the
refinement and planner classes are held to a higher bar, because a subtle error there
produces plausible numbers that are wrong, the worst possible failure for a tool whose
entire claim is that its output is measured rather than asserted.

Such a pull request must:

1. **Add pinning tests first.** Tests that capture the current behavior, committed before
   the behavioral change, so the diff shows exactly what moved.
2. **Update the whitepaper** if it changes documented behavior. The source is
   `site/whitepaper.html`; `site/whitepaper.md` is generated from it by
   `python scripts/build-whitepaper-md.py`. Regenerate it rather than editing it by hand.
3. **Say what was measured.** If you have hardware and a meter, include the before and
   after. If you do not, say that too. An unverified math change is still worth
   discussing, but it will be reviewed as a proposal rather than a result.

Do not weaken, skip, or delete an existing test to make a change pass. If a test is wrong,
that is its own pull request with its own argument.

## Reporting bugs

Use the [bug report template](.github/ISSUE_TEMPLATE/bug_report.yml). It asks for a
diagnostics bundle (tray → **Export Diagnostics**), which is not bureaucracy. Display
problems depend on your Windows version, GPU driver, panel, and HDR state, and the bundle
carries all four. A report without one usually turns into several rounds of questions.

Check [docs/troubleshooting.md](docs/troubleshooting.md) first; several common reports have
a known answer there.

## Security

Do not open a public issue for a security problem. See [SECURITY.md](SECURITY.md).
