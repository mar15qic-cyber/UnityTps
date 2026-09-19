using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    /// <summary>回拉归因（审计 2026-09-16 §M3：先确认"哪一个写者把已经向前的位置写回去了"）。</summary>
    public enum PullbackCause
    {
        /// <summary>本 tick 未检测到"前进却向后"。</summary>
        None = 0,
        /// <summary>Simulate（CC.Move）阶段就反向 → 碰撞/重叠恢复或模拟状态分叉（不是相机、不是纠偏）。</summary>
        MovePhase = 1,
        /// <summary>Simulate 前进，但平滑校正步进把它写回 → 软纠偏写者（审计 §3.2-4：与 Move 必须分开记）。</summary>
        SmoothPhase = 4,
        /// <summary>模拟根前进，但权威重基（快照对位+重放后的最终根）把它写回 → 权威纠偏。</summary>
        AuthorityCorrection = 2,
        /// <summary>模拟根前进，而**相邻两个渲染帧之间**视觉位置反向 → 视觉插值/缓冲重置路径（相机实现制造）。</summary>
        RenderPhase = 3,
    }

    /// <summary>
    /// 单条回拉取证样本（一个模拟 tick 一行；全部为世界坐标，除 MoveInput/ForwardDot 外）。
    /// 审计 2026-09-16 §3.2-4 修正后的口径：
    /// ① `MoveAfter` = **紧接** Simulate 后的根（不含平滑校正），`MoveAfterSmooth` = 施加平滑校正后；
    /// ② `SnapTo` = 重基**并重放完成后**的最终根（旧实现记的是对位瞬间的根）；
    /// ③ 视觉回拉比较**相邻渲染帧**的视觉世界位姿（`RenderPrev`→`RenderNow`），不再比较同帧
    ///    render−root（正常一个 tick 的插值天然落后根 → 旧口径必然误报 RenderPhase）。
    /// </summary>
    public struct PullbackTraceSample
    {
        public uint ClientTick;
        /// <summary>本帧共模拟了多少步（多步帧的最后一步携带帧级视觉位移）。</summary>
        public int StepsInFrame;
        /// <summary>本 tick 的移动输入（本地空间；y&gt;0=想前进）。</summary>
        public Vector2 MoveInput;
        /// <summary>Simulate 前的模拟根位置。</summary>
        public Vector3 MoveBefore;
        /// <summary>**仅** CC.Move/模拟 后的根位置（不含平滑校正）。</summary>
        public Vector3 MoveAfter;
        /// <summary>施加本步平滑校正后的根位置（无校正时 == MoveAfter）。</summary>
        public Vector3 MoveAfterSmooth;
        /// <summary>本 tick 施加的平滑校正（xyz，世界；未施加=零）。</summary>
        public Vector3 SmoothCorrection;
        /// <summary>本 tick 是否发生硬重基。</summary>
        public bool SnapApplied;
        public Vector3 SnapFrom;
        /// <summary>重基 + 重放完成后的最终根位置。</summary>
        public Vector3 SnapTo;
        /// <summary>帧末最终模拟根位置。</summary>
        public Vector3 RootAfterAll;
        /// <summary>上一渲染帧的视觉世界位姿（帧内非最后一步 = 本步 MoveAfterSmooth）。</summary>
        public Vector3 RenderPrev;
        /// <summary>本渲染帧的视觉世界位姿。</summary>
        public Vector3 RenderNow;
        /// <summary>本 tick 的水平前向（用于区分前后）。</summary>
        public Vector3 Forward;
        /// <summary>相邻渲染帧的视觉净位移投影（米）：&lt;0 = 画面可见地向后。</summary>
        public float ForwardDot;
        /// <summary>本步 Simulate 的净位移投影（米）。</summary>
        public float StepForwardDot;
        /// <summary>自 MoveBefore 到本帧渲染位置的净投影（米）。</summary>
        public float NetForwardDot;
    }

    /// <summary>
    /// 前进回拉取证环形缓冲（审计 2026-09-16 §M3）：只回答"哪个写者把已经向前的位置写回去了"，
    /// 不做统计口径的猜测。分批归因（优先级=写者链顺序）：
    /// ① `MoveAfter` 相对 `MoveBefore` 就反向 → `MovePhase`（碰撞/重叠恢复/模拟状态）；
    /// ② Simulate 前进但平滑校正步进反向 → `SmoothPhase`（软纠偏写者）；
    /// ③ 模拟根前进但重基+重放后的最终根反向 → `AuthorityCorrection`（权威纠偏）；
    /// ④ 相邻渲染帧的视觉位置反向 → `RenderPhase`（视觉插值/缓冲重置）。
    /// 纯逻辑、可离线测试；容量固定、稳态零分配。
    /// </summary>
    public sealed class MovementPullbackTrace
    {
        private readonly PullbackTraceSample[] _ring;
        private readonly List<PullbackTraceSample> _scratch = new();
        private int _next;
        private int _count;

        public MovementPullbackTrace(int capacity = MovementPredictionConfig.PullbackTraceCapacity)
        {
            _ring = new PullbackTraceSample[Mathf.Max(8, capacity)];
        }

        public int Count => _count;
        public int Capacity => _ring.Length;
        /// <summary>最近一次记录被判定为回拉的原因（诊断/测试）。</summary>
        public PullbackCause LastCause { get; private set; }

        public void Record(in PullbackTraceSample sample)
        {
            _ring[_next] = sample;
            _next = (_next + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            LastCause = Classify(sample);
        }

        /// <summary>
        /// 逐帧写入一整帧的模拟步样本（审计 §3.3"多步帧逐步写环形记录"）：
        /// 只有**最后一步**携带帧级视觉位移（RenderPrev→RenderNow），其余步的视觉字段取本步根位置
        /// （帧内视觉节点尚未更新，渲染帧之间的比较对它们不成立——避免旧口径的同帧 render−root 误报）。
        /// 返回本帧最严重的归因。
        /// </summary>
        public PullbackCause RecordFrame(List<PullbackTraceSample> steps, Vector3 renderPrev, Vector3 renderNow)
        {
            PullbackCause worst = PullbackCause.None;
            if (steps == null || steps.Count == 0)
            {
                // 无模拟步的帧：仍要能抓"纯视觉回拉"（重基把视觉节点写回但本帧没有模拟步）
                var idle = new PullbackTraceSample
                {
                    Forward = Vector3.forward,
                    RenderPrev = renderPrev,
                    RenderNow = renderNow,
                };
                idle.ForwardDot = Vector3.Dot(Flatten(idle.RenderNow - idle.RenderPrev), Vector3.forward);
                Record(idle);
                return LastCause;
            }

            for (int i = 0; i < steps.Count; i++)
            {
                var sample = steps[i];
                sample.StepsInFrame = steps.Count;
                bool last = i == steps.Count - 1;
                sample.RenderPrev = last ? renderPrev : sample.MoveAfterSmooth;
                sample.RenderNow = last ? renderNow : sample.MoveAfterSmooth;
                var forward = Flatten(sample.Forward);
                if (forward.sqrMagnitude > 1e-6f)
                {
                    forward.Normalize();
                    sample.ForwardDot = Vector3.Dot(Flatten(sample.RenderNow - sample.RenderPrev), forward);
                    sample.StepForwardDot = Vector3.Dot(Flatten(sample.MoveAfter - sample.MoveBefore), forward);
                    sample.NetForwardDot = Vector3.Dot(Flatten(sample.RenderNow - sample.MoveBefore), forward);
                }
                Record(sample);
                var cause = LastCause;
                if (cause != PullbackCause.None && (worst == PullbackCause.None || (int)cause < (int)worst))
                    worst = cause;
            }
            return worst;
        }

        /// <summary>最近 max 条（最旧在前）。</summary>
        public List<PullbackTraceSample> RecentSamples(int max)
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
            LastCause = PullbackCause.None;
            _scratch.Clear();
        }

        /// <summary>水平分量。</summary>
        private static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>
        /// 归因单个样本：输入想前进（MoveInput.y ≥ 阈值）且某写者造成净向后位移时给出原因，
        /// 否则 None。阈值见 MovementPredictionConfig（滤掉静止与噪声）。
        /// </summary>
        public static PullbackCause Classify(in PullbackTraceSample sample)
        {
            Vector3 forward = Flatten(sample.Forward);
            if (forward.sqrMagnitude < 1e-6f) return PullbackCause.None;
            forward.Normalize();
            if (sample.MoveInput.y < MovementPredictionConfig.PullbackMinForwardInput) return PullbackCause.None;

            float threshold = MovementPredictionConfig.PullbackMinBackwardMeters;
            float movePhase = Vector3.Dot(Flatten(sample.MoveAfter - sample.MoveBefore), forward);
            if (movePhase < -threshold) return PullbackCause.MovePhase;

            // 平滑写者单独放宽阈值（2026-09-16 实测）：平滑步进本身是亚厘米量级，与 Move/Authority
            // 共用 0.005 会把正常收敛也当成回拉（本局 4 条导出全是亚厘米噪声）
            float smoothPhase = Vector3.Dot(Flatten(sample.MoveAfterSmooth - sample.MoveAfter), forward);
            if (smoothPhase < -MovementPredictionConfig.PullbackSmoothMinMeters) return PullbackCause.SmoothPhase;

            if (sample.SnapApplied)
            {
                float snapPhase = Vector3.Dot(Flatten(sample.SnapTo - sample.SnapFrom), forward);
                if (snapPhase < -threshold) return PullbackCause.AuthorityCorrection;
            }

            // 审计 §3.2-4：视觉回拉必须比较**相邻渲染帧**的世界位置（同帧 render−root 天然落后一 tick）。
            float renderPhase = Vector3.Dot(Flatten(sample.RenderNow - sample.RenderPrev), forward);
            if (renderPhase < -threshold) return PullbackCause.RenderPhase;
            return PullbackCause.None;
        }

        /// <summary>单行取证输出（字段稳定，便于 grep/对账）。</summary>
        public static string Format(in PullbackTraceSample sample, PullbackCause cause)
        {
            var sb = new StringBuilder(256);
            sb.Append("[Pullback] cause=").Append(cause)
              .Append(" tick=").Append(sample.ClientTick.ToString(CultureInfo.InvariantCulture))
              .Append(" steps=").Append(sample.StepsInFrame.ToString(CultureInfo.InvariantCulture))
              .Append(" move=").Append(sample.MoveInput.ToString("F2"))
              .Append(" before=").Append(sample.MoveBefore.ToString("F3"))
              .Append(" after=").Append(sample.MoveAfter.ToString("F3"))
              .Append(" afterSmooth=").Append(sample.MoveAfterSmooth.ToString("F3"))
              .Append(" smooth=").Append(sample.SmoothCorrection.ToString("F3"))
              .Append(" snap=").Append(sample.SnapApplied ? 1 : 0)
              .Append(" snapFrom=").Append(sample.SnapFrom.ToString("F3"))
              .Append(" snapTo=").Append(sample.SnapTo.ToString("F3"))
              .Append(" root=").Append(sample.RootAfterAll.ToString("F3"))
              .Append(" renderPrev=").Append(sample.RenderPrev.ToString("F3"))
              .Append(" render=").Append(sample.RenderNow.ToString("F3"))
              .Append(" fwd=").Append(sample.Forward.ToString("F2"))
              .Append(" dot=").Append(sample.ForwardDot.ToString("F4")).Append('m')
              .Append(" stepDot=").Append(sample.StepForwardDot.ToString("F4")).Append('m')
              .Append(" netDot=").Append(sample.NetForwardDot.ToString("F4")).Append('m');
            return sb.ToString();
        }
    }
}
