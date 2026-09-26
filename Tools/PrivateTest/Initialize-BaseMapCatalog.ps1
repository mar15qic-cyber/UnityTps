param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Private.Common.ps1"
$c=Read-PrivateConfig $ConfigPath
$hot=Join-Path $c.releaseRoot 'Api/hotupdate'
if(-not(Test-Path -LiteralPath (Join-Path $hot 'maps-manifest.json'))){throw 'Host release is incomplete. Select a complete release from Builds/PrivateInvitations using the host configuration window, not a client EXE package.'}
$manifest=Get-Content (Join-Path $hot 'maps-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.releaseId -ne $c.releaseId -or [string]$manifest.version -notmatch '^[1-9][0-9]*$'){throw 'Map release identity invalid.'}
$catalog=Join-Path $c.stateRoot 'maps.json'
$maps=@()
if(Test-Path -LiteralPath $catalog){$maps=@(Get-Content $catalog -Raw -Encoding UTF8 | ConvertFrom-Json | ForEach-Object { $_ })}
$defaults=@(
 @{mapId='map_04';displayName='Training Yard';sceneName='Map_TrainingYard';modes=@('TDM');maxCapacity=8;spawnGroups=@('Red','Blue')},
 @{mapId='map_05';displayName='Night Relay';sceneName='Map_NightRelay';modes=@('KillRace');maxCapacity=16;spawnGroups=@('Red','Blue','FFA')})
$changed=$false
foreach($map in $defaults){
    # Explicitly published later map versions must not be overwritten by baseline startup.
    if(@($maps | Where-Object mapId -eq $map.mapId).Count){continue}
    $relative='maps/'+$map.sceneName.ToLowerInvariant()+'.bundle'
    $files=@($manifest.files | Where-Object path -eq $relative)
    if($files.Count -ne 1 -or $files[0].hash -notmatch '^[a-fA-F0-9]{64}$'){throw ('Map bundle identity missing: '+$map.mapId)}
    $bundle=Join-Path (Join-Path $hot $manifest.version) $relative
    if((Get-FileHash -LiteralPath $bundle).Hash -ne $files[0].hash){throw ('Map bundle corrupt: '+$map.mapId)}
    $map.contentVersion=[string]$manifest.version;$map.contentHash=$files[0].hash
    $maps += [pscustomobject]$map;$changed=$true
}
if($changed){
    New-Item -ItemType Directory -Path $c.stateRoot -Force | Out-Null
    ConvertTo-Json -InputObject @($maps) -Depth 8 | Set-Content ($catalog+'.tmp') -Encoding UTF8
    if(Test-Path $catalog){[IO.File]::Replace($catalog+'.tmp',$catalog,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($catalog+'.tmp',$catalog)}
}
Write-Output 'BASE_MAP_CATALOG_READY'
