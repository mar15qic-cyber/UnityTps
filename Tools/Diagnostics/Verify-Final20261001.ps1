$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=Join-Path $project 'Builds/Distributions/PreviewV1/PreviewV1-Host-Win64-Final20261001'
$results=@()
foreach($role in @('Client','Server')){
 $source=Join-Path $project $(if($role -eq 'Client'){'Builds/ReleaseClient'}else{'Builds/Server'})
 $app=if($role -eq 'Client'){'UnityFpsClient'}else{'UnityFpsDedicatedServer'}
 $target=Join-Path $root $role
 $data=Join-Path $source ($app+'_Data')
 $files=@(Get-ChildItem -LiteralPath $data -Recurse -File)
 foreach($name in @(($app+'.exe'),'UnityPlayer.dll','UnityCrashHandler64.exe','FishNet.SDK.Id')){
  $path=Join-Path $source $name
  if(Test-Path -LiteralPath $path){$files += Get-Item -LiteralPath $path}
 }
 $checked=0
 foreach($file in $files){
  $relative=$file.FullName.Substring($source.Length+1)
  $copy=Join-Path $target $relative
  $before=(Get-FileHash -LiteralPath $file.FullName).Hash
  $after=(Get-FileHash -LiteralPath $copy).Hash
  if($before -ne $after){
   # The packaging tool removes only stale names for an excluded audit assembly.
   if($file.Name -ne 'ScriptingAssemblies.json'){throw ('Installed game bytes changed: '+$role+'/'+$relative)}
   $old=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
   $new=Get-Content -LiteralPath $copy -Raw | ConvertFrom-Json
   $keep=@(0..($old.names.Count-1) | Where-Object {$old.names[$_] -ne 'Game.RuntimeAudit.dll'})
   $wantedNames=@($keep | ForEach-Object {$old.names[$_]})
   $wantedTypes=@($keep | ForEach-Object {$old.types[$_]})
   if(($wantedNames -join '|') -ne ($new.names -join '|') -or ($wantedTypes -join '|') -ne ($new.types -join '|')){throw 'Unexpected assembly loader change.'}
  }
  $checked++
 }
 $sourceManifest=Get-Content (Join-Path $source 'build-manifest.json') -Raw | ConvertFrom-Json
 $sealedManifest=Get-Content (Join-Path $target 'build-manifest.json') -Raw | ConvertFrom-Json
 foreach($field in @('buildId','protocolId','builtAtUtc','unityVersion','subtarget','gamePlayDllSha256','gameUiDllSha256','gameAccountDllSha256','inputDigest','inputFileCount')){
  if($sourceManifest.$field -ne $sealedManifest.$field){throw ('Build identity changed: '+$field)}
 }
 $results += [pscustomobject]@{role=$role;gameFilesChecked=$checked;installedGameBytesPreserved=$true;originalBuildIdentityPreserved=$true;buildId=$sourceManifest.buildId}
}
$loader=Get-Content "$root/Client/UnityFpsClient_Data/ScriptingAssemblies.json" -Raw | ConvertFrom-Json
if($loader.names -contains 'Game.RuntimeAudit.dll'){throw 'Audit assembly loader found.'}
$env=Get-Content "$root/Client/client-environment.json" -Raw | ConvertFrom-Json
if($env.releaseId -ne 'PreviewV1' -or $env.networkMode -ne 'private-overlay' -or $env.inviteOnly -ne $false -or $env.apiBaseUrl -ne 'http://10.16.41.231:5081'){throw 'Unexpected friends connection configuration.'}
$result=[ordered]@{checkedUtc=[DateTime]::UtcNow.ToString('o');verification='static installed-to-final game file comparison only';isFinal=$true;packageRevision='Final20261001';roles=$results;privateOverlayConfigurationVerified=$true;runtimeAuditAbsent=$true;noGameOrServerLaunched=$true}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $project 'Logs/Final20261001/installed-to-final-verification.json') -Encoding utf8
$result | ConvertTo-Json -Depth 5
