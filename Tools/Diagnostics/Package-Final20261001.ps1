param([string]$HotUpdatePath,[ValidatePattern('^(-[a-zA-Z0-9]+)?$')][string]$PackageSuffix='',
 [string]$ReleaseRoot,[switch]$SkipHostZip,[switch]$SkipAllZip)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=Join-Path $project 'Builds/PrivateInvitations/PreviewV1'
if($ReleaseRoot){$root=[IO.Path]::GetFullPath($ReleaseRoot)}
if(-not $root.StartsWith([IO.Path]::GetFullPath((Join-Path $project 'Builds'))+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Release root must stay inside project Builds.'}
$distribution=Join-Path $project 'Builds/Distributions/PreviewV1'
if(-not $HotUpdatePath){
 $latest=Get-ChildItem -LiteralPath (Join-Path $project 'Logs/HotUpdate/releases') -Directory |
  Where-Object {$_.Name -match '^\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'maps-manifest.json'))} |
  Sort-Object {[long]$_.Name} -Descending | Select-Object -First 1
 if(-not $latest){throw 'No staged map release. Run Publish-HotUpdate.ps1 after rebuilding client map bundles.'}
 $HotUpdatePath=$latest.FullName
}
$hotSource=if([IO.Path]::IsPathRooted($HotUpdatePath)){[IO.Path]::GetFullPath($HotUpdatePath)}else{[IO.Path]::GetFullPath((Join-Path $project $HotUpdatePath))}
$mapBundles=@(Get-ChildItem -LiteralPath $hotSource -Recurse -File -Filter '*.bundle')
if($mapBundles.Count -eq 0){throw 'Preview package requires validated map bundles.'}
$python=Get-Command python -ErrorAction Stop
$mapBundlePaths=@($mapBundles | ForEach-Object {$_.FullName})
& $python.Source (Join-Path $project 'Tools/HotUpdate/Validate-MapBundles.py') @mapBundlePaths
if($LASTEXITCODE -ne 0){throw 'Map bundle shader validation failed; packaging stopped before changing the release.'}
$cm=Get-Content "$root/Client/build-manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$sm=Get-Content "$root/Server/build-manifest.json" -Raw -Encoding UTF8 | ConvertFrom-Json
if($cm.releaseId -ne 'PreviewV1' -or $sm.releaseId -ne 'PreviewV1' -or $cm.inputDigest -ne $sm.inputDigest -or $cm.protocolId -ne $sm.protocolId){throw 'Preview build pair mismatch.'}
# Unity's script-only build cache can retain an excluded diagnostic assembly name.
# Keep the production loader metadata aligned with the actual production DLLs.
foreach($role in @('Client','Server')){
 $app=if($role -eq 'Client'){'UnityFpsClient'}else{'UnityFpsDedicatedServer'}
 $data=Join-Path $root "$role/${app}_Data"
 if(Test-Path -LiteralPath "$data/Managed/Game.RuntimeAudit.dll"){throw 'Runtime audit driver must not be shipped.'}
 $init=Get-Content "$data/RuntimeInitializeOnLoads.json" -Raw | ConvertFrom-Json
 if(@($init.root | Where-Object assemblyName -eq 'Game.RuntimeAudit').Count){throw 'Runtime audit initializer must not be shipped.'}
 $loader=Get-Content "$data/ScriptingAssemblies.json" -Raw | ConvertFrom-Json
 if($loader.names.Count -ne $loader.types.Count){throw 'Assembly loader metadata is inconsistent.'}
 if($loader.names -contains 'Game.RuntimeAudit.dll'){
  $keep=@(0..($loader.names.Count-1) | Where-Object {$loader.names[$_] -ne 'Game.RuntimeAudit.dll'})
  $loader.names=@($keep | ForEach-Object {$loader.names[$_]})
  $loader.types=@($keep | ForEach-Object {$loader.types[$_]})
  $loader | ConvertTo-Json -Depth 5 -Compress | Set-Content "$data/ScriptingAssemblies.json" -Encoding utf8
 }
}
foreach($name in @('mscorlib.dll','System.Private.CoreLib.dll','StartGame.exe')){if(Test-Path -LiteralPath "$root/Client/$name"){throw ('Launcher runtime must be isolated before packaging: '+$name)}}
New-Item -ItemType Directory -Path "$root/Client/Launcher" -Force | Out-Null
Copy-Item "$project/Temp/Final20261001Launcher/*" "$root/Client/Launcher" -Recurse -Force
Copy-Item "$PSScriptRoot/../Cloud/Export-PlayerEvidence.ps1" "$root/Client" -Force
@('@echo off','start "" "%~dp0Launcher\StartGame.exe"') | Set-Content "$root/Client/开始游戏.cmd" -Encoding ASCII
$settings=@{Database=@{AllowInMemoryFallback=$false};Access=@{InviteOnly=$false}} | ConvertTo-Json -Depth 4
Get-ChildItem "$root/Api" -Filter 'appsettings*.json' | ForEach-Object { Set-Content -LiteralPath $_.FullName -Value $settings -Encoding UTF8 }
$hotTarget=[IO.Path]::GetFullPath((Join-Path $root 'Api/hotupdate'))
if(-not $hotTarget.StartsWith([IO.Path]::GetFullPath($root)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Hot-update destination escaped release.'}
if(Test-Path -LiteralPath $hotTarget){Remove-Item -LiteralPath $hotTarget -Recurse -Force}
Copy-Item -LiteralPath $hotSource -Destination $hotTarget -Recurse
foreach($name in @('manifest.json','maps-manifest.json')){
 $manifest=Get-Content "$hotTarget/$name" -Raw -Encoding UTF8 | ConvertFrom-Json
 $manifest | Add-Member -NotePropertyName releaseId -NotePropertyValue 'PreviewV1' -Force
 $manifest | Add-Member -NotePropertyName protocolId -NotePropertyValue $sm.protocolId -Force
 foreach($f in $manifest.files){
  $path=[IO.Path]::GetFullPath((Join-Path "$hotTarget/$($manifest.version)" $f.path))
  if(-not $path.StartsWith($hotTarget+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path).Hash -ne $f.hash){throw 'Hot-update content hash mismatch.'}
 }
 $manifest | ConvertTo-Json -Depth 12 | Set-Content "$hotTarget/$name" -Encoding UTF8
}
foreach($group in @('PrivateTest','Cloud')){New-Item -ItemType Directory -Path "$root/HostTools/$group" -Force | Out-Null}
foreach($name in @('Private.Common','Start-PrivateHost','Stop-PrivateHost','Initialize-BaseMapCatalog','Restart-AdditionalMaps','Set-PrivateFirewall')){Copy-Item "$project/Tools/PrivateTest/$name.ps1" "$root/HostTools/PrivateTest" -Force}
foreach($name in @('Cloud.Common','Start-CloudServer','Stop-CloudServer','Test-CloudReadiness','Export-PlayerEvidence')){Copy-Item "$PSScriptRoot/../Cloud/$name.ps1" "$root/HostTools/Cloud" -Force}
@'
param([string]$SecretsPath='E:\UnityProject\UnityFpsLowPoly\Tools\PrivateTest\.runtime\secrets.json',[switch]$ConfigureOnly)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $SecretsPath -PathType Leaf)){throw 'Provide the existing private host secrets file with -SecretsPath. Never send that file to players.'}
$config=Get-Content "$PSScriptRoot/host-template.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$config.releaseRoot=$PSScriptRoot
$config.stateRoot=Join-Path $PSScriptRoot 'HostState'
$config.secretsFile=[IO.Path]::GetFullPath($SecretsPath)
New-Item -ItemType Directory -Path $config.stateRoot -Force | Out-Null
$path=Join-Path $config.stateRoot 'host.json'
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding UTF8
if($ConfigureOnly){Write-Output ('HOST_CONFIGURED '+$path);exit}
& "$PSScriptRoot/HostTools/PrivateTest/Start-PrivateHost.ps1" -ConfigPath $path
'@ | Set-Content "$root/Start-PreviewHost.ps1" -Encoding UTF8
@'
$ErrorActionPreference='Stop'
& "$PSScriptRoot/HostTools/PrivateTest/Stop-PrivateHost.ps1" -ConfigPath "$PSScriptRoot/HostState/host.json"
'@ | Set-Content "$root/Stop-PreviewHost.ps1" -Encoding UTF8
@'
$ErrorActionPreference='Stop'
$output=Join-Path $PSScriptRoot ('PreviewV1-HostEvidence-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.zip')
& "$PSScriptRoot/HostTools/Cloud/Export-PlayerEvidence.ps1" -EvidenceDirectory "$PSScriptRoot/HostState/logs" -OutputZip $output
'@ | Set-Content "$root/Export-HostEvidence.ps1" -Encoding UTF8
foreach($action in @('Start-PreviewHost','Stop-PreviewHost','Export-HostEvidence')){
 @('@echo off',('powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0'+$action+'.ps1"'),'pause') | Set-Content "$root/$action.cmd" -Encoding ASCII
}
$template=Get-Content "$project/Tools/PrivateTest/.runtime/host.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$template.releaseId='PreviewV1';$template.releaseRoot='SET_BY_START_SCRIPT';$template.stateRoot='SET_BY_START_SCRIPT';$template.secretsFile='SET_BY_START_SCRIPT'
$template | ConvertTo-Json -Depth 5 | Set-Content "$root/host-template.json" -Encoding UTF8
@'
PreviewV1 好友联机测试（Windows 64 位）

1. 完整解压到可写文件夹，双击“开始游戏.cmd”，不要在压缩包里运行。
2. 首次按启动助手提示安装官方 ZeroTier，允许加入测试网络的管理员请求。
3. 将 ZeroTier 设备编号发给组织者，等待组织者批准，然后再次点击“开始游戏”。
4. 登录页可自行创建账号（密码至少 8 位）。等待地图更新完成，再创建或加入房间。
5. 组织者必须开着 PreviewV1 主机。看到版本不兼容时请联系组织者，勿自行修改配置。

反馈问题：退出游戏后，在启动助手点击“导出测试记录”，等 ZIP 生成后发给组织者。
同时说明：发生时间、地图、自己的游戏昵称、问题表现。截图或录像也很有帮助。

每次启动自动保留独立日志，不会覆盖上一局：
%LOCALAPPDATA%\UnityFps\PreviewV1\Logs\<本次运行编号>\client.log
%LOCALAPPDATA%\UnityFps\PreviewV1\Logs\<本次运行编号>\Telemetry\*.jsonl
将上面的 %LOCALAPPDATA%\UnityFps\PreviewV1\Logs 粘贴到资源管理器地址栏即可打开。
网络数据每秒采集一次；射击、命中、同步纠正等事件另外记录。日志只写在本机，不自动上传。
导出按钮导出经过字段筛选的网络/对局记录，不包含原始 client.log。原始异常堆栈保留在本地。
不要发送密码或登录凭据；需要原始 client.log 时先检查内容再单独提供。
日志不会自动删除，长时间测试后可手动归档；请预留磁盘空间。
请始终使用“开始游戏”，直接运行 UnityFpsClient.exe 会使用另一处默认日志目录。
本机同时开服和游玩：保持主机运行，再打开同一主机包 Client 文件夹中的“开始游戏.cmd”。无需再启动另一套服务器或 Unity 编辑器。
请保留 Launcher 子文件夹及整个游戏目录，勿将里面的文件搬到游戏 EXE 旁。
'@ | Set-Content "$root/Client/玩家说明.txt" -Encoding UTF8
@'
# PreviewV1 主机使用说明

此包给组织者；只把最新的 PreviewV1 客户端 ZIP 发给朋友。主机包含客户端、六地图专用服务器、API 与开停服工具。
使用既有 ZeroTier 网络 743993800fd77ce1，主机地址 10.16.41.231，TCP 5081，UDP 7770–7775，API 管理监听 127.0.0.1:5080。每张地图的服务器容量为 8。

## 开服

1. 完整解压到可写目录。确保本机 ZeroTier 在线且地址为 10.16.41.231，原有数据库服务可用。
2. 先让玩家退出，并通过原有管理工具停止旧版 API/六地图 DS。新脚本检测端口被其他进程占用时会拒绝启动，不会强杀旧服务。
   当前开发机的本地栈由 E:\UnityProject\UnityFpsLowPoly\Tools\Server\Stop-LocalServer.ps1 管理；确认无人游玩后，可在 PowerShell 执行该脚本停服。
3. 原开发电脑可双击 Start-PreviewHost.cmd。默认复用 E:\UnityProject\UnityFpsLowPoly\Tools\PrivateTest\.runtime\secrets.json；包内不含任何密钥。
   其他位置的密钥用 PowerShell 指定：
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-PreviewHost.ps1 -SecretsPath "D:\Private\secrets.json"
4. 等待 PRIVATE_HOST_READY。在 ZeroTier 管理页面批准朋友设备。朋友使用 PreviewV1 客户端连接，自行注册游戏账号。
5. 停服双击 Stop-PreviewHost.cmd；有对局时会先关闭新准入，等待玩家离开后再执行一次。
6. 主机本人游玩：保持主机运行，双击本包 Client\开始游戏.cmd，使用自己的游戏账号登录即可。服务器和游戏窗口可以在同一台电脑运行。

若尚未配置 ZeroTier 防火墙规则，先执行 Start-PreviewHost.ps1 -ConfigureOnly，
再在管理员 PowerShell 运行：
powershell -NoProfile -ExecutionPolicy Bypass -File .\HostTools\PrivateTest\Set-PrivateFirewall.ps1 -ConfigPath .\HostState\host.json
随后再运行 Start-PreviewHost.cmd。规则限定在 ZeroTier 接口与子网，不需要公网端口转发。
主机网络地址若变更，需要同时生成对应的客户端配置并重新分发，不能只改主机地址。

## 日志

客户端：%LOCALAPPDATA%\UnityFps\PreviewV1\Logs\<运行编号>\，含 client.log 与 Telemetry。
主机：本包 HostState\logs\<启动时间-编号>\，含 API 标准输出/错误、每张地图 Unity 日志、<地图ID>\Telemetry\*.jsonl。
双击 Export-HostEvidence.cmd 导出主机结构化网络数据；朋友用启动助手“导出测试记录”。原始 Unity/API 日志持续保留本地，不自动上传。

结构化数据包括：UTC、版本、协议、角色、进程、会话、运行、对局、地图、连接事件、每秒收发字节/包数、RTT、传输层丢包计数、FPS/平均与最大帧耗时，
以及已有的输入/服务端 tick、显示 tick、插值缓冲、位置纠正、射击参数、命中/伤害与拒绝原因等事件字段。
通过 matchId 关联同局，通过 sessionId 区分客户端/服务端会话；同机不同运行不会覆盖日志。统计字段只对对应 kind 有意义，其他事件的默认零值不代表测量结果。
packets/bytes/loss 是累计值，可按相邻样本差值计算速率；socketEpoch 变化表示 socket 计数重置，peer 重连也须分段。
statisticsAvailable=false 表示还没有可用 socket 统计，不能解释成零丢包。
packetLossPercent 来自 LiteNetLib 的可靠传输丢失计数/发送包数，包含重传相关检测，不等同于独立抓包测得的全链路丢包率；未采集网络载荷或 ZeroTier 直连/中继判定。
日志约每秒刷新、每 64 MiB 分段，正常退出会关闭文件；强制结束或掉电可能丢失最后缓冲数据。日志不自动清理，请定期归档。
直接运行客户端 EXE 时，结构化数据需要显式启用；本包启动助手与主机脚本已启用，但写入 Unity persistentDataPath/PublicTestEvidence，原始 Player.log 可能被下一次运行覆盖，所以请使用启动助手。

## 构建与热更

双端发布标识/显示版本 PreviewV1，协议 fps-net-v25；两端 build-manifest.json 的 inputDigest 必须一致。
本包热更版本以 manifest.json 与 maps-manifest.json 为准，releaseId 为 PreviewV1；地图已检查材质 Shader 引用与清单 SHA256。请整体使用本包，勿混入旧服清单。
打包脚本只做构建产物与文件校验，不自动切换当前服务器。实机测试结果以随版测试报告为准；朋友异地网络环境仍需实际验证。
release-manifest.json 保存静态文件哈希。开始开服后请勿直接重新压缩整个目录发给朋友，它会包含 HostState 与本地运行记录。
'@ | Set-Content "$root/主机说明.md" -Encoding UTF8
# Static syntax validation only: never invoke the packaged startup scripts here.
foreach($file in Get-ChildItem $root -Recurse -Filter '*.ps1'){
 $tokens=$null;$errors=$null
 [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors) | Out-Null
 if($errors.Count){throw ('PowerShell syntax error: '+$file.FullName)}
}
$forbidden=@(Get-ChildItem $root -Recurse -File | Where-Object { $_.Name -match '(?i)(secrets?\.json$|serverkey|\.env$|\.pfx$|\.pem$)' -or $_.FullName -match '[\\/](HostState|\.runtime)[\\/]' })
if($forbidden.Count){throw 'Private runtime files found in release.'}
if(@(Get-ChildItem "$root/Client" -Recurse -File | Where-Object Name -match '(?i)(appsettings|secrets|serverkey)').Count){throw 'Client contains server configuration.'}
$files=@(Get-ChildItem $root -Recurse -File | Where-Object { $_.Name -notin @('release-manifest.json','maps-manifest.json') } | ForEach-Object { @{path=$_.FullName.Substring($root.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
@{isFinal=$true;finalizedAtUtc=[DateTime]::UtcNow.ToString('o');releaseId='PreviewV1';packageRevision=$PackageSuffix.TrimStart('-');protocolId=$sm.protocolId;inputDigest=$sm.inputDigest;files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$root/release-manifest.json" -Encoding UTF8
New-Item -ItemType Directory -Path $distribution -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$parts=if($SkipAllZip){@()}elseif($SkipHostZip){@('Client')}else{@('Client','Host')}
foreach($part in $parts){
 $zip=Join-Path $distribution ('PreviewV1-'+$part+'-Win64'+$PackageSuffix+'.zip')
 if(Test-Path -LiteralPath $zip){throw 'Distribution ZIP exists; preserve it and choose a new release.'}
 $source=if($part -eq 'Client'){Join-Path $root 'Client'}else{$root}
 [IO.Compression.ZipFile]::CreateFromDirectory($source,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
 $archive=[IO.Compression.ZipFile]::OpenRead($zip)
 try{if($archive.Entries.Count -lt 20){throw 'ZIP incomplete.'};Write-Output ($part+' ZIP entries: '+$archive.Entries.Count)}finally{$archive.Dispose()}
}
if(-not $SkipAllZip){
 Get-Item -LiteralPath (Join-Path $distribution 'PreviewV1-Client-Win64-Final20261001.zip') | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name } | Set-Content "$distribution/SHA256SUMS-Final20261001.txt" -Encoding ASCII
 Copy-Item "$root/主机说明.md" (Join-Path $distribution 'Final20261001-主机说明.md')
 Copy-Item "$root/Client/玩家说明.txt" (Join-Path $distribution 'Final20261001-玩家说明.txt')
 Write-Output ('PREVIEW_V1_PACKAGED '+$distribution)
}else{
 Write-Output ('PREVIEW_V1_LOCAL_DIRECTORY_READY '+$root)
}

