# Day4 武器系统实机回归审计与团结 AI 修改提示词

审计日期：2026-08-27  
审计对象：`Desktop 2026.08.27 - 03.17.41.01.mp4`（93.09 秒、120 FPS）、Arena 场景与当前工作区源码。  
范围：只审计用户提出的枪口拖尾、准心、相机/手臂后坐及枪声差异；本文档不把 `13-Day4武器系统审计与重构计划.md` 中的“已完成/已验收”声明当作当前事实。

## 结论摘要

五项反馈均成立。系统已有数据模型和事件骨架，但实机表现层存在关键断链，导致“不同枪像同一把枪”。应按以下顺序修复：

1. 先修复命中查询对玩家自身碰撞体的处理，并让拖尾使用明确的发射方向；这是偶发异常短线、竖直线或下坠线的首要根因。
2. 修复并实机验证四向十字准心的布局，再重设“AK 与 M4”可感知的散布/Bloom 差异。
3. 以同一 `ShotRecoilResult` 同时驱动瞄准、相机与第一人称手臂/枪模；当前值和 ADS 二次削弱使差异几乎不可见。
4. 替换“所有武器共用 `shoot.wav`”的占位配置；仅改音量、音高或 Mixer 不能形成真实枪械差异。

## 证据与问题链路

### 1. 枪口拖尾偶发垂直向下/不沿发射方向

**实机证据**：录像中可见黄色拖尾；异常并非某一把武器专属，因此不应继续优先归因于单个 Muzzle Prefab 的旋转。

**确定的代码链路**：

```text
WeaponController.TryFire
  -> CombatResolver.ResolveHitscan(相机/CameraPivot 原点, 发射方向)
  -> WeaponShot.Result.Point
  -> WeaponView.HandleShot
  -> LineRenderer: 起点=Muzzle.position，终点=Result.Point
```

`CombatResolver.ResolveHitscan()` 当前一旦首先击中玩家自身碰撞体，就直接返回：

```csharp
return new HitscanResult(false, false, hitInfo.point, hitInfo.normal, null);
```

它没有继续射线检测下一个非自身碰撞体，也没有回退到 `origin + direction * maxRange`。所以 `WeaponView` 会把一个非常近的“自身命中点”当成拖尾终点；起点又在动画中的枪口，二者的相对位置会形成偶发的短竖线、斜向下线或明显不贴枪管的条光。该漏洞在 Day4 文档的历史备注中也出现过，但没有被真正修复。

**第二个设计问题**：`WeaponView` 直接使用 `Result.Point`，没有保存“本发实际散布后的方向”。`WeaponShot.Direction` 目前携带的是后坐前的 `aimDirection`，不是 `mainDirection`/pellet direction。于是拖尾既不由枪口前向约束，也不由本发真实弹道方向约束。

**修复要求**：

- `CombatResolver` 改用 `RaycastNonAlloc`/排序结果或循环 Raycast，跳过 `ignoreRoot` 的全部碰撞体，命中第一个非自身目标；若没有非自身命中，结果点必须为 `origin + normalizedDirection * maxRange`。
- 在 `WeaponShot` 明确记录本发的 `FiredDirection`（单发为最终散布方向；霰弹单独保留 pellet directions 或以主方向表示）。
- 拖尾应从枪口开始，沿 `FiredDirection` 作视觉射线，长度按命中距离投影并限制为 `maxTracerLength`。不要把未经校验的原始 `Result.Point` 直接作为唯一终点。
- 保持既有“命中射线来自相机中心”的玩法约束；不要改为直接用 `muzzle.forward` 做伤害判定，否则准心、命中点和枪械晃动会失去一致性。这里要求的是**视觉上从枪口出发且平行于本发弹道**。
- 为“自身碰撞体在射线首位”“无命中”“近墙”“连射且武器后坐”添加测试和运行时断言。

