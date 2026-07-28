# Contributing a golden fixture

Gloam's calibration model is pinned by *golden fixtures*: real measurement sets, recorded
from real panels, replayed through the live pipeline on every CI run and compared against
committed numbers with explicit tolerances. If a change to the color math would alter what
Gloam computes from real panel data, the build fails and the diff is visible in review.

Two panels are pinned today, both mid-range LCDs. Everything the codebase does that is
specific to OLED, QD-OLED, wide-gamut LCD, or mini-LED is currently unpinned. The logic
is there, but nothing stops a refactor from quietly changing its behavior. Closing that
gap needs measurement data from those panels, which needs people who own them.

That is what this document is for. If you have a supported colorimeter and a display we
have not pinned, you can produce a fixture in about an hour, most of which is the
calibration run itself.

## What you need

- **A display we do not already cover.** See the Hardware validation section in the README
  for the current list. An OLED, QD-OLED, wide-gamut LCD, or mini-LED panel is the most
  valuable; a second sample of an already-covered class is still useful but less urgent.
- **A colorimeter Gloam supports.** Anything ArgyllCMS `spotread` can drive. Note the exact
  model, because it goes in the manifest and determines the uncertainty budget's instrument class.
- **A meter correction (`.ccss`/`.ccmx`) if your panel needs one.** Wide-gamut and OLED
  panels generally do. Gloam can download one from the DisplayCAL database during setup.
  Whether you used one also goes in the manifest.
- **A build of the repo**, to run the ingest CLI. `dotnet run --project src/Gloam.Cli`.

You do **not** need to write any code.

## Step 1: run a real calibration

Nothing about this step is special. Run the calibration you would run anyway, following
the flow in the README:

1. Warm the display up for at least 30 minutes. This matters more than usual here: the
   fixture becomes a permanent reference, and warm-up drift baked into it is drift every
   future contributor's changes get compared against.
2. Choose the monitor, display type, target, and preset. Use a **Thorough** or **Full**
   preset, because the adaptive presets measure fewer patches and a sparse recording pins less.
3. Attach your meter correction if you have one.
4. Run it. Let the report's automatic apply-and-re-measure finish, so both a native
   characterization pass and a verification pass exist.

The target you pick must be one of Gloam's standard targets, because the fixture stores it
by name and CI resolves it back. `HDR Desktop PQ (sRGB gamut)` is the most useful for HDR
panels; `sRGB (Gamma 2.2)` for SDR. Custom or native targets cannot be used.

## Step 2: find the two CSV recordings

Gloam writes them next to the report automatically. Look in:

```
%LOCALAPPDATA%\Gloam\reports\measurements\
```

for the pair belonging to your run:

```
<MonitorName>_<yyyyMMdd_HHmmss>_native-measurements.csv
<MonitorName>_<yyyyMMdd_HHmmss>_verification-measurements.csv
```

Both are needed. The native pass is what the characterization is fitted from; the
verification pass is what the accuracy metrics and the uncertainty budget are computed
from. A fixture with only one of them cannot be built.

These are plain CSV, one row per patch, with the requested display values, the measured
XYZ, and per-read metadata. Open them and check they are not truncated and that
`is_valid` is `True` on essentially every row before going further.

## Step 3: build the fixture

Pick a directory name: lowercase, hyphenated, manufacturer and model, matching the
existing ones (`gigabyte-m27q-p`, `msi-mag271qpx28`). Then:

```powershell
dotnet run --project src/Gloam.Cli -- golden-ingest tests/Gloam.Tests/Fixtures/Golden/<your-panel> `
  --native      "<path>\<...>_native-measurements.csv" `
  --verification "<path>\<...>_verification-measurements.csv" `
  --target      "HDR Desktop PQ (sRGB gamut)" `
  --panel       "Acme XZ27 (QD-OLED)" `
  --instrument  "i1 DisplayPro" `
  --hdr `
  --sdr-white   200
