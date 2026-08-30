using Game.Gameplay.Network;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// 远端玩家的 PlayerStateView 替身（Docs/19 N2）：
    /// 场景 prefab 的 PlayerStateView 读本地 Locomotor（Owner/服务器正确）；
    /// 远端化身挂本组件——同一组只读属性改由 NetworkLocomotionState 的 SyncVar 缓存供给，
    /// TPAnimDriver 的依赖注入不变（stateView 字段重指到此）。
    /// 由 PlayerNetworkAdapter 在远端实例上自动接线（无需手工配置）。
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public sealed class RemotePlayerStateView : MonoBehaviour
    {
        [SerializeField] private NetworkLocomotionState networkState;

        public Game.Gameplay.Movement.LocomotionState LocomotionState =>
            networkState != null ? networkState.State : Game.Gameplay.Movement.LocomotionState.Idle;
        public Vector2 MoveInput => networkState != null ? networkState.MoveInput : Vector2.zero;
        public float HorizontalSpeed => networkState != null ? networkState.HorizontalSpeed : 0f;
        public float GaitPhase => networkState != null ? networkState.GaitPhase : 0f;

        private void Awake()
        {
            if (networkState == null) networkState = GetComponentInParent<NetworkLocomotionState>();
        }
    }
}
