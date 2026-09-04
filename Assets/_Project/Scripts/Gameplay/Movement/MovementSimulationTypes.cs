using System;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    public enum MovementSimulationMode
    {
        OfflineLocal,
        PredictedOwner,
        ServerAuthority,
        RemoteProxy
    }

    [Serializable]
    public struct MovementCommand
    {
        public Vector2 Move;
        public bool Sprint;
        public bool Jump;
        public float YawDelta;
        /// <summary>本帧俯仰增量（抬头为正，灵敏度系数与 yaw 同为 0.1f——Docs/23 P0-4 G2）。
        /// Locomotor 不消费此字段：俯仰由相机层（FPMouseLook/服务器侧 pivot 重放）消费。</summary>
        public float PitchDelta;
        public uint Tick;

        /// <summary>兼容构造器（Locomotor 离线路径等既有调用点零改动）：PitchDelta 默认 0。</summary>
        public MovementCommand(Vector2 move, bool sprint, bool jump, float yawDelta, uint tick)
            : this(move, sprint, jump, yawDelta, 0f, tick) { }

        public MovementCommand(Vector2 move, bool sprint, bool jump, float yawDelta, float pitchDelta, uint tick)
        {
            Move = Vector2.ClampMagnitude(move, 1f);
            Sprint = sprint;
            Jump = jump;
            YawDelta = yawDelta;
            PitchDelta = pitchDelta;
            Tick = tick;
        }
    }

    [Serializable]
    public struct MovementSnapshot
    {
        public uint Tick;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 HorizontalVelocity;
        public float VerticalVelocity;
        public LocomotionState LocomotionState;
        public float GaitPhase;
    }
}
