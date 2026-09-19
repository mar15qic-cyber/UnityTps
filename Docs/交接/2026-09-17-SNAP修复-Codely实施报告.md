# 2026-09-17 SNAP 修复实施报告（Codely）

日期：2026-09-17。票据来源：`Docs/交接/2026-09-17-SNAP静态全量代码审计报告.md` §15 修复清单（用户令按报告制定计划并实施）；基线 = `2026-09-17-远端表现两问题-Codely修复报告.md`（协议 fps-net-v4）。主工作区脏区直改，**未提交 git**。

## 1. 修复清单（按审计 ID）

| 审计项 | 严重度 | 修复 | 实施位置 |
|---|---|---|---|
| **D1** | Critical | **Snap/Smooth/None 双阈值判定改用 corrected 误差**（raw − 在途平滑量）。`Reconciler` 新增 5 参重载 `Decide(server, predicted, inFlight, out raw, out corrected)`（内部 `corrected = raw − inFlight` 后判 0.03/0.75），旧三参重载保留（等价 inFlight=0，既有测试零改动）。`ApplyOwnerAuthoritativeState` 把 `ConsumeInFlightAfter` 提前到判定前，Snap/Divergence/Smooth/None 全部走 corrected；SNAP/DIVERGENCE 日志行打 `err=/raw=/inflight=` 三值 | `MovementPredictionCore.cs`（Reconciler 重构 + `CompleteDivergence` 共用提取）、`PlayerNetworkAdapter.cs`（判定序）、`MovementDiagnostics.cs`（G3：`LastErrorMeters`=corrected 口径 + 新增 `LastRawErrorMeters`，`[MoveDiag]` 行 `err=`/`errRaw=` 成对留证） |
| **D2** | High | **服务器空队列改"保持位姿"（hold）**——删除 `ServerInputExtrapolation` 类/`ServerInputExtrapolationMaxTicks` 常量/空命令模拟/`_extrapolatedSteps`/EMPTY step 证据。空 tick 完全不模拟（位姿/速度/步态保持），快照照发（重复 ACK 由客户端门忽略）；`IDLE_START` 小窗口取证保留；`Snapshot.Tick == LastClientTick` 配对不变量恢复，`IdleStepsAtSnapshot` 降级为纯诊断字段 | `PlayerNetworkAdapter.cs`（ServerTick 拆为编排层 + 可 EditMode 直驱核心 `AdvanceServerInputs(bool applyRemotePitch)`）、`MovementPredictionCore.cs`（删类/常量）、`MovementDiagnostics.cs`（删 `ExtrapolatedSteps`/`extrap=`） |
| **R1** | High | **服务器有限追赶**：`Drain(1)` → `Drain(ServerMaxCatchUpPerTick=3)`（接线既有未用常量）。突发积压（客户端单帧 5 步）数 tick 内追回，lead 不再永久抬升；追赶仍逐步 fixedDelta 模拟，Snapshot.Tick 与 LastClientTick 仍一致 | `PlayerNetworkAdapter.AdvanceServerInputs` |
| **D3** | High | **冻结窗口拒收在途批次**：`ServerSubmitInputBatch` 拆出 `EnqueueServerInputBatch`（RPC 载体与逻辑分离），开头 `MovementFrozen`（倒计时/死亡）直接丢弃并计数（`ServerInputQueue.DroppedFrozen` + `[MoveDiag]` `dropFrozen=`）。死亡前"已发出未确认"的旧命令不再在重生后作为新 epoch 输入被消费。**不动 wire 契约**（epoch 上契约留待协议 v5 窗口）；每 tick `Clear()` 兜底保留 | `PlayerNetworkAdapter`、`MovementPredictionCore.ServerInputQueue`、`MovementDiagnostics` |
| **D5** | Low | `ResetOwnerPredictionState` 补 `_ticksSinceSend = 0` | `PlayerNetworkAdapter` |
| **R4** | Medium | `WireServerTick`/`UnwireServerTick` 幂等旗标 `_serverTickWired`（防同实例重复 OnStartServer 双订阅=时间轴压缩一半）；顺带修 `TimeManager` 直取在无 NetworkObject 时 NRE（改 `NetworkObject == null` 前置短路，与既有诊断旁路惯例一致） | `PlayerNetworkAdapter` |
| **R5** | Medium | `HardSnapTo` 重放缺口守卫：新增 `PredictionBuffer.InvalidateCommandsAbove(uint)`（作废缺口上方命令 + NewestTick 回拨）；缺口分支作废快照**与命令**后 `_localTick = replayed`（取代 `max(replayed, newest)`——旧"不回退避免 TryStore 拒绝"的顾虑依赖"命令仍在缓冲"，作废后不再成立）。无缺口时行为不变（replayed==newest） | `MovementPredictionCore.PredictionBuffer`、`PlayerNetworkAdapter.HardSnapTo` |
| **G1** | High | `[MoveStep]`/`[MoveStepWindow]`（c/s 两端）补 `conn=/obj=` 身份字段（`MovementStepSample.Conn/ObjId` + `StepIdentity()` 助手；无网络/离线 = -1/0）——跨玩家取证可程序化归属（上一轮审计曾因无身份误读） | `MovementStepTrace.cs`、`PlayerNetworkAdapter`（两个 Capture + 两个 Export） |
| **G2** | Medium | 会话累计口径与区间口径分离：`[MoveDiag]` 新增 `emptyTotal=/smoothTotal=`（`ServerEmptyStepsTotal`/`SmoothsTotal`，不随 ResetInterval 清零；`snaps=` 区间 + `rebase=` 会话原样保留） | `MovementDiagnostics.cs` |
| **G4**（轻量版） | Medium | `[MoveStep]` 行补 `yw=/pt=`（YawDelta/PitchDelta）——两端口径完整的输入签名，同 tick 输入对账不再需要独立哈希 | `MovementStepTrace.cs`、`PlayerNetworkAdapter` |

