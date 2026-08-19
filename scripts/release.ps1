<#
.SYNOPSIS
    Builds, packages, and (optionally) publishes a FastApp release via Velopack.

.DESCRIPTION
    1. Stamps the given version into FastApp.csproj.
    2. Publishes a self-contained win-x64 build using the full Visual Studio
       MSBuild -- the `dotnet` CLI can't resolve the IWshRuntimeLibrary COM
       reference the startup-shortcut code depends on.
    3. Packs that build into a Velopack release (Setup.exe + delta packages)
       into .\Releases.
    4. With -Publish, uploads the release to GitHub Releases and makes it
       live immediately -- every installed copy of FastApp checks this feed
       on launch and will pick it up automatically. Without -Publish, the
       release is only built locally so you can test Releases\Setup.exe
       yourself first.

    Requires: Visual Studio (for MSBuild), the `vpk` global tool
    (dotnet tool install -g vpk), and `gh` authenticated (gh auth login)
    if you pass -Publish.

.EXAMPLE
    .\scripts\release.ps1 -Version 1.0.1
    Builds and packs v1.0.1 locally without publishing anything.

.EXAMPLE
    .\scripts\release.ps1 -Version 1.0.1 -Publish
    Builds, packs, and publishes v1.0.1 live to GitHub Releases.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$csprojPath = Join-Path $repoRoot "FastApp.csproj"
$publishDir = Join-Path $repoRoot "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$releasesDir = Join-Path $repoRoot "Releases"

# --- 1. Stamp the version into the csproj --------------------------------
Write-Host "==> Setting version to $Version in FastApp.csproj" -ForegroundColor Cyan
$csprojContent = Get-Content $csprojPath -Raw
$csprojContent = $csprojContent -replace '<Version>[\d\.]+</Version>', "<Version>$Version</Version>"
Set-Content -Path $csprojPath -Value $csprojContent -NoNewline

# --- 2. Locate the full MSBuild (the dotnet CLI can't resolve the COM ref) -
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found -- is Visual Studio installed?" }
$msbuildPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\amd64\MSBuild.exe" | Select-Object -First 1
if (-not $msbuildPath) { throw "Could not locate MSBuild.exe via vswhere." }
Write-Host "==> Using MSBuild: $msbuildPath" -ForegroundColor Cyan

# --- 3. Publish a self-contained win-x64 build ----------------------------
Write-Host "==> Publishing self-contained win-x64 build..." -ForegroundColor Cyan
& $msbuildPath $csprojPath `
    -t:Publish `
    -p:Configuration=Release `
    -p:RuntimeIdentifier=win-x64 `
    -p:SelfContained=true `
    -p:PublishDir=$publishDir `
    -verbosity:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# --- 4. Pack it into a Velopack release ------------------------------------
Write-Host "==> Packing Velopack release..." -ForegroundColor Cyan
vpk pack `
    --packId FastApp `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe FastApp.exe `
    --packTitle "FastApp" `
    --packAuthors "oHfok" `
    --icon (Join-Path $repoRoot "Assets\app-icon.ico") `
    --runtime win-x64 `
    --outputDir $releasesDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Write-Host "==> Packed. Check .\Releases for the generated Setup.exe -- test it before publishing." -ForegroundColor Green

# --- 5. Publish to GitHub Releases (opt-in) --------------------------------
if ($Publish) {
    Write-Host "==> Publishing v$Version to GitHub Releases..." -ForegroundColor Cyan
    $ghToken = (gh auth token 2>$null)
    if (-not $ghToken) { throw "Not authenticated with gh. Run 'gh auth login' first." }

    vpk upload github `
        --repoUrl "https://github.com/oHfok/FastApp" `
        --token $ghToken `
        --outputDir $releasesDir `
        --publish true `
        --releaseName "v$Version"
    if ($LASTEXITCODE -ne 0) { throw "vpk upload github failed." }

    Write-Host "==> v$Version is live. Installed copies of FastApp will pick it up automatically on next launch." -ForegroundColor Green
} else {
    Write-Host "==> Not published (pass -Publish to push this live to GitHub Releases)." -ForegroundColor Yellow
}
