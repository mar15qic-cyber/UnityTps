param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName, [switch]$UseBuiltAudit,
 [ValidateSet('arena','map_01','map_02','map_03','map_04','map_05')][string]$MapId='arena', [ValidateSet('TDM','KillRace')][string]$Mode='TDM', [switch]$NetworkProxy,
 [ValidateSet('Audit','Audit-Final')][string]$AuditDirectory='Audit')
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$release=Join-Path $project 'Builds/RealTest0930-Rework-Final'
$audit=Join-Path $project ('Builds/RealTest0930-Rework-Final/'+$AuditDirectory)
$run=Join-Path $project ('Logs/RealTest0930-Rework/'+$RunName)
if(Test-Path -LiteralPath $run){throw 'Evidence directory already exists; use a new RunName.'}
foreach($port in @(5180,5181)){if(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue){throw 'Test TCP port occupied.'}}
if(Get-NetUDPEndpoint -LocalPort 27770 -ErrorAction SilentlyContinue){throw 'Test UDP port occupied.'}
New-Item -ItemType Directory -Path $run -Force | Out-Null
if($NetworkProxy){
 $proxy=Start-Process -FilePath 'C:\Python314\python.exe' -ArgumentList @('"'+$project+'/Tools/Network/runtime_audit_proxy.py"','--directory',('"'+$run+'"'),'--seconds','1800') -WindowStyle Hidden -PassThru
 @{pid=$proxy.Id;exe=$proxy.Path;startedUtc=$proxy.StartTime.ToUniversalTime().ToString('o')}|ConvertTo-Json|Set-Content "$run/proxy-process.json" -Encoding utf8
}
$identities=@()
if(-not $UseBuiltAudit){
foreach($role in @('Server','Client')){
 $app=if($role -eq 'Server'){'UnityFpsDedicatedServer'}else{'UnityFpsClient'}
 $published=Get-Content "$release/$role/build-manifest.json" -Raw | ConvertFrom-Json
 $instrumented=Get-Content "$audit/$role/build-manifest.json" -Raw | ConvertFrom-Json
 if($published.inputDigest -ne $instrumented.inputDigest -or $published.protocolId -ne $instrumented.protocolId){throw 'Audit inputs differ from release.'}
 $data=Join-Path $audit "$role/${app}_Data"
 if(-not(Test-Path "$data/Managed/Game.RuntimeAudit.dll")){throw 'Built audit driver missing.'}
 $auditInit=Get-Content "$data/RuntimeInitializeOnLoads.json" -Raw | ConvertFrom-Json
 $probeInit=@($auditInit.root | Where-Object assemblyName -eq 'Game.RuntimeAudit')
 if(-not @($probeInit | Where-Object methodName -eq 'Boot').Count){throw 'Audit initializer missing.'}
 # Test the exact published assets and gameplay DLLs, adding only the local observation driver.
 Copy-Item "$release/$role/*" "$audit/$role" -Recurse -Force
 $init=Get-Content "$data/RuntimeInitializeOnLoads.json" -Raw | ConvertFrom-Json
 Copy-Item "$data/RuntimeInitializeOnLoads.json" "$run/$role-RuntimeInitializeOnLoads.original.json"
 $init.root=@($init.root)+$probeInit
 [IO.File]::WriteAllText("$data/RuntimeInitializeOnLoads.json",($init|ConvertTo-Json -Depth 12 -Compress),[Text.UTF8Encoding]::new($false))
 foreach($dll in @('Game.Gameplay.dll','Game.Presentation.dll','Game.UI.dll','Game.Core.dll','Game.Account.dll')){
  $a=(Get-FileHash "$release/$role/${app}_Data/Managed/$dll").Hash
  $b=(Get-FileHash "$audit/$role/${app}_Data/Managed/$dll").Hash
  if($a -ne $b){throw ('Gameplay assembly differs from release: '+$role+'/'+$dll)}
 }
 $load=Get-Content "$data/ScriptingAssemblies.json" -Raw | ConvertFrom-Json
 if($load.names -notcontains 'Game.RuntimeAudit.dll'){
  Copy-Item "$data/ScriptingAssemblies.json" "$run/$role-ScriptingAssemblies.original.json"
  $load.names+= 'Game.RuntimeAudit.dll';$load.types+=16
  [IO.File]::WriteAllText("$data/ScriptingAssemblies.json",($load|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
 }
 $identities += @{role=$role;inputDigest=$published.inputDigest;buildId=$published.buildId;gameAssembliesIdentical=$true}
}
}
if($UseBuiltAudit){
 foreach($role in @('Server','Client')){
  $app=if($role -eq 'Server'){'UnityFpsDedicatedServer'}else{'UnityFpsClient'}
  $auditData=Join-Path $audit "$role/${app}_Data"
  $auditScripts=Get-Content "$auditData/ScriptingAssemblies.json" -Raw | ConvertFrom-Json
  $auditInitializers=Get-Content "$auditData/RuntimeInitializeOnLoads.json" -Raw | ConvertFrom-Json
  if(-not @($auditInitializers.root | Where-Object { $_.assemblyName -eq 'Game.RuntimeAudit' -and $_.methodName -eq 'Boot' }).Count){throw 'Audit bootstrap is absent from built player.'}
  # Unity incremental output can omit the define-constrained assembly from this list.
  # Retain the fully built audit scene/execution-order data; register only its existing DLL.
  if($auditScripts.names -notcontains 'Game.RuntimeAudit.dll'){
   Copy-Item "$auditData/ScriptingAssemblies.json" "$run/$role-ScriptingAssemblies.original.json"
   $auditScripts.names+='Game.RuntimeAudit.dll';$auditScripts.types+=16
   [IO.File]::WriteAllText("$auditData/ScriptingAssemblies.json",($auditScripts|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
  }
  $m=Get-Content "$audit/$role/build-manifest.json" -Raw | ConvertFrom-Json
  $identities+=@{role=$role;buildId=$m.buildId;inputDigest=$m.inputDigest;protocol=$m.protocolId;instrumentedBuild=$true;presentationSha=(Get-FileHash "$audit/$role/${app}_Data/Managed/Game.Presentation.dll").Hash}
 }
 if($identities[0].inputDigest -ne $identities[1].inputDigest){throw 'Built audit pair input mismatch.'}
}
$identities | ConvertTo-Json -Depth 5 | Set-Content "$run/identity.json" -Encoding utf8
if(-not (Test-Path "$audit/Api")){ Copy-Item "$release/Api" "$audit/Api" -Recurse }
foreach($taskApiFile in Get-ChildItem -LiteralPath "$release/Api" -File){
 if($taskApiFile.Extension -in @('.dll','.exe','.pdb','.json')){Copy-Item -LiteralPath $taskApiFile.FullName -Destination "$audit/Api" -Force}
}
$maps=Get-Content "$audit/Api/hotupdate/maps-manifest.json" -Raw | ConvertFrom-Json
if($maps.releaseId -ne 'PreviewV1'){
 $sourceVersion=[string]$maps.version; $maps.version=[string]([int]$maps.version+1); $maps.releaseId='PreviewV1'
 Copy-Item "$audit/Api/hotupdate/$sourceVersion" "$audit/Api/hotupdate/$($maps.version)" -Recurse -Force
 [IO.File]::WriteAllText("$audit/Api/hotupdate/maps-manifest.json",($maps|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
$night=@($maps.files | Where-Object path -eq 'maps/map_nightrelay.bundle')[0]
$training=@($maps.files | Where-Object path -eq 'maps/map_trainingyard.bundle')[0]
ConvertTo-Json -InputObject @(@{mapId='map_04';displayName='Training Yard';sceneName='Map_TrainingYard';modes=@('TDM','KillRace');maxCapacity=8;spawnGroups=@('Red','Blue','FFA');contentVersion=[string]$maps.version;contentHash=$training.hash},@{mapId='map_05';displayName='Night Relay';sceneName='Map_NightRelay';modes=@('KillRace');maxCapacity=8;spawnGroups=@('Red','Blue','FFA');contentVersion=[string]$maps.version;contentHash=$night.hash}) -Depth 5 | Set-Content "$run/maps.json" -Encoding utf8
$config=[pscustomobject]@{environmentId='realtest0930';releaseId='PreviewV1';apiBaseUrl='http://127.0.0.1:5180';hotUpdateBaseUrl='http://127.0.0.1:5180/hotupdate';inviteOnly=$false;networkMode='development'}
$config.apiBaseUrl='http://127.0.0.1:5180';$config.hotUpdateBaseUrl='http://127.0.0.1:5180/hotupdate'
$config | ConvertTo-Json -Depth 5 | Set-Content "$audit/Client/client-environment.json" -Encoding utf8
# Keep the already built player observation driver; never substitute editor assemblies.
$records=[Collections.Generic.List[object]]::new()
function Record($p,$role,$exe){
 $records.Add(@{role=$role;pid=$p.Id;exe=$exe;startedUtc=$p.StartTime.ToUniversalTime().ToString('o')})
 $records | ConvertTo-Json -Depth 5 | Set-Content "$run/processes.json" -Encoding utf8
}
$key=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
$envs=@{
 ASPNETCORE_ENVIRONMENT='Development';ASPNETCORE_URLS='http://127.0.0.1:5180';
 PrivateTest__Address='';PrivateTest__Port='5181';PrivateTest__InternalPort='5180';
 Database__AllowInMemoryFallback='true';Database__InMemoryName=('visualfix-'+$RunName);
 ConnectionStrings__GameDb='';Access__InviteOnly='false';Access__AdmissionsPauseFile='';
 Jwt__SigningKey=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N'));
 ServerInstances__ServerKey=$key;FPS_SERVER_KEY=$key;FPS_MAP_CATALOG="$run/maps.json";
 FPS_RELEASE_ID='PreviewV1';FPS_SERVER_BIND='127.0.0.1';FPS_MAP_CONTENT_HASH=$(if($MapId -eq 'map_05'){$night.hash}elseif($MapId -eq 'map_04'){$training.hash}else{''})
}
$old=@{}
try{
 foreach($k in $envs.Keys){$old[$k]=[Environment]::GetEnvironmentVariable($k,'Process');[Environment]::SetEnvironmentVariable($k,$envs[$k],'Process')}
 $api="$audit/Api/UnityFps.Api.exe"
 $p=Start-Process -FilePath $api -WorkingDirectory "$audit/Api" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$run/api.out.log" -RedirectStandardError "$run/api.err.log"
 Record $p 'api' $api
 $ready=$false
 for($i=0;$i -lt 30;$i++){
  if($p.HasExited){throw 'Test API exited.'}
  try{$h=Invoke-RestMethod 'http://127.0.0.1:5180/health' -TimeoutSec 1;if($h.status -eq 'ok'){$ready=$true;break}}catch{}
  if($p.HasExited){throw 'Test API exited.'};Start-Sleep -Seconds 1
 }
 if(-not $ready){throw 'Test API startup timeout.'}
 $ds="$audit/Server/UnityFpsDedicatedServer.exe"
 $p=Start-Process -FilePath $ds -WorkingDirectory "$audit/Server" -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-nographics','-dedicatedServer','-instanceId',('visualfix-'+$RunName),'-port','27770','-backendUrl','http://127.0.0.1:5180','-publicAddress','127.0.0.1','-capacity','8','-mapId',$MapId,'-buildVersion','PreviewV1','-runtimeAuditDir',('"'+$run+'"'),'-runtimeAuditRole','server','-testRunId',('visualfix-'+$RunName),'-evidenceDir',('"'+$run+'/server-telemetry"'),'-logFile',('"'+$run+'/server.log"'))
 Record $p 'server' $ds
 $registered=$false
 for($i=0;$i -lt 35;$i++){
  if((Test-Path "$run/server.log") -and (Select-String -LiteralPath "$run/server.log" -Pattern '[ServerRegistry] REGISTERED' -SimpleMatch -Quiet)){$registered=$true;break}
  if($p.HasExited){throw 'Test server exited.'};Start-Sleep -Seconds 1
 }
 if(-not $registered){throw 'Test server registration timeout.'}
 $password='R2-'+[Guid]::NewGuid().ToString('N')+'!'
 foreach($role in @('alpha','bravo')){
  $taskUsername='real0930'+$role+[DateTime]::UtcNow.ToString('HHmmss')
  $registration=Invoke-RestMethod 'http://127.0.0.1:5180/api/auth/register' -Method Post -ContentType 'application/json' -Body (@{username=$taskUsername;password=$password}|ConvertTo-Json)
  $taskHeaders=@{Authorization='Bearer '+$registration.token}
  foreach($itemId in @('throwable.frag_02','throwable.frag_03','attach.pistol.magazine')){
   $null=Invoke-RestMethod 'http://127.0.0.1:5180/api/shop/purchases' -Method Post -Headers $taskHeaders -ContentType 'application/json' -Body (@{itemId=$itemId;quantity=1;idempotencyKey=('real0930-'+$itemId)}|ConvertTo-Json)
  }
  $sets=@(@('throwable.frag','throwable.flash','throwable.smoke'),@('throwable.frag_02','throwable.frag_02',$null),@($null,'throwable.smoke','throwable.frag_03'))
  for($bag=1;$bag -le 3;$bag++){
   $previous=Invoke-RestMethod ('http://127.0.0.1:5180/api/loadout?backpack='+$bag) -Headers $taskHeaders
   $newLoadout=@{primaryWeaponId=$(if($bag -eq 2){'weapon.ak'}else{'weapon.m4'});secondaryWeaponId='weapon.service_pistol';throwableIds=$sets[$bag-1];expectedVersion=$previous.version}
   $null=Invoke-RestMethod ('http://127.0.0.1:5180/api/loadout?backpack='+$bag) -Method Put -Headers $taskHeaders -ContentType 'application/json' -Body ($newLoadout|ConvertTo-Json -Depth 5)
  }
  @{seq=1;fps=60;op="screenshot";name="boot-first-frame";caseId="boot"}|ConvertTo-Json -Compress|Set-Content "$run/$role.command.json" -Encoding utf8
  $exe="$audit/Client/UnityFpsClient.exe";$action=if($role -eq 'alpha'){'create'}else{'join'}
  $p=Start-Process -FilePath $exe -WorkingDirectory "$audit/Client" -WindowStyle Hidden -PassThru -ArgumentList @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-runtimeAuditDir',('"'+$run+'"'),'-runtimeAuditRole',$role,'-runtimeAuditProxyPort',$(if($NetworkProxy){if($role -eq 'alpha'){'27780'}else{'27781'}}else{'0'}),'-itUser',$taskUsername,'-itPass',$password,'-itAction',$action,'-itRoomFile',('"'+$run+'/room-code.txt"'),'-itMap',$MapId,'-itMax','2','-itWaitPlayers','2','-itMode',$Mode,'-itKill',$(if($Mode -eq 'TDM'){'50'}else{'20'}),'-itTime',$(if($Mode -eq 'TDM'){'15'}else{'10'}),'-testRunId',('visualfix-'+$RunName),'-evidenceDir',('"'+$run+'/'+$role+'-telemetry"'),'-logFile',('"'+$run+'/'+$role+'.log"'))
  Record $p $role $exe
 }
 Write-Output ('ROUND1_STARTED '+$run)
}finally{foreach($k in $old.Keys){[Environment]::SetEnvironmentVariable($k,$old[$k],'Process')}}


