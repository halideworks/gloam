param(
    [string]$ExpectedRepoUrl = "https://github.com/halideworks/gloam",
    [string]$ExpectedPackId = "GloamApp"
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

function Read-RepoFile([string]$RelativePath) {
    Get-Content -Raw -LiteralPath (Join-Path $repoRoot $RelativePath)
}

$failures = New-Object System.Collections.Generic.List[string]

$updateService = Read-RepoFile "src/HDRGammaController/Services/UpdateService.cs"
if ($updateService -notmatch 'private const string RepoUrl = "([^"]+)"') {
    $failures.Add("UpdateService.RepoUrl was not found.")
} elseif ($Matches[1] -ne $ExpectedRepoUrl) {
    $failures.Add("UpdateService.RepoUrl is '$($Matches[1])', expected '$ExpectedRepoUrl'.")
}

$releaseDoc = Read-RepoFile "RELEASE.md"
if ($releaseDoc -notmatch [regex]::Escape($ExpectedRepoUrl.Replace("https://", ""))) {
    $failures.Add("RELEASE.md does not mention the canonical repo '$ExpectedRepoUrl'.")
}

$packageScript = Read-RepoFile "package.ps1"
if ($packageScript -notmatch "--packId\s+`"$([regex]::Escape($ExpectedPackId))`"") {
    $failures.Add("package.ps1 does not pack with --packId '$ExpectedPackId'.")
}
if ($packageScript -notmatch '\[string\]\$Version = ""') {
    $failures.Add("package.ps1 should default -Version to the project <Version>, not a throwaway value.")
}

$workflow = Read-RepoFile ".github/workflows/build.yml"
if ($workflow -notmatch "--packId\s+$([regex]::Escape($ExpectedPackId))") {
    $failures.Add("build.yml does not pack with --packId '$ExpectedPackId'.")
}
if ($workflow -notmatch 'vpk download github\s+--repoUrl https://github.com/\$\{\{ github\.repository \}\}') {
    $failures.Add("build.yml download step should use the current GitHub repository as the Velopack feed.")
}
if ($workflow -notmatch 'vpk upload github\s+[\s\S]*--repoUrl https://github.com/\$\{\{ github\.repository \}\}') {
    $failures.Add("build.yml upload step should publish to the current GitHub repository as the Velopack feed.")
}

if ($failures.Count -gt 0) {
    throw "Update feed consistency failed:`n - $($failures -join "`n - ")"
}

Write-Host "Update feed consistency passed."
