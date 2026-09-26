param([Parameter(Mandatory)][string]$ConfigPath, [switch]$StopProcesses)
. "$PSScriptRoot/Cloud.Common.ps1"
$c = Read-CloudConfig $ConfigPath
$operationLock=New-Object Threading.Mutex($false,'Local\UnityFpsHostProcessOperation')
try{$locked=$operationLock.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
if(-not $locked){$operationLock.Dispose();throw 'Another host start/stop is in progress. Wait for it to finish.'}
New-Item -ItemType Directory -Path $c.stateRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $c.stateRoot 'admissions.paused') -Value 'maintenance'
if (-not $StopProcesses) { Write-Output 'ADMISSIONS_PAUSED; existing matches continue. Re-run with -StopProcesses only after draining.'; exit }
$state = Join-Path $c.stateRoot 'processes.json'
if (Test-Path -LiteralPath $state) {
    foreach ($r in @(Get-Content -LiteralPath $state -Raw | ConvertFrom-Json | ForEach-Object { $_ } | Sort-Object @{Expression={if($_.name -eq 'api'){1}else{0}}})) {
        if (Test-ManagedProcess $r) {
            Stop-Process -Id $r.pid -ErrorAction Stop
            Wait-Process -Id $r.pid -Timeout 10 -ErrorAction SilentlyContinue
            for($i=0;$i -lt 20 -and (Test-ManagedProcess $r);$i++){Start-Sleep -Milliseconds 100}
            if(Test-ManagedProcess $r){throw ('PROCESS_STILL_RUNNING '+$r.name+' PID='+$r.pid)}
            Write-Output ('STOPPED '+$r.name+' PID='+$r.pid)
        }else{Write-Output ('NOT_OWNED_OR_ALREADY_EXITED '+$r.name+' recordedPID='+$r.pid)}
    }
}
$ports=@(([uri]$c.apiListenUrl).Port)
if($c.PSObject.Properties.Name -contains 'playerTcpPort'){$ports+=$c.playerTcpPort}
$tcp=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -in $ports)
$udpPorts=@(Get-CloudMaps $c | ForEach-Object {$_.port})
$udp=@(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object LocalPort -in $udpPorts)
foreach($listener in @($tcp)+@($udp)){Write-Output ('PORT_STILL_OCCUPIED port='+$listener.LocalPort+' PID='+$listener.OwningProcess)}
if($tcp.Count -or $udp.Count){throw 'Managed stop completed, but ports remain occupied. Inspect process/port status; foreign processes were not terminated.'}
Write-Output 'MANAGED_PROCESSES_STOPPED; configured ports are free; admission pause remains until explicitly removed.'
