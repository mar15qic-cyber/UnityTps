param([Parameter(Mandatory)][string]$ConfigPath)
. "$PSScriptRoot/Private.Common.ps1"
$c=Read-PrivateConfig $ConfigPath
$ip=Get-NetIPAddress -IPAddress $c.publicAddress -AddressFamily IPv4 -ErrorAction Stop
$adapter=Get-NetAdapter -InterfaceIndex $ip.InterfaceIndex
if ($adapter.InterfaceDescription -notmatch 'ZeroTier') { throw 'ZeroTier interface required.' }
foreach ($name in @('UnityFps-Private-TCP','UnityFps-Private-UDP')) {
    if (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue) { Remove-NetFirewallRule -Name $name }
}
New-NetFirewallRule -Name 'UnityFps-Private-TCP' -DisplayName 'Unity FPS private API' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $c.playerTcpPort -LocalAddress $c.publicAddress -InterfaceAlias $adapter.Name -RemoteAddress $c.overlaySubnet | Out-Null
New-NetFirewallRule -Name 'UnityFps-Private-UDP' -DisplayName 'Unity FPS private game' -Direction Inbound -Action Allow -Protocol UDP -LocalPort "$($c.baseUdpPort)-$($c.baseUdpPort+5)" -LocalAddress $c.publicAddress -InterfaceAlias $adapter.Name -RemoteAddress $c.overlaySubnet | Out-Null
