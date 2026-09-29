param([string]$RunName = 'run01', [ValidateSet('KillRace','TDM')][string]$Mode = 'KillRace',
    [ValidateSet('SystemAudit0927','SystemFix0927')][string]$Campaign = 'SystemFix0927')
$ErrorActionPreference = 'Stop'
$auditProject = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$auditDir = Join-Path $auditProject ('Logs/' + $Campaign + '/' + $RunName)
& python (Join-Path $PSScriptRoot 'Prepare-SystemAuditPackage.py') $Campaign
if ($LASTEXITCODE -ne 0) { throw 'Audit package registration failed.' }
if (Test-Path -LiteralPath $auditDir) { throw 'Use a new run directory to preserve existing evidence.' }
New-Item -ItemType Directory -Path $auditDir -Force | Out-Null
$auditClientDir = Join-Path $auditProject ('Builds/' + $Campaign + '/Client')
$auditServerDir = Join-Path $auditProject ('Builds/' + $Campaign + '/Server')
$auditClient = Get-Content -LiteralPath (Join-Path $auditClientDir 'build-manifest.json') -Raw | ConvertFrom-Json
$auditServer = Get-Content -LiteralPath (Join-Path $auditServerDir 'build-manifest.json') -Raw | ConvertFrom-Json
if ($auditClient.inputDigest -ne $auditServer.inputDigest -or $auditClient.protocolId -ne $auditServer.protocolId) { throw 'Client/server identity mismatch.' }
if ($auditClient.inputDigest -ne (Get-Content -LiteralPath (Join-Path $auditProject ('Logs/' + $Campaign + '/input-digest-before.txt')) -Raw).Trim()) { throw 'Unexpected source digest.' }
foreach ($auditBuild in @(@($auditClientDir, 'UnityFpsClient', $auditClient), @($auditServerDir, 'UnityFpsDedicatedServer', $auditServer))) {
    $auditManaged = Join-Path $auditBuild[0] ($auditBuild[1] + '_Data/Managed')
    $auditHash = (Get-FileHash -LiteralPath (Join-Path $auditManaged 'Game.Gameplay.dll') -Algorithm SHA256).Hash
    if ($auditHash -ne $auditBuild[2].gamePlayDllSha256) { throw 'Gameplay DLL integrity failure.' }
    if (-not (Test-Path -LiteralPath (Join-Path $auditManaged 'Game.RuntimeAudit.dll'))) { throw 'Test driver missing from audit build.' }
}
$auditRecords = [System.Collections.Generic.List[object]]::new()
function Record-AuditProcess($Process, [string]$Role, [string]$Executable) {
    $auditRecords.Add([ordered]@{ role = $Role; pid = $Process.Id; executable = $Executable; startedAtUtc = [DateTime]::UtcNow.ToString('O') })
    $auditRecords | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $auditDir 'processes.json') -Encoding utf8
}
$auditKey = (Get-Content -LiteralPath (Join-Path $auditProject 'Tools/Server/.runtime/serverKey.txt') -Raw).Trim()
$auditBackendReady = $false
try { $null = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health' -TimeoutSec 3; $auditBackendReady = $true } catch { }
if (-not $auditBackendReady) {
    $auditOldKey = [Environment]::GetEnvironmentVariable('ServerInstances__ServerKey', 'Process')
    $auditOldCatalog = [Environment]::GetEnvironmentVariable('FPS_MAP_CATALOG', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('ServerInstances__ServerKey', $auditKey, 'Process')
        [Environment]::SetEnvironmentVariable('FPS_MAP_CATALOG', (Join-Path $auditProject 'Tools/Server/.runtime/maps.json'), 'Process')
        $auditBackendProject = Join-Path $auditProject 'fps-backend/src/UnityFps.Api/UnityFps.Api.csproj'
        $auditBackend = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-restore','--project',('"' + $auditBackendProject + '"')) -WorkingDirectory $auditProject -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $auditDir 'backend.out.log') -RedirectStandardError (Join-Path $auditDir 'backend.err.log')
        Record-AuditProcess $auditBackend 'backend-parent' (Get-Command dotnet).Source
    } finally {
        [Environment]::SetEnvironmentVariable('ServerInstances__ServerKey', $auditOldKey, 'Process')
        [Environment]::SetEnvironmentVariable('FPS_MAP_CATALOG', $auditOldCatalog, 'Process')
    }
    $auditDeadline = [DateTime]::UtcNow.AddSeconds(90)
    while ([DateTime]::UtcNow -lt $auditDeadline) {
        Start-Sleep -Seconds 2
        try { $null = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health' -TimeoutSec 3; $auditBackendReady = $true; break } catch { }
        if ($auditBackend.HasExited) { throw 'Audit backend exited; inspect its local log.' }
    }
    if (-not $auditBackendReady) { throw 'Backend readiness deadline.' }
}
$auditProxyScript = Join-Path $auditProject 'Tools/Network/runtime_audit_proxy.py'
$auditProxy = Start-Process -FilePath 'python' -ArgumentList @('-u', ('"' + $auditProxyScript + '"'), '--directory', ('"' + $auditDir + '"')) -WorkingDirectory $auditProject -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $auditDir 'proxy.out.log') -RedirectStandardError (Join-Path $auditDir 'proxy.err.log')
Record-AuditProcess $auditProxy 'proxy' (Get-Command python).Source
$auditDsExe = Join-Path $auditServerDir 'UnityFpsDedicatedServer.exe'
$auditInstance = 'audit-927-' + $RunName + '-' + [DateTime]::UtcNow.ToString('HHmmss')
$auditOldDsKey = [Environment]::GetEnvironmentVariable('FPS_SERVER_KEY', 'Process')
try {
    [Environment]::SetEnvironmentVariable('FPS_SERVER_KEY', $auditKey, 'Process')
    $auditDs = Start-Process -FilePath $auditDsExe -ArgumentList @('-batchmode','-nographics','-instanceId',$auditInstance,'-port','27770','-backendUrl','http://127.0.0.1:5080','-publicAddress','127.0.0.1','-capacity','8','-mapId','arena','-buildVersion',$auditServer.buildId,'-runtimeAuditDir',('"' + $auditDir + '"'),'-runtimeAuditRole','server','-publicTestTelemetry','-testRunId',('audit927-' + $RunName),'-logFile',('"' + (Join-Path $auditDir 'server.log') + '"')) -WorkingDirectory $auditServerDir -WindowStyle Hidden -PassThru
    Record-AuditProcess $auditDs 'server' $auditDsExe
} finally { [Environment]::SetEnvironmentVariable('FPS_SERVER_KEY', $auditOldDsKey, 'Process') }
$auditDeadline = [DateTime]::UtcNow.AddSeconds(45)
$auditRegistered = $false
while ([DateTime]::UtcNow -lt $auditDeadline) {
    Start-Sleep -Seconds 2
    if ($auditDs.HasExited) { throw 'Audit server exited; inspect its local log.' }
    $auditLog = Join-Path $auditDir 'server.log'
    if ((Test-Path -LiteralPath $auditLog) -and (Select-String -LiteralPath $auditLog -Pattern '[ServerRegistry] REGISTERED' -SimpleMatch -Quiet)) { $auditRegistered = $true; break }
}
if (-not $auditRegistered) { throw 'Audit DS registration deadline.' }
$auditClientExe = Join-Path $auditClientDir 'UnityFpsClient.exe'
$auditPassword = 'Audit-' + [Guid]::NewGuid().ToString('N') + '!'
$auditRoomFile = Join-Path $auditDir 'room-code.txt'
$auditRoles = @('alpha','bravo','charlie')
$auditKillTarget = if ($Mode -eq 'TDM') { '100' } else { '30' }
$auditTimeLimit = if ($Mode -eq 'TDM') { '15' } else { '10' }
for ($auditIndex = 0; $auditIndex -lt 3; $auditIndex++) {
    $auditRole = $auditRoles[$auditIndex]
    $auditUser = 'au927' + $RunName + $auditRole + [DateTime]::UtcNow.ToString('HHmmss')
    $auditAction = if ($auditIndex -eq 0) { 'create' } else { 'join' }
    $auditClientProcess = Start-Process -FilePath $auditClientExe -ArgumentList @('-screen-fullscreen','0','-screen-width','640','-screen-height','360','-runtimeAuditDir',('"' + $auditDir + '"'),'-runtimeAuditRole',$auditRole,'-runtimeAuditProxyPort',([string](27780 + $auditIndex)),'-itUser',$auditUser,'-itPass',$auditPassword,'-itAction',$auditAction,'-itRoomFile',('"' + $auditRoomFile + '"'),'-itMax','4','-itWaitPlayers','3','-itMode',$Mode,'-itKill',$auditKillTarget,'-itTime',$auditTimeLimit,'-publicTestTelemetry','-testRunId',('audit927-' + $RunName),'-logFile',('"' + (Join-Path $auditDir ($auditRole + '.log')) + '"')) -WorkingDirectory $auditClientDir -WindowStyle Hidden -PassThru
    Record-AuditProcess $auditClientProcess $auditRole $auditClientExe
    Write-Output ('AUDIT_CLIENT_START ' + $auditRole + ' pid=' + $auditClientProcess.Id)
}
Write-Output ('AUDIT_STARTED directory=' + $auditDir + ' protocol=' + $auditServer.protocolId + ' inputDigest=' + $auditServer.inputDigest)
