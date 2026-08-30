# 配件系统与 FishNet 联网实施计划（含 GPT 审计复核）

> 制定日期：2026-08-30 ｜ 文档状态：执行基线 v2（用户修订：N 线 FishNet 先行）
> 输入：用户目标（39 枪全量改装 + 配件驱动 FP 动画 + 镜内视野 + FishNet 联网）+ GPT 只读审计意见
> v2 修订：29 把 LPW 武器 idle/ADS 视觉未过实机验收（GPT 修复因额度中断）→ FishNet 优先；配件线后置并以 LPW 视觉修复为前置。
> 复核方法：全部结论经引擎内探针/源码实测（AssetDatabase 全量遍历 42 个 WeaponDefinition、LoadoutService 源码、LPW 配件池目录扫描、Docs/04/07/18 交叉引用），非转述。
> 关联：Docs/04（网络设计，Host 模式决策）、Docs/15（商城/配件数据层）、Docs/18（ADS 动画 Phase 0+1+§12 已完成）

---

## 1. 对 GPT 审计的复核结论

### 1.1 确认成立的部分（实测一致）

| # | GPT 结论 | 复核证据 |
|---|---|---|
| 1 | FishNet 仅导入、无业务接入 | `Scripts/NetworkAdapter/` 只有 `.gitkeep`；业务代码零 NetworkBehaviour；Day7 在 Docs/07 仅为路线表 |
| 2 | ASP.NET 是账户/经济服，非实时游戏服 | 属实——这是设计而非缺陷（见 §1.2-③） |
| 3 | 后端配件接口全线硬拒绝 | LoadoutService.cs L56-58：LPW→"配件尚未适配"、非空请求→"该武器暂无已验证配件"；GetCompatibilityAsync 恒空 |
| 4 | 仅 2 把枪 supportsVerifiedAttachments=1 | WeaponAssetCatalog.asset 实测：仅 weapon.m4 / weapon.service_pistol；LPW 管线三处强制 false（L387/L685/L769） |
| 5 | FPWeaponAttachmentView 是硬编码演示 | 只认 2 瞄具 ID + 2 消音器 ID；ResolveStableWeaponId 仅 3 条映射；无 Grip/Stock/Tactical/Laser/Light 槽 |
| 6 | 无镜内视野 | Docs/18 Phase 3 未实施；RenderTexture 仅大厅预览用 |
| 7 | 39 = 10 LPFP + 29 LPW | 目录实测一致（另有 3 把 LPWTest spike 枪不在此列） |

### 1.2 GPT 低估/过时的部分（探针推翻）

**① ADS 动画基础设施已就绪（GPT 最大盲区）**。实测：42 个 WeaponDefinition 中 **39 个 aim 四件套已填充（含全部 29 把 LPW 量产枪**；仅 3 把 LPWTest spike 枪未填）；FPAimAnimStateMachine 纯逻辑状态机 + 15 个 EditMode 用例已锁定语义；ADS 照门对位已按"Armature/camera 作者相机节点法"修复（残差 2.1mm，Docs/18 §12）。
**但注意**：clip 引用已填 ≠ 视觉验收通过——用户实机反馈 29 把 LPW 的 idle/ADS 视觉仍未过（姿态/对位/穿模问题），此为 F 线前置阻塞项（F0）。

**② 垂直握把→动画族切换已有半套机制**。WeaponDefinition 已有 `FirstPersonAnimationFamily` 枚举（Rifle01/02/03）、`rifleHasVerticalGrip` 字段、三族动画集字段。缺口只是：资产静态字段 → 运行时由 Underbarrel 配件驱动，以及 LPW 步枪补齐第二动画族集（clip 素材现成，纯填充脚本）。

**③ Dedicated Server 建议与项目既定架构矛盾，不采纳**。Docs/04 明确：Dedicated Server 评级 **"C 放弃"**，"FishNet 支持 headless build，当前用 Host 模式是 Demo 权衡"。正确路径：**client-hosted（房主即主机）**，ASP.NET 维持账户/经济/配装职责。

**④ LPW 配件资源池核实**：LPW 8 Optic + 19 Attachment（Muffler 消音器 2 型 ×6 口径 + Laser + Light + Mod×4 + PistolMod）+ LPFP 4 Scope + Silencer。素材真实存在。

