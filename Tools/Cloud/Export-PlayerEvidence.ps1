param([Parameter(Mandatory)][string]$EvidenceDirectory,[Parameter(Mandatory)][string]$OutputZip)
$ErrorActionPreference='Stop'
# Only our fixed-schema JSONL telemetry is eligible, not Player.log or API logs containing login data.
$allowed=@('matchId','runId','kind','sessionId','releaseId','map','reason','time','displayTick','usedTick','rtt','frameMs','error','rawError','renderTick','bufferMs','serverTick','inputTick','shotId','lifeEpoch','connection','targetConnection','queueDepth',
 'utc','role','protocolId','transport','networkState','statisticsScope','schemaVersion','processId','connectedPeers','socketEpoch','packetsSent','packetsReceived','bytesSent','bytesReceived','packetsLost','packetLossPercent','fps','maxFrameMs','sampleSeconds','statisticsAvailable',
 'weaponId','collider','commandId','equipmentCommandId','shotSeconds','seed','damage','healthAfter','targetObjectId','pelletCount','ads','spread','origin','aimDirection','firedDirection','hitPoint')
$stage=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputZip))) ('evidence-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($f in Get-ChildItem -LiteralPath $EvidenceDirectory -File -Filter '*.jsonl' -Recurse) {
    # Stream rotated files instead of materializing hours of per-shot telemetry in memory.
    $output=Join-Path $stage ([guid]::NewGuid().ToString('N')+'-'+$f.Name)
    $stream=[IO.File]::Open($f.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=[IO.StreamReader]::new($stream)
    $writer=[IO.StreamWriter]::new($output,$false,[Text.UTF8Encoding]::new($false))
    try { while(($line=$reader.ReadLine()) -ne $null) {
        if (-not $line.Trim()) { continue }
        try { $record=$line | ConvertFrom-Json } catch { if($reader.EndOfStream){continue}; throw }
        if (-not $record.kind -or -not $record.sessionId) { throw 'Invalid telemetry record.' }
        $filtered=[ordered]@{}
        foreach ($key in $allowed) { if ($record.PSObject.Properties.Name -contains $key) { $filtered[$key]=$record.$key } }
        $writer.WriteLine(($filtered | ConvertTo-Json -Depth 5 -Compress))
    } } finally { $reader.Dispose(); $writer.Dispose() }
}
if (@(Get-ChildItem -LiteralPath $stage -File).Count -eq 0) { throw 'No telemetry files. Launch client with -publicTestTelemetry first.' }
Compress-Archive -LiteralPath $stage -DestinationPath $OutputZip
Write-Output ('EVIDENCE_EXPORTED '+$OutputZip)
