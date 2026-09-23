param([Parameter(Mandatory)][string]$MySqlDefaultsFile,[Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_]+$')][string]$Database,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
# Defaults file is private and contains [client] user/password/host, never command-line credentials.
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$out=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ($Database+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.sql')
& mysqldump "--defaults-extra-file=$MySqlDefaultsFile" --single-transaction --routines --triggers --hex-blob "--result-file=$out" $Database
if ($LASTEXITCODE -ne 0) { throw 'Database backup failed; do not deploy or roll back.' }
Write-Output ('DATABASE_BACKUP_OK '+$out)