### 1.3 修正后的现状基线

| 子系统 | 现状 | 就绪度 |
|---|---|---|
| 武器量产 | 39 量产定义 + FP/TP prefab + 数值 | ✅ 完成 |
| ADS 动画（clip 引用） | 39/39 量产枪 aim 四件套 + 状态机 + 对位 | ✅ 引用层完成 |
| **LPW idle/ADS 视觉** | **用户实机未验收（姿态/对位问题）** | ❌ **阻塞项 F0** |
| 配件数据层（后端） | 全线硬拒绝、矩阵空 | ❌ 未开始 |
| 配件表现（客户端） | 3 条硬编码映射演示 | ❌ 演示级 |
| 挂点/校准体系 | 无通用 socket | ❌ 未开始 |
| 握把→动画族 | 静态资产字段，无配件驱动 | ⚠️ 半套 |
| 镜内视野 | 无（但 ADS 底座就绪） | ❌ 未开始 |
| FishNet | 插件可用（FISHNET/FISHNET_PRO 符号），零业务 | ❌ 未开始 |

---

## 2. 目标与非目标

### 2.1 目标（用户规则固化，v2 优先级）

1. **本轮优先：FishNet 联网 N1~N4**，M7 里程碑（两人局域网互看互跑）为第一目标；
2. 39 把量产枪全部具备配件能力（后置）——"有槽位能力描述 + 至少一组真实可用改装"，不兼容组合拒绝；
3. 配件驱动 FP 动画（用户规则，后置）：垂直握把→**Rifle03（SCAR）** 动画族、无握把→**Rifle01（AK）**；拆装后 FP 重绑 + TP 左手 IK 同步；
4. 装瞄具→镜内视野（用户硬性要求，后置）：aim_scope_XX 变体 + 红点镜筒准星/放大 FOV 分档/狙击全屏 overlay；
5. 验证流程约束（用户指定）：镜内视野/配件视觉一律先 **Arena_LPWTest** 验证后进 Arena；
6. LPW 29 把 idle/ADS 视觉修复 = F 线前置阻塞项（F0，接手 GPT 中断处）。

### 2.2 非目标

- Headless Dedicated Server（Docs/04 既定放弃）；
- 39×33 全组合校准（只校准兼容矩阵放行的组合）；
- Refresh Token / 云部署 / 豪华房间系统（沿用 Docs/07 边界）；
- 3 把 LPWTest spike 枪不进商城目录。

---

## 3. 架构设计要点

### 3.1 网络架构（本轮主线，client-hosted，对齐 Docs/04）

```
房主 StartHost（即服务器+客户端） / 客人 StartClient（直连 IP 或房间码）
ASP.NET 职责不变：账户/JWT/商城/配装持久化；房间码=轻量注册表（可选，LAN 直连兜底）
战斗权威：Host 验证（冷却/弹药/动作槽）→ Raycast 命中 → 伤害广播（Docs/04 §6.6）
```

### 3.2 配件数据模型（后置 F 线）

- 6 槽（Optic/Muzzle/Underbarrel/Magazine/Tactical/Stock）；后端 AttachmentCompat 种子矩阵（权威放行/拒绝）；
- AttachmentDefinition SO：itemId/槽/prefab/数值修饰（接 WeaponStatResolver 既有管线）/动画族覆盖规则/瞄准变体索引；
- Attach_* 标准挂点 + AttachmentCalibration 每枪×配件局部位姿（对齐 Docs/15 CalibrationKey）。

### 3.3 配件驱动动画族（后置 F 线）

WeaponAnimationResolver（新组件）= WeaponDefinition 静态字段 + 配件覆盖规则（垂直握把→Rifle03）；FPWeaponAnimator.LoadClips 改读 resolver；切配件重绑 + ADS 中先收镜重入（复用 ResetToHip 语义）；TPLeftHandIK 目标按族切换。

### 3.4 瞄具变体与镜内视野（后置 F 线）

WeaponAnimationSet 增 AimScopeVariants[4]（19 族×4 变体 clip 批量填充）；FPWeaponAnimator 按 optic 索引选集（状态机零改动）；镜内视野三层：L1 红点=LPFP Scope Render Mesh+Sight Texture 模式移植、L2 放大=AdsFov 分档、L3 狙击=全屏 overlay；Scope Camera+RT 列可选增强。

