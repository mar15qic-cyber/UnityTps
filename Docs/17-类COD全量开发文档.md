# 17 类COD方向全量开发文档

> 文档状态：**执行基线 v2.0**（当前最高执行文档）
> 制定日期：2026-08-29
> 取代关系：本文取代 15 号成为执行基线；15 号与本文冲突处以本文为准，15 号的事务纪律、测试门槛、安全边界条款在本文未修改处继续有效。历史文档（00–14、16）仅作设计依据与交接参考。
> 阅读对象：后续接手的实施者（人或 AI），目标是不读聊天历史即可理解全部需求、设计与验收。

---

## 0. 本轮需求变更摘要（相对 15 号基线）

| # | 变更 | 内容 | 影响面 |
|---|---|---|---|
| 1 | 新增 | **通行证系统**：配件不再商城售卖，改由通行证等级解锁（自动发放至库存） | 数据库、后端、结算、UI |
| 2 | 新增 | **成就系统**：成就一次性发放通行证经验，是通行证升级两大来源之一 | 数据库、后端、结算、UI |
| 3 | 定版 | **唯一对战模式**：「击杀竞赛」——先达到 20 杀获胜 | Gameplay、网络、结算 |
| 4 | 定版 | 对战完成发放**账号经验 + 金币 + 通行证经验** | 后端结算、对账 |
| 5 | 明确 | **仓库 = 已购买/已拥有武器存放页**；商城只卖武器（等级 + 金币双门槛） | UI 信息架构 |
| 6 | 冻结 | **技能点系统保留但停止演进**：UI 移除入口，字段与 API 保留，规划后续删除 | Profile、升级页 |
| 7 | 继承 | 枪械自由改装（Gunsmith）、商城双门槛、配件诚实呈现边界，沿用 15 号 §6/§9 | 全线 |

---

## 1. 设计方向

### 1.1 一句话定位

低多边形画风的**类 COD 快节奏竞技 FPS**：短 TTK、大厅即枢纽（Hub）、以「枪械改装 + 赛季通行证」为成长主轴的单模式联机对战。

### 1.2 核心循环

```
启动/连接 → 登录/注册 → 大厅首页
    ├─ 仓库（查看已拥有武器）→ 枪械改装（3D 预览 + 配件装配）→ 保存配装
    ├─ 商城（等级+金币双门槛购买新武器）
    ├─ 通行证（查看等级/奖励轨/成就进度）
    ├─ 设置（本地偏好）
    └─ 开始对战 → [击杀竞赛：先到 20 杀] → 结算（XP+金币+通行证经验）→ 回大厅
```

成长反馈闭环：**打一局 → 账号经验（等级提升解锁商城购买资格）+ 金币（商城购买武器）+ 通行证经验（等级提升解锁配件）→ 下一局带着新枪新配件打**。

### 1.3 成长系统三支柱

| 支柱 | 货币/进度 | 获取 | 消耗 | 解锁物 |
|---|---|---|---|---|
| 账号等级 | XP → Level | 每局结算（按击杀/胜负） | 无（等级本身是门槛） | 商城武器的**购买资格** |
| 金币 | Coins | 每局结算 + 通行证等级奖励 | 商城购买武器 | 武器所有权 |
| 通行证 | PassXP → PassLevel | **每完成一局（固定值）+ 成就一次性奖励** | 无（等级自动发放奖励） | **全部配件** + 少量金币 |

关键规则（继承并定版）：

- 等级达到只代表获得**购买资格**，不自动获得武器；武器必须支付金币（15 号 §6 继续有效）。
- 配件**不可购买**，唯一来源是通行证等级奖励；未达到对应通行证等级的配件在改装页显示为「通行证 Lv.X 解锁」。
- M4、AK 为初始解锁步枪，Service Pistol 为初始副武器；其余主武器一律 `UnlockLevel > 1` 且 `PriceCoins > 0`。
- 业务 ID 与资源名分离（`weapon.m4` / `weapon.ak` / `weapon.service_pistol`…），价格、门槛、曲线全部集中配置 + 测试覆盖，禁止散落硬编码。

### 1.4 对战规则（定版）

| 项 | 规格 |
|---|---|
| 模式名 | 击杀竞赛（Kill Race） |
| 规模 | 首版 **1v1**（Host + 1 客户端），架构预留 FFA N 人扩展 |
| 胜负 | 任一玩家击杀数 **≥ 20** 立即结束，该玩家获胜 |
| 时长上限 | 10 分钟；超时按击杀数判定，击杀相同按死亡少者，再相同判 DRAW（奖励按败方档） |
| 重生 | 死亡后 3 秒重生，出生点从固定点集随机（不与对手当前点重叠） |
| 比分 | 服务器权威，HUD 实时显示双方击杀数与目标值 20 |
| 奖励 | 每局完成必得：账号 XP、金币、通行证经验（公式见 §4.6） |

### 1.5 枪械改装哲学（诚实呈现）

- 两把已审计武器（`FP_Rifle_View`、`FP_ServicePistol_View`）提供**真实可用**的瞄具/消音器/弹匣装配：3D 预览可见、保存后持久化、重登恢复。
- 其余 8 把武器提供商城/仓库展示、详情属性条与基础 3D 预览；配件槽显示「尚未适配」，不得出现假装配按钮。
- Tactical/Grip/Stock 槽位本轮一律不启用（无验证挂点）。
- 只有 `IsImplemented=true` 且校准数据存在的武器-配件组合可保存为实际装配（15 号 §5.7/§9 继续有效）。

