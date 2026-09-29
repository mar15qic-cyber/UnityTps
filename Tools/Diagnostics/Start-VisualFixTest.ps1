param([Parameter(Mandatory=$true)][ValidatePattern('^[a-zA-Z0-9-]+$')][string]$RunName, [switch]$UseBuiltAudit,
 [ValidatePattern('^-r[0-9]+$')][string]$ReleaseSuffix='-r2')
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$release=Join-Path $project ('Builds/Distributions/PreviewV1/PreviewV1-Host-Win64'+$ReleaseSuffix)
$audit=Join-Path $project 'Builds/PreviewV1-r2-Audit'
$run=Join-Path $project ('Logs/VisualFix0928/'+$RunName)
if(Test-Path -LiteralPath $run){throw 'Evidence directory already exists; use a new RunName.'}
foreach($port in @(5180,5181)){if(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue){throw 'Test TCP port occupied.'}}
if(Get-NetUDPEndpoint -LocalPort 27770 -ErrorAction SilentlyContinue){throw 'Test UDP port occupied.'}
New-Item -ItemType Directory -Path $run -Force | Out-Null
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
 $init | ConvertTo-Json -Depth 12 -Compress | Set-Content "$data/RuntimeInitializeOnLoads.json" -Encoding utf8
 foreach($dll in @('Game.Gameplay.dll','Game.Presentation.dll','Game.UI.dll','Game.Core.dll','Game.Account.dll')){
  $a=(Get-FileHash "$release/$role/${app}_Data/Managed/$dll").Hash
  $b=(Get-FileHash "$audit/$role/${app}_Data/Managed/$dll").Hash
  if($a -ne $b){throw ('Gameplay assembly differs from release: '+$role+'/'+$dll)}
 }
 $load=Get-Content "$data/ScriptingAssemblies.json" -Raw | ConvertFrom-Json
 if($load.names -notcontains 'Game.RuntimeAudit.dll'){
  Copy-Item "$data/ScriptingAssemblies.json" "$run/$role-ScriptingAssemblies.original.json"
  $load.names+= 'Game.RuntimeAudit.dll';$load.types+=16
  $load | ConvertTo-Json -Depth 5 -Compress | Set-Content "$data/ScriptingAssemblies.json" -Encoding utf8
 }
 $identities += @{role=$role;inputDigest=$published.inputDigest;buildId=$published.buildId;gameAssembliesIdentical=$true}
}
}
if($UseBuiltAudit){
 foreach($role in @('Server','Client')){
  $app=if($role -eq 'Server'){'UnityFpsDedicatedServer'}else{'UnityFpsClient'}
  $m=Get-Content "$audit/$role/build-manifest.json" -Raw | ConvertFrom-Json
  $identities+=@{role=$role;buildId=$m.buildId;inputDigest=$m.inputDigest;protocol=$m.protocolId;instrumentedBuild=$true;presentationSha=(Get-FileHash "$audit/$role/${app}_Data/Managed/Game.Presentation.dll").Hash}
 }
 if($identities[0].inputDigest -ne $identities[1].inputDigest){throw 'Built audit pair input mismatch.'}
}
$identities | ConvertTo-Json -Depth 5 | Set-Content "$run/identity.json" -Encoding utf8
if(-not (Test-Path "$audit/Api")){ Copy-Item "$release/Api" "$audit/Api" -Recurse }
$maps=Get-Content "$release/Api/hotupdate/maps-manifest.json" -Raw | ConvertFrom-Json
$night=@($maps.files | Where-Object path -eq 'maps/map_nightrelay.bundle')[0]
@(@{mapId='map_05';displayName='Night Relay';sceneName='Map_NightRelay';modes=@('KillRace');maxCapacity=8;spawnGroups=@('Red','Blue','FFA');contentVersion=[string]$maps.version;contentHash=$night.hash}) | ConvertTo-Json -Depth 5 -AsArray | Set-Content "$run/maps.json" -Encoding utf8
$config=Get-Content "$audit/Client/client-environment.json" -Raw | ConvertFrom-Json
$config.apiBaseUrl='http://10.16.41.231:5181';$config.hotUpdateBaseUrl='http://10.16.41.231:5181/hotupdate'
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
 PrivateTest__Address='10.16.41.231';PrivateTest__Port='5181';PrivateTest__InternalPort='5180';
 Database__AllowInMemoryFallback='true';Database__InMemoryName=('visualfix-'+$RunName);
 ConnectionStrings__GameDb='';Access__InviteOnly='false';Access__AdmissionsPauseFile='';
 Jwt__SigningKey=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N'));
 ServerInstances__ServerKey=$key;FPS_SERVER_KEY=$key;FPS_MAP_CATALOG="$run/maps.json";
 FPS_RELEASE_ID='PreviewV1';FPS_SERVER_BIND='10.16.41.231';FPS_MAP_CONTENT_HASH=$night.hash
}
$old=@{}
try{
 foreach($k in $envs.Keys){$old[$k]=[Environment]::GetEnvironmentVariable($k,'Process');[Environment]::SetEnvironmentVariable($k,$envs[$k],'Process')}
 $api="$audit/Api/UnityFps.Api.exe"
 $p=Start-Process -FilePath $api -WorkingDirectory "$audit/Api" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$run/api.out.log" -RedirectStandardError "$run/api.err.log"
 Record $p 'api' $api
 $ready=$false
 for($i=0;$i -lt 30;$i++){
  try{$h=Invoke-RestMethod 'http://127.0.0.1:5180/health' -TimeoutSec 1;if($h.status -eq 'ok'){$ready=$true;break}}catch{}
  if($p.HasExited){throw 'Test API exited.'};Start-Sleep -Seconds 1
 }
 if(-not $ready){throw 'Test API startup timeout.'}
 $ds="$audit/Server/UnityFpsDedicatedServer.exe"
 $p=Start-Process -FilePath $ds -WorkingDirectory "$audit/Server" -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-nographics','-dedicatedServer','-instanceId',('visualfix-'+$RunName),'-port','27770','-backendUrl','http://127.0.0.1:5180','-publicAddress','10.16.41.231','-capacity','8','-mapId','map_05','-buildVersion','PreviewV1','-runtimeAuditDir',('"'+$run+'"'),'-runtimeAuditRole','server','-testRunId',('visualfix-'+$RunName),'-evidenceDir',('"'+$run+'/server-telemetry"'),'-logFile',('"'+$run+'/server.log"'))
 Record $p 'server' $ds
 $registered=$false
 for($i=0;$i -lt 35;$i++){
  if((Test-Path "$run/server.log") -and (Select-String -LiteralPath "$run/server.log" -Pattern '[ServerRegistry] REGISTERED' -SimpleMatch -Quiet)){$registered=$true;break}
  if($p.HasExited){throw 'Test server exited.'};Start-Sleep -Seconds 1
 }
 if(-not $registered){throw 'Test server registration timeout.'}
 $password='R2-'+[Guid]::NewGuid().ToString('N')+'!'
 foreach($role in @('alpha','bravo')){
  $exe="$audit/Client/UnityFpsClient.exe";$action=if($role -eq 'alpha'){'create'}else{'join'}
  $p=Start-Process -FilePath $exe -WorkingDirectory "$audit/Client" -WindowStyle Hidden -PassThru -ArgumentList @('-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-runtimeAuditDir',('"'+$run+'"'),'-runtimeAuditRole',$role,'-itUser',('r2'+$role+[DateTime]::UtcNow.ToString('HHmmss')),'-itPass',$password,'-itAction',$action,'-itRoomFile',('"'+$run+'/room-code.txt"'),'-itMap','map_05','-itMax','2','-itWaitPlayers','2','-itMode','KillRace','-itKill','20','-itTime','10','-testRunId',('visualfix-'+$RunName),'-evidenceDir',('"'+$run+'/'+$role+'-telemetry"'),'-logFile',('"'+$run+'/'+$role+'.log"'))
  Record $p $role $exe
 }
 Write-Output ('ROUND1_STARTED '+$run)
}finally{foreach($k in $old.Keys){[Environment]::SetEnvironmentVariable($k,$old[$k],'Process')}}


