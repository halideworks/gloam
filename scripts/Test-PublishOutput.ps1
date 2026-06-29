param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [string]$ArgyllVersion = "Argyll_V3.5.0",

    [switch]$SkipLaunchSmoke
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish directory '$PublishDir' does not exist."
}

$publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $script:failures.Add($message) | Out-Null
}

function Test-RequiredFile([string]$relativePath, [int64]$minimumBytes = 1) {
    $path = Join-Path $publishRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Failure "Missing required file: $relativePath"
        return
    }

    $item = Get-Item -LiteralPath $path
    if ($item.Length -lt $minimumBytes) {
        Add-Failure "Required file is too small: $relativePath ($($item.Length) bytes)"
    }
}

function Test-RequiredText([string]$relativePath, [string]$pattern, [string]$description) {
    $path = Join-Path $publishRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Failure "Cannot validate $description because '$relativePath' is missing."
        return
    }

    $text = Get-Content -LiteralPath $path -Raw
    if ($text -notmatch $pattern) {
        Add-Failure "Missing $description in $relativePath."
    }
}

function Quote-ProcessArgument([string]$value) {
    '"' + ($value -replace '"', '\"') + '"'
}

function Test-LaunchSmoke {
    if ($SkipLaunchSmoke) {
        Write-Host "Launch smoke skipped."
        return
    }

    $exe = Join-Path $publishRoot "Gloam.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        Add-Failure "Cannot run launch smoke because Gloam.exe is missing."
        return
    }

    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GloamLaunchSmoke-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

    try {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $exe
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.Arguments = "--smoke --data-dir " + (Quote-ProcessArgument $smokeRoot)

        $process = [System.Diagnostics.Process]::Start($psi)
        if ($process -eq $null) {
            Add-Failure "Launch smoke failed to start Gloam.exe."
            return
        }

        if (-not $process.WaitForExit(15000)) {
            try { $process.Kill($true) } catch { }
            Add-Failure "Launch smoke timed out after 15 seconds."
            return
        }

        if ($process.ExitCode -ne 0) {
            Add-Failure "Launch smoke exited with code $($process.ExitCode)."
            return
        }

        $smokeLog = Join-Path $smokeRoot "app.log"
        if (-not (Test-Path -LiteralPath $smokeLog -PathType Leaf)) {
            Add-Failure "Launch smoke did not create an isolated app.log."
            return
        }

        $logText = Get-Content -LiteralPath $smokeLog -Raw
        if ($logText -notmatch "Launch smoke: completed\.") {
            Add-Failure "Launch smoke log did not contain the completion marker."
        }
    }
    finally {
        if (Test-Path -LiteralPath $smokeRoot -PathType Container) {
            Remove-Item -LiteralPath $smokeRoot -Recurse -Force
        }
    }
}

$requiredFiles = @(
    "Gloam.exe",
    "Gloam.dll",
    "HDRGammaController.Core.dll",
    "HDRGammaController.Interop.dll",
    "README.md",
    "LICENSE.txt",
    "THIRD_PARTY_NOTICES.txt",
    "srgb_to_gamma2p2_100_mhc2.icm",
    "srgb_to_gamma2p2_200_mhc2.icm",
    "srgb_to_gamma2p2_300_mhc2.icm",
    "srgb_to_gamma2p2_400_mhc2.icm",
    "srgb_to_gamma2p2_sdr.icm",
    "srgb_to_gamma2p2_unspecified.icm",
    "argyll_cache\$ArgyllVersion\bin\dispwin.exe",
    "argyll_cache\$ArgyllVersion\bin\spotread.exe"
)

foreach ($file in $requiredFiles) {
    Test-RequiredFile $file
}

Test-RequiredText "THIRD_PARTY_NOTICES.txt" "ArgyllCMS" "ArgyllCMS third-party notice"
Test-RequiredText "THIRD_PARTY_NOTICES.txt" "GNU Affero General Public License|AGPL" "ArgyllCMS AGPL license reference"
Test-RequiredText "THIRD_PARTY_NOTICES.txt" "Velopack" "Velopack third-party notice"

$profileFiles = Get-ChildItem -LiteralPath $publishRoot -Filter "*.icm" -File
if ($profileFiles.Count -lt 6) {
    Add-Failure "Expected at least 6 bundled ICM templates, found $($profileFiles.Count)."
}

$argyllBin = Join-Path $publishRoot "argyll_cache\$ArgyllVersion\bin"
if (Test-Path -LiteralPath $argyllBin -PathType Container) {
    $unexpectedNestedRoot = Join-Path $argyllBin $ArgyllVersion
    if (Test-Path -LiteralPath $unexpectedNestedRoot) {
        Add-Failure "Argyll tools appear to be nested incorrectly under '$unexpectedNestedRoot'."
    }
}

Test-LaunchSmoke

if ($failures.Count -gt 0) {
    $message = "Publish output validation failed:`n - " + ($failures -join "`n - ")
    throw $message
}

Write-Host "Publish output validation passed for '$publishRoot'."
