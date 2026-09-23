# TP 手枪持枪位姿与左手 IK（握把自适应）— Codely 交付报告

日期：2026-09-21 凌晨
来源：用户双端实机录屏两段（`Desktop 2026.09.21 - 00.38.35.15.mp4` / `00.39.55.16.mp4`）反馈两问题：
1. TP 模型手上的手枪歪斜；
2. TP 模型手持不同 rifle 时左手姿势恒定，不随当前枪械改变握持点（装握把应握握把、没装应握护木），要求用 IK 约束左手。

协议无变更（fps-net-v7 不动），DS 无需重建；ReleaseClient 已重建（buildId=83c91f71ad45）。

---

## 一、根因结论

### 问题 1：TP 手枪歪斜
- TP 武器 prefab 以固定根位姿挂到 `hand_R`（`TPWeaponMeshSwapper` 不覆盖根变换）。位姿是否"正确"取决于**动画集摆出的手骨姿态**。
- 长枪（AR/SMG/霰弹/狙击）播 `@rifle` 动画集，其根位姿（yaw-90 家族 / AR01 步枪基准）经长期校准视觉正确；
- **手枪播 `@handgun` 动画集，手骨系与 rifle 集不同**，而 4 把 `TP_Weapon_Handgun_0X` 的根位姿在 2026-09-18 被统一成了"步枪参考值"（euler 291.6,354.7,178 / pos 0.0087,0.1524,0.1093）——该值只对 rifle 动画集成立。实测探针数据：手枪姿态下枪体悬浮在拳头**前上方约 11cm、前移 15cm、带 roll 与上仰**，与录屏现象一致。
- 注：2026-09-18 修复只解决了"手枪在 rifle 动画集语义下的朝向"，未针对 @handgun 动画集重新标定。

### 问题 2：左手不随武器变握点
- `TPLeftHandIK.cs`（把左臂 TwoBoneIK 解算到武器 `LeftHandTarget` 的完整实现，含换弹抑制/死亡闸门/混合权重）**从未挂上正式玩家 prefab**——全库 GUID 检索只命中已废弃的 `Arena_LPWTest.unity`（2026-09-05 LPW 转向后即成死资产）。正式游戏里左手姿势完全由动画决定（动画作者摆的是"扶弹匣"位），自然不随武器变化。
- 即使挂上也只覆盖"护木"握点：下挂握把配件（`attach.lpw.grip.01`，Mod_04.prefab）没有独立持握点数据，装配后 IK 目标仍停在武器 prefab 烘焙的 LeftHandTarget（护木）上。

## 二、修复内容

### 1. 手枪根位姿重标定（4 把 TP_Weapon_Handgun_0X.prefab）
**标定方法（可复用 SOP）**：EditMode 探针实例化正式玩家 prefab → `AnimationMode.SampleAnimationClip` 采样 `idle@rifle_tpc` / `idle@handgun_tpc` 烘焙到骨架 → 读手骨世界位姿。以步枪集为基准做**传递标定**：
- 旋转：`L_new = inverse(R_handR@handgun) × (R_handR@rifle × L_rifle)`——把手枪 stance 手骨系下"枪的根相对旋转"对齐到步枪 stance 已验证的持枪朝向；
- 位置：枪的 mag 节点（握把中段代理点）落位到**拳头中心**（步枪 case 实测手骨正位于枪的握把区，故规则一致："腕/拳落在握把区内"）。
- 结果：四把共用旋转 `Euler(283.1, 62.8, 123.1)`，逐枪位置见下表（写入测试 `TPGripIkTests.HandgunPoses` 锁定回归）：

| prefab | localPos | 
|---|---|
| TP_Weapon_Handgun_01 | (0.0130, 0.0398, 0.0455) |
| TP_Weapon_Handgun_02 | (0.0097, 0.0280, 0.0366) |
| TP_Weapon_Handgun_03 | (0.0115, 0.0338, 0.0422) |
| TP_Weapon_Handgun_04 | (0.0123, 0.0292, 0.0576) |

逐把截图目检（HG01 两色 Glock / HG02 M9 / HG03 M1911 / HG04 紧凑型）：滑槽朝上、握把入拳、枪口水平向前；`walk_forward@handgun` 行走姿态复验同样贴合。

