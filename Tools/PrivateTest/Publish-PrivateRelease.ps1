param([Parameter(Mandatory)][string]$EnvironmentPath,[Parameter(Mandatory)][string]$HotUpdatePath)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$e=Get-Content $EnvironmentPath -Raw | ConvertFrom-Json
if ($e.networkMode -ne 'private-overlay' -or $e.inviteOnly -isnot [bool] -or $e.zeroTierNetworkId -notmatch '^[a-fA-F0-9]{16}$' -or $e.releaseId -notmatch '^[a-zA-Z0-9_-]{1,80}$') { throw 'Invalid private environment.' }
$client=Join-Path $project 'Builds/PrivateInvitationClient';$server=Join-Path $project 'Builds/PrivateInvitationServer'
$cm=Get-Content "$client/build-manifest.json" -Raw | ConvertFrom-Json;$sm=Get-Content "$server/build-manifest.json" -Raw | ConvertFrom-Json
if ($cm.subtarget -ne 'PrivateInvitationPlayer' -or $cm.protocolId -ne $sm.protocolId -or $cm.inputDigest -ne $sm.inputDigest) { throw 'Build pair mismatch.' }
$built=Get-Content "$client/client-environment.json" -Raw | ConvertFrom-Json
foreach ($key in @('environmentId','releaseId','networkMode','inviteOnly','zeroTierNetworkId','hostOverlayAddress','apiBaseUrl','hotUpdateBaseUrl')) { if ($e.$key -ne $built.$key) { throw 'Environment differs from built client.' } }
$dest=Join-Path $project ('Builds/PrivateInvitations/'+$e.releaseId)
if (Test-Path $dest) { throw 'Release exists; use a new release id.' }
New-Item -ItemType Directory -Path $dest | Out-Null
Copy-Item $client "$dest/Client" -Recurse;Copy-Item $server "$dest/Server" -Recurse
foreach($name in @('mscorlib.dll','System.Private.CoreLib.dll','StartGame.exe')){if(Test-Path -LiteralPath "$dest/Client/$name"){throw ('Unity source contains launcher runtime: '+$name)}}
& dotnet publish "$PSScriptRoot/Launcher/Launcher.csproj" -c Release -r win-x64 --self-contained true -o "$dest/Client/Launcher"
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed.' }
Copy-Item "$PSScriptRoot/../Cloud/Export-PlayerEvidence.ps1" "$dest/Client"
Copy-Item "$PSScriptRoot/玩家说明.txt" "$dest/Client"
@('@echo off','start "" "%~dp0Launcher\StartGame.exe"') | Set-Content "$dest/Client/开始游戏.cmd" -Encoding ASCII
& dotnet publish "$project/fps-backend/src/UnityFps.Api/UnityFps.Api.csproj" -c Release -r win-x64 --self-contained true -o "$dest/Api"
if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
$settings=@{Database=@{AllowInMemoryFallback=$false};Access=@{InviteOnly=[bool]$e.inviteOnly}} | ConvertTo-Json -Depth 4
Get-ChildItem "$dest/Api" -Filter 'appsettings*.json' | ForEach-Object { Set-Content $_.FullName $settings -Encoding UTF8 }
$hotTarget=[IO.Path]::GetFullPath((Join-Path $dest 'Api/hotupdate'))
if(-not $hotTarget.StartsWith([IO.Path]::GetFullPath($dest)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Generated hot-update directory escaped release.'}
# dotnet publish may copy the developer's old hotupdate directory; replace only this new release's copy.
if(Test-Path -LiteralPath $hotTarget){Remove-Item -LiteralPath $hotTarget -Recurse -Force}
Copy-Item -LiteralPath $HotUpdatePath -Destination $hotTarget -Recurse
$hm=Get-Content "$dest/Api/hotupdate/manifest.json" -Raw | ConvertFrom-Json
$hm | Add-Member -NotePropertyName releaseId -NotePropertyValue $e.releaseId -Force
$hm | ConvertTo-Json -Depth 12 | Set-Content "$dest/Api/hotupdate/manifest.json" -Encoding UTF8
$hm | Add-Member -NotePropertyName protocolId -NotePropertyValue $sm.protocolId -Force
$hm.files=@($hm.files | Where-Object path -like 'maps/*.bundle')
if (-not $hm.files.Count) { throw 'TrainingYard content missing.' }
foreach($file in $hm.files) {
 $path=[IO.Path]::GetFullPath((Join-Path "$dest/Api/hotupdate/$($hm.version)" $file.path))
 if(-not $path.StartsWith([IO.Path]::GetFullPath("$dest/Api/hotupdate")+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash $path).Hash -ne $file.hash){throw 'Content hash mismatch.'}
}
$hm | ConvertTo-Json -Depth 12 | Set-Content "$dest/Api/hotupdate/maps-manifest.json" -Encoding UTF8
$forbidden=Get-ChildItem "$dest/Client" -Recurse -File | Where-Object Name -match '(?i)(secret|serverkey|appsettings|\.env$|\.pfx$|\.pem$)'
if ($forbidden) { throw 'Client contains forbidden files.' }
$files=@(Get-ChildItem $dest -Recurse -File | Where-Object Name -ne 'maps-manifest.json' | ForEach-Object { @{path=$_.FullName.Substring($dest.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()} })
@{releaseId=$e.releaseId;protocolId=$sm.protocolId;inputDigest=$sm.inputDigest;files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$dest/release-manifest.json" -Encoding UTF8
Compress-Archive -Path "$dest/Client/*" -DestinationPath "$dest/InvitationClient.zip"
Write-Output ('PRIVATE_RELEASE_READY '+$dest)
