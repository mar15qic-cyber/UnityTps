using FishNet.Object;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Movement;
using UnityEngine;

namespace Game.Gameplay.Network
{
    /// <summary>
    /// 远端玩家的移动动画状态源（Docs/19 N2）：
    /// 服务器把每个玩家的 LocomotionState/MoveInput/GaitPhase 广播给观察者；
    /// 远端客户端上本组件替代 PlayerStateView 的移动数据源——TPAnimDriver 经
    /// RemotePlayerStateView 读这里。服务器/Owner 侧本组件静默（真实 Locomotor 是权威）。
    /// </summary>
    public sealed class NetworkLocomotionState : NetworkBehaviour
    {
        // ---- 服务器侧采集 → 广播（客户端侧回调更新本地缓存） ----

        private readonly SyncVar<LocomotionState> _state = new();
        private readonly SyncVar<Vector2> _moveInput = new();   // Serialize 由 FishNet 自动生成（Vector2 可序列化）
        private readonly SyncVar<float> _gaitPhase = new();

        private Locomotor _locomotor;

        private void Awake() => _locomotor = GetComponent<Locomotor>();

        private void Update()
        {
            if (IsServerInitialized && _locomotor != null)
            {
                // 服务器权威采集（Host 上每个玩家的 Locomotor 都在 Simulate）
                if (_state.Value != _locomotor.State) _state.Value = _locomotor.State;
                _moveInput.Value = _locomotor.MoveInput;
                _gaitPhase.Value = _locomotor.GaitPhase;
            }
        }

        /// <summary>远端读取接口（RemotePlayerStateView 消费）。服务器/Owner 读真实 Locomotor。</summary>
        public LocomotionState State => !IsOwner && IsClientInitialized
            ? _state.Value
            : _locomotor != null ? _locomotor.State : LocomotionState.Idle;
        public Vector2 MoveInput => !IsOwner && IsClientInitialized
            ? _moveInput.Value
            : _locomotor != null ? _locomotor.MoveInput : Vector2.zero;
        public float GaitPhase => !IsOwner && IsClientInitialized
            ? _gaitPhase.Value
            : _locomotor != null ? _locomotor.GaitPhase : 0f;
        public float HorizontalSpeed => !IsOwner && IsClientInitialized
            ? _moveInput.Value.magnitude * 3.44f // 近似步速（TP 动画只需走/跑分档，不需要精确值）
            : _locomotor != null ? _locomotor.HorizontalSpeed : 0f;
    }
}
