
$ErrorActionPreference = "Stop"

$projectPath = "src\HDRGammaController\HDRGammaController.csproj"
$publishRoot = "publish"
$argyllVersion = "Argyll_V3.5.0"
$argyllUrl = "https://www.argyllcms.com/${argyllVersion}_win64_exe.zip"
$argyllZip = "argyll_cache_3.5.0.zip"
$argyllExtractDir = "argyll_cache_3.5.0"
$argyllSha256 = "C80F78BA30E715079A00A14C2E7C9F533C58E6C8FDBC27F2D32B3D7813F9D132"

# Ensure clean state
if (Test-Path $publishRoot) { Remove-Item -Path $publishRoot -Recurse -Force }
New-Item -ItemType Directory -Path $publishRoot | Out-Null

# --- Helper Functions ---

function Download-Argyll {
    if (-not (Test-Path $argyllZip)) {
        Write-Host "Downloading ArgyllCMS..."
        Invoke-WebRequest -Uri $argyllUrl -OutFile $argyllZip
    }
    
    if (-not (Test-Path $argyllExtractDir)) {
        Write-Host "Extracting ArgyllCMS..."
        Expand-Archive -Path $argyllZip -DestinationPath $argyllExtractDir
    }
    
    $root = Join-Path $argyllExtractDir $argyllVersion
    if (-not (Test-Path (Join-Path $root "bin\dispwin.exe")) -or
        -not (Test-Path (Join-Path $root "bin\spotread.exe"))) {
        throw "Argyll archive is missing dispwin.exe or spotread.exe"
    }

    $actualHash = (Get-FileHash $argyllZip -Algorithm SHA256).Hash
    if ($actualHash -ne $argyllSha256) {
        throw "Argyll archive integrity check failed (SHA-256 $actualHash)"
    }
    return $root
}

function Create-Package {
    param(
        [string]$Name,
        [bool]$SelfContained,
        [bool]$IncludeArgyll
    )
    
    $outDir = "$publishRoot\$Name"
    Write-Host "`n--- Building $Name Package (SelfContained=$SelfContained) ---"
    
    # 1. Publish
    # Note: /p:IncludeNativeLibrariesForSelfExtract=true handles native .NET deps, but not external exes like dispwin
    dotnet publish $projectPath -c Release -r win-x64 --output $outDir `
        /p:PublishSingleFile=true `
        /p:SelfContained=$SelfContained `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:DebugType=None

    # 2. Copy Assets (Readme, License)
    # Note: ICM profiles are now embedded in the EXE and extracted at runtime
    Copy-Item "README.md" $outDir
    Copy-Item "LICENSE.txt" $outDir
    
    # 3. Bundle Argyll (Full only)
    if ($IncludeArgyll) {
        $argyllRoot = Download-Argyll
        $bundleRoot = Join-Path $outDir "argyll_cache\$argyllVersion"
        Write-Host "Bundling $argyllVersion from $argyllRoot"
        New-Item -ItemType Directory -Path (Split-Path $bundleRoot) -Force | Out-Null
        Copy-Item $argyllRoot $bundleRoot -Recurse
    }

    # 4. Zip
    $zipFile = "Gloam_${Name}.zip"
    if (Test-Path $zipFile) { Remove-Item $zipFile }
    
    Write-Host "Creating $zipFile..."
    Compress-Archive -Path "$outDir\*" -DestinationPath $zipFile
    Write-Host "Created $PWD\$zipFile"
}

# --- Execution ---

# Build Lite (Small, requires .NET 8)
Create-Package -Name "Lite" -SelfContained $false -IncludeArgyll $false

# Build Full (Large, standalone, includes Argyll)
Create-Package -Name "Full" -SelfContained $true -IncludeArgyll $true

Write-Host "`nPackaging Complete."
