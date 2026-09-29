# Start-LocalServer.ps1 - local online stack launcher (backend control plane + dedicated server).
# Pure ASCII on purpose: PS 5.1 mis-reads non-BOM UTF-8 Chinese sources (project SOP).
# Chinese UX lives in the project-root .cmd entries that call this script.
#
# Guarantees (2026-09-10 dual-player audit item 2; F07/F08 rework 2026-09-19 audit):
#   - Six-phase structure: read state -> build plan -> CheckOnly exit -> execute -> verify -> merge-save.
#   - Idempotent: re-running never restarts a healthy backend or a running/registered DS
#     (main AND each map instance are discovered, identity-verified, then reused or failed loudly).
#   - -CheckOnly never mutates: no process start, no log deletion, no state-file writes,
#     and it returns BEFORE any start action (F08: previously -CheckOnly -AllMaps with the
#     main DS already running could start real map DS processes).
#   - Backend recovery happens BEFORE any DS work (F08: previously "backend dead + main DS
#     alive" printed BACKEND=start but exited before starting it, and still started map DS
#     instances against a dead backend).
#   - Map DS instances (F07): per-instance reuse by stable instanceId + pid/exe/log identity;
#     foreign port occupiers fail explicitly; MAP_DS_READY only means "process alive AND
#     registered (pool or log evidence)"; failures exit non-zero; healthy old records are
#     preserved (state files are merged, never blind-overwritten; running logs never deleted).
#   - Never kills by process name; only this tool's state.json/map-servers.json PIDs are
#     managed by the Stop script.
#   - Verifies control-plane key (pool endpoint) on every run, reused backend included.
#   - Key is read from the existing secret file; it is never printed.
param(
    [switch]$CheckOnly,
    [int]$BackendPort = 5080,
    [int]$DsPort = 7770,
    [string]$InstanceId = "local-dev-01",
    [int]$Capacity = 8,
    # Phase 8: map binding for the DS instance (arena | map_01 | map_02 | map_03).
    # The DS loads the scene resolved from GameMapCatalog and reports the id to the backend,
    # whose lease query only rents instances whose map matches the room's mapId.
    [string]$MapId = "arena",
    # Phase 8: additionally start one DS per combat map (map_01..04) so rooms created
    # with those maps can lease an instance. Each uses its own port/instanceId/log and is
    # recorded in .runtime/map-servers.json for Stop-LocalServer.ps1.
    [switch]$AllMaps
)

$ErrorActionPreference = "Continue"

$toolsDir    = $PSScriptRoot
$root        = Split-Path -Parent (Split-Path -Parent $toolsDir)
$backendCsproj = Join-Path $root "fps-backend\src\UnityFps.Api\UnityFps.Api.csproj"
$dsExe       = Join-Path $root "Builds\Server\UnityFpsDedicatedServer.exe"
$dsManaged   = Join-Path $root "Builds\Server\UnityFpsDedicatedServer_Data\Managed\Game.Gameplay.dll"
$runtimeDir  = Join-Path $toolsDir ".runtime"
$stateFile   = Join-Path $runtimeDir "state.json"
$mapStateFile = Join-Path $runtimeDir "map-servers.json"
$logRoot     = Join-Path $toolsDir "Logs"
# ServerKey: prefer the workspace-local copy (survives TEMP cleanup); fall back to the
# legacy Day1 IT TEMP path so the older smoke scripts keep working with the same value.
$secretFile      = Join-Path $runtimeDir "serverKey.txt"
$secretFallback  = Join-Path $env:TEMP "day1-it-secrets\serverKey.txt"
$healthUrl   = "http://127.0.0.1:$BackendPort/health"
$poolUrl     = "http://127.0.0.1:$BackendPort/api/server-instances/pool?requestedCapacity=$Capacity"

function Fail([string]$code, [string]$detail) {
    Write-Output "FAILED $code"
    if ($detail) { Write-Output $detail }
    exit 1
}

function Get-HttpCode([string]$url, [string]$serverKey, [string]$bodyOut) {
    $curlArgs = @('--noproxy', '*', '-s', '-o', $bodyOut, '-w', '%{http_code}', '--max-time', '4', $url)
    if ($serverKey) { $curlArgs += @('-H', "X-Server-Key: $serverKey") }
    $code = & curl.exe @curlArgs 2>$null
    if ($LASTEXITCODE -ne 0 -and -not $code) { return '000' }
    return ("$code").Trim()
}

