using System.Collections.Generic;
using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>Day3 Phase 1 定向测试：tick 累积器（波动/有限 catch-up）、输入队列（丢包/乱序/重复/
    /// 独立不阻塞）、预测缓冲（非一一对应 tick 的确认与重放）、reconciliation（小误差平滑/大误差硬校正/
    /// 死亡清历史）。</summary>
    public sealed class MovementPredictionCoreTests
    {
        private const double FixedDelta = 1.0 / 30.0;

        private static MovementCommand Cmd(uint tick, float x = 0f, float y = 0f, bool jump = false)
        {
            return new MovementCommand(new Vector2(x, y), false, jump, 0f, 0f, tick);
        }

        // ---- TickAccumulator：模拟步与渲染帧解耦 ----

        [Test]
        public void Accumulator_ExactStepsForUniformFrames()
        {
            var acc = new TickAccumulator();
            // 60fps 渲染 × 30Hz 模拟：每 2 帧出 1 步
            int total = 0;
            for (int i = 0; i < 60; i++) total += acc.Advance(1.0 / 60.0, FixedDelta, 5);
            Assert.That(total, Is.EqualTo(30));
            Assert.That(acc.DroppedSteps, Is.EqualTo(0));
        }

        [Test]
        public void Accumulator_JitteredIntervals_StillTrackWallClock()
        {
            var acc = new TickAccumulator();
            // 帧间隔波动（8ms/50ms 交替）：无论抖动如何，总模拟步数只由总时长决定
            int total = 0;
            double totalDelta = 0;
            for (int i = 0; i < 625; i++)
            {
                double dt = i % 2 == 0 ? 0.008 : 0.050;
                totalDelta += dt;
                total += acc.Advance(dt, FixedDelta, 5);
            }
            double expected = totalDelta / FixedDelta; // ≈543.75
            Assert.That(total, Is.EqualTo(expected).Within(2));
            Assert.That(acc.DroppedSteps, Is.EqualTo(0), "正常抖动不应触发丢弃");
        }

        [Test]
        public void Accumulator_StallBacklog_DropsBeyondCatchUpCap()
        {
            var acc = new TickAccumulator();
            // 卡顿 1 秒积压 30 步，单帧上限 5：执行 5、丢弃 25（禁无限追帧）
            int executed = acc.Advance(1.0, FixedDelta, MovementPredictionConfig.ClientMaxCatchUpSteps);
            Assert.That(executed, Is.EqualTo(MovementPredictionConfig.ClientMaxCatchUpSteps));
            Assert.That(acc.DroppedSteps, Is.EqualTo(25));
            // 积压不残留
            Assert.That(acc.Advance(0.0, FixedDelta, 5), Is.EqualTo(0));
        }

        // ---- PredictionBuffer：重复拒绝 / 确认剪枝 / 未确认重传 ----

        [Test]
        public void Buffer_DuplicateTickRejected()
        {
            var buffer = new PredictionBuffer();
            Assert.That(buffer.TryStore(Cmd(1)), Is.True);
            Assert.That(buffer.TryStore(Cmd(1, x: 1f)), Is.False, "同 tick 重复存储被拒绝（不重复模拟）");
            buffer.TryGetCommand(1, out var kept);
            Assert.That(kept.Move.x, Is.EqualTo(0f), "保留首次存储的命令");
        }

        [Test]
        public void Buffer_AckPrune_AndUnackedCollection_OrderByTick()
        {
            var buffer = new PredictionBuffer();
            for (uint t = 1; t <= 10; t++) buffer.TryStore(Cmd(t));

            // 服务器确认到 tick 6（客户端与服务器 tick 非一一对应场景：确认号来自服务器快照）
            buffer.PruneUpTo(6);
            Assert.That(buffer.Count, Is.EqualTo(4));
            Assert.That(buffer.HasCommands, Is.True);

            var unacked = buffer.CollectUnacked(6, 12);
            Assert.That(unacked.Count, Is.EqualTo(4));
            Assert.That(unacked[0].Tick, Is.EqualTo(7), "重传载荷最旧在前");
            Assert.That(unacked[3].Tick, Is.EqualTo(10));

            var capped = buffer.CollectUnacked(6, 2);
            Assert.That(capped.Count, Is.EqualTo(2));
            Assert.That(capped[0].Tick, Is.EqualTo(7));
        }

        [Test]
        public void Buffer_AckSnapshotReadBeforePrune_RemainsAvailableForReconciliation()
        {
            var buffer = new PredictionBuffer();
            buffer.TryStore(Cmd(3));
            buffer.RecordPredicted(new MovementSnapshot { Tick = 3, Position = new Vector3(3f, 0f, 0f) });

            Assert.That(buffer.TryGetPredictedAndPrune(3, out var ackSnapshot), Is.True,
                "校正必须在剪枝确认历史前读取 ACK 对应快照");
            Assert.That(ackSnapshot.Position.x, Is.EqualTo(3f));
            Assert.That(buffer.TryGetPredicted(3, out _), Is.False, "读取后确认快照才被剪枝");
        }

        [Test]
        public void Buffer_Clear_EmptiesHistory()
        {
            var buffer = new PredictionBuffer();
            buffer.TryStore(Cmd(3));
            buffer.RecordPredicted(new MovementSnapshot { Tick = 3 });
            buffer.Clear();
            Assert.That(buffer.HasCommands, Is.False);
            Assert.That(buffer.TryGetPredicted(3, out _), Is.False);
        }

        // ---- ServerInputQueue：丢包/乱序/重复/失控/积压 ----

        [Test]
        public void Queue_DuplicatesAndStaleDropped()
        {
            var queue = new ServerInputQueue();
            var drained = new List<MovementCommand>();
            queue.Enqueue(Cmd(5));
            Assert.That(queue.Drain(3, drained), Is.EqualTo(1));
            Assert.That(queue.LastProcessedTick, Is.EqualTo(5u));

            queue.Enqueue(Cmd(5)); // 重复
            queue.Enqueue(Cmd(4)); // 迟到
            Assert.That(queue.Drain(3, drained), Is.EqualTo(0));
            Assert.That(queue.DroppedStale, Is.EqualTo(2));
        }

        [Test]
        public void Queue_PacketLoss_GapSkipped_NoReplay()
        {
            var queue = new ServerInputQueue();
            var drained = new List<MovementCommand>();
            // 丢失 tick 1/2/3，直接收到 4：消费 4 并把确认号跳到 4（缺口不重放）
            queue.Enqueue(Cmd(4));
            Assert.That(queue.Drain(3, drained), Is.EqualTo(1));
            Assert.That(drained[0].Tick, Is.EqualTo(4u));
            Assert.That(queue.LastProcessedTick, Is.EqualTo(4u));

            // 迟到补投的 tick 3 不会再被模拟
            queue.Enqueue(Cmd(3));
            Assert.That(queue.Drain(3, drained), Is.EqualTo(0));
            Assert.That(queue.DroppedStale, Is.EqualTo(1));
        }

        [Test]
        public void Queue_OutOfOrderArrival_DrainsInTickOrder()
        {
            var queue = new ServerInputQueue();
            var drained = new List<MovementCommand>();
            queue.Enqueue(Cmd(8));
            queue.Enqueue(Cmd(6));
            queue.Enqueue(Cmd(7));
            Assert.That(queue.Drain(3, drained), Is.EqualTo(3));
            Assert.That(drained[0].Tick, Is.EqualTo(6u));
            Assert.That(drained[1].Tick, Is.EqualTo(7u));
            Assert.That(drained[2].Tick, Is.EqualTo(8u));
        }

        [Test]
        public void Queue_BurstDrain_CappedByCatchUp()
        {
            var queue = new ServerInputQueue();
            var drained = new List<MovementCommand>();
            for (uint t = 1; t <= 10; t++) queue.Enqueue(Cmd(t));
            Assert.That(queue.Drain(MovementPredictionConfig.ServerMaxCatchUpPerTick, drained),
                Is.EqualTo(MovementPredictionConfig.ServerMaxCatchUpPerTick), "单 tick 有限 catch-up");
            Assert.That(queue.Drain(3, drained), Is.EqualTo(3), "剩余积压随后续 tick 消化");
        }

        [Test]
        public void Queue_Overflow_DropsOldestAndCounts()
        {
            var queue = new ServerInputQueue();
            for (uint t = 1; t <= MovementPredictionConfig.ServerMaxPendingCommands + 4; t++)
                queue.Enqueue(Cmd(t));
            Assert.That(queue.Count, Is.LessThanOrEqualTo(MovementPredictionConfig.ServerMaxPendingCommands));
            Assert.That(queue.DroppedBacklog, Is.EqualTo(4), "超限积压丢弃最旧并告警计数");
        }

        [Test]
        public void Queue_RunawayFutureTick_Dropped()
        {
            var queue = new ServerInputQueue();
            var drained = new List<MovementCommand>();
            queue.Enqueue(Cmd(1));
            queue.Drain(3, drained);
            queue.Enqueue(Cmd(1 + MovementPredictionConfig.ServerMaxFutureTicks + 1));
            Assert.That(queue.DroppedFuture, Is.EqualTo(1));
            Assert.That(queue.Drain(3, drained), Is.EqualTo(0));
        }

        [Test]
        public void Queue_FirstPacketRunawayFutureTick_Dropped()
        {
            var queue = new ServerInputQueue();
            queue.Enqueue(Cmd(MovementPredictionConfig.ServerMaxFutureTicks + 1));
            Assert.That(queue.DroppedFuture, Is.EqualTo(1), "首包同样不得绕过未来 tick 窗口");
            Assert.That(queue.Count, Is.EqualTo(0));
        }

        // ---- 两个不同 RTT 客户端互不拖慢 ----

        [Test]
        public void TwoClients_DifferentSchedules_DoNotBlockEachOther()
        {
            var highLatency = new ServerInputQueue();
            var lowLatency = new ServerInputQueue();
            var drained = new List<MovementCommand>();

            // 高延迟客户端 10 个 tick 才到一批；低延迟客户端每 tick 一条
            for (uint t = 1; t <= 10; t++) highLatency.Enqueue(Cmd(t));
            lowLatency.Enqueue(Cmd(1));

            // 同一服务器 tick 内各自 Drain：低延迟立即有输入可消费，不被高延迟积压阻塞
            Assert.That(lowLatency.Drain(3, drained), Is.EqualTo(1));
            Assert.That(drained[0].Tick, Is.EqualTo(1u));
            Assert.That(highLatency.Drain(3, drained), Is.EqualTo(3), "高延迟积压按 catch-up 上限消化");
            // 各自确认号独立推进
            Assert.That(lowLatency.LastProcessedTick, Is.EqualTo(1u));
            Assert.That(highLatency.LastProcessedTick, Is.EqualTo(3u));
        }

        // ---- Reconciler：小误差平滑 / 大误差硬校正 / 噪声忽略 ----

        [Test]
        public void Reconciler_None_BelowNoiseThreshold()
        {
            var action = Reconciler.Decide(new Vector3(1f, 0f, 2f), new Vector3(1f, 0f, 2.01f), out _);
            Assert.That(action, Is.EqualTo(ReconcileAction.None));
        }

        [Test]
        public void Reconciler_Smooth_ForSmallError()
        {
            var action = Reconciler.Decide(new Vector3(0f, 0f, 0f), new Vector3(0.30f, 0f, 0f), out var correction);
            Assert.That(action, Is.EqualTo(ReconcileAction.Smooth));
            Assert.That(correction.x, Is.EqualTo(-0.30f).Within(1e-4), "校正量 = 权威 - 预测");
        }

        [Test]
        public void Reconciler_Snap_ForLargeError()
        {
            var action = Reconciler.Decide(new Vector3(5f, 0f, 0f), new Vector3(0f, 0f, 0f), out _);
            Assert.That(action, Is.EqualTo(ReconcileAction.Snap));
        }

        // ---- 2026-09-17 审计 D1（Critical）：阈值判定基于 corrected（扣除在途平滑量）----

        [Test]
        public void Reconciler_InFlightSubtraction_DemotesNearThresholdErrorToSmooth()
        {
            // 审计 §7.1 最小反例：raw 0.78 恰在 0.75 之上（旧实现直接硬对位）；
            // 但 0.20m 是已施加、尚未被快照计入的在途平滑步进 → 真实残余 0.58 落平滑带，必须 Smooth
            var server = Snap(5, new Vector3(0.78f, 0f, 0f));
            var predicted = Snap(5, Vector3.zero);
            var decision = Reconciler.Decide(server, predicted, new Vector3(0.20f, 0f, 0f),
                out var raw, out var corrected);
            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.Smooth),
                "raw 0.78 − inFlight 0.20 = 0.58 ∈ (0.03, 0.75) 平滑带");
            Assert.That(raw.magnitude, Is.EqualTo(0.78f).Within(1e-4f));
            Assert.That(corrected.magnitude, Is.EqualTo(0.58f).Within(1e-4f));
        }

        [Test]
        public void Reconciler_ZeroInFlight_KeepsRawDecision()
        {
            // 空源直通恒等：inFlight=0 → corrected == raw，决策与旧三参重载完全一致
            var server = Snap(5, new Vector3(0.80f, 0f, 0f));
            var predicted = Snap(5, Vector3.zero);
            var viaInFlight = Reconciler.Decide(server, predicted, Vector3.zero, out _, out var corrected);
            var legacy = Reconciler.Decide(server, predicted, out var legacyCorrection);
            Assert.That(viaInFlight.Action, Is.EqualTo(legacy.Action));
            Assert.That(corrected, Is.EqualTo(legacyCorrection).Within(1e-6f));
        }

        [Test]
        public void Reconciler_InFlightSubtraction_LargeErrorStillSnaps()
        {
            // 扣除后仍超阈（0.90 − 0.05 = 0.85 ≥ 0.75）：硬对位语义保留（修复不是"永不 Snap"）
            var server = Snap(5, new Vector3(0.90f, 0f, 0f));
            var predicted = Snap(5, Vector3.zero);
            var decision = Reconciler.Decide(server, predicted, new Vector3(0.05f, 0f, 0f), out _, out _);
            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.Snap));
        }

        [Test]
        public void Reconciler_InFlightSubtraction_NoiseBandAfterDeduction()
        {
            // raw 0.04 扣在途 0.02 → 残余 0.02 ≤ 0.03：纯噪声，不施加任何校正
            var server = Snap(5, new Vector3(0.04f, 0f, 0f));
            var predicted = Snap(5, Vector3.zero);
            var decision = Reconciler.Decide(server, predicted, new Vector3(0.02f, 0f, 0f),
                out _, out var corrected);
            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.None));
            Assert.That(corrected.magnitude, Is.EqualTo(0.02f).Within(1e-4f));
        }

        [Test]
        public void Reconciler_SmoothStep_DecaysExpontentiallyToZero()
        {
            Vector3 remaining = new Vector3(0.5f, 0f, 0f);
            float total = 0f;
            for (int i = 0; i < 60; i++) // 2 秒 @30Hz：远超 SmoothConvergeSeconds
            {
                var step = Reconciler.SmoothStep(remaining, (float)FixedDelta);
                remaining -= step;
                total += step.magnitude;
            }
            Assert.That(total, Is.EqualTo(0.5f).Within(0.005f), "总量守恒（收敛到权威位置）");
            Assert.That(remaining.magnitude, Is.LessThan(0.001f), "余量收敛到零");
        }

        // ---- 2026-09-13 专项：快照域决策（速度/朝向/移动分叉）与滞回 ----

        private static MovementSnapshot Snap(uint tick, Vector3 pos, float yawDeg = 0f,
            Vector3 horizontal = default, float vertical = 0f, LocomotionState state = LocomotionState.Idle)
        {
            return new MovementSnapshot
            {
                Tick = tick,
                Position = pos,
                Rotation = Quaternion.Euler(0f, yawDeg, 0f),
                HorizontalVelocity = horizontal,
                VerticalVelocity = vertical,
                LocomotionState = state,
            };
        }

        [Test]
        public void Reconciler_Snapshot_IdenticalState_NoDivergence()
        {
            var server = Snap(5, new Vector3(1f, 0f, 2f), 30f, new Vector3(1.5f, 0f, 0f), -2f);
            var predicted = Snap(5, new Vector3(1f, 0f, 2f), 30f, new Vector3(1.5f, 0f, 0f), -2f);
            var decision = Reconciler.Decide(server, predicted, out _);
            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.None));
            Assert.That(decision.SoftDivergence, Is.False);
        }

        [Test]
        public void Reconciler_HorizontalSpeedDivergence_Reported_AtSamePosition()
        {
            // 位置相同但水平速度错误：必须报分叉（速度错误会在后续积分中放大成位置漂移）
            var server = Snap(5, new Vector3(1f, 0f, 2f), 0f, new Vector3(3.4f, 0f, 0f));
            var predicted = Snap(5, new Vector3(1f, 0f, 2f), 0f, new Vector3(0f, 0f, 0f));
            var decision = Reconciler.Decide(server, predicted, out _);
            Assert.That(decision.Divergence, Is.EqualTo(ReconcileDivergence.HorizontalSpeed));
        }

        [Test]
        public void Reconciler_VerticalSpeedDirectionFlip_Reported_AtSamePosition()
        {
            // 位置相同但垂直速度方向相反（一升一降）：重力积分方向错误，必须报分叉
            var server = Snap(5, new Vector3(1f, 0f, 2f), 0f, default, 3f, LocomotionState.Jump);
            var predicted = Snap(5, new Vector3(1f, 0f, 2f), 0f, default, -3f, LocomotionState.Air);
            var decision = Reconciler.Decide(server, predicted, out _);
            // 跨组（Air vs Air 同组）+ vv 差 6.0 ≥ 2.0：按优先级应报垂直速度（组一致时）
            Assert.That(decision.Divergence, Is.EqualTo(ReconcileDivergence.VerticalSpeed));
        }

        [Test]
        public void Reconciler_GroundedAirborneBranchDivergence_Reported()
        {
            // grounded/airborne 跨组分叉：SimulateGround vs SimulateAir 是不同的积分路径
            var server = Snap(5, new Vector3(1f, 0f, 2f), 0f, default, -2f, LocomotionState.Idle);
            var predicted = Snap(5, new Vector3(1f, 0f, 2f), 0f, default, -5f, LocomotionState.Air);
            var decision = Reconciler.Decide(server, predicted, out _);
            Assert.That(decision.Divergence, Is.EqualTo(ReconcileDivergence.LocomotionState));
        }

        [Test]
        public void Reconciler_SameBranchStateDelta_Ignored()
        {
            // Idle vs Walk 同在地面分支：不影响积分路径，离散状态差异不动作（专项决策表）
            var server = Snap(5, new Vector3(1f, 0f, 2f), 0f, default, -2f, LocomotionState.Idle);
            var predicted = Snap(5, new Vector3(1f, 0f, 2f), 0f, default, -2f, LocomotionState.Walk);
            var decision = Reconciler.Decide(server, predicted, out _);
            Assert.That(decision.SoftDivergence, Is.False);
        }

        [Test]
        public void Reconciler_YawAcrossZeroBoundary_UsesShortestArc()
        {
            // 旋转跨 0/360°：359.5° vs 0.5° 实际差 1°（<4° 容差），不得按欧拉数值误报 359°
            var server = Snap(5, new Vector3(1f, 0f, 2f), 359.5f);
            var predicted = Snap(5, new Vector3(1f, 0f, 2f), 0.5f);
            Assert.That(Reconciler.Decide(server, predicted, out _).SoftDivergence, Is.False,
                "跨 0/360° 的 1° 角差在容差内");

            // 真实分叉：350° vs 10° 短弧差 20°（≥4°）→ yaw 域
            var serverB = Snap(5, new Vector3(1f, 0f, 2f), 350f);
            var predictedB = Snap(5, new Vector3(1f, 0f, 2f), 10f);
            Assert.That(Reconciler.Decide(serverB, predictedB, out _).Divergence,
                Is.EqualTo(ReconcileDivergence.Yaw));
        }

        [Test]
        public void Reconciler_TimeAlignedComparison_IgnoresLocalViewAhead()
        {
            // 正常转头不被拉回：比较对象是"同一已确认 tick 的配对快照"（时间对齐）——
            // 服务器 yaw 与 ACK tick 预测一致即为 None；本地视角（CameraPivot pitch/后续 yaw 预测）
            // 根本不进入比较，网络滞后不会把旧朝向强压到当前视角。
            var server = Snap(5, new Vector3(1f, 0f, 2f), 50f);
            var predictedAtAck = Snap(5, new Vector3(1f, 0f, 2f), 50f); // tick 5 时的预测
            var localCurrentYaw = 80f; // 本地已预测/转头到 80°（滞后差 30°）——不参与比较
            Assert.That(localCurrentYaw, Is.GreaterThan(50f), "前置：本地视角确实超前于权威快照");
            var decision = Reconciler.Decide(server, predictedAtAck, out _);
            Assert.That(decision.SoftDivergence, Is.False, "时间对齐下朝向一致，不得拉回");
        }

        [Test]
        public void DivergenceGate_SpeedDivergence_RequiresConsecutiveConfirmations()
        {
            // 位置相同但水平速度分叉：单快照不动作（瞬态容忍），连续 3 快照升级硬校正
            var gate = new DivergenceGate();
            var server = Snap(5, new Vector3(1f, 0f, 2f), 0f, new Vector3(3f, 0f, 0f));
            var predicted = Snap(5, new Vector3(1f, 0f, 2f), 0f, Vector3.zero);
            var decision = Reconciler.Decide(server, predicted, out _);
            Assert.That(decision.Action, Is.EqualTo(ReconcileAction.None), "位置在噪声阈值内");

            Assert.That(gate.Feed(decision), Is.False, "第 1 次不触发");
            Assert.That(gate.Feed(decision), Is.False, "第 2 次不触发");
            Assert.That(gate.Feed(decision), Is.True, "第 3 次连续分叉确认升级");
            Assert.That(gate.Feed(decision), Is.False, "触发后重新计数");
        }

        [Test]
        public void DivergenceGate_TransientDivergence_ResetsCounter()
        {
            // 落地/起跳相位瞬态：1-2 个快照后恢复一致 → 计数清零，不得升级硬校正
            var gate = new DivergenceGate();
            var diverged = Reconciler.Decide(
                Snap(5, Vector3.zero, 0f, default, 8f, LocomotionState.Jump),
                Snap(5, Vector3.zero, 0f, default, -2f, LocomotionState.Idle), out _);
            Assert.That(diverged.SoftDivergence, Is.True);

            var consistent = Reconciler.Decide(
                Snap(6, Vector3.zero), Snap(6, Vector3.zero), out _);
            Assert.That(gate.Feed(diverged), Is.False);
            Assert.That(gate.Feed(consistent), Is.False);
            Assert.That(gate.Feed(diverged), Is.False, "瞬态中断后重新计数");
            Assert.That(gate.Feed(diverged), Is.False);
            Assert.That(gate.Feed(diverged), Is.True, "只有再次连续 3 次才升级");
        }

        [Test]
        public void DivergenceGate_YawConfirmsEarlierThanSpeed()
        {
            // yaw 无瞬态来源（只在输入丢失时分叉）：2 次即确认；速度需 3 次
            var gate = new DivergenceGate();
            var yawDiverged = Reconciler.Decide(
                Snap(5, Vector3.zero, 30f), Snap(5, Vector3.zero, 0f), out _);
            Assert.That(yawDiverged.Divergence, Is.EqualTo(ReconcileDivergence.Yaw));
            Assert.That(gate.Feed(yawDiverged), Is.False);
            Assert.That(gate.Feed(yawDiverged), Is.True, "yaw 第 2 次确认");
        }

        // ---- 2026-09-13 专项：快照 ACK 门（重复/乱序/初始对位/边界） ----

        [Test]
        public void Gate_DuplicateAck_Ignored()
        {
            // 核心修复：服务器 30Hz 推快照 vs 客户端 15Hz 批量上传 → LastClientTick 相等的重复
            // 快照是常态。此前相等快照穿透乱序门 → 配对历史已剪枝 → 误判"历史缺失"每快照硬对位。
            var gate = new ReconcileGate();
            Assert.That(gate.Feed(5), Is.EqualTo(ReconcileGateAction.Compare));
            Assert.That(gate.Feed(5), Is.EqualTo(ReconcileGateAction.IgnoreRepeat),
                "相等 ACK = 重复快照，必须忽略");
            Assert.That(gate.Feed(4), Is.EqualTo(ReconcileGateAction.IgnoreRepeat), "乱序旧快照忽略");
        }

        [Test]
        public void Gate_InitialSnap_OnlyOnce()
        {
            // 进房后首个输入确认前的 LastClientTick==0 快照只做一次初始对位，
            // 后续重复的 0 快照不得每 tick 硬对位（既有缺陷：空闲等待期反复对位）。
            var gate = new ReconcileGate();
            Assert.That(gate.Feed(0), Is.EqualTo(ReconcileGateAction.InitialSnap));
            Assert.That(gate.Feed(0), Is.EqualTo(ReconcileGateAction.IgnoreRepeat));
            Assert.That(gate.Feed(0), Is.EqualTo(ReconcileGateAction.IgnoreRepeat));
        }

        [Test]
        public void Gate_AckAdvance_Compare_AndHistoryMissingHandledByCaller()
        {
            var gate = new ReconcileGate();
            Assert.That(gate.Feed(3), Is.EqualTo(ReconcileGateAction.Compare));
            Assert.That(gate.LastAckedClientTick, Is.EqualTo(3u));
            Assert.That(gate.Feed(8), Is.EqualTo(ReconcileGateAction.Compare),
                "ACK 前进进入配对比较（历史是否缺失由调用方查 buffer 决定）");
            Assert.That(gate.LastAckedClientTick, Is.EqualTo(8u));
        }

        [Test]
        public void Gate_Reset_ClearsBaselineForRespawnOrReconnect()
        {
            // 重生/重连/接管边界：ACK 基线与初始对位标记一并重建
            var gate = new ReconcileGate();
            gate.Feed(0);
            gate.Feed(12);
            gate.Reset();
            Assert.That(gate.LastAckedClientTick, Is.EqualTo(0u));
            Assert.That(gate.Feed(0), Is.EqualTo(ReconcileGateAction.InitialSnap),
                "重置后允许再次初始对位");
        }

        [Test]
        public void Gate_Observe_SyncsBaselineWithoutAction()
        {
            // 死亡边界：Observe 只同步 ACK 基线（服务器 LastProcessedTick 单调不减），不产生决策
            var gate = new ReconcileGate();
            gate.Feed(6);
            gate.Observe(9);
            Assert.That(gate.LastAckedClientTick, Is.EqualTo(9u));
            Assert.That(gate.Feed(9), Is.EqualTo(ReconcileGateAction.IgnoreRepeat),
                "死亡后的重复快照同样被门拦截");
            Assert.That(gate.Feed(10), Is.EqualTo(ReconcileGateAction.Compare));
        }

        [Test]
        public void Gate_RepeatedZeroAfterDeathSnapshot_DoesNotResnap()
        {
            // 死亡后 _localTick=LastClientTick；重生快照（Dead=false）ACK 前进→Compare→无配对历史
            // →调用方硬对位。门的职责：0 快照的 initialSnapDone 已消费，不重复 InitialSnap。
            var gate = new ReconcileGate();
            gate.Feed(0);
            gate.Observe(7);
            Assert.That(gate.Feed(0), Is.EqualTo(ReconcileGateAction.IgnoreRepeat));
        }

        // ---- 死亡边界：清空旧预测历史 ----

        [Test]
        public void DeathBoundary_ClearHistoryThenRespawnSnap()
        {
            var buffer = new PredictionBuffer();
            for (uint t = 1; t <= 20; t++)
            {
                buffer.TryStore(Cmd(t));
                buffer.RecordPredicted(new MovementSnapshot { Tick = t, Position = new Vector3(t, 0, 0) });
            }

            // 死亡快照到达：Owner 清历史（Adapter 对 Dead 分支调用 Clear + 硬对位）
            buffer.Clear();
            Assert.That(buffer.HasCommands, Is.False);
            Assert.That(buffer.TryGetPredicted(20, out _), Is.False, "旧预测历史不得跨死亡保留");

            // 重生：新预测与确认从零重建
            buffer.TryStore(Cmd(1));
            Assert.That(buffer.HasCommands, Is.True);
            Assert.That(buffer.NewestTick, Is.EqualTo(1u));
        }

        // ---- 确认与重放语义（客户端/服务器 tick 非一一对应）----

        [Test]
        public void ReplayFromAckedPlusOne_UsesBufferedCommands()
        {
            var buffer = new PredictionBuffer();
            for (uint t = 1; t <= 8; t++)
            {
                buffer.TryStore(Cmd(t, x: t));
                buffer.RecordPredicted(new MovementSnapshot { Tick = t, Position = new Vector3(t * 0.1f, 0, 0) });
            }

            const uint acked = 5;
            Assert.That(buffer.TryGetPredicted(acked, out var predictedAtAck), Is.True,
                "确认 tick 的预测快照可用于误差评估");
            buffer.PruneUpTo(acked);

            // HardSnapTo 重放路径：acked+1..newest 逐条取出重模拟
            int replayed = 0;
            for (uint tick = acked + 1; tick <= buffer.NewestTick; tick++)
                if (buffer.TryGetCommand(tick, out var cmd))
                    replayed++;
            Assert.That(replayed, Is.EqualTo(3), "重放 = 确认 tick+1 到最新，且每条至多一次");
        }
    }
}
