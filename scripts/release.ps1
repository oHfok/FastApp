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
    5. With -Publish and -NotesFile, sets the GitHub release's body from that
       markdown file afterward (vpk upload github itself doesn't take notes,
       so this is a separate `gh release edit` call).

    Requires: Visual Studio (for MSBuild), the `vpk` global tool
    (dotnet tool install -g vpk), and `gh` authenticated (gh auth login)
    if you pass -Publish.

.EXAMPLE
    .\scripts\release.ps1 -Version 1.0.1
    Builds and packs v1.0.1 locally without publishing anything.

.EXAMPLE
    .\scripts\release.ps1 -Version 1.0.1 -Publish -NotesFile notes.md
    Builds, packs, publishes v1.0.1 live, and sets its GitHub release notes.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$Publish,

    # Path to a markdown file to use as the GitHub release's body. Only takes
    # effect with -Publish; ignored otherwise.
    [string]$NotesFile,

    # How many recent full packages to keep in .\Releases after a successful
    # publish. Everything older is deleted, but only once its version has been
    # confirmed live on GitHub -- see Remove-PublishedPackages.
    #
    # This exists because the folder never cleaned itself: it had accumulated
    # every package back to 1.0.6, 3.8 GB of them, and eventually filled the
    # disk mid-pack. A full disk is also one of the ways SQLite corrupts a
    # database, so this is not only about tidiness.
    [int]$KeepPackages = 2,

    # Skip the cleanup entirely, for when you want the local history.
    [switch]$NoPrune
)

$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
    Delete local packages that are already published, keeping the most recent.

.DESCRIPTION
    Only ever removes a package whose version is present in `gh release list`.
    That is the whole safety argument: a package that never made it upstream --
    a local build done without -Publish, or an upload that failed halfway -- is
    the only copy there is, and is left alone.

    If the published list cannot be fetched, nothing is deleted. Failing to
    tidy up is free; deleting the only copy of something is not.
