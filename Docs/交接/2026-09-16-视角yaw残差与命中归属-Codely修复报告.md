# 2026-09-16 视角 yaw 残差（左右偏移）与命中归属（打中不掉血）— Codely 修复报告

状态：**已实现 / 已编译 / 定向回归 27/27 / 双端已重建并部署就绪**；**实机双人复测待用户**。
工作区：主工作区脏区直改（未提交，与用户历史改动混同文件保护）。

---

## 0. 证据来源（本轮排查素材）

| 对象 | 路径 | 身份 |
|---|---|---|
| 客户端 A | `Tools/Client/Logs/client-20260916_185209-b927f0.log` | pid 23216 / user 67「1234」/ 服务器 conn=1 |
| 客户端 B | `Tools/Client/Logs/client-20260916_185209-66ed70.log` | pid 30240 / user 741「YYLL」/ 服务器 conn=0 |
| 专用服务器 | `Tools/Server/Logs/20260916_175319/server.log` | pid 36224 / buildId 60eae905beab / 单局 match=`b388c319…f9750`（TDM，298s，reason=PlayerLeft） |

（本局为 17:53 起 DS 生命周期内**唯一**一局：日志 7737 行中 match id 只有该局 + 上局一条补偿记录。）

---

## 1. 排查结论

### 1.1 「请求是否发到服务器 / 是否被服务器吞掉」→ **全部发到，服务器按设计闸拒了 96%**

服务端 `[FireTrace]` 行数 = **6228 reject + 248 resolve = 6476**，与两台客户端 `[FireTrace] submit` 之和（4573 + 1903 = 6476）**逐 id 完全对齐**——不存在丢包或吞请求。

| 连接 | submit（客户端 FireHeld 每帧发） | reject（`CanAttemptFire=false`） | resolve（真正开火） | 造成伤害 |
|---|---|---|---|---|
| conn=1 =「1234」 | 4573 | 4398（96.2%） | 175 | **8** |
| conn=0 =「YYLL」 | 1903 | 1830（96.1%） | 73 | **20** |

reject 原因唯一：`冷却/弹药/动作槽忙碌——未回溯`（`WeaponController.CanAttemptFire` 的廉价准入闸，2026-09-16 §6.4 引入）。**这是设计行为**：`PlayerNetworkAdapter.HandleOwnerCombatRequests` 在 `FireHeld` 时每帧提交一次，服务器只在自身武器冷却结束后接受。

### 1.2 根因 A（同时解释「左右偏移不走直线」与「打了不中」）：**视角 yaw 超前量 R 粘滞不衰减**

`MovementPredictionConfig.ViewYawClampDegrees = 10f`。修复前：**累积端钳制、扣减端不钳制**：

```csharp
_viewYawDegrees = Mathf.Clamp(_viewYawDegrees + lookYaw, -10f, 10f);  // 有损
...
_viewYawDegrees -= cmd.YawDelta;                                       // 无损（完整 _pendingYaw）
```

一次饱和即产生残差 `R = 超前量 − 未提交输入`，而**不饱和的帧严格保持 R 不变**（可代数证明），只能靠死亡/重生复位清零。证据：

- **MoveDiag `yawLead` 整段冻住**：1.50（t=84→120，36s）、2.30（95→117）、3.29（137→169）、4.50（55→81），以及**恰好 ±10.00**（=钳制值，快甩鼠标饱和）；清零点一律对应 `REBASE DeathRespawn` 或冻结复位。
- **90 发逐发对账**（服务端 `[FireGeom] … | cam o=… d=…` 与同 id 客户端 `[FireTrace] submit … aimD=…` 的夹角 vs 该时刻 MoveDiag `yawLead`）：

| shotId | 客户端瞄准 − 服务器射线 Δyaw | 同期 yawLead |
|---|---|---|
| 233 | −4.91° | 3.59° |
| 518 | −7.96° | 6.80° |
| 556 | −9.54° | 6.80° |
| 834 | −6.44° | 6.20° |
| 1105 | −7.12° | 8.10° |
| 1494 | −5.80° | 5.36° |

→ **|Δyaw| ≈ |yawLead|（30/30 命中，最大 13.5°，均值 3.6°）**。

机制：客户端视觉/准心被 R 转走（`_viewOffsetRoot.localRotation` 承载，`CameraPivot` 挂其下），而**服务器射线沿身体 yaw**（`ServerFireRequest` 只上传 `estimatedServerTick`，方向由服务器自算）。于是：
- 按 W 沿身体前进、与视线夹角 R → **不走直线**；
- 准心对着人开火，服务器射线偏 R 度（12m 处 10° ≈ 2.1m）→ **打了不掉血**。
- 「1234」的 R 常驻饱和 ±10° → 命中率 4.6%；「YYLL」R 仅 3–4.5° → 27%。**这正是用户观察到的"一方完全不掉血"的角色不对称。**

### 1.3 根因 B（真打中也不掉血）：命中归属回退容差 5cm 过小

