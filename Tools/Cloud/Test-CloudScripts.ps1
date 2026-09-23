$ErrorActionPreference='Stop'
$root=Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) ('Temp/CloudScriptTests/'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'release/Api'),(Join-Path $root 'release/Server') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root 'release/Api/UnityFps.Api.exe') -Value 'fixture-not-executable'
Set-Content -LiteralPath (Join-Path $root 'release/Server/UnityFpsDedicatedServer.exe') -Value 'fixture-not-executable'
Set-Content -LiteralPath (Join-Path $root 'release/Server/build-manifest.json') -Value '{"protocolId":"fixture-v8"}'
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'release') -Recurse -File | ForEach-Object {
    @{path=$_.FullName.Substring((Join-Path $root 'release').Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
@{releaseId='fixture';protocolId='fixture-v8';files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'release/release-manifest.json')
# Random disposable fixtures, no real credentials or database access.
@{jwtSigningKey=[Guid]::NewGuid().ToString('N');serverKey=[Guid]::NewGuid().ToString('N');gameDb='unused'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'secrets.json')
$config=@{releaseId='fixture';releaseRoot=(Join-Path $root 'release');stateRoot=(Join-Path $root 'state');secretsFile=(Join-Path $root 'secrets.json');apiListenUrl='http://127.0.0.1:53980';dsBindAddress='0.0.0.0';publicAddress='203.0.113.20';baseUdpPort=53981;capacityPerMap=8}
$path=Join-Path $root 'config.json'; $config | ConvertTo-Json | Set-Content -LiteralPath $path
& powershell.exe -NoProfile -File "$PSScriptRoot/Start-CloudServer.ps1" -ConfigPath $path -CheckOnly
if ($LASTEXITCODE -ne 0) { throw 'Valid fixture preflight failed.' }
if (Test-Path -LiteralPath $config.stateRoot) { throw 'CheckOnly mutated runtime state.' }
$config.publicAddress='127.0.0.1'; $config | ConvertTo-Json | Set-Content -LiteralPath $path
$ErrorActionPreference='Continue'
$out=& powershell.exe -NoProfile -File "$PSScriptRoot/Start-CloudServer.ps1" -ConfigPath $path -CheckOnly 2>&1
$ErrorActionPreference='Stop'
if ($LASTEXITCODE -eq 0) { throw 'Loopback advertisement was accepted.' }
Write-Output 'CLOUD_SCRIPT_TESTS_PASSED: nonmutating preflight and loopback rejection.'
