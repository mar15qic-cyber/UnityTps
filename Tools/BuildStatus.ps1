# BuildStatus.ps1 - 测试前构建身份核对（2026-09-19 测试收口闸）
#
# 解决的实机痛点：连续多轮实测测到旧构建（22:42 / 00:55 / 00:56 三轮全测了 09-18 06:37 的
# 旧 DLL），测试者无法判断"这一轮是在测修改前还是修改后"。
#
# 本脚本一次回答四个问题：
#   1) 磁盘上的客户端构建是哪个（buildId + 本地构建时刻）
#   2) 磁盘上的服务器构建是哪个
#   3) 源码是否比构建新（STALE = 你这轮测试测不到最新修改）
#   4) 运行中的 DS / 最近一次启动的客户端分别是什么身份
#
# 用法：
#   Tools\BuildStatus.ps1            # 人读表格
#   Tools\BuildStatus.ps1 -Gate      # 启动闸模式：STALE 时退出码 3（供 .cmd 判断）
#
# 退出码：0 = 两端构建都不旧于源码；3 = 任一端 STALE；4 = 清单缺失（从未构建/产物被删）。

param([switch]$Gate)

$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$clientManifest = Join-Path $root "Builds\ReleaseClient\build-manifest.json"
$serverManifest = Join-Path $root "Builds\Server\build-manifest.json"
$verdictStale = $false

function Read-Manifest($path) {
    if (-not (Test-Path $path)) { return $null }
    try { return Get-Content $path -Raw | ConvertFrom-Json } catch { return $null }
}

function UtcToLocal($iso) {
    if (-not $iso) { return $null }
    try { return ([datetime]::Parse($iso, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal)).ToLocalTime() } catch { return $null }
}

Write-Output ""
Write-Output "  ================= 构建身份核对（测试收口） ================="

# ---- 1. 源码最新修改时刻 ----------------------------------------------------
$sourceDirs = @(
    (Join-Path $root "Assets\_Project\Scripts"),
    (Join-Path $root "Assets\_Project\Editor"),
    (Join-Path $root "Assets\_Project\Tests")
)
$newestSource = $null
foreach ($d in $sourceDirs) {
    if (-not (Test-Path $d)) { continue }
    $f = Get-ChildItem $d -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f -and ($null -eq $newestSource -or $f.LastWriteTime -gt $newestSource.LastWriteTime)) { $newestSource = $f }
}
if ($newestSource) {
    Write-Output ("  源码最新修改 : {0:MM-dd HH:mm:ss}  ({1})" -f $newestSource.LastWriteTime, $newestSource.Name)
} else {
    Write-Output "  源码最新修改 : 未知（未找到 .cs）"
}

# ---- 2. 两端磁盘构建 --------------------------------------------------------
$staleReasons = @()
foreach ($side in @(
        @{ Name = "客户端构建"; Path = $clientManifest },
        @{ Name = "服务器构建"; Path = $serverManifest })) {
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
    if ($newestSource -and $built -and $newestSource.LastWriteTime -gt $built) {
        $delta = [int]([math]::Round(($newestSource.LastWriteTime - $built).TotalMinutes))
        $line += "   <-- STALE：源码比构建新约 {0} 分钟，测不到最新修改！" -f $delta
        $staleReasons += ($side.Name + " 落后于源码（需重建）")
        $verdictStale = $true
    }
    Write-Output $line
}

# ---- 3. 运行中的 DS 身份（从最新 server.log 的 APP_PROTOCOL 行取）-----------
$dsLine = $null
$serverLogs = Get-ChildItem (Join-Path $root "Tools\Server\Logs") -Recurse -Filter "server.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($serverLogs) {
    $hit = Select-String -Path $serverLogs.FullName -Pattern 'APP_PROTOCOL.*buildId=([0-9a-f]+) builtAt=([^ ]+)' |
        Select-Object -Last 1
    if ($hit -and $hit.Matches.Count -gt 0) {
        $dsBuildId = $hit.Matches[0].Groups[1].Value
        if ($dsBuildId.Length -gt 8) { $dsBuildId = $dsBuildId.Substring(0, 8) }
        $dsLine = "  运行中 DS    : build $dsBuildId  ($($serverLogs.Directory.Name))"
    }
}
if ($dsLine) { Write-Output $dsLine } else { Write-Output "  运行中 DS    : <未发现运行日志或未启动>" }

# ---- 4. 最近一次客户端启动身份（最新 client 日志的 APP_PROTOCOL 行）---------
$cliLine = $null
$clientLog = Get-ChildItem (Join-Path $root "Tools\Client\Logs") -Filter "client-*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($clientLog) {
    $hit = Select-String -Path $clientLog.FullName -Pattern 'APP_PROTOCOL.*buildId=([0-9a-f]+)' |
        Select-Object -First 1
    if ($hit -and $hit.Matches.Count -gt 0) {
        $cliBuildId = $hit.Matches[0].Groups[1].Value
        if ($cliBuildId.Length -gt 8) { $cliBuildId = $cliBuildId.Substring(0, 8) }
        $cliLine = "  最近客户端   : build $cliBuildId  ($($clientLog.LastWriteTime.ToString('MM-dd HH:mm:ss')) 日志)"
    }
}
if ($cliLine) { Write-Output $cliLine } else { Write-Output "  最近客户端   : <无启动日志>" }

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
