using FishNet.Object;
using Game.Gameplay.Action;
using Game.Gameplay.Player;
using Game.Gameplay.Movement;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 玩家网络适配器（Docs/19 N1，client-hosted 架构）：
    /// 把现有单人 Player（全部 Gameplay 组件不动）接入 FishNet。
    /// 职责只有三件事——
    /// ① 权威分派：Host（服务器）模拟所有玩家移动；客户端只对自己发输入命令，不本地模拟；
    /// ② 本地/远端表现切换：Owner 启用 FP 相机栈/输入/TP 自隐藏，远端只留 TP 表现；
    /// ③ 移动状态广播：LocomotionState/GaitPhase 轻量同步（远端 TP 动画驱动输入）。
    /// 设计约束（Docs/04）：Host 即服务器（单人离线 = OfflineLocal 模式完全不变，
    /// 本组件在网络未启动时不做任何事）。
    /// </summary>
    [DefaultExecutionOrder(-120)] // 早于 PlayerStateView(-150 之后)读取？不——晚于 ActionSystem(-100) 之前的输入链
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
        private bool _initialized;

        private void Awake()
        {
            _locomotor = GetComponent<Locomotor>();
            _input = GetComponentInParent<InputReader>();
            _actions = GetComponentInParent<ActionSystem>();
            if (ownerOnlyComponents == null || ownerOnlyComponents.Length == 0)
                ownerOnlyComponents = new Behaviour[] { _input };
            // Docs/23 P1-5：死亡冻结判定需要，Awake 即解析（离线无此组件时保持 null，判定安全）
            if (_combatAuthority == null) _combatAuthority = GetComponent<NetworkCombatAuthority>();
            // Docs/23 表现迭代（切枪单一通路）：本地切枪目标统一来自 Arsenal 已解析意图
            _arsenal = GetComponentInParent<Arsenal>();
            if (_arsenal != null) _arsenal.OnSlotIntentResolved += HandleSlotIntentResolved;
        }

        private void OnDestroy()
        {
            if (_arsenal != null) _arsenal.OnSlotIntentResolved -= HandleSlotIntentResolved;
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
            // 网络启动：本地离线模拟关闭，改由权威侧驱动
#if UNITY_EDITOR
            Debug.Log($"[PlayerNetworkAdapter] net start: isServer={IsServerInitialized} ownerLocal={Owner.IsLocalClient}", this);
#endif
        }

        public override void OnStartClient()
        {
            bool isOwner = IsOwner;
            if (isOwner)
            {
                // Owner：本地输入+相机+HUD 全开；移动交权威模拟（Host 上即本地跑 Simulate）
                SetActiveAll(localOnlyRoot, true);
                SetBehaviours(ownerOnlyComponents, true);
                SetBehaviours(remoteOnlyComponents, false);
            }
            else
            {
                // 远端玩家的化身：只看得到 TP_Model（层 8 对其相机可见——本地玩家主相机
                // cullingMask 不含层 8，自隐藏已天然成立）；输入/相机/HUD 全关
                SetActiveAll(localOnlyRoot, false);
                SetBehaviours(ownerOnlyComponents, false);
                SetBehaviours(remoteOnlyComponents, true);
                WireRemotePresentation();
            }
        }

        /// <summary>远端表现接线（Docs/19 N2）：TPAnimDriver 的 stateView 重指
        /// RemotePlayerStateView（读 NetworkLocomotionState 的 SyncVar 缓存），
        /// 使远端 TP 动画由服务器广播的移动状态驱动。
        /// 反向依赖规避：Game.Gameplay 不能引用 Game.Presentation——经反射写字段
        /// （表现层组件类型按名查找，编译期无依赖）。</summary>
        private void WireRemotePresentation()
        {
            if (GetComponent<NetworkLocomotionState>() == null) return;
            MonoBehaviour remoteView = null, tpAnim = null;
            foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var n = mb.GetType().Name;
                if (n == "RemotePlayerStateView") remoteView = mb;
                else if (n == "TPAnimDriver") tpAnim = mb;
            }
            if (remoteView == null || tpAnim == null) return;
            var field = tpAnim.GetType().GetField("stateView",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null && field.FieldType.IsInstanceOfType(remoteView))
                field.SetValue(tpAnim, remoteView);
        }

        public override void OnStartServer()
        {
            // Host 即服务器：服务器侧为每个玩家跑 Locomotor.Simulate。
            // Owner 玩家的输入由本进程直接读（下方 Update 服务端分支）；
            // 远端玩家输入经 ServerRpc 送来（N3 接 Prediction 时替换为 FishNet 预测通道）。
        }

        private void Update()
        {
            if (!_initialized) return;

            if (IsServerInitialized)
            {
                // 服务器权威模拟：Host 上对自己（IsOwner）直接采输入；远端玩家输入经 _pendingCommand
                if (IsOwner && _input != null && _locomotor != null)
                {
                    // Docs/23 P1-5：倒计时冻结 / Host 自己死亡期间不模拟（命令不采、不发）
                    if (MovementFrozen) return;
                    // Docs/23 P0-4：俯仰增量随命令上行（系数与 yaw 同为 0.1f，抬头为正）
                    var cmd = new MovementCommand(
                        _input.Move, _input.Sprint, _input.JumpQueued,
                        _input.LookDelta.x * 0.1f, _input.LookDelta.y * 0.1f,
                        (uint)(Time.frameCount & 0x7FFFFFFF));
                    _locomotor.Simulate(cmd, Time.deltaTime);
                    if (cmd.Jump) _input.ConsumeJump();
                }
                else if (!IsOwner)
                {
                    // 远端玩家命令在服务器重放（默认 1x，无预测——M7 里程碑够用，N3 升级）
                    if (_hasCommand && _locomotor != null)
                    {
                        // Docs/23 P1-5：该远端玩家死亡/倒计时冻结期间命令丢弃（防解冻后爆发重放）
                        if (MovementFrozen)
                        {
                            _hasCommand = false;
                            return;
                        }
                        _locomotor.Simulate(_pendingCommand, Time.deltaTime);
                        // Docs/23 P0-4（G2a）：俯仰增量应用到服务器侧 CameraPivot（aimPivot 同一节点），
                        // 服务器 TryFire 的 AimDirection 随之恢复正确俯仰
                        ApplyRemotePitch(_pendingCommand.PitchDelta);
                        _hasCommand = false;
                    }
                }
            }
            else if (IsOwner && _locomotor != null)
            {
                // Docs/23 P1-5：死亡期/倒计时输入冻结（跳过 Simulate/上报/开火请求）
                if (MovementFrozen) return;
                // 纯客户端的 Owner：本地直接模拟 + 上报命令（当前演示用"客户端先跑、服务器校正"简式；
                // FishNet PredictedOwner/N3 阶段替换为标准预测）
                var cmd = new MovementCommand(
                    _input.Move, _input.Sprint, _input.JumpQueued,
                    _input.LookDelta.x * 0.1f, _input.LookDelta.y * 0.1f,
                    (uint)(Time.frameCount & 0x7FFFFFFF));
                _locomotor.Simulate(cmd, Time.deltaTime);
                if (cmd.Jump) _input.ConsumeJump();
                ServerSubmitCommand(cmd);

                // N3：开火意图转服务器权威结算（FireHeld 连发每帧请求；服务器 TryFire 自带闸）
                if (_weaponController == null) _weaponController = GetComponentInParent<WeaponController>();
                if (_combatAuthority == null) _combatAuthority = GetComponent<NetworkCombatAuthority>();
                if (_weaponController != null && _combatAuthority != null && _input.FireHeld)
                    _combatAuthority.SubmitFireRequest();
                // Docs/23 P0-1（G1）：换弹意图转发服务器验证（本地 WeaponController 照常跑预测，
                // 两端都执行是设计意图）。切枪不再在此转发——统一经 Arsenal.OnSlotIntentResolved。
                if (_combatAuthority != null && _input.ReloadPressed)
                    _combatAuthority.SubmitReloadRequest();
            }
        }

        private WeaponController _weaponController;
        private NetworkCombatAuthority _combatAuthority;

        /// <summary>移动/输入冻结（Docs/23 P1-5）：倒计时期间（全局镜像）或本实例玩家已死亡。</summary>
        private bool MovementFrozen
            => MatchLifecycle.InputFrozen || (_combatAuthority != null && _combatAuthority.IsDead);

        // ---- 远端玩家 → 服务器 输入通道 ----

        private MovementCommand _pendingCommand;
        private bool _hasCommand;
        private float _remotePitch;

        /// <summary>服务器侧：把远端玩家俯仰增量应用到 localOnlyRoot（CameraPivot）。
        /// 公式与 FPMouseLook 本地写法逐字一致：抬头为正增量 → Euler X 取负、夹紧 ±89°。
        /// 服务器侧该 GameObject 虽被禁用（非 Owner 端 localOnlyRoot=false），Transform 写入安全（既有先例）。</summary>
        private void ApplyRemotePitch(float pitchUpDelta)
        {
            if (Mathf.Approximately(pitchUpDelta, 0f)) return;
            _remotePitch = Mathf.Clamp(_remotePitch - pitchUpDelta, -89f, 89f);
            if (localOnlyRoot != null)
                localOnlyRoot.transform.localRotation = Quaternion.Euler(_remotePitch, 0f, 0f);
        }

        [ServerRpc(RequireOwnership = false, RunLocally = false)]
        private void ServerSubmitCommand(MovementCommand command)
        {
            _pendingCommand = command;
            _hasCommand = true;
        }

        // ---- 工具 ----

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
