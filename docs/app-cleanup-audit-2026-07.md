# Application cleanup audit — July 2026

This pass reviewed the desktop UI, calibration lifecycle, report persistence, shared styles, migration copy, build warnings, and the largest maintenance hotspots. The goal was to remove misleading residue and consolidate behavior where the same physical operation had drifted into multiple implementations.

## Completed in this pass

- Replaced the separate calibration and follow-up probe-placement interfaces with `ProbePlacementControl`. Initial calibration, HDR refinement, white re-anchoring, manual verification, trust checks, and CCSS/CCMX capture now use the same target, drag/nudge controls, instructions, and actions.
- Reused the shared brutalist button style in the code-built patch window instead of maintaining another local button template.
- Removed the monitor-settings declaration that global Night Mode settings had moved. The page now presents only the per-monitor override that is actually available there.
- Removed the two obsolete, private HDR refinement implementations from `CalibrationReportWindow`; the UI and code now have one joint tone-and-color refinement path.
- Made refinement automatically re-verify the installed result. The accuracy table, grade, recommendations, diagnostics, charts, raw verification data, saved JSON report, and subsequent print/PDF export all follow the final correction.
- Corrected fresh profile bookkeeping: native measurements are stored as `PreCalibrationDeltaE`, not `PostCalibrationDeltaE`.
- Preserved verified “after” metrics when later report tools, such as HDR characterization, update the same report.
- Persisted verification/PQ/colored-HDR diagnostics and tone-mapping characterization so historical reports contain the same evidence as the live report. Tone-mapping data now passes through the same non-finite-value sanitization as the rest of the profile.
- Cleared the application-project build warnings found by the audit: removed a no-op preference-loading flag and false `async`, declared Unicode Win32 marshaling, made theme persistence a property, documented borrowed window-source ownership, and eliminated new placement nullability warnings.

## Recommended extractions completed

All six follow-up boundaries from the original audit are now implemented:

1. `ProbeOperationScope` owns cancellation, busy-state entry, optional rendering bypass, patch-window lifetime, instrument-session lifetime, restoration, and cleanup. Verification, joint HDR refinement, white re-anchor, HDR characterization, renderer validation, trust checks, CCSS spectral capture, and two-meter CCMX capture use the same scope.
2. `CalibrationReportWindow` delegates standard verification and analysis to `VerificationCoordinator`, joint solve/install policy to `HdrRefinementCoordinator`, and report JSON plus raw CSV persistence to `ReportSnapshotBuilder`. Coordinator tests cover standard measurement analysis and report-refresh behavior independently of the window.
3. `CalibrationSessionCoordinator` now owns initial-run orchestration policy, progress/event wiring, SDR closed-loop refinement, HDR wire measurement, and calibration-artifact construction. The calibration window retains navigation and presentation responsibilities.
4. `SettingsPersistence`, `SettingsMigration`, and `SettingsNormalization` separate disk/schema handling from domain validation while `SettingsManager` remains the stable facade. The existing settings and Game Lab regression suites cover the split.
5. `GameLibraryCoordinator` owns process and launcher discovery, cancellation, stale-result suppression, and path filtering. `CalibrationLaunchCoordinator` owns the setup-to-calibration-to-back state loop and meter ownership. Dashboard and tray view models now present those workflows instead of implementing their resource state directly.
6. The CCSS browser and Trust Check now use shared XAML surfaces, theme resources, and common chrome. `BrutalistChrome` can safely host existing XAML content, so those windows no longer reconstruct their primary layout and theme wiring in code.

These are responsibility seams rather than mechanical file splitting. The WPF windows still contain substantial presentation code, but session ownership, persistence, discovery, installation, and report-refresh policy now live behind focused collaborators with tests.

## Items reviewed and intentionally retained

- `MainWindow` is a hidden tray/message host and is still part of the application lifecycle; it is not a dead empty window.
- Legacy install/profile/settings checks are active safety and migration code. Their comments mention old versions, but they are not user-facing “this moved” declarations and should remain until support for those installs is deliberately retired.
- The core 1D and color refinement algorithms remain in use by tests and the joint HDR solver. Only their obsolete duplicate report-window entry points were removed.

## Supporting hygiene completed

- Added `WpfTestHost` for reusable STA setup, dispatcher pumping, resources, and cleanup in small WPF tests. Structural tests now load the CCSS and Trust Check XAML surfaces through that host.
- Added rate-limited debug logging for protected-process races, launcher/config discovery, correction-folder setup, transient WPF drawing, instrument termination, renderer and COM cleanup, profile staging, and history pruning. Remaining silent fallback catches are deliberate recovery boundaries, such as the logger guarding itself during process bootstrap or conservative provenance fallback after an optional metadata read.
- Closed a shutdown race in the melanopic monitor worker: queue admission is synchronized with disposal, and a worker that exceeds the bounded join keeps its wait handle until process cleanup instead of observing a disposed handle.
