param([Parameter(Mandatory)][string]$ConfigPath, [switch]$CheckOnly)
. "$PSScriptRoot/Cloud.Common.ps1"
$c = Read-CloudConfig $ConfigPath
$secret = Read-CloudSecrets $c
$root = [IO.Path]::GetFullPath($c.releaseRoot)
$api = Join-Path $root 'Api/UnityFps.Api.exe'
$ds = Join-Path $root 'Server/UnityFpsDedicatedServer.exe'
$release = Get-Content -LiteralPath (Join-Path $root 'release-manifest.json') -Raw | ConvertFrom-Json
if ($release.releaseId -ne $c.releaseId) { throw 'Release identity mismatch.' }
foreach ($f in $release.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $f.path))
    if (-not $path.StartsWith($root.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes release.' }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $f.sha256) { throw ('Release hash mismatch: '+$f.path) }
}
foreach ($f in @($api,$ds)) { if (-not (Test-Path -LiteralPath $f)) { throw 'Release executable missing.' } }
$manifest = Get-Content -LiteralPath (Join-Path $root 'Server/build-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.protocolId -ne $release.protocolId) { throw 'Server protocol differs from release.' }
$stateFile = Join-Path $c.stateRoot 'processes.json'
$records = @()
if (Test-Path -LiteralPath $stateFile) { $records = @(Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json) }
foreach ($r in $records) {
    if ((Test-ManagedProcess $r) -and $r.releaseId -ne $c.releaseId) { throw 'Old release still running; drain and stop it first.' }
}
# A healthy response from someone else's API or UDP process must never count as our deployment.
$ports = @([pscustomobject]@{ name='api'; port=([Uri]$c.apiListenUrl).Port; tcp=$true })
if ($c.PSObject.Properties.Name -contains 'privateOverlay' -and $c.privateOverlay) {
    $ports += [pscustomobject]@{name='api';port=$c.playerTcpPort;tcp=$true}
}
foreach ($map in (Get-CloudMaps $c)) { $ports += [pscustomobject]@{name=$map.id;port=$map.port;tcp=$false} }
foreach ($entry in $ports) {
    $listeners = if ($entry.tcp) { @(Get-NetTCPConnection -LocalPort $entry.port -State Listen -ErrorAction SilentlyContinue) }
        else { @(Get-NetUDPEndpoint -LocalPort $entry.port -ErrorAction SilentlyContinue) }
    foreach ($listener in $listeners) {
        $owned=@($records | Where-Object { $_.name -eq $entry.name -and $_.pid -eq $listener.OwningProcess -and (Test-ManagedProcess $_) })
        if ($owned.Count -eq 0) { throw ('Port owned by unmanaged process: '+$entry.port) }
    }
}
if ($CheckOnly) { Write-Output 'CLOUD_PREFLIGHT_OK (no changes; connectivity not tested)'; exit 0 }
New-Item -ItemType Directory -Path $c.stateRoot -Force | Out-Null
$logs = Join-Path $c.stateRoot ('logs/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$envValues = @{
    'ASPNETCORE_ENVIRONMENT'='Production'; 'ASPNETCORE_URLS'=$c.apiListenUrl;
    'Jwt__SigningKey'=$secret.jwtSigningKey; 'ServerInstances__ServerKey'=$secret.serverKey;
    'ConnectionStrings__GameDb'=$secret.gameDb; 'Access__InviteOnly'='true';
    'Access__AdmissionsPauseFile'=(Join-Path $c.stateRoot 'admissions.paused');
    'FPS_SERVER_KEY'=$secret.serverKey; 'FPS_SERVER_BIND'=$c.dsBindAddress;
    'FPS_RELEASE_ID'=$c.releaseId
}
$previous = @{}
if ($c.PSObject.Properties.Name -contains 'privateOverlay' -and $c.privateOverlay) {
    $envValues['PrivateTest__Address']=$c.publicAddress
    $envValues['PrivateTest__Port']=[string]$c.playerTcpPort
    $envValues['PrivateTest__InternalPort']=[string]([Uri]$c.apiListenUrl).Port
    $envValues['FPS_MAP_CATALOG']=(Join-Path $c.stateRoot 'maps.json')
}
foreach ($k in $envValues.Keys) { $previous[$k]=[Environment]::GetEnvironmentVariable($k,'Process'); [Environment]::SetEnvironmentVariable($k,$envValues[$k],'Process') }
function Start-Managed([string]$Name,[string]$Exe,[string[]]$Arguments,[string]$WorkingDirectory) {
    $existing = @($script:records | Where-Object { $_.name -eq $Name -and (Test-ManagedProcess $_) })
    if ($existing.Count -gt 0) { return }
    $script:records = @($script:records | Where-Object { $_.name -ne $Name })
    $params = @{FilePath=$Exe; WorkingDirectory=$WorkingDirectory; WindowStyle='Hidden'; PassThru=$true;
        RedirectStandardOutput=(Join-Path $logs ($Name+'.out.log')); RedirectStandardError=(Join-Path $logs ($Name+'.err.log'))}
    if ($Arguments.Count -gt 0) { $params.ArgumentList=$Arguments }
    $p = Start-Process @params
    $script:records += [pscustomobject]@{ name=$Name; pid=$p.Id; exe=$Exe; startedUtc=$p.StartTime.ToUniversalTime().ToString('o'); releaseId=$c.releaseId; logDirectory=$logs }
    $script:records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $stateFile -Encoding UTF8
}
try {
    Start-Managed 'api' $api @() (Split-Path -Parent $api)
    $ready=$false
    for ($i=0; $i -lt 30; $i++) {
        try { $health=Invoke-RestMethod ($c.apiListenUrl+'/health') -TimeoutSec 2; if ($health.status -eq 'ok') { $ready=$true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'API not healthy; inspect private server logs.' }
    foreach ($map in (Get-CloudMaps $c)) {
        Start-Managed $map.id $ds @('-batchmode','-nographics','-dedicatedServer','-instanceId',$map.instance,
            '-port',"$($map.port)",'-publicAddress',$c.publicAddress,'-backendUrl',$c.apiListenUrl,
            '-mapId',$map.id,'-capacity',"$($c.capacityPerMap)",'-buildVersion',$c.releaseId,
            '-logFile',('"'+(Join-Path $logs ($map.id+'.unity.log'))+'"'),'-publicTestTelemetry','-testRunId',$c.releaseId) (Split-Path -Parent $ds)
    }
    & "$PSScriptRoot/Test-CloudReadiness.ps1" -ConfigPath $ConfigPath -AllowActive
    if (-not $?) { throw 'Cloud readiness failed.' }
} finally {
    foreach ($k in $previous.Keys) { [Environment]::SetEnvironmentVariable($k,$previous[$k],'Process') }
}
