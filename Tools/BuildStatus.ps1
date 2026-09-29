# BuildStatus.ps1 - 测试前构建身份核对（2026-09-19 测试收口闸；F16 加固 2026-09-19 审计轮）
#
# 解决的实机痛点：连续多轮实测测到旧构建（22:42 / 00:55 / 00:56 三轮全测了 09-18 06:37 的
# 旧 DLL），测试者无法判断"这一轮是在测修改前还是修改后"。
#
# 本脚本一次回答四个问题：
#   1) 磁盘上的客户端构建是哪个（buildId + 本地构建时刻）
#   2) 磁盘上的服务器构建是哪个
#   3) 源码是否比构建新（STALE = 你这轮测试测不到最新修改）
#   4) 运行中的 DS（含四个地图实例）/ 最近一次启动的客户端分别是什么身份（PID 存活核验）
#
# F16（2026-09-19 审计）加固：
#   - 门禁失败关闭（fail closed）：清单缺字段（protocolId/builtAtUtc 无法解析/gamePlayDllSha256
#     缺失/缺 inputDigest）一律按 STALE 阻止，不再"没比较出差异就 OK"；
#   - 产物 DLL 完整性：两端 Game.Gameplay.dll 的磁盘 SHA256 必须等于清单记录；
#   - 输入内容摘要：清单 inputDigest = 构建输入集合（Assets (including meta)/Packages manifest+lock/
#     ProjectVersion）逐文件内容摘要。-Gate 模式重算当前源码摘要做【内容】比对（权威）；
#     mtime 仅作非 Gate 模式的廉价提示；
#   - 构建输入不含 Assets/_Project/Tests（EditMode 测试不进产物）：测试代码改动不再把两端
#     判旧（仅作提示行显示）；
#   - "运行中 DS/客户端"必须通过 PID 存活（+可执行路径一致）核验，否则标"历史日志"。
#
# 用法：
#   Tools\BuildStatus.ps1            # 人读表格（mtime 提示）
#   Tools\BuildStatus.ps1 -Gate      # 启动闸模式：内容摘要比对；STALE 时退出码 3
#
# 退出码：0 = 两端构建都不旧于源码；3 = 任一端 STALE/INVALID；4 = 清单缺失（从未构建/产物被删）。

param([switch]$Gate)

$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$clientManifest = Join-Path $root "Builds\ReleaseClient\build-manifest.json"
$serverManifest = Join-Path $root "Builds\Server\build-manifest.json"
$verdictStale = $false
$deep = [bool]$Gate

function Read-Manifest($path) {
    if (-not (Test-Path $path)) { return $null }
    try { return Get-Content $path -Raw | ConvertFrom-Json } catch { return $null }
}

function UtcToLocal($iso) {
    if (-not $iso) { return $null }
    try { return ([datetime]::Parse($iso, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal)).ToLocalTime() } catch { return $null }
}

# ---- 构建输入集合（与 BuildManifestWriter.InputRoots 保持一致） ----------------
$script:inputRoots = @(
    (Join-Path $root "Assets"),
    (Join-Path $root "Packages\manifest.json"),
    (Join-Path $root "Packages\packages-lock.json"),
    (Join-Path $root "ProjectSettings")
)
$testsRoot = Join-Path $root "Assets\_Project\Tests"

function Get-NewestFileTime($paths) {
    $newest = $null
    foreach ($p in $paths) {
        if (-not (Test-Path $p)) { continue }
        if (Test-Path $p -PathType Leaf) {
            $t = (Get-Item $p).LastWriteTime
        } else {
            $f = Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $f) { continue }
            $t = $f.LastWriteTime
        }
        if ($null -eq $newest -or $t -gt $newest) { $newest = $t }
    }
    return $newest
}

