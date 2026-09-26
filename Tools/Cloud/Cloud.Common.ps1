Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Read-CloudConfig([string]$Path) {
    $c = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $u = [Uri]$c.apiListenUrl
    if (-not $u.IsLoopback -or $u.Scheme -ne 'http') { throw 'API listener must be loopback HTTP behind IIS.' }
    if ($c.publicAddress -match 'REPLACE|localhost|^127\.|^0\.' -or $c.publicAddress -match '[\s"\x27]') { throw 'Public address required.' }
    if ($c.releaseId -notmatch '^[a-zA-Z0-9_-]{1,80}$') { throw 'Invalid release id.' }
    if ($c.baseUdpPort -lt 1024 -or $c.baseUdpPort -gt 65530) { throw 'Invalid UDP range.' }
    if ($c.capacityPerMap -lt 2 -or $c.capacityPerMap -gt 8) { throw 'Invitation capacity must be 2..8 per map.' }
    foreach ($p in @($c.releaseRoot, $c.stateRoot, $c.secretsFile)) {
        if (-not [IO.Path]::IsPathRooted($p) -or $p.Contains('"')) { throw 'Absolute paths without quotes required.' }
    }
    return $c
}
function Read-CloudSecrets($Config) {
    $s = Get-Content -LiteralPath $Config.secretsFile -Raw | ConvertFrom-Json
    foreach ($v in @($s.jwtSigningKey, $s.serverKey)) {
        if ([Text.Encoding]::UTF8.GetByteCount($v) -lt 32 -or $v -match 'REPLACE|development|change-me') { throw 'Invalid production secrets.' }
    }
    if ($s.jwtSigningKey -eq $s.serverKey -or $s.gameDb -match 'REPLACE') { throw 'Distinct secrets and database credentials required.' }
    return $s
}
function Test-ManagedProcess($Record) {
    $p = Get-Process -Id $Record.pid -ErrorAction SilentlyContinue
    return $p -and -not $p.HasExited -and $p.Path -eq $Record.exe -and $p.StartTime.ToUniversalTime().ToString('o') -eq $Record.startedUtc
}
function Get-CloudMaps($Config) {
    $ids = @('arena','map_01','map_02','map_03','map_04','map_05')
    for ($i=0; $i -lt $ids.Count; $i++) { [pscustomobject]@{ id=$ids[$i]; port=($Config.baseUdpPort+$i); instance=('invite-'+$ids[$i]) } }
}
