param([ValidatePattern('^(-[a-zA-Z0-9]+)?$')][string]$PackageSuffix='',[string]$ReleaseRoot,[switch]$SkipHostZip,[switch]$SkipAllZip,[string]$ReportPath)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=Join-Path $project 'Builds/PrivateInvitations/PreviewV1'
if($ReleaseRoot){$root=[IO.Path]::GetFullPath($ReleaseRoot)}
$dist=Join-Path $project 'Builds/Distributions/PreviewV1'
$release=Get-Content "$root/release-manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$expected=@{}
foreach($f in $release.files){
 $actual=(Get-FileHash -LiteralPath (Join-Path $root $f.path)).Hash.ToLowerInvariant()
 if($actual -ne $f.sha256){throw ('Release hash mismatch: '+$f.path)}
 $expected[$f.path]=$actual
}
foreach($name in @('release-manifest.json','Api/hotupdate/maps-manifest.json')){$expected[$name]=(Get-FileHash -LiteralPath (Join-Path $root $name)).Hash.ToLowerInvariant()}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archives=@()
$roles=if($SkipAllZip){@()}elseif($SkipHostZip){@('Client')}else{@('Client','Host')}
if($SkipAllZip){
 foreach($role in @('Client','Host')){
  if(Test-Path -LiteralPath (Join-Path $dist ('PreviewV1-'+$role+'-Win64'+$PackageSuffix+'.zip'))){throw 'Local-only revision unexpectedly has a distribution ZIP.'}
 }
}
foreach($role in $roles){
 $zip=Join-Path $dist ('PreviewV1-'+$role+'-Win64'+$PackageSuffix+'.zip')
 $archive=[IO.Compression.ZipFile]::OpenRead($zip)
 $count=0
 try{
  foreach($entry in $archive.Entries){
   if(-not $entry.Name){continue}
   $relative=$entry.FullName.Replace('\','/')
   $key=if($role -eq 'Client'){'Client/'+$relative}else{$relative}
   if(-not $expected.ContainsKey($key)){throw ('Unsealed archive entry: '+$key)}
   $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
   try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
   if($hash -ne $expected[$key]){throw ('Archive content mismatch: '+$key)}
   $count++
  }
  $wanted=if($role -eq 'Client'){@($expected.Keys | Where-Object { $_.StartsWith('Client/') }).Count}else{$expected.Count}
  if($wanted -ne $count){throw ('Archive missing files: '+$role)}
 }finally{$archive.Dispose()}
 $archives += [pscustomobject]@{role=$role;files=$count;bytes=(Get-Item $zip).Length;sha256=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant();allEntriesHashMatched=$true}
}
$client=Get-Content "$root/Client/build-manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$server=Get-Content "$root/Server/build-manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
if($client.inputDigest -ne $server.inputDigest -or $client.inputDigest -ne $release.inputDigest){throw 'Input digest mismatch.'}
$normal=Get-Content "$root/Api/hotupdate/manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$maps=Get-Content "$root/Api/hotupdate/maps-manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
if($normal.releaseId -ne 'PreviewV1' -or $maps.releaseId -ne 'PreviewV1' -or $normal.version -ne $maps.version -or $maps.protocolId -ne $release.protocolId){throw 'Hot-update identity mismatch.'}
if(@($maps.files | Where-Object path -notlike 'maps/*.bundle').Count -or $maps.files.Count -ne 2){throw 'Unexpected map manifest.'}
$result=[ordered]@{releaseId=$release.releaseId;protocolId=$release.protocolId;inputDigest=$release.inputDigest;hotUpdateVersion=$maps.version;checkedUtc=[DateTime]::UtcNow.ToString('o');verification='static directory and optional archive hashes only; no game or server launched';localDirectoryOnly=[bool]$SkipAllZip;directoryFilesChecked=$expected.Count;allDirectoryHashesMatched=$true;archives=$archives}
if(-not $ReportPath){$ReportPath="$project/Logs/PreviewV1/package-verification.json"}
$result | ConvertTo-Json -Depth 6 | Set-Content $ReportPath -Encoding UTF8
$result | ConvertTo-Json -Depth 6
