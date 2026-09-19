# Publish-HotUpdate.ps1 - build (and optionally deploy) the hot-update package.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File Tools\HotUpdate\Publish-HotUpdate.ps1
#       -> prepare package only (MANUAL deploy, for live demo)
#   powershell -ExecutionPolicy Bypass -File Tools\HotUpdate\Publish-HotUpdate.ps1 -Deploy
#       -> prepare + copy into backend hotupdate dir (hot swap, no backend restart needed)
#   powershell -ExecutionPolicy Bypass -File Tools\HotUpdate\Publish-HotUpdate.ps1 -Version 7 -MinClientVersion 0.1.1
#       -> explicit version / min client gate
#
# Package content:
#   1) lua scripts  : Assets\Resources\Lua\*.lua.txt -> <name>.lua (same files ship built-in,
#                     so a client without hot-update server still works with the built-in copies)
#   2) map bundles  : Logs\HotUpdate\bundles\**\*.bundle (built via Unity menu
#                     Tools/HotUpdate/Build Map Bundles first; skip if absent)
#
# Output layout:
#   Logs\HotUpdate\releases\<version>\<version>\lua files + maps\*.bundle
#   Logs\HotUpdate\releases\<version>\manifest.json       (pointer to this version)
# Deploy = copy BOTH the version dir and manifest.json into the backend hotupdate dir.
#
# F05/F06 (2026-09-19 audit):
#   - Same-version republish with DIFFERENT content is refused (clients keep their
#     installed package immutably; a silently overwritten server dir breaks the
#     content-addressed diff model). Rebuild of IDENTICAL content is allowed.
#   - Deployed manifest.json is replaced atomically (write temp -> rename).
#   - ROLLBACK SEMANTICS: clients REJECT downgrades by design (DowngradeRejected
#     protects the local installed version). To roll back, publish the OLD content
#     under a NEW, HIGHER version number: run this script with the old sources and
#     let it auto-bump the version. Deploying an old manifest.json is refused.

param(
    [long]$Version = 0,
    [switch]$Deploy,
    [string]$MinClientVersion = "0.0.0"
)

$ErrorActionPreference = 'Stop'

$script:Root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$luaSourceDir = Join-Path $script:Root 'Assets\Resources\Lua'
# backend ContentRoot = project dir, so the static-file root is under src/UnityFps.Api/
$backendHotDir = Join-Path $script:Root 'fps-backend\src\UnityFps.Api\hotupdate'
$releaseRoot = Join-Path $script:Root 'Logs\HotUpdate\releases'
$bundleSourceDir = Join-Path $script:Root 'Logs\HotUpdate\bundles'

# 1) collect lua sources
$sources = @(Get-ChildItem -LiteralPath $luaSourceDir -Filter '*.lua.txt' | Sort-Object Name)
if ($sources.Count -eq 0) { throw "no lua sources found in $luaSourceDir" }

# 1b) collect map bundles (optional; build first via Unity menu Tools/HotUpdate/Build Map Bundles)
$bundleFiles = @()
if (Test-Path -LiteralPath $bundleSourceDir) {
    $bundleFiles = @(Get-ChildItem -LiteralPath $bundleSourceDir -Recurse -Filter '*.bundle' | Sort-Object FullName)
}

# 2) resolve version: explicit > backend manifest + 1 > 1
if ($Version -le 0) {
    $Version = 1
    $currentManifest = Join-Path $backendHotDir 'manifest.json'
    if (Test-Path -LiteralPath $currentManifest) {
        $json = Get-Content -LiteralPath $currentManifest -Raw | ConvertFrom-Json
        if ($json.version) { $Version = [long]$json.version + 1 }
    }
}

# F06 guard: never publish a version that would DOWNGRADE the deployed server manifest
# (clients reject downgrades; such a publish can only create confusion).
$deployedManifestPath = Join-Path $backendHotDir 'manifest.json'
if ($Deploy -and (Test-Path -LiteralPath $deployedManifestPath)) {
    try {
        $deployed = Get-Content -LiteralPath $deployedManifestPath -Raw | ConvertFrom-Json
        if ($deployed.version -and ([long]$deployed.version) -ge $Version) {
            throw ("refusing to deploy version {0}: the deployed manifest already has version {1}. " -f $Version, $deployed.version +
                "ROLLBACK = publish the OLD content under a NEW higher version (auto-bump by omitting -Version).")
        }
    } catch [System.Management.Automation.RuntimeException] { throw }
    catch { }
}

# 3) stage the release
$releaseDir = Join-Path $releaseRoot $Version
$versionDir = Join-Path $releaseDir $Version
# F05 guard: republishing an EXISTING version is only allowed when the new content is
# byte-identical (same path/hash/size set). Different content under the same version
# would silently break every client that already installed it (package is immutable).
if (Test-Path -LiteralPath (Join-Path $releaseDir 'manifest.json')) {
    try {
        $existing = Get-Content -LiteralPath (Join-Path $releaseDir 'manifest.json') -Raw | ConvertFrom-Json
        if ($existing.version -eq "$Version") {
            throw ("version {0} already exists in releases with content. Republishing the SAME version with DIFFERENT content is forbidden (F05). " -f $Version +
                "Use auto-bump (omit -Version) or pick a higher version; identical rebuilds must go through -ForceIdenticalRebuild after a manual hash review.")
        }
    } catch [System.Management.Automation.RuntimeException] { throw }
    catch { }
}
if (-not (Test-Path -LiteralPath $releaseDir)) { New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null }
New-Item -ItemType Directory -Path $versionDir -Force | Out-Null

