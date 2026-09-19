# Stop-LocalServer.ps1 - stop ONLY what Start-LocalServer.ps1 recorded in state.json.
# Never kills by process name or port scan: every candidate PID must still match the
# expected executable path recorded at start time (2026-09-10 audit item 2).
param(
    [int]$BackendPort = 5080,
    [int]$DsPort = 7770
)

$ErrorActionPreference = "Continue"
$toolsDir  = $PSScriptRoot
$runtimeDir = Join-Path $toolsDir ".runtime"
$stateFile = Join-Path $runtimeDir "state.json"

if (-not (Test-Path $stateFile)) {
    Write-Output 'NOTHING_TO_STOP (no state.json - nothing was started by the launcher)'
    exit 0
}

$state = $null
try { $state = Get-Content $stateFile -Raw | ConvertFrom-Json } catch { }

if ($state -eq $null) {
    Write-Output 'STATE_CORRUPT (delete Tools/Server/.runtime/state.json manually if it is stale)'
    exit 0
}

function Assert-Path([int]$procId, [string]$expectedPath) {
    if ($procId -le 0) { return $false }
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($p -eq $null) { return $false }
    if ($expectedPath -and $p.Path -and ($p.Path -eq $expectedPath)) { return $true }
    return $false
}

$stopped = 0

# Dedicated server: recorded PID must still be the recorded exe.
$dsPid = 0
if ($state.dsPid) { $dsPid = [int]$state.dsPid }
if (Assert-Path $dsPid $state.dsExe) {
    Stop-Process -Id $dsPid -Force -ErrorAction SilentlyContinue
    Write-Output "STOPPED_DS pid=$dsPid"
    $stopped++
} else {
    Write-Output "DS_NOT_RUNNING_OR_FOREIGN pid=$dsPid (left untouched)"
}

# Phase 8 per-map DS instances: recorded in map-servers.json by Start-LocalServer.ps1 -AllMaps.
# Same discipline: only stop PIDs whose executable path still matches the recorded exe.
$mapStateFile = Join-Path $runtimeDir "map-servers.json"
if (Test-Path $mapStateFile) {
    $mapState = $null
    try { $mapState = Get-Content $mapStateFile -Raw | ConvertFrom-Json } catch { }
    foreach ($entry in @($mapState)) {
        if ($entry -eq $null) { continue }
        $mapPid = 0
        if ($entry.pid) { $mapPid = [int]$entry.pid }
        if (Assert-Path $mapPid $entry.exe) {
            Stop-Process -Id $mapPid -Force -ErrorAction SilentlyContinue
            Write-Output "STOPPED_MAP_DS pid=$mapPid map=$($entry.mapId)"
            $stopped++
        } else {
            Write-Output "MAP_DS_NOT_RUNNING_OR_FOREIGN pid=$mapPid map=$($entry.mapId) (left untouched)"
        }
    }
    Remove-Item $mapStateFile -ErrorAction SilentlyContinue
}

# Backend: recorded PID is the `dotnet run` parent; its UnityFps.Api child is killed too.
# Both are verified against the recorded project path in the command line before stopping.
$backendPid = 0
if ($state.backendPid) { $backendPid = [int]$state.backendPid }
if ($backendPid -gt 0) {
    $dotnet = Get-CimInstance Win32_Process -Filter "ProcessId=$backendPid" -ErrorAction SilentlyContinue
    if ($dotnet -and $dotnet.CommandLine -like '*UnityFps.Api.csproj*') {
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId=$backendPid" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq 'UnityFps.Api.exe' -or ($_.CommandLine -like '*UnityFps.Api.csproj*') }
        foreach ($child in $children) {
            Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Output "STOPPED_BACKEND_CHILD pid=$($child.ProcessId)"
            $stopped++
        }
        Stop-Process -Id $backendPid -Force -ErrorAction SilentlyContinue
        Write-Output "STOPPED_BACKEND pid=$backendPid"
        $stopped++
    } else {
        Write-Output "BACKEND_NOT_RUNNING_OR_FOREIGN pid=$backendPid (left untouched)"
    }
}

Remove-Item $stateFile -ErrorAction SilentlyContinue
Write-Output "DONE stopped=$stopped"
