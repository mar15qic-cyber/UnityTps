param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$run=Join-Path $project ('Logs/VisualFix0928/'+$RunName)
$audit=[IO.Path]::GetFullPath((Join-Path $project 'Builds/PreviewV1-r2-Audit'))
$records=@(Get-Content "$run/processes.json" -Raw | ConvertFrom-Json)
foreach($role in @('alpha','bravo','server')){
 $s=Get-Content "$run/$role.state.json" -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
 if($s){
  @{seq=([int]$s.seq+100);op='quit';caseId='round1-end'} | ConvertTo-Json -Compress | Set-Content "$run/$role.command.tmp" -Encoding utf8
  Move-Item -LiteralPath "$run/$role.command.tmp" -Destination "$run/$role.command.json" -Force
 }
}
Start-Sleep -Seconds 5
$stopped=@()
foreach($r in $records){
 $p=Get-CimInstance Win32_Process -Filter ('ProcessId='+$r.pid)
 if(-not $p){$stopped+=@{role=$r.role;pid=$r.pid;alreadyExited=$true};continue}
 if(-not $p.ExecutablePath.StartsWith($audit+'\',[StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFullPath($p.ExecutablePath) -ne [IO.Path]::GetFullPath($r.exe)){throw 'Process identity changed; refusing to stop.'}
 $live=Get-Process -Id $r.pid
 $expectedStart=[DateTime]$r.startedUtc
 if([Math]::Abs(($live.StartTime.ToUniversalTime()-$expectedStart.ToUniversalTime()).TotalSeconds) -gt 1){throw 'Process start time changed.'}
 Stop-Process -Id $r.pid
 $stopped+=@{role=$r.role;pid=$r.pid;stopped=$true}
}
$stopped | ConvertTo-Json -Depth 4 | Set-Content "$run/stopped.json" -Encoding utf8
Write-Output 'ROUND1_STOPPED; original host processes untouched.'