#>
function Remove-PublishedPackages {
    param(
        [Parameter(Mandatory = $true)][string]$ReleasesDir,
        [Parameter(Mandatory = $true)][int]$Keep
    )

    if (-not (Test-Path $ReleasesDir)) { return }

    $published = $null
    try {
        $published = gh release list --repo oHfok/FastApp --limit 200 --json tagName |
                     ConvertFrom-Json | ForEach-Object { $_.tagName }
    }
    catch {
        Write-Host "==> Skipping cleanup: could not list published releases." -ForegroundColor Yellow
        return
    }
    if (-not $published) {
        Write-Host "==> Skipping cleanup: no published releases came back." -ForegroundColor Yellow
        return
    }

    $publishedSet = @{}
    foreach ($tag in $published) { $publishedSet[$tag] = $true }

    # Sorted by version, not by name: a string sort puts 1.0.9 after 1.0.10.
    $packages =
        Get-ChildItem $ReleasesDir -File -Filter "FastApp-*-full.nupkg" |
        ForEach-Object {
            $v = $_.Name -replace '^FastApp-', '' -replace '-full\.nupkg$', ''
            $parsed = $null
            if ([version]::TryParse($v, [ref]$parsed)) {
                [pscustomobject]@{ File = $_; Version = $v; Parsed = $parsed }
            }
        } |
        Sort-Object Parsed -Descending

    if ($packages.Count -le $Keep) { return }

    $stale = $packages | Select-Object -Skip $Keep
    $removed = 0
    $freed = 0

    foreach ($package in $stale) {
        if (-not $publishedSet.ContainsKey($package.Version)) {
            Write-Host "   keeping $($package.Version): not published, so this is the only copy." -ForegroundColor Yellow
            continue
        }

        $freed += $package.File.Length
        Remove-Item $package.File.FullName -Force
        $removed++

        # Its delta, if one was ever built, goes with it.
        $delta = Join-Path $ReleasesDir "FastApp-$($package.Version)-delta.nupkg"
        if (Test-Path $delta) {
            $freed += (Get-Item $delta).Length
            Remove-Item $delta -Force
        }
    }

    if ($removed -gt 0) {
        $mb = [math]::Round($freed / 1MB, 0)
        Write-Host "==> Cleaned up $removed published package(s), freeing $mb MB." -ForegroundColor Green
    }
}

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
# -restore, not just -t:Publish: the Publish target does not imply a restore,
# so a newly added PackageReference fails here with a XAML tag that "does not
# exist in the XML namespace" rather than anything mentioning NuGet.
& $msbuildPath $csprojPath `
    -restore `
    -t:Publish `
    -p:Configuration=Release `
    -p:RuntimeIdentifier=win-x64 `
    -p:SelfContained=true `
    -p:PublishDir=$publishDir `
    -verbosity:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# --- 4. Pack it into a Velopack release ------------------------------------
# --delta None, deliberately.
#
# Updating 1.3.1 -> 2.0.0 through a delta produced an install that was missing
# every file the release ADDED: the whole wwwrootpp folder and css	okens.css.
# FastApp started, reported version 2.0.0, and showed ERR_ACCESS_DENIED where its
# interface should have been.
#
# The packages were not at fault. "vpk delta patch" reconstructs 2.0.0 from
# 1.3.1 plus the delta byte for byte -- 679 files, none missing, no size
# differences -- so the delta itself is sound and the loss happens when the
# client applies it. Velopack 1.2.0 is the newest release, so there is no fix to
# upgrade to, and shipping a mechanism that silently drops files from an install
# is not worth the bandwidth it saves. Full packages are around 60 MB; an update
# that always works is worth more than a small one that sometimes does not.
Write-Host "==> Packing Velopack release (full packages only)..." -ForegroundColor Cyan
vpk pack `
    --packId FastApp `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe FastApp.exe `
    --packTitle "FastApp" `
    --packAuthors "oHfok" `
    --icon (Join-Path $repoRoot "Assets\app-icon.ico") `
    --runtime win-x64 `
    --delta None `
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

    if ($NotesFile) {
        if (-not (Test-Path $NotesFile)) { throw "NotesFile not found: $NotesFile" }
        Write-Host "==> Setting release notes from $NotesFile..." -ForegroundColor Cyan

        # GH_TOKEN is handed over explicitly rather than letting `gh` resolve its
        # own auth here. `gh auth token` above clearly succeeds (vpk uploads with
        # that very token), yet `gh release edit` invoked from this script kept
        # failing with "please run gh auth login" -- it re-resolves credentials
        # independently, and that lookup does not survive into this context.
        # Releases 1.0.6, 1.0.7 and 1.0.8 all published fine and then died on this
        # line, leaving the release live with an empty body until it was set by
        # hand afterwards. Passing the token removes the second lookup entirely.
        $priorGhToken = $env:GH_TOKEN
        $env:GH_TOKEN = $ghToken
        try {
            # Velopack tags releases by the raw version number (no "v" prefix),
            # confirmed against this repo's actual tags -- e.g. "1.0.5", not "v1.0.5".
            gh release edit $Version --repo oHfok/FastApp --notes-file $NotesFile
            $notesExit = $LASTEXITCODE
        }
        finally {
            $env:GH_TOKEN = $priorGhToken
        }

        if ($notesExit -ne 0) {
            # The release itself is already live at this point, so failing loudly
            # without saying how to finish the job is the unhelpful outcome.
            Write-Host ""
            Write-Host "!! Release v$Version PUBLISHED, but its notes were not set." -ForegroundColor Red
            Write-Host "   Run this to finish:" -ForegroundColor Yellow
            Write-Host "   gh release edit $Version --repo oHfok/FastApp --notes-file `"$NotesFile`"" -ForegroundColor Yellow
            throw "gh release edit failed."
        }
    }

    Write-Host "==> v$Version is live. Installed copies of FastApp will pick it up automatically on next launch." -ForegroundColor Green

    # Last, deliberately: everything above can throw, and a package that has not
    # been confirmed live is a package worth keeping. By this line the upload
    # succeeded and the notes are set.
    if (-not $NoPrune) { Remove-PublishedPackages -ReleasesDir $releasesDir -Keep $KeepPackages }
} else {
    Write-Host "==> Not published (pass -Publish to push this live to GitHub Releases)." -ForegroundColor Yellow
}