---

## 4. 分阶段实施（v2：N 线先行）

### N 线（本轮执行）

**N1 FishNet 基础接入（1 天）**
1. NetworkManager 场景接线（Arena_Online 或 Arena 加网络对象 + Room UI 激活）；
2. Player 网络生成（房主/客人 spawn 规则）、NetworkTransform + 移动同步；
3. LocomotionState SyncVar → 远端 TP 动画（TPAnimDriver 已有，补网络侧）；
4. 验收：**M7——两人局域网互看互跑**（Docs/07 Day7 定义）。

**N2 武器状态同步（1 天）**
1. WeaponId 同步（切枪）、远端 TP 持枪切换（TPWeaponMeshSwapper 已有）；
2. 开火/换弹动画事件 RPC（OnShotFired/OnReloadStarted → 远端 TP 表现）；弹药 HUD 仅本地；
3. 验收：远端看到对方切枪/开火/换弹动画正确。

**N3 服务器权威战斗（1~1.5 天）**
1. FireRequest→Host 验证（冷却/弹药/动作槽，ActionSystem 服务器侧）→ 服务器 Raycast → 伤害广播（CombatResolver 服务器跑）；
2. 死亡/重生简单版；
3. 验收：双人对射命中一致、弹药/冷却服务器校验、本地改弹药作弊无效。

**N4 房间码与大厅接线（0.5~1 天）**
1. ASP.NET 轻量房间表（创建/加入/列表，JWT 鉴权）+ LAN 直连兜底；
2. Lobby→Play 打通（登录→选配装→进房→开战，配装来自后端 Loadout）；
3. 验收：两账号全链路对战。

### F 线（后置：待 N 线完成后启动）

**F0 LPW 视觉修复（阻塞项，接手 GPT 中断处）**：29 把 idle/ADS 实机问题逐枪修复（探针定位姿态/对位偏差→校准→LPWTest 验收）。

**F1 配件数据层与后端矩阵（0.5~1 天）**：AttachmentCompat 种子+Migration+去硬拒绝+矩阵接口+测试；Catalog 增 slotCapabilities；AttachmentDefinition SO + 27 LPW + LPFP 配件条目。

**F2 LPWTest 配件试验场（2~4 天）**：挂点批量工具（39 视图标准化）→ LPW 8 optic 审计分档（L1/L2/L3）→ 消音器/握把/激光校准 → FPWeaponAttachmentView 重写为 socket 驱动。验收：LPWTest 三类枪装/拆全套，贴合无穿模、切枪不丢件、重登恢复。

**F3 握把→动画族运行时切换（1 天）**：Resolver+规则表；LPW 步枪批量补 Rifle01+Rifle03 两族集（含 aim 四件套）；LoadClips 改造；TP 左手 IK 族切换。验收：同一把 LPW 步枪装/拆握把 FP 手型与 TP 左手即时切换；EditMode 锁规则。

**F4 瞄具变体+镜内视野（2~3 天，LPWTest→Arena 两段验收）**：AimScopeVariants 批量填充 + optic 索引选集 + L1/L2/L3 三层镜内视野。LPWTest 段：LPWAimInTestOverlay 扩展 scope 状态，逐档开镜验证；Arena 段：已验证组合接入主场景 + 配件持久化（后端权威）。

总量：N 线 3.5~4.5 天（本轮）+ F 线 5.5~9 天（后置）。

---

## 5. 验收门槛

- **本轮**：M7 双人互看互跑 → N2 远端武器表现 → N3 权威命中/防作弊 → N4 全链路
- **后置**：39/39 有槽位能力 + ≥1 真实改装；垂直握把装/拆→Rifle03/Rifle01（FP+TP）；镜内视野分档（LPWTest 先验收后 Arena）；后端矩阵/库存/保存/重登恢复完整

---

## 6. 风险与对策

