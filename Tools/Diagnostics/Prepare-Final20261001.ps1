$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=Join-Path $project 'Builds/Distributions/PreviewV1/PreviewV1-Host-Win64-Final20261001'
if(Test-Path -LiteralPath $root){throw 'Final output already exists; never overwrite a sealed final package.'}
$client=Get-Content (Join-Path $project 'Builds/ReleaseClient/build-manifest.json') -Raw | ConvertFrom-Json
$server=Get-Content (Join-Path $project 'Builds/Server/build-manifest.json') -Raw | ConvertFrom-Json
if($client.protocolId -ne 'fps-net-v25' -or $client.protocolId -ne $server.protocolId -or $client.inputDigest -ne $server.inputDigest){throw 'Installed build pair does not match.'}
New-Item -ItemType Directory -Path $root | Out-Null
foreach($role in @('Client','Server')){
 $source=Join-Path $project $(if($role -eq 'Client'){'Builds/ReleaseClient'}else{'Builds/Server'})
 $app=if($role -eq 'Client'){'UnityFpsClient'}else{'UnityFpsDedicatedServer'}
 $target=Join-Path $root $role
 New-Item -ItemType Directory -Path $target | Out-Null
 foreach($name in @(($app+'.exe'),'UnityPlayer.dll','UnityCrashHandler64.exe','FishNet.SDK.Id','build-manifest.json')){
  $file=Join-Path $source $name
  if(Test-Path -LiteralPath $file){Copy-Item -LiteralPath $file -Destination $target}
 }
 Copy-Item -LiteralPath (Join-Path $source ($app+'_Data')) -Destination $target -Recurse
 $manifest=Get-Content (Join-Path $target 'build-manifest.json') -Raw | ConvertFrom-Json
 $manifest | Add-Member -NotePropertyName releaseId -NotePropertyValue 'PreviewV1' -Force
 $manifest | Add-Member -NotePropertyName packageRevision -NotePropertyValue 'Final20261001' -Force
 $manifest | Add-Member -NotePropertyName isFinal -NotePropertyValue $true -Force
 $manifest | Add-Member -NotePropertyName telemetryDefaultEnabled -NotePropertyValue $false -Force
 $manifest | Add-Member -NotePropertyName telemetryEnabledByLauncher -NotePropertyValue $true -Force
 $manifest | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $target 'build-manifest.json') -Encoding utf8
}
$apiTarget=Join-Path $root 'Api'
New-Item -ItemType Directory -Path $apiTarget | Out-Null
$apiSource=Join-Path $project 'fps-backend/src/UnityFps.Api/bin/Debug/net8.0'
foreach($file in Get-ChildItem -LiteralPath $apiSource -File){
 if($file.Extension -in @('.dll','.exe') -or $file.Name -in @('UnityFps.Api.deps.json','UnityFps.Api.runtimeconfig.json')){Copy-Item -LiteralPath $file.FullName -Destination $apiTarget}
}
if(Test-Path -LiteralPath (Join-Path $apiSource 'runtimes')){Copy-Item -LiteralPath (Join-Path $apiSource 'runtimes') -Destination $apiTarget -Recurse}
@{Database=@{AllowInMemoryFallback=$false};Access=@{InviteOnly=$false}} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $apiTarget 'appsettings.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $project 'Builds/Distributions/PreviewV1/PreviewV1-Host-Win64-r5/Client/client-environment.json') -Destination (Join-Path $root 'Client/client-environment.json')
@'
PreviewV1 — Final20261001
本轮最终朋友测试版。Windows 64位。
基于2026-09-30 22:35的现有客户端，协议fps-net-v25，热更25。
完整解压后双击“开始游戏.cmd”，使用既有ZeroTier测试网络。
不要单独移动游戏EXE。组织者必须启动本版匹配的主机。
'@ | Set-Content (Join-Path $root 'Client/最终版本.txt') -Encoding utf8
@'
# PreviewV1 Final20261001

用户于2026-10-01指定当前包为本轮最终版。
游戏客户端和服务器沿用已安装二进制，不重建游戏、不修改玩法。
客户端buildId=f4f4cc5a4e53，服务器buildId=e407c8d03b45，fps-net-v25，热更25。
只将PreviewV1-Client-Win64-Final20261001.zip发给朋友。此主机目录留给组织者。
主机API为当前.NET 8构建，组织者电脑需安装.NET 8运行时；启动助手自身为独立运行包。
旧分发包保留；请使用匹配的最终主机，不能把新客户端连接旧协议主机。
封存为最终版是发行选择，不把此前未验证的玩法项改称测试通过。
'@ | Set-Content (Join-Path $root '最终版本.md') -Encoding utf8

# Adapt only this final packaging invocation. The existing r1-r5 packaging tool stays unchanged.
$package=Get-Content (Join-Path $project 'Tools/PrivateTest/Package-PreviewV1.ps1') -Raw
$old=' -or -not $cm.telemetryDefaultEnabled -or -not $sm.telemetryDefaultEnabled'
if(-not $package.Contains($old)){throw 'Packaging template changed; review its manifest gate.'}
$package=$package.Replace($old,'')
$package=$package.Replace('Temp/PreviewV1Launcher','Temp/Final20261001Launcher')
$package=$package.Replace('$PSScriptRoot/$name.ps1','$project/Tools/PrivateTest/$name.ps1')
$package=$package.Replace('$PSScriptRoot/.runtime/host.json','$project/Tools/PrivateTest/.runtime/host.json')
$package=$package.Replace('fps-net-v23','fps-net-v25')
$package=$package.Replace('结构化数据仍默认开启','结构化数据需要显式启用；本包启动助手与主机脚本已启用')
$package=$package.Replace("@{releaseId='PreviewV1';packageRevision=", "@{isFinal=`$true;finalizedAtUtc=[DateTime]::UtcNow.ToString('o');releaseId='PreviewV1';packageRevision=")
$package=$package.Replace("Get-ChildItem `$distribution -Filter '*.zip'", "Get-Item -LiteralPath (Join-Path `$distribution 'PreviewV1-Client-Win64-Final20261001.zip')")
$package=$package.Replace('$distribution/SHA256SUMS.txt','$distribution/SHA256SUMS-Final20261001.txt')
$package=$package.Replace('Copy-Item "$root/主机说明.md" $distribution -Force','Copy-Item "$root/主机说明.md" (Join-Path $distribution ''Final20261001-主机说明.md'')')
$package=$package.Replace('Copy-Item "$root/Client/玩家说明.txt" $distribution -Force','Copy-Item "$root/Client/玩家说明.txt" (Join-Path $distribution ''Final20261001-玩家说明.txt'')')
$packagePath=Join-Path $project 'Tools/Diagnostics/Package-Final20261001.ps1'
Set-Content -LiteralPath $packagePath -Value $package -Encoding utf8
$tokens=$null;$errors=$null
[Management.Automation.Language.Parser]::ParseFile($packagePath,[ref]$tokens,[ref]$errors) | Out-Null
if($errors.Count){throw 'Final packaging adaptation has syntax errors.'}
Write-Output ('FINAL_STAGE_READY '+$root)
