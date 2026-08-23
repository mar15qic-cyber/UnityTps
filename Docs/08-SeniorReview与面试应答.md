# 08 Senior Review 与面试应答（原方案 §18）

## 18. Senior Review（面试官视角）

**像真实工程项目吗？** —— 是，且结构上是国内中大型项目常见形态：网络同步策略自研于插件之上、服务器权威战斗、状态/事件二分、Lua 数值层、HTTP 账号服。流程闭环（登录→对战→成长）给了完整「商业感」叙事。

**容易被认为「插件拼装」的部分**（要主动防御）：
- 登录/HTTP 部分（模板感强）→ 讲清 JWT 流程与信任边界取舍
- FishNet Pro / Animancer / FinalIK 等多为付费插件（共 4+ 资产级插件），容易被质疑「堆插件」→ **主动展示自研部分**：AnimDriver 的状态→clip 映射代码、CombatResolver 单点判定、三 FSM 门控矩阵、ActionSystem 打断规则、自定义 CinemachineComponent（sway/bob/recoil）、AimIK 目标=同步瞄准方向——证明插件只提供地基与工具，业务全自研
- NetworkTransform 直同步（如果 prediction 回退了）→ 必须能讲「为什么可以不用」以及「用的话怎么做」
- 大厅 UI（无技术含量）→ 一句话带过，别花面试时间

**体现个人能力的部分**（面试主打）：
1. State Ownership 表 + 单写者原则（直接回应真实工程痛点）
2. 三 FSM 正交分解 + 门控矩阵 + 打断优先级（§6，有理论有取舍）
3. 「网络同步 Action 语义而非动画」的三端一致性流程（§6.6，本项目最独特的讲解点）
4. ActionSystem 与动画解耦（RE4 踩坑→本项目的防御设计，**最好的成长叙事**）
5. 状态同步 vs 事件同步的划分与开火全链路
6. 服务器权威 + 验证点清单（FireRequest 校验什么）
7. Lua/C# 边界（白名单式纯函数暴露）
8. asmdef 编译期架构隔离

**应砍**：手雷（候选）、MatchRecord（可选）、死亡观战等一切锦上添花。
**加分项**：Prediction 跑通（哪怕基于官方 demo 改造）、EF Migration、热更演示视频、架构图文档。

**「为什么这样设计」应答框架（背熟）**：
- 为什么 FishNet 不用 NGO/Mirror/Fusion？→ 源码可读性/Pro 工具链（Prediction+LagComp）/授权与学习曲线权衡（§2 表）
- 三个 FSM 为什么分开、不合成一个大状态机？→ 状态爆炸(≈96态)/三维度同步策略不同/行业惯例正交叠加；Fire 为何不是状态（§6.1）
- 网络怎么同步动画？→ **不同步动画，同步 Action 语义**：SyncVar 兜底 + 事件触发表现 + 本机预测去重（§6.6）
- 为什么用 Animancer 不用 AnimatorController？→ 资产图不可 diff/不可单测/AI 无法可靠编辑；Animancer 让状态→动画映射全部显式在代码，且它只是播放引擎，可整体替换（§7）
- 为什么用 Cinemachine 而不全手写相机？→ 它只提供 rig/priority 切换与 pipeline；sway/bob/recoil/ADS 是自己写的自定义 Component（讲 pipeline 扩展点）；死亡观战切换几乎免费
- 为什么用 FinalIK？→ 远端瞄准方向是**网络状态**，动画混合无法精确表达枪口指向；AimIK 读状态做姿态校正，纯表现层可整体移除
- 为什么服务器权威？→ 客户端权威在 FPS 意味着什么；哪些验证点必须在服务器（§5 表B）
- 为什么 ActionSystem？→ 讲 RE4 reload 卡死案例：真相在计时器，动画是表现（§6）
- 为什么 Lua 只放数值？→ 热更边界与风险控制；Lua 碰 Transform/网络对象会破坏权威模型（§12）
- Lag Compensation 为什么一行就能接入？→ CombatResolver 单点化（所有判定只在此发生）+ 讲 history buffer/回滚射线原理（§9）
- 为什么战绩客户端自报？→ 信任边界取舍，正确方案是什么（§11）
