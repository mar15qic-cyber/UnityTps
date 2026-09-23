param([Parameter(Mandatory)][string]$ConfigPath, [switch]$AllowActive)
. "$PSScriptRoot/Cloud.Common.ps1"
$c = Read-CloudConfig $ConfigPath
$secret = Read-CloudSecrets $c
$healthy = $false
$states=if ($AllowActive) { @('Ready','Reserved','InMatch') } else { @('Ready') }
$release=Get-Content -LiteralPath (Join-Path $c.releaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json
for ($i=0; $i -lt 30; $i++) {
    try {
        $pool = Invoke-RestMethod ($c.apiListenUrl+'/api/server-instances/pool?requestedCapacity='+$c.capacityPerMap) -Headers @{'X-Server-Key'=$secret.serverKey} -TimeoutSec 3
        $missing = @()
        foreach ($m in (Get-CloudMaps $c)) {
            $found = @($pool.instances | Where-Object { $_.instanceId -eq $m.instance -and $_.mapId -eq $m.id -and $_.buildVersion -eq $c.releaseId -and $_.state -in $states -and $_.protocolId -eq $release.protocolId -and $_.address -eq $c.publicAddress -and $_.port -eq $m.port -and $_.fresh -and $_.heartbeatAgeSeconds -lt 30 })
            if ($found.Count -ne 1) { $missing += $m.id }
        }
        if ($missing.Count -eq 0) { $healthy=$true; break }
    } catch { }
    Start-Sleep -Seconds 1
}
if (-not $healthy) { throw 'MAP_READINESS_FAILED (requires all five fresh Ready instances; this is not a public UDP test).' }
Write-Output 'FIVE_MAPS_READY; external HTTPS/UDP/player tests still required.'