服务端绊线 **`[CombatRay] HIT_NO_TARGET victimRootCollider=Player_Day2_Rebuilt(Clone) … hits=2` 触发 8 次**；`[FireGeom]` 另有 5 条 `reason=NO_TARGET decision=UseMuzzleHit finalCollider=Player_Day2_Rebuilt(Clone) finalTarget=null`——**枪口射线确实命中敌玩家碰撞体，却零伤害**。

`CombatResolver.ResolveAttributionFallback` 假设"根 CC 与 BodyHitbox 同体积共心"，用 `±0.05m` 容差在同根其余命中里找可归属碰撞体。**PlayMode 真实射线实测**（CC r0.35/h1.8/skin0.08 + 同参数 BodyHitbox 胶囊，1096 组几何扫描）：

```
raycast hits=2
   PROBE4_BodyHitbox [CapsuleCollider] dist=4.6838
   PROBE4_Player    [CharacterController] dist=4.8961    ← 差 0.2123m ≫ 0.05m
```

实测最大差 **0.2946m**（典型 0.04–0.27m），远超 5cm 假设 → 最近命中落在根 CC 时归因失败 → `NO_TARGET` → 零伤害。
附带事实：**CharacterController 必须真正 `Move()` 过才会被 `Physics.RaycastNonAlloc` 命中**（静止时命中列表里没有它）——这解释了"编辑器静置复现不出、实机跑动才炸"。

### 1.4 根因 C（同一批证据，**未修，另立票据**）：俯仰/后坐债务两端不同步

同批 90 发 `Δpitch` 均值 **−5.23°**（极值 −18.2°），与 MoveDiag `pitchGap` 量级一致（「YYLL」开局整段冻在 5.80°）。客户端瞄准方向含本地 `WeaponRecoilState.OffsetRotation`（补偿债务），服务器含它自己的债务，仅在 rebase 快照时对齐 → 持续开火时垂直方向系统性偏差。审计已列为遗留项，本轮不做（需动契约/同步频率）。

---

## 2. 修复内容

### F1 视角 yaw 超前量（客户端）

`Assets/_Project/Scripts/Gameplay/Network/PlayerNetworkAdapter.cs`

- 新增三件套接缝（唯一写入点语义，含完整根因注释）：
  - `AccumulateViewYawLead(lead, lookYaw) = lead + lookYaw`（**无损**）
  - `ConsumeViewYawLead(lead, committed) = lead − committed`
  - `ResolveViewYawForDisplay(lead) = Clamp(lead, ±ViewYawClampDegrees)`（**唯一钳制点**）
- `Update()` 累积改走 `AccumulateViewYawLead`；`RunOwnerPrediction()` 扣减改走 `ConsumeViewYawLead`；`ApplyOwnerVisualOffset()` 写节点改走 `ResolveViewYawForDisplay`。
- 不变量：**提交后超前量恒为 0**；显示角度仍被 ±10° 兜底；视觉/准心与身体 yaw 同轴 → 服务器弹道与准心同轴。

`Assets/_Project/Scripts/Gameplay/Movement/MovementPredictionCore.cs`：`ViewYawClampDegrees` 文档改为"只限制写入视觉节点的显示角度，绝不可钳制累积量"（防回归）。

### F2 命中归属兜底（服务器权威路径 + 离线共用）

`Assets/_Project/Scripts/Gameplay/Combat/CombatResolver.cs`

- 新增第二层归属 `ResolveDescendantTarget(hitTransform, ignoreRoot)`：命中对象（典型=玩家根 CC）父链上无 `DamageableTarget` 时，在**该命中对象自身子树内**找 `DamageableTarget` 归因，用本次命中点/法线/距离。
- 第一层（5cm 同根共面）保持原样并注明"只负责几何共面时的精确落点"；**不同根绝不归属**不变量不变（搜索限定命中对象子树 + 射手根排除）；死亡/友军过滤仍在下游统一处理。
- 绊线补 `colliderType=`（两层兜底都失败才打印 = 资产缺口信号）。

---

## 3. 验证

| 项 | 结果 |
|---|---|
| 编译 | **0 错**（新增 0 警告；仅存量 CS0067/CS0414 与 Unity/XLua/FishNet 既有警告） |
| 定向 EditMode | **27/27 通过**（`HitAttributionFallbackTests` 4 + `ViewYawLeadResidueTests` 4 + `TwoStageHitPhysicsTests` 5 + `CombatResolverSelfHitTests` + `BodyHitboxAlignmentTests`） |
| **红测证明** | 临时禁用 F2 第二层后复跑：`passed=2 failed=2`——`RootBodyCollider_WithoutAnyAttributableChildHit_DamagesTheOwner`（Expected True / But was False）与 `ShooterOwnRoot_NeverSelfDamages` 如期变红；还原后 **27/27 全绿** |
| 新增用例 | `Assets/_Project/Tests/EditMode/HitAttributionFallbackTests.cs`（根碰撞体命中归属 / 子 hitbox 父链不回退 / **无目标对象绝不跨根归属** / 射手自身不自伤）；`Assets/_Project/Tests/EditMode/ViewYawLeadResidueTests.cs`（累积无损、显示钳制、提交后残差=0，并显式给出旧语义残差 −26° 的反证） |
| 双端构建 | DS `buildId=12a4093078bd`（172.4 MB，`protocol=fps-net-v4`）、Client `buildId=4ee7487937c8`（209.4 MB，3 场景） |
| 程序集标记扫描 | 双端 `Game.Gameplay.dll` 均含 `AccumulateViewYawLead` / `ConsumeViewYawLead` / `ResolveViewYawForDisplay` / `ResolveDescendantTarget`；`colliderType=` **仅 DS** 命中（`#if UNITY_SERVER && !UNITY_EDITOR`，条件编译签名正确） |
| 构建后设置还原 | 4 文件（GraphicsSettings / ProjectSettings / PC_RPAsset / UniversalRenderPipelineGlobalSettings）按 `Logs/BuildSettingsBackup_20260916_194132` 还原并 MD5 校验 `match=True` ×4 |

