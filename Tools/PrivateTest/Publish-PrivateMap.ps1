param([Parameter(Mandatory)][string]$ConfigPath,[Parameter(Mandatory)][string]$MapBuildPath,[int]$Port=7775)
. "$PSScriptRoot/Private.Common.ps1"
$c=Read-PrivateConfig $ConfigPath;$s=Read-CloudSecrets $c
$request=Get-Content (Join-Path $MapBuildPath 'map-request.json') -Raw | ConvertFrom-Json
$new=Get-Content (Join-Path $MapBuildPath 'Server/build-manifest.json') -Raw | ConvertFrom-Json
$base=Get-Content (Join-Path $c.releaseRoot 'Server/build-manifest.json') -Raw | ConvertFrom-Json
if ($new.protocolId -ne $base.protocolId -or $new.gamePlayDllSha256 -ne $base.gamePlayDllSha256) { throw 'Map changes gameplay code; distribute a new client/server release.' }
if ($request.mapId -notmatch '^[a-z0-9_]{1,32}$' -or $request.version -notmatch '^[1-9][0-9]*$') { throw 'Invalid map identity.' }
$modes = if ($request.modes) { @($request.modes) } else { @('TDM','KillRace') }
$capacity = if ($request.maxCapacity) { [int]$request.maxCapacity } else { 8 }
if (@($modes | Where-Object { $_ -notin @('TDM','KillRace') }).Count -or $capacity -notin @(2,4,8,12,16)) { throw 'Invalid map mode or capacity.' }
if($Port -lt 1024 -or $Port -gt 65535){throw 'Invalid port.'}
$pool=Get-PrivatePool $c
if (@($pool.instances | Where-Object {$_.mapId -eq $request.mapId -and ($_.currentPlayers -gt 0 -or $_.state -in @('Reserved','InMatch','Draining'))}).Count) { throw 'Map still in use; drain before publishing.' }
$scene=[IO.Path]::GetFileNameWithoutExtension($request.scenePath)
$bundle=Join-Path $MapBuildPath ('Content/maps/'+$scene.ToLowerInvariant()+'.bundle')
$hash=(Get-FileHash $bundle).Hash.ToLowerInvariant()
$hot=Join-Path $c.releaseRoot 'Api/hotupdate'
$old=Get-Content (Join-Path $hot 'maps-manifest.json') -Raw | ConvertFrom-Json
if ([long]$request.version -le [long]$old.version) { throw 'Content version must exceed current map content version.' }
$versionDir=Join-Path $hot $request.version
if(Test-Path $versionDir){throw 'Immutable content directory exists.'}
New-Item -ItemType Directory -Path (Join-Path $versionDir 'maps') -Force | Out-Null
$files=@($old.files | Where-Object path -ne ('maps/'+$scene.ToLowerInvariant()+'.bundle'))
foreach($f in $files){
 $source=[IO.Path]::GetFullPath((Join-Path (Join-Path $hot $old.version) $f.path))
 if(-not $source.StartsWith([IO.Path]::GetFullPath($hot)+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash $source).Hash -ne $f.hash){throw 'Previous map content invalid.'}
 Copy-Item -LiteralPath $source -Destination (Join-Path $versionDir $f.path)
}
Copy-Item -LiteralPath $bundle -Destination (Join-Path $versionDir ('maps/'+$scene.ToLowerInvariant()+'.bundle'))
$files+=@{path=('maps/'+$scene.ToLowerInvariant()+'.bundle');hash=$hash;size=(Get-Item $bundle).Length}
$stateFile=Join-Path $c.stateRoot 'processes.json'
$records=@(Get-Content $stateFile -Raw | ConvertFrom-Json | ForEach-Object { $_ })
# Stop only the drained instance owned by this tool. Never kill a foreign UDP owner.
foreach($instance in @($pool.instances | Where-Object mapId -eq $request.mapId)) {
 Invoke-RestMethod ($c.apiListenUrl+'/api/server-instances/'+[Uri]::EscapeDataString($instance.instanceId)+'/maintenance') -Method Post -Headers @{'X-Server-Key'=$s.serverKey} -TimeoutSec 10 | Out-Null
}
foreach($r in @($records | Where-Object name -eq $request.mapId)){if(Test-ManagedProcess $r){Stop-Process -Id $r.pid}}
if(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue){throw 'UDP port occupied.'}
$exe=[IO.Path]::GetFullPath((Join-Path $MapBuildPath 'Server/UnityFpsDedicatedServer.exe'))
$logs=Join-Path $c.stateRoot ('logs/'+$request.mapId+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$environment=@{FPS_SERVER_KEY=$s.serverKey;FPS_SERVER_BIND=$c.publicAddress;FPS_RELEASE_ID=$c.releaseId;FPS_MAP_ID=$request.mapId;FPS_MAP_SCENE=$scene;FPS_MAP_CONTENT_HASH=$hash}
$previous=@{}
try{
 foreach($k in $environment.Keys){$previous[$k]=[Environment]::GetEnvironmentVariable($k,'Process');[Environment]::SetEnvironmentVariable($k,$environment[$k],'Process')}
 $p=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-nographics','-dedicatedServer','-instanceId',('invite-'+$request.mapId),'-port',"$Port",'-publicAddress',$c.publicAddress,'-backendUrl',$c.apiListenUrl,'-mapId',$request.mapId,'-capacity',"$capacity",'-buildVersion',$c.releaseId,'-publicTestTelemetry','-logFile',('"'+(Join-Path $logs 'server.log')+'"'))
 $records=@($records | Where-Object name -ne $request.mapId)+@([pscustomobject]@{name=$request.mapId;pid=$p.Id;exe=$exe;startedUtc=$p.StartTime.ToUniversalTime().ToString('o');releaseId=$c.releaseId;logDirectory=$logs;mapScene=$scene;mapHash=$hash;mapPort=$Port})
 $records | ConvertTo-Json -Depth 5 | Set-Content $stateFile -Encoding UTF8
}finally{foreach($k in $previous.Keys){[Environment]::SetEnvironmentVariable($k,$previous[$k],'Process')}}
$ready=$false
for($i=0;$i -lt 30;$i++){
 $pool=Get-PrivatePool $c
 if(@($pool.instances | Where-Object {$_.mapId -eq $request.mapId -and $_.fresh -and $_.state -eq 'Ready' -and $_.protocolId -eq $new.protocolId -and $_.port -eq $Port}).Count -eq 1){$ready=$true;break}
 Start-Sleep -Seconds 1
}
if(-not $ready){throw 'New map DS not ready; catalog not promoted.'}
$old.version=[string]$request.version;$old.files=$files
$pointer=Join-Path $hot 'maps-manifest.json';$old | ConvertTo-Json -Depth 12 | Set-Content ($pointer+'.tmp') -Encoding UTF8
[IO.File]::Replace($pointer+'.tmp',$pointer,[System.Management.Automation.Language.NullString]::Value)
$catalogPath=Join-Path $c.stateRoot 'maps.json';$maps=@()
if(Test-Path $catalogPath){$maps=@(Get-Content $catalogPath -Raw | ConvertFrom-Json | Where-Object mapId -ne $request.mapId)}
$maps+=@{mapId=$request.mapId;displayName=$request.displayName;sceneName=$scene;modes=$modes;maxCapacity=$capacity;spawnGroups=@('Red','Blue','FFA');contentVersion=[string]$request.version;contentHash=$hash}
ConvertTo-Json -InputObject @($maps) -Depth 8 | Set-Content ($catalogPath+'.tmp') -Encoding UTF8
if(Test-Path $catalogPath){[IO.File]::Replace($catalogPath+'.tmp',$catalogPath,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($catalogPath+'.tmp',$catalogPath)}
Write-Output 'MAP_PUBLISHED; lobby clients can download without restarting.'