### 1.1 D2 设计决策记录（审计三选一，选③强化版）

审计 §15.2 给了三个语义：①按 `Snapshot.Tick` 配对——被拒（菜单冻结期客户端无该 tick 预测 → HistoryMissing 反而把应忽略的快照变成硬对位）；②按 `IdleSteps×步长` 扣除——被拒（k 步位移含碰撞解算，无法从输入重建）；③外推期间不下发位置推进——**采纳强化版：空 tick 完全不模拟**。hold 后：菜单/聊天冻结两端同静（误差恒 0，当前代码开菜单 >0.27s 必产 ≈0.9m+ 误差）；瞬时到批间隙 1–3 步位移 ≤0.34m 落平滑带由平滑收敛 + R1 追赶立即回填；真实长断流仍按设计走 SNAP。

## 2. 改动文件

- `Assets/_Project/Scripts/Gameplay/Movement/MovementPredictionCore.cs`（Reconciler 新重载 + CompleteDivergence 提取；删 ServerInputExtrapolation/常量；ServerInputQueue +DroppedFrozen/NoteDroppedFrozen；PredictionBuffer +InvalidateCommandsAbove）
- `Assets/_Project/Scripts/Gameplay/Network/PlayerNetworkAdapter.cs`（判定序重构；ServerTick 拆 AdvanceServerInputs/EnqueueServerInputBatch；WireServerTick 幂等；HardSnapTo 缺口守卫+可选 errorMeters；Reset 补 _ticksSinceSend；取证身份/输入字段接线；删 _extrapolatedSteps）
- `Assets/_Project/Scripts/Gameplay/Movement/MovementDiagnostics.cs`（errRaw、LastRawErrorMeters、emptyTotal/smoothTotal、dropFrozen；删 ExtrapolatedSteps）
- `Assets/_Project/Scripts/Gameplay/Movement/MovementStepTrace.cs`（Conn/ObjId/YawDelta/PitchDelta + Format）
- 测试：`MovementPredictionCoreTests.cs`（D1 纯函数 4 例）、`PlayerNetworkAdapterReconcileTests.cs`（D1 接线 1 例 + R4/R5 各 1 例）、`MovementBatchAndGapTests.cs`（外推用例改写为 idle 计数 + hold/Drain(3)/frozen gate 接线 3 例）、`MovementDiagnosticsTests.cs`（格式断言更新 + 会话累计 1 例）

## 3. 验证证据

