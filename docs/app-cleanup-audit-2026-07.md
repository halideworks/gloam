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

## Recommended next extractions

### 1. Probe operation/session scope — high value

`CalibrationReportWindow` still repeats the lifecycle around most probe operations: create a cancellation source, enter the busy gate, optionally bypass ramps, open a patch window, begin/end the meter session, restore state, close the window, and update status. The placement surface is shared now, but the surrounding resource ownership is not. A small `ProbeOperationScope` (or coordinator) should own that lifecycle and make cleanup-on-cancel structurally unavoidable.

Likely consumers: verification, joint HDR refinement, white re-anchor, HDR characterization, renderer validation, CCSS capture, and trust checks.

### 2. Split `CalibrationReportWindow` by capability — high value

The code-behind remains roughly 3,100 lines after deleting about 500 lines of dead refinement code. It currently owns presentation, profile installation, verification, refinement, tone mapping, white tools, chart refresh, CSV persistence, and report export coordination. Extracting `VerificationCoordinator`, `HdrRefinementCoordinator`, and `ReportSnapshotBuilder` would give each workflow an independently testable boundary and prevent future report-refresh omissions.

### 3. Split initial calibration orchestration from its window — high value

`CalibrationWindow.xaml.cs` remains roughly 2,100 lines and mixes navigation, patch presentation, device/session management, profile construction, installation handoff, and measurement progress. The new placement control is a first seam; the next useful seam is a calibration-session coordinator whose state the window only presents.

### 4. Separate settings storage, migration, and normalization — medium value

`SettingsManager` is roughly 1,500 lines and currently combines schema models, disk I/O, legacy migration, validation, path safety, monitor-profile updates, and gamer-profile normalization. Keep one public settings facade, but move serialization/migration and domain normalizers into dedicated collaborators. This reduces the chance that a new setting changes an unrelated migration path.

### 5. Decompose dashboard and tray view models — medium value

`DashboardViewModel` and `TrayViewModel` are each over 1,000 lines and coordinate monitor state, night mode, app discovery, game profiles, calibration entry points, and update state. Extracting app discovery/game-library state and calibration launch state would reduce redraw/persistence coupling and make their refresh behavior easier to reason about.

### 6. Continue design-system adoption — medium value

Several older utility windows still build substantial UI in C# while the main surfaces use shared XAML tokens and component styles. Convert them when they next receive product work, starting with the CCSS browser and trust-check result presentation. The goal is shared behavior and accessibility, not a cosmetic rewrite.

## Items reviewed and intentionally retained

- `MainWindow` is a hidden tray/message host and is still part of the application lifecycle; it is not a dead empty window.
- Legacy install/profile/settings checks are active safety and migration code. Their comments mention old versions, but they are not user-facing “this moved” declarations and should remain until support for those installs is deliberately retired.
- The core 1D and color refinement algorithms remain in use by tests and the joint HDR solver. Only their obsolete duplicate report-window entry points were removed.

## Lower-risk hygiene backlog

- Add a reusable UI-test harness for small WPF components. Current integration tests contain their own STA thread, dispatcher pumping, resource setup, and cleanup.
- Replace broad empty `catch` blocks in non-critical UI drawing paths with rate-limited debug logging so failures remain diagnosable without interrupting users.
