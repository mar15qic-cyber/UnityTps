param(
    [Parameter(Mandatory)][string]$EnvironmentPath,
    [Parameter(Mandatory)][string]$HotUpdatePath,
    [string]$ClientPath = 'Builds/InvitationClient',
    [string]$ServerPath = 'Builds/Server'
)
$ErrorActionPreference='Stop'
$project = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$environment = Get-Content -LiteralPath $EnvironmentPath -Raw | ConvertFrom-Json
if ($environment.releaseId -notmatch '^[a-zA-Z0-9_-]{1,80}$' -or -not $environment.inviteOnly) { throw 'Invalid invitation environment.' }
foreach ($url in @($environment.apiBaseUrl,$environment.hotUpdateBaseUrl)) {
    $u=[Uri]$url
    if ($u.Scheme -ne 'https' -or $u.IsLoopback -or $u.Host -match 'REPLACE' -or $u.UserInfo) { throw 'Real public HTTPS endpoints required.' }
}
$client = [IO.Path]::GetFullPath((Join-Path $project $ClientPath))
$server = [IO.Path]::GetFullPath((Join-Path $project $ServerPath))
$cm = Get-Content -LiteralPath (Join-Path $client 'build-manifest.json') -Raw | ConvertFrom-Json
$sm = Get-Content -LiteralPath (Join-Path $server 'build-manifest.json') -Raw | ConvertFrom-Json
if ($cm.subtarget -ne 'InvitationPlayer' -or $cm.protocolId -ne $sm.protocolId -or $cm.inputDigest -ne $sm.inputDigest -or -not $cm.inputDigest) { throw 'Matching invitation client/server builds required.' }
$builtEnv=Get-Content -LiteralPath (Join-Path $client 'client-environment.json') -Raw | ConvertFrom-Json
foreach ($name in @('environmentId','releaseId','apiBaseUrl','hotUpdateBaseUrl','inviteOnly')) {
    if ($builtEnv.$name -ne $environment.$name) { throw 'Build environment differs from release environment.' }
}
foreach ($pair in @(@($client,$cm),@($server,$sm))) {
    $data=Get-ChildItem -LiteralPath $pair[0] -Directory -Filter '*_Data' | Select-Object -First 1
    if ((Get-FileHash -LiteralPath (Join-Path $data.FullName 'Managed/Game.Gameplay.dll')).Hash -ne $pair[1].gamePlayDllSha256) { throw 'Build DLL does not match manifest.' }
}
$hot=[IO.Path]::GetFullPath($HotUpdatePath)
$hm=Get-Content -LiteralPath (Join-Path $hot 'manifest.json') -Raw | ConvertFrom-Json
if ($hm.version -notmatch '^[0-9]+$') { throw 'Invalid hot-update version.' }
if (@($hm.files | Where-Object { $_.path -match '(?i)trainingyard.*bundle$' }).Count -eq 0) { throw 'TrainingYard bundle is required.' }
foreach ($f in $hm.files) {
    $hp=[IO.Path]::GetFullPath((Join-Path $hot ($hm.version+'/'+$f.path)))
    if (-not $hp.StartsWith($hot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Hot-update path escapes root.' }
    if ((Get-FileHash -LiteralPath $hp).Hash -ne $f.hash) { throw 'Hot-update content hash mismatch.' }
}
$dest=Join-Path $project ('Builds/Invitations/'+$environment.releaseId)
if (Test-Path -LiteralPath $dest) { throw 'Immutable release already exists; choose a new releaseId.' }
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item -LiteralPath $client -Destination (Join-Path $dest 'Client') -Recurse
Copy-Item -LiteralPath $server -Destination (Join-Path $dest 'Server') -Recurse
& dotnet publish (Join-Path $project 'fps-backend/src/UnityFps.Api/UnityFps.Api.csproj') -c Release -r win-x64 --self-contained false -o (Join-Path $dest 'Api')
if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
# Deployment config is environment-only; never ship developer connection strings.
foreach ($f in Get-ChildItem -LiteralPath (Join-Path $dest 'Api') -Filter 'appsettings*.json') {
    Set-Content -LiteralPath $f.FullName -Value '{"Database":{"AllowInMemoryFallback":false},"Access":{"InviteOnly":true}}' -Encoding UTF8
}
Copy-Item -LiteralPath $hot -Destination (Join-Path $dest 'Api/hotupdate') -Recurse
$hotManifest=Join-Path $dest 'Api/hotupdate/manifest.json'
$hm | Add-Member -NotePropertyName releaseId -NotePropertyValue $environment.releaseId -Force
$hm | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $hotManifest -Encoding UTF8
# Only the Client tree is player-distributable. Reject common accidental credential files.
$forbidden=Get-ChildItem -LiteralPath (Join-Path $dest 'Client') -Recurse -File | Where-Object { $_.Name -match '(?i)(secret|serverkey|appsettings|\.env$|\.pfx$|\.pem$)' }
if ($forbidden) { throw 'Client contains a forbidden credential/config file; release not sealed.' }
$launcher='@echo off'+"`r`n"+'cd /d "%~dp0"'+"`r`n"+'start "" "UnityFpsClient.exe" -publicTestTelemetry -testRunId '+$environment.releaseId+"`r`n"
Set-Content -LiteralPath (Join-Path $dest 'Client/Start-InvitationClient.cmd') -Value $launcher -Encoding ASCII
$files=@(Get-ChildItem -LiteralPath $dest -Recurse -File | ForEach-Object {
    [ordered]@{ path=$_.FullName.Substring($dest.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
})
@{ releaseId=$environment.releaseId; protocolId=$sm.protocolId; inputDigest=$sm.inputDigest; createdUtc=[DateTime]::UtcNow.ToString('o'); files=$files } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $dest 'release-manifest.json') -Encoding UTF8
Compress-Archive -LiteralPath (Join-Path $dest 'Client') -DestinationPath (Join-Path $dest 'InvitationClient.zip')
Write-Output ('RELEASE_SEALED '+$dest+'; distribute InvitationClient.zip only.')
