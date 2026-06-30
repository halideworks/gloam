param(
    [string]$ReleaseDir = "Releases",

    [string]$Version = "",

    [string]$ExpectedPublisher = "David Torcivia",

    [string]$ExpectedPackId = "GloamApp",

    [string]$ExpectedChannel = "win"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ReleaseDir -PathType Container)) {
    throw "Release directory '$ReleaseDir' does not exist."
}

$releaseRoot = (Resolve-Path -LiteralPath $ReleaseDir).Path
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $script:failures.Add($message) | Out-Null
}

function Get-Artifact([string]$pattern, [string]$description) {
    $matches = @(Get-ChildItem -LiteralPath $releaseRoot -Filter $pattern -File)
    if ($matches.Count -eq 0) {
        $available = @((Get-ChildItem -LiteralPath $releaseRoot -File).Name)
        $availableText = if ($available.Count -eq 0) { "none" } else { $available -join ", " }
        Add-Failure "Missing $description matching '$pattern'. Available artifacts: $availableText."
        return $null
    }
    if ($matches.Count -gt 1) {
        Add-Failure "Expected one $description matching '$pattern', found $($matches.Count): $($matches.Name -join ', ')."
        return $null
    }
    return $matches[0]
}

function Test-Signature([string]$path, [string]$description) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-Failure "Cannot validate signature for missing $description '$path'."
        return
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        Add-Failure "$description is not signed with a valid Authenticode signature: $($signature.Status) $($signature.StatusMessage)"
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublisher)) {
        $subject = $signature.SignerCertificate.Subject
        if ($subject -notlike "*$ExpectedPublisher*") {
            Add-Failure "$description signer subject '$subject' does not contain expected publisher '$ExpectedPublisher'."
        }
    }
}

$versionPattern = if ([string]::IsNullOrWhiteSpace($Version)) { "*" } else { $Version }
$channelSuffix = if ([string]::IsNullOrWhiteSpace($ExpectedChannel)) { "" } else { "-$ExpectedChannel" }
$setup = Get-Artifact "$ExpectedPackId$channelSuffix-Setup.exe" "signed installer"
$portable = Get-Artifact "$ExpectedPackId$channelSuffix-Portable.zip" "portable package"
$fullPackage = Get-Artifact "$ExpectedPackId-$versionPattern-full.nupkg" "Velopack full package"

if ($setup -ne $null) {
    Test-Signature $setup.FullName "Installer '$($setup.Name)'"
}

if ($portable -ne $null) {
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GloamPortableSignature-" + [Guid]::NewGuid().ToString("N"))
    try {
        Expand-Archive -LiteralPath $portable.FullName -DestinationPath $extractRoot -Force
        $portableExe = @(Get-ChildItem -LiteralPath $extractRoot -Filter "Gloam.exe" -File -Recurse)
        if ($portableExe.Count -eq 0) {
            Add-Failure "Portable package '$($portable.Name)' does not contain Gloam.exe."
        }
        elseif ($portableExe.Count -gt 1) {
            Add-Failure "Portable package '$($portable.Name)' contains multiple Gloam.exe files."
        }
        else {
            Test-Signature $portableExe[0].FullName "Portable Gloam.exe"
        }
    }
    finally {
        if (Test-Path -LiteralPath $extractRoot -PathType Container) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}

if ($fullPackage -ne $null) {
    $extractRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GloamVelopackPackageSignature-" + [Guid]::NewGuid().ToString("N"))
    try {
        Expand-Archive -LiteralPath $fullPackage.FullName -DestinationPath $extractRoot -Force

        $nuspec = @(Get-ChildItem -LiteralPath $extractRoot -Filter "*.nuspec" -File -Recurse)
        if ($nuspec.Count -eq 0) {
            Add-Failure "Velopack package '$($fullPackage.Name)' does not contain a .nuspec."
        }
        elseif ($nuspec.Count -gt 1) {
            Add-Failure "Velopack package '$($fullPackage.Name)' contains multiple .nuspec files."
        }
        else {
            [xml]$nuspecXml = Get-Content -Raw -LiteralPath $nuspec[0].FullName
            $idNode = $nuspecXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']")
            $versionNode = $nuspecXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']")
            $packageId = if ($idNode -eq $null) { "" } else { $idNode.InnerText }
            $packageVersion = if ($versionNode -eq $null) { "" } else { $versionNode.InnerText }
            if ($packageId -ne $ExpectedPackId) {
                Add-Failure "Velopack package id '$packageId' does not match expected '$ExpectedPackId'."
            }
            if (-not [string]::IsNullOrWhiteSpace($Version) -and $packageVersion -ne $Version) {
                Add-Failure "Velopack package version '$packageVersion' does not match expected '$Version'."
            }
        }

        $packageExe = @(Get-ChildItem -LiteralPath $extractRoot -Filter "Gloam.exe" -File -Recurse)
        if ($packageExe.Count -eq 0) {
            Add-Failure "Velopack package '$($fullPackage.Name)' does not contain Gloam.exe."
        }
        elseif ($packageExe.Count -gt 1) {
            Add-Failure "Velopack package '$($fullPackage.Name)' contains multiple Gloam.exe files."
        }
        else {
            Test-Signature $packageExe[0].FullName "Velopack package Gloam.exe"
        }
    }
    finally {
        if (Test-Path -LiteralPath $extractRoot -PathType Container) {
            Remove-Item -LiteralPath $extractRoot -Recurse -Force
        }
    }
}

if ($failures.Count -gt 0) {
    $message = "Signed release artifact validation failed:`n - " + ($failures -join "`n - ")
    throw $message
}

Write-Host "Signed release artifact validation passed for '$releaseRoot'."
