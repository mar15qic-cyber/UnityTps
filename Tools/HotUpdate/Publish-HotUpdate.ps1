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
# Rollback = deploy the manifest.json of an older release (version dirs are kept).

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

# 3) stage the release
$releaseDir = Join-Path $releaseRoot $Version
$versionDir = Join-Path $releaseDir $Version
if (Test-Path -LiteralPath $releaseDir) { Remove-Item -LiteralPath $releaseDir -Recurse -Force }
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
    # 4) deploy: hot swap (no backend restart required - static files are read per request)
    if (Test-Path -LiteralPath (Join-Path $backendHotDir "$Version")) {
        Remove-Item -LiteralPath (Join-Path $backendHotDir "$Version") -Recurse -Force
    }
    Copy-Item -LiteralPath $versionDir -Destination $backendHotDir -Recurse -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $backendHotDir 'manifest.json') -Force
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
    Write-Host "ROLLBACK: deploy the manifest.json of an older release (kept under Logs\HotUpdate\releases\)."
}
