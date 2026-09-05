# 25 LPFP 全面转向说明

> 决策日期：2026-09-05  
> 文档状态：**当前正式资产与表现层基线**  
> 决策摘要：项目已停止研究 `Arena_LPWTest` 中的 LPW 实验内容，后续全面转向 `Low Poly FPS Pack`（下称 **LPFP**）完成项目搭建。

---

## 1. 本次决策

从本文件生效之日起：

1. `Arena_LPWTest.unity` 不再是开发、验收或回归基线；
2. `Assets/_Project/Prefabs/Weapons/LPW/`、`LPWTest/` 及对应 `ScriptableObjects/Weapons/LPW/`、`LPWTest/` 不再继续扩充；
3. 不再投入时间修复 LPW 实验链中的瞄准钉回、持枪姿态、动画适配、左手 IK、开火抖动或逐枪校准问题；
4. 正式项目以 `Assets/Low Poly FPS Pack/` 提供的模型、动画、Animator Controller、第一/第三人称资源为原始素材来源；
5. 正式运行场景以 `Assets/_Project/Scenes/Arena.unity` 为战斗场景，Lobby → Arena、直接进入 Arena、离线与联网玩法均围绕这一条主线建设；
6. 新武器、新动画、新配件适配和后续表现修复，只允许进入 LPFP 正式链路，不再为 LPW 实验链提供双份实现。

这是一次明确的方向切换，不是 LPW 与 LPFP 并行维护，也不是暂时冻结 LPW 等待以后恢复。

---

## 2. 为什么停止 LPW 实验线

`Arena_LPWTest` 的作用已经完成：它验证了批量接入外部枪械、程序化 ADS、姿态校准、IK、后坐力叠加以及多套动画来源共存时可能出现的问题。实验过程中也暴露出以下长期成本：

- LPW 枪械需要大量逐枪姿态、瞄具轴线和握持点校准；
- LPW 动画与当前第一人称手臂、Animancer 状态机和后坐系统之间需要额外适配层；
- 动画、程序化后坐、ADS 钉回和 IK 可能同时写入同一表现节点，容易形成多写入者冲突；
- 为 LPW 与 LPFP 同时维护 prefab、`WeaponDefinition`、动画路由、配件挂点和验证规则，会持续放大项目复杂度；
- LPW 实验线的继续投入不会直接提高正式 Lobby → Arena → 对战 → 结算闭环的完成度。

当前项目目标是完成一套稳定、完整、可演示的 FPS，而不是维护通用枪械资产兼容框架。因此，统一到 LPFP 是当前完成度和维护成本最合理的选择。

---

## 3. LPFP 正式基线

### 3.1 原始素材来源

LPFP 原始内容位于：

```text
Assets/Low Poly FPS Pack/
├─ Components/                  # 武器组件、动画与 Animator Controller
├─ Prefabs/                     # 原厂 prefab
├─ Third_Person_Character/      # 第三人称角色相关内容
└─ Demo_Scenes/                 # 仅用于理解原厂资源，不作为项目正式场景
```

原厂资源原则上作为源素材保存。项目需要的正式 prefab、定义和配置仍放在 `_Project` 目录中，通过项目自己的 Gameplay 与 Presentation 架构接入。

### 3.2 项目正式武器资产

当前正式 LPFP 包装资产包括：

```text
Assets/_Project/Prefabs/Weapons/FP_*.prefab
Assets/_Project/Prefabs/Weapons/TP_Weapon_*.prefab
Assets/_Project/ScriptableObjects/Weapons/Day2_*.asset
Assets/_Project/ScriptableObjects/Weapons/Day3_*.asset
Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset
```

后续新增武器时，应沿用正式的 `weapon.*` / `*.day*` 身份体系和 `Docs/21-weapon-identity-matrix.csv`，不得再新增 `weapon.lpw.*` 或 `lpw.*` 正式条目。

### 3.3 动画运行方式

LPFP 存在完整的原厂 Animator Controller 和 FBX animation clips，但项目运行时的第一人称动画仍由当前架构统一驱动：

```text
WeaponDefinition
  → FirstPersonAnimations（Native）
  → FPWeaponAnimator
  → Animancer
```

因此：

- 原厂 Controller 是动画来源和行为参考，不应仅因项目 prefab YAML 中没有显式 `m_Controller` 就判断动画缺失；
- 正式检查应解析 `WeaponDefinition` 的 Native clip 集，而不是套用 LPW 实验线的动画规则；
- `Idle`、`Fire`、换弹、`Draw`、`Holster`、`AimIn`、`AimOut` 等实际动作是否可用，应以定义中解析出的 clips 和实机表现为准；
- `DryFire` 等可选 clip 为空时，若运行时已有安全降级，不得自动判定整把武器动画不完整；
- 不得为了兼容 LPW，再为 LPFP 引入第二套并行状态机或姿态驱动器。

### 3.4 Gameplay 与网络层保持项目自研

“全面转向 LPFP”指统一素材与表现层基线，不代表直接复制 LPFP Demo 的全部脚本。以下系统继续使用本项目现有实现：

