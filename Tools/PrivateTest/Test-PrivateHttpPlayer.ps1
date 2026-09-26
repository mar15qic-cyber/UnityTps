# Test only the unsealed build, against the current host's compatible hot-update release.
# Does not authenticate or enter a match. Restores environment bytes even on failure.
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$client=Join-Path $project 'Builds/PrivateInvitationClient'
$configPath=Join-Path $client 'client-environment.json'
$original=[IO.File]::ReadAllBytes($configPath)
$config=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
$remote=Invoke-RestMethod ($config.hotUpdateBaseUrl+'/manifest.json') -TimeoutSec 5
if($remote.releaseId -notmatch '^[a-zA-Z0-9_-]{1,80}$'){throw 'Invalid host release ID.'}
$log=Join-Path $project ('Logs/PrivateHttpSmoke-'+[Guid]::NewGuid().ToString('N')+'.log')
$process=$null
try {
    $config.releaseId=$remote.releaseId
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
    $process=Start-Process -FilePath (Join-Path $client 'UnityFpsClient.exe') -WorkingDirectory $client -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-nographics','-logFile',('"'+$log+'"'))
    $started=$process.StartTime
    $passed=$false
    for($i=0;$i -lt 45;$i++) {
        Start-Sleep -Seconds 1
        if(Test-Path -LiteralPath $log){
            $lines=Get-Content -LiteralPath $log
            if($lines -match 'Insecure connection not allowed'){throw 'Player still rejects HTTP.'}
            if($lines -match '\[HotUpdate\] kind=(applied|uptodate) '){$passed=$true;break}
            if($lines -match '\[HotUpdate\] kind=(error|release-mismatch) '){throw 'Hot update failed; inspect smoke log.'}
        }
        if($process.HasExited){break}
    }
    if(-not $passed){throw 'No successful hot-update result before smoke timeout.'}
    Select-String -LiteralPath $log -Pattern '\[HotUpdate\] kind=' | ForEach-Object {$_.Line}
    Write-Output ('PRIVATE_HTTP_PLAYER_SMOKE_PASSED '+$log)
} finally {
    if($process -and -not $process.HasExited){
        $current=Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if($current -and $current.StartTime -eq $started -and $current.Path -eq (Join-Path $client 'UnityFpsClient.exe')){Stop-Process -InputObject $current}
    }
    [IO.File]::WriteAllBytes($configPath,$original)
}