function Get-ListeningPid([int]$port) {
    $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($conn) { return [int]$conn.OwningProcess }
    return 0
}

function Get-UdpOwnerPid([int]$port) {
    $ep = Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ep) { return [int]$ep.OwningProcess }
    return 0
}

function Get-PidLabel([int]$procId) {
    if ($procId -le 0) { return 'unknown' }
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($p) { return "$($p.ProcessName) pid=$($p.Id)" }
    return "pid=$procId(exited)"
}

function Test-PidRunsExe([int]$procId, [string]$expectedPath) {
    if ($procId -le 0) { return $false }
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if (-not $p) { return $false }
    if ($expectedPath -and $p.Path -and ($p.Path -eq $expectedPath)) { return $true }
    return $false
}

# Returns evidence that a DS instance is currently registered: backend pool membership
# (authoritative) or a REGISTERED line in its own log (fallback when pool is unreachable).
function Test-InstanceRegistered([string]$instanceId, [string]$logFile, [string]$bodyFile) {
    $poolCode = Get-HttpCode $poolUrl $key $bodyFile
    if ($poolCode -eq '200' -and (Test-Path $bodyFile)) {
        $body = Get-Content $bodyFile -Raw -ErrorAction SilentlyContinue
        if ($body -and $body -match [regex]::Escape("`"$instanceId`"")) { return $true }
    }
    if ($logFile -and (Test-Path $logFile)) {
        if (Select-String -Path $logFile -Pattern 'REGISTERED' -Quiet) { return $true }
    }
    return $false
}

# Phase 8 map instance defs (stable ids across runs -> same compensation directories in the DS).
function Get-MapDefs {
    @(
        @{ id = "$InstanceId-map01"; port = $DsPort + 1; mapId = 'map_01'; capacity = 8 },
        @{ id = "$InstanceId-map02"; port = $DsPort + 2; mapId = 'map_02'; capacity = 16 },
        @{ id = "$InstanceId-map03"; port = $DsPort + 3; mapId = 'map_03'; capacity = 8 },
        @{ id = "$InstanceId-map04"; port = $DsPort + 4; mapId = 'map_04'; capacity = 8 },
        @{ id = "$InstanceId-map05"; port = $DsPort + 5; mapId = 'map_05'; capacity = 16 }
    )
}

Write-Output "== UNITY FPS local server launcher =="
Write-Output "root=$root"

# ---- 1. prerequisites -------------------------------------------------------
if (-not (Test-Path $backendCsproj)) { Fail 'BACKEND_PROJECT_MISSING' $backendCsproj }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'DOTNET_NOT_FOUND' 'dotnet SDK is not on PATH' }
if (-not (Test-Path $secretFile)) {
    if (Test-Path $secretFallback) { $secretFile = $secretFallback }
}
if (-not (Test-Path $secretFile)) {
    Fail 'SERVERKEY_MISSING' "missing both $secretFile and $secretFallback - regenerate a 64-hex random key into the first path (Day1 IT SOP: per-run temporary credential, safe to regenerate while no backend is running)"
}
$key = (Get-Content $secretFile -Raw).Trim()
if (-not $key) { Fail 'SERVERKEY_EMPTY' $secretFile }
$dsOk = Test-Path $dsExe
if (-not $dsOk) { Fail 'DS_BUILD_MISSING' $dsExe }
$dsTime = (Get-Item $dsExe).LastWriteTime
$mgmtTime = ''
if (Test-Path $dsManaged) { $mgmtTime = ' managed(Game.Gameplay.dll)=' + (Get-Item $dsManaged).LastWriteTime }
Write-Output "DS_BUILD=$dsExe written=$dsTime$mgmtTime"

# ---- 1.5 build manifest: deployed-build identity gate (P0-A 2026-09-15) --------
$dsManifestPath = Join-Path (Split-Path -Parent $dsExe) 'build-manifest.json'
if (-not (Test-Path $dsManifestPath)) {
    Fail 'DS_MANIFEST_MISSING' "$dsManifestPath - rebuild via Tools/Dedicated Server/Build Windows Server (Release), which now writes the manifest."
}
$manifest = $null
try { $manifest = Get-Content $dsManifestPath -Raw | ConvertFrom-Json } catch { }
if ($null -eq $manifest -or -not $manifest.protocolId) {
    Fail 'DS_MANIFEST_INVALID' "$dsManifestPath could not be parsed (protocolId missing)"
}
if (Test-Path $dsManaged) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $onDiskHash = ($sha.ComputeHash([System.IO.File]::ReadAllBytes($dsManaged)) | ForEach-Object { $_.ToString('x2') }) -join ''
    $sha.Dispose()
    if ($onDiskHash -ne $manifest.gamePlayDllSha256) {
        Fail 'DS_BUILD_INCONSISTENT' "on-disk Game.Gameplay.dll does not match build-manifest.json (disk was modified after the build). Rebuild the server."
    }
}
Write-Output "DS_MANIFEST_OK protocol=$($manifest.protocolId) buildId=$($manifest.buildId) builtAt=$($manifest.builtAtUtc)"

# ---- 2. read state + build plan (NO side effects in this phase) --------------
$prevState = $null
if (Test-Path $stateFile) { try { $prevState = Get-Content $stateFile -Raw | ConvertFrom-Json } catch { } }
$prevMapState = @()
if (Test-Path $mapStateFile) {
    try {
        $parsed = Get-Content $mapStateFile -Raw | ConvertFrom-Json
        # PS 5.1: ConvertFrom-Json already yields Object[] for JSON arrays - @(...) would
        # nest it ([array-of-array]) and break per-record field access below.
        if ($parsed -is [array]) { $prevMapState = $parsed } else { $prevMapState = @($parsed) }
    } catch { $prevMapState = @() }
}

# 2.1 backend plan
$healthTmp = Join-Path $env:TEMP ("usp-health-" + [guid]::NewGuid().ToString('N') + ".tmp")
$healthCode = Get-HttpCode $healthUrl $null $healthTmp
$backendAction = 'none'
if ($healthCode -eq '200') {
    $healthBody = ''
    if (Test-Path $healthTmp) { $healthBody = (Get-Content $healthTmp -Raw -ErrorAction SilentlyContinue) }
    Write-Output "BACKEND_REUSED port=$BackendPort health=$healthBody"
    if ($healthBody -and $healthBody -notmatch 'mysql') {
        Write-Output 'WARN DB_STATUS_UNEXPECTED (health 200 but database field is not mysql; /api/rooms may fail)'
    }
    $backendAction = 'reused'
} else {
    $busyPid = Get-ListeningPid $BackendPort
    if ($busyPid -gt 0) {
        Fail 'BACKEND_PORT_BUSY' "port $BackendPort is occupied by $(Get-PidLabel $busyPid) but /health is not OK (http=$healthCode). Stop that process yourself or change -BackendPort."
    }
    $backendAction = 'start'
}
Remove-Item $healthTmp -ErrorAction SilentlyContinue

# 2.2 main DS plan: discover, identity-verify, then reuse / fail loudly (never auto-kill)
$dsAction = 'start'
if ($prevState) { $script:managedLogDir = $prevState.logDir } else { $script:managedLogDir = $null }
$udpOwner = Get-UdpOwnerPid $DsPort
if ($udpOwner -gt 0) {
    $managed = $false
    if ($prevState -and $prevState.dsPid -eq $udpOwner -and $prevState.dsExe) {
        $ownerProc = Get-Process -Id $udpOwner -ErrorAction SilentlyContinue
        if ($ownerProc -and $ownerProc.Path -eq $prevState.dsExe) { $managed = $true }
    }
    if ($managed) {
        # P0-A deployment gate (2026-09-15): a running DS only counts as "this build is deployed"
        # when its log declares the manifest protocol AND the process started after the build.
        # A stale process is NOT auto-killed - the user is told exactly what to do.
        $serverLog = $null
        if ($script:managedLogDir) { $serverLog = Join-Path $script:managedLogDir 'server.log' }
        $builtAtUtc = [DateTime]::Parse($manifest.builtAtUtc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
        $proc = Get-Process -Id $udpOwner
        $declared = $null
        if ($serverLog -and (Test-Path $serverLog)) {
            $hits = @(Select-String -Path $serverLog -Pattern 'APP_PROTOCOL id=([a-z0-9\-]+)' -AllMatches)
            if ($hits.Count -gt 0) { $declared = $hits[$hits.Count - 1].Matches[0].Groups[1].Value }
        }
        if ($proc.StartTime.ToUniversalTime() -lt $builtAtUtc) {
            Fail 'DS_STALE_PROCESS' ("A dedicated server (pid=$udpOwner, port $DsPort) is running an OLD build and must not be treated as deployed: process started at {0} (UTC {1}) BEFORE build written at {2}. Run the project-root stop script and re-run this launcher. If the old instance has an ongoing match, wait for it to finish; this tool never force-kills processes." -f $proc.StartTime, $proc.StartTime.ToUniversalTime(), $builtAtUtc)
        }
        if ($declared -ne $manifest.protocolId) {
            Fail 'DS_STALE_PROCESS' ("running DS (pid=$udpOwner) declares protocol '$declared' but this build is '$($manifest.protocolId)'. Stop it with the stop script and re-run this launcher.")
        }
        $dsAction = 'reused'
        Write-Output "DS_ALREADY_RUNNING pid=$udpOwner port=$DsPort (verified against build manifest; started by this tool)"
    } else {
        Fail 'DS_PORT_BUSY' "udp/$DsPort is held by $(Get-PidLabel $udpOwner) which was NOT started by this tool (or state.json is stale). Close it or pass -DsPort."
    }
}

# 2.3 map DS plan (per instance): reuse by stable identity; foreign port holder = explicit failure
$mapPlan = @()
if ($AllMaps) {
    $poolProbe = Join-Path $env:TEMP ("usp-pool-probe-" + [guid]::NewGuid().ToString('N') + ".tmp")
    foreach ($def in (Get-MapDefs)) {
        $plan = [ordered]@{ def = $def; action = 'start'; record = $null }
        $old = $null
        foreach ($entry in $prevMapState) {
            if ($entry -and $entry.instanceId -eq $def.id) { $old = $entry; break }
        }
        if ($old -and (Test-PidRunsExe ([int]$old.pid) $old.exe)) {
            # identity holds: same pid still runs the recorded exe; require registration evidence
            if (Test-InstanceRegistered $def.id $old.logFile $poolProbe) {
                $plan.action = 'reused'
                $plan.record = $old
                Write-Output "MAP_DS_ALREADY_RUNNING instance=$($def.id) pid=$($old.pid) port=$($def.port)"
            }
        }
        if ($plan.action -eq 'start') {
            $holder = Get-UdpOwnerPid $def.port
            if ($holder -gt 0) {
                Fail 'MAP_DS_PORT_BUSY' "udp/$($def.port) for $($def.id) is held by $(Get-PidLabel $holder) which is not a verified instance of this tool. Close it or pass a different -DsPort."
            }
        }
        $mapPlan += $plan
    }
    Remove-Item $poolProbe -ErrorAction SilentlyContinue
}

Write-Output ("PLAN backend=$backendAction mainDs=$dsAction maps=" + $(if ($AllMaps) { ($mapPlan | ForEach-Object { $_.action }) -join ',' } else { 'off' }))

# 2.4 CheckOnly exits BEFORE any mutation (F08)
if ($CheckOnly) {
    Write-Output 'CHECKONLY_DONE (no process started, no logs or state files touched)'
    exit 0
}

# ---- 3. execute: backend first (recovery), then control-plane key ------------
$hotRoot = Join-Path $root 'fps-backend\src\UnityFps.Api\hotupdate'
$hotManifestPath = Join-Path $hotRoot 'maps-manifest.json'
$hasMapChannel = Test-Path -LiteralPath $hotManifestPath
if (-not $hasMapChannel) { $hotManifestPath = Join-Path $hotRoot 'manifest.json' }
$hotMapHashes = @{}
$hotMapCatalog = @()
if (Test-Path -LiteralPath $hotManifestPath) {
    $hotManifest = Get-Content -LiteralPath $hotManifestPath -Raw | ConvertFrom-Json
    if ($hasMapChannel -and ([string]$hotManifest.protocolId -ne [string]$manifest.protocolId -or [string]$hotManifest.releaseId -ne '')) {
        Fail 'HOT_MAP_IDENTITY_MISMATCH' 'Local map channel must match this DS protocol and the local release identity.'
    }
    if ([string]$hotManifest.version -notmatch '^[1-9][0-9]*$') { Fail 'HOT_MAP_MANIFEST_INVALID' 'Invalid hotupdate version.' }
    foreach ($spec in @(
        @{ id = 'map_04'; scene = 'Map_TrainingYard'; display = 'Training Yard'; modes = @('TDM'); capacity = 8; groups = @('Red','Blue') },
        @{ id = 'map_05'; scene = 'Map_NightRelay'; display = 'Night Relay'; modes = @('KillRace'); capacity = 16; groups = @('Red','Blue','FFA') }
    )) {
        $bundlePath = 'maps/' + $spec.scene.ToLowerInvariant() + '.bundle'
        $entry = @($hotManifest.files | Where-Object { $_.path -eq $bundlePath }) | Select-Object -First 1
        if (-not $entry) { continue }
        $onDisk = Join-Path (Join-Path $hotRoot ([string]$hotManifest.version)) ($bundlePath.Replace('/', [IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $onDisk) -or [string]$entry.hash -notmatch '^[a-fA-F0-9]{64}$' `
            -or (Get-FileHash -LiteralPath $onDisk -Algorithm SHA256).Hash -ne [string]$entry.hash) {
            Fail 'HOT_MAP_BUNDLE_INVALID' "Missing or mismatched $bundlePath for version $($hotManifest.version)."
        }
        $hotMapHashes[$spec.id] = ([string]$entry.hash).ToLowerInvariant()
        $hotMapCatalog += [ordered]@{ mapId = $spec.id; displayName = $spec.display; sceneName = $spec.scene;
            modes = $spec.modes; maxCapacity = $spec.capacity; spawnGroups = $spec.groups;
            contentVersion = [string]$hotManifest.version; contentHash = $hotMapHashes[$spec.id] }
    }
}
if ($AllMaps -and (-not $hotMapHashes.ContainsKey('map_04') -or -not $hotMapHashes.ContainsKey('map_05'))) {
    Fail 'HOT_MAP_BUNDLE_MISSING' 'AllMaps requires published, verified bundles for map_04 and map_05.'
}
if ($dsAction -eq 'reused' -and [string]$prevState.contentHash -ne [string]$hotMapHashes[$MapId]) {
    Fail 'DS_STALE_MAP_CONTENT' "The running main DS has an old map content hash. Stop it and rerun this launcher."
}
foreach ($entry in $mapPlan) {
    if ($entry.action -ne 'reused') { continue }
    $expectedHash = [string]$hotMapHashes[$entry.def.mapId]
    if ([string]$entry.record.contentHash -ne $expectedHash -or [int]$entry.record.capacity -ne [int]$entry.def.capacity) {
        Fail 'MAP_DS_STALE_CONTENT_OR_CAPACITY' "The running $($entry.def.mapId) DS has an old map hash or capacity. Stop it and rerun this launcher."
    }
}
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
$catalogPath = Join-Path $runtimeDir 'maps.json'
ConvertTo-Json -InputObject @($hotMapCatalog) -Depth 8 | Set-Content -LiteralPath $catalogPath -Encoding UTF8
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runLog = Join-Path $logRoot $stamp
New-Item -ItemType Directory -Path $runLog -Force | Out-Null

$backendPid = 0
if ($prevState -and $prevState.backendPid) { $backendPid = [int]$prevState.backendPid }
if ($backendAction -eq 'start') {
    Write-Output "BACKEND_STARTING port=$BackendPort log=$runLog"
    $argList = @('run', '--no-restore', '--project', $backendCsproj, '--', "--ServerInstances:ServerKey=$key")
    $previousCatalog = [Environment]::GetEnvironmentVariable('FPS_MAP_CATALOG', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('FPS_MAP_CATALOG', $catalogPath, 'Process')
        $api = Start-Process -FilePath 'dotnet' -ArgumentList $argList -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $runLog 'backend.out.log') `
            -RedirectStandardError (Join-Path $runLog 'backend.err.log') -PassThru
    } finally { [Environment]::SetEnvironmentVariable('FPS_MAP_CATALOG', $previousCatalog, 'Process') }
    $backendPid = $api.Id
    $ok = $false
    $healthTmp2 = Join-Path $env:TEMP ("usp-health2-" + [guid]::NewGuid().ToString('N') + ".tmp")
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 2
        $code = Get-HttpCode $healthUrl $null $healthTmp2
        if ($code -eq '200') { $ok = $true; break }
        if ($api.HasExited) { break }
    }
    Remove-Item $healthTmp2 -ErrorAction SilentlyContinue
    if (-not $ok) {
        $tail = ''
        $errLog = Join-Path $runLog 'backend.err.log'
        if (Test-Path $errLog) { $tail = (Get-Content $errLog -Tail 20) -join "`n" }
        Fail 'BACKEND_HEALTH_FAIL' "pid=$backendPid exited=$($api.HasExited)`n$tail"
    }
    Write-Output "BACKEND_OK pid=$backendPid"
}

