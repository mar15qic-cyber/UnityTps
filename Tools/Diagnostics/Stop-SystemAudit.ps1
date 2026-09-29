param([Parameter(Mandatory=$true)][string]$RunName,
    [ValidateSet('SystemAudit0927','SystemFix0927')][string]$Campaign = 'SystemFix0927')
$ErrorActionPreference='Stop'
$auditProject=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$auditDirectory=Join-Path $auditProject ('Logs/'+$Campaign+'/'+$RunName)
$auditRecords=Get-Content -LiteralPath (Join-Path $auditDirectory 'processes.json') -Raw | ConvertFrom-Json
New-Item -ItemType File -Path (Join-Path $auditDirectory 'proxy.stop') -Force | Out-Null
foreach($auditRecord in $auditRecords | Where-Object role -in @('server','alpha','bravo','charlie','delta')) {
    $auditRoleName=$auditRecord.role
    $auditEvidenceDir=$auditDirectory
    if($auditRecord.role -eq 'delta'){$auditRoleName='charlie';$auditEvidenceDir=Join-Path $auditDirectory 'delta'}
    $auditStatePath=Join-Path $auditEvidenceDir ($auditRoleName+'.state.json')
    if(Test-Path -LiteralPath $auditStatePath) {
        $auditState=$null
        for($auditReadAttempt=0;$auditReadAttempt -lt 20 -and $null -eq $auditState;$auditReadAttempt++) {
            try { $auditState=Get-Content -LiteralPath $auditStatePath -Raw | ConvertFrom-Json }
            catch { Start-Sleep -Milliseconds 50 }
        }
        if($null -eq $auditState){throw ('Cannot read audit state after bounded retries: '+$auditStatePath)}
        @{seq=([int]$auditState.seq+100);op='quit';caseId='cleanup'} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $auditEvidenceDir ($auditRoleName+'.command.json')) -Encoding utf8
    }
}
Start-Sleep -Seconds 3
foreach($auditRecord in $auditRecords | Where-Object role -in @('server','alpha','bravo','charlie','delta','proxy')) {
    $auditProcess=Get-Process -Id $auditRecord.pid -ErrorAction SilentlyContinue
    if($auditProcess -and $auditProcess.Path -eq $auditRecord.executable -and [Math]::Abs(($auditProcess.StartTime.ToUniversalTime()-([datetime]$auditRecord.startedAtUtc).ToUniversalTime()).TotalSeconds)-lt 5) {
        Stop-Process -Id $auditProcess.Id
        Write-Output ('AUDIT_STOPPED '+$auditRecord.role+' '+$auditRecord.pid)
    }
}
Write-Output ('AUDIT_CLEANUP '+$RunName+' complete; shared backend untouched')
