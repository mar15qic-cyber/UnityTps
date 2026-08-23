# 03 FSM 与动画架构（原方案 §6 §7）

## 6. Gameplay FSM 架构与 Action System（防 RE4 问题2）

### 6.1 Locomotion / Weapon(Action) / Life 三个 FSM：**分离——且是三个正交状态维度，绝不合并为一个大 FSM**

| FSM | 状态集 | Owner（唯一写者） | 更新频率 | 职责边界 |
|---|---|---|---|---|
| **LocomotionFSM** | Idle / Walk / Sprint / Jump / Air / Land | Locomotor | 每 FixedUpdate | 只管移动与速度；不知道玩家在换弹还是濒死 |
| **ActionFSM**（上半身互斥槽） | None / Reload / SwitchWeapon / GrenadeThrow | ActionSystem | 事件驱动+计时器 | 只管占用上半身的互斥动作；不知道玩家在跑还是在空中 |
| **LifeFSM** | Alive / Dying / Dead / Respawning | Health | 事件驱动 | 只管生死；**最高层门控**：死亡即冻结另两个 FSM 的输入 |

**为什么不是一个大 FSM**：
1. 合并 = 状态爆炸（6 Loco × 4 Action × 4 Life ≈ 96 状态，转移表不可维护、AI 改一处崩一片）；
2. 三个维度**变化频率与网络同步策略不同**（Locomotion 每 tick 连续同步、Action 偶发事件同步、Life 极少状态同步），合并会让同步粒度变粗；
3. 竞技 FPS 行业惯例就是「移动层 + 动作层 + 生命层」正交叠加（跑动中可换弹、换弹中可被打死）。

**Fire 不是任何 FSM 的状态**：开火是被 ActionFSM gate 的瞬时行为（CurrentAction==None 且冷却好即可触发），若做成状态，全自动武器会导致状态机每发子弹自旋一次。这是面试高频追问点。

### 6.2 门控矩阵（FSM 间只读查询，禁止反向写入）

| 查询方 → 被查方 | 读取 | 用途 |
|---|---|---|
| WeaponController → ActionFSM | CurrentAction == None? | 换弹/切枪/手雷期间禁止开火 |
| Locomotor → LifeFSM | IsAlive? | 死亡冻结移动输入 |
| ActionSystem → LifeFSM | IsAlive? | 死亡打断一切动作 |
| AnimDriver → 三者 | **PlayerStateView**（聚合只读结构：locoState/speed/action/life/weaponIdx） | 合成动画的唯一输入源 |

**跨 FSM 唯一允许的"写" = LifeFSM 死亡时的 Interrupt 广播**（C# 事件，非直接方法调用）。HitReact 从 ActionFSM 中**移除**：它降级为 additive 叠加层（受击只加动画权重+镜头 shake，不占动作槽、不打断换弹）——竞技 FPS 中「挨一枪换弹就断」惩罚过重；这一取舍本身就是好的面试谈资。

### 6.3 ActionFSM 生命周期

```
Request → TryStart(条件: 动作槽空闲 / 弹药未满 / 存活)
        → Start(锁定动作槽, 发 OnActionStarted)        ← AnimDriver 收事件播动画
        → Execute(计时器推进 0→1)                       ← 真相在计时器, 不在动画
        → Complete(应用效果: 弹药回满/切枪生效, 发 OnActionCompleted)
任意时刻: Interrupt(reason) → Cancel(回滚: 弹药不变/槽清空, 发 OnActionInterrupted)
                                                    ← AnimDriver 收事件淡出动作层
```

### 6.4 打断优先级表

| 优先级 | 动作 | 可被打断于 | 说明 |
|---|---|---|---|
| 最高 | （Death 属 LifeFSM） | 不可被打断 | 广播 Interrupt 打断一切 |
| 1 | SwitchWeapon | Death | 切枪可打断换弹（FPS 手感惯例） |
| 2 | Reload / GrenadeThrow | Death、SwitchWeapon | 被打断弹药不变 |
| 叠加层 | HitReact | 不打断任何人 | additive 权重 0.2s 衰减 |

### 6.5 真相规则

**Reload 完成 = WeaponController 计时器到点（duration 来自 Lua），不是动画播完**。动画事件只用于表现对齐；两者谁先到都收敛：计时器到 → 服务器完成并广播；动画没播完也强制切回姿势。动画被打断**永远**不会卡死换弹状态——这就是 RE4 问题2 的制度级解法。

### 6.6 网络如何同步 Action（而不是 Animator）

**原则：网络上只传 Action 语义（枚举 + tick + 参数），从不传 Animator 参数、动画名、进度百分比。每端各自把 Action 语义翻译成本地动画。**

