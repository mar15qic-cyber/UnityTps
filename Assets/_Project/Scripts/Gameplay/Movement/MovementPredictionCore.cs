using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    /// <summary>
    /// Day3 Phase 1 预测/校正参数全集（一处可调，测试锁语义）：
    /// 客户端 tick 表示模拟步（1/TickRate 秒），与渲染帧、与服务器 tick 均无一一对应关系。
    /// </summary>
    public static class MovementPredictionConfig
    {
        /// <summary>客户端单帧最大模拟步数：有限 catch-up，超出积压直接丢弃（禁无限追帧卡死）。</summary>
        public const int ClientMaxCatchUpSteps = 5;
        /// <summary>每个客户端模拟 tick 上行一次。旧 2-tick 批次使服务器交替停步/追两步，
        /// 虽然观察快照每 tick 发送，权威位置本身仍只有约 15Hz 的更新。</summary>
        public const int ClientBatchSendEveryTicks = 1;
        /// <summary>单批最多重传的未确认输入条数（丢包冗余上限）。</summary>
        public const int ClientBatchMaxCommands = 12;
        /// <summary>客户端预测历史容量（命令+预测快照环形缓冲）。</summary>
        public const int ClientHistoryCapacity = 128;
        /// <summary>服务器单玩家待处理输入队列上限；超出丢弃最旧并告警。</summary>
        public const int ServerMaxPendingCommands = 16;
        /// <summary>服务器单 tick 对单玩家最多消费的输入命令数（有限 catch-up）。</summary>
        public const int ServerMaxCatchUpPerTick = 3;
        /// <summary>超过 lastProcessed+该值的输入视为客户端 tick 失控，直接丢弃。</summary>
        public const int ServerMaxFutureTicks = 32;
        /// <summary>误差 ≥ 该米数 = 硬校正（快照对位 + 从确认 tick+1 重放）。</summary>
        public const float ReconcileSnapMeters = 0.75f;
        /// <summary>误差 ≥ 该米数才需要校正；更小的误差视为噪声忽略。</summary>
        public const float ReconcileSmoothMeters = 0.03f;
        /// <summary>平滑收敛时间常数（秒）：每模拟步按指数衰减消费校正偏移。</summary>
        public const float SmoothConvergeSeconds = 0.10f;

        // ---- 2026-09-13 快照域分叉容差（专项决策表；依据 Locomotor.Simulate 实际消费的状态）----

        /// <summary>水平朝向角差 ≥ 该度数 = yaw 分叉（TransformDirection 以根 yaw 变换本地移动向量，
        /// 分叉直接改变水平速度方向；相机 pitch 在 CameraPivot 节点、不进快照，不会被压视角）。</summary>
        public const float ReconcileYawDegrees = 4f;
        /// <summary>水平速度差 ≥ 该值 = 分叉（SimulateAir 以它为 MoveTowards 起点）。走速 1.58 m/s 的
        /// ~22%：容忍 CharacterController 碰撞解算的瞬态差异，持续超出才是真分叉。</summary>
        public const float ReconcileHorizontalSpeedMps = 0.35f;
        /// <summary>垂直速度差 ≥ 该值 = 分叉。跳跃初速 ≈6.96 m/s、重力 22 m/s²：客户端与服务器 1-2 tick
        /// 的落地相位差造成 ~0.7-1.5 m/s 瞬态差须被容忍，持续 ≥2.0 才是重力/落地分支真分叉。</summary>
        public const float ReconcileVerticalSpeedMps = 2.0f;
        /// <summary>速度/状态分叉的滞回确认快照数（30Hz 下 ≈100ms）：落地/起跳相位差典型 1-2 tick 瞬态，
        /// 连续 3 个快照仍分叉才升级硬校正，避免每快照回滚。</summary>
        public const int DivergenceConfirmTicks = 3;
        /// <summary>yaw 分叉的滞回确认快照数：yaw 无落地类瞬态来源（只在输入丢失时分叉），2 次即确认。</summary>
        public const int YawConfirmTicks = 2;

        // ---- 2026-09-15 渲染位置插值（Owner 表现层平滑；模拟根与服务器权威不动）----

        /// <summary>渲染位置插值开关：固定 tick 模拟在 120fps 渲染下呈阶梯位移（走速≈5cm/tick、
        /// 冲刺≈11cm/tick），开启后相机/第一人称表现按渲染帧在上一模拟 tick 与当前 tick 之间插值；
        /// 关闭=相机随模拟根逐 tick 阶梯移动（运行时可切换，用于 A/B 对比手感）。</summary>
        public static bool RenderPositionInterpolationEnabled { get; set; } = true;
        /// <summary>视觉偏移上限（米）：正常 ≤ 一个 tick 位移；超出说明是本机卡顿/瞬移残余，
        /// 直接清零并重新记基准（不做穿场插值）。</summary>
        public const float RenderVisualOffsetClampMeters = 0.6f;
        /// <summary>诊断汇总输出周期（秒）。</summary>
        public const float DiagnosticsIntervalSeconds = 2f;

        // ---- 2026-09-16 审计 C1/C3：渲染视角 yaw 与视觉残差 ----

        /// <summary>渲染视角 yaw 插值开关（C1）：水平转头按渲染帧平滑（权威身体 yaw 仍逐 tick）。
        /// 关闭=水平视角随身体逐 tick 转（30Hz 台阶，A/B 对比用）。</summary>
        public static bool ViewYawInterpolationEnabled { get; set; } = true;
        /// <summary>视角 yaw 偏移上限（度）：**只限制写入视觉节点的显示角度**（PlayerNetworkAdapter.
        /// ResolveViewYawForDisplay）。2026-09-16 实机修复：绝不可把本值钳制到累积量上——累积端被钳制
        /// 会产生永不衰减的粘滞残差（视角/准心永久偏离身体 yaw，服务器弹道沿身体 → 打不中）。</summary>
        public const float ViewYawClampDegrees = 10f;

        /// <summary>普通权威重基（纠偏）允许保留的视觉残差上限（米）：超过视为真传送/重生 → 直接清视觉历史。</summary>
        public const float RebaseVisualCarryMaxMeters = 2.5f;
        /// <summary>视觉残差衰减时间常数（秒）：纠偏时保留"纠偏前视觉世界位姿"再衰减到新轨迹（C3）。</summary>
        public const float VisualCarryDecaySeconds = 0.12f;
        /// <summary>残差视为归零的阈值（米）。</summary>
        public const float VisualCarryEpsilonMeters = 0.0005f;

        // ---- 2026-09-16 审计 M3：前进回拉取证 ----

        /// <summary>回拉取证环形缓冲容量（模拟 tick 条数）。</summary>
        public const int PullbackTraceCapacity = 96;
        /// <summary>回拉取证导出节流（秒）：检测到反向仍持续输出会淹没日志。</summary>
        public const float PullbackTraceLogIntervalSeconds = 2f;
        /// <summary>判定"前进却向后"的最小反向位移（米）：滤掉静止/噪声抖动。</summary>
        public const float PullbackMinBackwardMeters = 0.005f;
        /// <summary>平滑校正写者的取证阈值（米，2026-09-16 实测修正）：平滑步进本身是亚厘米量级，
        /// 与 Move/Authority 共用 0.005 会把正常收敛也当成回拉（本局 4 条导出全是噪声）。</summary>
        public const float PullbackSmoothMinMeters = 0.02f;
        /// <summary>判定"前进却向后"所需的输入前进分量下限（本地前向单位向量点积）。</summary>
        public const float PullbackMinForwardInput = 0.35f;

        // ---- 2026-09-16 审计 §3.3：同 tick 模拟状态环形采样（首次分叉定位）----

        /// <summary>模拟步取证环形容量（步）。</summary>
        public const int StepTraceCapacity = 320;
        /// <summary>导出窗口：异常步前后各取多少步（小窗口，避免逐帧刷日志）。</summary>
        public const int StepTraceWindowRadius = 8;
        /// <summary>模拟步取证导出节流（秒）。</summary>
        public const float StepTraceLogIntervalSeconds = 1.5f;

        /// <summary>客户端领先（local−ack）告警阈值（tick）：超过即说明输入时间轴积压，
        /// 服务器命中的目标位姿会比客户端画面旧这么多 tick（命中注册/回拉的可对账证据）。</summary>
        public const int ClientLeadWarnTicks = 20;

        // ---- 2026-09-16 审计 §6.2：基础俯仰一致性 ----

        /// <summary>两端基础俯仰偏差告警阈值（度）：超过即在诊断行输出（pitch 不参与位置/yaw 纠偏）。</summary>
        public const float AimPitchWarnDegrees = 3f;

        // ---- 2026-09-16 审计 M4：远端碰撞代理时间基准 ----

        /// <summary>远端代理滞后诊断半径（米）：仅当远端玩家在此距离内才输出 [RemoteProxyLag]。
        /// 不改变阻挡玩法（远端根仍在其插值位置），只提供"插值位置 vs 权威位置"的可对账量化。</summary>
        public const float RemoteProxyDiagnosticsRadiusMeters = 6f;

        // ---- 2026-09-16 实测：远端权威碰撞根（方案A，替代插值代理参与本地碰撞）----

        /// <summary>
        /// 远端权威碰撞根（2026-09-16 实测定案）：远端玩家的**根/CharacterController 停在最新权威
        /// 位姿**（NT 插值压到最小），视觉平滑改由 TP_Model 承担（指数追随根）。
        /// 为什么必须：本地 Owner 预测碰撞撞的是远端"插值代理"（滞后 2-8 tick + RTT），而服务器
        /// 模拟撞的是远端"权威"位姿——近距交火时同一 tick 两端碰撞结果不同，CC 去贯穿推挤产生
        /// **单步跳变**误差（实测 YYLL：36 次 SNAP、平滑带只命中 5 次、MovePhase 35 次；追击方
        /// 被反复拽回、被追方无感——角色不对称，与网络无关）。
        /// 开关保留旧行为（插值根 + RTT 自适应 2-8 tick）供 A/B 对比。
        /// </summary>
        public static bool RemoteAuthoritativeCollisionEnabled { get; set; } = true;
        /// <summary>远端视觉平滑时间常数（秒）：TP_Model 指数追随根，吸收 30Hz 权威台阶。
        /// 0.07s ≈ 旧 2-tick 插值（66ms）的观感；冲刺稳态视觉滞后 ≈ 0.07×3.44 ≈ 24cm。
        /// [2026-09-17 起仅供 A/B 旧路径使用] 现行远端视觉走缓冲插值（RemoteVisualInterpolationBuffer）。</summary>
        public const float RemoteVisualSmoothingSeconds = 0.07f;
        /// <summary>远端视觉缓冲插值的渲染延迟下限（秒）：自适应延迟的下钳制。</summary>
        public const float RemoteVisualInterpMinDelaySeconds = 0.05f;
        /// <summary>远端视觉缓冲插值的渲染延迟上限（秒）：到达间隔自适应延迟的上钳制
        /// （断流/低帧时不把视觉延迟无限放大）。</summary>
        public const float RemoteVisualInterpMaxDelaySeconds = 0.25f;
    }

    /// <summary>
    /// 客户端模拟步累积器：按固定步长（1/TickRate）推进模拟 tick，与渲染帧解耦。
    /// deltaTime 抖动被累积吸收；单次 Advance 超过 maxSteps 的积压被丢弃并计数（有限 catch-up，
    /// 禁止无限追帧卡死）。纯逻辑、可离线测试。
    /// </summary>
    public sealed class TickAccumulator
    {
        private double _remainder;

        /// <summary>因超出 catch-up 上限被丢弃的模拟步总数（诊断/测试）。</summary>
        public long DroppedSteps { get; private set; }

        /// <summary>未消费的时间余量（秒）：&lt; fixedDelta，可换算成本帧在两次模拟步之间的位置插值比例。</summary>
        public double Remainder => _remainder;

        /// <summary>推进一个渲染帧；返回本帧实际执行的模拟步数。</summary>
        public int Advance(double deltaTime, double fixedDelta, int maxSteps)
        {
            if (fixedDelta <= 0d) return 0;
            if (deltaTime < 0d) deltaTime = 0d;
            _remainder += deltaTime;

            int executed = 0;
            while (_remainder >= fixedDelta)
            {
                _remainder -= fixedDelta;
                if (executed >= maxSteps)
                {
                    DroppedSteps++; // 积压超出上限：丢弃（不追帧）
                    continue;
                }
                executed++;
            }
            return executed;
        }

        /// <summary>会话/重生边界：清空未消费的时间余量。</summary>
        public void Reset()
        {
            _remainder = 0d;
            DroppedSteps = 0;
        }
    }

    /// <summary>
    /// 客户端预测历史缓冲：输入命令 + 对应预测快照，按输入 tick 索引。
    /// 服务器确认（LastClientTick）之后的条目用于硬校正重放与丢包冗余重传；
    /// 重复 tick 拒绝存储（丢包/乱序/重复输入不会重复模拟的第一道防线）。
    /// </summary>
    public sealed class PredictionBuffer
    {
        private readonly int _capacity;
        private readonly Dictionary<uint, MovementCommand> _commands = new();
        private readonly Dictionary<uint, MovementSnapshot> _predicted = new();
        private readonly Queue<uint> _tickOrder = new();
        private readonly List<MovementCommand> _scratch = new();

        public uint OldestTick { get; private set; }
        public uint NewestTick { get; private set; }
        public bool HasCommands { get; private set; }
        public int Count => _commands.Count;

        public PredictionBuffer(int capacity = MovementPredictionConfig.ClientHistoryCapacity)
        {
            _capacity = Math.Max(8, capacity);
        }

        /// <summary>存入一条输入命令；同 tick 重复返回 false（不重复模拟）。</summary>
        public bool TryStore(in MovementCommand cmd)
        {
            if (_commands.ContainsKey(cmd.Tick)) return false;
            if (_commands.Count >= _capacity) EvictOldest();
            _commands[cmd.Tick] = cmd;
            _tickOrder.Enqueue(cmd.Tick);
            if (!HasCommands)
            {
                OldestTick = cmd.Tick;
                HasCommands = true;
            }
            NewestTick = cmd.Tick;
            return true;
        }

        public bool TryGetCommand(uint tick, out MovementCommand cmd) => _commands.TryGetValue(tick, out cmd);

        /// <summary>记录某 tick 的预测快照（重放后同 tick 覆盖）。</summary>
        public void RecordPredicted(in MovementSnapshot snapshot) => _predicted[snapshot.Tick] = snapshot;

        public bool TryGetPredicted(uint tick, out MovementSnapshot snapshot) => _predicted.TryGetValue(tick, out snapshot);

        /// <summary>原子读取确认 tick 的预测快照并剪枝。
        /// 校正流程必须先取样再删除确认快照，否则正常 ACK 会错误退化为硬校正。</summary>
        public bool TryGetPredictedAndPrune(uint ackedTick, out MovementSnapshot snapshot)
        {
            bool found = TryGetPredicted(ackedTick, out snapshot);
            PruneUpTo(ackedTick);
            return found;
        }

        /// <summary>清除 ≤ ackedTick 的历史（服务器已确认，不再参与重放/重传）。</summary>
        public void PruneUpTo(uint ackedTick)
        {
            if (!HasCommands) return;
            while (_tickOrder.Count > 0 && _tickOrder.Peek() <= ackedTick)
            {
                uint tick = _tickOrder.Dequeue();
                _commands.Remove(tick);
                _predicted.Remove(tick);
            }
            if (_tickOrder.Count > 0) OldestTick = _tickOrder.Peek();
            else
            {
                HasCommands = false;
                OldestTick = 0;
                NewestTick = 0;
            }
        }

        /// <summary>收集 > ackedTick 的未确认命令（最旧在前，最多 max 条；丢包冗余重传载荷）。
        /// 2026-09-16 审计 M1：必须"完整收集 → 排序 → 截断"（或按有序 tick 队列取最旧）。
        /// 旧实现按 Dictionary 枚举凑满 max 就 break、之后才排序——Dictionary 删除后重插入会复用空槽，
        /// 枚举顺序不再等于 tick 序，于是"最旧 max 条"被漏掉更旧的几条（实测反例：9..16 删除 9..12 后
        /// 再插入 17..24，旧实现给出 9,10,11,12,17..24，漏 13..16；正确应是 9..20）。
        /// 漏发会让服务器直接跳缺口 → 客户端把未模拟的输入当已确认（PruneUpTo(ACK)）。</summary>
        public List<MovementCommand> CollectUnacked(uint ackedTick, int max)
        {
            _scratch.Clear();
            if (!HasCommands || max <= 0) return _scratch;
            foreach (uint tick in _tickOrder)
            {
                if (tick <= ackedTick) continue;
                if (_commands.TryGetValue(tick, out var cmd)) _scratch.Add(cmd);
            }
            // 队列正常情况下已升序（TryStore 只接受新 tick）；排序+截断保证任何顺序异常下都不漏旧输入
            if (_scratch.Count > max)
            {
                _scratch.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                _scratch.RemoveRange(max, _scratch.Count - max);
            }
            return _scratch;
        }

        /// <summary>
        /// 作废 tick &gt; 指定值的**输入命令**（连同 tickOrder 重建与 NewestTick 回拨；
        /// 预测快照由 InvalidatePredictedAbove 处理）。
        /// 用途（审计 2026-09-17 R5）：硬重基重放因缺口中断时，缺口上方命令从未被重放——
        /// 保留它们会让 `_localTick = max(replayed, newest)` 声称"已模拟到 newest"而实际
        /// 只模拟到 replayed（逻辑 tick 与物理状态脱节、静默持续）。作废后 tick 基线可安全
        /// 回退到 replayed，后续 tick 重新生成（TryStore 不再撞旧命令）。
        /// </summary>
        public void InvalidateCommandsAbove(uint tick)
        {
            if (!HasCommands) return;
            uint newestRemaining = 0;
            bool any = false;
            var kept = new Queue<uint>(_tickOrder.Count);
            while (_tickOrder.Count > 0)
            {
                uint t = _tickOrder.Dequeue();
                if (t <= tick)
                {
                    kept.Enqueue(t);
                    newestRemaining = t;
                    any = true;
                }
                else
                {
                    _commands.Remove(t);
                }
            }
            while (kept.Count > 0) _tickOrder.Enqueue(kept.Dequeue());
            if (any) NewestTick = newestRemaining;
            else
            {
                HasCommands = false;
                OldestTick = 0;
                NewestTick = 0;
            }
        }

        /// <summary>清空全部历史（死亡/重生边界调用）。</summary>
        public void Clear()
        {
            _commands.Clear();
            _predicted.Clear();
            _tickOrder.Clear();
            _scratch.Clear();
            HasCommands = false;
            OldestTick = 0;
            NewestTick = 0;
        }

        /// <summary>
        /// 作废 tick &gt; 指定值之后的**预测快照**（保留输入命令以便重传）。
        /// 用途（2026-09-16 审计 M3）：硬重基时若命令缓冲存在缺口导致重放提前中断，则 (replayed, newest]
        /// 区间的模拟从未发生，其预测快照已陈旧——必须作废，否则后续 ACK 会拿陈旧快照做比较，
        /// 产生虚假误差与额外校正（记录/发送与模拟状态脱节）。
        /// </summary>
        public void InvalidatePredictedAbove(uint tick)
        {
            if (_predicted.Count == 0) return;
            var stale = new List<uint>();
            foreach (var pair in _predicted)
                if (pair.Key > tick) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) _predicted.Remove(stale[i]);
        }

        private void EvictOldest()
        {
            if (_tickOrder.Count == 0) return;
            uint oldest = _tickOrder.Dequeue();
            _commands.Remove(oldest);
            _predicted.Remove(oldest);
            if (_tickOrder.Count > 0) OldestTick = _tickOrder.Peek();
            else HasCommands = false;
        }
    }

    /// <summary>
    /// 服务器侧单玩家待处理输入队列（Day3 Phase 1）。
    /// 每个玩家实例独享一个队列：不同 RTT/丢包玩家互不阻塞，也不影响服务器全局 tick。
    /// 乱序（迟到旧 tick）、重复、tick 失控一律丢弃；积压超过上限丢弃最旧并计数告警——
    /// 每条命令至多被模拟一次。纯逻辑、可离线测试。
    /// </summary>
    public sealed class ServerInputQueue
    {
        private readonly Dictionary<uint, MovementCommand> _pending = new();
        private bool _hasProcessed;

        public uint LastProcessedTick { get; private set; }
        public int Count => _pending.Count;
        /// <summary>最后一次被消费的输入（服务器空队列时的有界外推源；Clear 后失效）。</summary>
        public MovementCommand LastConsumedCommand { get; private set; }
        /// <summary>是否已有可外推的最后输入。</summary>
        public bool HasConsumedCommand { get; private set; }
        /// <summary>自最后一次消费后的连续空 tick 数（外推深度；消费到输入即清零）。</summary>
        public int ConsecutiveEmptyTicks { get; private set; }
        /// <summary>累计入队命令数（诊断：服务器"收到"口径）。</summary>
        public long ReceivedTotal { get; private set; }
        /// <summary>累计消费（已模拟）命令数（诊断：服务器"消费"口径）。</summary>
        public long ConsumedTotal { get; private set; }
        /// <summary>累计因缺口被跳过的客户端 tick 数（LastProcessedTick 不连续处）。
        /// 语义（审计 2026-09-16 M1）：下行 ACK 表示"已处理到该 tick"，缺口即**跳过并结算**——
        /// 被跳过的输入永不被模拟，由客户端预测纠偏吸收；不得把它当成"已模拟"。
        /// 持续增长 = 上行批次漏发/排队/积压（不是传输丢包的证明）。</summary>
        public long SkippedTicks { get; private set; }
        /// <summary>最近一次 Drain 跳过的 tick 数（0=连续；服务器 tick 处理器据此节流告警）。</summary>
        public int LastDrainGap { get; private set; }
        /// <summary>重复/迟到（tick ≤ lastProcessed）丢弃计数。</summary>
        public long DroppedStale { get; private set; }
        /// <summary>超出队列上限被丢弃的最旧命令计数（告警）。</summary>
        public long DroppedBacklog { get; private set; }
        /// <summary>tick 超前失控被丢弃计数（恶意/bug 防护）。</summary>
        public long DroppedFuture { get; private set; }
        /// <summary>冻结窗口（倒计时/死亡）拒收的在途命令计数（审计 2026-09-17 D3——
        /// 死亡前"已发出未确认"的旧命令不得在重生后作为新 epoch 输入被消费）。</summary>
        public long DroppedFrozen { get; private set; }

        /// <summary>旧生命代际拒收的在途命令计数（F14，2026-09-19 审计——冻结门已开但仍
        /// 迟到抵达的旧代际批次；不得作为新生命输入消费）。</summary>
        public long DroppedStaleEpoch { get; private set; }

        /// <summary>冻结窗口拒收整批输入（调用方判定冻结；不入队不模拟，丢弃留痕）。</summary>
        public void NoteDroppedFrozen(int commands)
        {
            if (commands > 0) DroppedFrozen += commands;
        }

        /// <summary>旧生命代际拒收整批输入（F14：不入队不模拟，丢弃留痕）。</summary>
        public void NoteDroppedStaleEpoch(int commands)
        {
            if (commands > 0) DroppedStaleEpoch += commands;
        }

        /// <summary>入队（ServerRpc 批量载荷逐条调用；容忍乱序/重复/突发）。</summary>
        public void Enqueue(in MovementCommand cmd)
        {
            // 首包也必须受未来窗口约束；否则新连接可注入任意大 tick，绕过失控 tick 防护。
            if (!_hasProcessed && cmd.Tick > MovementPredictionConfig.ServerMaxFutureTicks)
            {
                DroppedFuture++;
                return;
            }
            if (_hasProcessed)
            {
                if (cmd.Tick <= LastProcessedTick)
                {
                    DroppedStale++;
                    return;
                }
                if ((long)cmd.Tick - LastProcessedTick > MovementPredictionConfig.ServerMaxFutureTicks)
                {
                    DroppedFuture++;
                    return;
                }
            }
            if (!_pending.TryAdd(cmd.Tick, cmd))
            {
                DroppedStale++; // 同批内重复 tick
                return;
            }
            ReceivedTotal++;
            if (_pending.Count > MovementPredictionConfig.ServerMaxPendingCommands)
            {
                uint oldest = MinPendingTick();
                _pending.Remove(oldest);
                DroppedBacklog++;
            }
        }

        /// <summary>
        /// 在一个服务器模拟 tick 消费至多 maxCatchUp 条命令（按 tick 升序）。
        /// 丢包造成的 tick 缺口：直接消费下一个可用命令并把 lastProcessed 跳到该 tick——
        /// 缺失的输入不会被重放（由客户端 reconcile 纠正漂移）。
        /// 返回消费条数；0 = 本 tick 无输入（调用方应模拟空命令保持权威推进）。
        /// </summary>
        public int Drain(int maxCatchUp, List<MovementCommand> output)
        {
            output.Clear();
            LastDrainGap = 0;
            while (output.Count < maxCatchUp && _pending.Count > 0)
            {
                uint next = MinPendingTick();
                // 审计 2026-09-16 M1：缺口不再静默——显式计数（被跳过的输入永不被模拟）
                if (_hasProcessed && next > LastProcessedTick + 1u)
                    LastDrainGap += (int)(next - LastProcessedTick - 1u);
                output.Add(_pending[next]);
                _pending.Remove(next);
                LastProcessedTick = next;
                _hasProcessed = true;
                ConsumedTotal++;
            }
            SkippedTicks += LastDrainGap;
            if (output.Count > 0)
            {
                // 审计 §3.2-2：记录最后已知输入，供空队列时的有界外推使用
                LastConsumedCommand = output[output.Count - 1];
                HasConsumedCommand = true;
                ConsecutiveEmptyTicks = 0;
            }
            else
            {
                ConsecutiveEmptyTicks++;
            }
            return output.Count;
        }

        /// <summary>死亡/冻结/会话重置：丢弃全部待处理输入（防解冻后爆发重放）。
        /// 审计 §3.2-2：最后已知输入一并作废——解冻后不得用冻结前的输入外推。</summary>
        public void Clear()
        {
            _pending.Clear();
            DroppedBacklog = 0;
            HasConsumedCommand = false;
            LastConsumedCommand = default;
            ConsecutiveEmptyTicks = 0;
        }

        private uint MinPendingTick()
        {
            uint min = uint.MaxValue;
            foreach (var key in _pending.Keys)
                if (key < min) min = key;
            return min;
        }
    }

    public enum ReconcileAction
    {
        /// <summary>误差在噪声阈值内：不动。</summary>
        None,
        /// <summary>小误差：平滑收敛（不回滚姿态，逐模拟步消费衰减校正偏移）。</summary>
        Smooth,
        /// <summary>大误差：硬校正（对位权威快照 + 从确认 tick+1 重放未确认输入）。</summary>
        Snap
    }
    /// <summary>2026-09-13 专项：位置之外的分叉域（只列实际影响 Simulate 后续积分的量）。</summary>
    public enum ReconcileDivergence
    {
        None = 0,
        /// <summary>水平朝向分叉（改变 TransformDirection 的速度方向）。</summary>
        Yaw = 1,
        /// <summary>水平速度分叉（SimulateAir 惯性起点）。</summary>
        HorizontalSpeed = 2,
        /// <summary>垂直速度分叉（重力积分/落地分支）。</summary>
        VerticalSpeed = 3,
        /// <summary>grounded/airborne 模拟分支分叉（SimulateGround vs SimulateAir 路径选择）。</summary>
        LocomotionState = 4,
    }

    /// <summary>单次快照配对的决策结果：位置动作 + 是否存在待滞回确认的软分叉。</summary>
    public struct ReconcileDecision
    {
        public ReconcileAction Action;
        public ReconcileDivergence Divergence;
        public bool SoftDivergence => Divergence != ReconcileDivergence.None;
    }

    /// <summary>LocomotionState → 模拟分支分组：只有跨组差异才改变积分路径
    /// （SimulateGround/SimulateAir 由 CC.isGrounded 选择；Idle/Walk/Sprint/Land 同在地面分支）。</summary>
    public static class LocomotionBranch
    {
        public static bool IsAirborne(LocomotionState state)
            => state == LocomotionState.Jump || state == LocomotionState.Air;
    }

    /// <summary>
    /// 平滑校正台账（纯逻辑，审计 2026-09-15 §4）：记录每个模拟步已施加的校正步进。
    /// ACK 配对（ackedTick）时，"已确认快照之后"施加的步进尚未进入配对快照——
    /// 若把新误差整体覆盖 remaining 会把这段步进重复施加（同段误差反复纠偏 → 视点振荡晃动）。
    /// 用法：模拟步内施加校正后 Record(tick, step)；Compare(ackedTick) 时先取
    /// ConsumeInFlightAfter(ackedTick) 从新误差中扣除，再写回 remaining。硬校正/死亡/重置整体作废。
    ///
    /// 审计 2026-09-15 §2 修正：扣减是"逐 ACK 增量"而非"一次性消费"——同一条在途步进会被
    /// 连续多张 ACK 快照（全部早于该步进）重复看见，因此**必须保留到 ACK 追过它为止**，只剪枝
    /// tick ≤ ackedTick（已进入配对快照、误差天然已扣除）的条目。旧实现求和后整体 Clear 会让
    /// 第二张 ACK 拿不到在途量、把同一段过时误差再施加一次（用户报告的"前进被拽回"）。
    /// </summary>
    public sealed class SmoothCorrectionLedger
    {
        /// <summary>容量上限（防 ACK 长时间不到时无限累积；超限整体作废并要求调用方硬重基）。</summary>
        public const int MaxEntries = 64;

        private readonly List<(uint tick, Vector3 step)> _entries = new();

        /// <summary>当前账目条数（诊断/测试）。</summary>
        public int Count => _entries.Count;

        /// <summary>溢出粘滞标记：置位表示台账曾被整体作废（在途量已丢失，平滑语义不可信），
        /// 必须由调用方执行硬重基（HardSnapTo）方可复位。仅 Invalidate 清除。</summary>
        public bool Overflowed { get; private set; }

        /// <summary>记录一个模拟步已施加的校正步进；超限整体作废并置 Overflowed（返回 false = 调用方必须硬重基）。</summary>
        public bool Record(uint tick, in Vector3 step)
        {
            if (_entries.Count >= MaxEntries)
            {
                Invalidate();
                Overflowed = true;
                return false;
            }
            _entries.Add((tick, step));
            return true;
        }

        /// <summary>ACK 配对：返回 tick &gt; ackedTick（已确认快照之后）施加的步进之和，并只剪枝
        /// tick ≤ ackedTick 的条目（这些已包含进配对快照，新误差里天然已扣除）。
        /// 在途条目（tick &gt; ackedTick）保留，供后续更老的 ACK 继续扣除——否则连续 ACK 会重复施加。</summary>
        public Vector3 ConsumeInFlightAfter(uint ackedTick)
        {
            Vector3 sum = Vector3.zero;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].tick > ackedTick) sum += _entries[i].step;
                else _entries.RemoveAt(i);
            }
            return sum;
        }

        /// <summary>硬校正/死亡/会话边界：全部步进被绝对对位覆盖，账目整体作废并复位溢出标记。</summary>
        public void Invalidate()
        {
            _entries.Clear();
            Overflowed = false;
        }
    }

    /// <summary>reconciliation 决策（纯函数，Day3 Phase 1）。</summary>
    public static class Reconciler
    {
        public static ReconcileAction Decide(Vector3 serverPosition, Vector3 predictedPosition, out Vector3 correction)
        {
            correction = serverPosition - predictedPosition;
            float error = correction.magnitude;
            if (error <= MovementPredictionConfig.ReconcileSmoothMeters) return ReconcileAction.None;
            if (error >= MovementPredictionConfig.ReconcileSnapMeters) return ReconcileAction.Snap;
            return ReconcileAction.Smooth;
        }

        /// <summary>
        /// 2026-09-13 专项：完整快照域决策。位置沿用既有 0.03/0.75 双阈值；
        /// 速度/朝向/移动分支的分叉以 SoftDivergence 报告，由 DivergenceGate 滞回确认后升级硬校正
        /// （速度错误不能只平滑位置——校正必须同时恢复影响后续积分的状态，重放是唯一完整恢复路径）。
        /// 比较对象必须是同一已确认 tick 的配对快照（时间对齐），不是客户端当前状态：
        /// 网络滞后造成的"服务器朝向旧于本地视角"不会进入本函数。
        /// </summary>
        public static ReconcileDecision Decide(in MovementSnapshot server, in MovementSnapshot predicted, out Vector3 correction)
            => Decide(server, predicted, Vector3.zero, out correction, out _);

        /// <summary>
        /// 2026-09-17 审计 D1（Critical）：双阈值判定必须在"扣除在途平滑量"之后的 corrected 误差上进行。
        /// raw 误差包含"配对快照之后已施加、尚未被任何快照计入"的在途校正步进（台账口径）——
        /// raw 贴近阈值时会把本应平滑（0.03–0.75 带）的误差误判硬对位；且旧判定序在 Snap 路径
        /// 跳过台账消费、HardSnapTo 又 Invalidate 台账，在途量每次硬对位后从零重积，放大效应可
        /// 反复成立——正是实测"平滑带从不命中（smooth=0）+ err 全部挤在 0.753–0.808 + 一旦开始
        /// 就停不下来"的风暴形态（2026-09-17 局 47 次 SNAP）。inFlight = 台账 ConsumeInFlightAfter(ack)。
        /// </summary>
        public static ReconcileDecision Decide(in MovementSnapshot server, in MovementSnapshot predicted,
            in Vector3 inFlightCorrection, out Vector3 rawCorrection, out Vector3 correctedCorrection)
        {
            rawCorrection = server.Position - predicted.Position;
            correctedCorrection = rawCorrection - inFlightCorrection;
            float correctedError = correctedCorrection.magnitude;
            var decision = new ReconcileDecision
            {
                Action = correctedError <= MovementPredictionConfig.ReconcileSmoothMeters ? ReconcileAction.None
                    : correctedError >= MovementPredictionConfig.ReconcileSnapMeters ? ReconcileAction.Snap
                    : ReconcileAction.Smooth,
                Divergence = ReconcileDivergence.None,
            };
            return CompleteDivergence(server, predicted, decision);
        }

        /// <summary>位置非 Snap 时评估速度/朝向/移动分支软分叉（两个快照域 Decide 重载共用）。</summary>
        private static ReconcileDecision CompleteDivergence(in MovementSnapshot server, in MovementSnapshot predicted,
            ReconcileDecision decision)
        {
            if (decision.Action == ReconcileAction.Snap) return decision; // 位置硬校正的重放已恢复全部状态

            float yawError = Mathf.Abs(Mathf.DeltaAngle(
                server.Rotation.eulerAngles.y, predicted.Rotation.eulerAngles.y));
            float horizontalError = Vector3
                .Distance(server.HorizontalVelocity, predicted.HorizontalVelocity);
            float verticalError = Mathf.Abs(server.VerticalVelocity - predicted.VerticalVelocity);
            bool branchDiverged = LocomotionBranch.IsAirborne(server.LocomotionState)
                != LocomotionBranch.IsAirborne(predicted.LocomotionState);

            if (yawError >= MovementPredictionConfig.ReconcileYawDegrees)
                decision.Divergence = ReconcileDivergence.Yaw;
            else if (branchDiverged)
                decision.Divergence = ReconcileDivergence.LocomotionState;
            else if (verticalError >= MovementPredictionConfig.ReconcileVerticalSpeedMps)
                decision.Divergence = ReconcileDivergence.VerticalSpeed;
            else if (horizontalError >= MovementPredictionConfig.ReconcileHorizontalSpeedMps)
                decision.Divergence = ReconcileDivergence.HorizontalSpeed;
            return decision;
        }

        /// <summary>平滑收敛单步：按 SmoothConvergeSeconds 时间常数指数衰减。</summary>
        public static Vector3 SmoothStep(Vector3 remaining, float fixedDelta)
        {
            if (fixedDelta <= 0f) return Vector3.zero;
            float fraction = 1f - Mathf.Exp(-fixedDelta / MovementPredictionConfig.SmoothConvergeSeconds);
            return remaining * fraction;
        }
    }

    /// <summary>
    /// 软分叉滞回闸（纯逻辑）：同域分叉连续出现达阈值才升级硬校正。
    /// 速度/移动分支用 DivergenceConfirmTicks（滤落地/起跳相位瞬态），yaw 用 YawConfirmTicks（无瞬态来源）；
    /// 任一域复位即清零计数（分叉交替说明输入/物理持续分叉，继续累计是正确语义）。
    /// </summary>
    public sealed class DivergenceGate
    {
        private int _consecutive;

        public bool Feed(in ReconcileDecision decision)
        {
            if (!decision.SoftDivergence)
            {
                _consecutive = 0;
                return false;
            }
            int required = decision.Divergence == ReconcileDivergence.Yaw
                ? MovementPredictionConfig.YawConfirmTicks
                : MovementPredictionConfig.DivergenceConfirmTicks;
            _consecutive++;
            if (_consecutive >= required)
            {
                _consecutive = 0;
                return true;
            }
            return false;
        }

        public void Reset() => _consecutive = 0;
    }

    /// <summary>
    /// 快照 ACK 门（纯逻辑，2026-09-13 专项核心修复）：
    /// 服务器每 tick 推快照而客户端按批量（每 2 模拟 tick）上传，LastClientTick 相等的重复快照是
    /// 常态路径——此前相等快照穿透乱序门后配对历史已被剪枝，误判"历史缺失"触发静默硬对位+重放
    /// （空闲/丢包/倒计时冻结期间每快照一次）。门语义：更旧或相等的 ACK 一律忽略（重复 ACK 期间
    /// 客户端与服务器输入源不同，无可配对 tick，等批次到达后配对决策）；LastClientTick==0 只做一次
    /// 初始对位；ACK 前进才进入配对比较。
    /// </summary>
    public sealed class ReconcileGate
    {
        private bool _initialSnapDone;

        public uint LastAckedClientTick { get; private set; }

        public ReconcileGateAction Feed(uint lastClientTick)
        {
            if (lastClientTick == 0)
            {
                if (_initialSnapDone) return ReconcileGateAction.IgnoreRepeat;
                _initialSnapDone = true;
                return ReconcileGateAction.InitialSnap;
            }
            if (lastClientTick <= LastAckedClientTick) return ReconcileGateAction.IgnoreRepeat;
            LastAckedClientTick = lastClientTick;
            return ReconcileGateAction.Compare;
        }

        /// <summary>死亡/重生/接管/重连边界：ACK 基线与初始对位标记随预测历史一并重建。</summary>
        public void Reset()
        {
            LastAckedClientTick = 0;
            _initialSnapDone = false;
        }

        /// <summary>死亡快照边界：同步 ACK 基线（服务器 LastProcessedTick 单调不减），不产生动作。</summary>
        public void Observe(uint lastClientTick)
        {
            if (lastClientTick > LastAckedClientTick) LastAckedClientTick = lastClientTick;
            _initialSnapDone = true;
        }
    }

    public enum ReconcileGateAction
    {
        /// <summary>重复/乱序 ACK 快照：忽略（仍可缓存 ServerTick）。</summary>
        IgnoreRepeat,
        /// <summary>首次权威快照（LastClientTick==0）：一次性初始对位。</summary>
        InitialSnap,
        /// <summary>ACK 前进：查配对预测快照做域决策。</summary>
        Compare,
    }
}
