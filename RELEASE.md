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
3. `Gloam-X.Y.Z-Setup.exe` (auto-updating, per-user install) and `Gloam-X.Y.Z-Portable.zip` are
   attached to the release. Installed clients pick up the update on next launch.

The version comes from the tag. `UpdateService.RepoUrl` and the CI upload target are both
`github.com/halideworks/gloam` - keep them identical.

## One-time setup (must exist before the first tag)

### GitHub repo (`halideworks/gloam`)
- **Settings -> Environments -> New environment: `release`** (no protection rules required; add a
  required reviewer if you want a manual gate before each signed release).
- **Settings -> Secrets and variables -> Actions -> Variables** (these are *Variables*, not Secrets):

  | Variable | Value |
  |---|---|
  | `AZURE_SIGN_ENDPOINT` | `https://eastus.codesigning.azure.net` |
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
2. Pick the version `X.Y.Z`. (Optional: set `<Version>` in `HDRGammaController.csproj`; CI overrides
   it from the tag regardless.)
3. Tag and push:
   ```bash
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```
4. Watch the Actions run. On success the GitHub Release is published with the Setup.exe + portable zip,
   both signed and timestamped.

## Pre-release validation checklist

- [ ] Build green, full test suite passing.
- [ ] **Colorimeter re-validation** of the two measured-behavior changes:
  - [ ] Tone-curve black-subtraction (FIX 2): grayscale tracking + shadow dE on a **raised-black /
        non-OLED** panel (OLED is unaffected).
  - [ ] Calibration bypass: confirm an in-progress calibration is not perturbed by a slider drag /
        night-mode tick / display-change/resume, and that normal apply + external-stomp restore still work.
- [ ] First-release sanity: confirm the repo is at `halideworks/gloam`, the `release` environment and
      all six variables exist, and the federated credential subject matches.

## Verifying the first signed release

- The release assets show a valid Authenticode signature (publisher = your validated legal name) and a
  trusted timestamp.
- Running `Setup.exe` installs per-user to `%LocalAppData%\GloamApp` with no "Unknown Publisher" warning
  (SmartScreen reputation still warms up over the first weeks/installs - expected).
- An installed older build detects the new version, downloads it, and applies on restart.

> Note: app data (settings, logs, calibration reports) lives under `%LocalAppData%\Gloam`, separate
> from the Velopack install root `%LocalAppData%\GloamApp`, so it survives updates and uninstalls.
