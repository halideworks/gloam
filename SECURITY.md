# Security policy

## Reporting a vulnerability

**Please do not open a public issue.**

Report privately through GitHub:
[Security → Report a vulnerability](https://github.com/halideworks/gloam/security/advisories/new).
That opens a private advisory visible only to the maintainers.

Include what you would want if you were fixing it: what you did, what happened, what you
expected, and — if you have one — a proof of concept. If the issue involves a file Gloam
parses, attach the file.

You will get an acknowledgement. If a report turns out not to be a security issue, we will
say so and why rather than leaving it open indefinitely.

## Supported versions

The latest release. Gloam auto-updates by default when installed with the setup package,
and fixes ship in a new release rather than as backports to older versions.

## In scope

These are the places Gloam handles input it did not create, and where a vulnerability would
be most consequential:

- **Profile and settings parsing.** `settings.json`, calibration profiles, saved report
  snapshots, and update state, all under `%LOCALAPPDATA%\Gloam`. Malformed or hostile input
  should fail closed, not execute or corrupt.
- **Downloaded CCSS/CCMX handling.** Meter corrections fetched from the DisplayCAL
  community database are third-party files from a remote source, parsed locally.
- **ArgyllCMS invocation.** Gloam launches `spotread`, `dispwin`, and related tools as
  child processes, and locates the binaries at runtime. Argument handling, path resolution,
  and anything that could result in executing an unexpected binary is in scope.
- **The updater.** Velopack feed handling, signature and version checks, and the install
  path under `%LOCALAPPDATA%\GloamApp`.
- **ICC/MHC2 profile generation and installation**, including what Gloam writes into the
  Windows color store.

Bundled ArgyllCMS is third-party. Report vulnerabilities in ArgyllCMS itself upstream — but
if Gloam's *use* of it is what makes something exploitable, that is ours, so tell us.

## Out of scope

- Findings that require an attacker who already has code execution as the user. Gloam is a
  per-user desktop application with no service and no elevated component; an attacker at
  that level does not need Gloam.
- Missing hardening flags with no demonstrated impact.
- SmartScreen reputation warnings on new releases. Expected, and covered in
  [docs/troubleshooting.md](docs/troubleshooting.md).

## Prior review

The codebase was audited in July 2026; the findings, the verification performed, and the
stated residual risks are in
[docs/codebase-hardening-audit-2026-07.md](docs/codebase-hardening-audit-2026-07.md). It is
worth reading before reporting — it is explicit about what that audit could not cover, and
those gaps are where something is most likely still hiding.