### 2. 左手 IK 挂载 + 握把自适应目标
- `Player_Day2_Rebuilt.prefab`：`TP_Model` 挂 `TPLeftHandIK`（swapper/controller 已接线；死亡写者名单 `PoseWriterNameMarkers` 按 `Contains("TPLeftHand")` 自动覆盖，无需改动）。
- `TPWeaponMeshSwapper`：`CurrentLeftHandTarget` 从"换枪时缓存"改为**动态决策**——新增纯函数 `ResolveLeftHandTarget(attachments, fallback)`：已装配的配件克隆（`Att_*`）里存在 `LeftHandGrip` 子标记 → 返回握把标记（左手握握把）；否则回退武器 prefab 的 `LeftHandTarget`（护木/护圈）。配件随换枪整体重挂，决策天然随装配刷新。
- `Mod_04.prefab`（垂直握把）：新增 `LeftHandGrip` 子标记，localPos (0.0001,-0.0312,-0.0349)、localRot Euler(0,270,90)——含义：腕点在握把中段左侧 ~3.5cm，手旋转 = 武器系绕前向轴 +90°（掌心贴握把、手指向前卷握、拳道竖直）。探针实测 IK 解算后手骨与标记重合误差 0.0001m。

### 3. 手枪 LHT 说明
手枪 prefab 的 `LeftHandTarget`（护圈前下方）不变——挂载 IK 后副手自动包到该点，形成标准双手持枪。

## 三、验证
- 编译 0 错（两次 start_compilation_pipeline，唯一一轮报错为测试文件缺 using，已修）。
- EditMode：`TPGripIkTests` 9/9（目标决策 3 + prefab 挂载/接线 1 + 握把标记 1 + 4 把手枪位姿回归）；回归 `P30Handgun04AssetTests` + `FormalLpfpAssetIntegrityTests` + `AttachmentSnapshotSyncTests` 16/16。
- 视觉：手枪 stance 截图 ×4 枪 + 行走姿态 + 步枪握把 IK 姿态，全部符合预期（screenshots/ProbeCam_*.png）。
- 往返验证：从**已落盘 prefab** 重新实例化 → 握把中心与拳头误差 0.0001m、枪口方向 (0,0,-1) 水平、IK 组件随实例化。
- 构建：ReleaseClient BUILD_OK（216MB，fps-net-v7，buildId 83c91f71ad45）；产物 `Game.Presentation.dll` 扫描含 `ResolveLeftHandTarget`/`LeftHandGrip` 标记；5 个设置文件备份→还原 MD5 逐个一致（Logs/BuildSettingsBackup_20260921_014754）。

## 四、待用户实机验收（VERIFY_PENDING）
1. 双端实机：远端玩家持**手枪**不再歪斜（idle/行走/开火/换弹）；
2. 远端玩家持步枪：左手贴护木（此前动画原姿），装**垂直握把**后左手移到握把上，卸下回到护木；
3. 换枪瞬间左手平滑过渡（IK 混合 0.15s）；换弹动画期间左手放开（既有换弹抑制逻辑）；
4. 手枪副手包护圈姿势目检。

## 五、边界与非目标
- 未动 FP 侧（FPLeftHandIK/poseProfile 体系本就完整）；未动网络契约与数值；DS 未重建（表现层变更，服务器权威无影响；DS 也会执行该表现组件但无观察者、死亡闸门已覆盖）。
- 握把标记当前只配了 Mod_04（目录里唯一握把）；后续新增握把配件需在 prefab 内加同名 `LeftHandGrip` 标记。
- 手枪根位姿标定基准 = @handgun idle + rifle 集传递，未逐个校准 fire/reload 手骨帧（同一骨骼链，偏移恒定，idle 正确则全程正确）。

## 六、关键文件
| 动作 | 文件 |
|---|---|
| 改 | Assets/_Project/Scripts/Presentation/Animation/TPWeaponMeshSwapper.cs（CurrentLeftHandTarget 动态决策 + ResolveLeftHandTarget 纯函数） |
| 改 | Assets/_Project/Prefabs/Player/Player_Day2_Rebuilt.prefab（TP_Model 挂 TPLeftHandIK） |
| 改 | Assets/_Project/Prefabs/Weapons/TP_Weapon_Handgun_01..04.prefab（根位姿重标定） |
| 改 | Assets/LowPolyWeapons/Prefabs/Attachments/Mod_04.prefab（+LeftHandGrip 标记） |
| 增 | Assets/_Project/Tests/EditMode/TPGripIkTests.cs |
| 建 | Builds/ReleaseClient（重构建） |

> 附注：构建期间弹出的"Arena 场景已修改"对话框选择了 **Don't Save**——脏来源是本会话 EditMode 探针对象的增删（净效果为零），磁盘场景保持用户原状。