- 玩家移动、输入和状态机；
- 武器数值、弹药、换弹、切枪与命中结算；
- Animancer 动画路由；
- Cinemachine 相机、ADS 与后坐力；
- FishNet 网络同步与服务器权威；
- Lobby、商城、配装、HUD、结算与后端接口。

LPFP Demo 脚本和 Demo Scene 仅可作为理解资源结构的参考，不得绕过项目架构直接接入正式场景。

---

## 4. Arena_LPWTest 与 LPW 资产的后续定位

### 4.1 保留但不再开发

现阶段不要求立即删除 LPW 资产或 `Arena_LPWTest.unity`，避免破坏历史引用和未整理的工作区内容。它们统一视为：

- 历史实验记录；
- 技术问题复盘样本；
- 必要时用于比对旧实现的只读参考。

保留不等于继续支持。任何正式功能不得依赖只有 `Arena_LPWTest` 才存在的对象、prefab、配置或运行时代码。

### 4.2 禁止继续投入的事项

除非用户以后明确撤销本决策，否则不再执行：

- 为 29 把 LPW 枪逐枪校准 Hip/ADS/IK/配件挂点；
- 修复只在 `Arena_LPWTest` 中出现的动画或开火表现问题；
- 扩充 `LPWWeaponManifest` 或生成新的 LPW 包装 prefab；
- 将 LPW 实验结果设为正式 Arena 的验收门槛；
- 为同时兼容 LPW 和 LPFP 修改正式动画、相机或武器架构；
- 在新的商城、背包、配装或网络数据中加入 `weapon.lpw.*` 商品。

### 4.3 可以复用的内容

LPW 实验中得到的通用工程经验仍可复用，例如单写入者原则、避免动画与程序化姿态争抢 Transform、配置审计方法及问题定位记录。但复用的是经验，不是继续维护 LPW 资产链。

---

## 5. 后续实施规则

### 5.1 新功能落点

| 功能 | 正式落点 |
|---|---|
| 战斗场景 | `Assets/_Project/Scenes/Arena.unity` |
| 第一人称武器 | `Assets/_Project/Prefabs/Weapons/FP_*.prefab` |
| 第三人称武器 | `Assets/_Project/Prefabs/Weapons/TP_Weapon_*.prefab` |
| 武器定义 | `Assets/_Project/ScriptableObjects/Weapons/Day2_*.asset`、`Day3_*.asset` |
| 平衡数据 | `Day2_DemoBalance.asset` 或后续正式 BalanceConfig |
| 原始模型与动画 | `Assets/Low Poly FPS Pack/` |
| 武器身份 | `Docs/21-weapon-identity-matrix.csv` 中的非 LPW 正式条目 |

### 5.2 每把 LPFP 武器的接入完成条件

一把武器只有同时满足以下条件，才可进入正式 Lobby 配装和 Arena：

1. `WeaponDefinition` 的 ID、类型、数值和 Native animation clips 正确；
2. FP/TP prefab 引用正确，切枪时不会残留旧视图；
3. 腰射、ADS、开火、换弹、切入和切出动作可用；
4. 枪口、弹匣、左右手握点和必要配件挂点明确；
5. BalanceConfig 存在同 ID 条目，不依赖缺失项的默认兜底；
6. Lobby 预览、配装数据和 Arena 运行时解析到同一个武器身份；
7. 离线与联网模式共用同一份正式武器定义，不创建 LPW 专用分支。

### 5.3 对后续人类或 AI 实施者的要求

开始任何武器、动画、配件、Arena 或联网任务前，必须先遵守本文件：

- 搜索到 LPW/LWPTest 相关资产时，默认把它们视为历史实验内容；
- 不得因为旧计划、旧审计或脏工作区仍包含 LPW 文件，就推断 LPW 仍是当前开发目标；
- 若任务描述同时涉及 LPW 与 LPFP，必须先向用户确认；在没有新指令时，以 LPFP 为唯一正式方向；
- 修改正式系统时，不得以“继续兼容 LPW”为理由扩大实现范围。

---

## 6. 与旧文档的关系

本文件在“武器资产与表现层方向”上具有更高优先级，并作如下取代：

- `Docs/22-firing-jitter-audit.md`：保留为 LPW 实验审计记录，不再是待实施修复计划；
- `Docs/23-联网对战收口实施计划.md` §1.4、§3.2 中“LPW 基线测试进行中/活跃区”的描述：自本决策起失效；联网工作应以 LPFP 正式 Arena 为基线重新确认现场；
- `Docs/24-联网对战收口执行提示词.md` 中所有以“LPW 基线测试仍在进行”为前提的冻结说明：视为历史现场保护条款，不再代表产品方向；工作区中属于用户的未提交改动仍必须保护，不能因此擅自删除或回退；
- `Docs/21-weapon-identity-matrix.csv`：继续作为武器身份参考，但其中 `weapon.lpw.*` 行只保留历史含义，不进入新的正式商品、配装或网络数据。

若其他旧文档与本文件冲突，以用户最新指令和本文件为准。

---

## 7. 一句话交接结论

**停止 Arena_LPWTest 和 LPW 枪械实验线；保留其历史资产但不再开发。后续以 Arena 正式场景、LPFP 原厂素材、项目现有 WeaponDefinition + Animancer + Gameplay/Network 架构，完成整个 FPS 项目的建设。**