**协议未变**：F1/F2 不改 RPC 签名/载荷/DTO，`GameProtocolIdentity` 仍为 `fps-net-v4`，无需递增、无跨版本兼容问题。

## 4. 部署现状（可直接实测）

- 后端：`http://127.0.0.1:5080`（pid 18512，复用未重启）
- DS：pid **28280**，`instanceId=local-dev-01`，`port=7770`，日志 `Tools/Server/Logs/20260916_200009/`
  - `[DedicatedServer] APP_PROTOCOL … pid=28280 buildId=12a4093078bd` ✔ 与磁盘 manifest 一致
  - `DS_READY` → `REGISTERED state=Ready protocol=fps-net-v4` ✔
  - 上局遗留补偿已冲销（`MATCH_RESULT_COMPENSATED match=b388c319… outcome=StateConflict`）
- 客户端：`Builds/ReleaseClient/UnityFpsClient.exe`（buildId 4ee7487937c8）——**唯一入口 = 项目根 `启动客户端.cmd`**（独立日志到 `Tools/Client/Logs/`）

### 4.1 构建期发现的设置副作用（已处理，值得记录）

第一次构建时（19:41:45，与 DS 构建窗口重合）编辑器把 **`ProjectSettings/DynamicsManager.asset` 重新序列化**：`serializedVersion 13→23`，且**静默把 `m_AutoSyncTransforms: 0 → 1`**（Unity 6 新 schema 的默认值回填）。该文件不在既有的"构建会写脏 4 文件"清单里，因此**未被备份**；本轮按 HEAD 还原（`git checkout`，该文件改动可确证来自本次构建窗口，非用户改动），随后**重新构建双端**以保证交付二进制与该文件的历史配置一致（`AutoSyncTransforms=0`）。

- 还原后复跑双端构建，`DynamicsManager.asset` **MD5 未再变化**（`6E3BF82B…`，仍为 v13/autoSync=0）→ 该写入是"schema 升级一次性回写"，不是每次构建都会发生。
- **建议**：把 `ProjectSettings/DynamicsManager.asset` 加入构建前备份清单（第 5 个文件）——它含 `m_AutoSyncTransforms`，直接改变物理查询与变换同步语义，一旦被静默改写会与"移动/命中"类改动混叠、污染实机对照。
- 本轮交付的 buildId 与第一次构建相同（`12a4093078bd` / `4ee7487937c8`，游戏代码未变），仅设置状态不同 → 报告中的 buildId 引用均有效。

## 5. 实机验收判据（用户，双开 v4 新客户端）

1. **走直线**：鼠标静置按 W/冲刺前进 20m，视线与移动方向不得存在可见夹角；`[MoveDiag]` 的 `yawLead` 必须常态读 **0.00**（旧值 1.5–10 整段冻住即残留）。
2. **准心=弹道**：同一发 `[FireTrace] submit … aimD=` 与服务器 `[FireGeom] … | cam d=` 的水平夹角（Δyaw）应 **< 1–2°**（旧局均值 3.6°、极值 13.5°）。
3. **打中掉血**：中近距离互射应稳定掉血；服务端不应再出现 `[CombatRay] HIT_NO_TARGET`（若出现，新增的 `colliderType=` 字段会指明是什么碰撞体）。
4. 关切旁证：`[MoveDiag] snaps/rebase` 不应因本轮改动变化（F1 只动表现层超前量，不动模拟根/对位）。

## 6. 遗留与未做

- **根因 C（俯仰/后坐债务两端同步）未修**——建议单独票据（会动 RPC 载荷或同步频率，需协议递增 + 同批重建）。
- 「客户端每帧提交开火请求」的冗余带宽（96% 必被拒）未动；如需降本可改为"仅在本地开火事件时提交"（属优化，非本轮缺陷）。
- 上一会话遗留的 **服务端开火速率 vs 客户端本地射速一致性**未独立核实（本轮面板数据：hold 窗口 ≈30s、接受 175 发 ≈5.3/s）。若实机仍觉"开了枪但服务器没开火"，需加一个客户端本地开火计数与服务器 resolve 计数对账。
- 主工作区赃区未提交（用户历史改动混同文件，按惯例由用户点名提交）。
