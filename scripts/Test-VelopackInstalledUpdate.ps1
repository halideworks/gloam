param(
    [Parameter(Mandatory = $true)]
    [string]$OlderSetupPath,

    [string]$ExpectedVersion = "",

    [int]$TimeoutSeconds = 300,

    [switch]$IUnderstandThisModifiesLocalInstall
)

$ErrorActionPreference = "Stop"

if (-not $IUnderstandThisModifiesLocalInstall) {
    throw "This smoke test installs and stops the per-user Gloam app. Re-run with -IUnderstandThisModifiesLocalInstall."
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$setupPath = (Resolve-Path -LiteralPath $OlderSetupPath).Path

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    [xml]$projectXml = Get-Content -Raw -LiteralPath (Join-Path $repoRoot "src/HDRGammaController/HDRGammaController.csproj")
    $ExpectedVersion = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    throw "ExpectedVersion was not provided and could not be read from the project file."
}

$installRoot = Join-Path $env:LOCALAPPDATA "GloamApp"
$appExe = Join-Path $installRoot "current\Gloam.exe"
$stateFile = Join-Path $env:LOCALAPPDATA "Gloam\update-state.json"

function Stop-Gloam {
    Get-Process -Name "Gloam" -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Wait-Until([scriptblock]$Condition, [string]$Description) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for $Description."
}

Write-Host "Stopping any running Gloam process..."
Stop-Gloam

Write-Host "Installing older setup: $setupPath"
$install = Start-Process -FilePath $setupPath -ArgumentList "--silent" -Wait -PassThru
if ($install.ExitCode -ne 0) {
    throw "Older setup exited with code $($install.ExitCode)."
}

if (-not (Test-Path -LiteralPath $appExe -PathType Leaf)) {
    throw "Installed app was not found at $appExe."
}

Write-Host "Launching installed app to let it check the official update feed..."
$proc = Start-Process -FilePath $appExe -PassThru

Wait-Until {
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) { return $false }
    try {
        $state = Get-Content -Raw -LiteralPath $stateFile | ConvertFrom-Json
        return $state.LastScheduledVersion -eq $ExpectedVersion -or
            $state.PendingRestartVersion -eq $ExpectedVersion -or
            $state.InstalledVersion -eq $ExpectedVersion
    } catch {
        return $false
    }
} "Gloam to download/schedule update $ExpectedVersion"

Write-Host "Stopping Gloam so Velopack can apply the prepared update..."
Stop-Gloam

Wait-Until {
    Test-Path -LiteralPath (Join-Path $installRoot "packages\GloamApp-$ExpectedVersion-full.nupkg") -PathType Leaf
} "installed package GloamApp-$ExpectedVersion-full.nupkg"

Write-Host "Launching updated app once to verify installed package metadata..."
$proc = Start-Process -FilePath $appExe -PassThru
Start-Sleep -Seconds 5
Stop-Gloam

if (Test-Path -LiteralPath $stateFile -PathType Leaf) {
    $state = Get-Content -Raw -LiteralPath $stateFile | ConvertFrom-Json
    if ($state.InstalledVersion -ne $ExpectedVersion) {
        throw "Expected installed version $ExpectedVersion, update-state.json reports '$($state.InstalledVersion)'."
    }
}

Write-Host "Velopack installed-update smoke passed for $ExpectedVersion."
