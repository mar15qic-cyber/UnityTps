using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Owner 校正决策回归（审计 2026-09-15 §2/§4）：连续 ACK 不得把同一段过时误差重复施加。
    /// 与 SmoothCorrectionLedgerTests（纯台账语义）互补——本套件驱动 PlayerNetworkAdapter 的
    /// ApplyOwnerAuthoritativeState 决策链，锁"台账 + remaining 重算"的接线正确性。
    /// </summary>
    public sealed class PlayerNetworkAdapterReconcileTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<GameObject> _spawned = new();

        private (PlayerNetworkAdapter adapter, Locomotor locomotor, Transform root) Spawn(Vector3 position)
        {
            var go = new GameObject("Owner_" + _spawned.Count);
            go.transform.position = position;
            _spawned.Add(go);
            go.AddComponent<CharacterController>();
            var locomotor = go.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.PredictedOwner);
            typeof(Locomotor).GetMethod("Awake", NonPublic)?.Invoke(locomotor, null);
            var adapter = go.AddComponent<PlayerNetworkAdapter>();
            typeof(PlayerNetworkAdapter).GetField("_locomotor", NonPublic)?.SetValue(adapter, locomotor);
            return (adapter, locomotor, go.transform);
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

        private static MovementSnapshot SnapshotAt(uint tick, Vector3 position)
        {
            return new MovementSnapshot
            {
                Tick = tick,
                Position = position,
                Rotation = Quaternion.identity,
                HorizontalVelocity = Vector3.zero,
                VerticalVelocity = 0f,
                LocomotionState = LocomotionState.Idle,
                GaitPhase = 0f,
            };
        }

        private static AuthoritativeMovementState State(uint serverTick, uint ack, Vector3 position)
            => new AuthoritativeMovementState
            {
                ServerTick = serverTick,
                LastClientTick = ack,
                Dead = false,
                Snapshot = SnapshotAt(ack, position),
            };

        private static void Feed(PlayerNetworkAdapter adapter, in AuthoritativeMovementState state)
        {
            typeof(PlayerNetworkAdapter).GetMethod("ApplyOwnerAuthoritativeState", NonPublic)
                ?.Invoke(adapter, new object[] { state });
        }

        private static T Private<T>(object target, string field)
            => (T)target.GetType().GetField(field, NonPublic).GetValue(target);

        private static void SetPrivate(object target, string field, object value)
            => target.GetType().GetField(field, NonPublic)?.SetValue(target, value);

        /// <summary>建立"配对预测快照"：ACK 前进时按 LastClientTick 精确配对（缺快照=保守硬对位）。</summary>
        private static void SeedPredicted(PlayerNetworkAdapter adapter, uint tick, Vector3 position)
        {
            var buffer = Private<PredictionBuffer>(adapter, "_buffer");
            buffer.RecordPredicted(SnapshotAt(tick, position));
        }

        private static Vector3 Remaining(PlayerNetworkAdapter adapter)
            => Private<Vector3>(adapter, "_smoothOffsetRemaining");

        private static bool SmoothActive(PlayerNetworkAdapter adapter)
            => Private<bool>(adapter, "_smoothActive");

        /// <summary>
        /// 审计 §2 最小反例的接线版：tick12 已施加 +0.1m 校正；ACK10 与 ACK11 的配对快照都早于
        /// tick12（原始误差都是 +0.1m）。两次决策都必须扣掉这条在途量 → remaining 恒为 0。
        /// 旧实现（求和后整体清账）第二张 ACK 拿不到在途量，会把 +0.1m 再施加一次 = 前进被拽回。
        /// </summary>
        [Test]
        public void ConsecutiveAcks_OlderThanInFlightStep_DoNotReapplyCorrection()
        {
            var (adapter, _, root) = Spawn(new Vector3(0f, 0f, 0f));
            var clientPosition = root.position;

            // 初始化对位（LastClientTick==0 的首次权威快照）
            Feed(adapter, State(serverTick: 10, ack: 0, clientPosition));
            SetPrivate(adapter, "_localTick", 12u);

            var ledger = Private<SmoothCorrectionLedger>(adapter, "_correctionLedger");
            ledger.Record(12, new Vector3(0.1f, 0f, 0f)); // tick12 已施加的平滑步进（在途）

            // ACK10：配对快照早于 tick12，原始误差 +0.1m → 扣除在途后应为 0
            SeedPredicted(adapter, 10u, clientPosition);
            Feed(adapter, State(serverTick: 11, ack: 10, clientPosition + new Vector3(0.1f, 0f, 0f)));
            Assert.That(Remaining(adapter).magnitude, Is.LessThan(1e-4f), "ACK10：在途已扣，不得再施加");
            Assert.That(SmoothActive(adapter), Is.False, "扣除后无剩余偏移 → 停用平滑");
            Assert.That(ledger.Count, Is.EqualTo(1), "在途条目必须保留到 ACK 追上 tick12");

            // ACK11：同一段原始误差再次出现，仍必须扣除同一条在途量 → 依然不得施加
            SeedPredicted(adapter, 11u, clientPosition);
            Feed(adapter, State(serverTick: 12, ack: 11, clientPosition + new Vector3(0.1f, 0f, 0f)));
            Assert.That(Remaining(adapter).magnitude, Is.LessThan(1e-4f),
                "ACK11：同一条在途必须继续扣除（旧实现会再施加 +0.1m → 实机拽回）");
            Assert.That(SmoothActive(adapter), Is.False);

            // 位置未被写坏：两次决策都没有对模拟根做任何位移
            Assert.That(Vector3.Distance(root.position, clientPosition), Is.LessThan(1e-4f),
                "纯决策不得改动模拟根位置");
        }

        /// <summary>
        /// 审计 §2：误差已进噪声带（None）时不得继续消费旧纠偏——必须按当前 ACK 重算 remaining
        /// （在途已超调时给出反向偏移把它收回来），而不是保留旧 remaining 继续施加。
        /// </summary>
        [Test]
        public void ErrorInsideNoiseBand_RecomputesRemaining_InsteadOfConsumingStaleOffset()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            var clientPosition = root.position;

            Feed(adapter, State(serverTick: 10, ack: 0, clientPosition));
            SetPrivate(adapter, "_localTick", 40u);

            var ledger = Private<SmoothCorrectionLedger>(adapter, "_correctionLedger");
            ledger.Record(42, new Vector3(0.1f, 0f, 0f)); // 已施加 0.1m，但误差其实只剩 0.01m → 超调

            SeedPredicted(adapter, 41u, clientPosition);
            Feed(adapter, State(serverTick: 50, ack: 41, clientPosition + new Vector3(0.01f, 0f, 0f)));

            // corrected = 0.01 - 0.1 = -0.09 → 超出噪声带，必须以反向偏移收敛（旧实现保留旧 remaining 不动）
            Assert.That(SmoothActive(adapter), Is.True, "误差进噪声带但存在超调时仍要收敛");
            Assert.That(Remaining(adapter).x, Is.EqualTo(-0.09f).Within(1e-3f),
                "remaining 必须按当前 ACK 重算（含在途扣除），不能继续消费旧纠偏");
        }

        /// <summary>
        /// 审计 2026-09-17 D1（Critical）接线反例：raw=0.78 恰在 0.75 之上，但 0.20m 是 tick12
        /// 已施加、尚未被配对快照计入的在途平滑步进——真实残余 0.58 落平滑带：决策链必须走
        /// Smooth 而不是硬对位（旧实现 raw 直判 Snap 且跳过台账消费 + Invalidate 重积，
        /// 形成"从不进平滑带 + err 挤在阈值上方 + 一旦开始停不下来"的 SNAP 风暴）。
        /// </summary>
        [Test]
        public void NearThresholdRawError_WithInFlightStep_DecidesSmoothNotSnap()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            var clientPosition = root.position;

            Feed(adapter, State(serverTick: 10, ack: 0, clientPosition)); // 初始对位
            SetPrivate(adapter, "_localTick", 12u);
            int snapsBefore = Private<int>(adapter, "_snapCount");

            var ledger = Private<SmoothCorrectionLedger>(adapter, "_correctionLedger");
            ledger.Record(12, new Vector3(0.20f, 0f, 0f)); // tick12 已施加的在途平滑步进

            SeedPredicted(adapter, 10u, clientPosition);
            var serverPosition = clientPosition + new Vector3(0.78f, 0f, 0f);
            Feed(adapter, State(serverTick: 11, ack: 10, serverPosition));

            Assert.That(SmoothActive(adapter), Is.True, "raw 0.78 − inFlight 0.20 = 0.58 ∈ 平滑带 → 必须平滑收敛");
            Assert.That(Remaining(adapter).magnitude, Is.EqualTo(0.58f).Within(1e-3f),
                "remaining = corrected（raw − 在途）");
            Assert.That(Private<int>(adapter, "_snapCount"), Is.EqualTo(snapsBefore),
                "贴近阈值的误差经在途扣除后不得硬对位");
            Assert.That(Vector3.Distance(root.position, clientPosition), Is.LessThan(1e-4f),
                "Smooth 决策不得位移模拟根");
            var diag = Private<MovementDiagnostics>(adapter, "_diag");
            Assert.That(diag.LastErrorMeters, Is.EqualTo(0.58f).Within(1e-3f), "err= corrected 口径");
            Assert.That(diag.LastRawErrorMeters, Is.EqualTo(0.78f).Within(1e-3f), "errRaw= raw 口径");
        }

        /// <summary>台账溢出（ACK 长期不推进）：必须硬重基——清账 + 强制对位权威快照并清空 ACK 基线。</summary>
        [Test]
        public void LedgerOverflow_ForcesHardRebase()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            var drifted = new Vector3(3f, 0f, 3f);
            root.position = drifted;

            Feed(adapter, State(serverTick: 10, ack: 0, root.position));
            SetPrivate(adapter, "_localTick", 70u);

            var ledger = Private<SmoothCorrectionLedger>(adapter, "_correctionLedger");
            for (int i = 0; i < SmoothCorrectionLedger.MaxEntries + 1; i++)
                ledger.Record((uint)(60 + i), Vector3.one * 0.01f);
            Assert.That(ledger.Overflowed, Is.True, "超限后粘滞标记置位");

            var authoritative = new Vector3(20f, 0f, -20f);
            SeedPredicted(adapter, 71u, drifted);
            Feed(adapter, State(serverTick: 80, ack: 71, authoritative));

            Assert.That(Vector3.Distance(root.position, authoritative), Is.LessThan(1e-3f),
                "溢出必须硬重基到权威快照");
            Assert.That(ledger.Overflowed, Is.False, "硬重基（Invalidate）后溢出标记复位");
            Assert.That(Remaining(adapter).magnitude, Is.LessThan(1e-4f), "硬重基清空平滑偏移");
        }

        // ---- 2026-09-17 审计 R4/R5：结构守卫 ----

        /// <summary>
        /// R4：WireServerTick 必须幂等——同实例重复 OnStartServer（接管/网络重启）不得双重订阅
        /// （双重订阅 = 每 tick 双消费命令 + 双发快照，等价时间轴压缩一半）。
        /// EditMode 无 TimeManager：锁旗标机制（真实订阅由"旗标前置 +="保证）。
        /// </summary>
        [Test]
        public void WireServerTick_Twice_RemainsSingleSubscription_ByFlag()
        {
            var (adapter, _, _) = Spawn(Vector3.zero);
            var wire = typeof(PlayerNetworkAdapter).GetMethod("WireServerTick", NonPublic);
            var unwire = typeof(PlayerNetworkAdapter).GetMethod("UnwireServerTick", NonPublic);

            wire.Invoke(adapter, null);
            wire.Invoke(adapter, null);
            Assert.That(Private<bool>(adapter, "_serverTickWired"), Is.False,
                "无 TimeManager：空操作不置位（真实 Wire 时仍可生效）");

            SetPrivate(adapter, "_serverTickWired", true); // 模拟已订阅态
            wire.Invoke(adapter, null);
            Assert.That(Private<bool>(adapter, "_serverTickWired"), Is.True, "已置位时重复 Wire 直接返回");

            unwire.Invoke(adapter, null);
            Assert.That(Private<bool>(adapter, "_serverTickWired"), Is.False,
                "解绑复位旗标（不留滞留旗标封死下次 Wire）");
        }

        /// <summary>
        /// R5：重放因命令缺口中断时，tick 基线必须等于**实际重放完成**的 replayed，且缺口上方
        /// 命令/预测快照一并作废——旧 `max(replayed, newest)` 会声称"已模拟到 newest"而实际
        /// 只模拟到 replayed（逻辑 tick 与物理状态脱节、静默持续）。当前命令连续性不变量下缺口
        /// 不可达（TryStore 单调 / PruneUpTo 只删 ≤ack / 容量 128≫lead），本用例直接注入缺口锁守卫语义。
        /// </summary>
        [Test]
        public void HardSnapTo_ReplayGap_RebasesLocalTickToReplayed_AndInvalidatesAbove()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            var clientPosition = root.position;

            Feed(adapter, State(serverTick: 10, ack: 0, clientPosition)); // 初始对位
            var buffer = Private<PredictionBuffer>(adapter, "_buffer");

            buffer.TryStore(new MovementCommand(Vector2.zero, false, false, 0f, 0f, 11));
            buffer.TryStore(new MovementCommand(Vector2.zero, false, false, 0f, 0f, 13)); // 12 = 缺口
            buffer.RecordPredicted(SnapshotAt(11, clientPosition));
            buffer.RecordPredicted(SnapshotAt(13, clientPosition));
            SetPrivate(adapter, "_localTick", 13u);

            typeof(PlayerNetworkAdapter).GetMethod("HardSnapTo", NonPublic)
                .Invoke(adapter, new object[] { State(serverTick: 11, ack: 10, clientPosition), MovementRebaseKind.Snap, 0f });

            Assert.That(Private<uint>(adapter, "_localTick"), Is.EqualTo(11u),
                "tick 基线 = 实际重放完成的最大 tick（不得声称未模拟区间）");
            Assert.That(buffer.TryGetCommand(13, out _), Is.False, "缺口上方命令作废");
            Assert.That(buffer.TryGetPredicted(13, out _), Is.False, "缺口上方陈旧预测快照作废");
            Assert.That(buffer.NewestTick, Is.EqualTo(11u));
            Assert.That(buffer.TryStore(new MovementCommand(Vector2.zero, false, false, 0f, 0f, 12)),
                Is.True, "回退后新 tick 12 可重新入缓冲（不撞旧命令）");
        }

        /// <summary>
        /// 渲染位置插值（审计 §5.4）：视觉偏移写在专用节点上，模拟根不动；跨渲染帧连续（无跳变）。
        /// 用真实层级（根 + CameraPivot）驱动 EnsureViewOffsetRoot/UpdateOwnerVisualPosition。
        /// </summary>
        [Test]
        public void RenderInterpolation_MovesViewNodeOnly_AndStaysContinuous()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            var pivotGo = new GameObject("CameraPivot");
            _spawned.Add(pivotGo);
            pivotGo.transform.SetParent(root, false);
            pivotGo.transform.localPosition = new Vector3(0f, 1.62f, 0f);
            SetPrivate(adapter, "localOnlyRoot", pivotGo);

            var ensure = typeof(PlayerNetworkAdapter).GetMethod("EnsureViewOffsetRoot", NonPublic);
            var update = typeof(PlayerNetworkAdapter).GetMethod("UpdateOwnerVisualPosition", NonPublic);
            Assert.That((bool)ensure.Invoke(adapter, null), Is.True, "视觉偏移节点必须可创建");
            var node = root.Find("ViewSmoothingRoot");
            Assert.That(node, Is.Not.Null, "运行时创建专用视觉偏移节点");
            Assert.That(pivotGo.transform.parent, Is.EqualTo(node), "CameraPivot 挂到偏移节点下（单写者隔离）");
            Assert.That(pivotGo.transform.localPosition, Is.EqualTo(new Vector3(0f, 1.62f, 0f)),
                "重挂父级必须保持局部姿态");

            // 一个模拟 tick 的位移 = 10cm（世界 Z），累积器余量按 1/4 tick 推进
            var accumulator = Private<TickAccumulator>(adapter, "_accumulator");
            float fixedDelta = Private<float>(adapter, "_fixedDelta");
            SetPrivate(adapter, "_interpFrom", Vector3.zero);
            SetPrivate(adapter, "_interpTo", new Vector3(0f, 0f, 0.1f));
            SetPrivate(adapter, "_interpValid", true);

            var simPosition = root.position;
            float previousZ = pivotGo.transform.position.z;
            float maxStep = 0f;
            for (int frame = 0; frame < 3; frame++)
            {
                accumulator.Advance(fixedDelta * 0.25, fixedDelta, 5);
                update.Invoke(adapter, null);
                float z = pivotGo.transform.position.z;
                Assert.That(z, Is.GreaterThanOrEqualTo(previousZ - 1e-4f), $"第 {frame} 帧：渲染位置必须单调前进");
                maxStep = Mathf.Max(maxStep, Mathf.Abs(z - previousZ));
                previousZ = z;
            }
            Assert.That(maxStep, Is.LessThan(0.05f), "单渲染帧位移远小于整 tick（无阶梯跳变）");
            Assert.That(Vector3.Distance(root.position, simPosition), Is.LessThan(1e-5f),
                "视觉写入不得改动模拟根（服务器瞄准/碰撞根仍为权威值）");
            Assert.That(Private<float>(adapter, "_visualOffsetMeters"),
                Is.LessThan(MovementPredictionConfig.RenderVisualOffsetClampMeters),
                "视觉偏移在安全上限内");

            // 硬对位/死亡边界：基准作废，视觉立即回到模拟根（不做穿场插值）
            typeof(PlayerNetworkAdapter).GetMethod("ResetOwnerVisualInterpolation", NonPublic)
                .Invoke(adapter, null);
            Assert.That(node.localPosition.magnitude, Is.LessThan(1e-6f), "作废后视觉偏移归零");
            Assert.That(Private<float>(adapter, "_visualOffsetMeters"), Is.LessThan(1e-6f));
        }

        /// <summary>诊断输出是"旁观者"：脱离网络生命周期（无 NetworkObject/TimeManager）也必须安全，
        /// 绝不能因为诊断把玩法路径打断。</summary>
        [Test]
        public void EmitMovementDiagnostics_WithoutNetworkLifecycle_IsSafe()
        {
            var (adapter, _, _) = Spawn(Vector3.zero);
            var diag = Private<MovementDiagnostics>(adapter, "_diag");
            diag.NoteFrame(0.0166, 1d / 30d);

            var emit = typeof(PlayerNetworkAdapter).GetMethod("EmitMovementDiagnostics", NonPublic);
            Assert.DoesNotThrow(() => emit.Invoke(adapter, new object[] { "owner" }),
                "未初始化网络时诊断输出必须安全降级");
            Assert.That(diag.HasData, Is.False, "输出后区间计数已清空");
        }
    }
}