涉及文件：`Assets/_Project/Scripts/Gameplay/Combat/CombatResolver.cs`、`Assets/_Project/Scripts/Gameplay/Weapon/WeaponController.cs`、`Assets/_Project/Scripts/Presentation/Weapon/WeaponView.cs`。

### 2. 准心不是十字，且没有体现枪械差异

**实机证据**：录像中中心准心呈横向短线/点的残缺外观，而不是四向十字。故“有 `CrosshairView` 代码”不能视作验收通过。

**静态配置**：`Arena.unity` 中四个对象与 `CrosshairView.top/bottom/left/right` 引用均存在，四条尺寸也正确（竖线 `2×14`、横线 `14×2`）。但 HUD 根 `WeaponHudCanvas` 的 `RectTransform.m_LocalScale` 被保存为 `(0,0,0)`，这是非法/不应保留的布局状态；构建器也没有显式归一化根变换或做屏幕截图验收。应先在 Play Mode 查明每一条的 active、anchoredPosition、sizeDelta、Canvas 缩放与实际渲染结果，再修复，不可仅凭 YAML 引用认定无问题。

**数值不可感知的直接原因**：

- `rifle.day3` 与 `rifle.02` 的基础腰射散布仅为 `1.2°` 与 `1.0°`，每发 Bloom 仅 `0.22°` 与 `0.20°`；在 60° FOV、1080 高度下，静态 Gap 理论差仅约 3.3 px，录屏中的 Game View 缩小后几乎不可辨认。
- 准心还被 `MinGap=6px`、`SmoothSpeed=240px/s` 平滑，短点射的瞬时差异会进一步被掩盖。
- Presenter 只读取 `CurrentSpreadDegrees`。这是正确的“弹道真实来源”，但需要显式的、短暂的视觉 Shot Pulse 来让玩家感到每发 Bloom；该 Pulse 必须是视觉叠加，不能伪造真实弹道散布。

**修复要求**：

- 修复 `WeaponHudCanvas` 根 RectTransform（位置零、旋转 identity、缩放 one），并让 HUD Builder 每次幂等重建时显式写入这些值。
- 在 `CrosshairView` 增加 fail-fast 校验：缺少任一四线引用/Config 时输出错误；在 Play Mode 断言四线 active、上下为竖线、左右为横线、位置以中心镜像。
- 增加截图或图像断言：腰射时必须是可辨识的四向十字；ADS 隐藏逻辑另测。
- 重新制定确认过模型映射后的 AK/M4 平衡值，使基础散布、每发 Bloom、恢复速度、水平后坐至少有一个明显不同；不能继续使用 1.2 vs 1.0、0.22 vs 0.20 这种玩家不可感的差别。
- Model 中添加由 `OnShotFired` 触发、快速扩张后回落的 `ShotPulseGap`；最终显示 Gap = 物理散布 Gap + Pulse，HUD 上应同时显示/可记录真实 Spread 与视觉 Pulse，避免调试混淆。

涉及文件：`Assets/_Project/Scripts/Presentation/HUD/CrosshairPresenter.cs`、`CrosshairModel.cs`、`CrosshairView.cs`、`CrosshairConfig.cs`、`Assets/_Project/Editor/CP5_HudBuilder.cs`、`Assets/_Project/Scenes/Arena.unity`、`Assets/_Project/ScriptableObjects/HUD/CrosshairConfig.asset`、`Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset`。

### 3. 相机后坐几乎没有，难以形成压枪体验

**链路状态**：`WeaponRecoilState` 已产生 `ShotRecoilResult`；`CmFPCameraRecoil` 每帧读 `WeaponController.CurrentRecoilOffset`，架构方向正确。因此不能回退到表现层重新随机计算后坐。

**实际不足**：

