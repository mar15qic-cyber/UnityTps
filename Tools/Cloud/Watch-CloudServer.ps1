param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Cloud.Common.ps1"
$c=Read-CloudConfig $ConfigPath
# Run this supervisor as an at-startup Scheduled Task using a dedicated service account.
# Stop the task before a deliberate drain/stop, otherwise it restarts managed processes.
while ($true) {
    $state=Join-Path $c.stateRoot 'processes.json'
    $restart=-not (Test-Path -LiteralPath $state)
    if (-not $restart) {
        $records=@(Get-Content -LiteralPath $state -Raw | ConvertFrom-Json)
        $restart=$records.Count -ne 6 -or @($records | Where-Object { -not (Test-ManagedProcess $_) }).Count -gt 0
    }
    if ($restart) {
        try { & "$PSScriptRoot/Start-CloudServer.ps1" -ConfigPath $ConfigPath }
        catch { Write-Warning 'Cloud restart failed; inspect server logs. Retrying in 30 seconds.' }
    }
    Start-Sleep -Seconds 30
}