# 内容摘要（与 C# BuildManifestWriter.ComputeInputDigest 逐字节同算法：
# 稳定顺序遍历 → 相对路径+\n+文件内容SHA256 → 归并 SHA256）
function Get-InputDigest {
    $merger = [System.Security.Cryptography.SHA256]::Create()
    $count = 0
    foreach ($p in $script:inputRoots) {
        if (-not (Test-Path $p)) { continue }
        $files = @()
        if (Test-Path $p -PathType Leaf) { $files = @($p) }
        else {
            # 与 C# StringComparer.Ordinal 排序严格一致（PS Sort-Object 默认文化排序 → 摘要漂移）
            $names = New-Object 'System.Collections.Generic.List[string]'
            Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { [void]$names.Add($_.FullName) }
            $pathsArray = $names.ToArray()
            [System.Array]::Sort($pathsArray, [System.StringComparer]::Ordinal)
            $files = $pathsArray
        }
        foreach ($f in $files) {
            $path = if ($f -is [System.IO.FileInfo]) { $f.FullName } else { $f }
            $rel = $path.Substring($root.Length + 1).Replace('\', '/')
            $pathBytes = [System.Text.Encoding]::UTF8.GetBytes($rel + "`n")
            [void]$merger.TransformBlock($pathBytes, 0, $pathBytes.Length, $null, 0)
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $stream = [System.IO.File]::OpenRead($path)
            try { $contentHash = $sha.ComputeHash($stream) }
            finally { $stream.Dispose(); $sha.Dispose() }
            [void]$merger.TransformBlock($contentHash, 0, $contentHash.Length, $null, 0)
            $count++
        }
    }
    [void]$merger.TransformFinalBlock(@(), 0, 0)
    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $merger.Hash) { [void]$sb.Append($b.ToString('x2')) }
    $merger.Dispose()
    return @{ Digest = $sb.ToString(); Count = $count }
}

function Get-DllPathFromManifest($manifestPath) {
    $dir = Split-Path -Parent $manifestPath
    $dataDir = Get-ChildItem $dir -Directory -Filter "*_Data" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($dataDir) { return (Join-Path $dataDir.FullName "Managed\Game.Gameplay.dll") }
    return $null
}

function Test-DllHashMatches([string]$dllPath, [string]$expected) {
    if (-not $dllPath -or -not (Test-Path $dllPath)) { return $null } # 无 DLL 可验（构建形态差异）→ 不以此判死
    if (-not $expected -or $expected -eq '<absent>') { return $false }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $onDisk = ($sha.ComputeHash([System.IO.File]::ReadAllBytes($dllPath)) | ForEach-Object { $_.ToString('x2') }) -join ''
    $sha.Dispose()
    return ($onDisk -eq $expected)
}

function Get-LogProcessIdentity([string]$logPath, [string]$expectedExe) {
    # 返回 @{ Alive=bool; Pid=int; Verified=bool }：APP_PROTOCOL 行的 pid 必须仍存活，
    # 且（进程路径可读时）与预期 exe 一致——否则该日志只是历史记录。
    $hit = Select-String -Path $logPath -Pattern 'APP_PROTOCOL.*pid=(\d+).*buildId=([0-9a-f]+) builtAt=([^ ]+)' |
        Select-Object -Last 1
    if (-not $hit -or $hit.Matches.Count -eq 0) { return $null }
    $procId = [int]$hit.Matches[0].Groups[1].Value
    $buildId = $hit.Matches[0].Groups[2].Value
    $built = $hit.Matches[0].Groups[3].Value
    $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
    $alive = ($null -ne $proc)
    $verified = $false
    if ($alive -and $expectedExe -and $proc.Path) { $verified = ($proc.Path -eq $expectedExe) }
    return @{ Alive = $alive; Pid = $procId; BuildId = $buildId; Built = $built; Verified = $verified }
}

Write-Output ""
Write-Output "  ================= 构建身份核对（测试收口） ================="
$deepNote = if ($deep) { "Gate：输入内容摘要比对" } else { "提示：mtime 比对（-Gate 启用内容摘要）" }
Write-Output ("  模式         : " + $deepNote)

