using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 审计 2026-09-16 C1/C2/C3/M3 定向回归（Owner 表现层与重基语义）：
    /// ① C2：一帧多步时插值端点必须只覆盖**最后一步**（旧实现记整帧首尾 → 位置白落后/单步多步交替变速）；
    /// ② C1：渲染视角 yaw 逐帧、只写视觉节点（绕 Y 旋转不移动轴上 pivot、不改权威根）；
    /// ③ C1：tick 提交时按"实际写入权威根的角度增量"扣减视角超前量；
    /// ④ C3：普通权威重基保留"纠偏前视觉世界位姿"（画面连续），真传送才清历史；
    /// ⑤ M3：每种重基都要计入诊断，且回拉取证被记录。
    /// </summary>
    public sealed class PlayerVisualContinuityTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<GameObject> _spawned = new();

        private float FixedDelta => Private<float>(_adapter, "_fixedDelta");
        private PlayerNetworkAdapter _adapter;

        private (PlayerNetworkAdapter adapter, Transform root, Transform pivot, Transform node) Spawn()
        {
            var go = new GameObject("Owner_" + _spawned.Count);
            go.transform.position = Vector3.zero;
            _spawned.Add(go);
            go.AddComponent<CharacterController>();
            var locomotor = go.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.PredictedOwner);
            typeof(Locomotor).GetMethod("Awake", NonPublic)?.Invoke(locomotor, null);
            var adapter = go.AddComponent<PlayerNetworkAdapter>();
            typeof(PlayerNetworkAdapter).GetField("_locomotor", NonPublic)?.SetValue(adapter, locomotor);

            var pivotGo = new GameObject("CameraPivot");
            _spawned.Add(pivotGo);
            pivotGo.transform.SetParent(go.transform, false);
            pivotGo.transform.localPosition = new Vector3(0f, 1.62f, 0f);
            typeof(PlayerNetworkAdapter).GetField("localOnlyRoot", NonPublic)?.SetValue(adapter, pivotGo);

            _adapter = adapter;
            Assert.That((bool)Invoke(adapter, "EnsureViewOffsetRoot"), Is.True, "视觉偏移节点必须可创建");
            var node = go.transform.Find("ViewSmoothingRoot");
            Assert.That(node, Is.Not.Null);
            return (adapter, go.transform, pivotGo.transform, node);
        }

        private static object Invoke(object target, string method, params object[] args)
            => target.GetType().GetMethod(method, NonPublic).Invoke(target, args);

        private static void Set(object target, string field, object value)
            => target.GetType().GetField(field, NonPublic)?.SetValue(target, value);

        private static T Private<T>(object target, string field)
            => (T)target.GetType().GetField(field, NonPublic).GetValue(target);

        private static MovementSnapshot SnapshotAt(uint tick, Vector3 position) => new MovementSnapshot
        {
            Tick = tick,
            Position = position,
            Rotation = Quaternion.identity,
            HorizontalVelocity = Vector3.zero,
            VerticalVelocity = 0f,
            LocomotionState = LocomotionState.Idle,
            GaitPhase = 0f,
            Grounded = true,
            CoyoteTimer = 0.12f,
        };

        private static AuthoritativeMovementState State(uint serverTick, uint ack, Vector3 position, bool dead = false)
            => new AuthoritativeMovementState
            {
                ServerTick = serverTick,
                LastClientTick = ack,
                Dead = dead,
                Snapshot = SnapshotAt(ack, position),
            };

        private static void Feed(PlayerNetworkAdapter adapter, in AuthoritativeMovementState state)
            => Invoke(adapter, "ApplyOwnerAuthoritativeState", state);

        private static void SeedPredicted(PlayerNetworkAdapter adapter, uint tick, Vector3 position)
            => Private<PredictionBuffer>(adapter, "_buffer").RecordPredicted(SnapshotAt(tick, position));

        [SetUp]
        public void SetUp() => Physics.autoSyncTransforms = true;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
            _adapter = null;
        }

        /// <summary>
        /// 审计 2026-09-16 §3.3：入场期累积的输入领先必须在**首个权威快照**上收敛——
        /// 否则 local−ack 会永久停在 1 秒量级（服务器位姿/回滚窗口系统性落后于客户端画面）。
        /// 只做一次；领先不大时不干预（不制造无谓的对位）。
        /// </summary>
        [Test]
        public void StartupLead_IsAlignedOnFirstAuthoritativeSnapshot_Once()
        {
            var (adapter, root, _, _) = Spawn();
            Set(adapter, "_localTick", 90u);       // 模拟连接/加载期间累积的预测 tick
            Set(adapter, "_predictionAligned", false);

            Feed(adapter, State(serverTick: 500, ack: 12, root.position));

            Assert.That(Private<uint>(adapter, "_localTick"), Is.EqualTo(12u),
                "首个权威快照必须把客户端 tick 基线对齐到服务器 ACK（消除入场领先）");
            Assert.That(Private<bool>(adapter, "_predictionAligned"), Is.True);

            // 第二次快照（领先已在合理范围）：不得再次强制对位
            Set(adapter, "_localTick", 20u);
            SeedPredicted(adapter, 19u, root.position);
            SeedPredicted(adapter, 20u, root.position);
            Feed(adapter, State(serverTick: 520, ack: 19, root.position));
            Assert.That(Private<uint>(adapter, "_localTick"), Is.EqualTo(20u),
                "对齐只发生一次：后续快照按常规 ACK 门处理");
        }

        /// <summary>
        /// 2026-09-16 实测（用户"按 W 向左偏"）：视角 yaw 超前量的扣减基准必须是**提交的输入**
        /// （cmd.YawDelta），不是身体实际旋转。被后坐补偿债务吃掉的输入如果留在超前量里，
        /// 每压枪一次泄漏一点，累积成 4-5° 永久视角/身体夹角（对着墙按 W 会沿身体方向滑出墙外）。
        /// </summary>
        [Test]
        public void ViewYawLead_DoesNotLeakRecoilCompensatedInput()
        {
            var (adapter, root, _, _) = Spawn();
            var controller = root.gameObject.AddComponent<WeaponController>();
            var locomotor = Private<Locomotor>(adapter, "_locomotor");
            typeof(Locomotor).GetField("_weaponController", NonPublic).SetValue(locomotor, controller);
            controller.RestoreRecoilCompensationDebt(new Vector2(0f, 2f)); // 2° 向右的后坐债

            // 模拟 Update 已累积 -3° 视角输入（玩家向左压枪），tick 命令同样携带 -3°
            Set(adapter, "_viewYawDegrees", -3f);
            Set(adapter, "_pendingYaw", -3f);
            adapter.TestPredictionDeltaTime = FixedDelta;
            Invoke(adapter, "RunOwnerPrediction");

            // 身体实际只转 -1°（2° 被债务吃掉）——这是压枪的正确语义
            Assert.That(Mathf.DeltaAngle(root.eulerAngles.y, -1f), Is.LessThan(0.01f),
                "身体旋转 = 输入 − 债务消费");
            // 超前量必须归零：被吃掉的输入不得留在视角里成为永久夹角
            Assert.That(Mathf.Abs(Private<float>(adapter, "_viewYawDegrees")), Is.LessThan(0.01f),
                "旧实现按'实际旋转'扣减会把 2° 债务消费泄漏成永久视角偏移（左偏根因）");
        }

        /// <summary>C2：一帧模拟两步时，插值端点必须覆盖"最后一步"，而不是整帧首尾。</summary>
        [Test]
        public void MultiStepFrame_InterpEndpointsCoverLastStepOnly()
        {
            var (adapter, root, _, _) = Spawn();
            adapter.TestPredictionDeltaTime = 2f * FixedDelta; // 强制一帧两步（其余时间余量恰好清空）

            Vector3 before = root.position;
            Invoke(adapter, "RunOwnerPrediction");
            Vector3 after = root.position;

            var from = Private<Vector3>(adapter, "_interpFrom");
            var to = Private<Vector3>(adapter, "_interpTo");
            float frameMove = Vector3.Distance(after, before);
            float span = Vector3.Distance(to, from);

            Assert.That(Private<bool>(adapter, "_interpValid"), Is.True);
            Assert.That(Vector3.Distance(to, after), Is.LessThan(1e-4f), "端点终点=当前模拟根");
            Assert.That(Vector3.Distance(from, before), Is.GreaterThan(1e-5f),
                "端点起点必须是最后一步之前（旧实现=整帧首帧位置）");
            Assert.That(span, Is.LessThan(frameMove * 0.75f),
                "端点跨度必须只覆盖最后一步，不是整帧位移（旧实现 span==frameMove）");
        }

        /// <summary>C1：视角 yaw 只写视觉节点——绕 Y 旋转不移动轴上 pivot，也不改权威根。</summary>
        [Test]
        public void ViewYawOffset_RotatesViewNodeOnly()
        {
            var (adapter, root, pivot, node) = Spawn();
            Private<TickAccumulator>(adapter, "_accumulator").Advance(FixedDelta * 0.5f, FixedDelta, 5);
            Set(adapter, "_viewYawDegrees", 7.5f);

            Invoke(adapter, "UpdateOwnerVisualPosition");

            Assert.That(Quaternion.Angle(node.localRotation, Quaternion.Euler(0f, 7.5f, 0f)),
                Is.LessThan(0.01f), "视觉节点承载逐帧视角 yaw");
            Assert.That(Vector3.Distance(pivot.position, new Vector3(0f, 1.62f, 0f)),
                Is.LessThan(1e-3f), "绕 Y 旋转不改变（Y 轴上）pivot 位置");
            Assert.That(Quaternion.Angle(root.rotation, Quaternion.identity),
                Is.LessThan(0.01f), "权威根旋转不被视角偏移写入");
        }

        /// <summary>C1：tick 提交时用"实际写入权威根的角度增量"扣减视角超前量（含后坐补偿消耗）。</summary>
        [Test]
        public void TickCommit_DeductsAppliedBodyYawFromViewLead()
        {
            var (adapter, root, _, _) = Spawn();
            Set(adapter, "_pendingYaw", 3f);
            Set(adapter, "_viewYawDegrees", 4f);
            adapter.TestPredictionDeltaTime = FixedDelta;
            Set(adapter, "_ticksSinceSend", 0);

            Invoke(adapter, "RunOwnerPrediction");

            Assert.That(Mathf.DeltaAngle(0f, root.eulerAngles.y), Is.EqualTo(3f).Within(0.05f),
                "权威身体按 tick 提交转动");
            Assert.That(Private<float>(adapter, "_viewYawDegrees"), Is.EqualTo(1f).Within(0.05f),
                "视角超前量扣除实际写入根的角度（4 - 3 = 1）");
        }

        /// <summary>C3：普通权威重基（纠偏）保留纠偏前的视觉世界位姿，画面不跳变。</summary>
        [Test]
        public void NormalRebase_PreservesPreviousVisualWorldPosition()
        {
            var (adapter, root, _, node) = Spawn();
            Set(adapter, "_visualCarryWorld", new Vector3(0.2f, 0f, 0f));
            Invoke(adapter, "UpdateOwnerVisualPosition");
            Vector3 visualBefore = node.position;
            Assert.That(Vector3.Distance(visualBefore, root.position), Is.GreaterThan(0.15f), "先制造视觉残差");

            // 普通纠偏：权威位置前移 0.4m（< 传送阈值）
            var authoritative = new Vector3(0.4f, 0f, 0f);
            Feed(adapter, State(serverTick: 10, ack: 0, authoritative));

            Assert.That(Vector3.Distance(root.position, authoritative), Is.LessThan(1e-3f), "模拟根对位权威");
            Assert.That(Vector3.Distance(node.position, visualBefore), Is.LessThan(5e-3f),
                "视觉世界位姿保持（残差衰减起点=纠偏前位姿，而非瞬移）");
            Assert.That(Private<Vector3>(adapter, "_visualCarryWorld").magnitude, Is.GreaterThan(0.001f),
                "残差保留待衰减");
        }

        /// <summary>C3：真传送（位移超阈值）必须清视觉历史，不跨图滑动。</summary>
        [Test]
        public void TeleportSizedRebase_ClearsVisualHistory()
        {
            var (adapter, root, _, node) = Spawn();
            Set(adapter, "_visualCarryWorld", new Vector3(0.2f, 0f, 0f));
            Invoke(adapter, "UpdateOwnerVisualPosition");

            var far = new Vector3(20f, 0f, -20f); // 远超 RebaseVisualCarryMaxMeters
            Feed(adapter, State(serverTick: 10, ack: 0, far));

            Assert.That(Vector3.Distance(root.position, far), Is.LessThan(1e-3f));
            Assert.That(Vector3.Distance(node.position, root.position), Is.LessThan(1e-3f),
                "真传送必须清残差（视觉与根同位，不滑过地图）");
            Assert.That(Private<Vector3>(adapter, "_visualCarryWorld").magnitude, Is.LessThan(1e-4f));
        }

        /// <summary>
        /// M3：每一种重基都要计入诊断（旧实现只统计 SNAP/分叉/溢出，`snaps=0` 会被误读为"没有任何对位"）。
        /// 覆盖：首次对位 / 历史缺失 / 位置 SNAP / 死亡-重生。
        /// </summary>
        [Test]
        public void EveryRebaseKind_IsCounted()
        {
            var (adapter, root, _, _) = Spawn();
            var diag = Private<MovementDiagnostics>(adapter, "_diag");

            Feed(adapter, State(serverTick: 10, ack: 0, root.position)); // 首次对位
            Assert.That(diag.RebaseInitial, Is.EqualTo(1));

            Feed(adapter, State(serverTick: 20, ack: 5, root.position)); // ACK 前进但无配对历史
            Assert.That(diag.RebaseHistoryMissing, Is.EqualTo(1));

            SeedPredicted(adapter, 9u, root.position);
            Feed(adapter, State(serverTick: 30, ack: 9, root.position + new Vector3(1.5f, 0f, 0f))); // > SNAP 阈值
            Assert.That(diag.RebaseSnap, Is.EqualTo(1));

            Feed(adapter, State(serverTick: 40, ack: 9, root.position, dead: true));
            Feed(adapter, State(serverTick: 41, ack: 9, root.position, dead: true)); // 重复死亡快照不重复计数
            Assert.That(diag.RebaseDeathRespawn, Is.EqualTo(1), "死亡边界只计一次");

            Feed(adapter, State(serverTick: 60, ack: 9, root.position + new Vector3(3f, 0f, 3f)));
            Assert.That(diag.RebaseDeathRespawn, Is.EqualTo(2), "重生边界也计入（真传送）");
            Assert.That(diag.RebaseTotal, Is.EqualTo(5), "总计数=全部重基来源");
            Assert.That(diag.RebaseSnap, Is.EqualTo(1), "SNAP 只计真正的位置硬校正");
        }

        /// <summary>M3：回拉取证必须被记录（同 tick 的前后根位 + 最终渲染位），供"前进却向后"归因。</summary>
        [Test]
        public void PullbackTrace_RecordsWriterChainPerTick()
        {
            var (adapter, root, _, node) = Spawn();
            adapter.TestPredictionDeltaTime = FixedDelta;
            Set(adapter, "_ticksSinceSend", 0);

            Invoke(adapter, "RunOwnerPrediction");
            Invoke(adapter, "UpdateOwnerVisualPosition");
            Invoke(adapter, "RecordPullbackTrace");

            var trace = Private<MovementPullbackTrace>(adapter, "_pullbackTrace");
            Assert.That(trace.Count, Is.EqualTo(1), "每个模拟 tick 记录一条写者链");
            var samples = trace.RecentSamples(1);
            Assert.That(samples.Count, Is.EqualTo(1));
            Assert.That(Vector3.Distance(samples[0].RootAfterAll, root.position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(samples[0].RenderNow, node.position), Is.LessThan(1e-3f));
            Assert.That(samples[0].ClientTick, Is.GreaterThan(0u));
        }
    }
}
