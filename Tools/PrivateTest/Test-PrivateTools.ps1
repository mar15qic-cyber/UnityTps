$ErrorActionPreference='Stop'
. "$PSScriptRoot/Private.Common.ps1"
$tmp=Join-Path ([IO.Path]::GetTempPath()) ('fps-private-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
$c=Get-Content "$PSScriptRoot/private-host.example.json" -Raw | ConvertFrom-Json
$c.networkId='743993800fd77ce1';$c.publicAddress='10.16.41.231';$c.dsBindAddress=$c.publicAddress
$path=Join-Path $tmp 'config.json';$c | ConvertTo-Json | Set-Content $path -Encoding UTF8
$valid=Read-PrivateConfig $path
if($valid.publicAddress -ne '10.16.41.231'){throw 'Valid address rejected.'}
foreach($bad in @('127.0.0.1','8.141.92.231','0.0.0.0','::1')){
 $c.publicAddress=$bad;$c.dsBindAddress=$bad;$c | ConvertTo-Json | Set-Content $path -Encoding UTF8
 $rejected=$false;try{Read-PrivateConfig $path | Out-Null}catch{$rejected=$true}
 if(-not $rejected){throw 'Invalid address accepted.'}
}
$errors=@();Get-ChildItem $PSScriptRoot -Filter '*.ps1' | ForEach-Object {$tokens=$null;$parse=$null;[Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$parse)|Out-Null;$errors+=$parse}
if($errors.Count){throw ($errors | Out-String)}
Write-Output 'PRIVATE_TOOL_CHECKS_PASSED: valid config, 4 rejected addresses, all PowerShell parsed. No network changes.'
$records=@('[{"name":"api"},{"name":"arena"}]' | ConvertFrom-Json | ForEach-Object { $_ })
if($records.Count -ne 2){throw 'Process JSON was not flattened.'}
$pool=[pscustomobject]@{instances=@(
 [pscustomobject]@{instanceId='historical-test';state='InMatch';currentPlayers=1;fresh=$false},
 [pscustomobject]@{instanceId='invite-arena';state='Ready';currentPlayers=0;fresh=$true})}
if(@(Get-PrivateBusyInstances $pool $records).Count){throw 'Historical foreign instance blocked draining.'}
$pool.instances[1].state='InMatch';$pool.instances[1].fresh=$false
if(@(Get-PrivateBusyInstances $pool $records).Count -ne 1){throw 'Owned stale match was not protected.'}
Write-Output 'PRIVATE_DRAIN_TESTS_PASSED: process enumeration, foreign history ignored, owned stale matches protected.'
