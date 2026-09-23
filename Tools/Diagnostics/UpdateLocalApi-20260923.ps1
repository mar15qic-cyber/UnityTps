param([ValidateSet('Backup','Restart','Verify')][string]$Phase = 'Verify')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$apiRoot = Join-Path $root 'fps-backend/src/UnityFps.Api'
$runtime = Join-Path $root 'Tools/Server/.runtime'
$backup = Join-Path $runtime 'api-update-20260923'
$target = Join-Path $apiRoot 'bin/Debug/net8.0'
$stage = Join-Path $root 'Temp/ApiUpdateBuild'
$statePath = Join-Path $runtime 'state.json'
$expectedExe = Join-Path $target 'UnityFps.Api.exe'
$secretsPath = Join-Path $env:APPDATA 'Microsoft/UserSecrets/7acf495b-5209-4b71-bf55-76ca1b684c55/secrets.json'
$secrets = Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection.set_ConnectionString($secrets.'ConnectionStrings:GameDb')
if ($env:ConnectionStrings__GameDb) { throw 'Environment connection override requires explicit inspection first.' }
function Read-ConnectionPart([string[]]$names) {
    foreach ($name in $names) { if ($connection.ContainsKey($name)) { return [string]$connection[$name] } }
    return ''
}
$dbHost = Read-ConnectionPart @('Server','Host','Data Source')
$dbPort = Read-ConnectionPart @('Port')
if (!$dbPort) { $dbPort = '3306' }
$dbUser = Read-ConnectionPart @('User ID','User','Uid','Username')
$dbName = Read-ConnectionPart @('Database','Initial Catalog')
$dbPassword = Read-ConnectionPart @('Password','Pwd')
if ($dbHost -notin @('localhost','127.0.0.1') -or !$dbUser -or $dbName -notmatch '^[a-zA-Z0-9_]+$') {
    throw 'Expected a configured local MySQL database; refusing guessed target.'
}
$mysqlArgs = @('--protocol=TCP',"--host=$dbHost","--port=$dbPort","--user=$dbUser")
function Query([string]$sql) {
    $oldPassword = $env:MYSQL_PWD
    try {
        $env:MYSQL_PWD = $dbPassword
        $rows = & mysql @mysqlArgs --batch --skip-column-names $dbName "--execute=$sql"
        if ($LASTEXITCODE -ne 0) { throw 'Read-only database verification failed.' }
        return $rows
    } finally { $env:MYSQL_PWD = $oldPassword }
}
function Verify-Rules {
    $sql = @"
SELECT 'p90_grip_compat', COUNT(*) FROM AttachmentCompat WHERE WeaponItemId='weapon.smg04' AND SlotType='Underbarrel';
SELECT 'lpw_silencer_active', COUNT(*) FROM CatalogItem WHERE ItemId IN ('attach.lpw.muffler.01','attach.lpw.muffler.02') AND IsActive=1;
SELECT 'lpw_silencer_compat', COUNT(*) FROM AttachmentCompat WHERE AttachmentItemId IN ('attach.lpw.muffler.01','attach.lpw.muffler.02');
SELECT 'lpw_silencer_loadouts', COUNT(*) FROM PlayerLoadoutAttachment WHERE AttachmentItemId IN ('attach.lpw.muffler.01','attach.lpw.muffler.02');
SELECT 'p90_grip_loadouts', COUNT(*) FROM PlayerLoadoutAttachment a JOIN PlayerLoadout l ON l.Id=a.LoadoutId WHERE a.AttachmentSlot='Underbarrel' AND ((a.WeaponSlot='Primary' AND l.PrimaryWeaponId='weapon.smg04') OR (a.WeaponSlot='Secondary' AND l.SecondaryWeaponId='weapon.smg04'));
SELECT 'lpfp_silencer_active', COUNT(*) FROM CatalogItem WHERE ItemId='attach.lpfp.muffler.01' AND IsActive=1;
SELECT 'p90_lpfp_silencer_compat', COUNT(*) FROM AttachmentCompat WHERE WeaponItemId='weapon.smg04' AND AttachmentItemId='attach.lpfp.muffler.01';
"@
    $rows = @(Query $sql)
    $rows | Write-Output
    if ($Phase -ne 'Backup') {
        if ($rows.Count -ne 7) { throw 'Incomplete rule verification.' }
        foreach ($row in $rows) {
            $parts = $row -split "`t"
            $expected = if ($parts[0] -in @('lpfp_silencer_active','p90_lpfp_silencer_compat')) { 1 } else { 0 }
            if ([int]$parts[1] -ne $expected) { throw "Rule not applied: $($parts[0])" }
        }
    }
}
if ($Phase -eq 'Backup') {
    if (Test-Path -LiteralPath $backup) { throw 'Backup already exists; never overwrite recovery evidence.' }
    New-Item -ItemType Directory -Path (Join-Path $backup 'binaries') | Out-Null
    $oldPassword = $env:MYSQL_PWD
    try {
        $env:MYSQL_PWD = $dbPassword
        & mysqldump @mysqlArgs --single-transaction --routines --triggers --hex-blob --no-tablespaces --set-gtid-purged=OFF "--result-file=$(Join-Path $backup 'database.sql')" $dbName
        if ($LASTEXITCODE -ne 0) { throw 'Database backup failed; do not restart.' }
    } finally { $env:MYSQL_PWD = $oldPassword }
    Copy-Item -LiteralPath $statePath -Destination (Join-Path $backup 'state.json')
    foreach ($file in Get-ChildItem -LiteralPath $target -File) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $backup 'binaries') }
    Get-FileHash -LiteralPath (Join-Path $backup 'database.sql') | Select-Object Algorithm,Hash,Path
    Verify-Rules
    Write-Output 'BACKUP_OK'
    exit 0
}
if ($Phase -eq 'Restart') {
    if (!(Test-Path (Join-Path $backup 'database.sql')) -or !(Test-Path (Join-Path $backup 'binaries/UnityFps.Api.dll'))) { throw 'Missing recovery backup.' }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $listener = @(Get-NetTCPConnection -LocalPort 5080 -State Listen)
    if ($listener.Count -ne 1) { throw 'Expected exactly one API listener.' }
    $apiProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener[0].OwningProcess)"
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($apiProcess.ParentProcessId)"
    if ($apiProcess.ExecutablePath -ne $expectedExe -or $parent.ProcessId -ne $state.backendPid -or $parent.CommandLine -notlike '*UnityFps.Api.csproj*') { throw 'API process identity changed; stopping nothing.' }
    $newDll = Join-Path $stage 'UnityFps.Api.dll'
    if (!(Test-Path $newDll)) { throw 'Missing staged build.' }
    # Only the verified API and its dotnet-run parent; never touch Unity servers or MySQL.
    Stop-Process -Id $apiProcess.ProcessId -Force
    if (Get-Process -Id $parent.ProcessId -ErrorAction SilentlyContinue) { Stop-Process -Id $parent.ProcessId -Force }
    foreach ($file in Get-ChildItem -LiteralPath $stage -File) {
        if ($file.Extension -in @('.dll','.exe','.pdb') -or $file.Name -in @('UnityFps.Api.deps.json','UnityFps.Api.runtimeconfig.json')) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $file.Name) -Force
        }
    }
    $expectedHash = (Get-FileHash -LiteralPath $newDll).Hash
    if ((Get-FileHash (Join-Path $target 'UnityFps.Api.dll')).Hash -ne $expectedHash) { throw 'Installed API hash mismatch.' }
    $logDir = Join-Path $root ('Tools/Server/Logs/api-update-' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
    New-Item -ItemType Directory -Path $logDir | Out-Null
    $oldKey = $env:ServerInstances__ServerKey
    try {
        $env:ServerInstances__ServerKey = (Get-Content (Join-Path $runtime 'serverKey.txt') -Raw).Trim()
        $newParent = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--project',(Join-Path $apiRoot 'UnityFps.Api.csproj')) -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $logDir 'backend.out.log') -RedirectStandardError (Join-Path $logDir 'backend.err.log') -PassThru
    } finally { $env:ServerInstances__ServerKey = $oldKey }
    $state.backendPid = $newParent.Id
    $state | Add-Member -NotePropertyName backendLogDir -NotePropertyValue $logDir -Force
    $state | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statePath -Encoding utf8
    $healthy = $false
    for ($i=0; $i -lt 20; $i++) {
        try { $health=Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 2; if ($health.status -eq 'ok' -and $health.database -eq 'mysql') { $healthy=$true; break } } catch { }
        if ($newParent.HasExited) { break }
        Start-Sleep -Seconds 1
    }
    if (!$healthy) { throw "API did not become healthy; inspect private logs at $logDir. Backup remains at $backup." }
    Write-Output "API_RESTART_OK parent=$($newParent.Id) hash=$expectedHash logs=$logDir"
}
$health = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 5
if ($health.status -ne 'ok' -or $health.database -ne 'mysql') { throw 'API health is not MySQL/ok.' }
$current = Get-NetTCPConnection -LocalPort 5080 -State Listen | Select-Object -First 1
$currentProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($current.OwningProcess)"
if ($currentProcess.ExecutablePath -ne $expectedExe) { throw 'Health response came from an unexpected process.' }
Verify-Rules
Write-Output "VERIFIED_API pid=$($current.OwningProcess) database=mysql"