- **编译**：0 错（25 警告全存量，与本轮改动无关）。
- **定向 EditMode**（Phase 1+2 一轮 70/70；Phase 3+4 一轮 99/100→R4 测试暴露 `TimeManager` 直取 NRE→产品代码修复→复跑 25/25）。
- **全量 EditMode**：**852/861，9 失败全为既有 DedicatedServer* persistentDataPath 环境伪影**（与基线逐名一致，零回归；总量 820→861 = 新增 41 用例全绿）。
- **双端同批重建**（协议**不变 fps-net-v4**——本轮无 RPC/DTO/SyncVar 形状变更）：DS `buildId=f33328ca594f`（172MB）/ Client `buildId=ea87c6a7dc5d`（209MB）；5 设置文件（含 DynamicsManager）备份还原 + MD5 校验 5/5（`Logs/BuildSettingsBackup_20260917_013522`）。
- **运行栈**：后端 5080 复用（health mysql ok）；DS 新构建已起（pid 43928，`APP_PROTOCOL id=fps-net-v4 buildId=f33328ca594f` 与磁盘 manifest 一致，REGISTERED state=Ready，port 7770）。
- **新增 41 用例关键反例**：raw 0.78 − inFlight 0.20 = 0.58 → **Smooth 不得硬对位**（审计 §7.1 最小反例的接线锁）；空 tick 位姿钉住（hold）；9 条积压 3 tick 清空（Drain(3)）；冻结窗口批次拒收计数；重放缺口 `_localTick==replayed` 且缺口上方命令作废。

## 4. 搁置项（本轮明确不做，后续票据入口）

- **D4（RecoilDebt 帧率依赖）**：恢复由 `WeaponController.Update` 帧驱动、消费在固定 tick——彻底修复三条路（债务演化 tick 化 / yaw 消费移出 Simulate / 服务器侧同源化）全部触及开火手感语义；且 `WeaponRecoilState(seed)` 的 yaw 随机 **seed 两端是否同源未核**（若不同源，帧率只是次要项）。待本轮实机复测后：若射击相关 SNAP 仍存，立独立票据（先核 seed 同源性）。
- **R2/R3（世界回溯/远端碰撞代理）**：玩法级设计，用户已有"先不做保持现状"决定（2026-09-15）；方案A 已实机定案。
- **R6（快照 Pitch 两端不同源）**：影响瞄准不影响位置（pitchGap 残余）。
- **Epoch 上契约（D3 彻底版）**：本轮服务器 gate 已关闭可达路径；wire 变更留协议 v5 窗口（须 ProtocolId 递增 + 双端同批重建纪律）。

## 5. VERIFY_PENDING（用户实机清单）

判据日志：客户端 `启动客户端.cmd` ×2（独立 -logFile），按行内 conn/obj 归属（服务器行现在也带 conn=/obj=）。

1. **菜单/聊天冻结**：开 ESC 菜单或聊天输入 ≥3s 再关闭——不得出现拽回/SNAP（历史必触 0.75m+ 误差）；`[MoveDiag]` 该段 `snaps=` 恒 0。
2. **追击/近距互推**：追着对方跑+贴身推挤——不得出现"每 7 tick 一次"的硬对位风暴；`smooth=` 大量命中、`err=`/`errRaw=` 常态 <0.1m 且 `errRaw−err`（在途量）非零。
3. **本机卡顿**：故意后台压帧/开大录制造成一次长帧——`lead=` 跃升后应在数秒内**自动回落**（R1 生效证据；旧版永久抬升）。
4. **重生**：死亡→重生后立即移动——不得出现短暂"输入无响应后瞬移"（D3 生效证据）；死亡期间服务器日志应见 `dropFrozen=` 计数增长。
5. **远端观感回归**：远端走动丝滑、TP 俯仰保持（上一轮修复不得回退）。
6. **真实断流退化路径**（预期行为说明）：客户端断流 >0.75m 位移时仍会一次 SNAP——这是设计（不可预测断流只能硬对位），修复的是"无断流也 SNAP"的虚假路径。

## 6. 部署与入口

- 客户端唯一测试入口 = 项目根 `启动客户端.cmd`（横幅已提示勿直接双击 exe）；服务器栈 = `启动联机服务器.cmd` / `停止联机服务器.cmd`。
- 后端 5080 与 DS 7770 现已就绪（DS=新构建 f33328ca594f，Ready）。客户端用本轮新构建 ea87c6a7dc5d（`Builds/ReleaseClient`）。
- 协议仍 fps-net-v4：旧客户端（df89b68fe4e4）连接新 DS 不会因协议被拒，但**不含本轮修复**——双端实测请都用新构建。