### 1.6 视觉与交互规范

- 沿用参考视频视觉语言：青蓝/蓝绿渐变背景、半透明玻璃面板 + 细描边、顶部主导航、中央内容区 + 右侧详情、武器页以 3D 模型为视觉中心、规则物品网格、横向属性进度条。
- 状态可视区分：选中 / 锁定（含解锁条件文案）/ 已拥有 / 已装备 / 购买中 / 不可用，六态必须有明显视觉差异。
- 适配 1920×1080 基准 + 常见 16:10；Canvas Scaler + Safe Area；长用户名不破版。
- 中文使用可再分发字体或系统字体回退，保留许可说明；不引入未知来源素材。
- 键鼠导航优先；手柄仅保留可扩展路径。

### 1.7 明确不做（本版范围外）

真实支付/充值/退款、通行证付费轨（premium track）、多游戏模式、排位/段位、好友与聊天、邮件/找回密码、Refresh Token、角色权限体系、云部署、正式反作弊、新大型 UI 框架、手雷 Throwable 实现（槽位灰显「预留」）。**技能点升级页冻结**（见 §3.6-状态注记）。

---

## 2. 技术栈与架构总纲

### 2.1 客户端（Unity）

| 项 | 选型 | 备注 |
|---|---|---|
| 引擎 | 团结引擎（Unity 6 兼容）/ URP（URP-Balanced） | 异常时回退官方 Unity 6000.0.76f1 打开同工程 |
| 网络 | **FishNet (Pro)** | 房间/同步/服务器权威战斗；Prediction 可选，回退线 NetworkTransform |
| 动画 | Animancer（代码驱动 mixer/layer/EndEvent） | 回退线裸 Mecanim（只改 AnimDriver 内部） |
| IK | Final IK（TP 瞄准 AimIK + 左手 IK） | >0.5 天兼容成本则退化无 IK |
| 相机 | Cinemachine 3.1.7（本地 tgz） | FP rig + ADS 已落地 |
| 热更 | xLua | 仅 Reload 演示链路；失败降级 JSON 热载 |
| UI | uGUI + TextMeshPro | **必须用 `InputSystemUIInputModule`**（项目启用新 Input System，当前遗留旧 StandaloneInputModule 需修正） |
| 输入 | 新 Input System | 大厅 UI 与战斗输入一致 |
| 资产 | LowPoly FPS Pack 3.2（10 枪、19 配件 prefab、8 瞄具 prefab、FP/TP 视图 ×20） | 全部已入库 |

### 2.2 后端

| 项 | 选型 |
|---|---|
| 框架 | ASP.NET Core 8 WebAPI（`fps-backend/src/UnityFps.Api`） |
| ORM | EF Core 8.0.13 + Pomelo MySQL 8.0.3；本地固定 `dotnet-ef` 8.0.x（`.config/dotnet-tools.json`，**尚未建立，Phase 1 首项**） |
| 数据库 | MySQL 开发库 `unitylowpolyfps` / 压测库 `unitylowpolyfps_test`；连接串只存 User Secrets（`GameDb` / `GameDbTest`） |
| 认证 | JWT，12 小时会话；BCrypt work factor 12；JWT 不落盘、密钥不入库 |
| 错误模型 | 统一稳定错误码 + ProblemDetails（中间件已就位） |
| 文档 | Swagger |

### 2.3 测试工具链

xUnit（后端单元/契约/服务流，`fps-backend/tests/UnityFps.Api.Tests` 骨架已存在）、真实 MySQL 集成测试（禁止 InMemory 替代）、k6（HTTP 压测）、MySQL `EXPLAIN ANALYZE` + 锁等待/连接池观测、Unity EditMode 测试、截图验收（1920×1080 + 16:10）。

### 2.4 架构铁律（不可违反）

1. **三层单向依赖**：Presentation → Gameplay ← NetworkAdapter，asmdef 编译期强制。
2. **单写者原则**：只有 Locomotor 动 Transform、只有 AnimDriver 播动画、只有服务器改比分。
3. **状态/事件二分**：位置/血量/弹药/比分 = 服务器权威同步状态；开火/换弹/死亡 = 服务器广播事件；网络只传 Action 语义，从不传动画。
4. **权威边界**：客户端不是价格、余额、等级门槛、购买结果、比分、奖励的权威来源。比赛内比分由游戏服（Host）权威统计，后端结算只做受约束输入 + 数值裁剪。
5. **运行时测试授权制**（07 号最高纪律，继续有效）：PlayMode 注入测试必须用户亲自授权；禁止轮询；默认安全手段 = EditMode + 只读 console 检查。
6. **每完成一个 Job 立即 `git add -A && git commit`**（2026-08-25 事故制度）。

### 2.5 程序集与目录

- Unity：`Game.Core` / `Game.Gameplay` / `Game.Presentation` / `Game.NetworkAdapter` / `Game.Account` / `Game.UI`（UI 引用 Core+Gameplay+Account+TMP+uGUI）/ `Game.HotUpdate` / Editor 程序集。
- 本轮改动面：`Assets/_Project/Scripts/UI/`（全量页面）、`Account/`（新端点实现）、`Gameplay/`+`NetworkAdapter/`（对战规则）、`Prefabs/Weapons/`（配件校准）、`ScriptableObjects/`（目录映射/校准配置）；后端全部限定在 `fps-backend/`。
- 场景：`Boot.unity`（连接+登录入口）、`Lobby.unity`（大厅全页面）、`Arena.unity`（对战）。

---

## 3. Gameplay 内容