# Control-plane key check runs on EVERY path (reused backend included): the launcher
# must not hand DS instances to a backend started with a different key.
$poolTmp = Join-Path $env:TEMP ("usp-pool-" + [guid]::NewGuid().ToString('N') + ".tmp")
$poolCode = Get-HttpCode $poolUrl $key $poolTmp
Remove-Item $poolTmp -ErrorAction SilentlyContinue
if ($poolCode -eq '401' -or $poolCode -eq '403') {
    Fail 'CONTROL_KEY_REJECTED' "pool http=$poolCode - the running backend was started with a DIFFERENT ServerKey than $secretFile. Stop that backend and rerun this entry (it will start one with the key file value)."
}
if ($poolCode -ne '200') {
    Fail 'CONTROL_PLANE_CHECK_FAIL' "pool http=$poolCode - backend is up but the server-instance control plane did not answer; check backend logs in $runLog"
}
Write-Output "CONTROL_PLANE_OK pool=200 capacity=$Capacity"

# ---- 4. execute: main DS ------------------------------------------------------
$mainPid = 0
$mainLogDir = $runLog
if ($dsAction -eq 'start') {
    $serverLog = Join-Path $runLog 'server.log'
    $argList = @('-batchmode', '-nographics',
        '-dedicatedServer', '-instanceId', $InstanceId, '-port', "$DsPort",
        '-publicAddress', '127.0.0.1', '-backendUrl', "http://127.0.0.1:$BackendPort",
        '-serverKey', $key, '-buildVersion', 'local-dev', '-capacity', "$Capacity",
        '-mapId', $MapId,
        '-logFile', $serverLog)
    $previousMapHash = [Environment]::GetEnvironmentVariable('FPS_MAP_CONTENT_HASH', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('FPS_MAP_CONTENT_HASH', $hotMapHashes[$MapId], 'Process')
        $ds = Start-Process -FilePath $dsExe -ArgumentList $argList `
            -RedirectStandardOutput (Join-Path $runLog 'server.out.log') `
            -RedirectStandardError (Join-Path $runLog 'server.err.log') -PassThru -WindowStyle Hidden
    } finally { [Environment]::SetEnvironmentVariable('FPS_MAP_CONTENT_HASH', $previousMapHash, 'Process') }
    Write-Output "DS_STARTING pid=$($ds.Id) instance=$InstanceId port=$DsPort"
    $ready = $false
    $registered = $false
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 2
        if (Test-Path $serverLog) {
            if (-not $ready) {
                if (Select-String -Path $serverLog -Pattern 'DS_READY' -Quiet) { $ready = $true }
            }
            if (Select-String -Path $serverLog -Pattern 'REGISTERED' -Quiet) { $registered = $true; break }
        }
        if ($ds.HasExited) { break }
    }
    if (-not $registered) {
        $tail = ''
        if (Test-Path $serverLog) { $tail = (Get-Content $serverLog -Tail 30) -join "`n" }
        Fail 'DS_NOT_REGISTERED' "pid=$($ds.Id) ready=$ready exited=$($ds.HasExited) log=$serverLog`n$tail"
    }
    Write-Output "DS_READY_AND_REGISTERED pid=$($ds.Id) log=$serverLog"
    $mainPid = $ds.Id
} else {
    # reused: preserve the original ownership record (do not rewrite pid/logDir/startedAt)
    $mainPid = [int]$prevState.dsPid
    $mainLogDir = $prevState.logDir
}

