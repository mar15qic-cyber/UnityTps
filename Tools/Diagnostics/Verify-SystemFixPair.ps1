$ErrorActionPreference = 'Stop'
$fixProject = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixEvidence = Join-Path $fixProject 'Logs/SystemFix0927'
$fixExpected = (Get-Content (Join-Path $fixEvidence 'input-digest-before.txt') -Raw).Trim()
$fixRows = [Collections.Generic.List[object]]::new()
foreach ($fixRole in @('Server','Client')) {
    foreach ($fixVariant in @('release','audit')) {
        $fixRelative = if ($fixVariant -eq 'audit') { 'Builds/SystemFix0927/'+$fixRole } elseif ($fixRole -eq 'Server') { 'Builds/Server' } else { 'Builds/ReleaseClient' }
        $fixFolder = Join-Path $fixProject $fixRelative
        $fixExe = if ($fixRole -eq 'Server') { 'UnityFpsDedicatedServer' } else { 'UnityFpsClient' }
        $fixManaged = Join-Path $fixFolder ($fixExe+'_Data/Managed')
        $fixManifest = Get-Content (Join-Path $fixFolder 'build-manifest.json') -Raw | ConvertFrom-Json
        $fixHash = (Get-FileHash (Join-Path $fixManaged 'Game.Gameplay.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        $fixDriver = Test-Path (Join-Path $fixManaged 'Game.RuntimeAudit.dll')
        if ($fixManifest.inputDigest -ne $fixExpected -or $fixManifest.protocolId -ne 'fps-net-v23' -or $fixHash -ne $fixManifest.gamePlayDllSha256) { throw ('Build identity mismatch: '+$fixRelative) }
        if ($fixDriver -ne ($fixVariant -eq 'audit')) { throw ('Wrong audit assembly boundary: '+$fixRelative) }
        $fixRows.Add([pscustomobject]@{variant=$fixVariant;role=$fixRole;protocol=$fixManifest.protocolId;inputDigest=$fixManifest.inputDigest;buildId=$fixManifest.buildId;builtAt=$fixManifest.builtAtUtc;gameplayHashMatches=$true;hasAuditAssembly=$fixDriver})
    }
}
$fixRows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $fixEvidence 'alignment.json') -Encoding utf8

Add-Type -Path 'E:/Unity6/Editor/Data/Managed/Unity.Cecil.dll'
$fixReferences = @((Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll').FullName) + @('E:/Unity6/Editor/Data/Managed/Unity.Cecil.dll')
Add-Type -ReferencedAssemblies $fixReferences -TypeDefinition @'
using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Mono.Cecil;
public static class AuditIlIdentity {
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types) {
        foreach(var t in types) { yield return t; foreach(var n in Types(t.NestedTypes)) yield return n; }
    }
    public static string Text(string path) {
        using(var a = AssemblyDefinition.ReadAssembly(path)) {
            var s = new StringBuilder();
            foreach(var t in Types(a.MainModule.Types).OrderBy(x => x.FullName, StringComparer.Ordinal))
                foreach(var m in t.Methods.OrderBy(x => x.FullName, StringComparer.Ordinal)) {
                    s.AppendLine(m.FullName);
                    if(!m.HasBody) continue;
                    s.AppendLine(m.Body.InitLocals.ToString());
                    foreach(var v in m.Body.Variables) s.AppendLine(v.VariableType.FullName);
                    foreach(var i in m.Body.Instructions) s.AppendLine(i.ToString());
                    foreach(var e in m.Body.ExceptionHandlers)
                        s.AppendLine(e.HandlerType+"|"+e.CatchType+"|"+e.TryStart+"|"+e.TryEnd+"|"+e.HandlerStart+"|"+e.HandlerEnd+"|"+e.FilterStart);
                }
            return s.ToString();
        }
    }
}
'@
$fixIlRows = [Collections.Generic.List[object]]::new()
foreach ($fixRole in @('Server','Client')) {
    $fixExe = if ($fixRole -eq 'Server') { 'UnityFpsDedicatedServer' } else { 'UnityFpsClient' }
    $fixRelease = if ($fixRole -eq 'Server') { 'Builds/Server' } else { 'Builds/ReleaseClient' }
    foreach ($fixAssembly in @('Game.Gameplay.dll','Game.Presentation.dll','Game.Core.dll')) {
        $fixTexts = foreach ($fixRelative in @($fixRelease,('Builds/SystemFix0927/'+$fixRole))) {
            [AuditIlIdentity]::Text((Join-Path $fixProject ($fixRelative+'/'+$fixExe+'_Data/Managed/'+$fixAssembly)))
        }
        $fixHashes = foreach ($fixText in $fixTexts) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fixText))).ToLowerInvariant() }
        if ($fixHashes[0] -ne $fixHashes[1]) { throw ('Gameplay IL differs between release and audit: '+$fixRole+'/'+$fixAssembly) }
        $fixIlRows.Add([pscustomobject]@{role=$fixRole;assembly=$fixAssembly;releaseIL=$fixHashes[0];auditIL=$fixHashes[1];methodBodiesEqual=$true})
    }
}
$fixIlRows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $fixEvidence 'gameplay-il-equivalence.json') -Encoding utf8
$fixRows | Select-Object variant,role,protocol,buildId,gameplayHashMatches,hasAuditAssembly
Write-Output 'ALL_FOUR_IDENTITIES_AND_SIX_IL_COMPARISONS_PASSED'
