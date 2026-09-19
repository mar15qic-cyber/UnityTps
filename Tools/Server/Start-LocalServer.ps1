# Start-LocalServer.ps1 - local online stack launcher (backend control plane + dedicated server).
# Pure ASCII on purpose: PS 5.1 mis-reads non-BOM UTF-8 Chinese sources (project SOP).
# Chinese UX lives in the project-root .cmd entries that call this script.
#
# Guarantees (2026-09-10 dual-player audit item 2):
#   - Idempotent: re-running never restarts a healthy backend or a running DS.
#   - Never kills by process name; only this tool's state.json PIDs are managed by Stop script.
#   - Reuses an already-healthy backend; refuses on foreign port occupiers.
#   - Verifies control-plane key (pool endpoint) instead of trusting listener readiness only.
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
    # Phase 8: additionally start one DS per combat map (map_01/02/03) so rooms created
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

function Get-PidLabel([int]$procId) {
    if ($procId -le 0) { return 'unknown' }
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($p) { return "$($p.ProcessName) pid=$($p.Id)" }
    return "pid=$procId(exited)"
}

# Phase 8: start one DS per combat map (map_01/02/03), each with its own port/instanceId/log.
# Called from BOTH the idempotent rerun path and the fresh-start path (script-scope vars resolve
# at call time). Instances are recorded in .runtime/map-servers.json for Stop-LocalServer.ps1.
function Start-MapDsInstances {
    if (-not $AllMaps) { return }
    if (-not $runLog) {
        $script:runLog = Join-Path $logRoot ("maps_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
        New-Item -ItemType Directory -Path $runLog -Force | Out-Null
    }
    $mapDefs = @(
        @{ id = "$InstanceId-map01"; port = $DsPort + 1; mapId = 'map_01' },
        @{ id = "$InstanceId-map02"; port = $DsPort + 2; mapId = 'map_02' },
        @{ id = "$InstanceId-map03"; port = $DsPort + 3; mapId = 'map_03' },
        @{ id = "$InstanceId-map04"; port = $DsPort + 4; mapId = 'map_04' }
    )
    $mapRecords = @()
    foreach ($def in $mapDefs) {
        $mapLog = Join-Path $runLog ("server_" + $def.mapId + ".log")
        if (Test-Path $mapLog) { Remove-Item $mapLog -Force }
        $mapArgs = @('-batchmode', '-nographics',
            '-dedicatedServer', '-instanceId', $def.id, '-port', "$($def.port)",
            '-publicAddress', '127.0.0.1', '-backendUrl', "http://127.0.0.1:$BackendPort",
            '-serverKey', $key, '-buildVersion', 'local-dev', '-capacity', "$Capacity",
            '-mapId', $def.mapId,
            '-logFile', $mapLog)
        $mapProc = Start-Process -FilePath $dsExe -ArgumentList $mapArgs -PassThru -WindowStyle Hidden
        Write-Output "MAP_DS_STARTING pid=$($mapProc.Id) instance=$($def.id) map=$($def.mapId) port=$($def.port)"
        $mapRecords += [ordered]@{
            instanceId = $def.id
            mapId      = $def.mapId
            port       = $def.port
            pid        = $mapProc.Id
            exe        = $dsExe
            logFile    = $mapLog
            registered = $false
        }
    }
    $deadline = (Get-Date).ToUniversalTime().AddSeconds(40)
    while ((Get-Date).ToUniversalTime() -lt $deadline) {
        Start-Sleep -Seconds 2
        $pending = @($mapRecords | Where-Object { -not $_.registered })
        if ($pending.Count -eq 0) { break }
        foreach ($rec in $pending) {
            if (Test-Path $rec.logFile) {
                if (Select-String -Path $rec.logFile -Pattern 'REGISTERED' -Quiet) { $rec.registered = $true }
            }
        }
    }
    $mapStatePath = Join-Path $runtimeDir 'map-servers.json'
    $mapRecords | ConvertTo-Json | Set-Content -Path $mapStatePath -Encoding ASCII
    foreach ($rec in $mapRecords) {
        Write-Output ("MAP_DS_READY pid=" + $rec.pid + " map=" + $rec.mapId + " port=" + $rec.port +
            " registered=" + [bool]$rec.registered)
    }
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
# The manifest is written by the Unity build scripts into the build output dir.
# It carries the app protocol id + business-assembly hashes for THIS build.
# Rules:
#   - manifest missing            -> refuse to vouch for the build (rebuild DS)
#   - on-disk Game.Gameplay.dll hash != manifest -> build dir was tampered/half-copied
#   - a RUNNING DS process only counts as deployed when
#       (a) its log contains APP_PROTOCOL id == manifest protocolId, and
#       (b) the process started AFTER this build was written.
#     Otherwise the running process is a stale binary (the 2026-09-15 incident:
#     old DS + new clients -> unknown PacketId kick mid-match). We never auto-kill.
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

function Test-RunningDsMatchesManifest([int]$procId, [string]$procLogDir) {
    # (b) process start time must be after this build was written
    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if (-not $proc) { return @{ ok = $false; reason = "pid $procId no longer exists" } }
    $builtAtUtc = [DateTime]::Parse($manifest.builtAtUtc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    if ($proc.StartTime.ToUniversalTime() -lt $builtAtUtc) {
        return @{ ok = $false; reason = ("process started at {0} (UTC {1}) BEFORE build written at {2} - it cannot be running this build" -f $proc.StartTime, $proc.StartTime.ToUniversalTime(), $builtAtUtc) }
    }
    # (a) its log must declare the same app protocol id as this build
    $serverLog = $null
    if ($procLogDir) { $serverLog = Join-Path $procLogDir 'server.log' }
    $declared = $null
    if ($serverLog -and (Test-Path $serverLog)) {
        $hits = @(Select-String -Path $serverLog -Pattern 'APP_PROTOCOL id=([a-z0-9\-]+)' -AllMatches)
        if ($hits.Count -gt 0) {
            $declared = $hits[$hits.Count - 1].Matches[0].Groups[1].Value
        }
    }
    if ($declared -ne $manifest.protocolId) {
        if ($null -eq $declared) { return @{ ok = $false; reason = "running DS log has no APP_PROTOCOL line (pre-2026-09-15 binary)" } }
        return @{ ok = $false; reason = "running DS declares protocol '$declared' but this build is '$($manifest.protocolId)'" }
    }
    return @{ ok = $true }
}

# ---- 2. backend state -------------------------------------------------------
$tmp = Join-Path $env:TEMP ("usp-health-" + [guid]::NewGuid().ToString('N') + ".tmp")
$healthCode = Get-HttpCode $healthUrl $null $tmp
$backendAction = 'none'
$backendPid = 0
if ($healthCode -eq '200') {
    $healthBody = ''
    if (Test-Path $tmp) { $healthBody = (Get-Content $tmp -Raw -ErrorAction SilentlyContinue) }
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
Remove-Item $tmp -ErrorAction SilentlyContinue

# ---- 3. DS port ownership ---------------------------------------------------
$udp = Get-NetUDPEndpoint -LocalPort $DsPort -ErrorAction SilentlyContinue | Select-Object -First 1
if ($udp) {
    $ownerPid = [int]$udp.OwningProcess
    $ownerName = Get-PidLabel $ownerPid
    $managed = $false
    $managedLogDir = $null
    if (Test-Path $stateFile) {
        try {
            $prev = Get-Content $stateFile -Raw | ConvertFrom-Json
            if ($prev.dsPid -eq $ownerPid -and $prev.dsExe) {
                $ownerProc = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
                if ($ownerProc -and $ownerProc.Path -eq $prev.dsExe) {
                    $managed = $true
                    $managedLogDir = $prev.logDir
                }
            }
        } catch { }
    }
    if ($managed) {
        # P0-A deployment gate (2026-09-15): a running DS only counts as "this build is deployed"
        # when its log declares the manifest protocol AND the process started after the build.
        # A stale process is NOT auto-killed - the user is told exactly what to do.
        $gate = Test-RunningDsMatchesManifest $ownerPid $managedLogDir
        if (-not $gate.ok) {
            Fail 'DS_STALE_PROCESS' ("A dedicated server (pid=$ownerPid, port $DsPort) is running an OLD build and must not be treated as deployed: " + $gate.reason + ". Run the project-root stop script ('停止联机服务器.cmd' / Stop-LocalServer.ps1) and re-run this launcher. If the old instance has an ongoing match, wait for it to finish or coordinate with its operator; this tool never force-kills processes.")
        }
        Write-Output "DS_ALREADY_RUNNING pid=$ownerPid port=$DsPort (verified against build manifest; started by this tool; nothing to do)"
        Start-MapDsInstances
        Write-Output "BACKEND=$backendAction"
        exit 0
    }
    Fail 'DS_PORT_BUSY' "udp/$DsPort is held by $ownerName which was NOT started by this tool. Close it or pass -DsPort."
}

if ($CheckOnly) {
    Write-Output 'CHECKONLY_DONE (no process started)'
    exit 0
}

# ---- 4. start backend when needed ------------------------------------------
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runLog = Join-Path $logRoot $stamp
New-Item -ItemType Directory -Path $runLog -Force | Out-Null
if ($backendAction -eq 'start') {
    Write-Output "BACKEND_STARTING port=$BackendPort log=$runLog"
    $argList = @('run', '--project', $backendCsproj, '--', "--ServerInstances:ServerKey=$key")
    $api = Start-Process -FilePath 'dotnet' -ArgumentList $argList -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runLog 'backend.out.log') `
        -RedirectStandardError (Join-Path $runLog 'backend.err.log') -PassThru
    $backendPid = $api.Id
    $ok = $false
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 2
        $code = Get-HttpCode $healthUrl $null $tmp
        if ($code -eq '200') { $ok = $true; break }
        if ($api.HasExited) { break }
    }
    Remove-Item $tmp -ErrorAction SilentlyContinue
    if (-not $ok) {
        $tail = ''
        $errLog = Join-Path $runLog 'backend.err.log'
        if (Test-Path $errLog) { $tail = (Get-Content $errLog -Tail 20) -join "`n" }
        Fail 'BACKEND_HEALTH_FAIL' "pid=$backendPid exited=$($api.HasExited)`n$tail"
    }
    Write-Output "BACKEND_OK pid=$backendPid"
}

# ---- 5. control-plane key check (what start_stack.ps1 never verified) -------
$poolTmp = Join-Path $env:TEMP 'usp-pool.tmp'
$poolCode = Get-HttpCode $poolUrl $key $poolTmp
Remove-Item $poolTmp -ErrorAction SilentlyContinue
if ($poolCode -eq '401' -or $poolCode -eq '403') {
    Fail 'CONTROL_KEY_REJECTED' "pool http=$poolCode - the running backend was started with a DIFFERENT ServerKey than $secretFile. Stop that backend and rerun this entry (it will start one with the key file value)."
}
if ($poolCode -ne '200') {
    Fail 'CONTROL_PLANE_CHECK_FAIL' "pool http=$poolCode - backend is up but the server-instance control plane did not answer; check backend logs in $runLog"
}
Write-Output "CONTROL_PLANE_OK pool=200 capacity=$Capacity"

# ---- 6. start dedicated server ---------------------------------------------
$serverLog = Join-Path $runLog 'server.log'
$argList = @('-batchmode', '-nographics',
    '-dedicatedServer', '-instanceId', $InstanceId, '-port', "$DsPort",
    '-publicAddress', '127.0.0.1', '-backendUrl', "http://127.0.0.1:$BackendPort",
    '-serverKey', $key, '-buildVersion', 'local-dev', '-capacity', "$Capacity",
    '-mapId', $MapId,
    '-logFile', $serverLog)
$ds = Start-Process -FilePath $dsExe -ArgumentList $argList `
    -RedirectStandardOutput (Join-Path $runLog 'server.out.log') `
    -RedirectStandardError (Join-Path $runLog 'server.err.log') -PassThru
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

# ---- 7. record ownership for the Stop entry ---------------------------------
New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
$state = [ordered]@{
    startedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    backendPort  = $BackendPort
    backendPid   = $backendPid
    dsPid        = $ds.Id
    dsPort       = $DsPort
    instanceId   = $InstanceId
    dsExe        = $dsExe
    logDir       = $runLog
}
$state | ConvertTo-Json | Set-Content -Path $stateFile -Encoding ASCII
Write-Output "ALL_OK backend=$backendAction ds_pid=$($ds.Id) log_dir=$runLog"
Start-MapDsInstances
