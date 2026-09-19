using System.Globalization;
using System.Text;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    /// <summary>硬重基（权威对位）原因（审计 2026-09-16 M3：所有重基都要可计数——
    /// `snaps=0` 不再是"零对位"的证据）。</summary>
    public enum MovementRebaseKind
    {
        /// <summary>首次权威快照（LastClientTick==0）的一次性对位。</summary>
        Initial = 0,
        /// <summary>ACK 前进但无配对预测历史（容量淘汰/历史缺口）→ 保守对位。</summary>
        HistoryMissing = 1,
        /// <summary>位置误差 ≥ SNAP 阈值。</summary>
        Snap = 2,
        /// <summary>位置接近但速度/朝向/移动分支持续分叉（滞回确认后升级）。</summary>
        Divergence = 3,
        /// <summary>台账溢出（ACK 长期未推进）→ 平滑不可信，强制硬重基。</summary>
        Overflow = 4,
        /// <summary>死亡→重生边界。</summary>
        DeathRespawn = 5,
    }

    /// <summary>
    /// 移动/校正有限诊断汇总（审计 2026-09-15 §2 第 2 项；纯逻辑可离线测试）。
    /// 目的：把"移动到底有没有丢、纠偏到底有没有风暴"从猜测变成可对账的数字——
    /// 每 1-2 秒按角色（owner/server）输出一行，粘到任何一份进程日志后即可按 conn/时间对齐。
    /// 口径说明：
    /// ① gen = 本地生成的模拟 tick 数；sub = 上行批次/命令数；recv/cons = 服务器收到/消费命令数；
    ///    empty = 服务器无输入的空步 tick 数（2026-09-17 D2 起：保持位姿不模拟；丢包与本地调度不均都会推高它）；
    /// ② ack = 最后一次被服务器确认的客户端 tick；qdepth = 服务器待处理队列深度；
    /// ③ dropStale/Backlog/Future = 服务器侧丢弃口径（Stale 含冗余重传去重，**不是丢包证据**）；
    ///    dropSteps = 客户端累积器因追帧上限丢弃的模拟步；
    /// ④ dt = 本区间未缩放帧时长 min/avg/max 与"长帧"（> 2×固定步长）计数，用于识别本机卡顿；
    /// ⑤ bg/focus = runInBackground / 应用是否前台（双开时失焦会显著放大阶梯感）。
    /// </summary>
    public sealed class MovementDiagnostics
    {
        private long _frames;
        private double _dtSum = double.MaxValue;
        private double _dtMin;
        private double _dtMax;

        /// <summary>本区间本地生成的模拟 tick 数。</summary>
        public long GeneratedTicks { get; private set; }
        /// <summary>本区间上行批次总数。</summary>
        public long SubmitBatches { get; private set; }
        /// <summary>本区间上行命令总条数（含冗余重传）。</summary>
        public long SubmittedCommands { get; private set; }
        /// <summary>服务器累计收到（入队）的命令条数（会话单调，跨区间可比）。</summary>
        public long ServerReceived { get; private set; }
        /// <summary>服务器累计消费（已模拟）的命令条数（会话单调，跨区间可比）。</summary>
        public long ServerConsumed { get; private set; }
        /// <summary>本区间服务器空步（无输入，保持位姿不模拟）的 tick 数。</summary>
        public long ServerEmptySteps { get; private set; }
        /// <summary>会话累计服务器空步 tick 数（G2 审计 2026-09-17：empty= 是 2s 区间口径，
        /// 跨区间不可比——整局是否外推/空转无法从区间样本反推）。</summary>
        public long ServerEmptyStepsTotal { get; private set; }
        /// <summary>本区间硬校正（SNAP/分叉升级）次数。</summary>
        public long Snaps { get; private set; }
        /// <summary>本区间平滑校正激活次数。</summary>
        public long Smooths { get; private set; }
        /// <summary>会话累计平滑校正激活次数（G2 审计 2026-09-17：smooth= 是区间口径）。</summary>
        public long SmoothsTotal { get; private set; }
        /// <summary>本区间平滑校正累计施加位移（米，绝对值累计，非净位移）。</summary>
        public float AppliedMeters { get; private set; }
        /// <summary>当前待消费的平滑校正偏移量（米）。</summary>
        public float PendingMeters { get; set; }
        /// <summary>最近一次配对误差（米，corrected 口径=已扣在途平滑量；审计 2026-09-17 D1/G3）。
        /// 重基时由 NoteRebase(kind, errorMeters) 写入该次重基的 corrected 误差。</summary>
        public float LastErrorMeters { get; set; }
        /// <summary>最近一次配对误差的 raw 口径（米；未扣在途平滑量——与 err= 成对留证，
        /// 用于反推历史"贴近阈值"的误差是否本应落平滑带）。raw−err ≈ 在途平滑步进量。</summary>
        public float LastRawErrorMeters { get; set; }
        /// <summary>本区间长帧（帧时长 &gt; 2×固定步长）计数。</summary>
        public long LongFrames { get; private set; }

        // ---- 2026-09-16 审计 §3.3：时间轴/瞄准瞬时状态（注入值，跨区间保留）----

        /// <summary>输入 epoch（启动/冻结/重生/接管边界递增；旧输入与新基线不得混用）。</summary>
        public int InputEpoch { get; set; }
        /// <summary>最新一次上行的客户端 tick（0=还没发过）。</summary>
        public uint LatestSentTick { get; set; }
        /// <summary>本端基础俯仰（度）。</summary>
        public float PitchDegrees { get; set; }
        /// <summary>服务器权威基础俯仰（度；客户端侧由快照携带，NaN=未知）。</summary>
        public float AuthoritativePitchDegrees { get; set; } = float.NaN;

        // ---- 2026-09-16 审计 M1/M3：重基明细、缺口、回拉取证（会话单调累计）----

        /// <summary>硬重基总次数（含死亡/重生、历史缺失、首次对位——`snaps` 只说明有对位，不能说明没有）。</summary>
        public long RebaseTotal { get; private set; }
        public long RebaseInitial { get; private set; }
        public long RebaseHistoryMissing { get; private set; }
        public long RebaseSnap { get; private set; }
        public long RebaseDivergence { get; private set; }
        public long RebaseOverflow { get; private set; }
        public long RebaseDeathRespawn { get; private set; }
        /// <summary>服务器累计因缺口跳过的客户端 tick 数（由服务端队列绝对值注入）。</summary>
        public long ServerSkippedTicks { get; set; }
        /// <summary>客户端因重放缺口作废的预测 tick 数（模拟从未发生、快照已陈旧）。</summary>
        public long ReplayGapTicks { get; private set; }
        /// <summary>回拉取证计数（按归因；审计 M3 要求能区分是哪个写者写回去的）。</summary>
        public long PullbackMovePhase { get; private set; }
        public long PullbackAuthorityCorrection { get; private set; }
        public long PullbackRenderPhase { get; private set; }

        /// <summary>该区间是否已有任何数据（无数据时调用方跳过输出，避免刷屏）。</summary>
        public bool HasData => _frames > 0 || GeneratedTicks > 0 || SubmitBatches > 0
            || ServerReceived > 0 || ServerConsumed > 0;

        /// <summary>渲染帧采样（owner 侧每帧调用；server 侧也可用绝对时间为 0 调用）。</summary>
        public void NoteFrame(double unscaledDelta, double fixedDelta)
        {
            if (unscaledDelta <= 0d) return;
            _frames++;
            if (_dtSum > double.MaxValue / 2d) { _dtSum = 0d; _dtMin = unscaledDelta; _dtMax = unscaledDelta; }
            _dtSum += unscaledDelta;
            if (unscaledDelta < _dtMin) _dtMin = unscaledDelta;
            if (unscaledDelta > _dtMax) _dtMax = unscaledDelta;
            if (fixedDelta > 0d && unscaledDelta > fixedDelta * 2d) LongFrames++;
        }

        public void NoteGenerated(int steps)
        {
            if (steps > 0) GeneratedTicks += steps;
        }

        public void NoteSubmit(int commands)
        {
            SubmitBatches++;
            if (commands > 0) SubmittedCommands += commands;
        }

        public void NoteServerConsumed(int commands)
        {
            if (commands > 0) ServerConsumed += commands;
            else
            {
                ServerEmptySteps++;
                ServerEmptyStepsTotal++;
            }
        }

        /// <summary>服务器侧区间汇总：由队列累计值差值（本区间收到/消费）驱动。</summary>
        public void NoteServerTotals(long receivedTotal, long consumedTotal)
        {
            ServerReceived = receivedTotal;
            ServerConsumed = consumedTotal;
        }

        public void NoteSnap(float errorMeters)
        {
            Snaps++;
            LastErrorMeters = errorMeters;
        }

        /// <summary>记一次硬重基（审计 M3：所有重基来源都要计数，否则 `snaps=0` 会被误读为"零对位"）。</summary>
        public void NoteRebase(MovementRebaseKind kind, float errorMeters = 0f)
        {
            RebaseTotal++;
            switch (kind)
            {
                case MovementRebaseKind.Initial: RebaseInitial++; break;
                case MovementRebaseKind.HistoryMissing: RebaseHistoryMissing++; break;
                case MovementRebaseKind.Snap: RebaseSnap++; break;
                case MovementRebaseKind.Divergence: RebaseDivergence++; break;
                case MovementRebaseKind.Overflow: RebaseOverflow++; break;
                case MovementRebaseKind.DeathRespawn: RebaseDeathRespawn++; break;
            }
            NoteSnap(errorMeters);
        }

        /// <summary>重放因命令缺口提前中断：作废了 ticks 条预测快照（未模拟区间）。</summary>
        public void NoteReplayGap(int ticks)
        {
            if (ticks > 0) ReplayGapTicks += ticks;
        }

        /// <summary>回拉取证归因计数。</summary>
        public void NotePullback(PullbackCause cause)
        {
            switch (cause)
            {
                case PullbackCause.MovePhase: PullbackMovePhase++; break;
                case PullbackCause.AuthorityCorrection: PullbackAuthorityCorrection++; break;
                case PullbackCause.RenderPhase: PullbackRenderPhase++; break;
            }
        }

        public void NoteSmooth(float pendingMeters, float correctedErrorMeters, float rawErrorMeters)
        {
            Smooths++;
            SmoothsTotal++;
            PendingMeters = pendingMeters;
            LastErrorMeters = correctedErrorMeters;
            LastRawErrorMeters = rawErrorMeters;
        }

        public void NoteSmoothApplied(float meters) => AppliedMeters += meters;

        /// <summary>区间结束：清空区间内累计（保留 Pending/LastError 这类瞬时值）。</summary>
        public void ResetInterval()
        {
            _frames = 0;
            _dtSum = double.MaxValue;
            _dtMin = 0d;
            _dtMax = 0d;
            LongFrames = 0;
            GeneratedTicks = 0;
            SubmitBatches = 0;
            SubmittedCommands = 0;
            ServerReceived = 0;
            ServerConsumed = 0;
            ServerEmptySteps = 0;
            Snaps = 0;
            Smooths = 0;
            AppliedMeters = 0f;
        }

        /// <summary>区间帧时长统计（min/avg/max，毫秒）。</summary>
        public void GetFrameStats(out double minMs, out double avgMs, out double maxMs)
        {
            minMs = _frames > 0 ? _dtMin * 1000d : 0d;
            avgMs = _frames > 0 ? (_dtSum / _frames) * 1000d : 0d;
            maxMs = _frames > 0 ? _dtMax * 1000d : 0d;
        }

        /// <summary>
        /// 单行汇总（稳定字段顺序，便于 shell 侧按 key=value grep/切分）。
        /// </summary>
        public string Format(in MovementDiagnosticsContext context)
        {
            GetFrameStats(out double minMs, out double avgMs, out double maxMs);
            var sb = new StringBuilder(256);
            sb.Append("[MoveDiag] role=").Append(context.Role)
              .Append(" pid=").Append(context.ProcessId.ToString(CultureInfo.InvariantCulture))
              .Append(" conn=").Append(context.ConnectionId.ToString(CultureInfo.InvariantCulture))
              .Append(" obj=").Append(context.NetworkObjectId.ToString(CultureInfo.InvariantCulture))
              .Append(" match=").Append(string.IsNullOrEmpty(context.MatchId) ? "-" : context.MatchId)
              .Append(" t=").Append(context.MonotonicSeconds.ToString("F1", CultureInfo.InvariantCulture))
              .Append(" tickRate=").Append(context.TickRate.ToString(CultureInfo.InvariantCulture))
              .Append(" gen=").Append(GeneratedTicks.ToString(CultureInfo.InvariantCulture))
              .Append(" sub=").Append(SubmitBatches.ToString(CultureInfo.InvariantCulture))
              .Append('/').Append(SubmittedCommands.ToString(CultureInfo.InvariantCulture))
              .Append(" recv=").Append(ServerReceived.ToString(CultureInfo.InvariantCulture))
              .Append(" cons=").Append(ServerConsumed.ToString(CultureInfo.InvariantCulture))
              .Append(" empty=").Append(ServerEmptySteps.ToString(CultureInfo.InvariantCulture))
              .Append(" emptyTotal=").Append(ServerEmptyStepsTotal.ToString(CultureInfo.InvariantCulture))
              .Append(" local=").Append(context.LocalTick.ToString(CultureInfo.InvariantCulture))
              .Append(" ack=").Append(context.AckedTick.ToString(CultureInfo.InvariantCulture))
              .Append(" stTick=").Append(context.ServerTick.ToString(CultureInfo.InvariantCulture))
              .Append(" qdepth=").Append(context.QueueDepth.ToString(CultureInfo.InvariantCulture))
              .Append(" dropSteps=").Append(context.DroppedSteps.ToString(CultureInfo.InvariantCulture))
              .Append(" dropStale=").Append(context.DroppedStale.ToString(CultureInfo.InvariantCulture))
              .Append(" dropBacklog=").Append(context.DroppedBacklog.ToString(CultureInfo.InvariantCulture))
              .Append(" dropFuture=").Append(context.DroppedFuture.ToString(CultureInfo.InvariantCulture))
              .Append(" dropFrozen=").Append(context.DroppedFrozen.ToString(CultureInfo.InvariantCulture))
              .Append(" rtt=").Append(context.RoundTripMs.ToString(CultureInfo.InvariantCulture)).Append("ms")
              .Append(" dt=").Append(minMs.ToString("F1", CultureInfo.InvariantCulture)).Append('/')
              .Append(avgMs.ToString("F1", CultureInfo.InvariantCulture)).Append('/')
              .Append(maxMs.ToString("F1", CultureInfo.InvariantCulture))
              .Append(" long=").Append(LongFrames.ToString(CultureInfo.InvariantCulture))
              .Append(" bg=").Append(context.Background ? 1 : 0)
              .Append(" focus=").Append(context.Focused ? 1 : 0)
              .Append(" snaps=").Append(Snaps.ToString(CultureInfo.InvariantCulture))
              .Append(" rebase=").Append(RebaseTotal.ToString(CultureInfo.InvariantCulture))
              .Append('(').Append(RebaseInitial.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(RebaseHistoryMissing.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(RebaseSnap.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(RebaseDivergence.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(RebaseOverflow.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(RebaseDeathRespawn.ToString(CultureInfo.InvariantCulture)).Append(')')
              .Append(" gaps=").Append(ServerSkippedTicks.ToString(CultureInfo.InvariantCulture))
              .Append(" replayGap=").Append(ReplayGapTicks.ToString(CultureInfo.InvariantCulture))
              .Append(" pullback=").Append(PullbackMovePhase.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(PullbackAuthorityCorrection.ToString(CultureInfo.InvariantCulture)).Append('/')
              .Append(PullbackRenderPhase.ToString(CultureInfo.InvariantCulture))
              .Append(" smooth=").Append(Smooths.ToString(CultureInfo.InvariantCulture))
              .Append(" smoothTotal=").Append(SmoothsTotal.ToString(CultureInfo.InvariantCulture))
              .Append(" applied=").Append(AppliedMeters.ToString("F3", CultureInfo.InvariantCulture)).Append('m')
              .Append(" pend=").Append(PendingMeters.ToString("F3", CultureInfo.InvariantCulture)).Append('m')
              .Append(" err=").Append(LastErrorMeters.ToString("F3", CultureInfo.InvariantCulture)).Append('m')
              .Append(" errRaw=").Append(LastRawErrorMeters.ToString("F3", CultureInfo.InvariantCulture)).Append('m')
              .Append(" visOff=").Append(context.VisualOffsetMeters.ToString("F3", CultureInfo.InvariantCulture)).Append('m')
              .Append(" yawLead=").Append(context.ViewYawDegrees.ToString("F2", CultureInfo.InvariantCulture)).Append("deg")
              // 2026-09-16 审计 §3.3/§6.2：时间轴积压与两端基础瞄准的可对账字段
              .Append(" lead=").Append(context.LeadTicks.ToString(CultureInfo.InvariantCulture))
              .Append(" epoch=").Append(context.InputEpoch.ToString(CultureInfo.InvariantCulture))
              .Append(" sent=").Append(context.LatestSentTick.ToString(CultureInfo.InvariantCulture))
              .Append(" pitch=").Append(context.PitchDegrees.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" aPitch=").Append(float.IsNaN(context.AuthoritativePitchDegrees)
                  ? "n/a"
                  : context.AuthoritativePitchDegrees.ToString("F2", CultureInfo.InvariantCulture))
              .Append(" pitchGap=").Append(float.IsNaN(context.AuthoritativePitchDegrees)
                  ? "n/a"
                  : Mathf.Abs(Mathf.DeltaAngle(context.PitchDegrees, context.AuthoritativePitchDegrees))
                      .ToString("F2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// <summary>一行诊断的进程/会话上下文（每次输出时采样，避免诊断对象持有易失状态）。</summary>
    public readonly struct MovementDiagnosticsContext
    {
        public readonly string Role;
        public readonly int ProcessId;
        public readonly int ConnectionId;
        public readonly int NetworkObjectId;
        public readonly string MatchId;
        public readonly double MonotonicSeconds;
        public readonly int TickRate;
        public readonly uint LocalTick;
        public readonly uint AckedTick;
        public readonly long ServerTick;
        public readonly int QueueDepth;
        public readonly long DroppedSteps;
        public readonly long DroppedStale;
        public readonly long DroppedBacklog;
        public readonly long DroppedFuture;
        /// <summary>冻结窗口（倒计时/死亡）拒收的在途命令计数（审计 2026-09-17 D3）。</summary>
        public readonly long DroppedFrozen;
        public readonly long RoundTripMs;
        public readonly bool Background;
        public readonly bool Focused;
        public readonly float VisualOffsetMeters;
        /// <summary>渲染视角 yaw 偏移（度；C1：每帧视角相对权威身体 yaw 的超前量）。</summary>
        public readonly float ViewYawDegrees;
        /// <summary>客户端领先（local − ack，tick）：输入时间轴积压量（审计 §3.3）。</summary>
        public readonly int LeadTicks;
        public readonly int InputEpoch;
        public readonly uint LatestSentTick;
        public readonly float PitchDegrees;
        public readonly float AuthoritativePitchDegrees;

        public MovementDiagnosticsContext(string role, int processId, int connectionId, int networkObjectId,
            string matchId, double monotonicSeconds, int tickRate, uint localTick, uint ackedTick, long serverTick,
            int queueDepth, long droppedSteps, long droppedStale, long droppedBacklog, long droppedFuture,
            long droppedFrozen, long roundTripMs, bool background, bool focused, float visualOffsetMeters,
            float viewYawDegrees = 0f, int inputEpoch = 0, uint latestSentTick = 0,
            float pitchDegrees = 0f, float authoritativePitchDegrees = float.NaN)
        {
            Role = role;
            ProcessId = processId;
            ConnectionId = connectionId;
            NetworkObjectId = networkObjectId;
            MatchId = matchId;
            MonotonicSeconds = monotonicSeconds;
            TickRate = tickRate;
            LocalTick = localTick;
            AckedTick = ackedTick;
            ServerTick = serverTick;
            QueueDepth = queueDepth;
            DroppedSteps = droppedSteps;
            DroppedStale = droppedStale;
            DroppedBacklog = droppedBacklog;
            DroppedFuture = droppedFuture;
            DroppedFrozen = droppedFrozen;
            RoundTripMs = roundTripMs;
            Background = background;
            Focused = focused;
            VisualOffsetMeters = visualOffsetMeters;
            ViewYawDegrees = viewYawDegrees;
            InputEpoch = inputEpoch;
            LatestSentTick = latestSentTick;
            PitchDegrees = pitchDegrees;
            AuthoritativePitchDegrees = authoritativePitchDegrees;
            LeadTicks = localTick >= ackedTick ? (int)(localTick - ackedTick) : -(int)(ackedTick - localTick);
        }
    }
}
