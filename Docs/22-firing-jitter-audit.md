# UnityFpsLowPoly 开火异常抖动 · 全面审计报告
**日期**：2026-09-03　**场景**：Arena_LPWTest（29 把 LPW 枪，Player @ (0,0,9)）　**素材**：Desktop 2026.09.03 - 11.25.03.12.mp4（1920×1080@120fps，42s，游戏实跑 ~60fps）
**审计方法**：KIMI-K3 多模态（5 段视频 + 4 张帧级网格）+ ffmpeg YDIF 逐帧差分 + 代码静态审计 + Unity EditMode 曲线探针

---

## 一、症状与证据总览

### 1.1 用户报告
开枪的时候出现异常的抖动。

### 1.2 K3 视频审计结论（按证据强度排序）

| 素材 | 时段/枪 | 采样粒度 | K3 判定 |
|---|---|---|---|
| burstA 网格 | 2.30-2.70s G36 | 帧级(1/120s) | **60Hz 逐帧换向微抖（1-2px 垂直，周期2帧）**，存在于稳态瞄准段与射击恢复段；单发后坐本身干脆（上跳1-2帧、回落2-3帧）；**背景锁定≤1px（该段无可见相机后坐）**；手臂与枪刚性同步 |
| burstB 网格 | 17.00-17.40s G36 连射 | 帧级 | 基本正常：每枪单次"上冲→回落"脉冲，恢复1-2格；无交替；无累积；背景随枪动（可见相机后坐） |
| burstB2b 网格 | 17.75-17.93s G36 连射（YDIF 异常窗尾段） | 帧级 | 干净"单发上跳→指数式回落"脉冲×3（间隔67/50ms）；**无 +−+ 交替**；背景静止；**末发后 .167/.183 连续两格偏高未完全回位** |
| burstB2a 网格（部分） | 17.55-17.75s G36 连射（YDIF 异常窗头段） | 帧级 | （截断仅存尾部）**.150(=17.70s) 起出现水平/右侧偏移，可读帧内未回位** |
| seg3 视频 | 16.5-19.5s G36 连射 | ~1s 粗采样 | 大幅旋转抖动+往复晃动；镜头右倾累积；**枪口指向左下而弹道命中正前方（枪口与落点脱节）** |
| seg4 视频 | 25.5-28.5s AUG 连射 | ~1s 粗采样 | **每枪向左横跳（枪口 700→540px）+向左旋转，累积不回中**；准星基本稳定 |
| seg1/seg2/seg5 | 点射段 | ~1s | 正常 |
| burstDa/Db（AUG 帧级） | 26.30-26.70s | 帧级 | 本轮空返回（后端故障），AUG 横跳未获帧级确认 |

### 1.3 YDIF 客观定量（ffmpeg signalstats，120fps）
- seg5 区（33s+）：尖峰每 6-7 游戏帧一次（≈10Hz）= 与 670RPM 射速同步的逐发节奏（正常后坐）
- seg1 区：0.233-0.283s 出现 YDIF≈16.5 的双帧大突跳
- seg3 区（17.0-18.2s）：**0.65-0.80s 出现连续多帧尖峰簇（6尖峰/10游戏帧）**——对应 burstB2 窗口

### 1.4 证据矛盾与消解
- 粗采样（seg3"剧烈旋转抖动"）与帧级（burstB2"干净脉冲无交替"）矛盾 → **1s 采样对 11.2Hz 后坐锯齿的混叠**：每秒 11 发的连射在 3 帧采样中呈现随机姿态差，视觉描述被放大。帧级证据优先。
- 但粗采样的部分观察有帧级独立佐证：**累积不回位**（burstB2a 水平偏移滞留 + burstB2b 末发未完全回位 + seg3/4 粗采样一致）。
- burstA 的 60Hz 微抖与 burstB 的"无交替"并存 → 微抖幅度仅 1-2px，网格缩放后接近 K3 分辨率下限，burstB 段（有相机后坐掩盖）可能低于可检阈值。