| 风险 | 等级 | 对策 |
|---|---|---|
| FishNet×Animancer/既有组件执行顺序冲突 | 中 | N1 只加网络对象不动本地循环；N3 才服务器化 ActionSystem（保持单人可回退） |
| client-host 房主掉线整局终止 | 已知 | Demo 权衡（Docs/04 既定）；文档保留 FishNet headless 一句话说明 |
| N3 服务器化改动大 | 中 | FireRequest 走服务器验证通道，本地单人路径保留（离线模式） |
| LPW 视觉修复与 N 线抢编辑器资源 | 中 | 严格串行：N 先 F 后（v2 用户指定） |
| 29×27 挂点校准工作量爆炸（F2） | 高 | 脚本批量+分类抽查；矩阵先小子集再扩 |
| LPW 8 optic 无自带准星结构（F4） | 中 | F2 首项审计分档；红点档统一准星 quad 方案 |

---

## 7. 与既有文档的关系

- Docs/15：F 线是其配件数据层（CalibrationKey/兼容矩阵）的实施落地版，商城购买链路沿用；
- Docs/18：F4 = 其 Phase 3（scope 变体/狙击镜）的正式实施，ADS 底座已由 Phase 0+1+§12 交付；
- Docs/04/07：N 线严格对齐 Host 模式与 M7 里程碑定义，不引入 dedicated server。

---

## 8. 执行记录

- 2026-08-30：v2 制定（N 线先行）。
- 2026-08-30 晚：**N1~N4 全部交付**（详见下方 N 线交付记录）。

### N 线交付记录（2026-08-30）

| Phase | 交付物 | 提交 |
|---|---|---|
| N1 | PlayerNetworkAdapter（Owner/远端组件门控+移动通道）、NetworkHud（F1/F2/F3，Input System）、OfflinePlayerGate（联网禁用离线玩家/断线恢复）、Arena NetworkSystems（NM+Spawner+Spawn_0）、Player prefab（NetworkObject/NetworkTransform/Adapter）| 71491cf8 |
| N2 | NetworkLocomotionState（State/MoveInput/GaitPhase SyncVar 采集）、NetworkWeaponState（weaponId 同步+开火/换弹 ObserversRpc）、RemotePlayerStateView+反射接线（TPAnimDriver stateView 重指）、WeaponController 远端表现入口 | fef8cfc8 |
| N3 | NetworkCombatAuthority（FireRequest ServerRpc→服务器 TryFire 全链路、生命值 SyncVar、死亡广播、3s 重生回出生点）、Player prefab + DamageableTarget(TP_Model) | 763a7fa4 |
| N4 | 后端 GameRoom/GameRoomMember+迁移（房间码 6 位无混淆字符、30s 心跳懒清理、房主离房解散、一人一房）、RoomService/RoomsController（create/list/join/heartbeat/leave，JWT）、6 个房间契约测试；Unity 大厅联机入口（房主/加入按钮→Arena+F1/F2） | bb00260e + 本提交 |

**验证证据**：
- Unity 侧每 Phase PlayMode Host 单实例冒烟（spawn/门控/Owner 配置/移动 Simulate/Sprint 状态采集/开火换弹事件链/伤害→死亡→重生循环）全绿、0 Console 错误；
- 后端 17/18 测试通过（1 跳过=显式 MySQL 集成测试）；
- 修复的两个存量后端 bug：①`UseExceptionHandler()` 无参重载吞 ApiException→业务码降级 500（删除，让 ApiExceptionMiddleware 全权处理）；②AuthService 注册事务与 `EnableRetryOnFailure` 冲突（移除冗余事务包裹，唯一约束冲突由 catch 转 409）；
- 开发/测试库因 GPT 会话迁移文件改名产生 schema 漂移——已重建并全量迁移（数据由 seeder 恢复）。

**双实例 M7 验收（需用户实机）**：两个 Unity 实例（或一编辑器一构建包）→ 实例 A 按 F1 开房 → 实例 B NetworkHud.clientAddress 填 A 的 IPv4 后按 F2 → 双方互看互跑（TP 动画/切枪/开火/换弹由 N2 同步，命中扣血由 N3 服务器结算，死亡 3s 重生）。

**已知边界（后续打磨项）**：大厅房间码列表 UI（后端 API 就绪未接 UI，当前 LAN 直连+提示语）、移动无预测（远端玩家命令 1x 重放，高延迟下有操作延迟——预留 FishNet PredictedOwner 升级位）、LagCompensator（Docs/04 Day9 范畴）。