# ---- 5. execute + verify: map DS instances (F07: per-instance, merged state) --
$mapRecords = @()
$mapFailures = @()
if ($AllMaps) {
    $pending = @($mapPlan | Where-Object { $_.action -eq 'start' })
    $reused  = @($mapPlan | Where-Object { $_.action -eq 'reused' })
    foreach ($entry in $reused) {
        $rec = $entry.record
        $mapRecords += [ordered]@{
            instanceId = $rec.instanceId; mapId = $rec.mapId; port = $rec.port; pid = $rec.pid
            exe = $rec.exe; logFile = $rec.logFile; registered = $true; reused = $true
            capacity = $rec.capacity; contentHash = $rec.contentHash
        }
    }
    $started = @()
    foreach ($entry in $pending) {
        $def = $entry.def
        $mapLog = Join-Path $runLog ("server_" + $def.mapId + ".log")
        $mapArgs = @('-batchmode', '-nographics',
            '-dedicatedServer', '-instanceId', $def.id, '-port', "$($def.port)",
            '-publicAddress', '127.0.0.1', '-backendUrl', "http://127.0.0.1:$BackendPort",
            '-serverKey', $key, '-buildVersion', 'local-dev', '-capacity', "$($def.capacity)",
            '-mapId', $def.mapId,
            '-logFile', $mapLog)
        $previousMapHash = [Environment]::GetEnvironmentVariable('FPS_MAP_CONTENT_HASH', 'Process')
        try {
            [Environment]::SetEnvironmentVariable('FPS_MAP_CONTENT_HASH', $hotMapHashes[$def.mapId], 'Process')
            $mapProc = Start-Process -FilePath $dsExe -ArgumentList $mapArgs -PassThru -WindowStyle Hidden
        } finally { [Environment]::SetEnvironmentVariable('FPS_MAP_CONTENT_HASH', $previousMapHash, 'Process') }
        Write-Output "MAP_DS_STARTING pid=$($mapProc.Id) instance=$($def.id) map=$($def.mapId) port=$($def.port)"
        $started += [ordered]@{
            def = $def; pid = $mapProc.Id; logFile = $mapLog; registered = $false
        }
    }
    $deadline = (Get-Date).ToUniversalTime().AddSeconds(60)
    while ((Get-Date).ToUniversalTime() -lt $deadline) {
        Start-Sleep -Seconds 2
        $waiting = @($started | Where-Object { -not $_.registered })
        if ($waiting.Count -eq 0) { break }
        foreach ($rec in $waiting) {
            if (Test-Path $rec.logFile) {
                if (Select-String -Path $rec.logFile -Pattern 'REGISTERED' -Quiet) { $rec.registered = $true }
            }
        }
    }
    foreach ($rec in $started) {
        $alive = Test-PidRunsExe ([int]$rec.pid) $dsExe
        if ($rec.registered -and $alive) {
            Write-Output ("MAP_DS_READY pid=" + $rec.pid + " map=" + $rec.def.mapId + " port=" + $rec.def.port)
            $mapRecords += [ordered]@{
                instanceId = $rec.def.id; mapId = $rec.def.mapId; port = $rec.def.port; pid = $rec.pid
                exe = $dsExe; logFile = $rec.logFile; registered = $true; reused = $false
                capacity = $rec.def.capacity; contentHash = $hotMapHashes[$rec.def.mapId]
            }
        } else {
            Write-Output ("MAP_DS_FAILED map=" + $rec.def.mapId + " pid=" + $rec.pid +
                " alive=" + $alive + " registered=" + $rec.registered + " log=" + $rec.logFile)
            $mapFailures += $rec.def.mapId
            # no record for the failed instance; an existing healthy record for the same
            # instanceId (from a previous run) stays untouched below
        }
    }
}

# ---- 6. merge-save ownership records (never blind-overwrite healthy records) --
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
$state = [ordered]@{
    startedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    backendPort  = $BackendPort
    backendPid   = $backendPid
    dsPid        = $mainPid
    dsPort       = $DsPort
    instanceId   = $InstanceId
    dsExe        = $dsExe
    logDir       = $mainLogDir
    contentHash  = $hotMapHashes[$MapId]
}
$tmpState = $stateFile + '.tmp'
$state | ConvertTo-Json | Set-Content -Path $tmpState -Encoding ASCII
Move-Item -Force -Path $tmpState -Destination $stateFile

$tmpMapState = $mapStateFile + '.tmp'
$mapRecords | ConvertTo-Json | Set-Content -Path $tmpMapState -Encoding ASCII
Move-Item -Force -Path $tmpMapState -Destination $mapStateFile

if ($mapFailures.Count -gt 0) {
    Fail 'MAP_DS_NOT_ALL_READY' ("maps failed: " + ($mapFailures -join ',') + "; healthy instances are recorded in $mapStateFile and can be stopped with the stop script")
}
Write-Output "ALL_OK backend=$backendAction ds_pid=$mainPid log_dir=$runLog"