---

## 二、开火期 viewmodel 写入者全景（代码枚举，已核实）

| # | 写入者 | 写什么 | 频率/时序 |
|---|---|---|---|
| 1 | CmFPCameraRecoil（Cinemachine Aim 阶段） | 相机 OrientationCorrection = gameplay 后坐债务 | 每帧；每发阶跃 +0.68°(pitch)±0.24°(yaw)（1.51°×0.45 债务缩放），0.18s 延迟后 4.5°/s 恢复，clamp 12°/4°。**已核实无跨帧累积**（PullStateFromVirtualCamera 每帧 identity 重建） |
| 2 | FPWeaponAnimator + Animancer | AimFire clip **每发 FromStart 重启**（fireFade 0.04s） | 670RPM=89.6ms/发；clip 长 233ms 永远播不完即重启 |
| 3 | FPWeaponMotion（order 20）— ①camNode x/y 对位 | FP_Weapon_Root localPosition.xy（增益1逐帧） | 每帧重算（读被动画驱动的 Armature/camera 节点） |
| 4 | FPWeaponMotion — ②pivot 钉回 | LPW_ADS_Pivot_Runtime 旋转（复位→实测→增益1重钉）+ sharedTranslation 世界位移 | 每帧（目标=当前相机轴，随相机后坐每发变化） |
| 5 | FPWeaponMotion — ③后坐弹簧 | root localRotation/localPosition（8Hz/ζ0.7 欧拉积分） | 每发冲量：pitch +5°、back 60mm（clamp 10°/100mm）；yaw ±1.12°（**随机方向**，clamp ±4°） |
| 6 | FPLeftHandIK（order 40） | 左臂骨骼（无阻尼解析 IK，权重1 硬锁枪上 LeftSupportGrip） | 每帧 |
| 7 | LPWGunPoseDriver（order 30，仅 V2/AUG） | LPW_Gun 局部姿态（hip↔ads 混合 + 独立后坐弹簧） | 每帧 |

非嫌疑（已排除）：DetachableMagazineView（仅换弹窗口）、WeaponView（特效）、TP 侧、AimIdle clip（**EditMode 探针证实全曲线常量 0.00mm/0.00°**）。

---

## 三、逐发时序链与量化（G36 legacy 路径）

**每发开火（WeaponController.TryFire，Update 相位）→ OnShotFired 广播 → 同帧：**
1. WeaponRecoilState 累积相机债务（pitch +0.68°）
2. FPWeaponMotion.HandleShot：弹簧冲量（pitch −5°X、yaw ±1.12°、roll、back −60mm）
3. FPWeaponAnimator.Update：FSM 判定 → AimFire clip FromStart 重启 → 动画求值把 **weapon 骨骼 Y 从 -0.098 瞬跳回 -0.132（EditMode 采样实证：t45ms=-0.091，重启锯齿 ≈37.9mm/发，全 clip 最大偏移 42.5mm）**，经 0.04s 淡化
4. LateUpdate（order 20）：camNode 对位（camNode 在 AimFire 中恒定，探针证实）→ root 绝对写 → pivot 复位→实测瞄准线→**用当帧（被相机后坐踢动的）相机轴增益1重钉** → sharedTranslation（深度目标来自挂在 weapon 骨骼下的 SightReference，**随 42mm 锯齿每发跳变**）→ 弹簧叠加写
5. order 40：左手 IK 硬锁到钉回后的握点

**弹簧数学**：ω=50.3rad/s，ζ=0.7 → τ=28ms；发间隔 89.6ms → 单发踢后 ~1.7 游戏帧衰减完（burstB2b 帧级实测"上跳→指数式回落"吻合）。**yaw 为随机方向 ±1.12°/发** → 连射期在 ±4° clamp 内随机游走 → 粗采样下表现为"左右晃动/旋转抖动"。

