param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Private.Common.ps1"
$c=Read-PrivateConfig $ConfigPath
New-Item -ItemType Directory -Path $c.stateRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $c.stateRoot 'admissions.paused') -Value 'draining'
$pool=Get-PrivatePool $c
if (@($pool.instances | Where-Object { $_.currentPlayers -gt 0 -or $_.state -in @('InMatch','Reserved','Draining') }).Count) {
    Write-Output 'DRAINING: new admission stopped; matches are still active. Retry after players leave.'; exit 2
}
& "$PSScriptRoot/../Cloud/Stop-CloudServer.ps1" -ConfigPath $ConfigPath -StopProcesses