| 机制 | 载荷 | 用途 |
|---|---|---|
| SyncVar CurrentAction | (ActionType, startTick, weaponIdx) | 迟加入者补齐 + 正确性兜底 |
| ObserversRpc OnActionStart / Complete / Interrupt | (actionType, reason) | 触发远端动画/VFX/音效 |
| ServerRpc ReloadRequest / SwitchRequest / GrenadeRequest | 玩家意图 | 服务器验证后正式启动权威计时器 |
| TargetRpc OnActionRejected | 拒绝原因 | 客户端回滚本地预测（弹药/姿势显示恢复） |

三端一致性流程（以 Reload 为例）：
```
本机(预测端): R键 → ActionSystem.TryStart(Reload) 本地立即启动
   → FP 换弹动画立刻播、HUD 进换弹态 → ServerRpc(ReloadRequest)
服务器(权威): 验证(存活/动作槽空/弹药未满)
   → 启动权威 Reload 计时器 → SyncVar CurrentAction=Reload
   → ObserversRpc(OnActionStart, Reload)
远端客户端: OnActionStart → TPAnimDriver.Play(reloadClip, 淡入0.1s)
本机收到自己的广播: 已预测 → 去重跳过
服务器拒绝: TargetRpc(OnActionRejected) → 本机 Interrupt 本地预测并回滚显示
被打断:   服务器 Interrupt → SyncVar=None + ObserversRpc(OnActionInterrupt,reason)
   → 远端淡出动作层回 locomotion; 本机同步回滚
```
远端换弹动画时长由 clip 决定，但**换弹是否真实完成只由服务器计时器决定**——逻辑与动画在各端解耦、再经事件对齐。这就是「同步 Action 而非同步 Animator」的完整含义，也是本项目 Animation×Network 交叉处的核心设计。

---

## 7. Animation 架构（防 RE4 问题1）——Animancer 作为 Presentation 播放引擎

### 7.1 分层定位

```
Gameplay FSM (Locomotion/Action/Life, 真相)          ← §6
        │ PlayerStateView(只读) + C# 事件(OnActionStart...)
        ▼
AnimDriver (FP/TP 各一, 唯一动画写者)                 ← Presentation 层的"翻译官"
        │ 状态→Clip映射 / 事件→Play()调用, 全部显式代码
        ▼
Animancer (播放引擎: Mixer/Layer/Fade/EndEvent)       ← 插件, 只做播放
        ▼
Animator (渲染)  ← 不再有 AnimatorController 资产图, 不再有参数表
```

**Animancer 在本架构中的角色 = 纯播放引擎**（等同 FishNet 之于网络）：它不含任何 gameplay 逻辑，只是让「代码驱动动画」比裸 Mecanim 干净一个数量级。**Gameplay 层完全不知道 Animancer 存在**（asmdef 隔离），AnimDriver 内部才使用它——若哪天换回 AnimatorController，只改 AnimDriver 一个类。

选用理由（面试讲法）：
1. AnimatorController 资产图是**不可代码评审、不可 diff、AI 无法可靠编辑**的黑盒；Animancer 把状态机搬进代码 = 可单测、可 git diff、可让 AI 精确修改；
2. 天然匹配单写者制度：没有参数表就没有「谁都能 SetBool」的漏洞；
3. 网络事件→动画的映射（OnActionStart→Play(clip)）用 Animancer 是一行显式调用，无需 Any State/Trigger 这类脆弱机制。

### 7.2 动画职责表（谁播什么）

| 动画 | Local (FP Rig) | Remote (TP Model) | Animancer 实现方式 | 触发源 |
|---|---|---|---|---|
| Idle/Walk/Run/Jump | FP 手臂/武器随呼吸轻摆 | TP 移动混合 | **LinearMixerState**(0=idle,1=walk,2=run) 按速度参数驱动 + 空中Clip切换 | **状态**：LocomotionFSM SyncVar |
| Fire | 立即播（表现预测） | TP 开火 | Action 层 `Play(fireClip, 0.05s fade)` | **事件**：OnFireShot(本机预测/远端RPC) |
| Reload | FP 换弹 | TP 换弹 | Action 层 `Play(reloadClip)` | **事件**：OnActionStart(Reload) |
| SwitchWeapon | 收枪/出枪两段 | TP 收出枪 | Action 层两段 clip + EndEvent 串联 | **事件**：OnActionStart(Switch) |
| GrenadeThrow | FP 投掷 | TP 投掷 | Action 层 | **事件**：OnActionStart(Grenade) |
| HitReact | 不播（仅镜头 shake+红屏） | TP 受击方向 | **additive 叠加层**（不占动作槽） | **事件**：OnHitReact(dir) |
| Death | 相机坠落+隐藏手臂 | TP 死亡 | 停掉 mixer 与动作层, Play(deathClip, 不淡入) | **事件**：OnDeath |
| Sway/Bob/Breathing/Recoil/ADS | **不走动画系统**：CinemachineCamera + 自定义 CinemachineComponent 纯数学计算偏移 | — | — | 本地输入/状态 |