# ---- 1. 源码最新修改时刻（产物相关输入 + 测试代码分列） ----------------------
$newestSource = Get-NewestFileTime $script:inputRoots
$newestTest = Get-NewestFileTime @($testsRoot)
if ($newestSource) {
    Write-Output ("  源码最新修改 : {0:MM-dd HH:mm:ss}  （产物输入集：Scripts/Editor/Lua/Packages/ProjectSettings）" -f $newestSource)
} else {
    Write-Output "  源码最新修改 : 未知（输入集为空）"
}
if ($newestTest) {
    Write-Output ("  测试代码修改 : {0:MM-dd HH:mm:ss}  （不参与 STALE 判定）" -f $newestTest)
}

# ---- 2. 两端磁盘构建 --------------------------------------------------------
$staleReasons = @()
foreach ($side in @(
        @{ Name = "客户端构建"; Path = $clientManifest; Exe = (Join-Path $root "Builds\ReleaseClient\UnityFpsClient.exe") },
        @{ Name = "服务器构建"; Path = $serverManifest; Exe = (Join-Path $root "Builds\Server\UnityFpsDedicatedServer.exe") })) {
    $m = Read-Manifest $side.Path
    if ($null -eq $m) {
        Write-Output ("  {0} : <清单缺失>  {1}" -f $side.Name, $side.Path)
        $staleReasons += ($side.Name + " 从未构建或清单缺失")
        $verdictStale = $true
        continue
    }
    $built = UtcToLocal $m.builtAtUtc
    $id = $m.buildId
    if ($id -and $id.Length -gt 8) { $id = $id.Substring(0, 8) }
    $line = "  {0} : build {1}  {2:MM-dd HH:mm:ss}" -f $side.Name, $id, $built

    # F16 fail closed：关键字段缺失/非法一律 INVALID（不是"未知但放行"）
    if (-not $m.protocolId) {
        Write-Output ($line + "   <-- INVALID：清单缺 protocolId")
        $staleReasons += ($side.Name + " 清单缺 protocolId（旧构建器产物，重建）")
        $verdictStale = $true
        continue
    }
    if (-not $built) {
        Write-Output ($line + "   <-- INVALID：builtAtUtc 无法解析")
        $staleReasons += ($side.Name + " 清单时间无法解析")
        $verdictStale = $true
        continue
    }
    if (-not $m.gamePlayDllSha256 -or $m.gamePlayDllSha256 -eq '<absent>') {
        Write-Output ($line + "   <-- INVALID：缺业务程序集哈希")
        $staleReasons += ($side.Name + " 清单缺 gamePlayDllSha256")
        $verdictStale = $true
        continue
    }
    # 产物 DLL 完整性（磁盘 vs 清单）
    $dllVerdict = Test-DllHashMatches (Get-DllPathFromManifest $side.Path) $m.gamePlayDllSha256
    if ($dllVerdict -eq $false) {
        Write-Output ($line + "   <-- INVALID：磁盘 DLL 与清单哈希不一致（产物被改动/半拷贝）")
        $staleReasons += ($side.Name + " 产物 DLL 哈希不一致")
        $verdictStale = $true
        continue
    }
    # 输入内容摘要（权威；缺 = 旧构建器产物 → 门禁阻止）
    if ($deep) {
        if (-not $m.inputDigest -or $m.inputDigest -eq '<absent>') {
            Write-Output ($line + "   <-- INVALID：清单缺 inputDigest（旧构建器产物，重建以启用内容比对）")
            $staleReasons += ($side.Name + " 清单缺 inputDigest")
            $verdictStale = $true
            continue
        }
        $current = Get-InputDigest
        if ($current.Digest -ne $m.inputDigest) {
            Write-Output ($line + "   <-- STALE：源码内容摘要与构建不一致（files={0}）" -f $current.Count)
            $staleReasons += ($side.Name + " 源码内容与构建不一致（需重建）")
            $verdictStale = $true
            continue
        }
        $line += "   inputDigest=OK"
    } else {
        # mtime 提示（非权威）
        if ($newestSource -and $newestSource -gt $built) {
            $delta = [int]([math]::Round(($newestSource - $built).TotalMinutes))
            $line += "   <-- STALE？源码比构建新约 {0} 分钟（-Gate 做内容比对确认）" -f $delta
            $staleReasons += ($side.Name + " 落后于源码 mtime（Gate 模式确认）")
            $verdictStale = $true
        }
    }
    Write-Output $line
}