```

Flags:

| Flag | Meaning |
| --- | --- |
| `--target` | Must match one of Gloam's standard targets. Matching is case-insensitive and by substring, so the full name always works. |
| `--panel` | Human-readable label shown in test output. Include the panel technology in parentheses, since that is the whole point of your contribution. |
| `--instrument` | Exact meter model. Drives the uncertainty budget's instrument class; names containing `i1 Pro`, `munki`, or `spectro` are classified as spectrometers. |
| `--hdr` | Pass it if the calibration was run with Windows HDR on. Omit for SDR. HDR fixtures additionally drive the closed-loop model tests. |
| `--sdr-white` | Windows SDR content brightness in nits during the run. Default 200. |
| `--meter-correction` | Pass it if you used a `.ccss`/`.ccmx`. Affects the uncertainty budget. |

The command copies the two CSVs into the fixture directory under their canonical names,
writes `manifest.json` from your flags, and computes `baseline.json`, the recorded
expectations. It prints what it computed. **Read that output.** If the peak luminance,
black level, gamma, or average ΔE do not look like your display, something is wrong with
the recording, and committing it would pin a bad number forever.

The result is exactly four files:

```
tests/Gloam.Tests/Fixtures/Golden/<your-panel>/
  manifest.json                    what was measured, and with what
  baseline.json                    the numbers CI will hold you to
  native-measurements.csv          characterization pass recording
  verification-measurements.csv    verification pass recording
```

CI discovers any directory under `Fixtures/Golden/` that contains both `manifest.json` and
`baseline.json`. There is no registry to add yourself to.

## Step 4: check it passes

```powershell
dotnet test src/Gloam.sln -c Release --filter "FullyQualifiedName~Golden"
```

Your fixture is now a test case. It should pass immediately, since you just generated the
baseline from the same data the test replays. If it does not, that is a genuine finding
worth reporting even if you stop there: it means the ingest path and the replay path
disagree about your panel.

If you passed `--hdr`, your fixture also feeds the closed-loop model tests, which fit a
panel model from your recording and assert refinement invariants on it. Those can surface
real issues on panel types we have never seen. A failure there is a bug report, not a
mistake on your part. Please send it.

## Step 5: send it

Either way works:

- **Pull request** adding the fixture directory. Preferred, because it runs through CI on the way
  in, so we both find out immediately whether it holds.
- **Issue** with the four files attached, if you would rather not open a PR. Say what the
  display is, what meter you used, and whether the run was HDR or SDR.

Please include, in the PR or issue body:

- Display make, model, and panel technology.
- Meter model, and the meter correction used, if any.
- Windows version, GPU, and driver version.
- Anything unusual about the run: a panel setting that mattered, ABL behavior you
  noticed, a mode you had to disable.

That last one is often the most valuable part. The numbers pin the model; the notes are
what tell the next person why the panel behaves the way it does.

## Notes

**Fixture recordings are real data and stay in the repo.** The blanket `*.csv` ignore in
`.gitignore` has an explicit exception for `tests/Gloam.Tests/Fixtures/**/*.csv` for this
reason. If `git status` does not show your CSVs, check that exception first.

**Regenerating a baseline.** When an intended modeling change moves the numbers,
re-running `golden-ingest` against an existing fixture directory with no flags recomputes
`baseline.json` in place from the committed recordings:

```powershell
dotnet run --project src/Gloam.Cli -- golden-ingest tests/Gloam.Tests/Fixtures/Golden/<panel>
```

That is a deliberate act, and the resulting diff is meant to be read in review. Do not do
it to make a red build green.

**What a fixture does not prove.** The recordings are open-loop: they capture what the
panel did under the corrections that were actually applied, and cannot say what it would
have done under a different one. Tier A replays them through the pipeline and checks the
computed numbers; tier B fits a model and checks loop invariants on the model, and labels
those results as model-based inferences rather than measurements. Neither is a claim that
Gloam is correct on your panel, only that its behavior on your panel does not change
without someone noticing.
