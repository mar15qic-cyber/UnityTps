param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Private.Common.ps1"
$c=Read-PrivateConfig $ConfigPath;$s=Read-CloudSecrets $c
$stateFile=Join-Path $c.stateRoot 'processes.json'
if(-not(Test-Path $stateFile)){return}
$records=@(Get-Content $stateFile -Raw | ConvertFrom-Json)
foreach($r in $records){
 if(-not($r.PSObject.Properties.Name -contains 'mapHash') -or (Test-ManagedProcess $r)){continue}
 if(Get-NetUDPEndpoint -LocalPort $r.mapPort -ErrorAction SilentlyContinue){throw 'Additional map UDP port occupied.'}
 $envs=@{FPS_SERVER_KEY=$s.serverKey;FPS_SERVER_BIND=$c.publicAddress;FPS_RELEASE_ID=$c.releaseId;FPS_MAP_ID=$r.name;FPS_MAP_SCENE=$r.mapScene;FPS_MAP_CONTENT_HASH=$r.mapHash};$previous=@{}
 try{
  foreach($key in $envs.Keys){$previous[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$envs[$key],'Process')}
  $r.logDirectory=Join-Path $c.stateRoot ('logs/'+$r.name+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
  New-Item -ItemType Directory $r.logDirectory -Force | Out-Null
  $p=Start-Process $r.exe -WorkingDirectory (Split-Path $r.exe) -WindowStyle Hidden -PassThru -ArgumentList @('-batchmode','-nographics','-dedicatedServer','-instanceId',('invite-'+$r.name),'-port',"$($r.mapPort)",'-publicAddress',$c.publicAddress,'-backendUrl',$c.apiListenUrl,'-mapId',$r.name,'-capacity','8','-buildVersion',$c.releaseId,'-publicTestTelemetry','-logFile',('"'+(Join-Path $r.logDirectory 'server.log')+'"'))
  $r.pid=$p.Id;$r.startedUtc=$p.StartTime.ToUniversalTime().ToString('o')
 }finally{foreach($key in $previous.Keys){[Environment]::SetEnvironmentVariable($key,$previous[$key],'Process')}}
}
$records | ConvertTo-Json -Depth 5 | Set-Content $stateFile -Encoding UTF8