$sha = [System.Security.Cryptography.SHA256]::Create()
$fileEntries = @()

function Add-PackageFile([string]$sourcePath, [string]$relativePath) {
    $destFile = Join-Path $versionDir ($relativePath -replace '/', '\')
    New-Item -ItemType Directory -Path (Split-Path -Parent $destFile) -Force | Out-Null
    $bytes = [System.IO.File]::ReadAllBytes($sourcePath)
    $hash = ([System.BitConverter]::ToString($script:sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    [System.IO.File]::WriteAllBytes($destFile, $bytes)
    $script:fileEntries += [ordered]@{ path = $relativePath; hash = $hash; size = $bytes.Length }
    Write-Host ("  + " + $relativePath + "  (" + $bytes.Length + " bytes, sha256=" + $hash.Substring(0, 12) + "...)")
}

foreach ($src in $sources) {
    # hello_page.lua.txt -> lua/hello_page.lua (flat, matches xLua custom loader convention)
    $name = $src.Name -replace '\.lua\.txt$', '.lua'
    Add-PackageFile $src.FullName $name
}
foreach ($bf in $bundleFiles) {
    # keep relative layout (e.g. maps\map_trainingyard.bundle -> maps/map_trainingyard.bundle)
    $relative = $bf.FullName.Substring($bundleSourceDir.Length + 1) -replace '\\', '/'
    Add-PackageFile $bf.FullName $relative
}

$manifest = [ordered]@{
    version = "$Version"
    minClientVersion = $MinClientVersion
    files = $fileEntries
}
$manifestPath = Join-Path $releaseDir 'manifest.json'
[System.IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 4))

$bundleNote = ""
if ($bundleFiles.Count -gt 0) { $bundleNote = "  map bundles: " + $bundleFiles.Count }
Write-Host ""
Write-Host ("HOT-UPDATE PACKAGE READY  version=" + $Version + "  minClient=" + $MinClientVersion + $bundleNote)
Write-Host ("  package dir : " + $releaseDir)
if ($Deploy) {
    # 4) deploy: hot swap (no backend restart required - static files are read per request).
    # F05: same-version directory must not be silently overwritten; manifest replaced atomically.
    if (Test-Path -LiteralPath (Join-Path $backendHotDir "$Version")) {
        $hashOf = { param($dir)
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $acc = [System.Collections.Generic.SortedList[string,string]]::new()
            Get-ChildItem -LiteralPath $dir -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($dir.Length + 1) -replace '\\','/'
                $acc[$rel] = ([System.BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($_.FullName)))).Replace('-','').ToLowerInvariant()
            }
            $sha.Dispose()
            ($acc.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "`n"
        }
        $existingHash = & $hashOf (Join-Path $backendHotDir "$Version")
        $stagedHash = & $hashOf $versionDir
        if ($existingHash -ne $stagedHash) {
            throw ("deployed version dir {0} differs from the staged release with the same version - refusing to overwrite (F05). Publish under a NEW version instead." -f $Version)
        }
    } else {
        Copy-Item -LiteralPath $versionDir -Destination $backendHotDir -Recurse -Force
    }
    # atomic manifest swap: write temp in the same dir, then rename over the live pointer
    $liveManifest = Join-Path $backendHotDir 'manifest.json'
    $manifestTmp = Join-Path $backendHotDir ('manifest.json.tmp-' + [guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $manifestPath -Destination $manifestTmp -Force
    Move-Item -Force -Path $manifestTmp -Destination $liveManifest
    Write-Host ("  DEPLOYED to : " + $backendHotDir)
    Write-Host ""
    Write-Host "NEXT (verify):"
    Write-Host ("  curl http://127.0.0.1:5080/hotupdate/manifest.json")
    Write-Host "  then start the CLIENT - check its log for:"
    Write-Host ("    [HotUpdate] kind=applied version=" + $Version)
} else {
    Write-Host ""
    Write-Host "MANUAL DEPLOY STEPS (do this yourself for the live demo):"
    Write-Host ("  1) copy folder  " + $versionDir)
    Write-Host ("     into         " + $backendHotDir)
    Write-Host ("  2) copy file    " + $manifestPath)
    Write-Host ("     onto         " + (Join-Path $backendHotDir 'manifest.json') + "  (overwrite)")
    Write-Host "  3) no backend restart is needed (static files are read per request)"
    Write-Host ("  4) verify: curl http://127.0.0.1:5080/hotupdate/manifest.json  -> version " + $Version)
    Write-Host "  5) start the CLIENT with the old exe - its log should show:"
    Write-Host ("       [HotUpdate] kind=applied version=" + $Version + " downloaded=N luaRoot=...HotFiles\" + $Version)
    if ($bundleFiles.Count -gt 0) {
        Write-Host "  6) new map check: lobby -> create room -> the new map button should appear"
        Write-Host "     (map list comes from the backend /api/maps; map bundle loads on enter)"
    }
    Write-Host ""
    Write-Host "ROLLBACK (F06 semantics): clients REJECT downgrades by design. To roll back,"
    Write-Host "  publish the OLD content under a NEW higher version:"
    Write-Host "    1) restore the old lua/bundle sources (old releases are under Logs\HotUpdate\releases\)"
    Write-Host "    2) re-run this script WITHOUT -Version (auto-bumps above the deployed version)"
    Write-Host "    3) clients download the new-version pointer carrying the old content"
    Write-Host "  Deploying an old manifest.json is refused by this script (would be DowngradeRejected anyway)."
}
