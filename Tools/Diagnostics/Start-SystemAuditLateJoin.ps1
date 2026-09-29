param([string]$RunName='run08',
    [ValidateSet('SystemAudit0927','SystemFix0927')][string]$Campaign = 'SystemFix0927')
$ErrorActionPreference='Stop'
$auditProject=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$auditDir=Join-Path $auditProject ('Logs/'+$Campaign+'/'+$RunName)
$auditDelta=Join-Path $auditDir 'delta'
if(Test-Path -LiteralPath $auditDelta){throw 'Preserve prior late-join evidence; use a fresh run.'}
New-Item -ItemType Directory -Path $auditDelta | Out-Null
$auditClientDir=Join-Path $auditProject ('Builds/'+$Campaign+'/Client')
$auditExe=Join-Path $auditClientDir 'UnityFpsClient.exe'
$auditPassword='Audit-'+[Guid]::NewGuid().ToString('N')+'!'
$auditUser='au927'+$RunName+'delta'+[DateTime]::UtcNow.ToString('HHmmss')
$auditDelayedCode=Join-Path $auditDelta 'room-code.txt'
$auditProc=Start-Process -FilePath $auditExe -ArgumentList @('-screen-fullscreen','0','-screen-width','640','-screen-height','360','-runtimeAuditDir',('"'+$auditDelta+'"'),'-runtimeAuditRole','charlie','-itUser',$auditUser,'-itPass',$auditPassword,'-itAction','join','-itRoomFile',('"'+$auditDelayedCode+'"'),'-itMode','TDM','-itKill','100','-itTime','15','-publicTestTelemetry','-testRunId',('audit927-'+$RunName),'-logFile',('"'+(Join-Path $auditDelta 'client.log')+'"')) -WorkingDirectory $auditClientDir -WindowStyle Hidden -PassThru
$auditRecords=@(Get-Content -LiteralPath (Join-Path $auditDir 'processes.json') -Raw | ConvertFrom-Json)
$auditRecords+= [pscustomobject]@{role='delta';pid=$auditProc.Id;executable=$auditExe;startedAtUtc=[DateTime]::UtcNow.ToString('O')}
$auditRecords | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $auditDir 'processes.json') -Encoding utf8
$auditDeadline=[DateTime]::UtcNow.AddSeconds(60)
do {
    Start-Sleep -Seconds 1
    $auditStatePath=Join-Path $auditDelta 'charlie.state.json'
    if(Test-Path -LiteralPath $auditStatePath){$auditState=Get-Content -LiteralPath $auditStatePath -Raw | ConvertFrom-Json; if($auditState.apiReady -and -not $auditState.mapBusy){break}}
}while([DateTime]::UtcNow -lt $auditDeadline)
if(-not $auditState.apiReady){throw 'Late-join lobby readiness deadline'}
Copy-Item -LiteralPath (Join-Path $auditDir 'room-code.txt') -Destination $auditDelayedCode
Write-Output ('AUDIT_LATE_JOIN_START pid='+$auditProc.Id+' directory='+$auditDelta)