- 以两支步枪为例，垂直冲量是 `1.1°` 和 `1.0°`，Yaw 都是 `0.3°`；首枪倍率、累计倍率、恢复参数也几乎相同。
- ADS 会把相机后坐乘以 `AdsRecoilMultiplier=0.6`。高频率（9 Hz）且 0.75 阻尼的弹簧使峰值很快回零，录像中难以察觉差异。
- 现有值没有“可量化体验目标”，例如全自动 10 发后的累计上抬角度、恢复时间、AK/M4 差异、玩家向下输入是否能抵消上抬。

**修复要求**：

- 保留 `WeaponRecoilState` 为唯一瞄准后坐真相；相机只回声，不能再独立随机/积分。
- 先确认 `rifle.day3` 与 `rifle.02` 分别对应的实际模型，再做有方向性的调参。至少让垂直、水平、累计或恢复中的两项产生玩家可见的差异，并把结果记录为设计目标。
- 增加开发期 Recoil Debug Overlay/日志：本发 Pitch/Yaw、当前 Offset、ShotIndex、ADS 倍率、10 发累计 Offset；默认在正式构建关闭。
- 写自动化 Play Mode 验收：固定种子、固定帧率下，连射 10 发的峰值/终值、停火恢复曲线、ADS 与腰射倍率、玩家反向 look 输入后的补偿均可断言。

涉及文件：`Assets/_Project/Scripts/Gameplay/Weapon/WeaponRecoilState.cs`、`WeaponController.cs`、`Assets/_Project/Scripts/Presentation/Camera/CmFPCameraRecoil.cs`、`Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset`。

### 4. 手臂/枪模没有足够的开火反馈

`FPWeaponMotion.HandleShot()` 虽消费了 `shot.Recoil.ViewModelPitchDeg/ViewModelBackM`，但只给 `FP_Weapon_Root` 施加一个短、快回零的整体弹簧位移。它没有独立的手腕/前臂反馈层，也没有以验收指标保证 Fire 动画与程序后坐确实可见。

更重要的是，ADS 中该 root 后坐还会乘 `motionScale = 1 - adsBlend * 0.85`。满 ADS 时仅保留 15% 的枪模后坐；相机同时被 0.6 倍削弱，因而玩家最关注的瞄准射击状态恰好拥有最弱的反馈。

**修复要求**：

- 所有视觉后坐仍只消费同一个 `ShotRecoilResult`，禁止在手臂层另起随机值。
- 调整 ADS 视觉缩放，不要把手臂反馈削到 15%；可与相机后坐独立做“视觉保持系数”，但必须配置化并有上下限。
- 增加可叠加的第一人称手臂/武器 Fire 反馈（优先使用现有 Fire 动画，再叠加 root 后移、上抬、轻微横摆；若采用 Rig/IK，不能与 Animancer 争夺同一骨骼写入权）。
- 给每把武器或族配置可见的 ViewModel Back/Pitch/Shake，AK/M4/SMG/霰弹/狙击的量级必须不同；以帧截图/峰值位移验证，而不是只看 Inspector 数值。

涉及文件：`Assets/_Project/Scripts/Presentation/Camera/FPWeaponMotion.cs`、`Assets/_Project/Scripts/Presentation/Animation/FPWeaponAnimator.cs`、`WeaponRecoilState.cs`、平衡资产。

### 5. 所有枪声相同

**确定证据**：五份族 Profile（Handgun/Rifle/SMG/Shotgun/Sniper）的 `FireVariants` 都指向同一资源：

```text
Assets/Low Poly FPS Pack/Components/Audio/Shoot/shoot.wav
```

因此 `WeaponAudioView` 的轮换池、随机音高和不同 Profile 名称都不能产生武器身份差异。当前工程内可见的射击素材只有 `shoot.wav` 与 `shoot_silencer.wav`；其余主要是换弹音。这是**素材缺口**，不是 AudioSource 接线问题。

