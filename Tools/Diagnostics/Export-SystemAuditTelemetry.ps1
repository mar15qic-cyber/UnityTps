param([string[]]$RunNames=@('fix01'),
    [ValidateSet('SystemAudit0927','SystemFix0927')][string]$Campaign='SystemFix0927')
$ErrorActionPreference='Stop'
$auditProject=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$auditSource=Join-Path $env:USERPROFILE 'AppData/LocalLow/DefaultCompany/UnityFps/PublicTestEvidence'
$auditIds=@{}
foreach($auditRunName in $RunNames){
    if($auditRunName -notmatch '^[a-zA-Z0-9_-]+$'){throw 'Invalid run name'}
    $auditIds['audit927-'+$auditRunName]=$auditRunName
}
$auditCopied=[Collections.Generic.List[object]]::new()
foreach($auditFile in Get-ChildItem -LiteralPath $auditSource -Filter '*.jsonl' -File){
    $auditStream=[IO.File]::Open($auditFile.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $auditReader=[IO.StreamReader]::new($auditStream)
    try{$auditFirst=$auditReader.ReadLine() | ConvertFrom-Json}catch{continue}finally{$auditReader.Dispose()}
    if(-not $auditFirst -or -not $auditIds.ContainsKey([string]$auditFirst.runId)){continue}
    $auditDestination=Join-Path $auditProject ('Logs/'+$Campaign+'/'+$auditIds[$auditFirst.runId]+'/telemetry')
    New-Item -ItemType Directory -Path $auditDestination -Force | Out-Null
    Copy-Item -LiteralPath $auditFile.FullName -Destination (Join-Path $auditDestination $auditFile.Name) -Force
    $auditCopied.Add([pscustomobject]@{run=$auditIds[$auditFirst.runId];file=$auditFile.Name;bytes=$auditFile.Length;releaseId=$auditFirst.releaseId})
}
$auditCopied | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $auditProject ('Logs/'+$Campaign+'/telemetry-index.json')) -Encoding utf8
$auditCopied | Group-Object run | Select-Object Name,Count | ConvertTo-Json