### 3.1 已完成基线（不重做，只引用）

| 模块 | 状态 |
|---|---|
| 移动（CC 驱动 WASD/Jump/Sprint/MouseLook） | ✅ Day1 |
| 武器四件套 WeaponDefinition/Runtime/Controller/View + 10 枪数据 | ✅ Day2/3 |
| ActionSystem 全状态 + 打断矩阵；Animancer FP/TP 双 Rig + 动画事件 | ✅ Day3 |
| 表现层：Cinemachine FP rig、sway/bob/breathing/recoil/ADS、枪口特效/弹壳/命中反馈、10 枪 muzzle 校准、 recoil/精度/弹丸接数值 | ✅ Day4 CP1–CP7 |
| uGUI 准星/HUD 底子 | ✅ Day4 CP5 |
| 后端注册/登录/JWT/档案/配装/升级/战绩 + Unity 登录→大厅→配装→升级流程 | ✅ Day5–6（`codex/day5-day6-backend-lobby` 及当前工作区） |

### 3.2 枪械改装（Gunsmith）——本轮核心新内容

**页面**：`Armory`（仓库）选中武器 → 进入 `WeaponDetails`（改装页）。

- **中央 3D 预览**：武器模型居中、鼠标拖拽旋转、滚轮缩放、重置按钮；数据来自 `WeaponPreviewController`（已存在，需接入真实目录）。
- **属性条**：伤害、射速、弹匣容量、稳定/后坐力——只展示 `WeaponDefinition` 中确有数据的属性；配件对属性的修正量走现有 StatResolver（Day4 CP3 嵌套 WeaponStat 组 + 描述性修饰符），**改装必须真实反映到属性条**。
- **配件槽**：Optic / Muzzle / Magazine 三类；槽位状态机：`空 → 已装备`、`可装备（已拥有）`、`未拥有（通行证 Lv.X 解锁）`、`不兼容`、`尚未适配`。
- **装配生效**：保存后服务端校验（§4.5）+ 客户端 prefab 实时挂载预览（偏移/旋转/缩放走校准配置 `CalibrationKey`）；FP/TP 两视图同步生效；重登后恢复。
- **两把全量演示武器**：`FP_Rifle_View` 与 `FP_ServicePistol_View` 各完成瞄具/消音器/弹匣 ×3 类的真实装配演示；其余武器该区域显示兼容性受限说明。
- **配件校准数据**：每个已实现组合的 offset/rotation/scale 集中为 ScriptableObject 校准配置，键 = 后端 `CalibrationKey`（§4.3），由配件校准流（§6.2-D 流）产出。

### 3.3 击杀竞赛玩法（对战内）

- **进场**：大厅「开始对战」→ 匹配/建房（Host 模式 + 房间码，见 §4.1）→ 出生在 Arena 出生点 → 3 秒倒计时冻结输入 → 开局。
- **战斗**：服务器权威开火链路 FireRequest → 弹药/射速/精度验证 → 服务器 Raycast（可选 LagCompensator）→ 伤害 → 死亡/重生；换弹/切枪为服务器广播事件驱动远端动画（03/04 号文档既定三端流程）。
- **比分**：服务器记录 `kills/deaths` 并以同步状态广播；HUD 顶部常驻 `我方击杀 / 目标20 / 对手击杀`；击杀播报（kill feed）为广播事件。
- **终局**：任一方达到 20 杀，服务器进入 Ended，广播胜负；全员切换结算页。
- **结算**：客户端携带 `ClientMatchId`（本局唯一，重试复用）提交 `POST /api/matches`；提交内容为受约束输入（击杀/死亡/时长/胜负），后端裁剪后发放 XP + 金币 + 通行证经验（§4.6）；结算页展示本次三者数值、通行证等级变化、新解锁成就与新获得配件，然后返回大厅刷新档案/钱包/通行证。
- **断线**：对战中断线 → 本局作废不计奖励（首版）；结算前断线 → 重连后凭 `ClientMatchId` 幂等补交（本地暂存待提交记录）。

### 3.4 HUD / 暂停 / 结算

- **HUD**：血量、弹药（当前/备弹）、当前武器名、准星（沿用 Day4 CP5）、比分板（Tab 展开：双方 K/D）、击杀播报、网络状态图标；HUD 只读运行时状态，不轮询数据库，后端不可用不得阻塞战斗显示。
- **暂停**：继续 / 设置 / 返回大厅（退出确认）；正确处理 TimeScale、输入锁定与光标释放；多人局退出即视为放弃本局。
- **结算**：胜负大标题、K/D、XP（含升级进度条动画）、金币（含余额）、通行证经验与等级变化、新解锁配件/成就徽标、重试态（提交失败可重试且不重复发奖）、返回大厅按钮。

### 3.5 UI 页面全量清单

导航结构（顶部主导航）：**游戏大厅 · 仓库 · 商城 · 通行证 · 设置 · 退出**。`LobbyPage` 枚举已覆盖以下全部页面（新增 `BattlePass`，`Upgrades` 冻结）。