另有一个次要断链：`WeaponAudioView.Awake()` 用默认 `FindObjectsOfType<FPWeaponAnimator>()` 收集动画器，不会收集切枪后才激活的视图动画器；这些武器的分阶段换弹音不会被订阅。开火声本身不受此问题影响。

**修复要求**：

- 为每种计划支持的枪械准备并导入有合法来源的不同开火素材；不得把同一个 `shoot.wav` 换 Profile 名或轻微 Pitch 后宣称完成差异化。
- 在新素材未到位前，显式标注“临时占位”。如需临时可玩性，可有限使用现有两个素材及克制的音高/低通差异，但不能作为最终验收。
- 在 `WeaponDefinition` 做单枪覆盖或在族 Profile 中接入真正不同 Clip；验证 GUID 不可全部相同。
- 改为在武器装备/视图激活时注册对应 `FPWeaponAnimator`，并在停用时解除注册，确保所有枪的换弹阶段音有效。
- 增加 Editor Validator：报告所有 FireVariants 为空、同 GUID 跨族复用、实际未被消费的 Profile 槽位。

涉及文件：`Assets/_Project/Scripts/Presentation/HUD/WeaponAudioView.cs`、`Assets/_Project/Scripts/Gameplay/Weapon/WeaponAudioProfile.cs`、`Assets/_Project/ScriptableObjects/Audio/AudioProfile_*.asset`、各 `WeaponDefinition` 资产。

## 团结 AI 修改提示词（可直接复制）

