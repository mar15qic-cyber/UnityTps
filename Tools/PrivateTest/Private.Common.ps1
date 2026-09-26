Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../Cloud/Cloud.Common.ps1"
function Read-PrivateConfig([string]$Path) {
    $c=Read-CloudConfig $Path
    if (-not $c.privateOverlay -or $c.networkId -notmatch '^[a-fA-F0-9]{16}$') { throw 'Invalid private network configuration.' }
    $ip=$null
    if (-not [Net.IPAddress]::TryParse($c.publicAddress,[ref]$ip) -or $ip.AddressFamily -ne 'InterNetwork') { throw 'Private IPv4 required.' }
    $b=$ip.GetAddressBytes()
    if (-not (($b[0] -eq 10) -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168))) { throw 'Private IPv4 required.' }
    if ($c.dsBindAddress -ne $c.publicAddress -or $c.playerTcpPort -eq ([uri]$c.apiListenUrl).Port) { throw 'Listeners are not isolated.' }
    return $c
}
function Get-PrivatePool($c) {
    $s=Read-CloudSecrets $c
    Invoke-RestMethod ($c.apiListenUrl+'/api/server-instances/pool?requestedCapacity=2') -Headers @{'X-Server-Key'=$s.serverKey} -TimeoutSec 3
}
function Get-PrivateBusyInstances($Pool, $Records) {
    # Historical tests and other hosts share the registry. Only this host's
    # recorded DS processes participate in its drain decision. Keep stale owned
    # busy records blocking: lost heartbeats are not proof that a match ended.
    $ids=@($Records | Where-Object { $_.name -ne 'api' } | ForEach-Object { 'invite-'+$_.name })
    @($Pool.instances | Where-Object {
        $_.instanceId -in $ids -and ($_.currentPlayers -gt 0 -or $_.state -in @('InMatch','Reserved','Draining'))
    })
}
