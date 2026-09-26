param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Private.Common.ps1"
$c=Read-PrivateConfig $ConfigPath
$state=Join-Path $c.stateRoot 'processes.json'
$originalHash=(Get-FileHash -LiteralPath $state).Hash
$records=@(Get-Content $state -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })
$processes=@(Get-CimInstance Win32_Process -Filter "Name='UnityFpsDedicatedServer.exe'")
$changed=$false
foreach($r in $records){
    if($r.name -eq 'api' -or (Test-ManagedProcess $r)){continue}
    $map=@(Get-CloudMaps $c | Where-Object id -eq $r.name)
    if($map.Count -ne 1){continue}
    $logPattern='-logFile\s+"('+[regex]::Escape((Join-Path $c.stateRoot 'logs')+'\')+'[0-9-]+\\'+[regex]::Escape($r.name+'.unity.log')+')"'
    $matches=@($processes | Where-Object {
        $_.ExecutablePath -eq $r.exe -and
        $_.CommandLine -match ('(?:^|\s)-instanceId\s+'+[regex]::Escape($map[0].instance)+'(?:\s|$)') -and
        $_.CommandLine -match ('(?:^|\s)-port\s+'+$map[0].port+'(?:\s|$)') -and
        $_.CommandLine -match ('(?:^|\s)-mapId\s+'+[regex]::Escape($r.name)+'(?:\s|$)') -and
        $_.CommandLine -match ('(?:^|\s)-buildVersion\s+'+[regex]::Escape($r.releaseId)+'(?:\s|$)') -and
        $_.CommandLine -match ('(?:^|\s)-backendUrl\s+'+[regex]::Escape($c.apiListenUrl)+'(?:\s|$)') -and
        $_.CommandLine -match ('(?:^|\s)-publicAddress\s+'+[regex]::Escape($c.publicAddress)+'(?:\s|$)') -and
        $_.CommandLine -match $logPattern
    })
    if($matches.Count -gt 1){throw ('Ambiguous matching processes for '+$r.name+'; no records written.')}
    if($matches.Count -eq 1){
        $p=Get-Process -Id $matches[0].ProcessId -ErrorAction Stop
        if($p.Path -ne $r.exe){throw 'Process changed during recovery.'}
        $logMatch=[regex]::Match($matches[0].CommandLine,$logPattern)
        if(-not(Test-Path -LiteralPath $logMatch.Groups[1].Value)){throw 'Original process log missing.'}
        $r.logDirectory=Split-Path -Parent $logMatch.Groups[1].Value
        $r.pid=$p.Id;$r.startedUtc=$p.StartTime.ToUniversalTime().ToString('o');$changed=$true
        Write-Output ('RECOVERED '+$r.name+' PID='+$r.pid+' (path, instance, port, release and original log verified)')
    }
}
if($changed){
    if((Get-FileHash $state).Hash -ne $originalHash){throw 'Process state changed concurrently; retry recovery.'}
    Copy-Item -LiteralPath $state -Destination ($state+'.backup-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    $records | ConvertTo-Json -Depth 8 | Set-Content ($state+'.tmp') -Encoding UTF8
    [IO.File]::Replace($state+'.tmp',$state,[System.Management.Automation.Language.NullString]::Value)
}
Write-Output 'RECOVERY_COMPLETE; no processes terminated. Use normal start/stop after refreshing status.'