| 页面 | 枚举 | 内容要点 | 必须覆盖状态 |
|---|---|---|---|
| Boot/连接 | `Boot` | 探活 `/health` + 版本兼容 | 连接中/成功/超时/不可用/重试 |
| 登录 | `Login` | 用户名/密码/登录/去注册 | 空输入/格式错误/提交中/401/网络错误/成功 |
| 注册 | `Register` | 用户名/密码/确认/返回 | 校验失败/用户名占用/提交中/成功自动进大厅 |
| 昵称确认 | `Identity` | 复用用户名显示或迁移加受约束 DisplayName | 首次登录展示、跳过 |
| 大厅首页 | `Lobby` | 顶部导航 + 用户名/等级/XP/金币/当前主副武器 + 模式卡片 + 开始按钮 | 档案加载/部分接口失败/刷新/登出确认 |
| 任务/地图 | `Mission` | 唯一模式卡片「击杀竞赛 · 先到 20 杀」+ Arena 地图卡 + 开始 | 选中态/进入中/失败 |
| **仓库** | `Armory` | **已拥有武器网格**（含 M4/AK/初始副武器标记）、类型筛选、选中进改装、未拥有置灰并显示来源（商城/等级） | 空（不可能，必有初始枪）/加载/失败/重试 |
| 武器详情/改装 | `WeaponDetails` | §3.2 全部内容 | §3.2 五态 + 保存中/保存成功/409 冲突刷新 |
| **商城** | `Shop` | **仅武器**（配件已移出商城）：网格、价格、等级门槛、余额、购买按钮 | 已拥有/等级不足/金币不足/购买中/成功原位刷新/重复请求重放/409/服务错误 |
| **通行证** | `BattlePass` | 赛季名、当前等级与进度条、**奖励轨横向滑动条**（每级奖励卡片：已领取/可领取即已自动发放/未达成）、**成就列表 Tab**（进度/已解锁/PassXP 奖励值） | 加载/失败/重试 |
| 设置 | `Settings` | 图形/分辨率全屏/音量/鼠标灵敏度；本地 PlayerPrefs 持久化 | 应用/恢复默认/取消未保存 |
| 战斗 HUD | `Hud` | §3.4 | 后端不可用不受阻 |
| 暂停 | `Pause` | §3.4 | — |
| 结算 | `Results` | §3.4 | 提交中/成功/已重放/失败重试/返回大厅 |
| 通用错误/会话过期 | `Error` / `SessionExpired` | 401 清会话回登录；409 冲突刷新；429/500/超时/离线重试；显示 traceId（不含连接串/JWT/堆栈） | — |
| ~~升级~~ | `Upgrades` | **冻结**：主导航不提供入口，代码与后端 API 保留 | — |

通用组件（一次建成全局复用）：加载遮罩、Toast、确认弹窗、错误弹窗、重试组件；所有请求按钮在忙碌期禁用防重。UI 工程要求：拆分巨型 `LobbyBootstrap`（页面导航/视图组件/ViewModel/Presenter/ApiClient/资源目录/错误映射分层）；API 调用可取消、可超时、可重试；页面销毁后禁止写已销毁对象；禁止 release 假数据（离线 Mock 只能走显式开发开关）。

---

## 4. 网络与后端

### 4.1 网络架构（FishNet）

| 项 | 规格 |
|---|---|
| 拓扑 | Host 模式（房主即服务器）+ 客户端加入；首版 1v1，代码不写死人数上限 |
| 进房 | 建房生成 6 位房间码；客户端输入房间码加入；房间满员/不存在/已开局给稳定错误 |
| 生成 | 进房即生成 Player（CC/Locomotor/ActionSystem/WeaponController/Arsenal）；`NetworkTransform` 同位移；`LocomotionState` SyncVar 驱动远端 TP 动画；AimIK 同步瞄准方向 |
| 战斗 | 服务器权威：FireRequest（含弹药/射速/精度校验）→ 服务器 Raycast → 伤害 → 死亡/重生广播；开火/换弹/切枪动画事件按 §6.6 三端流程广播；Prediction 为可选增强（Day9 专窗，硬回退线 = NetworkTransform） |
| 比分 | 服务器权威 `kills/deaths`（SyncVar/Scoreboard 广播）；服务器判定 ≥20 终局 |
| 场景流 | `Lobby.unity` → 加载 `Arena.unity`（联网增量场景加载）→ 对战 → 结算回 `Lobby.unity` |

### 4.2 对战生命周期与权威

```
MatchLifecycle（服务器状态机）:
  Countdown(3s, 输入冻结) → InProgress → (kills≥20 或 10min 超时) → Ended → ResultScreen → 回大厅
```

- 只有服务器能改比分、判胜负、结束比赛；客户端 HUD 全部读同步状态。
- 结算提交由**房主/各客户端**携带本局 `ClientMatchId` 调后端；后端为唯一奖励权威（见 §4.5/§4.6）。
- 信任边界：游戏服（Host）统计是比赛内权威；后端仍做数值裁剪（防 Host 被篡改的纵深防御），客户端战绩只能作为受约束输入。

### 4.3 数据库设计

迁移策略（继承 15 号 §5.1）：不修改 `20260827120000_InitialCreate`；新增前向迁移 `AddEconomyShopPassAndMatchRewards`；test 库先行（连接串断言 `_test` 后缀），开发库后应用；禁止 drop/重建/清空开发库；可破坏性重置只允许作用于已验证测试库。

**沿用 15 号原设计的表**（字段规格以 15 号 §5.2–§5.9 为准，此处列变更）：

