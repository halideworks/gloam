# Codebase hardening audit — July 2026

## Verdict

Gloam is in strong shape for a Windows desktop application that crosses managed code,
Win32, DXGI, ICC/MHC2 profiles, external measurement tools, and physical instruments. The
current tree has no known vulnerable NuGet packages, no security-category .NET analyzer
findings, a warning-free strict Release build, and 1,252 passing tests.

That is not the same as “proven bug-free.” No honest audit can make that claim, and this
environment cannot reproduce every Windows HDR compositor, GPU driver, display firmware,
USB instrument, or ICC-registration failure mode. The remaining risks are stated below.

| Area | Current assessment | Evidence |
| --- | --- | --- |
| Security | No known high-severity issue remains | Current NuGet advisory scan; process-launch, download, archive, path, persistence, and untrusted-input review |
| Correctness | Strong automated coverage; hardware boundary still matters | 1,252 Debug and Release tests; calibration math and artifact regression suites |
| Performance | Appropriate for the workload, with hot-path allocation waste removed | Allocation-free 3×3 color transforms; coalesced/rate-limited hardware writes; bounded network and file inputs |
| Maintainability | Good and materially improved, but not immaculate | Shared probe-placement flow, specific analyzer baseline, documented lifecycle ownership, remaining large WPF controllers |
| Dead code and cruft | No obvious unfinished or abandoned production path found | Source scan found no TODO/FIXME/HACK markers or `NotImplementedException`; compiler/analyzer pass clean under the project policy |

## Confirmed findings fixed

### Calibration-profile path traversal

Calibration profile IDs were interpolated into filenames without first constraining the ID.
A crafted value such as `..\outside` could make load, save, or delete escape the calibration
profile directory. Profile IDs must now be GUIDs in one of the two formats Gloam has emitted.
Invalid persisted IDs are cleared during settings validation, and regression coverage proves
that a file outside the profile directory cannot be read, overwritten, or deleted.

### Unbounded input and response handling

Several trusted-on-paper inputs were read in full before their plausibility was established.
The following boundaries are now explicit:

- settings and calibration-profile JSON have byte and character limits;
- measurement CSV has file, row, column, and field limits, rejects unterminated quotes, and
  rejects explicit non-finite numeric values;
- CCSS/CCMX files have byte and character limits plus structural CGATS validation;
- the community correction-database response is streamed with a hard maximum instead of
  being buffered without a ceiling;
- local correction enumeration and duplicate searches are capped;
- correction types are constrained to `ccss` or `ccmx` before parsing or saving.

Malformed or oversized settings remain on disk rather than being replaced with defaults.
That preserves the user's only copy and prevents a bad launch from becoming data loss.

### Resource ownership and shutdown

The latest-value coalescer now releases per-key cancellation sources and semaphores, cancels
active work on disposal, and records unexpected callback failures. Expected replacement
cancellation stays quiet. Its submit/dispose race is covered by a regression test.

Calibration-run cancellation sources are disposed after each run. Closing or canceling the
setup/calibration flow releases its colorimeter service; choosing Back transfers that ownership
to the reopened setup flow instead. WPF windows that own disposable resources now document
their close-path ownership explicitly, while conventional `IDisposable` implementations call
`GC.SuppressFinalize`.

### Native and parser error handling

Foreground-process detection now checks the `GetWindowThreadProcessId` return value before
using its process ID. Best-effort DWM calls explicitly discard unsupported-attribute results.
The CCSS wavelength parser carries its successfully parsed wavelength forward rather than
silently assuming a repeated parse succeeded.

### Performance and allocation pressure

The color-conversion core previously allocated input and output arrays for every 3×3 matrix
multiply, and Bradford matrices were rebuilt on each adaptation. Those transforms now use
scalar inputs and a value-type result, while the constant Bradford matrices are cached. This
removes repeated heap traffic from 33³ LUT generation and other color sweeps without changing
the arithmetic order or results.