**四写入者拉锯（核心病灶）**：pivot 钉回（增益1、目标随相机后坐变）↔ 后坐弹簧（8Hz）↔ AimFire 重启锯齿（11.2Hz）↔ 相机后坐（阶跃+斜坡恢复）——相位各异，ADS 期叠加为观感"抖动"。**腰射期无 pivot 钉回/对位（adsBlend=0）→ 只有弹簧+clip → 观感正常**（与 burstB 帧级"干净脉冲"及用户"开镜开枪才抖"一致）。

---

## 四、根因结论（分级）

### 确证（代码+探针+帧级证据三重支持）
- **R1 每发 AimFire FromStart 重启** → weapon 骨骼 37.9-42.5mm 纵向锯齿 @11.2Hz（探针定量）+ 0.04s 淡化 → 连射期 viewmodel 持续高频纵向脉动。
- **R2 ADS 期三处"增益 1 逐帧钉回"与后坐系统拉锯**：pivot 钉回目标轴随相机后坐逐发变化；sharedTranslation 深度目标随 42mm 锯齿逐发跳变；V2 分支 rearSight x/y 同理（AUG）。后坐被部分抵消（burstA"背景动而枪相对稳"）同时引入针锋相对的高频修正。
- **R3 yaw 随机游走**（±1.12°/发、clamp ±4°）：连射期枪口方向随机摆动，配合钉回产生"枪口与准星/落点脱节"（seg3"枪口指左下弹道在正前"、seg4"枪口左跳准星稳"的几何来源——muzzle 距 grip ~0.3-0.5m，4° 偏航 ≈ 枪口横移 2-3.5cm ≈ 100-170px@ADS）。

### 高度可疑（代码确证、帧级部分支持）
- **R4 累积不回位**：burstB2a 水平偏移滞留 + burstB2b 末发未完全回位 + seg3/4 粗采样一致。机制候选：yaw/roll 随机游走 clamp 滞留 + 弹簧位置 clamp（back 100mm）+ 钉回残差。
- **R5 AUG(V2) 每枪向左横跳**（仅粗采样）：V2 x/y 钉回保持 rearSight 居中而 muzzle 随 yaw 弹簧甩动 → 枪口独立左跳观感。burstDa/Db 帧级未获确认（后端故障）。

### 未决（列为后续运行时验证项）
- **U1 稳态 60Hz 1-2px 微抖机制**（burstA 独有发现）：AimIdle 全常量（已排除动画）；候选：CinemachineBrain 与 order 20 的帧序交错（相机朝向滞后 1 帧 + 增益 1 重解构成 2 帧周期环）、双相机同步、sway SmoothDamp 交互。**需 PlayMode 探针逐帧记录 pivot/root 实际值定位**。
- **U2 seg3"相机右倾累积"**：代码核实 CmFPCameraRecoil 无跨帧累积；疑为粗采样误判（12° 仰角债务+4° 偏航的合成观感）或另有来源。

---

## 五、覆盖面
- 27/29 LegacyUnverified（含视频中的 G36）：完整暴露于 R1-R4
- 1/29 V2（AssaultRifle2_01=视频中的 AUG）：R2(V2分支)+R3+R5，无 R1（ProceduralOnly 不重启 clip）
- 2/29 无 FPWeaponPoseProfile：不涉及

## 六、修复方向（此前已与用户对齐，未实施）
- P0：三处钉回收口为"过渡期求解→到位冻结→开火即冻结"，`transform.position +=` 并入 localPosition 单通道（单一写入者）
- P1：AimFire 连发不 FromStart（维持 AimIdle，后坐全交弹簧——即 V2 已验证的 ProceduralOnly 模式下放）
- P2：左手 IK SmoothDamp（用户已选型，暂不动）
- P3：切枪旧视图漂浮/沉枪（独立问题，暂不动）