| 表 | 本轮变更 |
|---|---|
| `CatalogItem` | **新增 `AcquisitionSource varchar(16)`**：`Shop`（武器）/ `PassReward`（配件）/ `Initial`（初始枪）。`PassReward` 项 `PriceCoins=0`、不进商城目录查询 |
| `PlayerWallet` / `WalletLedger` / `PlayerInventory` / `ShopPurchase` | 不变；`WalletLedger.OperationKind` 枚举追加 `PassReward`（通行证等级金币奖励） |
| `WeaponAttachmentCompatibility` | 不变（`IsImplemented`、`CalibrationKey` 照旧） |
| `PlayerLoadout` + `PlayerLoadoutAttachment` | 不变（`Version` 并发控制照旧） |
| `PlayerProfile` | **字段全部保留（`SkillPoints`/`UpDamage`/`UpAmmoCap`/`UpMaxHealth` 标记 DEPRECATED）**；本轮不删，删除见 §4.9 |
| `MatchRecord` | 在 15 号 §5.9 基础上追加 **`XpEarned int`、`PassXpEarned int`**（连同 `CoinsEarned` 一起构成结算快照）；唯一索引 `(UserId, ClientMatchId)` 不变 |

**新增表：**

`PlayerPass`（通行证进度，每用户每赛季一行）

- `UserId bigint` PK+FK、`SeasonId varchar(32)`（首季 `S1`）、`PassXp int`（当前等级内累计）、`PassLevel int`（默认 1）、`Version bigint`、`UpdatedAtUtc`
- 唯一索引 `(UserId, SeasonId)`

`PassReward`（奖励轨配置，Seeder 维护，S1 共 15 级）

- `SeasonId`、`PassLevel int`、`RewardType varchar(16)`：`Attachment` / `Coins`
- `ItemId varchar(64)`（Attachment 时指向 CatalogItem）、`CoinsAmount int`
- 复合主键 `(SeasonId, PassLevel)`（首版每级一条奖励）
- S1 奖励轨基准（ItemId 落库用稳定业务 ID，AssetKey 由配件校准流回填）：

| 等级 | 奖励 | 等级 | 奖励 |
|---|---|---|---|
| 1 | 200 金币 | 9 | 400 金币 |
| 2 | 步枪瞄具（weapon.m4 optic） | 10 | 手枪消音器 |
| 3 | 300 金币 | 11 | 500 金币 |
| 4 | 步枪消音器 | 12 | 手枪弹匣 |
| 5 | 300 金币 | 13 | 500 金币 |
| 6 | 步枪弹匣 | 14 | 600 金币 |
| 7 | 400 金币 | 15 | 800 金币 |
| 8 | 手枪瞄具 | | |

> 未实现挂点的配件**不入奖励轨**（诚实呈现原则）；后续赛季扩轨前提是逐把补齐挂点/校准/验证（15 号 §9.3）。

`PlayerPassRewardGrant`（发放幂等记录）

- `UserId`、`SeasonId`、`PassLevel`、`GrantedAtUtc`；主键 `(UserId, SeasonId, PassLevel)`——保证跨多级升级/重复结算时每级奖励**恰好发放一次**。

`AchievementDefinition`（成就配置，Seeder 维护）

- `AchievementId varchar(64)` PK、`DisplayName`、`Description`、`TargetMetric varchar(32)`、`TargetValue int`、`PassXpReward int`、`SortOrder int`

`PlayerAchievement`（玩家成就进度）

- `UserId`、`AchievementId`、`Progress int`、`UnlockedAtUtc datetime(6) NULL`、`GrantedPassXp int`；主键 `(UserId, AchievementId)`；解锁即发 `PassXpReward`，`GrantedPassXp` 落值保证一次性。

**S1 成就最小集**（全部可由服务端从既有数据计算，无客户端上报成就事件；数值集中配置可调）：

| ID | 名称 | 条件 | PassXP |
|---|---|---|---|
| ach.first_win | 首胜 | 胜 1 场 | 300 |
| ach.kills_10 / 50 / 200 | 累计击杀 | 10 / 50 / 200 | 200 / 300 / 500 |
| ach.match_10 | 常客 | 完成 10 局 | 200 |
| ach.single_10 | 单局高手 | 单局击杀 ≥10 | 300 |
| ach.gunsmith_win | 改装致胜 | 带 ≥3 配件的武器获胜 1 场 | 400 |
| ach.level_5 | 军衔晋升 | 账号等级达 5 | 300 |
| ach.first_buy | 添置军火 | 首次商城购买武器 | 200 |
| ach.pass_10 | 通行证达人 | 通行证达 10 级 | 400 |

**注册事务与回填**（在 15 号 §5.10 基础上追加）：注册一次性创建 `UserAccount + PlayerProfile + PlayerWallet + PlayerLoadout + M4/AK/初始副武器库存 + 初始钱包流水`，并创建 `PlayerPass(S1, Lv1)` 行；旧用户幂等回填同步补 `PlayerPass` 行。

### 4.4 后端 API 全表

| 方法/路径 | 说明 | 状态 |
|---|---|---|
| `POST /api/auth/register` | 注册（§4.3 全量初始化） | 已有，需改造 |
| `POST /api/auth/login` | 登录，JWT 12h | 已有 |
| `GET /api/profile` | 档案（等级/XP/金币余额一并返回） | 已有，需扩展 |
| `PUT /api/profile/upgrades` | **DEPRECATED**：保留可用，文档标记，无 UI 入口 | 已有 |
| `GET /api/shop/catalog` | 商城目录：**仅 `AcquisitionSource=Shop` 且 `IsActive`**，含价格/等级门槛/拥有状态 | 新 |
| `GET /api/inventory` | 库存（武器 + 已获配件，附目录渲染字段） | 新 |
| `POST /api/shop/purchases` | 购买武器（幂等，§4.5） | 新 |
| `GET /api/loadout` / `PUT /api/loadout` | 配装读写（ExpectedVersion） | 已有，需扩展校验链 |
| `GET /api/loadout/attachments` / `PUT /api/loadout/attachments` | 配件装配读写（校验链 §4.5） | 已有接口，需接通新表 |
| `GET /api/pass` | 通行证：赛季/等级/进度/奖励轨（含每级已发放状态）/成就进度合并视图 | 新 |
| `GET /api/achievements` | 成就定义 + 个人进度（可并入 `/api/pass`，二选一后固定） | 新 |
| `POST /api/matches` | 结算：XP + 金币 + PassXP + 成就解锁（幂等，§4.5/§4.6） | 已有，需重写 |
| `GET /health` | 后端/数据库健康探针 | 新 |

