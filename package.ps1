
param(
    # Semantic version for this build. Local default is a throwaway; CI passes the tag
    # (e.g. 1.2.0). Velopack compares the installed package version against the feed, so
    # this must be the version the release is cut from.
    [string]$Version = "0.0.1",

    # CI uses -PublishOnly to produce the publish dir + bundled assets, then runs
    # 'vpk download/pack/upload' itself (with Azure signing). Local runs omit it to get a
    # ready-to-test Setup.exe + portable zip.
    [switch]$PublishOnly,

    [string]$PublishDir = "publish",
    [string]$ReleaseDir = "Releases"
)

$ErrorActionPreference = "Stop"

$projectPath = "src\HDRGammaController\HDRGammaController.csproj"
$argyllVersion = "Argyll_V3.5.0"
$argyllUrl = "https://www.argyllcms.com/${argyllVersion}_win64_exe.zip"
$argyllZip = "argyll_cache_3.5.0.zip"
$argyllExtractDir = "argyll_cache_3.5.0"
$argyllSha256 = "C80F78BA30E715079A00A14C2E7C9F533C58E6C8FDBC27F2D32B3D7813F9D132"

# --- Helper Functions ---

function Download-Argyll {
    if (-not (Test-Path $argyllZip)) {
        Write-Host "Downloading ArgyllCMS..."
        Invoke-WebRequest -Uri $argyllUrl -OutFile $argyllZip
    }

    # Verify integrity BEFORE extracting. Never run Expand-Archive on unverified content,
    # and never trust a pre-existing cached zip or extract dir - a poisoned cache in the
    # build workspace would otherwise be bundled into a signed release. Fail closed.
    $actualHash = (Get-FileHash $argyllZip -Algorithm SHA256).Hash
    if ($actualHash -ne $argyllSha256) {
        throw "Argyll archive integrity check failed (SHA-256 $actualHash). Delete '$argyllZip' and retry."
    }

    # Re-extract from scratch so a stale/tampered extract directory is never reused.
    if (Test-Path $argyllExtractDir) { Remove-Item -Path $argyllExtractDir -Recurse -Force }
    Write-Host "Extracting ArgyllCMS..."
    Expand-Archive -Path $argyllZip -DestinationPath $argyllExtractDir

    $root = Join-Path $argyllExtractDir $argyllVersion
    if (-not (Test-Path (Join-Path $root "bin\dispwin.exe")) -or
        -not (Test-Path (Join-Path $root "bin\spotread.exe"))) {
        throw "Argyll archive is missing dispwin.exe or spotread.exe"
    }
    return $root
}

# --- Execution ---

# 1. Publish self-contained and MULTI-FILE. Velopack needs a normal publish directory,
#    not PublishSingleFile=true: its delta and extract machinery operate on individual
#    files, and it lays its own Update.exe alongside the app exe.
if (Test-Path $PublishDir) { Remove-Item -Path $PublishDir -Recurse -Force }
Write-Host "--- Publishing (self-contained, multi-file) v$Version ---"
dotnet publish $projectPath -c Release -r win-x64 --self-contained true --output $PublishDir `
    /p:Version=$Version `
    /p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# 2. Bundle docs + ArgyllCMS INTO the publish dir so vpk packages them as part of the app.
#    (ICM templates are already copied to the output by the csproj.)
Copy-Item "README.md" $PublishDir
Copy-Item "LICENSE.txt" $PublishDir
Copy-Item "THIRD_PARTY_NOTICES.txt" $PublishDir

$argyllRoot = Download-Argyll
$bundleRoot = Join-Path $PublishDir "argyll_cache\$argyllVersion"
Write-Host "Bundling $argyllVersion from $argyllRoot"
New-Item -ItemType Directory -Path (Split-Path $bundleRoot) -Force | Out-Null
Copy-Item $argyllRoot $bundleRoot -Recurse

& ".\scripts\Test-PublishOutput.ps1" -PublishDir $PublishDir -ArgyllVersion $argyllVersion

if ($PublishOnly) {
    Write-Host "`nPublish-only complete: '$PublishDir' is ready for 'vpk pack'."
    return
}

# 3. Ensure the Velopack CLI (vpk) is on PATH.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Velopack CLI (vpk)..."
    dotnet tool install -g vpk | Out-Null
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}

# 4. Pack: emits Gloam-<ver>-Setup.exe, a portable zip, and (delta) nupkgs in $ReleaseDir.
#    Local builds are unsigned; CI re-packs with --azureTrustedSignFile (see build.yml).
#    NOTE: packId is "GloamApp" (not "Gloam") on purpose. Velopack installs into and OWNS
#    %LocalAppData%\<packId>, and uninstall deletes that whole tree. The app's data dir is
#    %LocalAppData%\Gloam (AppPaths.DataDir: settings, logs, calibration reports). Using a
#    distinct packId keeps Velopack's install root off the user's data so an uninstall (or
#    future Velopack cleanup) never destroys calibration data. The user-facing name is the
#    packTitle below.
Write-Host "--- Packing Velopack release v$Version ---"
vpk pack `
    --packId "GloamApp" `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe "Gloam.exe" `
    --packTitle "Gloam" `
    --packAuthors "David Torcivia" `
    --outputDir $ReleaseDir `
    --icon "src\HDRGammaController\app.ico"
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

Write-Host "`nPackaging complete. Artifacts in '$ReleaseDir':"
Get-ChildItem $ReleaseDir | Select-Object Name, Length | Format-Table
