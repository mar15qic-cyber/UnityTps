param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName,[ValidateSet('Audit','Audit-Final')][string]$AuditDirectory='Audit')
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$audit=[IO.Path]::GetFullPath((Join-Path $project ('Builds/RealTest0930-Rework-Final/'+$AuditDirectory)))
$run=Join-Path $project ('Logs/RealTest0930-Rework/'+$RunName)
$records=@(Get-Content "$run/processes.json" -Raw|ConvertFrom-Json)
foreach($role in @('alpha','bravo','charlie','server')){
 if(Test-Path "$run/$role.state.json"){
  $state=$null
  for($attempt=0;$attempt -lt 20;$attempt++){try{$state=Get-Content "$run/$role.state.json" -Raw|ConvertFrom-Json;break}catch{Start-Sleep -Milliseconds 50}}
  if(-not $state){continue}
  @{seq=([int]$state.seq+100);op='quit';caseId='test-end'}|ConvertTo-Json -Compress|Set-Content "$run/$role.command.tmp" -Encoding utf8
  Move-Item -LiteralPath "$run/$role.command.tmp" -Destination "$run/$role.command.json" -Force
 }
}
Set-Content -LiteralPath "$run/proxy.stop" -Value 'stop' -Encoding utf8
Start-Sleep -Seconds 3
foreach($record in $records){
 $p=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
 if(-not $p){continue}
 $expected=[IO.Path]::GetFullPath($record.exe)
 if(-not $expected.StartsWith($audit+'\',[StringComparison]::OrdinalIgnoreCase)-or [IO.Path]::GetFullPath($p.Path)-ne $expected){throw 'Unexpected process identity'}
 if([Math]::Abs(($p.StartTime.ToUniversalTime()-([DateTime]$record.startedUtc).ToUniversalTime()).TotalSeconds)-gt 1){throw 'Process start time changed'}
 $p.Kill()
}
Write-Output 'REALTEST_STOPPED; only recorded test processes were stopped.'
