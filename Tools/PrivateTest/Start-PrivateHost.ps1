param([Parameter(Mandatory)][string]$ConfigPath,[switch]$CheckOnly)
. "$PSScriptRoot/Private.Common.ps1"
$c=Read-PrivateConfig $ConfigPath
$adapter=@(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $c.publicAddress -ErrorAction SilentlyContinue)
if ($adapter.Count -ne 1) { throw 'Host overlay address is not assigned. Join and authorize ZeroTier first.' }
$net=Get-NetAdapter -InterfaceIndex $adapter[0].InterfaceIndex
if ($net.InterfaceDescription -notmatch 'ZeroTier') { throw 'Configured address is not on the ZeroTier interface.' }
if ($CheckOnly) { & "$PSScriptRoot/../Cloud/Start-CloudServer.ps1" -ConfigPath $ConfigPath -CheckOnly; exit }
& "$PSScriptRoot/Initialize-BaseMapCatalog.ps1" -ConfigPath $ConfigPath
& "$PSScriptRoot/../Cloud/Start-CloudServer.ps1" -ConfigPath $ConfigPath
if (-not $?) { throw 'Private server startup failed.' }
& "$PSScriptRoot/Restart-AdditionalMaps.ps1" -ConfigPath $ConfigPath
$pause=Join-Path $c.stateRoot 'admissions.paused'
if (Test-Path -LiteralPath $pause) { Remove-Item -LiteralPath $pause }
Write-Output 'PRIVATE_HOST_READY; external UDP still unverified.'
