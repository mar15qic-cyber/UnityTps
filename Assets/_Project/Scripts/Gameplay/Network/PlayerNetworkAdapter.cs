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
        private bool _initialized;

        private void Awake()
        {
            _locomotor = GetComponent<Locomotor>();
            _input = GetComponentInParent<InputReader>();
            _actions = GetComponentInParent<ActionSystem>();
            if (ownerOnlyComponents == null || ownerOnlyComponents.Length == 0)
                ownerOnlyComponents = new Behaviour[] { _input };
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
                    var cmd = new MovementCommand(
                        _input.Move, _input.Sprint, _input.JumpQueued,
                        _input.LookDelta.x * 0.1f, (uint)(Time.frameCount & 0x7FFFFFFF));
                    _locomotor.Simulate(cmd, Time.deltaTime);
                    if (cmd.Jump) _input.ConsumeJump();
                }
                else if (!IsOwner)
                {
                    // 远端玩家命令在服务器重放（默认 1x，无预测——M7 里程碑够用，N3 升级）
                    if (_hasCommand && _locomotor != null)
                    {
                        _locomotor.Simulate(_pendingCommand, Time.deltaTime);
                        _hasCommand = false;
                    }
                }
            }
            else if (IsOwner && _locomotor != null)
            {
                // 纯客户端的 Owner：本地直接模拟 + 上报命令（当前演示用"客户端先跑、服务器校正"简式；
                // FishNet PredictedOwner/N3 阶段替换为标准预测）
                var cmd = new MovementCommand(
                    _input.Move, _input.Sprint, _input.JumpQueued,
                    _input.LookDelta.x * 0.1f, (uint)(Time.frameCount & 0x7FFFFFFF));
                _locomotor.Simulate(cmd, Time.deltaTime);
                if (cmd.Jump) _input.ConsumeJump();
                ServerSubmitCommand(cmd);
            }
        }

        // ---- 远端玩家 → 服务器 输入通道 ----

        private MovementCommand _pendingCommand;
        private bool _hasCommand;

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