# ---- 3. 运行中的 DS 身份（主实例 + 地图实例；PID 存活 + 可执行路径核验） -----
$serverLogs = Get-ChildItem (Join-Path $root "Tools\Server\Logs") -Recurse -Filter "server*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 6
$dsPrinted = 0
$dsExe = (Join-Path $root "Builds\Server\UnityFpsDedicatedServer.exe")
foreach ($log in $serverLogs) {
    $identity = Get-LogProcessIdentity $log.FullName $dsExe
    if ($null -eq $identity) { continue }
    $buildShort = $identity.BuildId
    if ($buildShort.Length -gt 8) { $buildShort = $buildShort.Substring(0, 8) }
    $state = if ($identity.Verified) { "运行中 pid=$($identity.Pid)" }
             elseif ($identity.Alive) { "运行中 pid=$($identity.Pid)（路径未核验）" }
             else { "历史日志（pid=$($identity.Pid) 已退出）" }
    Write-Output ("  DS {0} : build {1}  [{2}]  ({3})" -f $log.BaseName, $buildShort, $state, $log.Directory.Name)
    $dsPrinted++
}
if ($dsPrinted -eq 0) { Write-Output "  运行中 DS    : <未发现运行日志或未启动>" }

# ---- 4. 最近一次客户端启动身份（PID 存活核验） -------------------------------
$clientLogs = Get-ChildItem (Join-Path $root "Tools\Client\Logs") -Filter "client-*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$clientPrinted = $false
foreach ($log in $clientLogs) {
    $clientExe = (Join-Path $root "Builds\ReleaseClient\UnityFpsClient.exe")
    $identity = Get-LogProcessIdentity $log.FullName $clientExe
    if ($null -eq $identity) { continue }
    $buildShort = $identity.BuildId
    if ($buildShort.Length -gt 8) { $buildShort = $buildShort.Substring(0, 8) }
    $state = if ($identity.Verified) { "运行中 pid=$($identity.Pid)" }
             elseif ($identity.Alive) { "运行中 pid=$($identity.Pid)（路径未核验）" }
             else { "历史日志（pid=$($identity.Pid) 已退出）" }
    Write-Output ("  最近客户端   : build {0}  [{1}]  ({2:MM-dd HH:mm:ss} 日志)" -f $buildShort, $state, $log.LastWriteTime)
    $clientPrinted = $true
}
if (-not $clientPrinted) { Write-Output "  最近客户端   : <无启动日志>" }

# ---- 5. 结论 ----------------------------------------------------------------
Write-Output "  ============================================================"
if ($verdictStale) {
    Write-Output "  结论：不要开始测试 [X] —— $($staleReasons -join '；')"
    Write-Output "  先重建：Unity 菜单 Tools/Client/Build Windows Client (Release)"
    Write-Output "          与 Tools/Dedicated Server/Build Windows Server (Release)，"
    Write-Output "          然后重启联机服务器（改了服务器侧代码时必须停进程→重建→再启动）。"
    Write-Output "  ============================================================"
    if ($Gate) { exit 3 }
} else {
    Write-Output "  结论：可以测试 [OK] —— 两端构建都不旧于源码；进游戏后左上角可核对 build 编号。"
    Write-Output "  ============================================================"
    if ($Gate) { exit 0 }
}
