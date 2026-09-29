param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Private.Common.ps1"
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    Write-Output ('HOST_NOT_CONFIGURED: no host.json at ' + $ConfigPath + '; no processes were stopped.')
    exit 0
}
$c=Read-PrivateConfig $ConfigPath
New-Item -ItemType Directory -Path $c.stateRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $c.stateRoot 'admissions.paused') -Value 'draining'
$pool=Get-PrivatePool $c
$records=@()
$stateFile=Join-Path $c.stateRoot 'processes.json'
if(Test-Path -LiteralPath $stateFile){$records=@(Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })}
if (@(Get-PrivateBusyInstances $pool $records).Count) {
    Write-Output 'DRAINING: new admission stopped; matches are still active. Retry after players leave.'; exit 2
}
& "$PSScriptRoot/../Cloud/Stop-CloudServer.ps1" -ConfigPath $ConfigPath -StopProcesses
