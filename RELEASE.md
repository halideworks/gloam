# Release runbook

Gloam ships as a signed, self-updating Windows app built with [Velopack](https://velopack.io)
and code-signed by [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/).
Releases are cut by **pushing a `vX.Y.Z` tag**; CI builds, signs, and publishes a GitHub
Release, and installed apps auto-update from it.

## How a release flows

1. You push a tag `vX.Y.Z` to `main`.
2. `.github/workflows/build.yml` runs on `windows-latest` in the **`release`** environment:
   - `package.ps1 -Version X.Y.Z -PublishOnly` (self-contained multi-file publish + bundled ArgyllCMS).
   - `azure/login` via OIDC (no stored secret), then `vpk pack --azureTrustedSignFile` signs the
     installer + all bundled exes, then `vpk upload github` publishes the release.
3. `GloamApp-win-Setup.exe` (auto-updating, per-user install) and
   `GloamApp-win-Portable.zip` are attached to the release. Installed clients pick up the update on
   next launch.

The version comes from the tag. `UpdateService.RepoUrl` and the CI upload target are both
`github.com/halideworks/gloam` - keep them identical.

## One-time setup (must exist before the first tag)

### GitHub repo (`halideworks/gloam`)
- **Settings -> Environments -> New environment: `release`** (no protection rules required; add a
  required reviewer if you want a manual gate before each signed release).
- **Settings -> Secrets and variables -> Actions -> Variables** (these are *Variables*, not Secrets):

  | Variable | Value |
  |---|---|
  | `AZURE_SIGN_ENDPOINT` | `https://eus.codesigning.azure.net` (abbreviated region, e.g. `eus` = East US) |
  | `AZURE_SIGN_ACCOUNT` | `gloam-sign` |
  | `AZURE_SIGN_PROFILE` | `gloam-public-trust` |
  | `AZURE_SUBSCRIPTION_ID` | *(subscription holding the signing account)* |
  | `AZURE_CLIENT_ID` | *(the `gloam-github-signing` Entra app)* |
  | `AZURE_TENANT_ID` | *(the directory/tenant)* |

  > The three GUIDs are intentionally not stored in this repo. Set them here as Variables.

### Azure (one-time, already done)
- Trusted Signing account `gloam-sign` (East US) + cert profile `gloam-public-trust` (Public Trust),
  bound to a completed **individual** identity validation.
- Entra app `gloam-github-signing` with:
  - the **Artifact Signing Certificate Profile Signer** role on the `gloam-sign` account, and
  - a **federated credential** (scenario: GitHub Actions, entity type **Environment**, org
    `halideworks`, repo `gloam`, environment `release`) -> subject
    `repo:halideworks/gloam:environment:release`, which the workflow's `release` environment matches.

## Cutting a release

1. Make sure `main` has everything you want shipped and the build is green.
2. Pick the version `X.Y.Z`. Set `<Version>` in `Gloam.csproj` to match and commit it.
   CI overrides it from the tag anyway, but keeping them in sync means a local
   `.\package.ps1` with no arguments builds the same version the tag will.
3. Write the release notes at `docs/release-notes/vX.Y.Z.md` (see below).
4. Push `main`, then tag and push:
   ```bash
   git push origin main
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```
5. Watch the Actions run. On success the GitHub Release is published with the Setup.exe + portable zip,
   both signed and timestamped.
6. Attach the notes to the published release:
   ```bash
   gh release edit vX.Y.Z --notes-file docs/release-notes/vX.Y.Z.md
   ```

### Release notes

`vpk upload github` publishes with an empty body, so notes are a separate step. Releases
through v1.8.0 have no notes at all for this reason. Write them for someone deciding
whether to care, not for someone reading the diff: what changed for them, what they need
to do about it (usually nothing), and anything that changes measured behavior.

State plainly when a change affects calibration math or measured output. That is the one
category of change where a user may want to re-run a calibration rather than let the
update land silently.

## Pre-release validation checklist

- [ ] Build green, full test suite passing.
- [ ] Strict build clean: `dotnet build src/Gloam.sln -c Release -p:GloamStrictBuild=true`.
- [ ] `<Version>` in `Gloam.csproj` matches the tag being pushed.
- [ ] Release notes written at `docs/release-notes/vX.Y.Z.md`.
- [ ] **Colorimeter re-validation**, only when the release changes measured behavior
      (calibration math, LUT generation, drift handling, refinement). Verify the specific
      changed path on real hardware and record the before/after in the release notes.
      Skip for releases that do not touch those paths, and say so rather than leaving the
      box ambiguously unticked.

## Verifying a signed release

- The release assets show a valid Authenticode signature (publisher = your validated legal name) and a
  trusted timestamp.
- Running `Setup.exe` installs per-user to `%LocalAppData%\GloamApp` with no "Unknown Publisher" warning
  (SmartScreen reputation still warms up over the first weeks/installs - expected).
- An installed older build detects the new version, downloads it, and applies on restart.
  For a local end-to-end check against the official GitHub/Velopack feed, run:
  ```powershell
  .\scripts\Test-VelopackInstalledUpdate.ps1 -OlderSetupPath .\path\to\older\GloamApp-win-Setup.exe -ExpectedVersion X.Y.Z -IUnderstandThisModifiesLocalInstall
  ```

> Note: app data (settings, logs, calibration reports) lives under `%LocalAppData%\Gloam`, separate
> from the Velopack install root `%LocalAppData%\GloamApp`, so it survives updates and uninstalls.
