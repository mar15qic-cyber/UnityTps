param([switch]$Install,[long]$HotUpdateVersion=0,[string]$ResumeRecoveryDirectory)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stage=Join-Path $project 'Builds/RealTest0930'
$client=Get-Content "$stage/Client/build-manifest.json" -Raw|ConvertFrom-Json
$server=Get-Content "$stage/Server/build-manifest.json" -Raw|ConvertFrom-Json
if($client.protocolId -ne 'fps-net-v25' -or $server.protocolId -ne $client.protocolId -or $client.inputDigest -ne $server.inputDigest){throw 'Release identity mismatch.'}
$hot=Get-Content "$project/fps-backend/src/UnityFps.Api/hotupdate/maps-manifest.json" -Raw|ConvertFrom-Json
if($hot.protocolId -ne $client.protocolId -and $HotUpdateVersion -le 0){throw 'Select the final staged hot-map version before installing.'}
if(-not $Install){@{client=$client.buildId;server=$server.buildId;inputDigest=$client.inputDigest;hotVersion=$(if($HotUpdateVersion -gt 0){$HotUpdateVersion}else{$hot.version})}|ConvertTo-Json;exit 0}
if(-not $ResumeRecoveryDirectory){
$taskServerKey=(Get-Content "$project/Tools/Server/.runtime/serverKey.txt" -Raw).Trim()
$taskPool=Invoke-RestMethod 'http://127.0.0.1:5080/api/server-instances/pool?requestedCapacity=8' -Headers @{'X-Server-Key'=$taskServerKey} -TimeoutSec 5
if(@($taskPool.instances|Where-Object {$_.fresh -and ($_.currentPlayers -gt 0 -or $_.state -in @('InMatch','Reserved'))}).Count){throw 'Active development match detected; do not replace its server.'}
}
if($ResumeRecoveryDirectory){
 $recovery=[IO.Path]::GetFullPath($ResumeRecoveryDirectory)
 $recoveryRoot=[IO.Path]::GetFullPath((Join-Path $project 'Logs/RealTest0930'))+'\Recovery-'
 if(-not $recovery.StartsWith($recoveryRoot,[StringComparison]::OrdinalIgnoreCase)-or -not(Test-Path -LiteralPath "$recovery/Server/build-manifest.json")-or -not(Test-Path -LiteralPath "$recovery/Api/UnityFps.Api.dll")){throw 'Recovery backup does not belong to this installation'}
 if(Get-NetTCPConnection -LocalPort 5080 -State Listen -ErrorAction SilentlyContinue){throw 'Development API resumed independently; do not overwrite it'}
 $apiTarget=Join-Path $project 'fps-backend/src/UnityFps.Api/bin/Debug/net8.0'
}else{
$recovery=Join-Path $project ('Logs/RealTest0930/Recovery-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $recovery|Out-Null
foreach($pair in @(@{from='Builds/Server';to='Server'},@{from='Builds/ReleaseClient';to='Client'})){
 Copy-Item -LiteralPath (Join-Path $project $pair.from) -Destination (Join-Path $recovery $pair.to) -Recurse
}
$apiTarget=Join-Path $project 'fps-backend/src/UnityFps.Api/bin/Debug/net8.0'
$apiBackup=Join-Path $recovery 'Api';New-Item -ItemType Directory -Path $apiBackup|Out-Null
foreach($f in Get-ChildItem -LiteralPath $apiTarget -File){if($f.Extension -in @('.dll','.exe','.pdb')-or $f.Name -in @('UnityFps.Api.deps.json','UnityFps.Api.runtimeconfig.json')){Copy-Item -LiteralPath $f.FullName -Destination $apiBackup}}
$oldHotDir=Join-Path $project 'fps-backend/src/UnityFps.Api/hotupdate'
$hotBackup=Join-Path $recovery 'HotUpdate';New-Item -ItemType Directory -Path $hotBackup|Out-Null
foreach($pointer in @('manifest.json','maps-manifest.json')){
 $pointerPath=Join-Path $oldHotDir $pointer
 if(Test-Path -LiteralPath $pointerPath){
  Copy-Item -LiteralPath $pointerPath -Destination $hotBackup
  $oldVersion=[string](Get-Content -LiteralPath $pointerPath -Raw|ConvertFrom-Json).version
  if((Test-Path -LiteralPath (Join-Path $oldHotDir $oldVersion))-and -not(Test-Path -LiteralPath (Join-Path $hotBackup $oldVersion))){Copy-Item -LiteralPath (Join-Path $oldHotDir $oldVersion) -Destination $hotBackup -Recurse}
 }
}
}
# No database export: only the approved schema/seed migration on normal API startup.
& "$project/Tools/Server/Stop-LocalServer.ps1"
foreach($p in Get-CimInstance Win32_Process|Where-Object {$_.ExecutablePath -eq "$project\Builds\Server\UnityFpsDedicatedServer.exe" -or $_.ExecutablePath -eq "$apiTarget\UnityFps.Api.exe"}){throw 'Recorded development process did not stop; nothing overwritten.'}
if($HotUpdateVersion -gt 0){
 & "$project/Tools/HotUpdate/Publish-HotUpdate.ps1" -Version $HotUpdateVersion -Deploy
 if($LASTEXITCODE -ne 0){throw 'Final hot-map promotion failed'}
 $hot=Get-Content "$oldHotDir/maps-manifest.json" -Raw|ConvertFrom-Json
}
if($hot.protocolId -ne $client.protocolId){throw 'Hot-map protocol differs from final players'}
Copy-Item "$stage/Server/*" "$project/Builds/Server" -Recurse -Force
Copy-Item "$stage/Client/*" "$project/Builds/ReleaseClient" -Recurse -Force
foreach($f in Get-ChildItem -LiteralPath "$stage/Api" -File){if($f.Extension -in @('.dll','.exe','.pdb')-or $f.Name -in @('UnityFps.Api.deps.json','UnityFps.Api.runtimeconfig.json')){Copy-Item -LiteralPath $f.FullName -Destination $apiTarget -Force}}
foreach($role in @('Server','Client')){
 $target=if($role -eq 'Server'){'Builds/Server'}else{'Builds/ReleaseClient'}
 $app=if($role -eq 'Server'){'UnityFpsDedicatedServer'}else{'UnityFpsClient'}
 foreach($name in @('Game.Gameplay.dll','Game.UI.dll','Game.Presentation.dll','Game.Account.dll','Game.Core.dll')){
  if((Get-FileHash "$stage/$role/${app}_Data/Managed/$name").Hash -ne (Get-FileHash "$project/$target/${app}_Data/Managed/$name").Hash){throw 'Installed gameplay assembly mismatch.'}
 }
}
& "$project/Tools/Server/Start-LocalServer.ps1" -AllMaps
if($LASTEXITCODE -ne 0){throw 'Local stack did not become ready.'}
@{installedAtUtc=[DateTime]::UtcNow.ToString('o');inputDigest=$client.inputDigest;protocol=$client.protocolId;client=$client.buildId;server=$server.buildId;hotVersion=$hot.version;recovery=$recovery}|ConvertTo-Json -Depth 5|Set-Content "$project/Logs/RealTest0930/installed.json" -Encoding utf8
Write-Output 'LOCAL_V25_INSTALLED; existing tester distributions preserved.'
