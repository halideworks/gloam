
$ErrorActionPreference = "Stop"

$projectPath = "src\HDRGammaController\HDRGammaController.csproj"
$publishRoot = "publish"
$argyllUrl = "https://www.argyllcms.com/Argyll_V3.3.0_win64_exe.zip"
$argyllZip = "argyll_cache.zip"
$argyllExtractDir = "argyll_cache"

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
    
    # Locate dispwin.exe
    $dispwin = Get-ChildItem -Path $argyllExtractDir -Recurse -Filter "dispwin.exe" | Select-Object -First 1
    return $dispwin.FullName
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
        $dispwinPath = Download-Argyll
        if ($dispwinPath) {
            Write-Host "Bundling dispwin.exe from $dispwinPath"
            # Current app logic looks in current directory OR bin/ subfolder. 
            # Let's put it in bin/ to keep root clean, similar to DownloadArgyllAsync logic
            # Actually, `DispwinRunner.cs` checks `AppDomain.CurrentDomain.BaseDirectory, "dispwin.exe"`
            # So putting it in root is easiest.
            Copy-Item $dispwinPath $outDir
        }
        else {
            Write-Warning "Could not find dispwin.exe to bundle!"
        }
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
