using System;
using System.Collections.Generic;
using FishNet;
using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Object;
using Game.Gameplay.Action;
using Game.Gameplay.Health;
using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 玩家网络适配器（Docs/19 N1 → Day3 Phase 1/2 重构）：
    /// ① 服务器权威：服务器按固定 tick（TimeManager）消费各玩家输入队列并 Locomotor.Simulate——
    ///    唯一权威模拟；每 tick 把 AuthoritativeMovementState 推给 Owner。
    /// ② Owner 纯客户端本地预测：固定 tick 累积器（与渲染帧/服务器 tick 均非一一对应）驱动
    ///    Simulate，输入按模拟 tick 编号批量上传（冗余重传抗丢包）。
    /// ③ 服务器校正：小误差平滑收敛（指数衰减偏移）、大误差硬校正（对位快照 + 从确认 tick+1 重放）；
    ///    死亡/重生清空预测历史；重复/乱序/迟到/失控输入服务器侧至多模拟一次。
    /// ④ 表现切换/移动状态广播/切枪意图转发沿用 N1/N2；远端 NetworkTransform 插值缓冲按 RTT 抖动
    ///    自适应调整（上下限钳制）；开火请求携带服务器 tick 估算供有限回溯命中判定（Phase 2）。
    /// 单人离线零影响：网络未启动时本组件不做任何事（_initialized 门）。
    /// </summary>
    [DefaultExecutionOrder(-120)]
    public sealed class PlayerNetworkAdapter : NetworkBehaviour
    {
        [Header("本地/远端组件开关（留空=按类型自动查找）")]
        [SerializeField] private GameObject localOnlyRoot;   // CameraPivot：仅 Owner 启用（FP 相机/FP 武器/HUD 全挂其下）
        [SerializeField] private Behaviour[] ownerOnlyComponents; // InputReader 等仅 Owner 运行
        [SerializeField] private Behaviour[] remoteOnlyComponents; // 远端表现组件（N2 预留：TP 武器同步器等）

        private Locomotor _locomotor;
        private InputReader _input;
        private ActionSystem _actions;
        private Arsenal _arsenal;
        private NetworkCombatAuthority _combatAuthority;
        private WeaponController _weaponController;
        private bool _initialized;

        // ---- Day3 Phase 1：预测/校正状态 ----
        private readonly PredictionBuffer _buffer = new(MovementPredictionConfig.ClientHistoryCapacity);
        private readonly TickAccumulator _accumulator = new();
        private readonly ServerInputQueue _serverQueue = new();
        private readonly List<MovementCommand> _serverBatch = new();
        private readonly ReconcileGate _gate = new();
        private readonly DivergenceGate _divergenceGate = new();
        private float _fixedDelta = 1f / 30f;
        private uint _localTick;
        private uint _hostSourceTick;
        private uint _lastAckedClientTick;
        private uint _lastServerTick;
        private uint _localTickAtLastAck;
        private int _ticksSinceSend;
        private bool _smoothActive;
        private Vector3 _smoothOffsetRemaining;
        // 审计 2026-09-15 §4：平滑校正台账——配对历史必须包含全部已施加校正；ACK 到达时扣除
        // "已确认快照之后"施加的步进（且只在 ACK 追过该步进后才剪枝），防止同一段过时误差被重复施加
        // （视点振荡/晃动根因）。§2：溢出（ACK 长期不推进）置粘滞标记 → 必须硬重基。
        private readonly SmoothCorrectionLedger _correctionLedger = new();
        private float _pendingYaw;
        private float _pendingPitch;
        private NetworkTransform _networkTransform;

        // ---- 2026-09-15 渲染位置插值（Owner 表现层；模拟根/服务器权威/碰撞根不动）----
        private const string ViewOffsetNodeName = "ViewSmoothingRoot";
        private Transform _viewOffsetRoot;  // 专用视觉偏移节点（运行时创建，CameraPivot 的父级；唯一写者=本组件）
        private Transform _viewPivot;       // CameraPivot（localOnlyRoot）
        private Vector3 _interpFrom;        // 上一模拟 tick 的根位置
        private Vector3 _interpTo;          // 当前模拟 tick 的根位置
        private bool _interpValid;
        private float _visualOffsetMeters;
        // C1（2026-09-16 审计）：渲染视角 yaw——每帧消费本地 look 输入得到"尚未写入权威身体"的角度，
        // 写在视觉节点上（绕 Y 旋转不改变轴上 pivot 位置）；tick 提交时按"实际写入根的角度增量"扣减。
        private float _viewYawDegrees;
        // C3（2026-09-16 审计）：纠偏视觉残差——普通权威重基保留"纠偏前视觉世界位姿"并按时间常数衰减，
        // 只有真传送/重生才清视觉历史（旧实现把普通纠偏也当传送处理 → 画面跳变直接可见）。
        private Vector3 _visualCarryWorld;

        // M3（2026-09-16 审计）：前进回拉取证——记录每个模拟 tick 的写者链，回拉时导出前后样本。
        private readonly MovementPullbackTrace _pullbackTrace = new();
        private float _nextPullbackLogTime;
        private Vector2 _traceMoveInput;
        private bool _traceSnapApplied;
        private Vector3 _traceSnapFrom;
        private Vector3 _traceSnapTo;
        // §3.3（2026-09-16 审计）：同 tick 模拟状态环形采样——首次分叉字段定位（客户端+服务器共用）。
        private readonly MovementStepTrace _stepTrace = new();
        private readonly List<PullbackTraceSample> _framePullbackSteps = new(8);
        private float _nextStepTraceLogTime;
        private Vector3 _traceRenderPrev;
        private bool _traceRenderPrevValid;
        private int _traceRebaseKind = -1;
        private float _traceRebaseError;
        /// <summary>输入 epoch：启动/冻结/重生/接管边界递增（审计 §3.3"明确输入 epoch"）。</summary>
        private int _inputEpoch;
        /// <summary>最新一次上行的客户端 tick。</summary>
        private uint _latestSentTick;
        /// <summary>最近一次权威快照携带的服务器"无真实输入"步数（审计 §3.2-2）。</summary>
        private int _lastIdleStepsAtSnapshot;
        /// <summary>输入时间轴是否已与服务器首次对齐（审计 §3.3"收敛初始积压"；会话内只做一次）。</summary>
        private bool _predictionAligned;
        // M1（2026-09-16 审计）：服务器缺口告警节流。
        private float _nextGapLogTime;

        // ---- 2026-09-15 诊断（审计 §2 第 2 项）----
        private readonly MovementDiagnostics _diag = new();
        private float _nextDiagTime;
        private float _nextLeadWarnTime;
        private static int CachedProcessId => _processId != 0 ? _processId : (_processId = FetchProcessId());
        private static int _processId;

        private static int FetchProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        // Day3 验收诊断：校正事件计数（SNAP 罕见 + 平滑收敛 = 通过判据）
        private int _snapCount;

        // 远端插值自适应（观察者侧）：RTT 抖动 → 插值缓冲 tick 数（上下限钳制）
        private const int RemoteInterpolationMinTicks = 2;
        private const int RemoteInterpolationMaxTicks = 8;
        private readonly long[] _rttSamples = new long[8];
        private int _rttIndex;
        private int _rttFilled;
        private int _currentInterpolationTicks = RemoteInterpolationMinTicks;
        private float _nextInterpolationAdjust;

        private void Awake()
        {
            _locomotor = GetComponent<Locomotor>();
            // 审计 2026-09-16 §6.2：快照必须携带基础俯仰（两端对账 + 重生/重基有明确基线）
            if (_locomotor != null) _locomotor.PitchProvider = CurrentPitch;
            _input = GetComponentInParent<InputReader>();
            _actions = GetComponentInParent<ActionSystem>();
            if (ownerOnlyComponents == null || ownerOnlyComponents.Length == 0)
                ownerOnlyComponents = new Behaviour[] { _input };
            // Docs/23 P1-5：死亡冻结判定需要，Awake 即解析（离线无此组件时保持 null，判定安全）
            if (_combatAuthority == null) _combatAuthority = GetComponent<NetworkCombatAuthority>();
            // Docs/23 表现迭代（切枪单一通路）：本地切枪目标统一来自 Arsenal 已解析意图
            _arsenal = GetComponentInParent<Arsenal>();
            if (_arsenal != null) _arsenal.OnSlotIntentResolved += HandleSlotIntentResolved;

            // Day3 Phase 1：NetworkTransform 改服务器权威（无公开 setter，运行时改序列化私有字段；
            // 全部网络回调在其后的 OnStart 阶段读取，时序安全）。Owner 不再上传 transform
            // （服务器 tick 模拟为唯一权威），也不向 Owner 回发（Owner 靠预测+权威校正快照）。
            // 离线 authored 玩家上 NT 永不启动，翻转无副作用。
            // 注：只能用反射写字段——SetSendToOwner 在 Awake（未生成）阶段访问 FishNet 基础设施会 NRE。
            _networkTransform = GetComponent<NetworkTransform>();
            if (_networkTransform != null)
            {
                var type = typeof(NetworkTransform);
                type.GetField("_clientAuthoritative",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(_networkTransform, false);
                type.GetField("_sendToOwner",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(_networkTransform, false);
                // 2026-09-16 审计 D4：prefab 的 _enableTeleport=0 → 服务器侧重生/传送（根位置瞬移）
                // 不会被通知给观察者，观察者会沿旧插值历史从死亡点滑向出生点（模型先复活、根还在路上）。
                // 与上面两处同源的运行时字段翻转（零 prefab 改动）；每 tick 位移 ≤ 冲刺一 tick（~11cm）
                // 远低于 _teleportThreshold=1m，不会误触发。
                type.GetField("_enableTeleport",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(_networkTransform, true);
            }
        }

        private void OnDestroy()
        {
            if (_arsenal != null) _arsenal.OnSlotIntentResolved -= HandleSlotIntentResolved;
            UnwireServerTick();
        }

        /// <summary>Arsenal 已解析的切枪意图 → 服务器验证（数字键/滚轮/Q 三路同源，一次解析一次提交）。
        /// 服务器 Host 本地切枪不经网络（SubmitSwitchRequest 自身有守卫）；离线时 Submit 安全空转。</summary>
        private void HandleSlotIntentResolved(int slot)
        {
            if (_combatAuthority == null) _combatAuthority = GetComponent<NetworkCombatAuthority>();
            if (_combatAuthority != null) _combatAuthority.SubmitSwitchRequest(slot);
        }

        // ---- FishNet 生命周期 ----

        public override void OnStartNetwork()
        {
            _initialized = true;
            int tickRate = TimeManager != null ? (int)TimeManager.TickRate : 30;
            _fixedDelta = 1f / Mathf.Max(1, tickRate);
            EnsureBodyHitbox();
#if UNITY_EDITOR
            Debug.Log($"[PlayerNetworkAdapter] net start: isServer={IsServerInitialized} ownerLocal={Owner.IsLocalClient} tickRate={tickRate}", this);
#endif
        }

        // ---- 2026-09-19 第五轮：复合受击体（躯干 + 头部双胶囊，用户实机拍板"动工1"）----
        // 第三轮把胶囊**中心**标定到身体，但半径 0.35 维持旧值（方案 §6.2 禁盲缩、待四向扫描）。
        // 14:16 实机视频（修复后构建）证实：头部可视半宽 ≈0.12m，头旁 0.2m+ 的"可见空气"仍在
        // 0.35 半径胶囊内被判定命中（帧 h_018：瞄头旁后方空气掉血+血雾悬空）。
        // 用户拍板按方案 §6.2 升级路径做复合代理：躯干 + 头部双胶囊，同节点（BodyHitbox）双
        // Collider——归因链（collider→TP_Model 的 DamageableTarget）、延迟补偿注册
        // （GetComponentsInChildren<Collider>）、HitVolumeTag 角色语义全部不变。

        /// <summary>躯干胶囊中心（模型系，米）：覆盖脚底→肩部（y 0..1.40）。z 沿用第三轮标定
        /// 的身体前后中心 0.333。半径 0.26 依据：持枪姿态躯干可视半宽 ≈0.22~0.26m
        /// （0.35 旧值让躯干旁约 0.09~0.13m 可见空气可命中）。</summary>
        [Tooltip("躯干受击胶囊中心（模型系，米）。")]
        [SerializeField] private Vector3 torsoHitboxCenter = new Vector3(0f, 0.70f, 0.333f);
        [Tooltip("躯干受击胶囊半径（米）：持枪躯干可视半宽 ≈0.22~0.26。")]
        [SerializeField, Min(0.05f)] private float torsoHitboxRadius = 0.26f;
        [Tooltip("躯干受击胶囊总高（米）：脚底 0 → 肩部 1.40。")]
        [SerializeField, Min(0.2f)] private float torsoHitboxHeight = 1.40f;

        /// <summary>头部胶囊中心（模型系，米）：覆盖颈部→头顶（y 1.40..1.80，与躯干在 1.40
        /// 无缝重叠，颈/肩不漏判）。半径 0.15 依据：头盔外廓可视半宽 ≈0.12~0.15m。</summary>
        [Tooltip("头部受击胶囊中心（模型系，米）。")]
        [SerializeField] private Vector3 headHitboxCenter = new Vector3(0f, 1.60f, 0.333f);
        [Tooltip("头部受击胶囊半径（米）：头盔外廓可视半宽 ≈0.12~0.15。")]
        [SerializeField, Min(0.05f)] private float headHitboxRadius = 0.15f;
        [Tooltip("头部受击胶囊总高（米）：颈 1.40 → 头顶 1.80。")]
        [SerializeField, Min(0.2f)] private float headHitboxHeight = 0.40f;

        /// <summary>Day3 验收修复（既有缺口）：玩家可受击碰撞体此前只有根 CharacterController 胶囊，
        /// 而 DamageableTarget 挂在 TP_Model 子节点——CombatResolver 的 GetComponentInParent 归因链
        /// 从根胶囊向上搜索永远打不到，联网/离线射击均无法对玩家造成伤害。
        /// 生成时在 TP_Model 下补一个身体胶囊 hitbox 子物体（归因链：hitbox → TP_Model 自身 DamageableTarget）。
        /// 层必须用玩家根节点层（武器 hitMask 含 Default 而不含 LocalPlayerBody），否则射线直接过滤掉。
        /// 运行时添加，零 prefab/场景改动；authored 假人不走 OnStartNetwork、行为不变。</summary>
        private void EnsureBodyHitbox()
        {
            var model = transform.Find("TP_Model");
            if (model == null) return;
            if (model.GetComponent<DamageableTarget>() == null) return;
            var hitboxTransform = model.Find("BodyHitbox");
            if (hitboxTransform == null)
            {
                var hitboxGo = new GameObject("BodyHitbox");
                hitboxGo.transform.SetParent(model, false);
                hitboxTransform = hitboxGo.transform;
                hitboxGo.layer = gameObject.layer; // 与玩家根节点同层（hitMask 可命中）
                hitboxGo.AddComponent<CapsuleCollider>(); // [0] 躯干
                hitboxGo.AddComponent<CapsuleCollider>(); // [1] 头部
            }

            // 2026-09-10 审计 §3：受击判定与移动阻挡分离。实体胶囊与根 CharacterController 胶囊
            // 几乎重合，是"双玩家贴近反复推拉抖动"的确定性冲突源；trigger 后玩家间唯一移动阻挡
            // = 根 CharacterController（客户端预测与服务器权威阻挡同一来源），命中查询由
            // CombatResolver 显式 QueryTriggerInteraction.Collide 保证（全场景无其他 trigger）。
            // 第五轮：双胶囊**每次调用都按序列化参数重配**——池化复用对象可能是旧单胶囊（只有
            // [0]），在此补齐 [1] 并整体重配；旧形状/旧字段值一律被当前参数覆盖（幂等自愈）。
            var hitboxColliders = hitboxTransform.GetComponents<CapsuleCollider>();
            if (hitboxColliders.Length < 2)
                hitboxTransform.gameObject.AddComponent<CapsuleCollider>();
            hitboxColliders = hitboxTransform.GetComponents<CapsuleCollider>();
            ConfigureHitboxCapsule(hitboxColliders[0], torsoHitboxCenter, torsoHitboxRadius, torsoHitboxHeight);
            ConfigureHitboxCapsule(hitboxColliders[1], headHitboxCenter, headHitboxRadius, headHitboxHeight);
            // Day4 残余审计 P0-1（纵向契约，第五轮双胶囊表述）：躯干下端=脚底（y=0）、头上端=头顶
            // （y=1.80）、两胶囊在 y=1.40 无缝重叠（颈/肩不漏判）——纵向半身高不再由单一 center 表达，
            // 由 torso/head 两组参数直接给出；BodyHitboxAlignmentTests 的纵向断言锁这条。
            // 2026-09-18 第三轮（排查报告 §3.1）：center 的 **z 分量必须贴合可见 TP 模型**（0.333）。
            // 2026-09-19 第四轮（实机 03:50 视频取证 + 本轮分析）：钉扎**不得用当帧位姿**。
            // 本方法只在 OnStartNetwork 跑一次；池化复用/死亡表现未复位时 TP_Model 可能带着
            // "前倾 85°+贴地抬升"等瞬态姿态，InverseTransformPoint(当帧) 会把瞬态烤进受击体
            // 局部位置，此后受击体与可见身体永久错位（实机"瞄模型后方空气仍掉血 + 血雾悬空"，
            // 错位量与死亡抬升同量级 ≈0.4~0.8m，远超第三轮标定后 0.09m 的代理容差）。
            // 正确锚 = **作者基准**（与 EnsureRemoteVisualBaseline 同源，死亡门禁已内置）：
            // 解 "作者位姿下中心=根∘bodyHitboxCenter，且模型偏离基准时中心随身体" 得
            // pin = R_base⁻¹·(bodyHitboxCenter − p_base) − bodyHitboxCenter（模型系常量，
            // 与当帧姿态无关）。模型在基准位姿时中心=根+(0,0.9,0.333)（Day4/第三轮契约保持），
            // 模型偏离基准（视觉平滑/死亡）时中心随身体走——"跟身体"才是可维持的不变量，
            // "跟根"只在基准位姿那一刻可定义。baseline 不可用（极端时序）时退用死亡存档位姿；
            // 两者皆无则保留原钉不动（错误的重钉比保留更危险），由 ResetDeathVisual 的
            // 兜底重钉（RepinBodyHitboxAfterRestore）在死亡复位后修正。
            EnsureRemoteVisualBaseline();
            if (_tpModelBaseCaptured)
            {
                hitboxTransform.localPosition = PinInModelSpace(
                    _tpModelBaseLocalPosition, _tpModelBaseLocalRotation);
            }
            else if (_combatAuthority != null
                     && _combatAuthority.TryGetAuthoredTpLocalPose(out var authoredPos, out var authoredRot))
            {
                hitboxTransform.localPosition = PinInModelSpace(authoredPos, authoredRot);
            }
            // 2026-09-18 审计 §5：受击体与移动阻挡体按【显式角色】登记，射击判定按角色过滤而不是
            // 按名字包含 Player。BodyHitbox = 可归属受击面；本玩家对象的 CharacterController =
            // 只挡移动。运行时装配，零 prefab 改动（与上面的 hitbox 同源）。
            Game.Gameplay.Combat.HitVolumeTag.Assign(
                hitboxTransform.gameObject, Game.Gameplay.Combat.HitVolumeRole.DamageSurface);
            EnsureMovementBlockerRoles();
        }

        /// <summary>受击体节点在模型系的常量钉扎（2026-09-19 第四/五轮）：把节点原点钉到
        /// "根在模型系的表达" = −(R_base⁻¹ · p_base)，与具体胶囊中心无关——复合受击体的
        /// 躯干/头部两个 center 都表达在此节点坐标系内，随节点一起满足：
        /// 作者位姿下中心=根∘(R_base·center)（基准恒等旋转时即 根∘center），
        /// 模型偏离基准（视觉平滑/死亡）时中心随身体走。纯数学，无场景依赖。</summary>
        private Vector3 PinInModelSpace(Vector3 baseLocalPos, Quaternion baseLocalRot)
            => -(Quaternion.Inverse(baseLocalRot) * baseLocalPos);

        /// <summary>按参数重配一个受击胶囊（trigger + Y 轴 + 中心/半径/高）。幂等，每次
        /// EnsureBodyHitbox 都执行——池化复用/旧序列化值在此被当前参数整体覆盖。</summary>
        private static void ConfigureHitboxCapsule(CapsuleCollider capsule, Vector3 center, float radius, float height)
        {
            capsule.isTrigger = true;
            capsule.center = center;
            capsule.radius = radius;
            capsule.height = height;
            capsule.direction = 1; // Y 轴
        }

        /// <summary>死亡表现复位后的受击体兜底重钉（2026-09-19 第四轮）：
        /// NetworkCombatAuthority.ResetDeathVisual 还原 TP_Model 作者位姿后调用——此刻死亡门禁
        /// 解除、baseline 必然可捕获，任何此前被瞬态姿态污染的钉扎都在这里被正确值覆盖。
        /// 幂等；无模型/无 hitbox（未生成受击面的 authored 假人）安全跳过。</summary>
        internal void RepinBodyHitboxAfterRestore()
        {
            if (transform.Find("TP_Model") == null) return;
            EnsureBodyHitbox();
        }

        /// <summary>把本玩家对象的移动控制器显式声明为"只挡移动、不参与受击判定"（审计 §5）。
        /// 只标本对象子树的控制器，不触碰无关 CharacterController；DS/Host/纯客户端同一入口，
        /// 判定真相三端一致。</summary>
        private void EnsureMovementBlockerRoles()
        {
            var controllers = GetComponentsInChildren<CharacterController>(true);
            for (int i = 0; i < controllers.Length; i++)
                Game.Gameplay.Combat.HitVolumeTag.Assign(
                    controllers[i].gameObject, Game.Gameplay.Combat.HitVolumeRole.MovementBlocker);
        }

        public override void OnStartClient()
        {
            bool isOwner = IsOwner;
            // Locomotor must not keep its serialized OfflineLocal Update path once networked.
            // Host is server-authoritative even though it is also the local owner.
            if (IsServerInitialized)
                _locomotor?.SetSimulationMode(MovementSimulationMode.ServerAuthority);
            else
                _locomotor?.SetSimulationMode(isOwner
                    ? MovementSimulationMode.PredictedOwner
                    : MovementSimulationMode.RemoteProxy);
            if (isOwner)
            {
                // Owner：本地输入+相机+HUD 全开；纯客户端走预测（Update），Host 上走服务器 tick 模拟
                SetActiveAll(localOnlyRoot, true);
                SetBehaviours(ownerOnlyComponents, true);
                SetBehaviours(remoteOnlyComponents, false);
                // 2026-09-15 渲染位置插值前置：把 CameraPivot 挪到专用偏移节点下（运行时、零资产改动）
                EnsureViewOffsetRoot();
                ResetOwnerPredictionState();
                // 2026-09-08 P0 §6 一.3：运行时不变量探针（Development/Editor）——
                // 延迟 1s 扫描本进程唯一性（InputReader/主相机/AudioListener/Owner Player = 1）
                GameplayClientInvariantProbe.EnsureAttachedTo(gameObject);
            }
            else
            {
                // 远端玩家的化身：输入/相机/HUD 全关；TP 视觉层由 ApplyRemoteVisualLayers
                // 按实例改到可见层（主相机剔除 LocalPlayerBody 是进程级判定，远端不能留在层 8）
                SetActiveAll(localOnlyRoot, false);
                SetBehaviours(ownerOnlyComponents, false);
                SetBehaviours(remoteOnlyComponents, true);
                // 远端移动动画状态源不再由本组件反射注入（2026-09-16 审计 D1：类型检查恒 false 的
                // 死接线）——TPAnimDriver 自己按"远端替身是否启用"选择 NetworkLocomotionState。
                // 方案A（2026-09-16 实测定案）：远端根/CC 停在最新权威位姿（NT 插值压到最小），
                // 让本地 Owner 的预测碰撞与服务器用同一个碰撞对象；视觉平滑由 TP_Model 承担。
                if (MovementPredictionConfig.RemoteAuthoritativeCollisionEnabled)
                {
                    EnsureRemoteVisualBaseline();
                    if (_networkTransform != null) _networkTransform.SetInterpolation(1);
                }
            }
            ApplyRemoteVisualLayers();
        }

        // ---- 2026-09-10 双人实测审计 §2：Owner/Remote 视觉分层 ----

        private const string VisualModelNode = "TP_Model";
        private const string BodyHitboxNode = "BodyHitbox";
        private int _localBodyLayer = int.MinValue;

        /// <summary>接管/池化复用/网络停止边界：还原并作废远端视觉平滑状态（死亡期间不动 TP_Model）。</summary>
        private void ResetRemoteVisualSmoothing()
        {
            if (_tpModelBaseCaptured)
            {
                bool dead = _combatAuthority != null && _combatAuthority.IsDead;
                var model = transform.Find(VisualModelNode);
                if (model != null && !dead)
                {
                    model.localPosition = _tpModelBaseLocalPosition;
                    model.localRotation = _tpModelBaseLocalRotation;
                }
            }
            _tpModelBaseCaptured = false;
            _remoteVisualValid = false;
            _remoteVisualBuffer.Reset();
        }

        /// <summary>接管/池化复用（Owner 得失）时与生成路径走同一分层不变量（FishNet 4.7：客户端侧回调）。</summary>
        public override void OnOwnershipClient(NetworkConnection prevOwner)
        {
            base.OnOwnershipClient(prevOwner);
            ApplyRemoteVisualLayers();
            ResetRemoteVisualSmoothing(); // 角色可能翻转（远端↔Owner）：视觉平滑状态不得跨角色存活
            if (IsOwner && !IsServerInitialized)
            {
                // 2026-09-13 专项：接管时预测基线（tick/历史/ACK 门）是前任会话残留，OnStartClient
                // 不会再次回调——必须与新生成路径走同一重置，否则新 Owner 用旧 ACK 基线丢弃有效快照
                EnsureViewOffsetRoot();
                ResetOwnerPredictionState();
            }
        }

        /// <summary>Owner 预测状态全量重置（生成/接管/会话边界共用）：历史缓冲、tick 基线、
        /// ACK 门、分叉滞回与平滑偏移一并归零，等首个权威快照重新对位。</summary>
        private void ResetOwnerPredictionState()
        {
            _ownerWasDead = false;
            _buffer.Clear();
            _serverQueue.Clear();
            _localTick = 0;
            _lastAckedClientTick = 0;
            _lastServerTick = 0;
            _localTickAtLastAck = 0;
            _smoothActive = false;
            _smoothOffsetRemaining = Vector3.zero;
            _correctionLedger.Invalidate();
            _accumulator.Reset();
            _gate.Reset();
            _divergenceGate.Reset();
            _pullbackTrace.Clear();
            _framePullbackSteps.Clear();
            _traceSnapApplied = false;
            _traceRebaseKind = -1;
            _traceRebaseError = 0f;
            _traceRenderPrevValid = false;
            // 审计 §3.3：新的输入基线必须带新 epoch（旧输入与新模拟基线不得混用）
            _inputEpoch++;
            _predictionAligned = false;
            _latestSentTick = 0;
            _ticksSinceSend = 0; // 审计 2026-09-17 D5：发送相位残留（接管/会话边界后首个批次提前/滞后半周期）
            _lastIdleStepsAtSnapshot = 0;
            ResetOwnerVisualInterpolation();
        }

        /// <summary>每实例视觉层不变量：主相机 cullingMask 按层位整进程判定，分不清"自己/他人"——
        /// 全项目玩家身体/TP 武器共用 LocalPlayerBody(8) 且本地主相机剔除层 8，导致远端玩家
        /// 身体/武器/配件对本机相机全部不可见（2026-09-10 双人实测"互相看不到"确定根因）。
        /// 修复：Owner 树保持 LocalPlayerBody（自隐藏防挡镜）；远端实例 TP_Model 整树改到玩家根层
        /// （主相机与实体镜 RT 相机可见）。BodyHitbox 恒在玩家根层（武器 hitMask 依赖），不随视觉递归变化；
        /// 后续换枪/配件实例由 TPWeaponMeshSwapper 按 weaponBone 当前层拷贝，自动跟随本分层。</summary>
        private void ApplyRemoteVisualLayers()
        {
            var model = transform.Find(VisualModelNode);
            if (model == null) return;
            if (_localBodyLayer == int.MinValue)
                _localBodyLayer = LayerMask.NameToLayer("LocalPlayerBody");
            int visibleLayer = gameObject.layer;
            bool remote = !IsOwner;
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name == BodyHitboxNode) continue;
                if (remote)
                {
                    t.gameObject.layer = visibleLayer;
                }
                else if (_localBodyLayer >= 0 && t.gameObject.layer == visibleLayer)
                {
                    // 池化复用：曾被远端分层到根层的节点恢复 LocalPlayerBody（其余节点保持原层不动）
                    t.gameObject.layer = _localBodyLayer;
                }
            }
        }

        /// <summary>
        /// [已删除 2026-09-16 审计 D1] 远端 TP 动画状态源的反射接线：旧实现要求
        /// `TPAnimDriver.stateView` 字段的类型可容纳 `RemotePlayerStateView`，但该字段是
        /// `PlayerStateView`（sealed，且 RemotePlayerStateView 并非其子类）→ 类型检查恒 false、
        /// 静默不接线，远端化身永远读本地不模拟的 Locomotor（滑步/错误姿态）。
        /// 现由 TPAnimDriver 显式选择状态源（远端替身启用 → NetworkLocomotionState）。
        /// </summary>

        public override void OnStartServer()
        {
            // 2026-09-17 实机修复（远端 TP 抬头/低头瞬回）：纯 Dedicated 上 OnStartClient 不会回调，
            // 远端玩家的 CameraPivot(FPMouseLook) 一直激活——FPMouseLook._pitch 恒 0，每帧 LateUpdate
            // 把 ApplyRemotePitch 写入的权威俯仰清零，_aimPitch 只在消费输入的瞬间闪现
            // （实机症状：远端 TP 抬头/低头后立即回正）。服务器无本地观察者，按 OnStartClient 远端分支
            // 同口径去激活本地表现（相机/FP 臂/HUD 在 DS 全无用；localOnlyRoot 失活后
            // ApplyRemotePitch/TrySyncAimPitch 读写 localRotation 不受影响）。Host 的本人玩家
            // 由 OnStartClient Owner 分支重新激活，不受影响（FN weaver 禁 IsOwner→Owner.IsLocalClient）。
            bool ownedByLocalClient = NetworkObject != null && NetworkObject.Owner != null && NetworkObject.Owner.IsLocalClient;
            if (!ownedByLocalClient) DeactivateLocalPresentationForServer();
            _locomotor?.SetSimulationMode(MovementSimulationMode.ServerAuthority);
            // Day3 Phase 1：服务器固定 tick 权威模拟（订阅本实例 tick 处理器）
            WireServerTick();
            // Day3 Phase 2：注册 hitbox 历史（仅联网生成的玩家；authored 假人不注册）。
            // Phase 2：同时传入本实例 NetworkCombatAuthority——快照按采集 tick 记录生命代际/无敌态，
            // 命中判定据此拒绝旧生命回溯伤害新生命与出生保护期伤害（注册时 NCA 已随 Awake/OnStartServer 就绪）。
            var lagComp = ServerLagCompensation.Instance;
            if (lagComp != null)
                lagComp.RegisterPlayer(transform, GetComponentsInChildren<Collider>(true),
                    GetComponent<NetworkCombatAuthority>());
            // 2026-09-08 追加 P0 §6 二.3：服务器权威配装（早于 Arsenal.Start 首装，时序见下）
            ApplyAuthoritativeLoadoutOnServer();
        }

        // ---- 服务器权威配装（2026-09-08 追加 P0 §6 二.3/二.4，审计 §5.1 缺口 2）----

        private TicketLoadoutSnapshot _authoritativeLoadout;
        private WeaponController _serverWeaponController;

        /// <summary>服务器权威配装快照（OnStartServer 写入；NetworkWeaponState 据此广播
        /// 权威附件快照，Gate A-2）。</summary>
        public TicketLoadoutSnapshot AuthoritativeLoadout => _authoritativeLoadout;

        /// <summary>
        /// 服务器在网络玩家生成回调配置权威两槽（§6 二.3）。时序不变式：
        /// FishNet OnStartServer 同步于生成、早于 Unity Start——Arsenal.Start 的首次装备
        /// （equipInitialWeaponOnStart）必然消费本处 ConfigureSlots 后的槽位，而非 prefab 十槽。
        /// 数据源 = JoinTicketAuthenticator.AcceptedUsers[Owner.ClientId]（FinishAuthentication 在
        /// OnAuthenticationResult 之前写入——PlayerSpawner 更在其后，档案必已就位）。
        /// 无档案（F1 调试 Host/本地连接）→ 明确保留 prefab 十槽调试路径；
        /// 有档案但解析失败 → fail closed：拒绝生成 + 断开并给明确错误，绝不回退调试 Arsenal。
        /// </summary>
        private void ApplyAuthoritativeLoadoutOnServer()
        {
            if (_arsenal == null) _arsenal = GetComponentInParent<Arsenal>();
            if (_arsenal == null) return;

            TicketConsumeResult identity = null;
            long clientId = -1;
            var networkObject = NetworkObject;
            if (networkObject != null && networkObject.Owner != null)
                clientId = networkObject.Owner.ClientId;
            var authenticator = FishNet.InstanceFinder.NetworkManager != null
                ? FishNet.InstanceFinder.NetworkManager.GetComponent<JoinTicketAuthenticator>()
                : null;
            if (authenticator != null && clientId >= 0)
                authenticator.AcceptedUsers.TryGetValue((int)clientId, out identity);

            if (identity == null)
            {
                Debug.LogWarning($"[PlayerNetworkAdapter] 服务器权威配装跳过（无认证档案——F1 调试 Host/本地连接）：保留 prefab 调试 Arsenal conn={clientId}", this);
                return;
            }

            string error = NetworkLoadoutPolicy.TryApplyServerSlots(_arsenal, identity.Loadout);
            if (!string.IsNullOrEmpty(error))
            {
                // fail closed（§6 二.3）：拒绝生成 + 断开，明确错误（不含密钥/票据）
                Debug.LogError($"[PlayerNetworkAdapter] SERVER_LOADOUT_REJECTED user={identity.UserId} conn={clientId}：{error}——拒绝生成并断开", this);
                NetworkObject?.Despawn();
                NetworkObject?.Owner?.Disconnect(true);
                return;
            }

            _authoritativeLoadout = identity.Loadout;
            _serverWeaponController = GetComponentInParent<WeaponController>();
            if (_serverWeaponController != null)
                _serverWeaponController.OnWeaponEquipped += HandleServerEquipApplyAttachments;
            Debug.Log($"[PlayerNetworkAdapter] SERVER_LOADOUT_APPLIED user={identity.UserId} conn={clientId} primary={identity.Loadout?.primaryWeaponId} secondary={identity.Loadout?.secondaryWeaponId}", this);
        }

        /// <summary>服务器装备期重套权威配件（§6 二.4 六处一致）：EquipDefinition 先 Reset(null)——
        /// 本钩子在其后按当前槽位从权威快照重套（含加长弹匣的弹容量/散布/后坐属性重建）。
        /// 审计 2026-09-15 §3.5：槽位按"装备的定义属于哪个槽"解析，不依赖 Arsenal.ActiveIndex——
        /// 该索引在装备回调时刻可能尚未对齐（切槽 RPC/权威校正经此路径），读它会把主槽配件装到副枪上。</summary>
        private void HandleServerEquipApplyAttachments(WeaponDefinition equipped)
        {
            var networkObject = NetworkObject;
            if (networkObject == null || !networkObject.IsServerInitialized) return;
            if (_serverWeaponController == null || _authoritativeLoadout == null || equipped == null) return;
            int slot = ResolveSlotIndexOf(equipped);
            var entries = NetworkLoadoutPolicy.ResolveAttachmentEntries(_authoritativeLoadout, slot, this);
            _serverWeaponController.SetAttachments(entries);
        }

        /// <summary>定义 → 权威两槽索引（按 Arsenal.Slots 引用比对；不属于两槽回退 Primary）。</summary>
        private int ResolveSlotIndexOf(WeaponDefinition definition)
        {
            if (_arsenal == null) _arsenal = GetComponentInParent<Arsenal>();
            if (_arsenal == null) return 0;
            var slots = _arsenal.Slots;
            for (int i = 0; i < slots.Count; i++)
                if (slots[i] == definition) return i;
            return 0;
        }

        public override void OnStopServer()
        {
            UnwireServerTick();
            ServerLagCompensation.Instance?.UnregisterPlayer(transform);
            _serverQueue.Clear();
            if (_serverWeaponController != null)
                _serverWeaponController.OnWeaponEquipped -= HandleServerEquipApplyAttachments;
            _serverWeaponController = null;
            _authoritativeLoadout = null;
        }

        public override void OnStopNetwork()
        {
            _locomotor?.SetSimulationMode(MovementSimulationMode.OfflineLocal);
            _initialized = false;
            ResetRemoteVisualSmoothing();
            base.OnStopNetwork();
        }

        // R4（审计 2026-09-17）：订阅幂等旗标——同实例若被 FishNet 重复 OnStartServer（接管/网络
        // 重启场景），双重订阅会让服务器每 tick 双消费命令 + 双发快照（等价时间轴压缩一半）。
        private bool _serverTickWired;

        private void WireServerTick()
        {
            if (_serverTickWired || NetworkObject == null) return;
            var timeManager = NetworkObject.TimeManager; // NetworkObject 存在时取值安全
            if (timeManager == null) return;
            timeManager.OnTick += ServerTick;
            _serverTickWired = true;
        }

        private void UnwireServerTick()
        {
            if (!_serverTickWired) return;
            if (NetworkObject != null)
            {
                var timeManager = NetworkObject.TimeManager;
                if (timeManager != null) timeManager.OnTick -= ServerTick;
            }
            _serverTickWired = false; // 无网络时也复位（防御：不留滞留旗标封死后续 Wire）
        }

        // ---- 每帧驱动 ----

        private void Update()
        {
            if (!_initialized) return;

            if (IsOwner || IsServerInitialized)
                _diag.NoteFrame(Time.unscaledDeltaTime, _fixedDelta);

            if (IsOwner)
            {
                bool frozen = LocalMovementFrozen;
                if (frozen)
                {
                    // 冻结（死亡/倒计时/菜单）：不采输入；look 累积清零防解冻暴冲
                    _pendingYaw = 0f;
                    _pendingPitch = 0f;
                }
                else if (_input != null)
                {
                    // 鼠标 look 增量逐帧累积，生成命令时整段消费（tick 驱动下不丢不重）
                    float lookYaw = _input.LookDelta.x * 0.1f;
                    _pendingYaw += lookYaw;
                    _pendingPitch += _input.LookDelta.y * 0.1f;
                    // C1（2026-09-16 审计）：渲染视角 yaw 逐帧消费**同一份**输入（权威身体仍等 tick 提交）。
                    // 只在累积点加一次，tick 提交时按"已提交输入"扣减——不重复消费、不回写碰撞根。
                    // 2026-09-16 实机修复：累积**不得**钳制（旧实现钳在累积端 → 饱和一次就留下永不衰减的
                    // 粘滞残差，见 AccumulateViewYawLead 注释）。钳制只在写视觉节点时做。
                    if (!IsServerInitialized && MovementPredictionConfig.ViewYawInterpolationEnabled)
                        _viewYawDegrees = AccumulateViewYawLead(_viewYawDegrees, lookYaw);
                }

                if (!IsServerInitialized)
                {
                    if (frozen)
                    {
                        ResetOwnerVisualInterpolation(); // 冻结：视觉归零，解冻/重生不做穿场插值
                        EmitMovementDiagnostics("owner");
                        return; // 纯客户端 Owner：冻结期不预测、不上报
                    }
                    // 纯客户端 Owner：本地固定 tick 预测 + 批量上传 + 服务器校正
                    RunOwnerPrediction();
                    UpdateOwnerVisualPosition();
                    RecordPullbackTrace();
                    HandleOwnerCombatRequests();
                }
                // Host Owner（Editor/Development 调试）：命令在 ServerTick 从同一累积量采样，权威管线一致
                EmitMovementDiagnostics("owner");
            }
            else
            {
                // 远端玩家实例：位置由服务器权威 NetworkTransform 呈现。
                // 方案A（2026-09-16 实测定案）：根/CC 停在**最新权威位姿**（NT 插值压到最小），
                // 让本地 Owner 的预测碰撞与服务器用同一个碰撞对象（消除近距 CC 去贯穿分歧）；
                // 视觉平滑由 TP_Model 指数追随根承担（UpdateRemoteVisualSmoothing）。
                if (MovementPredictionConfig.RemoteAuthoritativeCollisionEnabled)
                {
                    UpdateRemoteVisualSmoothing();
                }
                else
                {
                    // 旧行为（A/B 对比）：根插值 2-8 tick，按 RTT 抖动自适应
                    UpdateRemoteInterpolation();
                }
                EmitRespawnTrace(); // [RespawnTrace] 临时取证：窗口外零开销直接返回
            }
        }

        /// <summary>测试接缝（2026-09-16 审计 C1/C2 定向用例）：&lt;0 用 Time.deltaTime；
        /// ≥0 时按指定秒数推进预测累积器（EditMode 下无法控制 Time.deltaTime）。
        /// 产品路径不设置它。</summary>
        public float TestPredictionDeltaTime { get; set; } = -1f;

        /// <summary>Owner 预测主循环（纯客户端）：按模拟 tick 采输入→预测→记录→定期批量上传。</summary>
        private void RunOwnerPrediction()
        {
            if (_locomotor == null) return;
            double deltaTime = TestPredictionDeltaTime >= 0f ? TestPredictionDeltaTime : Time.deltaTime;
            int steps = _accumulator.Advance(deltaTime, _fixedDelta, MovementPredictionConfig.ClientMaxCatchUpSteps);
            _diag.NoteGenerated(steps);
            for (int i = 0; i < steps; i++)
            {
                uint tick = ++_localTick;
                bool jump = _input != null && _input.JumpQueued;
                var cmd = new MovementCommand(
                    _input != null ? _input.Move : Vector2.zero,
                    _input != null && _input.Sprint,
                    jump,
                    _pendingYaw, _pendingPitch, tick);
                if (jump && _input != null) _input.ConsumeJump();
                _pendingYaw = 0f;
                _pendingPitch = 0f;

                _buffer.TryStore(cmd);
                Vector3 moveBefore = transform.position;
                _locomotor.Simulate(cmd, _fixedDelta);
                // 审计 §3.2-4：MoveAfter 必须**紧接** Simulate 采样（旧实现在软校正之后取，
                // 把 Move 写者和 Smooth 写者混成一条位移，归因必然失真）。
                Vector3 moveAfterSimulate = transform.position;

                // C1（2026-09-16 实测修正）：视角超前量的扣减基准 = **本 tick 提交的输入**（cmd.YawDelta），
                // 不是身体实际转过的角度。旧实现按"实际旋转"扣减——被后坐补偿债务吃掉的那部分输入
                // 永远留在超前量里（压枪一次泄漏一点，累积成 4-5° 的永久视角/身体夹角 = 用户实测的
                // "按 W 向左偏"；对着墙按 W+Shift 会沿身体方向滑出墙外）。视角超前量的语义只允许
                // "本 tick 已积累、尚未提交"的输入，债务消费发生在提交点，不得计入。
                if (MovementPredictionConfig.ViewYawInterpolationEnabled)
                    _viewYawDegrees = ConsumeViewYawLead(_viewYawDegrees, cmd.YawDelta);

                // 小误差平滑收敛：逐模拟步消费衰减校正偏移（大误差走 HardSnapTo）。
                // 审计 2026-09-15 §4：先施加本步校正、再记录预测快照——配对历史必须包含全部
                // 已施加校正，否则 ACK 配对会把已施加的误差再次计入（重复纠偏）。
                Vector3 appliedSmooth = Vector3.zero;
                if (_smoothActive)
                {
                    Vector3 step = Reconciler.SmoothStep(_smoothOffsetRemaining, _fixedDelta);
                    _smoothOffsetRemaining -= step;
                    if (_smoothOffsetRemaining.sqrMagnitude < 1e-8f)
                    {
                        _smoothActive = false;
                        _smoothOffsetRemaining = Vector3.zero;
                    }
                    // 2026-09-10 审计 §3：平滑校正与硬校正统一走 Locomotor 写入入口（CC 短暂禁用后
                    // 直写 Transform），不再与 CharacterController 碰撞解算同帧盲写物理根互相拉扯
                    _locomotor.ApplySmoothCorrection(step);
                    appliedSmooth = step;
                    if (!_correctionLedger.Record(tick, step))
                    {
                        // 台账溢出 = ACK 长时间未推进：在途量已丢失，平滑语义不可信——停用并等硬重基
                        // （Overflowed 粘滞标记由 ApplyOwnerAuthoritativeState 消费，不在此处自行复位）
                        _smoothActive = false;
                        _smoothOffsetRemaining = Vector3.zero;
                    }
                    _diag.NoteSmoothApplied(step.magnitude);
                }
                // C2（2026-09-16 审计）：插值端点必须**逐步**更新——本帧模拟 2–5 步时旧实现把
                // from/to 记在整帧首尾，区间跨多 tick 而 alpha 仍按单 tick 余量 → 位置白落后
                // （1.58m/s 两步 alpha=.5：旧=0.0527m、正确=0.0790m），且单步/多步帧交替会变速。
                Vector3 moveAfterSmooth = transform.position;
                _interpFrom = moveBefore;
                _interpTo = moveAfterSmooth;
                _interpValid = true;
                _buffer.RecordPredicted(_locomotor.CaptureSnapshot());
                // M3/§3.3 取证：本步写者链 + 完整模拟状态（渲染位置在 UpdateOwnerVisualPosition 之后补记）
                _traceMoveInput = cmd.Move;
                CaptureClientStepEvidence(cmd, moveAfterSimulate, moveAfterSmooth, appliedSmooth);
                _framePullbackSteps.Add(new PullbackTraceSample
                {
                    ClientTick = tick,
                    MoveInput = cmd.Move,
                    MoveBefore = moveBefore,
                    MoveAfter = moveAfterSimulate,
                    MoveAfterSmooth = moveAfterSmooth,
                    SmoothCorrection = appliedSmooth,
                    SnapApplied = _traceSnapApplied,
                    SnapFrom = _traceSnapFrom,
                    // 重基+重放完成后的最终根（_traceSnapTo 在 HardSnapTo 重放结束后写入）
                    SnapTo = _traceSnapTo,
                    RootAfterAll = transform.position,
                    Forward = transform.forward,
                });
                if (++_ticksSinceSend >= MovementPredictionConfig.ClientBatchSendEveryTicks)
                {
                    _ticksSinceSend = 0;
                    SendUnackedCommands();
                }
            }
        }

        private void SendUnackedCommands()
        {
            if (!FishNetLifecycleGuard.CanSubmitRpc(this)) return;
            var unacked = _buffer.CollectUnacked(_lastAckedClientTick, MovementPredictionConfig.ClientBatchMaxCommands);
            if (unacked.Count == 0) return;
            var payload = new MovementCommand[unacked.Count];
            for (int i = 0; i < unacked.Count; i++) payload[i] = unacked[i];
            ServerSubmitInputBatch(payload);
            _latestSentTick = payload[payload.Length - 1].Tick;
            _diag.NoteSubmit(payload.Length);
        }

        /// <summary>Owner 战斗意图上行（纯客户端）：开火携带服务器 tick 估算（Phase 2 回溯），换弹走服务器验证。</summary>
        private void HandleOwnerCombatRequests()
        {
            if (_weaponController == null) _weaponController = GetComponentInParent<WeaponController>();
            if (_combatAuthority == null) _combatAuthority = GetComponent<NetworkCombatAuthority>();
            if (_weaponController != null && _combatAuthority != null && _input != null && _input.FireHeld)
                _combatAuthority.SubmitFireRequest(EstimateServerTick());
            if (_combatAuthority != null && _input != null && _input.ReloadPressed)
                _combatAuthority.SubmitReloadRequest();
        }

        /// <summary>服务器 tick 估算：最新权威快照 ServerTick + 快照后已预测的本地 tick 数。</summary>
        private uint EstimateServerTick()
        {
            return _lastServerTick + (_localTick - _localTickAtLastAck);
        }

        // ---- 服务器权威模拟（Day3 Phase 1 核心）----

        /// <summary>服务器 tick 处理器（每实例独立订阅；每玩家一队列，高延迟玩家不拖慢他人/不改变全局 tick）。
        /// 编排层：Host 采样入队 → 输入推进核心 → 向 Owner 推快照 → 诊断。
        /// IsOwner 单次求值（FishNet 缓存只在网络生命周期内有效；核心逻辑抽到 AdvanceServerInputs
        /// 供 EditMode 直驱，不触碰任何 FishNet 网络成员）。</summary>
        private void ServerTick()
        {
            if (_locomotor == null) return;
            bool ownerTick = IsOwner;

            // Host 调试（Owner 在服务器本地）：从同一输入累积量采样命令入队——与远端玩家完全同一条权威管线
            if (ownerTick && _input != null && !LocalMovementFrozen)
            {
                bool hostJump = _input.JumpQueued;
                var hostCmd = new MovementCommand(
                    _input.Move, _input.Sprint, hostJump, _pendingYaw, _pendingPitch, ++_hostSourceTick);
                if (hostJump) _input.ConsumeJump();
                _pendingYaw = 0f;
                _pendingPitch = 0f;
                _serverQueue.Enqueue(hostCmd);
            }

            AdvanceServerInputs(applyRemotePitch: !ownerTick);

            // 每 tick 向 Owner 推权威快照（Host Owner 本身即权威，不发）
            var owner = NetworkObject != null ? NetworkObject.Owner : null;
            if (!ownerTick && owner != null && owner.IsValid)
                TargetAuthoritativeState(owner, BuildAuthoritativeState());

            EmitMovementDiagnostics("server");
        }

        /// <summary>
        /// 服务器输入推进核心（审计 2026-09-17 D2/R1；EditMode 可直接反射驱动）：
        /// 冻结 → 清队列；否则有限追赶消费（R1），空 tick 保持位姿不模拟（D2）。
        /// </summary>
        internal void AdvanceServerInputs(bool applyRemotePitch)
        {
            if (MovementFrozen)
            {
                // 死亡/倒计时冻结：丢弃待处理输入（防解冻后爆发重放），权威快照照发（客户端对位）
                _serverQueue.Clear();
                return;
            }

            // R1（审计 2026-09-17）：每 tick 有限追赶（接线既有常量；此前恒 Drain(1) 使突发积压
            // **没有任何路径能追回**——客户端单帧可突发 5 步，两端平均速率相同，lead 一旦跃升
            // 就永久抬升 = 权威位姿相对画面永久滞后、硬对位位移恒为 lead×速度）。
            // 追赶仍逐步用 fixedDelta 模拟（不加速时间）；Snapshot.Tick 与 LastClientTick
            // 依然一致（都取本 tick 消费的末条命令）。
            int count = _serverQueue.Drain(MovementPredictionConfig.ServerMaxCatchUpPerTick, _serverBatch);
            _diag.NoteServerConsumed(count); // 0 = 本 tick 空步（丢包/空闲都会推高）
            // M1（审计 2026-09-16）：缺口不再静默——ACK 的语义是"已处理到该 tick"，
            // 缺口是"跳过并结算"（那些输入永不被模拟），必须留痕才能判上行漏发/积压。
            if (_serverQueue.LastDrainGap > 0 && Time.unscaledTime >= _nextGapLogTime)
            {
                _nextGapLogTime = Time.unscaledTime + 1f;
                Debug.LogWarning($"[Day3][Input] GAP skipped={_serverQueue.LastDrainGap} total={_serverQueue.SkippedTicks} lastTick={_serverQueue.LastProcessedTick} qdepth={_serverQueue.Count} — 缺口视为跳过并结算（未被模拟），由客户端预测纠偏吸收");
            }
            if (count == 0)
            {
                // D2（审计 2026-09-17）：空队列**保持位姿**——不外推、不喂空命令，完全不模拟。
                // 旧两版均有静态可证缺陷：空命令（Move=0）被地面减速 48 m/s² 在 0.07s 内刹停
                // （客户端仍在前进，每个到批间隙制造约一个身位误差）；"按最后已知输入有界外推"
                // 则把服务器单方推走 k 步——菜单/聊天冻结时客户端已停预测，k×步长 100% 计入
                // 配对误差（外推位移不被任何配对口径承认，Snapshot.Tick=ack+k 违反配对不变量）。
                // 保持位姿后：冻结场景两端同静（误差恒 0）；瞬时到批间隙的 1–3 步位移 ≤0.34m
                // 落平滑带由平滑收敛，批次到达后 R1 追赶立即回填。快照照发——重复 ACK 由客户端
                // ACK 门忽略（IgnoreRepeat），不产生虚假比较。空步不做 step 取证（未模拟时
                // LastStepDebug 是上一步陈旧值，入证必误导），靠 empty 计数 + ConsecutiveEmptyTicks。
                if (_serverQueue.ConsecutiveEmptyTicks == 1)
                    ExportServerStepTraceWindow("IDLE_START");
                return;
            }
            for (int i = 0; i < count; i++)
            {
                _locomotor.Simulate(_serverBatch[i], _fixedDelta);
                if (applyRemotePitch) ApplyRemotePitch(_serverBatch[i].PitchDelta);
                CaptureServerStepEvidence(_serverBatch[i], null);
            }
        }

        private AuthoritativeMovementState BuildAuthoritativeState()
        {
            var snapshot = _locomotor != null ? _locomotor.CaptureSnapshot() : default;
            return new AuthoritativeMovementState
            {
                ServerTick = TimeManager != null ? TimeManager.Tick : 0,
                LastClientTick = _serverQueue.LastProcessedTick,
                // 审计 §3.2-2：告诉客户端"本快照的位姿比 LastClientTick 多推进了几步无输入步"
                IdleStepsAtSnapshot = _serverQueue.ConsecutiveEmptyTicks,
                Dead = _combatAuthority != null && _combatAuthority.IsDead,
                Snapshot = snapshot,
            };
        }

        [ServerRpc(RequireOwnership = true)]
        private void ServerSubmitInputBatch(MovementCommand[] commands)
        {
            EnqueueServerInputBatch(commands);
        }

        /// <summary>服务器入队入口（与 RPC 载体分离，EditMode 可直驱）。</summary>
        internal void EnqueueServerInputBatch(MovementCommand[] commands)
        {
            if (commands == null) return;
            // D3（审计 2026-09-17）：冻结窗口（倒计时/死亡）拒收在途批次。冻结期客户端本就不产
            // 输入（LocalMovementFrozen 包含两者），此时到达的只可能是"冻结快照到达客户端之前"
            // 已发出的批次——若照常入队，死亡→重生后旧 epoch 命令会被当作新输入消费（客户端重生
            // 从 ack 重新生成同号 tick，旧命令先到先消费会把新命令挤成 stale 静默丢弃）。
            // 丢弃即清理 epoch 残留；每 tick 的 Clear() 保留兜底。协议不为此加 epoch 字段（留待 v5）。
            if (MovementFrozen)
            {
                _serverQueue.NoteDroppedFrozen(commands.Length);
                return;
            }
            for (int i = 0; i < commands.Length; i++)
                _serverQueue.Enqueue(commands[i]);
        }

        // ---- 校正（Owner 接收服务器权威快照）----

        private bool _ownerWasDead;

        [TargetRpc]
        private void TargetAuthoritativeState(NetworkConnection connection, AuthoritativeMovementState state)
        {
            if (IsServerInitialized) return; // Host：自身即权威
            ApplyOwnerAuthoritativeState(state);
        }

        private void ApplyOwnerAuthoritativeState(AuthoritativeMovementState state)
        {
            _lastServerTick = state.ServerTick;
            _lastIdleStepsAtSnapshot = state.IdleStepsAtSnapshot;
            _diag.AuthoritativePitchDegrees = state.Snapshot.Pitch;

            if (state.Dead)
            {
                bool firstDeathSnapshot = !_ownerWasDead;
                _ownerWasDead = true;
                // 死亡边界：终态无条件处理（不得被重复/乱序门拦截——死亡后输入停止，
                // LastClientTick 相同的重复死亡快照是常态），清空全部预测历史并硬对位。
                if (firstDeathSnapshot) _inputEpoch++; // 审计 §3.3：死亡是新 epoch（旧输入不得回灌）
                _gate.Observe(state.LastClientTick);
                _lastAckedClientTick = _gate.LastAckedClientTick;
                _divergenceGate.Reset();
                _buffer.Clear();
                _correctionLedger.Invalidate();
                _smoothActive = false;
                _smoothOffsetRemaining = Vector3.zero;
                _locomotor?.ApplyAuthoritativeSnapshot(state.Snapshot);
                _localTick = state.LastClientTick;
                if (firstDeathSnapshot)
                {
                    // 审计 M3：死亡边界也是一次根对位，必须计入（旧实现只统计 SNAP/分叉/溢出）
                    _snapCount++;
                    _diag.NoteRebase(MovementRebaseKind.DeathRespawn);
                }
                ResetOwnerVisualInterpolation(); // 死亡：视觉基准作废，复活不做穿场插值
                return;
            }

            // ACK 门：重复（相等）/乱序快照一律忽略。重复 ACK 期间服务器与客户端输入源不同
            // （输入批次未达服务器），没有可配对的 tick——硬对位会把玩家拉回旧位置（本轮修复的
            // 常态路径缺陷）；批次到达、ACK 前进后配对决策自然校正。
            if (_ownerWasDead)
            {
                // A respawn has no new input ACK while the dead owner is frozen.
                _ownerWasDead = false;
                _inputEpoch++; // 审计 §3.3：重生同样是新 epoch（清掉死亡期的输入时间轴）
                _buffer.Clear();
                _divergenceGate.Reset();
                _gate.Observe(state.LastClientTick);
                _lastAckedClientTick = state.LastClientTick;
                _localTickAtLastAck = state.LastClientTick;
                HardSnapTo(state, MovementRebaseKind.DeathRespawn);
                // 审计 2026-09-16 §2.1：权威重生边界同步本地基础俯仰（服务器重生快照 Pitch=0，
                // 本地积分状态不同步归零 → pitchGap 在重生后持续存在）。只在边界调用一次。
                ApplyOwnerRespawnPitchBaseline();
                return;
            }
            // 审计 §3.3：首个权威快照即对齐输入时间轴——客户端在连接/加载/入场期间会持续预测，
            // 这些 tick 永远不会被服务器消费（服务器每 tick 只消费 1 条，积压不会自行消失），
            // 结果是一个 1 秒量级的**永久领先**：服务器记录的玩家位姿相对客户端画面/命中回溯窗口
            // 系统性滞后（命中注册与"拽回"都对账不上）。这里只做一次、且只在领先明显时收敛。
            if (!_predictionAligned && state.LastClientTick != 0)
            {
                _predictionAligned = true;
                int startupLead = _localTick > state.LastClientTick ? (int)(_localTick - state.LastClientTick) : 0;
                if (startupLead > MovementPredictionConfig.ClientLeadWarnTicks)
                {
                    Debug.LogWarning($"[Day3][Input] LEAD_ALIGN local={_localTick} serverAck={state.LastClientTick} "
                        + $"lead={startupLead} — 首个权威快照收敛入场期累积的输入领先");
                    _gate.Observe(state.LastClientTick);
                    _lastAckedClientTick = state.LastClientTick;
                    _localTickAtLastAck = state.LastClientTick;
                    _buffer.Clear();
                    _divergenceGate.Reset();
                    HardSnapTo(state, MovementRebaseKind.Initial);
                    return;
                }
            }
            switch (_gate.Feed(state.LastClientTick))
            {
                case ReconcileGateAction.IgnoreRepeat:
                    return;
                case ReconcileGateAction.InitialSnap:
                    HardSnapTo(state, MovementRebaseKind.Initial);
                    return;
                case ReconcileGateAction.Compare:
                    _lastAckedClientTick = _gate.LastAckedClientTick;
                    _localTickAtLastAck = _localTick;
                    break;
            }

            // 必须在剪枝前读取 ACK 对应快照；TryGetPredictedAndPrune 保证两步不可反序。
            if (!_buffer.TryGetPredictedAndPrune(state.LastClientTick, out var predictedAtAck))
            {
                // ACK 前进但无配对历史（容量淘汰）：保守硬对位
                HardSnapTo(state, MovementRebaseKind.HistoryMissing);
                return;
            }

            // 审计 2026-09-17 D1（Critical）：在途扣除必须在双阈值判定**之前**——raw 误差包含
            // "配对快照之后已施加、尚未被任何快照计入"的平滑步进（台账口径），直接用 raw 判 Snap
            // 会把本应平滑（0.03–0.75 带）的误差误判硬对位；且旧判定序 Snap 跳过台账消费、
            // HardSnapTo 又 Invalidate，在途量每次硬对位后从零重积——"从不进平滑带 + err 挤在
            // 阈值上方 + 一旦开始停不下来"的风暴形态（2026-09-17 局 47 次 SNAP）即由此来。
            // 台账消费的逐 ACK 增量语义不变（审计 2026-09-15 §2）：只剪枝 tick ≤ ack，
            // 在途条目保留给后续更老的 ACK 继续扣除。
            Vector3 inFlight = _correctionLedger.ConsumeInFlightAfter(state.LastClientTick);
            var decision = Reconciler.Decide(state.Snapshot, predictedAtAck, inFlight,
                out var correction, out var corrected);
            // G3（审计 2026-09-17）：err 双口径留证——err=corrected（真实残余误差）、errRaw=raw。
            _diag.LastErrorMeters = corrected.magnitude;
            _diag.LastRawErrorMeters = correction.magnitude;
            if (decision.Action == ReconcileAction.Snap)
            {
                // Day3 验收证据：硬校正应为罕见事件（风暴=持续拉回/双模拟的信号）
                Debug.Log($"[Day3][Reconcile] SNAP err={corrected.magnitude:F3}m raw={correction.magnitude:F3}m inflight={inFlight.magnitude:F3}m serverTick={state.ServerTick} ack={state.LastClientTick} totalSnaps={_snapCount + 1}");
                HardSnapTo(state, MovementRebaseKind.Snap, corrected.magnitude);
                return;
            }
            if (_divergenceGate.Feed(decision))
            {
                // 2026-09-13 专项：位置接近但速度/朝向/移动分支持续分叉——速度错误不能只平滑位置，
                // 必须重放恢复影响后续积分的全部状态（速度/计时/步态相位），再重放未确认输入。
                _smoothActive = false;
                _smoothOffsetRemaining = Vector3.zero;
                Debug.Log($"[Day3][Reconcile] DIVERGENCE {decision.Divergence} serverTick={state.ServerTick} ack={state.LastClientTick} err={corrected.magnitude:F3}m raw={correction.magnitude:F3}m totalSnaps={_snapCount + 1}");
                HardSnapTo(state, MovementRebaseKind.Divergence, corrected.magnitude);
                return;
            }
            // 审计 2026-09-15 §2：台账溢出（ACK 长时间不推进）时在途量已丢失，平滑不再可信——
            // 此处必须硬重基（旧实现只是停用一次平滑，后续 ACK 又会把它重新启用）。
            if (_correctionLedger.Overflowed)
            {
                Debug.LogWarning($"[Day3][Reconcile] LEDGER_OVERFLOW_REBASE ack={state.LastClientTick} serverTick={state.ServerTick} totalSnaps={_snapCount + 1}");
                HardSnapTo(state, MovementRebaseKind.Overflow);
                return;
            }

            // corrected 已在判定前算好（None / Smooth 同源）：None 分支同样消费——
            // 误差已进噪声带而旧 remaining 仍在消费 = "进入阈值后继续消费旧纠偏"。
            const float smoothThreshold = MovementPredictionConfig.ReconcileSmoothMeters;
            if (corrected.sqrMagnitude <= smoothThreshold * smoothThreshold)
            {
                _smoothActive = false;
                _smoothOffsetRemaining = Vector3.zero;
                _diag.PendingMeters = 0f;
            }
            else
            {
                _smoothOffsetRemaining = corrected;
                _smoothActive = true;
                _diag.NoteSmooth(corrected.magnitude, corrected.magnitude, correction.magnitude);
            }
        }

        /// <summary>
        /// 硬重基：对位权威快照 → 从确认 tick+1 重放未确认输入 → 视觉/诊断收尾。
        /// 审计 2026-09-16 M3/C3 + 2026-09-17 R5 修正：
        /// ① 每一次重基（首次对位/历史缺失/SNAP/分叉/溢出/重生）都计入诊断（旧实现只统计其中三类，
        ///    `snaps=0` 会被误读成"没有任何根对位"）；
        /// ② 重放因命令缺口中断时，作废 (replayed, newest] 的陈旧预测快照**与命令**并计数
        ///    （未模拟区间），tick 基线回退到实际重放完成的 replayed——旧 `max(replayed, newest)`
        ///    会声称模拟了从未模拟的区间（逻辑 tick 与物理状态脱节、静默持续）；
        /// ③ 普通纠偏保留"纠偏前视觉世界位姿"作为残差衰减（视觉连续），重生/真传送才清视觉历史。
        /// </summary>
        private void HardSnapTo(AuthoritativeMovementState state, MovementRebaseKind kind, float errorMeters = 0f)
        {
            Vector3 previousVisualWorld = _viewOffsetRoot != null ? _viewOffsetRoot.position : transform.position;
            Vector3 rootBefore = transform.position;
            _locomotor?.ApplyAuthoritativeSnapshot(state.Snapshot);
            Vector3 rootAfterSnapshot = transform.position;
            _smoothActive = false;
            _smoothOffsetRemaining = Vector3.zero;
            _correctionLedger.Invalidate(); // 绝对对位覆盖一切在途步进（审计 §4）

            _snapCount++;
            _diag.NoteRebase(kind, errorMeters);
            Debug.Log($"[Day3][Reconcile] REBASE {kind} ack={state.LastClientTick} serverTick={state.ServerTick} totalRebases={_snapCount} idleSteps={state.IdleStepsAtSnapshot}");

            // M3 取证：重基写者（渲染位置在本帧末尾补记；SnapTo 在重放结束后写入最终根）
            _traceSnapApplied = true;
            _traceSnapFrom = rootBefore;
            _traceSnapTo = rootAfterSnapshot;
            _traceRebaseKind = (int)kind;
            _traceRebaseError = Vector3.Distance(rootBefore, rootAfterSnapshot);

            uint replayed = state.LastClientTick;
            if (state.LastClientTick != 0 && _buffer.HasCommands)
            {
                uint newest = _buffer.NewestTick;
                for (uint tick = state.LastClientTick + 1; tick <= newest; tick++)
                {
                    if (!_buffer.TryGetCommand(tick, out var cmd)) break; // 缺口：命令不可得，停止重放
                    Vector3 replayBefore = transform.position;
                    _locomotor?.Simulate(cmd, _fixedDelta);
                    _buffer.RecordPredicted(_locomotor.CaptureSnapshot());
                    // 审计 §3.3：重放步同样留证（首步的分支输入来自快照接地，是"分叉是否消除"的直接证据）
                    CaptureClientStepEvidence(cmd, transform.position, transform.position, Vector3.zero, "REPLAY",
                        replayBefore);
                    replayed = tick;
                }
                if (replayed < newest)
                {
                    // 缺口区间从未被模拟（服务器侧同样跳过——它从未收到这些输入）：预测快照**与命令**
                    // 一并作废。R5（审计 2026-09-17）：旧实现只作废快照、保留命令并把 _localTick 顶到
                    // newest，等于声称"已模拟到 newest"而实际只模拟到 replayed（逻辑 tick 与物理状态
                    // 脱节且静默持续）；"不回退避免 TryStore 拒绝"的旧顾虑依赖"命令仍在缓冲"——命令
                    // 作废后回退到 replayed 不再撞 TryStore，后续 tick 从 replayed+1 重新生成。
                    _buffer.InvalidatePredictedAbove(replayed);
                    _buffer.InvalidateCommandsAbove(replayed);
                    _diag.NoteReplayGap((int)(newest - replayed));
                    Debug.LogWarning($"[Day3][Reconcile] REPLAY_GAP ack={state.LastClientTick} replayed={replayed} newest={newest} invalidated={newest - replayed} localTick={replayed}");
                }
                // R5：tick 基线 = 实际重放完成的最大 tick（必须与物理状态一致）。
                // 无缺口时 replayed == newest（TryStore 单调/PruneUpTo 只删 ≤ack/容量 ≫ lead 的
                // 命令连续性不变量保证），与旧 max(replayed, newest) 结果相同——行为只在不可达的
                // 缺口路径上收紧为"不声称未模拟区间"。
                _localTick = replayed;
            }
            else
            {
                _localTick = state.LastClientTick;
            }
            // 审计 §3.2-3：重放结束后的最终根 = 客户端实际会继续预测的起点（旧实现记对位瞬间的根）
            _traceSnapTo = transform.position;

            // C3：普通纠偏保留视觉残差衰减；重生/真传送（距离超限）才清视觉历史
            if (kind == MovementRebaseKind.DeathRespawn) ResetOwnerVisualInterpolation();
            else CarryVisualAfterRebase(previousVisualWorld);

            // 审计 §3.3：重基本身就是要留证的异常 —— 小窗口导出同 tick 双端状态
            ExportStepTraceWindow($"REBASE_{kind}", state.LastClientTick);
        }

        // ---- 2026-09-15 Owner 渲染位置插值（审计 §5.4：分离模拟与渲染位置）----

        /// <summary>
        /// 运行时保证存在专用视觉偏移节点（CameraPivot 的父级）。为什么需要独立节点（"一个位置写者"）：
        /// ① CameraPivot 的 localPosition 已由 FPCameraRig 独占用于本人倒地视角；
        /// ② 模拟根（本对象，挂 CharacterController）是服务器权威对位的唯一对象，绝不能被表现层写入；
        /// ③ Cinemachine vcam 的 TrackingTarget=CameraPivot + HardLockToTarget：相机渲染位姿**只**由
        ///    CameraPivot 世界位姿决定（Main Camera 由 Brain 驱动，写它无效），所以平滑必须加在
        ///    CameraPivot 之上——用独立父节点承载，三个子系统各写各的、互不覆盖。
        /// 节点创建失败（无 CameraPivot 的离线/自制结构）返回 false，渲染插值自动跳过（零副作用）。
        /// 纯表现层：不改模拟根、不改服务器瞄准/枪口判定基准、不改玩家碰撞根。
        /// </summary>
        private bool EnsureViewOffsetRoot()
        {
            if (_viewOffsetRoot != null && _viewPivot != null && _viewPivot.parent == _viewOffsetRoot) return true;
            if (_viewPivot == null)
                _viewPivot = localOnlyRoot != null ? localOnlyRoot.transform : transform.Find("CameraPivot");
            if (_viewPivot == null) return false;
            // 池化复用：节点可能已由上一会话建好
            var existing = transform.Find(ViewOffsetNodeName);
            if (existing != null) { _viewOffsetRoot = existing; return true; }
            if (_viewPivot.parent != transform) return false; // 异形层级：不猜测，直接跳过
            var nodeGo = new GameObject(ViewOffsetNodeName);
            nodeGo.layer = gameObject.layer;
            var node = nodeGo.transform;
            node.SetParent(transform, false);
            node.localPosition = Vector3.zero;
            node.localRotation = Quaternion.identity;
            node.localScale = Vector3.one;
            _viewPivot.SetParent(node, true); // 保持世界位姿（引用型字段不受影响）
            _viewOffsetRoot = node;
            return true;
        }

        /// <summary>渲染插值基准作废（死亡/重生/真传送/冻结/接管）：视觉立即回到模拟根，不做穿场插值。
        /// 注意：普通权威纠偏**不**走这里（C3：纠偏要保留视觉残差衰减，见 CarryVisualAfterRebase）。</summary>
        private void ResetOwnerVisualInterpolation()
        {
            _interpValid = false;
            _interpFrom = transform.position;
            _interpTo = _interpFrom;
            _visualCarryWorld = Vector3.zero;
            _viewYawDegrees = 0f;
            ApplyOwnerVisualOffset(Vector3.zero);
        }

        /// <summary>
        /// 普通权威重基（纠偏）的视觉连续性（审计 2026-09-16 C3）：保留"纠偏前的视觉世界位姿"作为残差，
        /// 之后按 VisualCarryDecaySeconds 衰减到新的模拟轨迹——避免把每次纠偏都当传送、画面瞬间跳变。
        /// 超过 RebaseVisualCarryMaxMeters 视为真传送/重生（不跨图滑动）。
        /// </summary>
        private void CarryVisualAfterRebase(Vector3 previousVisualWorld)
        {
            Vector3 carry = previousVisualWorld - transform.position;
            ResetOwnerVisualInterpolation(); // 插值基准与视角 yaw 一并作废（新轨迹从当前模拟根起算）
            if (_viewOffsetRoot == null) return;
            if (carry.magnitude > MovementPredictionConfig.RebaseVisualCarryMaxMeters) return; // 真传送：不清残差
            _visualCarryWorld = carry;
            ApplyOwnerVisualOffset(carry); // 立即生效，避免重基那一帧视觉跳变
        }

        /// <summary>
        /// C1 视角 yaw 超前量的三件套（2026-09-16 实机修复；唯一写入点语义）：
        /// 语义 = "**本帧已积累、尚未随 tick 提交**的 look 输入角度"。
        /// ① <see cref="AccumulateViewYawLead"/>：逐帧加，**必须无损**——旧实现把 ViewYawClampDegrees
        ///    钳制加在累积端，而扣减端按未钳制的完整提交量扣减：一旦饱和（快甩 &gt;10°）就产生
        ///    `超前量 − 未提交输入` 的残差，且在不饱和的帧里该残差**严格不变**（数学上恒等），
        ///    只能靠死亡/重生复位清零 → 实测 MoveDiag yawLead 出现整段冻住的 1.5/2.3/3.29/4.5/±10
        ///    （t=84→120 等 20–36s 不衰减）。而服务器射线沿身体 yaw（客户端只上传 tick 估算），
        ///    于是残差 = 准心与服务器弹道的恒定夹角：用户表现为"按 W 不走直线"+"打对面不掉血"
        ///    （90 发对账实测 |Δyaw| ≈ |yawLead|，30/30 命中，最大 13.5°）。
        /// ② <see cref="ConsumeViewYawLead"/>：tick 提交时按本 tick 的输入量扣减（不饱和即归零）。
        /// ③ <see cref="ResolveViewYawForDisplay"/>：**唯一**钳制点，只影响写进视觉节点的显示角度，
        ///    不改累积量 → 显示被兜底限制在 ±10°，但残差恒为 0。
        /// </summary>
        internal static float AccumulateViewYawLead(float leadDegrees, float lookYawDeltaDegrees)
            => leadDegrees + lookYawDeltaDegrees;

        /// <inheritdoc cref="AccumulateViewYawLead"/>
        internal static float ConsumeViewYawLead(float leadDegrees, float committedYawDeltaDegrees)
            => leadDegrees - committedYawDeltaDegrees;

        /// <inheritdoc cref="AccumulateViewYawLead"/>
        internal static float ResolveViewYawForDisplay(float leadDegrees)
            => Mathf.Clamp(leadDegrees, -MovementPredictionConfig.ViewYawClampDegrees,
                MovementPredictionConfig.ViewYawClampDegrees);

        /// <summary>把世界空间视觉偏移 + 渲染视角 yaw 写到专用节点（节点是玩家根子级：需换到根局部空间）。</summary>
        private void ApplyOwnerVisualOffset(Vector3 worldOffset)
        {
            _visualOffsetMeters = worldOffset.magnitude;
            if (_viewOffsetRoot == null) return;
            _viewOffsetRoot.localPosition = transform.InverseTransformVector(worldOffset);
            // C1：绕 Y 旋转不改变（Y 轴上的）CameraPivot 位置，但会让相机/FP 武器随视角逐帧转动。
            // 显示角度是唯一的钳制点（累积量保持无损，见 AccumulateViewYawLead）。
            _viewOffsetRoot.localRotation = MovementPredictionConfig.ViewYawInterpolationEnabled
                ? Quaternion.Euler(0f, ResolveViewYawForDisplay(_viewYawDegrees), 0f)
                : Quaternion.identity;
        }

        /// <summary>
        /// 渲染帧位置插值：固定 tick 位移在 120fps 下呈阶梯（走速≈5cm/tick、冲刺≈11cm/tick）。
        /// 用累积器余量在"上一模拟 tick 位置 → 当前 tick 位置"之间插值，得到连续的相机/第一人称位姿。
        /// 代价（有意为之、可在配置里关）：相机位置滞后至多一个模拟 tick（≈33ms @30Hz）。
        /// 水平视角由 C1 的逐帧 yaw 偏移承担（不再随身体 30Hz 台阶）。
        /// 插值只写视觉偏移节点：服务器瞄准/枪口判定与玩家碰撞根仍是模拟根权威值。
        /// </summary>
        private void UpdateOwnerVisualPosition()
        {
            if (!MovementPredictionConfig.RenderPositionInterpolationEnabled && _visualCarryWorld.sqrMagnitude <= 0f) return;
            if (!EnsureViewOffsetRoot()) return;
            if (!_interpValid)
            {
                _interpFrom = _interpTo = transform.position;
                _interpValid = true;
            }
            float alpha = _fixedDelta > 0f
                ? Mathf.Clamp01((float)(_accumulator.Remainder / _fixedDelta))
                : 1f;
            Vector3 rendered = Vector3.Lerp(_interpFrom, _interpTo, alpha);
            Vector3 offset = rendered - transform.position + _visualCarryWorld;
            // C3：纠偏残差按时间常数衰减（视觉平滑吸收，模拟根不受影响）
            if (_visualCarryWorld.sqrMagnitude
                > MovementPredictionConfig.VisualCarryEpsilonMeters * MovementPredictionConfig.VisualCarryEpsilonMeters)
            {
                _visualCarryWorld *= Mathf.Exp(-Time.deltaTime / MovementPredictionConfig.VisualCarryDecaySeconds);
                if (_visualCarryWorld.sqrMagnitude
                    < MovementPredictionConfig.VisualCarryEpsilonMeters * MovementPredictionConfig.VisualCarryEpsilonMeters)
                    _visualCarryWorld = Vector3.zero;
            }
            if (offset.sqrMagnitude
                > MovementPredictionConfig.RenderVisualOffsetClampMeters * MovementPredictionConfig.RenderVisualOffsetClampMeters)
            {
                // 超限（本机卡顿/瞬移/接管残留）：不插值直接归零并重记基准
                _interpFrom = _interpTo = transform.position;
                _visualCarryWorld = Vector3.zero;
                ApplyOwnerVisualOffset(Vector3.zero);
                return;
            }
            ApplyOwnerVisualOffset(offset);
        }

        // ---- M3（2026-09-16 审计）：前进回拉取证 ----

        /// <summary>
        /// 记录本帧的写者链（审计 §3.3：多步帧逐步写环形记录），并（在检测到"前进却向后"时）节流导出。
        /// 判据（§3.2-4 修正后）：输入想前进 且 某写者造成净向后位移——按写者链顺序归因到
        /// MovePhase（CC.Move/碰撞）/ SmoothPhase（平滑校正步进）/ AuthorityCorrection（重基+重放后的
        /// 最终根）/ RenderPhase（**相邻渲染帧**的视觉位置反向）。
        /// 只读不写玩法状态；取证环形缓冲容量固定、稳态零分配。
        /// </summary>
        private void RecordPullbackTrace()
        {
            Vector3 renderNow = _viewOffsetRoot != null ? _viewOffsetRoot.position : transform.position;
            bool hasSteps = _framePullbackSteps.Count > 0;
            if (!hasSteps && !_traceSnapApplied && !_traceRenderPrevValid)
            {
                _traceRenderPrev = renderNow;
                _traceRenderPrevValid = true;
                return;
            }
            if (!hasSteps)
            {
                // 无模拟步的帧：仍记录一条（纯视觉回拉——重基可能把视觉节点写回而本帧没有模拟步）
                var root = transform.position;
                _framePullbackSteps.Add(new PullbackTraceSample
                {
                    ClientTick = _localTick,
                    MoveInput = _traceMoveInput,
                    MoveBefore = root,
                    MoveAfter = root,
                    MoveAfterSmooth = root,
                    SnapApplied = _traceSnapApplied,
                    SnapFrom = _traceSnapFrom,
                    SnapTo = _traceSnapTo,
                    RootAfterAll = root,
                    Forward = transform.forward,
                });
            }

            Vector3 renderPrev = _traceRenderPrevValid ? _traceRenderPrev : renderNow;
            var worst = _pullbackTrace.RecordFrame(_framePullbackSteps, renderPrev, renderNow);
            _traceRenderPrev = renderNow;
            _traceRenderPrevValid = true;
            _framePullbackSteps.Clear();
            _traceSnapApplied = false;
            _traceRebaseKind = -1;
            _traceRebaseError = 0f;

            _diag.NotePullback(worst);
            if (worst == PullbackCause.None || Time.unscaledTime < _nextPullbackLogTime) return;
            _nextPullbackLogTime = Time.unscaledTime
                + MovementPredictionConfig.PullbackTraceLogIntervalSeconds;
            var recent = _pullbackTrace.RecentSamples(1);
            if (recent.Count > 0)
                Debug.LogWarning(MovementPullbackTrace.Format(recent[0], worst), this);
            ExportStepTraceWindow($"PULLBACK_{worst}", _localTick);
        }

        /// <summary>
        /// 记录一步的完整模拟状态证据（审计 §3.3：同 clientTick 的 input/profile hash/完整状态/
        /// CC grounded（分支与 Move 后）/collision flags/软校正向量）。客户端预测与重放、服务器权威
        /// 模拟共用本方法，保证两端样本结构一致、可按 tick 对齐。
        /// </summary>
        private void CaptureClientStepEvidence(in MovementCommand cmd, Vector3 moveAfterSimulate,
            Vector3 moveAfterSmooth, Vector3 smooth, string note = null, Vector3? rootBeforeOverride = null)
        {
            if (_locomotor == null) return;
            var d = _locomotor.LastStepDebug;
            var (connId, objectId) = StepIdentity();
            _stepTrace.Record(new MovementStepSample
            {
                Server = false,
                ClientTick = cmd.Tick,
                Epoch = _inputEpoch,
                Conn = connId,
                ObjId = objectId,
                MoveInput = d.MoveInput,
                Sprint = d.Sprint,
                Jump = d.Jump,
                YawDelta = cmd.YawDelta,
                PitchDelta = cmd.PitchDelta,
                ProfileHash = d.ProfileHash,
                RootBefore = rootBeforeOverride ?? d.RootBefore,
                RootAfterMove = moveAfterSimulate,
                RootAfterSmooth = moveAfterSmooth,
                GroundedBranch = d.GroundedBranch,
                GroundedFromSnapshot = d.GroundedFromSnapshot,
                GroundedAfterMove = d.GroundedAfterMove,
                CollisionFlags = d.CollisionFlags,
                HorizontalVelocity = d.HorizontalVelocity,
                VerticalVelocity = d.VerticalVelocity,
                GroundSpeed = d.GroundSpeed,
                CoyoteTimer = d.CoyoteTimer,
                LandTimer = d.LandTimer,
                GaitPhase = d.GaitPhase,
                State = d.State,
                SprintIntent = d.SprintIntent,
                RecoilDebt = d.RecoilDebt,
                SmoothCorrection = smooth,
                SnapApplied = _traceSnapApplied,
                RebaseKind = _traceRebaseKind,
                ErrorMeters = _traceRebaseError,
                SnapFrom = _traceSnapFrom,
                SnapTo = _traceSnapTo,
                Note = note,
            });
        }

        /// <summary>当前实例的网络身份（G1 审计 2026-09-17：[MoveStep]/[MoveStepWindow] 取证行
        /// 之前无 conn/obj，跨玩家日志无法程序化归属；无网络/离线时 conn=-1）。</summary>
        private (int connId, int objectId) StepIdentity()
        {
            var networkObject = NetworkObject;
            if (networkObject == null) return (-1, 0);
            return (networkObject.Owner != null ? networkObject.Owner.ClientId : -1, networkObject.ObjectId);
        }

        /// <summary>
        /// 小窗口导出同 tick 模拟状态（审计 §3.3"只在 SNAP 前后导出小窗口，避免逐帧刷日志"）。
        /// 节流固定；导出内容=最近若干步的裸状态，供与另一端日志按 tick 对齐找出首个分叉字段。
        /// </summary>
        private void ExportStepTraceWindow(string reason, uint centerTick)
        {
            if (Time.unscaledTime < _nextStepTraceLogTime) return;
            _nextStepTraceLogTime = Time.unscaledTime + MovementPredictionConfig.StepTraceLogIntervalSeconds;
            var window = _stepTrace.Recent(MovementPredictionConfig.StepTraceWindowRadius * 2 + 1);
            var (connId, objectId) = StepIdentity();
            Debug.LogWarning($"[MoveStepWindow] role=c reason={reason} center={centerTick} epoch={_inputEpoch} "
                + $"conn={connId} obj={objectId} local={_localTick} ack={_lastAckedClientTick} "
                + $"lead={_localTick - _lastAckedClientTick} "
                + $"serverIdleSteps={_lastIdleStepsAtSnapshot} steps={window.Count}", this);
            for (int i = 0; i < window.Count; i++)
                Debug.Log(MovementStepTrace.Format(window[i]), this);
        }

        // ---- 服务器侧同款取证（审计 §3.3：记录服务器"最后收到/连续处理/最新发送 tick"与每步状态）----

        /// <summary>服务器权威模拟步证据（与客户端同结构，可按 clientTick 直接对齐比对）。</summary>
        private void CaptureServerStepEvidence(in MovementCommand cmd, string note)
        {
            if (_locomotor == null) return;
            var d = _locomotor.LastStepDebug;
            var (connId, objectId) = StepIdentity();
            _stepTrace.Record(new MovementStepSample
            {
                Server = true,
                ClientTick = cmd.Tick,
                Epoch = _inputEpoch,
                Conn = connId,
                ObjId = objectId,
                MoveInput = d.MoveInput,
                Sprint = d.Sprint,
                Jump = d.Jump,
                YawDelta = cmd.YawDelta,
                PitchDelta = cmd.PitchDelta,
                ProfileHash = d.ProfileHash,
                RootBefore = d.RootBefore,
                RootAfterMove = d.RootAfterMove,
                RootAfterSmooth = d.RootAfterMove,
                GroundedBranch = d.GroundedBranch,
                GroundedFromSnapshot = d.GroundedFromSnapshot,
                GroundedAfterMove = d.GroundedAfterMove,
                CollisionFlags = d.CollisionFlags,
                HorizontalVelocity = d.HorizontalVelocity,
                VerticalVelocity = d.VerticalVelocity,
                GroundSpeed = d.GroundSpeed,
                CoyoteTimer = d.CoyoteTimer,
                LandTimer = d.LandTimer,
                GaitPhase = d.GaitPhase,
                State = d.State,
                SprintIntent = d.SprintIntent,
                RecoilDebt = d.RecoilDebt,
                Note = note,
            });
        }

        /// <summary>服务器侧小窗口导出（节流与客户端一致；含队列与时间轴账本）。</summary>
        private void ExportServerStepTraceWindow(string reason)
        {
            if (Time.unscaledTime < _nextStepTraceLogTime) return;
            _nextStepTraceLogTime = Time.unscaledTime + MovementPredictionConfig.StepTraceLogIntervalSeconds;
            var window = _stepTrace.Recent(MovementPredictionConfig.StepTraceWindowRadius * 2 + 1);
            var (connId, objectId) = StepIdentity();
            Debug.LogWarning($"[MoveStepWindow] role=s reason={reason} epoch={_inputEpoch} "
                + $"conn={connId} obj={objectId} "
                + $"lastProcessed={_serverQueue.LastProcessedTick} recv={_serverQueue.ReceivedTotal} "
                + $"cons={_serverQueue.ConsumedTotal} emptyStreak={_serverQueue.ConsecutiveEmptyTicks} "
                + $"dropFrozen={_serverQueue.DroppedFrozen} qdepth={_serverQueue.Count} "
                + $"gaps={_serverQueue.SkippedTicks} stale={_serverQueue.DroppedStale} steps={window.Count}", this);
            for (int i = 0; i < window.Count; i++)
                Debug.Log(MovementStepTrace.Format(window[i]), this);
        }

        // ---- 2026-09-15 有限诊断汇总（审计 §5.2）----

        /// <summary>
        /// 每 DiagnosticsIntervalSeconds 输出一行 [MoveDiag]：把"移动有没有丢、纠偏有没有风暴、
        /// 本机有没有卡顿/失焦"从猜测变成可对账数字（口径见 MovementDiagnostics 注释）。
        /// 只做汇总输出，不参与任何判定。进程独立日志入口 = 启动客户端.cmd → Tools/Client/Start-LocalClient.ps1。
        /// </summary>
        private void EmitMovementDiagnostics(string role)
        {
            if (Time.unscaledTime < _nextDiagTime) return;
            _nextDiagTime = Time.unscaledTime + MovementPredictionConfig.DiagnosticsIntervalSeconds;
            _diag.NoteServerTotals(_serverQueue.ReceivedTotal, _serverQueue.ConsumedTotal);
            _diag.ServerSkippedTicks = _serverQueue.SkippedTicks;
            if (!_diag.HasData) return;
            // 审计 §3.3：输入时间轴积压必须显式告警——领先越大，服务器回滚窗口相对客户端画面越不可信
            int lead = _localTick >= _lastAckedClientTick
                ? (int)(_localTick - _lastAckedClientTick)
                : -(int)(_lastAckedClientTick - _localTick);
            if (lead > MovementPredictionConfig.ClientLeadWarnTicks && Time.unscaledTime >= _nextLeadWarnTime)
            {
                _nextLeadWarnTime = Time.unscaledTime + MovementPredictionConfig.DiagnosticsIntervalSeconds * 3f;
                Debug.LogWarning($"[Day3][Input] LEAD lead={lead} local={_localTick} ack={_lastAckedClientTick} "
                    + $"sent={_latestSentTick} qdepth={_serverQueue.Count} serverIdle={_lastIdleStepsAtSnapshot} "
                    + $"epoch={_inputEpoch} — 输入时间轴超前服务器 ACK（服务器命中的目标位姿/回滚窗口据此对账）", this);
            }
            int connId = NetworkObject != null && NetworkObject.Owner != null ? NetworkObject.Owner.ClientId : -1;
            int objectId = NetworkObject != null ? NetworkObject.ObjectId : 0;
            // 诊断绝不允许反过来影响玩法：TimeManager 经 NetworkObject 访问（未初始化时不该抛）
            var timeManager = NetworkObject != null ? TimeManager : null;
            _diag.InputEpoch = _inputEpoch;
            _diag.LatestSentTick = _latestSentTick;
            _diag.PitchDegrees = CurrentPitch();
            var context = new MovementDiagnosticsContext(
                role, CachedProcessId, connId, objectId,
                MatchLifecycle.ClientMatchId, Time.realtimeSinceStartupAsDouble,
                timeManager != null ? (int)timeManager.TickRate : 30,
                _localTick, _lastAckedClientTick, _lastServerTick,
                _serverQueue.Count, _accumulator.DroppedSteps,
                _serverQueue.DroppedStale, _serverQueue.DroppedBacklog, _serverQueue.DroppedFuture,
                _serverQueue.DroppedFrozen,
                timeManager != null ? timeManager.RoundTripTime : 0,
                !Application.isFocused, Application.isFocused, _visualOffsetMeters, _viewYawDegrees,
                _inputEpoch, _latestSentTick, _diag.PitchDegrees,
                _diag.AuthoritativePitchDegrees);
            Debug.Log(_diag.Format(context), this);
            _diag.ResetInterval();
        }

        // ---- 2026-09-16 实测：远端权威碰撞根的视觉平滑（方案A 的表现层半边）----

        private Vector3 _tpModelBaseLocalPosition;
        private Quaternion _tpModelBaseLocalRotation = Quaternion.identity;
        private Vector3 _remoteVisualWorldPosition;
        private Quaternion _remoteVisualWorldRotation = Quaternion.identity;
        /// <summary>作者基准是否已捕获（每网络会话一次；死亡/复活不重捕，防把倒地姿态存成基准）。</summary>
        private bool _tpModelBaseCaptured;
        /// <summary>视觉追随状态是否有效（死亡/瞬移级跳变时作废，下一帧直接对位）。</summary>
        private bool _remoteVisualValid;

        /// <summary>捕获 TP_Model 的作者局部位姿作为视觉追随基准（每会话一次；幂等）。</summary>
        private void EnsureRemoteVisualBaseline()
        {
            if (_tpModelBaseCaptured) return;
            // 2026-09-18 审计 §4.2 + 复核 R1：禁止用倒地/平滑瞬态捕获作者基准——出生即死、或"复活 RPC
            // 早于 dead=false 到达"时，本组件（DefaultExecutionOrder=-120）会先于 NetworkCombatAuthority
            // 的 Update 跑到这里，那一刻 IsDead 已经是 false 而 TP_Model 仍是"前倾 85° + 贴地抬升"，
            // 只看 IsDead 会把尸体位姿永久存成基准 → 之后每次复活都按它对位（= 永久性陷地）。
            // 正确的门禁是"死亡表现是否真的复位完成"。
            if (_combatAuthority != null
                && (_combatAuthority.IsDead || _combatAuthority.IsDeathVisualActive)) return;
            var model = transform.Find(VisualModelNode);
            if (model == null) return;
            _tpModelBaseLocalPosition = model.localPosition;
            _tpModelBaseLocalRotation = model.localRotation;
            _remoteVisualWorldPosition = model.position;
            _remoteVisualWorldRotation = model.rotation;
            _tpModelBaseCaptured = true;
            _remoteVisualValid = true;
        }

        /// <summary>测试接缝（2026-09-17 缓冲插值版）：&lt;0 用 Time.time；≥0 用该值作为"现在"
        /// （EditMode 无玩家循环，Time.time 不推进，无法驱动插值窗口）。产品路径不设置它。</summary>
        public float TestRemoteVisualTime { get; set; } = -1f;

        private readonly RemoteVisualInterpolationBuffer _remoteVisualBuffer = new();

        /// <summary>
        /// 远端视觉平滑（方案A 表现层，2026-09-17 缓冲插值版）：根在最新权威位姿上以到达节奏
        /// 阶梯移动，TP_Model 用快照缓冲插值（渲染时刻 = now − 自适应延迟）丝滑跟随——
        /// 取代旧指数追随（两段样本间先快后慢的脉冲感=实机"掉帧"主诉）。
        /// 写者唯一性：死亡期间本方法完全不碰 TP_Model（倒地/贴地表现是它的唯一写者）；
        /// 复活后 ResetDeathVisual 已把局部姿态还原到死亡前的值，这里从该世界位姿重新起算。
        /// </summary>
        private void UpdateRemoteVisualSmoothing()
        {
            EnsureRemoteVisualBaseline();
            if (!_tpModelBaseCaptured) return;
            if (_combatAuthority == null) _combatAuthority = GetComponent<NetworkCombatAuthority>();
            if (_combatAuthority != null && _combatAuthority.IsDead)
            {
                _remoteVisualValid = false; // 死亡：TP_Model 归死亡表现所有，本组件停写
                return;
            }

            float now = TestRemoteVisualTime >= 0f ? TestRemoteVisualTime : Time.time;
            // 追随目标 = 根位姿 ∘ 作者局部基准（保持 TP_Model 的 authored 偏移/朝向）
            Vector3 targetPosition = transform.TransformPoint(_tpModelBaseLocalPosition);
            Quaternion targetRotation = transform.rotation * _tpModelBaseLocalRotation;

            // 2026-09-18 实机问题6修复（侧/背射击不掉血根因）：Dedicated Server 上 TP_Model 是
            // BodyHitbox 的载体，必须与权威模拟根恒等——headless 无视觉平滑需求。缓冲插值会让
            // DS 侧 hitbox 滞后权威根 0.05~0.25s，LagComp 记录/回滚的正是这个漂移位姿
            //（横移目标侧向偏移可达 ~0.8m ≫ 胶囊半径 0.35m；正面因射线沿位移轴被胶囊纵深容忍
            // 而幸免，构成"正面中、侧背不中"的方向性）。Host（编辑器调试，同时渲染画面）与
            // 纯客户端保持插值平滑；NetworkObject 前置判空（EditMode 直驱安全）。
            var nobForServer = NetworkObject;
            if (nobForServer != null && nobForServer.IsServerInitialized && !nobForServer.IsClientInitialized)
            {
                PinTpModelToRootForServer();
                return;
            }

            // 基准刚建立/复活后首帧/瞬移级跳变（>5m）：直接对位并重建缓冲，不做穿场插值
            if (!_remoteVisualValid
                || (_remoteVisualBuffer.HasSamples
                    && (_remoteVisualBuffer.LatestPosition - targetPosition).sqrMagnitude > 25f))
            {
                _remoteVisualBuffer.Reset();
                _remoteVisualBuffer.Push(now, targetPosition, targetRotation,
                    MovementPredictionConfig.RemoteVisualInterpMinDelaySeconds,
                    MovementPredictionConfig.RemoteVisualInterpMaxDelaySeconds);
                _remoteVisualWorldPosition = targetPosition;
                _remoteVisualWorldRotation = targetRotation;
                _remoteVisualValid = true;
                var snapModel = transform.Find(VisualModelNode);
                if (snapModel != null) snapModel.SetPositionAndRotation(targetPosition, targetRotation);
                return;
            }

            // 只在根位姿变化时推入样本（NT 到达节奏 ≈ 服务器 tick）——渲染帧重复调用不得
            // 污染到达间隔 EMA（否则延迟被压到下限、插值退化为原地踏步）
            if (!_remoteVisualBuffer.HasSamples
                || (_remoteVisualBuffer.LatestPosition - targetPosition).sqrMagnitude > 1e-6f
                || Quaternion.Angle(_remoteVisualBuffer.LatestRotation, targetRotation) > 0.01f)
            {
                _remoteVisualBuffer.Push(now, targetPosition, targetRotation,
                    MovementPredictionConfig.RemoteVisualInterpMinDelaySeconds,
                    MovementPredictionConfig.RemoteVisualInterpMaxDelaySeconds);
            }

            if (_remoteVisualBuffer.Evaluate(now, out var evalPosition, out var evalRotation))
            {
                _remoteVisualWorldPosition = evalPosition;
                _remoteVisualWorldRotation = evalRotation;
                var model = transform.Find(VisualModelNode);
                if (model != null) model.SetPositionAndRotation(evalPosition, evalRotation);
            }
            _remoteVisualValid = true;
        }

        // ---- 远端插值自适应（观察者侧；方案A 关闭时的旧行为，A/B 对比用）----

        // ---- [RespawnTrace] 2026-09-18 实机问题1（重生陷地）临时取证：定位后可拆 ----
        private float _respawnTraceUntil = -1f;
        private float _nextRespawnTrace;

        /// <summary>远端视觉缓冲失效（幂等）：下一帧 UpdateRemoteVisualSmoothing 必走对位分支。
        /// 重生/边界事件的安全兜底——调用方不依赖本帧的执行顺序。</summary>
        internal void InvalidateRemoteVisualSmoothing()
        {
            _remoteVisualValid = false;
            _remoteVisualBuffer.Reset();
        }

        /// <summary>DS 钉根（2026-09-18 问题6；EditMode 可直驱的测试接缝）：TP_Model 直接写到
        /// 根∘作者基准，缓冲清空、追随作废——服务器侧 BodyHitbox（TP_Model 子物体）与权威根恒等，
        /// LagComp 记录/回滚的才是真实位姿。仅在 Dedicated（IsServerInitialized && !IsClientInitialized）调用。</summary>
        internal void PinTpModelToRootForServer()
        {
            EnsureRemoteVisualBaseline();
            if (!_tpModelBaseCaptured) return;
            Vector3 targetPosition = transform.TransformPoint(_tpModelBaseLocalPosition);
            Quaternion targetRotation = transform.rotation * _tpModelBaseLocalRotation;
            var serverModel = transform.Find(VisualModelNode);
            if (serverModel != null) serverModel.SetPositionAndRotation(targetPosition, targetRotation);
            _remoteVisualBuffer.Reset();
            _remoteVisualValid = false;
        }

        /// <summary>ObserversRespawned 到达（NCA 转发）：观察端缓冲立即失效（幂等兜底），
        /// 并开启 3s [RespawnTrace] 采样（每 0.25s 一行，服务器/观察端双侧定位陷地写者）。</summary>
        internal void HandleRespawnedFromNetwork()
        {
            InvalidateRemoteVisualSmoothing();
            _respawnTraceUntil = Time.unscaledTime + 3f;
            _nextRespawnTrace = 0f;
        }

        private void EmitRespawnTrace()
        {
            if (Time.unscaledTime >= _respawnTraceUntil) return;
            if (Time.unscaledTime < _nextRespawnTrace) return;
            _nextRespawnTrace = Time.unscaledTime + 0.25f;
            var model = transform.Find(VisualModelNode);
            var cc = GetComponent<CharacterController>();
            var animator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
            bool dead = _combatAuthority != null && _combatAuthority.IsDead;
            Debug.Log($"[RespawnTrace] t={Time.unscaledTime:F2} conn={(NetworkObject != null ? NetworkObject.OwnerId : -1)} " +
                $"obj={(NetworkObject != null ? NetworkObject.ObjectId : -1)} " +
                $"rootY={transform.position.y:F3} modelY={(model != null ? model.position.y : float.NaN):F3} " +
                $"modelLocalY={(model != null ? model.localPosition.y : float.NaN):F3} " +
                $"valid={_remoteVisualValid} hasSamples={_remoteVisualBuffer.HasSamples} " +
                $"latestY={(_remoteVisualBuffer.HasSamples ? _remoteVisualBuffer.LatestPosition.y : float.NaN):F3} " +
                $"deathVis={(_combatAuthority != null && _combatAuthority.DeathVisualActiveForTrace)} " +
                $"animEnabled={(animator != null && animator.enabled)} ccEnabled={(cc != null && cc.enabled)} " +
                $"ccGrounded={(cc != null && cc.enabled && cc.isGrounded)} dead={dead}", this);
        }

        /// <summary>按 RTT 均值/抖动（标准差）调整远端 NetworkTransform 插值缓冲，钳制在 [2,8] tick，
        /// 每秒最多调一次——高延迟玩家的波动不改变服务器全局节奏。</summary>
        private void UpdateRemoteInterpolation()
        {
            if (_networkTransform == null || TimeManager == null) return;
            if (Time.unscaledTime < _nextInterpolationAdjust) return;
            _nextInterpolationAdjust = Time.unscaledTime + 1f;

            long rtt = TimeManager.RoundTripTime;
            if (rtt <= 0) return;
            _rttIndex = (_rttIndex + 1) % _rttSamples.Length;
            _rttSamples[_rttIndex] = rtt;
            if (_rttFilled < _rttSamples.Length) _rttFilled++;

            double sum = 0;
            for (int i = 0; i < _rttFilled; i++) sum += _rttSamples[i];
            double mean = sum / _rttFilled;
            double variance = 0;
            for (int i = 0; i < _rttFilled; i++)
            {
                double d = _rttSamples[i] - mean;
                variance += d * d;
            }
            double jitter = Math.Sqrt(variance / _rttFilled);

            double tickMs = 1000.0 / Mathf.Max(1, (int)TimeManager.TickRate);
            int ticks = RemoteInterpolationMinTicks
                + (int)((jitter + mean * 0.2) / Math.Max(1.0, tickMs));
            ticks = Mathf.Clamp(ticks, RemoteInterpolationMinTicks, RemoteInterpolationMaxTicks);
            if (ticks != _currentInterpolationTicks)
            {
                _currentInterpolationTicks = ticks;
                _networkTransform.SetInterpolation((ushort)ticks);
            }
            ReportRemoteProxyLag();
        }

        // ---- M4（2026-09-16 审计）：远端碰撞代理时间基准诊断 ----

        private Vector3 _lastRemotePosition;
        private bool _remotePositionValid;
        private float _remoteObservedSpeed;
        private float _nextRemoteLagLogTime;

        /// <summary>
        /// 量化"远端插值位置 vs 服务器权威位置"的差距（M4）：远端根（含参与 Owner 预测碰撞的
        /// CharacterController）位于**插值/外推位置**，服务器模拟 Owner 时碰撞的是远端**权威位置**，
        /// 两者相差 ≈ 插值 tick 数 × 远端实测速度。仅在与本地 Owner 距离小于诊断半径时输出，
        /// 供用户日志对账——本方法不改变任何阻挡行为（策略选择见交付报告）。
        /// </summary>
        private void ReportRemoteProxyLag()
        {
            Vector3 position = transform.position;
            if (_remotePositionValid)
            {
                float dt = Mathf.Max(0.0001f, Time.unscaledDeltaTime);
                float instantaneous = Vector3.Distance(position, _lastRemotePosition) / dt;
                _remoteObservedSpeed = Mathf.Lerp(_remoteObservedSpeed, instantaneous, 0.25f);
            }
            _lastRemotePosition = position;
            _remotePositionValid = true;

            if (Time.unscaledTime < _nextRemoteLagLogTime) return;
            _nextRemoteLagLogTime = Time.unscaledTime + MovementPredictionConfig.DiagnosticsIntervalSeconds * 2f;
            float distance = LocalOwnerDistance(position);
            if (distance > MovementPredictionConfig.RemoteProxyDiagnosticsRadiusMeters) return;
            float tickSeconds = TimeManager != null ? 1f / Mathf.Max(1f, (int)TimeManager.TickRate) : 1f / 30f;
            float estimatedLagMeters = _currentInterpolationTicks * tickSeconds * _remoteObservedSpeed;
            Debug.Log($"[RemoteProxyLag] interpTicks={_currentInterpolationTicks} estLag={estimatedLagMeters:F3}m speed={_remoteObservedSpeed:F2}m/s distToLocal={distance:F2}m — 阻挡代理在插值位置，服务器用权威位置（差值≈estLag）", this);
        }

        /// <summary>本地 Owner 与给定世界点的距离（诊断用；找不到 Owner 返回 +∞ = 不输出）。</summary>
        private static float LocalOwnerDistance(Vector3 remotePosition)
        {
            var adapters = FindObjectsByType<PlayerNetworkAdapter>(FindObjectsSortMode.None);
            for (int i = 0; i < adapters.Length; i++)
            {
                var candidate = adapters[i];
                if (candidate == null || !candidate.IsOwner) continue;
                return Vector3.Distance(candidate.transform.position, remotePosition);
            }
            return float.MaxValue;
        }

        // ---- 冻结门与工具 ----

        /// <summary>移动/输入冻结（实例级）：倒计时期间（全局镜像）或本实例玩家已死亡。
        /// 服务器上远端玩家实例只看这两项（宿主本地菜单门控绝不影响远端模拟）。</summary>
        private bool MovementFrozen
            => MatchLifecycle.InputFrozen || (_combatAuthority != null && _combatAuthority.IsDead);

        /// <summary>本地 Owner 附加冻结（Phase A）：本地菜单/终局硬锁/死亡——菜单打开时
        /// 不采输入、不向服务器发送新玩家命令与战斗请求（Time.timeScale 恒为 1，不暂停网络）。</summary>
        private bool LocalMovementFrozen
            => MovementFrozen || Menu.GameplayInputGate.InputBlocked;

        /// <summary>服务器侧：把远端玩家俯仰增量应用到 localOnlyRoot（CameraPivot）。
        /// 公式与 FPMouseLook 本地写法逐字一致：**后坐债务先消费反向输入**，剩余部分才改基础俯仰，
        /// Euler X 取负、夹紧 ±89°。
        /// 审计 2026-09-16 §6.2：旧实现直接用原始 PitchDelta 积分——与 FPMouseLook 的两端 pitch
        /// 积分公式不等价（客户端消费债务、服务器不消费），连续压枪/死亡期间转头会永久偏离，
        /// 而位置/yaw 纠偏都不会修 pitch。</summary>
        private void ApplyRemotePitch(float pitchUpDelta)
        {
            if (Mathf.Approximately(pitchUpDelta, 0f)) return;
            if (_weaponController == null) _weaponController = GetComponentInParent<WeaponController>();
            float applied = pitchUpDelta;
            if (_weaponController != null)
                applied = _weaponController.ConsumeRecoilCompensation(new Vector2(pitchUpDelta, 0f)).x;
            if (Mathf.Approximately(applied, 0f)) return;
            _remotePitch = Mathf.Clamp(_remotePitch - applied, -89f, 89f);
            if (localOnlyRoot != null)
                localOnlyRoot.transform.localRotation = Quaternion.Euler(_remotePitch, 0f, 0f);
        }

        /// <summary>服务器权威基础俯仰（度；诊断/快照用）。</summary>
        public float RemotePitch => _remotePitch;

        // ---- 审计 2026-09-16 §2.1：Owner 重生边界的基础俯仰归零（Gameplay → Presentation 反射）----

        private static System.Type _fpMouseLookType;
        private static bool _fpMouseLookResolved;
        private static System.Reflection.MethodInfo _fpRespawnBaselineMethod;

        /// <summary>
        /// Owner 重生边界：把本地基础俯仰归零（与服务器重生快照 Pitch=0 对称）。
        /// 为什么必须：FPMouseLook._pitch 是本地私有积分状态，ServerResetAimPitch 只归零服务端
        /// _remotePitch；两端基线不同步会让 pitchGap 在重生后持续存在（位置/yaw 纠偏不修 pitch）。
        /// 只在权威重生边界调用一次——绝不逐帧回灌滞后服务器俯仰抢玩家鼠标。
        /// </summary>
        private void ApplyOwnerRespawnPitchBaseline()
        {
            if (!_fpMouseLookResolved)
            {
                _fpMouseLookResolved = true;
                _fpMouseLookType = System.Type.GetType("Game.Presentation.Camera.FPMouseLook, Game.Presentation");
                _fpRespawnBaselineMethod = _fpMouseLookType?.GetMethod("ApplyRespawnBaseline",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            }
            if (_fpRespawnBaselineMethod == null) return;
            var pivot = _viewPivot != null ? _viewPivot : (localOnlyRoot != null ? localOnlyRoot.transform : null);
            if (pivot == null) return;
            var mouseLook = pivot.GetComponent(_fpMouseLookType);
            if (mouseLook == null) return;
            _fpRespawnBaselineMethod.Invoke(mouseLook, null);
        }

        /// <summary>服务器侧重生边界：权威俯仰基线归零（审计 2026-09-16 §6.2）。
        /// 客户端俯仰仍由本地输入驱动（不抢玩家视角），此处只清服务器侧的积分残留。</summary>
        public void ServerResetAimPitch()
        {
            var netObject = NetworkObject;
            if (netObject == null || !netObject.IsSpawned || !netObject.IsServerInitialized) return;
            if (FishNetLifecycleGuard.IsLocalOwner(this)) return; // Host 本机俯仰由本地输入驱动，不抢视角
            _remotePitch = 0f;
            if (localOnlyRoot != null)
                localOnlyRoot.transform.localRotation = Quaternion.identity;
        }

        /// <summary>本端基础俯仰（Owner 读相机节点，服务器读权威值；快照携带，审计 §6.2）。
        /// 生命周期安全：网络未启动/对象未生成时不读 FishNet 所有权属性
        /// （authored/未生成对象上直接读 IsOwner 会 NRE，见 FishNetLifecycleGuard）。</summary>
        private float CurrentPitch()
        {
            var netObject = NetworkObject;
            bool serverSideRemote = netObject != null && netObject.IsSpawned && netObject.IsServerInitialized
                && !FishNetLifecycleGuard.IsLocalOwner(this);
            if (serverSideRemote) return _remotePitch;
            var pivot = _viewPivot != null ? _viewPivot : (localOnlyRoot != null ? localOnlyRoot.transform : null);
            if (pivot == null) return _remotePitch;
            float eulerX = pivot.localRotation.eulerAngles.x;
            return eulerX > 180f ? eulerX - 360f : eulerX;
        }

        private float _remotePitch;

        // ---- 工具 ----

        /// <summary>服务器侧去激活本地表现（2026-09-17）：独立方法供 OnStartServer 调用与
        /// EditMode 结构测试直调——只停表现（CameraPivot 整树 + ownerOnly 组件），不动任何权威逻辑。</summary>
        private void DeactivateLocalPresentationForServer()
        {
            SetActiveAll(localOnlyRoot, false);
            SetBehaviours(ownerOnlyComponents, false);
        }

        private static void SetActiveAll(GameObject go, bool active)
        {
            if (go != null) go.SetActive(active);
        }

        private static void SetBehaviours(Behaviour[] behaviours, bool enabled)
        {
            if (behaviours == null) return;
            foreach (var b in behaviours)
                if (b != null) b.enabled = enabled;
        }
    }
}
