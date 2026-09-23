$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$auditOut = Join-Path $env:TEMP 'fps-net-audit-20260923-direct'
New-Item -ItemType Directory -Path $auditOut -Force | Out-Null
$dotnetRoot = 'C:/Program Files/dotnet'
$sdk = (& dotnet --version).Trim()
$referencePack = Get-ChildItem (Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref') -Directory |
    Where-Object Name -Like '9.*' | Sort-Object Name -Descending | Select-Object -First 1
if (!$referencePack) { throw 'Requires installed .NET 9 reference pack (no network restore).' }
$auditArgs = @('-nologo', '-target:exe', ('-out:' + (Join-Path $auditOut 'BoundaryAudit.dll')))
$auditArgs += Get-ChildItem (Join-Path $referencePack.FullName 'ref/net9.0') -Filter '*.dll' |
    ForEach-Object { '-r:' + $_.FullName }
$auditArgs += @((Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'UnityStubs.cs'))
$auditArgs += @('Movement/MovementPredictionCore.cs', 'Movement/MovementSimulationTypes.cs',
    'Network/ObserverTimeline.cs', 'Weapon/WeaponRuntime.cs') |
    ForEach-Object { Join-Path $repo ('Assets/_Project/Scripts/Gameplay/' + $_) }
& dotnet (Join-Path $dotnetRoot "sdk/$sdk/Roslyn/bincore/csc.dll") @auditArgs
if ($LASTEXITCODE -ne 0) { throw 'Audit compilation failed.' }
'{"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"}}}' |
    Set-Content (Join-Path $auditOut 'BoundaryAudit.runtimeconfig.json') -Encoding utf8
& dotnet (Join-Path $auditOut 'BoundaryAudit.dll') |
    Tee-Object -FilePath (Join-Path $PSScriptRoot 'results.txt')
if ($LASTEXITCODE -ne 0) { throw 'Audit assertions failed.' }
