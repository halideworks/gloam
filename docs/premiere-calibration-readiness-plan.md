# Premiere Calibration Readiness Plan

This is the working plan for making Gloam a top-tier Windows HDR calibration and HDR-fix tool.
Hardware compatibility matrix work is intentionally excluded from the implementation scope here.

## 1. Release And Test Gates

- Require release CI to restore, build, run the full test suite, and check transitive package vulnerabilities before packaging.
- Keep signing/package jobs dependent on the validation job.
- Add package smoke checks: expected files present, Argyll tools present, third-party notices present, app launches far enough to initialize logging.
- Add regression tests around every color-math bug fixed in production.

## 2. Display Identity And Topology

- Persist by stable monitor instance path, not volatile display index or HMONITOR.
- Resolve EDID by exact monitor instance path before model-name fallback.
- Include raw DXGI color-space, bits-per-color, HDR metadata, DisplayConfig adapter/source identity, and bounds in diagnostics.
- Detect topology changes with debounce and invalidate previously-applied ramp state before reapply.

## 3. HDR State Modeling

- Separate "HDR currently active" from "display has HDR metadata/capability."
- Gate HDR calibration install on the same HDR/SDR mode used during measurement.
- Record SDR white, HDR min/peak/full-frame metadata, and color-space enum in diagnostics.
- Future work: add an in-app preflight row that warns when Windows HDR, SDR white, or Advanced Color profile state changes between measure and install.

## 4. Apply Pipeline Stability

- Coalesce rapid apply requests per monitor with "latest value wins."
- Rate-limit hardware gamma writes so slider drags, night-mode fades, and display-change storms cannot hammer the compositor.
- Dispose the apply coalescer on shutdown so queued writes cannot run after services are torn down.
- Re-check calibration bypass immediately before hardware writes.
- Future work: add cancellation-aware apply work for long external fallback calls.

## 5. Measurement Validity Gates

- Reject measurement sets with too few valid patches, near-black peak, flat/stale luminance, missing white/black anchors, non-monotonic grayscale, impossible primaries, or repeated-white drift.
- Run the same validator before LUT generation and profile install.
- Prefer explicit measured white over brightest saturated primary when normalizing verification metrics.
- Future work: expose the validator result in the calibration UI before install with targeted recovery text.

## 6. Profile Lifecycle

- Disable existing app profiles before native measurement without deleting them.
- Remove both SDR and Advanced Color profile associations on uninstall.
- Retire stale app-created associations for a monitor before measuring native.
- Store the previous Windows default profile per monitor and restore it after explicit deactivation/deletion of the active Gloam profile.

## 7. HDR Verification

- Verify HDR profiles with FP16/scRGB PQ wire patches, absolute-nit targets, and dE ITP.
- Keep SDR and HDR verification metrics separate in reports.
- Future work: add an automated post-install "profile visibly active" check by measuring a small sentinel patch through the applied profile.

## 8. Color Engine

- Keep tone curves neutral unless per-channel grayscale tracking has enough measured data to support chromatic grayscale correction safely.
- Guard unreachable target gamuts before building an MHC2 matrix.
- Preserve HDR highlight handling by building HDR tone LUTs in PQ wire domain.
- Future work: replace simple clipping with a perceptual gamut-compression mode for wide-gamut-to-narrow-gamut targets.

## 9. Hardware Compatibility Matrix

- Excluded from this implementation pass.
- Eventually track GPU vendor, driver branch, Windows build, display transport, panel type, HDR mode behavior, meter model, and known caveats.

## 10. UX Completeness

- Keep one calibration flow active at a time because only one probe can own the instrument.
- Surface calibration/measurement failures with actionable probe, driver, HDR-mode, and profile-association messages.
- Add tray diagnostics export for support.
- Future work: add an in-flow preflight checklist for HDR mode, SDR white, meter correction, warm-up time, and panel target reachability.

## 11. Diagnostics And Supportability

- Rotate app and colorimeter logs instead of deleting them at size limit.
- Export a sanitized diagnostics zip with manifest, monitor/HDR topology, logs, settings, calibration profile summary, and third-party notices.
- Redact user-scoped paths and usernames from bundle text.
- Future work: add optional user-controlled inclusion of full calibration reports and raw measurement CSVs.

## 12. Distribution, Legal, And Security

- Bundle `THIRD_PARTY_NOTICES.txt` with releases.
- Do not execute `dispwin` or `spotread` from arbitrary current-directory or PATH locations.
- Use structured `ProcessStartInfo.ArgumentList` for external tools.
- Verify Argyll archive integrity before extraction.
- Future work: add package-level notice validation and signed-file checks in CI.