契约冻结规则：Unity 侧已在 `AccountContracts.cs`/`IApiClient.cs` 起草大部分 DTO 与方法签名，**以冻结后的本表为准对齐**；字段命名对外统一小驼峰（金币字段名 `coins`）。

### 4.5 事务与幂等规则

**商城购买**（同 15 号 §7.2 全文有效）：同一 MySQL 事务内完成 幂等键校验 → 商品/等级/数量/所有权校验 → 原子条件更新扣款 → 购买记录 → 库存 → 流水 → 返回余额与物品；同键同请求重放 `replayed=true`，同键异请求 409；非堆叠已拥有返回稳定错误不扣款；余额恒 ≥0；客户端价格字段忽略或拒绝。

**配装更新**（15 号 §7.4 有效）：`ExpectedVersion` 乐观并发；装备配件校验链 = 拥有武器 ∧ 拥有配件 ∧ 商品有效 ∧ 兼容 ∧ 槽位匹配 ∧ `IsImplemented` ∧ 版本一致；过期返回 `409 LOADOUT_CONFLICT`；UserId 只取 JWT。

**比赛结算**（本轮重写核心，固定锁顺序 `Profile → Wallet → Pass → Achievement → MatchRecord/幂等`）：

1. 按 `(UserId, ClientMatchId)` 幂等：重复提交返回原结果 `replayed=true`；同 ID 不同载荷 409 `MATCH_IDEMPOTENCY_CONFLICT`。
2. 数值裁剪（`MatchRewardRules` 集中配置 + 测试）：`kills ≤ 30`、`duration ≤ 15min`、胜负一致性不校验（以提交为准但封顶奖励）；超限拒绝（422）。
3. 同一事务内：`PlayerProfile` 加 XP/升级（沿用 `ProgressionRules` 曲线，升级照旧发技能点——遗留行为）→ `PlayerWallet` 加金币 + `WalletLedger(OperationKind=MatchReward)` → `PlayerPass` 加 PassXP，**跨多级时逐级发奖**：`Attachment` 写 `PlayerInventory` + `PlayerPassRewardGrant`，`Coins` 写钱包 + `WalletLedger(OperationKind=PassReward)` → 成就进度推进、新解锁者发 PassXP（**PassXP 变化继续触发第 3 步升级发奖，循环至收敛**）→ `MatchRecord` 落 `XpEarned/CoinsEarned/PassXpEarned` 快照。
4. 返回：`{ xp, levelUp, coins, passXp, passLevel, passLevelUps[], newAttachments[], unlockedAchievements[], replayed }`。

**通行证发放幂等**：一切 PassXP 入口（结算、未来运营补偿）只允许走服务端事务；`PlayerPassRewardGrant` 主键兜底「每级奖励恰好一次」；成就 `GrantedPassXp` 落值兜底「每成就一次性」。

### 4.6 结算与成长公式（集中配置 `MatchRewardRules`，全部可调 + 测试覆盖）

| 项 | 公式（基准值） | 单局上限 |
|---|---|---|
| 账号 XP | `100 + 20×击杀 + 200×(胜?1:0)` | 1000 |
| 金币 | `50 + 10×击杀 + 100×(胜?1:0)` | 400 |
| 通行证经验 | `200` 固定 | 200 |
| 通行证升级曲线 | `Lv N→N+1 = 300 + 50×(N-1)`，S1 上限 15 级 | — |
| 等级曲线 | 沿用现有 `ProgressionRules`，不改 | — |

> 基准值仅为初始配置；调整只改配置类 + 对应测试期望，不改散落代码。达成「约 30 局 + 全成就可满通行证」的目标节奏。

### 4.7 稳定错误码

15 号 §7.5 全表继续有效；追加：

```text
PASS_SEASON_INVALID          // 赛季不存在/已结束
PASS_REWARD_GRANT_CONFLICT   // 发奖幂等冲突（理论兜底）
ACHIEVEMENT_INVALID          // 成就定义缺失
MATCH_PAYLOAD_REJECTED       // 结算数值裁剪拒绝（422）
```

### 4.8 安全与运维边界（15 号 §12 全文有效，摘要强化）

不读取/打印 User Secrets（只输出键存在性与脱敏结果）；密码/JWT 密钥/连接串不入 `appsettings*.json`、Unity 资源、日志、Git；不用 root 作为设计前提；开发库禁 drop/truncate/破坏性 Down；测试库重建前二次校验 `_test` 后缀；有购买/流水数据后回滚只回滚代码不回滚数据；不实现真实支付；不声称具备正式反作弊（幂等 + 裁剪是最低安全线）；InMemory 测试不得替代 MySQL 验证。

### 4.9 技能点删除路线（预告，本轮不执行）

