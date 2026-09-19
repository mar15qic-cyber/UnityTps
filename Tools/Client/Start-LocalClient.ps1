# Start-LocalClient.ps1 - launch a game client instance with a UNIQUE -logFile.
# Pure ASCII on purpose (PS 5.1 mis-reads non-BOM UTF-8 Chinese sources, project SOP).
#
# Why (P1 item 5, 2026-09-15 match-exit audit): two clients on the same machine
# share the default Player.log and overwrite each other mid-session, which made
# the audit unable to attribute FireTrace/exit events to a specific client.
# Every launch from this entry gets its own log under Tools\Client\Logs\.
param(
    [switch]$Development
)

$ErrorActionPreference = 'Stop'

$toolsDir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $toolsDir)
$exe = Join-Path $root 'Builds\ReleaseClient\UnityFpsClient.exe'
$logRoot = Join-Path $toolsDir 'Logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

if (-not (Test-Path $exe)) { Write-Output 'FAILED CLIENT_BUILD_MISSING'; Write-Output $exe; exit 1 }

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$logFile = Join-Path $logRoot ("client-$stamp-" + [guid]::NewGuid().ToString('N').Substring(0,6) + '.log')

# Deployment identity from the client build manifest (written by ClientBuild).
$manifestPath = Join-Path $root 'Builds\ReleaseClient\build-manifest.json'
$protocol = '<no manifest>'
$buildId = '<no manifest>'
if (Test-Path $manifestPath) {
    try {
        $m = Get-Content $manifestPath -Raw | ConvertFrom-Json
        $protocol = $m.protocolId
        $buildId = $m.buildId
    } catch { }
} else {
    Write-Output 'WARN CLIENT_MANIFEST_MISSING (rebuild via Tools/Client/Build Windows Client)'
}

$argList = @('-logFile', $logFile)
if ($Development) { $argList += @('-force-debugger') }

$proc = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
Write-Output "CLIENT_STARTING pid=$($proc.Id) protocol=$protocol buildId=$buildId"
Write-Output "CLIENT_LOG=$logFile"
Write-Output 'CLIENT_OK'
