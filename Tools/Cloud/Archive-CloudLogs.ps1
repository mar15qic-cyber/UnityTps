param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Cloud.Common.ps1"
$c=Read-CloudConfig $ConfigPath
$logRoot=[IO.Path]::GetFullPath((Join-Path $c.stateRoot 'logs'))
$state=Join-Path $c.stateRoot 'processes.json'
$active=@()
if (Test-Path -LiteralPath $state) {
    $active=@(Get-Content -LiteralPath $state -Raw | ConvertFrom-Json | Where-Object { Test-ManagedProcess $_ } | ForEach-Object { if ($_.PSObject.Properties.Name -contains 'logDirectory') { $_.logDirectory } else { $logRoot } })
}
if ($active -contains $logRoot) { throw 'Older process state lacks log ownership. Stop processes before archiving.' }
foreach ($dir in Get-ChildItem -LiteralPath $logRoot -Directory) {
    if ($active -contains $dir.FullName) { continue }
    $target=[IO.Path]::GetFullPath($dir.FullName)
    if (-not $target.StartsWith($logRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive path.' }
    $archive=$target+'.zip'
    if (Test-Path -LiteralPath $archive) { continue }
    Compress-Archive -LiteralPath $target -DestinationPath $archive
    # Preserve source for review. Retention/deletion is an explicit operator action.
}
Write-Output 'CLOSED_LOGS_ARCHIVED; active logs unchanged.'