1. 本轮：UI 移除入口、API 保留、字段标记 DEPRECATED（升级仍发点，行为不变）。
2. 下个大版本：新迁移 drop `SkillPoints/UpDamage/UpAmmoCap/UpMaxHealth` 列 + 删 `PUT /api/profile/upgrades` + 删升级页代码；先在 test 库演练 Down 脚本。

---

## 5. 测试

### 5.1 后端测试（xUnit + 真实 MySQL）

| 层 | 覆盖 |
|---|---|
| 单元 | 等级曲线、通行证曲线、结算公式与上限裁剪、购买校验分支、配装校验链、错误码映射 |
| 契约 | 全部 §4.4 API 的请求/响应/状态码/错误码快照；`/health` |
| 集成（MySQL） | 迁移可重复应用（test 库从 InitialCreate 升级到新迁移）；Seeder 幂等；注册全量初始化；旧用户回填幂等；重登后配装/库存/通行证恢复一致 |
| 并发（必测） | 15 号 §11.2 场景 1–9 全保留，追加：**同一局并发结算 100 次只加一次 XP/金币/PassXP**；**PassXP 跨多级时每级奖励恰好发放一次**（`PlayerPassRewardGrant` 无重复）；**成就并发解锁只发一次 PassXP**；结算触发成就→再触发升级的收敛性 |
| 安全 | A 用户伪造 B 用户资源全拒绝；JWT 过期/篡改；客户端价格/UserId 字段被忽略 |

### 5.2 Unity 测试

- **EditMode（默认手段）**：StatResolver 配件修正、ItemId→AssetKey 目录映射、错误码→本地化文案映射、结算结果→页面 ViewModel 映射、通行证奖励轨渲染数据、装配校准配置完整性（每个 `IsImplemented` 组合必有校准数据）。
- **PlayMode（授权制）**：仅「进入 → 只读检查 → 立即退出」，不注入逻辑；每次先报内容/时长/风险征得同意。
- **截图验收**：1920×1080 + 一个 16:10 分辨率全页面截图归档 `Assets/Screenshots/`（沿用现有做法）。

### 5.3 联机测试（局域网双端，手动脚本化清单）

互看互跑与动画同步 → 开火命中与伤害一致 → 击杀/比分实时同步 → 20 杀即时终局与胜负一致 → 超时判定 → 死亡重生 → 结算提交成功且双方各得奖励 → 结算重试不重复发奖 → 对战中断线处理 → 返回大厅档案/钱包/通行证数值刷新 → 1v1 两轮连胜续局。

### 5.4 性能与压测门槛（15 号 §11 全量继承）

基线规模 10,000 用户 / 100 商品 / 百万比赛记录；压测 2 分钟预热 + 10 分钟正式 + 冷却，基线过后 30–60 分钟 soak；读取 p95 ≤ 150ms / 购买与配装 p95 ≤ 300ms / **结算 p95 ≤ 500ms**（本轮最重事务）/ 登录 p95 ≤ 800ms；5xx < 0.1%；连接池不耗尽；点查询命中唯一键。受硬件限制未达标须报实况不得删指标。

### 5.5 压测后对账（15 号 §11.4 全量保留，追加）

钱包余额 = 流水累计 + 初始基线；每购买恰一条扣款流水；**每局结算至多一条 MatchReward 流水且 MatchRecord 快照 = 实发三币**；**通行证等级可由 PassXP 流水重放推出且 `PlayerPassRewardGrant` 无重复无缺失**；**成就无重复发放**；无负币、无孤儿记录、无非拥有装配。

### 5.6 完成定义（DoD）

- [ ] 后端与 Unity 编译零错误；自动化测试全绿（MySQL 核心集成不得跳过）
- [ ] 两库迁移一致；对账全通过
- [ ] M4/AK 初始拥有；商城武器双门槛（等级+金币）不可绕过；**配件只能经通行证获得且自动发放恰好一次**
- [ ] **1v1 击杀竞赛完整可演示**：建房→对战→20 杀终局→结算三币→回大厅数值刷新
- [ ] 枪械改装真实闭环：两把演示武器配件装配保存→重登恢复→属性条与 3D 模型同步变化
- [ ] §3.5 全部页面可访问，无死按钮、无空白页、无 release 假数据；仓库=已拥有武器、商城=纯武器、通行证页含奖励轨+成就
- [ ] 技能点入口已从 UI 移除且后端 API 不回归
- [ ] 双分辨率截图核验 + 端到端演示路径录屏
- [ ] 最终报告：改动文件、测试证据、未实现配件缺口清单、剩余风险

---

## 6. 实施阶段与并行开发

### 6.1 阶段划分与退出条件

> 阶段是风险隔离点不是缩水依据；串行闸门见 §6.2。

| Phase | 内容 | 退出条件 |
|---|---|---|
| 0 保护现场 | 提交当前未提交改动（Day4 修复 + Account/UI 契约草案）；记录基线（后端 build/test、Unity 编译、场景状态）；禁 reset/clean | 工作区干净，基线已记录 |
| 1 数据库 | 固定 `dotnet-ef`；§4.3 全部实体/约束/索引/迁移；Seeder（目录+奖励轨+成就）与回填；test 库先行，开发库后应用 | 两库迁移历史正确；无孤儿/负余额；回填幂等 |
| 2 后端服务 | §4.4 全部 API；购买/结算/通行证/成就事务；错误码/ProblemDetails/Swagger/健康探针；注册初始化改造 | 契约+集成+关键并发测试通过 |
| 3 Unity 数据层 | ApiClient 新端点实现；ItemId→AssetKey→Prefab/Icon 映射；配件兼容/校准配置载体；认证失效/取消/超时/错误映射 | 真实 API 拉通档案/目录/库存/配装/通行证，无假数据 |
| 4 全量 UI | §3.5 全页面全状态；LobbyBootstrap 分层重构；InputSystemUIInputModule 修正；视觉复刻与适配 | 启动→注册→大厅→购买→改装→**对战→结算→回大厅** 闭环可演示 |
| 5 联机对战 | FishNet 接入：房间码建房/加入、Player 同步、服务器权威战斗、比分与 20 杀终局、结算提交链路 | §5.3 联机清单通过；M 检查点=局域网双人完整一局 |
| 6 验证与压测 | §5 全量执行：并发/压测/对账/截图/联机回归 | §5.4/§5.5 达标 |
| 7 文档交付 | ER/表说明、API 契约、页面导航、迁移 ID 与测试命令、未实现配件缺口、改动清单 | 新窗口免读聊天历史可复现 |

