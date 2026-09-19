# 热更新 Demo 演示手册（用户亲自操作版）

> 目标：向观众演示"**不改客户端、不重发安装包，服务器上线一个新功能页面 / 一张新地图**"。
> 前置：后端已运行（`启动联机服务器.cmd`，127.0.0.1:5080）；客户端 = `Builds/ReleaseClient/UnityFpsClient.exe`
>（2026-09-17 19:23 构建，**含热更框架但不含战绩页**——这是演示的关键：旧客户端）。
> 唯一客户端入口：项目根 `启动客户端.cmd`（每实例独立日志，演示后好排查）。

---

## 演示 A：热更上线「战绩」页（推荐主打）

**效果**：旧客户端启动 → 自动拉取热更包 → 大厅顶栏多出「战绩」页签 → 点开见生涯汇总+对局列表。

| 步骤 | 操作 | 预期 |
|---|---|---|
| 0 | 登录两个账号打 1-2 局有结算的比赛（让战绩库有数据） | — |
| 1 | 部署 v3 包：<br>`powershell -ExecutionPolicy Bypass -File Tools\HotUpdate\Publish-HotUpdate.ps1 -Version 3 -Deploy` | 输出 `DEPLOYED to ...hotupdate` |
| 2 | 验证服务器：<br>`curl http://127.0.0.1:5080/hotupdate/manifest.json` | `"version": "3"` |
| 3 | **现场翻车演示（可选）**：先 `curl` 改坏 manifest（见 §回滚），启动客户端 | 回退内置版本照常可玩（日志 `fallback-*`） |
| 4 | 启动客户端（`启动客户端.cmd`），登录 | 客户端日志出现：<br>`[HotUpdate] kind=applied version=3 downloaded=3 luaRoot=...HotFiles\3` |
| 5 | 大厅顶栏 → 「战绩」 | 汇总卡（总场次/胜率/K/D/收益）+ 倒序对局列表 + 翻页 |
| 6 | 断网/关后端再开客户端 | 照常进大厅、战绩页仍在（已装版本本地兜底） |

**话术要点**：客户端 exe 全程未变（可比对 buildId）；页面代码在服务器 `hotupdate/3/career_page.lua`，观众可现场改文案重发看效果。

## 演示 B：热更上线「新地图 Training Yard」（进阶）

**前置**：演示 A 已部署 v3；DS 全地图栈运行中（含 map_04 实例，端口 7774）。
⚠️ **DS 必须是 19:50 后重建的新 Server**（服务器无法被客户端热更——这本身就是给观众讲的设计点）。

| 步骤 | 操作 | 预期 |
|---|---|---|
| 1 | （若未做）构建 bundle：Unity 菜单 `Tools/HotUpdate/Build Map Bundles` | `Logs/HotUpdate/bundles/maps/map_trainingyard.bundle`（34.8MB） |
| 2 | 部署 v4 包：<br>`powershell ... Publish-HotUpdate.ps1 -Version 4 -Deploy` | 包含 `maps/map_trainingyard.bundle` |
| 3 | 启动客户端登录 → 大厅 → 联机对战 → 创建房间 | 地图栏多出「Training Yard」（列表来自后端 /api/maps） |
| 4 | 选 Training Yard + KillRace(10杀/5分钟) → 创建并进入等待房间 → 开始比赛 | 入场；客户端日志 `kind=applied version=4 downloaded=4`，**34.8MB 地图按需下载** |
| 5 | 对局 | 场景为训练场布局（中央箱阵/两翼长墙），8 出生点，联网正常 |
| 6 | 对照演示：同客户端选 Arena 开局 | 内置图走 SceneManager，秒进（对比"内置 vs 热更"双通道） |

## 回滚（30 秒）
```
# 把任意旧版本的 manifest 覆盖回去（版本目录都留着）：
copy /Y Logs\HotUpdate\releases\2\manifest.json fps-backend\src\UnityFps.Api\hotupdate\manifest.json
```
已开过的客户端继续用本地已装版本（DowngradeRejected 保护），**新启动**的客户端回到旧版页面。

## 排障速查
| 症状 | 看哪 | 含义 |
|---|---|---|
| 日志无 `[HotUpdate]` 行 | 启动方式 | 直接双击 exe ≠ 启动客户端.cmd；或编辑器默认跳过（`-hotupdateUrl=http://127.0.0.1:5080/hotupdate` 可强制） |
| `kind=error` | error 字段 | 后端不可达/manifest 损坏 → 已回退可玩 |
| `kind=downgrade-rejected` | manifest.version | 服务器版本 < 本地已装（回滚保护生效） |
| `kind=min-client-gate` | minClientVersion | 该热更需要更新的客户端整包 |
| 战绩页空白/报错 | 页面内错误卡+重试 | 看 `career_page.lua` 是否语法错（热更包版本问题→回滚） |
| map_04 按钮不见了 | `curl /api/maps` | 后端未带 MapCatalog 新版（需 19:50 后重启的后端） |
| 进 map_04 提示需要热更资源 | 日志 manifest 是否含 maps/*.bundle | v4 未部署完整 / 下载中断（删 `%USERPROFILE%\AppData\LocalLow\DefaultCompany\UnityFps\HotFiles` 重来） |
