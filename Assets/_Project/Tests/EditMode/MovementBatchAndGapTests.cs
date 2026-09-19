using System.Collections.Generic;
using System.Reflection;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 审计 2026-09-16 M1/M3 定向回归：
    /// ① CollectUnacked 必须"完整收集→排序→截断"（Dictionary 删插后枚举顺序≠tick 序会漏旧输入）；
    /// ② 服务器缺口必须显式计数（ACK 语义 = 已跳过并结算，不得当成"已模拟"）；
    /// ③ 硬重基的缺口清理：作废陈旧预测快照但保留命令（可重传）；
    /// ④ 移动快照必须精确携带确定性重放状态（不再用速度模长/枚举反推）。
    /// 审计 2026-09-17 D2/R1/D3 定向回归：
    /// ⑤ 服务器空 tick 保持位姿（不外推、不喂空命令），ConsecutiveEmptyTicks 只作诊断；
    /// ⑥ Drain 按 ServerMaxCatchUpPerTick 有限追赶，突发积压数 tick 内追回；
    /// ⑦ 冻结窗口（死亡/倒计时）拒收在途批次（epoch 残留清理）。
    /// </summary>
    public sealed class MovementBatchAndGapTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<GameObject> _spawned = new();

        private static MovementCommand Cmd(uint tick)
            => new MovementCommand(Vector2.zero, false, false, 0f, 0f, tick);

        private Locomotor SpawnLocomotor()
        {
            var go = new GameObject("Sim_" + _spawned.Count);
            _spawned.Add(go);
            go.AddComponent<CharacterController>();
            var locomotor = go.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.PredictedOwner);
            typeof(Locomotor).GetMethod("Awake", NonPublic)?.Invoke(locomotor, null);
            return locomotor;
        }

        [SetUp]
        public void SetUp() => Physics.autoSyncTransforms = true;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        /// <summary>
        /// M1 最小反例：存 1..16 → 剪枝 1..8（腾空槽位）→ 再存 17..24。
        /// 旧实现按 Dictionary 枚举凑满 12 条就 break、之后才排序 → 可能返回 9,10,11,12,17..24（漏 13..16）；
        /// 正确必须是最旧的 12 条 = 9..20。漏发会让服务器跳缺口，而客户端已按 ACK 剪枝 → 输入被当已确认。
        /// </summary>
        [Test]
        public void CollectUnacked_AfterDeleteAndReinsert_StillReturnsOldestBatch()
        {
            var buffer = new PredictionBuffer();
            for (uint t = 1; t <= 16; t++) buffer.TryStore(Cmd(t));
            buffer.PruneUpTo(8);
            for (uint t = 17; t <= 24; t++) buffer.TryStore(Cmd(t));

            var batch = buffer.CollectUnacked(8, 12);

            Assert.That(batch.Count, Is.EqualTo(12));
            Assert.That(batch[0].Tick, Is.EqualTo(9u), "最旧在前");
            Assert.That(batch[11].Tick, Is.EqualTo(20u), "必须是连续最旧 12 条（9..20），不得漏 13..16");
            for (int i = 1; i < batch.Count; i++)
                Assert.That(batch[i].Tick, Is.EqualTo(batch[i - 1].Tick + 1u), "批次必须连续升序");
        }

        /// <summary>上限截断只影响尾部：小于总数时仍取最旧 max 条。</summary>
        [Test]
        public void CollectUnacked_Cap_TruncatesNewestNotOldest()
        {
            var buffer = new PredictionBuffer();
            for (uint t = 1; t <= 30; t++) buffer.TryStore(Cmd(t));
            buffer.PruneUpTo(25);

            var batch = buffer.CollectUnacked(25, 3);
            Assert.That(batch.Count, Is.EqualTo(3));
            Assert.That(batch[0].Tick, Is.EqualTo(26u));
            Assert.That(batch[2].Tick, Is.EqualTo(28u));
        }

        /// <summary>M1：服务器 Drain 跳过缺口时必须显式计数（旧实现静默跳变，ACK 语义含糊）。</summary>
        [Test]
        public void ServerInputQueue_GapIsCountedExplicitly()
        {
            var queue = new ServerInputQueue();
            var output = new List<MovementCommand>();
            queue.Enqueue(Cmd(10));
            queue.Enqueue(Cmd(11));
            queue.Enqueue(Cmd(15)); // 12..14 缺失（上行漏发/积压）

            Assert.That(queue.Drain(2, output), Is.EqualTo(2));
            Assert.That(queue.LastDrainGap, Is.EqualTo(0), "连续消费不算缺口");
            Assert.That(output[0].Tick, Is.EqualTo(10u));
            Assert.That(output[1].Tick, Is.EqualTo(11u));

            Assert.That(queue.Drain(1, output), Is.EqualTo(1));
            Assert.That(queue.LastDrainGap, Is.EqualTo(3), "10,11 → 15 跳过 12/13/14");
            Assert.That(queue.SkippedTicks, Is.EqualTo(3), "缺口累计=被跳过并结算、永不被模拟的 tick 数");
            Assert.That(queue.LastProcessedTick, Is.EqualTo(15u));
        }

        /// <summary>M3：硬重基遇到命令缺口 → 作废未模拟区间的陈旧预测快照，但命令保留（仍可重传）。</summary>
        [Test]
        public void InvalidatePredictedAbove_DropsStaleSnapshots_KeepsCommandsForResend()
        {
            var buffer = new PredictionBuffer();
            for (uint t = 1; t <= 5; t++) buffer.TryStore(Cmd(t));
            for (uint t = 1; t <= 5; t++)
                buffer.RecordPredicted(new MovementSnapshot { Tick = t, Position = new Vector3(t, 0f, 0f) });

            buffer.InvalidatePredictedAbove(3);

            Assert.That(buffer.TryGetPredicted(3, out _), Is.True, "≤ 指定 tick 的快照保留");
            Assert.That(buffer.TryGetPredicted(4, out _), Is.False, "未模拟区间的陈旧快照必须作废");
            Assert.That(buffer.TryGetPredicted(5, out _), Is.False);
            Assert.That(buffer.TryGetCommand(5, out _), Is.True, "命令保留（重传载荷不受影响）");
        }

        /// <summary>
        /// M3：快照必须精确携带确定性重放状态——原实现把 _groundSpeed 推断成 |HorizontalVelocity|、
        /// coyote/land 计时重写成满值、空中 sprintIntent 推断不出，硬重基后下一 tick 继续分叉。
        /// </summary>
        [Test]
        public void Snapshot_RoundTripsDeterministicState_NotInferred()
        {
            var locomotor = SpawnLocomotor();
            var source = new MovementSnapshot
            {
                Tick = 42,
                Position = new Vector3(1f, 0.1f, 2f),
                Rotation = Quaternion.identity,
                HorizontalVelocity = new Vector3(1.5f, 0f, 0f),
                VerticalVelocity = -0.2f,
                LocomotionState = LocomotionState.Sprint,
                GaitPhase = 0.37f,
                Grounded = true,
                GroundSpeed = 2.75f, // 刻意 ≠ |HorizontalVelocity|（root motion 相位下瞬时速度 ≠ 地面速度）
                CoyoteTimer = 0.05f,
                LandTimer = 0.08f,
                SprintIntent = true,
                RecoilDebt = new Vector2(0.4f, -0.2f),
            };

            locomotor.ApplyAuthoritativeSnapshot(source);
            var applied = locomotor.CaptureSnapshot();

            Assert.That(applied.GroundSpeed, Is.EqualTo(2.75f).Within(1e-4f),
                "必须精确恢复 _groundSpeed（旧实现 = |v| = 1.5 → 重放起点错误）");
            Assert.That(applied.CoyoteTimer, Is.EqualTo(0.05f).Within(1e-5f), "土狼计时精确恢复");
            Assert.That(applied.LandTimer, Is.EqualTo(0.08f).Within(1e-5f), "落地计时精确恢复");
            Assert.That(applied.SprintIntent, Is.True, "冲刺意图精确恢复（空中不再丢失）");
            Assert.That(applied.Grounded, Is.True, "落地判定随快照携带");
            Assert.That(applied.HorizontalVelocity.x, Is.EqualTo(1.5f).Within(1e-4f));
            Assert.That(applied.LocomotionState, Is.EqualTo(LocomotionState.Sprint));
        }

        /// <summary>M3：后坐补偿债务必须可存取（快照携带 + 重放前恢复），否则重放会二次消费/少消费。</summary>
        [Test]
        public void RecoilDebt_IsCapturedAndRestored()
        {
            var recoil = new WeaponRecoilState();
            var requested = new Vector2(0f, -1.5f); // 向右转 → 抵消正向右的后坐债
            recoil.RestoreCompensationDebt(new Vector2(0f, 0.9f));

            var remaining = recoil.ConsumeCompensation(requested);

            Assert.That(remaining.y, Is.EqualTo(-0.6f).Within(1e-4f), "债务被消费后剩余输入继续写入视角");
            Assert.That(recoil.CompensationDebt.y, Is.EqualTo(0f).Within(1e-4f), "债务清零");

            recoil.RestoreCompensationDebt(new Vector2(0.3f, 0.2f));
            Assert.That(recoil.CompensationDebt, Is.EqualTo(new Vector2(0.3f, 0.2f)),
                "重放前必须能恢复到权威债务（否则重放与首轮消费不同源）");
        }

        // ---- 2026-09-16 审计 §3.2-1/§3.2-2/§6.2：新增定向回归 ----

        /// <summary>
        /// §3.2-1：快照 Grounded 必须成为**重放首步**的分支输入（CC 刚被瞬移，isGrounded 尚未重新
        /// 解算）；后续步一律回到实际接触。旧实现把 Grounded 存进快照却仍读 _cc.isGrounded，
        /// 等于没恢复——重放第一步就分叉，硬校正后下一 tick 继续分叉（来回拽回的机制之一）。
        /// </summary>
        [Test]
        public void SnapshotGrounded_DrivesFirstReplayStep_ThenFallsBackToContact()
        {
            var locomotor = SpawnLocomotor();
            locomotor.ApplyAuthoritativeSnapshot(new MovementSnapshot
            {
                Tick = 1,
                Position = new Vector3(0f, 5f, 0f), // 空中：CC 不会自己报 grounded
                Rotation = Quaternion.identity,
                Grounded = true,
                CoyoteTimer = 0.12f,
                LocomotionState = LocomotionState.Walk,
            });

            locomotor.Simulate(new MovementCommand(new Vector2(0f, 1f), false, false, 0f, 0f, 2), 1f / 30f);
            Assert.That(locomotor.LastStepDebug.GroundedFromSnapshot, Is.True, "首步必须来自快照");
            Assert.That(locomotor.LastStepDebug.GroundedBranch, Is.True, "首步分支输入 = 快照接地");

            locomotor.Simulate(new MovementCommand(new Vector2(0f, 1f), false, false, 0f, 0f, 3), 1f / 30f);
            Assert.That(locomotor.LastStepDebug.GroundedFromSnapshot, Is.False, "覆盖只用一步");
            Assert.That(locomotor.LastStepDebug.GroundedBranch, Is.False, "后续步回到实际接触（此处空中）");
        }

        // ---- 2026-09-17 审计 D2/R1/D3：服务器权威端修复的定向回归 ----

        /// <summary>
        /// D2：空队列改"保持位姿"——服务器空 tick 不再外推/不喂空命令（旧外推把权威角色单方
        /// 推走 k 步，k×步长 100% 计入配对误差）；ConsecutiveEmptyTicks 降级为纯诊断计数，
        /// 消费到真实输入即清零（不再存在"外推上限"语义）。
        /// </summary>
        [Test]
        public void ServerInputQueue_EmptyQueue_CountsIdleStreak_ResetByRealInput()
        {
            var queue = new ServerInputQueue();
            var output = new List<MovementCommand>();
            queue.Enqueue(new MovementCommand(new Vector2(0f, 1f), true, false, 0f, 0f, 10));
            Assert.That(queue.Drain(1, output), Is.EqualTo(1));
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(0));

            Assert.That(queue.Drain(1, output), Is.EqualTo(0), "无输入（到批间隙）");
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(1), "空 tick 计数（保持位姿，不模拟）");
            Assert.That(queue.Drain(1, output), Is.EqualTo(0));
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(2), "连续空 tick 累加（无外推上限）");

            queue.Enqueue(new MovementCommand(Vector2.zero, false, false, 0f, 0f, 13));
            Assert.That(queue.Drain(1, output), Is.EqualTo(1));
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(0), "消费到真实输入即清零");
            Assert.That(queue.LastProcessedTick, Is.EqualTo(13u));

            queue.Clear(); // 冻结/死亡：待处理输入与空 tick 计数一并复位
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(0));
        }

        /// <summary>构建可 EditMode 直驱的服务器权威实例（adapter + Locomotor@ServerAuthority）。</summary>
        private (PlayerNetworkAdapter adapter, ServerInputQueue queue, Transform root, MethodInfo advance) SpawnServerSim()
        {
            var go = new GameObject("ServerSim_" + _spawned.Count);
            _spawned.Add(go);
            go.AddComponent<CharacterController>();
            var locomotor = go.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.ServerAuthority);
            typeof(Locomotor).GetMethod("Awake", NonPublic)?.Invoke(locomotor, null);
            var adapter = go.AddComponent<PlayerNetworkAdapter>();
            typeof(PlayerNetworkAdapter).GetField("_locomotor", NonPublic)?.SetValue(adapter, locomotor);
            var queue = (ServerInputQueue)typeof(PlayerNetworkAdapter).GetField("_serverQueue", NonPublic).GetValue(adapter);
            var advance = typeof(PlayerNetworkAdapter).GetMethod("AdvanceServerInputs", Any);
            return (adapter, queue, go.transform, advance);
        }

        private static void Advance(PlayerNetworkAdapter adapter, MethodInfo advance)
            => advance.Invoke(adapter, new object[] { false });

        /// <summary>
        /// D2 接线：空 tick 服务器保持位姿——不外推、不喂空命令。菜单/聊天冻结场景两端同静
        /// （客户端冻结不预测、服务器空 tick 不模拟 → 误差恒 0，旧实现的 0.9m+ 虚假 SNAP 消失）。
        /// </summary>
        [Test]
        public void AdvanceServerInputs_EmptyQueue_HoldsPosition_NoSimulation()
        {
            var (adapter, queue, root, advance) = SpawnServerSim();
            Vector3 initial = root.position;

            queue.Enqueue(new MovementCommand(new Vector2(0f, 1f), true, false, 0f, 0f, 1));
            Advance(adapter, advance);
            Assert.That(queue.LastProcessedTick, Is.EqualTo(1u), "前置：真实输入被消费");
            Vector3 afterMove = root.position;
            Assert.That(Vector3.Distance(initial, afterMove), Is.GreaterThan(1e-4f),
                "前置：真实输入使角色产生位移（空中重力分支）");

            for (int i = 0; i < 3; i++)
            {
                Advance(adapter, advance);
                Assert.That(root.position, Is.EqualTo(afterMove).Within(1e-6f),
                    $"空 tick {i + 1}：保持位姿，不得外推/空命令模拟");
            }
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(3), "空 tick 只计数不模拟");
        }

        /// <summary>
        /// R1 接线：积压按 ServerMaxCatchUpPerTick 追赶——突发 lead 数 tick 内追回
        /// （旧实现恒 Drain(1)，两端平均速率相同 → 突发积压永不排空、lead 永久抬升）。
        /// </summary>
        [Test]
        public void AdvanceServerInputs_BurstBacklog_IsRecoveredWithinFewTicks()
        {
            var (adapter, queue, _, advance) = SpawnServerSim();
            for (uint t = 1; t <= 9; t++)
                queue.Enqueue(new MovementCommand(new Vector2(0f, 1f), false, false, 0f, 0f, t));

            Advance(adapter, advance);
            Assert.That(queue.LastProcessedTick, Is.EqualTo(3u), "单 tick 有限追赶 = ServerMaxCatchUpPerTick");
            Advance(adapter, advance);
            Assert.That(queue.LastProcessedTick, Is.EqualTo(6u));
            Advance(adapter, advance);
            Assert.That(queue.LastProcessedTick, Is.EqualTo(9u), "9 条积压 3 tick 内清空");
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(0));

            Advance(adapter, advance);
            Assert.That(queue.ConsecutiveEmptyTicks, Is.EqualTo(1), "清空后的下一个空 tick 走 hold");
        }

        /// <summary>
        /// D3 接线：冻结窗口（死亡/倒计时）到达的在途批次必须拒收——否则死亡前"已发出未确认"
        /// 的旧命令在重生后会被当作新 epoch 输入消费（客户端重生从 ack 重新生成同号 tick）。
        /// </summary>
        [Test]
        public void EnqueueServerInputBatch_FrozenWindow_DropsBatch_AndCounts()
        {
            var go = new GameObject("FrozenSim_" + _spawned.Count);
            _spawned.Add(go);
            go.AddComponent<CharacterController>();
            var combat = go.AddComponent<NetworkCombatAuthority>();
            var locomotor = go.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.ServerAuthority);
            typeof(Locomotor).GetMethod("Awake", NonPublic)?.Invoke(locomotor, null);
            var adapter = go.AddComponent<PlayerNetworkAdapter>();
            typeof(PlayerNetworkAdapter).GetField("_locomotor", NonPublic)?.SetValue(adapter, locomotor);
            typeof(PlayerNetworkAdapter).GetField("_combatAuthority", NonPublic)?.SetValue(adapter, combat);
            var queue = (ServerInputQueue)typeof(PlayerNetworkAdapter).GetField("_serverQueue", NonPublic).GetValue(adapter);
            var enqueue = typeof(PlayerNetworkAdapter).GetMethod("EnqueueServerInputBatch", Any);

            SetDead(combat, true);
            enqueue.Invoke(adapter, new object[] { new[] { Cmd(1), Cmd(2), Cmd(3) } });
            Assert.That(queue.Count, Is.EqualTo(0), "冻结窗口拒收在途批次（不入队）");
            Assert.That(queue.DroppedFrozen, Is.EqualTo(3), "拒收计数留痕");

            SetDead(combat, false); // 重生解冻
            enqueue.Invoke(adapter, new object[] { new[] { Cmd(4) } });
            Assert.That(queue.Count, Is.EqualTo(1), "解冻后正常入队");
            Assert.That(queue.DroppedFrozen, Is.EqualTo(3), "解冻后的正常入队不再计入拒收");
        }

        private static void SetDead(NetworkCombatAuthority combat, bool dead)
        {
            var syncVar = (SyncVar<bool>)typeof(NetworkCombatAuthority).GetField("_dead", Any).GetValue(combat);
            typeof(SyncVar<bool>).GetField("_value", Any).SetValue(syncVar, dead);
        }

        /// <summary>§6.2：快照必须携带基础俯仰（两端基础瞄准可对账；未注入提供者时为 0）。</summary>
        [Test]
        public void Snapshot_CarriesPitch_FromProvider()
        {
            var locomotor = SpawnLocomotor();
            locomotor.PitchProvider = () => -12.5f;
            Assert.That(locomotor.CaptureSnapshot().Pitch, Is.EqualTo(-12.5f).Within(1e-4f));

            locomotor.PitchProvider = null;
            Assert.That(locomotor.CaptureSnapshot().Pitch, Is.EqualTo(0f));
        }

        /// <summary>§3.3：模拟步环形采样容量固定、按 tick 对齐输出、字段齐全。</summary>
        [Test]
        public void StepTrace_IsBounded_AndFormatsEveryForensicField()
        {
            var trace = new MovementStepTrace(capacity: 8);
            for (int i = 0; i < 20; i++)
                trace.Record(new MovementStepSample
                {
                    ClientTick = (uint)(i + 1),
                    Epoch = 2,
                    MoveInput = new Vector2(0f, 1f),
                    GroundedBranch = true,
                    GroundedFromSnapshot = i == 19,
                    GroundedAfterMove = true,
                    CollisionFlags = CollisionFlags.Below,
                    HorizontalVelocity = new Vector3(0f, 0f, 1.5f),
                    VerticalVelocity = -0.2f,
                    GroundSpeed = 1.4f,
                    ProfileHash = "ABCD1234",
                    State = LocomotionState.Walk,
                    RebaseKind = i == 19 ? (int)MovementRebaseKind.Snap : -1,
                    SnapApplied = i == 19,
                    ErrorMeters = i == 19 ? 0.782f : 0f,
                });

            Assert.That(trace.Count, Is.EqualTo(8), "容量固定");
            var recent = trace.Recent(2);
            Assert.That(recent[0].ClientTick, Is.EqualTo(19u), "最旧在前");
            Assert.That(recent[1].ClientTick, Is.EqualTo(20u));

            var line = MovementStepTrace.Format(recent[1]);
            foreach (var key in new[]
            {
                "[MoveStep] ", "role=c", "tick=20", "epoch=2", "conn=0", "obj=0",
                "in=", "sp=0", "jp=0", "yw=0.00", "pt=0.00", "g=1S", "cflags=Below",
                "root=", "move=", "smooth=", "vel=", "gs=", "coy=", "land=", "gait=", "st=Walk", "si=0",
                "rcl=", "snap=Snap", "err=0.782", "from=", "to=", "hash=ABCD1234",
            })
                Assert.That(line, Does.Contain(key), $"模拟步取证行缺少字段 {key}：{line}");
        }
    }
}