### 6.2 并行流与串行闸门

**六条并行流**（文件树互不相交，可分配给不同执行者/分支）：

| 流 | 范围 | 依赖 | 独占热点（单写者） |
|---|---|---|---|
| A 后端 | Phase 1–2 全部（`fps-backend/`） | 契约冻结 | `Entities.cs`/`AppDbContext`/`Contracts.cs`/`Mapping.cs` |
| B Unity 数据+资产 | Phase 3（`Account/`、目录映射 SO、图标） | 契约冻结 | `Account/` 目录 |
| C UI 全量 | Phase 4（`UI/`、`Lobby.unity`） | B 的接口签名（可先对契约开发） | `LobbyBootstrap.cs`、`Lobby.unity` |
| D 配件校准 | 两个 FP prefab 挂点校准 + CalibrationKey 数据 + 兼容矩阵 | CalibrationKey 格式约定 | `FP_Rifle_View.prefab`、`FP_ServicePistol_View.prefab` |
| E 联机对战 | Phase 5（`NetworkAdapter/`、`Gameplay/` 对战规则、`Arena.unity`） | **零后端依赖**，仅 FishNet；结算提交末端才接 A | `Arena.unity`、MatchLifecycle |
| F 测试+文档 | k6 脚本、对账 SQL、并发用例编写、EditMode 用例、API 表/ER 文档 | 对 §4 契约编写，不等实现 | 无 |

**串行闸门（不可并行）**：

1. Phase 0 提交现场（脏工作区上不能开并行分支）。
2. **契约冻结**：§4.3 表结构 + §4.4 API/DTO + `CalibrationKey` 格式，一次性定稿 commit——全体并行流的上游。
3. 迁移落库（`Entities/AppDbContext/Snapshot` 公共热点，Phase 1 一次 commit 收口，之后 A 流内部才能按 钱包购买/结算通行证/配装/错误码探针 分片并行）。
4. 压测与对账的**执行**（区别于编写）必须等 Phase 2 完成。
5. 结算链路集成点：E 流的 `POST /api/matches` 调用对接 A 流服务（两端各留契约测试兜底）。

**约束**：Unity 侧单编辑器实例（多工具链同挂一编辑器有卡死前科，07 号 §16 案例入档），C/D/E 流实为任务切分防冲突；真正无代价并行轴 = **后端（A、F 部分）× Unity（B/C/D/E、F 部分）**。

---

## 7. 风险与降级线（本轮增量）

| # | 风险 | 等级 | 缓解 / 降级线 |
|---|---|---|---|
| 1 | 结算事务复杂度（四表联动 + 成就→升级收敛循环） | 高 | 固定锁顺序 + 幂等三兜底（ClientMatchId / RewardGrant / GrantedPassXp）+ 收敛循环上限（如 3 轮）防死循环；并发用例先行 |
| 2 | FishNet Prediction 学习曲线 | 高 | NetworkTransform 硬回退线（局域网可接受）；Prediction 仅作增强专窗 |
| 3 | 通行证数值失衡（过肝/过快） | 中 | 曲线/奖励全部集中配置 + 单测；基准按「约 30 局满通行证」校准，可热调 |
| 4 | 配件校准工作量超预期（挂点/缩放/FP-TP 双视图） | 中 | 只保两把演示武器的硬承诺；其余武器「尚未适配」诚实呈现（原则不容妥协） |
| 5 | UI 全量页面规模（16 页 × 全状态） | 中 | 通用组件先行（遮罩/Toast/弹窗/重试），页面按 §6.2 C 流切分；禁止只交半成品由并行流保底 |
| 6 | MySQL 9.5 provider 兼容 | 中 | 探针不过即切 8.4，不以攻 provider 消耗排期（07 号既定） |
| 7 | 双项目并行疲劳/RE4 插入 | 中 | 每日 RE4 ≤1.5h；里程碑滞后砍功能不砍架构 |
| 8 | 技能点删除引发回归 | 低 | 本轮零删除只冻结；删除走独立迁移 + test 库演练 |

---

## 附：与历史文档的关系速查

- 15 号：经济/商城/装配/压测的**字段级**规格仍以它为准（§5、§7.2、§7.4、§11、§12）；配件商城化、四表限定、技能点演进等已被本文 §0 修订。
- 04 号：网络状态/事件/请求三分、开火链路细节；03 号：FSM/动画/动画事件规范；07 号：运行时测试授权制、编辑器多工具链纪律、每 Job 提交制度——全部继续有效。
- 16 号：单窗口连续执行提示词模板；如转多流并行，按本文 §6.2 拆分后为每流生成独立提示词。
