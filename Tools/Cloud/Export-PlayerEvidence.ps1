param([Parameter(Mandatory)][string]$EvidenceDirectory,[Parameter(Mandatory)][string]$OutputZip)
$ErrorActionPreference='Stop'
# Only our fixed-schema JSONL telemetry is eligible, not Player.log or API logs containing login data.
$allowed=@('matchId','runId','kind','sessionId','releaseId','map','reason','time','displayTick','usedTick','rtt','frameMs','error','rawError','renderTick','bufferMs','serverTick','inputTick','shotId','lifeEpoch','connection','targetConnection','queueDepth')
$stage=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputZip))) ('evidence-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($f in Get-ChildItem -LiteralPath $EvidenceDirectory -File -Filter '*.jsonl') {
    $clean=@(foreach ($line in Get-Content -LiteralPath $f.FullName) {
        if (-not $line.Trim()) { continue }
        $record=$line | ConvertFrom-Json
        if (-not $record.kind -or -not $record.sessionId) { throw 'Invalid telemetry record.' }
        $filtered=[ordered]@{}
        foreach ($key in $allowed) { if ($record.PSObject.Properties.Name -contains $key) { $filtered[$key]=$record.$key } }
        $filtered | ConvertTo-Json -Compress
    })
    $clean | Set-Content -LiteralPath (Join-Path $stage $f.Name) -Encoding UTF8
}
if (@(Get-ChildItem -LiteralPath $stage -File).Count -eq 0) { throw 'No telemetry files. Launch client with -publicTestTelemetry first.' }
Compress-Archive -LiteralPath $stage -DestinationPath $OutputZip
Write-Output ('EVIDENCE_EXPORTED '+$OutputZip)
