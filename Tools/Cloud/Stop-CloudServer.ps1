param([Parameter(Mandatory)][string]$ConfigPath, [switch]$StopProcesses)
. "$PSScriptRoot/Cloud.Common.ps1"
$c = Read-CloudConfig $ConfigPath
New-Item -ItemType Directory -Path $c.stateRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $c.stateRoot 'admissions.paused') -Value 'maintenance'
if (-not $StopProcesses) { Write-Output 'ADMISSIONS_PAUSED; existing matches continue. Re-run with -StopProcesses only after draining.'; exit }
$state = Join-Path $c.stateRoot 'processes.json'
if (Test-Path -LiteralPath $state) {
    foreach ($r in @(Get-Content -LiteralPath $state -Raw | ConvertFrom-Json)) {
        if (Test-ManagedProcess $r) { Stop-Process -Id $r.pid -ErrorAction Stop }
    }
}
Write-Output 'MANAGED_PROCESSES_STOPPED; admission pause remains until explicitly removed.'