Animancer 层划分（TP 模型）：`Layer0 = Locomotion mixer`（全身）、`Layer1 = Action 层`（上半身 AvatarMask）、`Layer2 = Additive 层`（HitReact/呼吸）。FP 手臂 Rig 更简单：单层 + mixer + action 即可。

### 7.3 Animation Event 使用规范（白名单制度）

1. **首选 Animancer State Events**（代码注册 `state.Events.OnEnd` / `OnNormalize(0.5f)` 回调）——注册点就在 AnimDriver 代码里，可评审、不藏在 clip 资产里；
2. clip 内 Unity AnimationEvent 仅允许作**时间标记**：命名 `EVT_<Action>_<Marker>`（如 `EVT_Reload_MagOut`、`EVT_Fire_Muzzle`），载荷只允许 枚举int + 可选float，禁止传字符串命令/对象引用；
3. 接收者唯一：挂在同一对象上的 `AnimEventReceiver` → 翻译成 C# 事件 → 仅流向 AnimDriver/WeaponView（表现层内部）；**需要通知 Gameplay 的（如换弹动画结束对齐）也必须经 AnimDriver 转发**，且 Gameplay 只把它当「对齐提示」，不当真相（真相=计时器）；
4. **服务器判定永不依赖动画事件**（服务器可能根本没有播这段动画）；网络层永不出现在动画事件链路上；
5. 事件丢失必须可容忍：没有 `EVT_Reload_MagOut` 播出来，换弹照样成功（音效可由计时器近似触发兜底）。

### 7.4 FP/TP 双表现组织

- 同一 PlayerEntity 下两个互斥子对象：本机激活 FP Rig（摄像机+手臂+FPAnimDriver），远端激活 TP Model（TPAnimDriver+武器挂点）。**避免同步两套模型**，`IsOwner` 分支决定激活谁。
- 两个 AnimDriver 输入完全相同（PlayerStateView + 事件流），输出各自模型——这正是「同一 Gameplay 状态，两种表现」的架构证明点。
- 远端换弹可见 = OnActionStart(Reload) 事件驱动，不是从位置/速度推断——满足「远端必须看到换弹」需求，且延迟=一次 RPC 而非动画同步。
- 备注：Animancer 需要 Animator 组件存在（作为播放后端），但**不再使用 AnimatorController 资产与参数**；Root Motion 保持关闭（§13 问题3）。

### 7.5 IK 层（FinalIK）与相机层（Cinemachine）——同为 Presentation 姿态/视角后处理

**执行顺序（TP Model 每帧）**：`Animancer 播放动画 → FinalIK 修正姿态 → 渲染`；IK 是动画之后的姿态校正，只美化画面，不产生任何 gameplay 数据。

| 系统 | 用途 | 关键设计 |
|---|---|---|
| **FinalIK AimIK** | 远端玩家躯干/头部/枪口朝向 | 目标 = **服务器同步的瞄准方向**（PlayerStateView.aimDir）——远端"枪口指哪"来自状态同步而非动画推断，这是「网络状态→表现」的又一落地 |
| **FinalIK 双手 IK** | 双手贴武器握把/护木 | 目标点 = WeaponView 的挂点；消除持枪穿模 |
| **Cinemachine** | FP 相机 Rig + FP↔死亡观战切换 | 切换用 priority 机制；FP 效果（sway/bob/breathing/recoil/ADS FOV）以**自定义 CinemachineComponent** 实现，逻辑自研 |

- **写者制度延伸**：IK solver 的 target/weight 唯一写者 = IKDriver；相机偏移唯一写者 = 自定义 CM 组件。Gameplay 层仍引用不到 FinalIK/Cinemachine 类型（asmdef 隔离）。
- FP Rig V1 不用 IK（手臂以动画为主，控制调试面）；时间富余可加 FP 左手贴枪。
- **降级线**：FinalIK 在团结/Unity6 编译异常且 >0.5 天修不完 → 退化为「无 IK + 动画包原生持枪姿势」（观感降级，架构不变）；Cinemachine 严重不适 → 回退手写相机管理器（FPCameraRig 接口不变）。