CSV escaping caches `SearchValues`, spectral parsing reuses its separator array, serializer
options are cached, and hashing uses the static SHA-256 APIs. These are small individually but
sit in repeated export, diagnostics, parsing, or persistence paths.

### Analyzer policy

The repository no longer disables entire analyzer categories such as Security, Reliability,
Performance, or Design. The normal strict build uses a specific, reviewed rule-ID baseline, so
turning on a new category cannot silently hide unrelated findings. A full `AnalysisMode=All`
audit was also reviewed. Its remaining output is dominated by test naming, P/Invoke surface
naming, explicit abstraction choices, and low-value modernization suggestions; it contains no
security-category diagnostic. Confirmed lifecycle, ignored-result, API-ordering, serializer,
hashing, and search-allocation findings were fixed rather than added to the baseline.

## Security controls already present and rechecked

- ArgyllCMS is downloaded over HTTPS, constrained by compressed/extracted size and entry
  count, checked against a pinned SHA-256 digest, protected against ZIP traversal, staged, and
  atomically swapped into place.
- Application updates validate package identity, version, size, and digest metadata. Release
  signing uses the repository's trusted-signing workflow rather than a committed credential.
- External tools are launched with `ProcessStartInfo.ArgumentList` and shell execution disabled.
  The only shell/elevation path is the intentional Argyll USB-driver installer.
- Settings and profile writes use write-then-move replacement. Newer settings schemas are not
  overwritten by older binaries, and unreadable settings are preserved.
- No embedded credential or private key was found in the tracked source review.

## Verification performed

- `dotnet build src/HDRGammaController.sln /p:GloamStrictBuild=true`: zero warnings and errors.
- strict Release build: zero warnings and errors.
- focused security, persistence, parser, lifecycle, and color-math suites: all passing.
- complete Debug suite: 1,252 passed, zero failed or skipped.
- complete Release suite: 1,252 passed, zero failed or skipped.
- `dotnet list ... package --vulnerable --include-transitive` against NuGet.org: no vulnerable
  package in any production, CLI, interop, debug, or test project.
- source scans for unfinished implementations, unsafe process argument construction, embedded
  secrets, destructive file paths, and broad analyzer suppression.

## Residual risks and deliberate non-changes

### Hardware and operating-system integration

Automated tests cannot certify real MHC2 installation, Windows Advanced Color profile-list
selection, GPU-ramp behavior across driver resets, FP16 scRGB patch presentation, colorimeter
USB recovery, or actual Argyll prompts across every supported instrument. Before a release,
the highest-value manual gate remains one SDR and one HDR calibration on supported hardware,
including cancel/retry, refinement, report regeneration, reboot persistence, and update install.

### Large UI controllers

`CalibrationReportWindow`, `CalibrationWindow`, and `SettingsManager` remain large. Their most
error-prone shared behavior has been consolidated, and the cleanup audit identifies sensible
extraction boundaries, but splitting thousands of lines mechanically would increase regression
risk without improving the shipped behavior. Further decomposition should follow real feature
work, one testable responsibility at a time.

### Dependency upgrades

The installed packages have no known advisory. A few newer major versions exist, including the
tray-icon library and the xUnit v3 line. Those are migrations, not security fixes, and should be
handled separately with UI/installer or test-runner validation rather than mixed into this
hardening change.

### Scope of “no dead code”

The compiler, analyzer, test, and source scans found no obvious abandoned implementation. They
cannot prove that every public method is reached in every product configuration. Runtime
telemetry or coverage gathered from real calibration, gamer-mode, update, and trust-check
sessions would be needed before deleting apparently cold platform code.

## Bottom line

The codebase is secure and maintainable enough to ship with the normal hardware smoke gate. It
is materially safer, more bounded, and less allocation-heavy than it was at the start of this
pass. There is no known correctness or security defect left from the audit, but “bug-free” should
remain an evidence target, not a label.
