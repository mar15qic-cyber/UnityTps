using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP 动画的移动状态读取接口（审计 2026-09-16 D1）。实现者必须是"能明确切换本地/网络"的状态源：
    /// RemotePlayerStateView 用它把远端 SyncVar 状态喂给 TPAnimDriver，取代旧实现
    /// （PlayerNetworkAdapter 反射按类型注入——PlayerStateView 是 sealed 且 RemotePlayerStateView
    /// 不是其子类，类型检查恒 false → 远端动画永远读本地不模拟的 Locomotor）。
    /// </summary>
    public interface ITpLocomotionSource
    {
        LocomotionState LocomotionState { get; }
        Vector2 MoveInput { get; }
        float HorizontalSpeed { get; }
        float GaitPhase { get; }
    }

    /// <summary>
    /// 远端玩家的 PlayerStateView 替身（Docs/19 N2）：
    /// 场景 prefab 的 PlayerStateView 读本地 Locomotor（Owner/服务器正确）；
    /// 远端化身启用本组件——同一组只读属性改由 NetworkLocomotionState 供给（其 getter 自身已区分
    /// 本地/远端/离线三分支，故"远端替身启用就优先用它"在三种情况下都正确）。
    /// TPAnimDriver 在 Awake/OnEnable 显式选择本组件（审计 2026-09-16 D1：不再依赖反射注入）。
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public sealed class RemotePlayerStateView : MonoBehaviour, ITpLocomotionSource
    {
        [SerializeField] private NetworkLocomotionState networkState;

        public LocomotionState LocomotionState =>
            networkState != null ? networkState.State : LocomotionState.Idle;
        public Vector2 MoveInput => networkState != null ? networkState.MoveInput : Vector2.zero;
        public float HorizontalSpeed => networkState != null ? networkState.HorizontalSpeed : 0f;
        public float GaitPhase => networkState != null ? networkState.GaitPhase : 0f;

        private void Awake()
        {
            if (networkState == null) networkState = GetComponentInParent<NetworkLocomotionState>();
        }
    }
}
