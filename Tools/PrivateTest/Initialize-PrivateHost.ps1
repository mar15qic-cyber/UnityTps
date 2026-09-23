param([Parameter(Mandatory)][string]$ReleaseRoot,[Parameter(Mandatory)][string]$GameDb,
 [string]$NetworkId='743993800fd77ce1',[string]$HostAddress='10.16.41.231',[string]$Subnet='10.16.41.0/24',[string]$ReleaseId='private-test-01')
$ErrorActionPreference='Stop'
if($NetworkId -notmatch '^[a-fA-F0-9]{16}$' -or $HostAddress -notmatch '^\d+\.\d+\.\d+\.\d+$' -or [string]::IsNullOrWhiteSpace($GameDb)) { throw 'Network and database configuration are required.' }
$sealed=Join-Path $ReleaseRoot 'release-manifest.json'
if(Test-Path -LiteralPath $sealed){
    $ReleaseId=(Get-Content -LiteralPath $sealed -Raw | ConvertFrom-Json).releaseId
    $published=Get-Content -LiteralPath (Join-Path $ReleaseRoot 'Client/client-environment.json') -Raw | ConvertFrom-Json
    if($published.hostOverlayAddress -ne $HostAddress -or $published.zeroTierNetworkId -ne $NetworkId){throw 'Host network differs from packaged client; rebuild the client for the new network.'}
}
$state=Join-Path $PSScriptRoot '.runtime'
New-Item -ItemType Directory -Path $state -Force | Out-Null
$secrets=Join-Path $state 'secrets.json'
if (-not(Test-Path $secrets)) {
    function New-Key { $bytes=New-Object byte[] 48; $rng=[Security.Cryptography.RandomNumberGenerator]::Create(); try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}; [Convert]::ToBase64String($bytes) }
    @{jwtSigningKey=(New-Key);serverKey=(New-Key);gameDb=$GameDb} | ConvertTo-Json | Set-Content $secrets -Encoding UTF8
    $acl=Get-Acl $secrets; $acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.WindowsIdentity]::GetCurrent().Name,'FullControl','Allow'))); Set-Acl $secrets $acl
}
else {
    $existing=Get-Content $secrets -Raw | ConvertFrom-Json
    $existing.gameDb=$GameDb
    $existing | ConvertTo-Json | Set-Content $secrets -Encoding UTF8
}
$c=Get-Content "$PSScriptRoot/private-host.example.json" -Raw | ConvertFrom-Json
$c.networkId=$NetworkId;$c.publicAddress=$HostAddress;$c.dsBindAddress=$HostAddress;$c.overlaySubnet=$Subnet
$c.releaseRoot=[IO.Path]::GetFullPath($ReleaseRoot);$c.releaseId=$ReleaseId;$c.stateRoot=$state;$c.secretsFile=$secrets
$c | ConvertTo-Json | Set-Content (Join-Path $state 'host.json') -Encoding UTF8
$e=Get-Content "$PSScriptRoot/client-environment.example.json" -Raw | ConvertFrom-Json
$e.zeroTierNetworkId=$NetworkId;$e.hostOverlayAddress=$HostAddress;$e.releaseId=$ReleaseId
$e.apiBaseUrl="http://${HostAddress}:5081";$e.hotUpdateBaseUrl=$e.apiBaseUrl+'/hotupdate'
$e | ConvertTo-Json | Set-Content (Join-Path $state 'client-environment.json') -Encoding UTF8
Write-Output 'Configuration created. No services started or firewall rules changed.'