```text
你正在修改 Unity 6 工程 E:\UnityProject\UnityFpsLowPoly 的 Arena 第一人称武器系统。请先读取当前代码与场景；不要把 Docs/13 中“已验收”的文字当成事实。目标是修复实机回归中五项问题：偶发异常拖尾、残缺准心、不可感知的枪械后坐差异、手臂缺乏后坐、所有枪声相同。

硬约束：
1. 保持 Gameplay 的权威瞄准不变量：伤害射线/相机中心/准心同源。不要改成 muzzle.forward 负责命中判定。
2. WeaponRecoilState 是后坐唯一真相；CmFPCameraRecoil、FPWeaponMotion、HUD 只能消费 ShotRecoilResult/只读状态，不能各自重新随机或积分。
3. 不要删除用户已有修改、不要使用 git reset/checkout，也不要修改无关资产。
4. 每一步在 Unity 编译、EditMode/PlayMode 测试和实机视觉验收后再继续；若没有真实的新射击音频素材，明确报告素材阻塞，不要用同一 shoot.wav 假装实现差异化。

请按以下顺序实施。

一、修复拖尾与自身碰撞体
- 在 CombatResolver 中跳过 ignoreRoot 下的所有命中，继续寻找第一个非自身碰撞体；无非自身命中时 Result.Point 必须是 origin + normalizedDirection * maxRange，绝不能返回自身 hitInfo.point。
- 扩展 WeaponShot，保存本发实际最终 FiredDirection（散布后的方向；霰弹保留清晰的主方向/每 pellet 方向语义）。
- WeaponView 的 tracer 必须从 muzzle.position 出发、平行于 FiredDirection，终点按有效命中距离投影并截到 maxTracerLength。不能直接把未经校验的 Result.Point 作为唯一终点。
- 新增测试：自身碰撞体首命中、无命中、近墙、连射/动画后坐时 tracer 起点与方向。日志应能显示 origin、firedDirection、selfHitSkipCount、visualEnd。

二、修复并验证十字准心
- 复核 Arena 的 WeaponHudCanvas。把根 RectTransform 重置为 localPosition=(0,0,0)、localRotation=identity、localScale=(1,1,1)，并修改 CP5_HudBuilder，让幂等重建也会写入这些值。
- 复核 CrosshairView 的 top/bottom/left/right/config 引用和运行时 active/anchoredPosition/sizeDelta；缺失引用必须明确报错。
- 腰射时显示四向十字+可选中心点，上下是竖线、左右是横线；ADS 隐藏逻辑保持独立。
- 增加 PlayMode/截图验收，不能只检查 YAML 引用：实际 Game View 必须不再是“---”或横线/点。
- 保持 CurrentSpreadDegrees 为准心物理间距来源；在 CrosshairModel 增加 OnShotFired 驱动的短暂 ShotPulseGap（视觉叠加），最终 Gap=物理 Gap+Pulse，并使调试 UI 能分别看到真实 Spread 与 Pulse。
- 先确认 rifle.day3 与 rifle.02 分别对应哪把实际模型，再重做 AK/M4 的 BaseHipSpread、BaseAdsSpread、ShotBloomPerShot、恢复和水平后坐。数值差必须在 1080p/60° FOV 的实际准心中清晰可辨，不得继续只保留 1.2 vs 1.0、0.22 vs 0.20 这种差别。

三、让相机和手臂形成可压枪、可区分的反馈
- 保留 WeaponRecoilState -> CurrentRecoilOffset -> CmFPCameraRecoil 的单向链路。增加仅开发期可见的 Recoil Debug（每发 Pitch/Yaw、ShotIndex、ADS 倍率、当前 offset）。
- 在固定随机种子下写 PlayMode 测试：10 发连射峰值/终值、停火恢复、ADS 与腰射倍率、用户反向 look 对下一发方向的补偿。
- 重新调每武器的 Pitch/Yaw/FirstShot/Accumulation/Recovery，使 AK 与 M4 至少在两项上有明显的玩家可感差异。
- FPWeaponMotion 继续只消费 ShotRecoilResult，但修复 ADS 把枪模反馈压到 15% 的问题；用配置化视觉保持系数并限制范围。Fire 动画与 root 后移/上抬/轻微横摆可叠加，但不得和 Animancer/IK 对同一骨骼抢写入。用截图或采样峰值验证手臂和枪模实际移动。

四、修复音频差异化与订阅
- 先跑 Validator，确认五个 AudioProfile 的 FireVariants 目前都指向 Assets/Low Poly FPS Pack/Components/Audio/Shoot/shoot.wav。
- 不要把同一个 wav 仅改名或轻微改 pitch 就当最终方案。列出工程现有可用音频；若没有合法的五类开火素材，新增一个明确的“需要用户提供/授权素材”清单并保留临时占位标识。
- 当有不同素材后，按 WeaponDefinition 单枪覆盖或族 Profile 接入，Validator 必须能检测跨族 Fire GUID 全相同、空槽和未使用槽位。
- WeaponAudioView 不可只在 Awake 用 FindObjectsOfType 收集激活动画器；在 equip/view enable 时订阅对应 FPWeaponAnimator，view disable 时解除，确保切枪后换弹阶段音仍工作。

交付要求：
- 给出修改文件清单、每个根因与修复对应关系、未解决的素材阻塞。
- 运行编译和相关 EditMode/PlayMode 测试；提供测试结果。
- 给出至少 4 张实机证据：异常自身碰撞拖尾已消失、腰射十字准心、AK/M4 连射准心/相机差异、不同武器的音频 Profile 与播放日志。不要声称“已验收”，除非这些证据都实际存在。
```

## 建议的人工回归顺序

1. 贴墙、转身、跳跃、切枪并连续开火 30 秒：拖尾必须始终从枪口向准心射线方向延伸，不能闪到脚下或自身附近。
2. 腰射静止、移动、冲刺、ADS：确认十字四线完整、对称，ADS 的隐藏仅发生在阈值之后。
3. 对已确认的 AK 和 M4：各腰射/ADS 连射 10 发，记录屏幕上抬量、准心 Gap 峰值和恢复时间；玩家应能肉眼区分并能以向下输入压枪。
4. 手枪、步枪、SMG、霰弹、狙击各开火与换弹一次：开火音不可再全部同声；如果素材尚未补齐，应明确显示为临时占位而非虚假通过。
