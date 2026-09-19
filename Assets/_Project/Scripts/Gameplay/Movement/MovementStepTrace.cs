using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    /// <summary>
    /// 单个模拟步的完整取证样本（审计 2026-09-16 §3.3"第一阶段只加有限环形采样"）。
    /// 目的：直走复现时能按 **同一 clientTick** 对齐两端的输入、模拟状态与该步的物理分支结果，
    /// 直接指出"误差第一次出现的字段"，而不是靠平均 FPS/累计计数猜。
    /// 客户端与服务器共用同一结构（`Server` 区分角色）；字符串字段只存稳定引用（资产哈希），无分配。
    /// </summary>
    public struct MovementStepSample
    {
        /// <summary>true=服务器侧权威模拟步；false=客户端预测/重放步。</summary>
        public bool Server;
        public uint ClientTick;
        /// <summary>输入 epoch（启动/冻结/重生边界递增；见 PlayerNetworkAdapter.InputEpoch）。</summary>
        public int Epoch;
        /// <summary>网络身份（G1 审计 2026-09-17：服务器逐步/窗口行此前无 conn/obj，跨玩家取证
        /// 无法程序化对齐归属——上一轮审计曾把另一玩家的窗口误读为故障客户端证据；
        /// conn=-1 表示无网络/离线）。</summary>
        public int Conn;
        public int ObjId;
        public Vector2 MoveInput;
        public bool Sprint;
        public bool Jump;
        /// <summary>输入增量（G4 审计 2026-09-17）：[MoveStep] 行自此携带完整输入签名
        /// （yaw=向右/pitch=向上，度）——两端同 tick 输入对账不再需要独立哈希。</summary>
        public float YawDelta;
        public float PitchDelta;
        /// <summary>根运动 profile 版本哈希（两端不一致 = 模拟资产不一致）。</summary>
        public string ProfileHash;

        public Vector3 RootBefore;
        /// <summary>CC.Move 之后的根（不含平滑校正）。</summary>
        public Vector3 RootAfterMove;
        /// <summary>施加平滑校正之后的根。</summary>
        public Vector3 RootAfterSmooth;

        // ---- 物理分支证据（审计 §3.3：CC grounded 禁启前/后、Move 后 + collision flags）----
        /// <summary>本步分支输入使用的 grounded（重放首步=快照值，其余=CC 实际值）。</summary>
        public bool GroundedBranch;
        /// <summary>本步分支输入是否来自权威快照（重放首步）。</summary>
        public bool GroundedFromSnapshot;
        /// <summary>Move 后采样到的 CC.isGrounded。</summary>
        public bool GroundedAfterMove;
        /// <summary>Move 返回的碰撞标志。</summary>
        public CollisionFlags CollisionFlags;

        // ---- 模拟状态（与 MovementSnapshot 同源，便于两端逐字段比对）----
        public Vector3 HorizontalVelocity;
        public float VerticalVelocity;
        public float GroundSpeed;
        public float CoyoteTimer;
        public float LandTimer;
        public float GaitPhase;
        public LocomotionState State;
        public bool SprintIntent;
        public Vector2 RecoilDebt;

        /// <summary>本步施加的平滑校正（世界；未施加=零）。</summary>
        public Vector3 SmoothCorrection;
        /// <summary>本步是否发生硬重基。</summary>
        public bool SnapApplied;
        /// <summary>重基类型（-1=无；只在 SnapApplied=true 时读取；C#9 结构体不支持字段初始化器）。</summary>
        public int RebaseKind;
        public float ErrorMeters;
        public Vector3 SnapFrom;
        public Vector3 SnapTo;
        /// <summary>服务器侧附注（"EMPTY_STEP"/"EXTRAPOLATED" 等；客户端为空）。</summary>
        public string Note;
    }

    /// <summary>
    /// 模拟步取证环形缓冲（纯逻辑、可离线测试）：容量固定，稳态零分配。
    /// 与 MovementPullbackTrace 的分工：那个回答"哪个写者把位置写回去了"（异常归因），
    /// 这个回答"这一步两端的模拟状态/物理分支到底是什么"（首次分叉定位）。
    /// </summary>
    public sealed class MovementStepTrace
    {
        private readonly MovementStepSample[] _ring;
        private readonly List<MovementStepSample> _scratch = new();
        private int _next;
        private int _count;

        public MovementStepTrace(int capacity = MovementPredictionConfig.StepTraceCapacity)
        {
            _ring = new MovementStepSample[Mathf.Max(8, capacity)];
        }

        public int Count => _count;
        public int Capacity => _ring.Length;

        public void Record(in MovementStepSample sample)
        {
            _ring[_next] = sample;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        /// <summary>最近 max 条（最旧在前）。</summary>
        public List<MovementStepSample> Recent(int max)
        {
            _scratch.Clear();
            if (max <= 0 || _count == 0) return _scratch;
            int take = Mathf.Min(max, _count);
            int start = (_next - take + _ring.Length * 2) % _ring.Length;
            for (int i = 0; i < take; i++)
                _scratch.Add(_ring[(start + i) % _ring.Length]);
            return _scratch;
        }

        public void Clear()
        {
            _next = 0;
            _count = 0;
            _scratch.Clear();
        }

        /// <summary>单步一行取证输出（字段稳定，便于两端按 tick 对齐 grep）。</summary>
        public static string Format(in MovementStepSample s)
        {
            var sb = new StringBuilder(320);
            sb.Append("[MoveStep] role=").Append(s.Server ? "s" : "c")
              .Append(" tick=").Append(s.ClientTick.ToString(CultureInfo.InvariantCulture))
              .Append(" epoch=").Append(s.Epoch.ToString(CultureInfo.InvariantCulture))
              .Append(" conn=").Append(s.Conn.ToString(CultureInfo.InvariantCulture))
              .Append(" obj=").Append(s.ObjId.ToString(CultureInfo.InvariantCulture))
              .Append(" in=").Append(s.MoveInput.ToString("F2"))
              .Append(" sp=").Append(s.Sprint ? 1 : 0)
              .Append(" jp=").Append(s.Jump ? 1 : 0)
              .Append(" yw=").Append(s.YawDelta.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" pt=").Append(s.PitchDelta.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" g=").Append(s.GroundedBranch ? 1 : 0)
              .Append(s.GroundedFromSnapshot ? "S" : "-")
              .Append(s.GroundedAfterMove ? "1" : "0")
              .Append(" cflags=").Append(s.CollisionFlags)
              .Append(" root=").Append(s.RootBefore.ToString("F3"))
              .Append(" move=").Append(s.RootAfterMove.ToString("F3"))
              .Append(" smooth=").Append(s.SmoothCorrection.ToString("F3"))
              .Append(" vel=").Append(s.HorizontalVelocity.ToString("F3")).Append('/')
              .Append(s.VerticalVelocity.ToString("F3"))
              .Append(" gs=").Append(s.GroundSpeed.ToString("F3"))
              .Append(" coy=").Append(s.CoyoteTimer.ToString("F3"))
              .Append(" land=").Append(s.LandTimer.ToString("F3"))
              .Append(" gait=").Append(s.GaitPhase.ToString("F3"))
              .Append(" st=").Append(s.State)
              .Append(" si=").Append(s.SprintIntent ? 1 : 0)
              .Append(" rcl=").Append(s.RecoilDebt.ToString("F3"));
            if (s.SnapApplied)
                sb.Append(" snap=").Append(s.RebaseKind < 0 ? "?" : ((MovementRebaseKind)s.RebaseKind).ToString())
                  .Append(" err=").Append(s.ErrorMeters.ToString("F3"))
                  .Append(" from=").Append(s.SnapFrom.ToString("F3"))
                  .Append(" to=").Append(s.SnapTo.ToString("F3"));
            if (!string.IsNullOrEmpty(s.ProfileHash)) sb.Append(" hash=").Append(s.ProfileHash);
            if (!string.IsNullOrEmpty(s.Note)) sb.Append(" note=").Append(s.Note);
            return sb.ToString();
        }
    }
}
