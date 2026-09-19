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
        /// <summary>输入生命代际（F14，2026-09-19 审计）：Owner 以最新权威快照回传的服务器
        /// 生命代际盖章；服务器逐批校验——旧代际批次（死亡前在途、冻结门开后才到达）拒收
        /// 并清队，不得作为新生命输入消费。协议 v5→v6（wire 字段新增）。</summary>
        public uint LifeEpoch;

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
            LifeEpoch = 0u;
        }

        public MovementCommand(Vector2 move, bool sprint, bool jump, float yawDelta, float pitchDelta, uint tick, uint lifeEpoch)
        {
            Move = Vector2.ClampMagnitude(move, 1f);
            Sprint = sprint;
            Jump = jump;
            YawDelta = yawDelta;
            PitchDelta = pitchDelta;
            Tick = tick;
            LifeEpoch = lifeEpoch;
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

        // ---- 2026-09-16 审计 M3：补齐确定性重放所需状态 ----
        // 原实现让 ApplyAuthoritativeSnapshot 用"速度模长/枚举"推断这些量：root motion 相位下
        // 瞬时速度模长 ≠ _groundSpeed，空中/落地的 sprintIntent 也推断不出来，导致"相同输入 + 少量
        // 快照字段"不保证重放收敛（硬校正后下一 tick 仍分叉 → 来回纠偏=拽回）。
        /// <summary>地面判定（CharacterController.isGrounded 的模拟采样值）。</summary>
        public bool Grounded;
        /// <summary>地面速度标量（米/秒；RootMotion 相位下与 |HorizontalVelocity| 不等）。</summary>
        public float GroundSpeed;
        /// <summary>土狼时间剩余（秒）。</summary>
        public float CoyoteTimer;
        /// <summary>落地计时剩余（秒）。</summary>
        public float LandTimer;
        /// <summary>冲刺意图（空中保留语义依赖它）。</summary>
        public bool SprintIntent;
        /// <summary>武器后坐补偿债务（度；Simulate 每步消费它 → 重放必须从同一债务开始）。</summary>
        public Vector2 RecoilDebt;

        /// <summary>
        /// 基础俯仰（度；FPMouseLook/服务器侧约定：抬头为负 Euler X）。
        /// 2026-09-16 审计 §6.2：两端基础瞄准此前只活在各自的相机节点上，位置/yaw 纠偏不会修 pitch；
        /// 纳入快照后服务器权威俯仰可随状态下发对账（重生/重基时也有明确基线）。
        /// </summary>
        public float Pitch;
    }
}
