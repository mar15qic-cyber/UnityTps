using System;

namespace Game.Gameplay.Movement
{
    /// <summary>
    /// 服务器权威移动状态（Day3 Phase 1）：服务器每个模拟 tick 经 TargetRpc 推送给 Owner。
    /// Owner 端据此做 reconciliation：小误差平滑收敛、大误差硬校正后重放未确认输入。
    /// 字段为 FishNet RPC 载荷，保持值类型 + 基础类型（MovementSnapshot 已有既有序列化路径）。
    /// </summary>
    [Serializable]
    public struct AuthoritativeMovementState
    {
        /// <summary>服务器模拟 tick（TimeManager.Tick，与客户端 tick 无一一对应关系）。</summary>
        public uint ServerTick;
        /// <summary>服务器已消费的最新客户端输入 tick（0=尚未消费任何输入）。</summary>
        public uint LastClientTick;
        /// <summary>
        /// 本快照时刻服务器已连续"无真实输入"推进的 tick 数（审计 2026-09-16 §3.2-2）：
        /// 服务器每 tick 必须模拟（重力/计时），队列空时用最后已知输入外推或空命令。
        /// &gt;0 表示 Snapshot 比 LastClientTick 多推进了这么多步——客户端据此判断误差是否来自
        /// 输入延迟（外推差异）而不是模拟分叉，避免把延迟误判成"拽回"而反复硬对位。
        /// </summary>
        public int IdleStepsAtSnapshot;
        /// <summary>玩家在服务器上已死亡：接收端必须清空预测历史并硬对位。</summary>
        public bool Dead;
        /// <summary>服务器当前生命代际（F14，2026-09-19 审计）：Owner 上行输入以此盖章，
        /// 服务器按当前代际校验——旧代际在途批次拒收，不得作为新生命输入消费。</summary>
        public uint LifeEpoch;
        /// <summary>服务器当前权威姿态（位置/旋转/速度/移动状态/步态）。</summary>
        public MovementSnapshot Snapshot;
    }
}
