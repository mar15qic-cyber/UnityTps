param([Parameter(Mandatory)][string]$ConfigPath,[Parameter(Mandatory)][string]$ServiceAccount)
$ErrorActionPreference='Stop'
$config=[IO.Path]::GetFullPath($ConfigPath)
$watch=Join-Path $PSScriptRoot 'Watch-CloudServer.ps1'
if ($config.Contains('"') -or $watch.Contains('"')) { throw 'Invalid path.' }
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -File "'+$watch+'" -ConfigPath "'+$config+'"')
$trigger=New-ScheduledTaskTrigger -AtStartup
$principal=New-ScheduledTaskPrincipal -UserId $ServiceAccount -LogonType S4U -RunLevel Limited
$settings=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'UnityFps-Invitation' -Action $action -Trigger $trigger -Principal $principal -Settings $settings
Write-Output 'Task installed but not started. Verify file permissions, IIS, firewall and database before Start-ScheduledTask.'
