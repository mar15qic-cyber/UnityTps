using System.Collections.Generic;
using Game.Gameplay.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 2026-09-13 MovementSnapshot 专项：Simulate 级回归（真实 Locomotor + CharacterController 物理）。
    /// 覆盖快照域决策无法覆盖的积分语义：同输入两端一致性（预测基线成立的前提）、跳跃/落地
    /// 相位瞬态不升级硬校正（滞回生效）、贴墙阻挡下的对位+重放收敛。
    /// 决策链组合 = Reconciler.Decide + DivergenceGate + ReconcileGate + PredictionBuffer 重放，
    /// 与 PlayerNetworkAdapter.TargetAuthoritativeState 的接线语义一一对应（RPC 层由实机矩阵验证）。
    /// </summary>
    public sealed class PredictedReplayConvergenceTests
    {
        private const float FixedDelta = 1f / 30f;
        private readonly List<GameObject> _spawned = new();

        private (Locomotor locomotor, Transform root) Spawn(Vector3 position)
        {
            // EditMode 下 AddComponent/激活都不触发普通 MonoBehaviour 的 Awake（_cc 解析依赖它，
            // SetActive 模式仅对 ExecuteAlways 组件生效）——按项目 EditMode 测试惯例反射直调 Awake
            var go = new GameObject($"Sim_{_spawned.Count}");
            go.transform.position = position;
            _spawned.Add(go);
            go.AddComponent<CharacterController>(); // 先 CC 后 Locomotor（Awake 依赖）
            var locomotor = go.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.PredictedOwner);
            typeof(Locomotor).GetMethod("Awake",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(locomotor, null);
            return (locomotor, go.transform);
        }

        private void SpawnGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(60f, 1f, 60f); // 顶面 y=0；覆盖 ±10 走廊
            _spawned.Add(ground);
        }

        private void SpawnWall(Vector3 center)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.transform.position = center;
            _spawned.Add(wall);
        }

        private static MovementCommand Cmd(uint tick, float x, float y, bool jump = false)
            => new MovementCommand(new Vector2(x, y), false, jump, 0f, 0f, tick);

        private static MovementSnapshot OverrideState(in MovementSnapshot source, Vector3 position,
            float vertical, LocomotionState state)
        {
            // C# 9 无 struct with：显式拷贝构造污染快照（仅测试注入用）
            var copy = source;
            copy.Position = position;
            copy.VerticalVelocity = vertical;
            copy.LocomotionState = state;
            return copy;
        }

        private static MovementSnapshot Shift(in MovementSnapshot s, Vector3 delta)
        {
            // 走廊平移：两端物理隔离在不同 x 走廊，权威快照平移到预测走廊后才能做时间对齐比较
            //（绝对位置差 = 走廊间距会直接落进 Snap 区间，掩盖全部软分叉域）
            var copy = s;
            copy.Position += delta;
            return copy;
        }

        private bool _autoSyncWasEnabled;

        [SetUp]
        public void SetUp()
        {
            // EditMode 不跑物理仿真步：新建静态 collider/每步移动后的 transform 不进物理 broadphase，
            // CC.Move 的碰撞查询会视为空世界（实测玩家穿地/穿墙自由落体）。开启自动同步，
            // 每次 Physics 查询（含 CC.Move）前把脏 transform 同步进物理引擎。
            _autoSyncWasEnabled = Physics.autoSyncTransforms;
            Physics.autoSyncTransforms = true;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.autoSyncTransforms = _autoSyncWasEnabled;
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        [Test]
        public void SameCommands_PredictedMatchesAuthority_Exactly()
        {
            // 预测基线成立的前提：同一命令序列在两端产生一致状态（同代码同输入），
            // 干净链路上 Reconciler 全 None、不发生任何校正。
            SpawnGround();
            // 两端实体分开放置：CharacterController 之间会互相碰撞推挤，同位重叠在失去同步后
            // 会互推产生假位移（实测 2.08m）——预测/权威语义上它们是同一实体的两份副本，
            // 物理上必须隔离（各自独立走廊）。
            var (predicted, _) = Spawn(new Vector3(-10f, 1.02f, 0f));
            var (authority, _) = Spawn(new Vector3(10f, 1.02f, 0f));

            var predictedStates = new List<MovementSnapshot>();
            var authorityStates = new List<MovementSnapshot>();
            for (uint t = 1; t <= 40; t++)
            {
                bool jump = t == 10; // 跳跃→滞空→落地后继续走
                var cmd = Cmd(t, 0f, 1f, jump);
                predicted.Simulate(cmd, FixedDelta);
                authority.Simulate(cmd, FixedDelta);
                predictedStates.Add(predicted.CaptureSnapshot());
                authorityStates.Add(authority.CaptureSnapshot());
            }

            var last = predictedStates[predictedStates.Count - 1];
            var lastAuthority = authorityStates[authorityStates.Count - 1];
            // 两端物理隔离在不同走廊：位置比较用相对各自起点的位移（走廊原点不同）
            var predictedStart = new Vector3(-10f, 1.02f, 0f);
            var authorityStart = new Vector3(10f, 1.02f, 0f);
            var predictedTravel = last.Position - predictedStart;
            var authorityTravel = lastAuthority.Position - authorityStart;
            Assert.That(predictedTravel, Is.EqualTo(authorityTravel).Within(1e-3f),
                "同输入两端位移一致");
            Assert.That(last.VerticalVelocity, Is.EqualTo(lastAuthority.VerticalVelocity).Within(1e-3f));
            Assert.That(last.LocomotionState, Is.EqualTo(lastAuthority.LocomotionState));

            // 干净链路：全部快照按 tick 对齐配对决策无分叉（权威快照平移到预测走廊）
            var corridorDelta = predictedStart - authorityStart;
            for (int i = 0; i < predictedStates.Count; i++)
            {
                var decision = Reconciler.Decide(Shift(authorityStates[i], corridorDelta), predictedStates[i], out _);
                Assert.That(decision.Action, Is.EqualTo(ReconcileAction.None),
                    $"tick {predictedStates[i].Tick}：同源快照不得触发校正");
                Assert.That(decision.SoftDivergence, Is.False, $"tick {predictedStates[i].Tick}：无域分叉");
            }
        }

        [Test]
        public void JumpLand_PhaseOffset_TransientVerticalDelta_StaysWithinTolerance()
        {
            // 客户端与服务器 1 tick 的跳跃相位差（命令晚 1 tick 到服务器）：垂直速度差
            // = 1 tick 重力积分 ≈0.73 m/s，恒在 2.0 容差内 → 滞回不升级硬校正；
            // 位置差 ~0.23m 走既有 Smooth 通道（不回滚速度/状态）。
            SpawnGround();
            var (predicted, _) = Spawn(new Vector3(-10f, 1.02f, 0f));
            var (authority, _) = Spawn(new Vector3(10f, 1.02f, 0f));

            var predictedStates = new List<MovementSnapshot>();
            var authorityStates = new List<MovementSnapshot>();
            for (uint t = 1; t <= 35; t++)
            {
                bool predictedJump = t == 10;
                bool authorityJump = t == 11; // 服务器晚 1 tick
                predicted.Simulate(Cmd(t, 0f, 1f, predictedJump), FixedDelta);
                authority.Simulate(Cmd(t, 0f, 1f, authorityJump), FixedDelta);
                predictedStates.Add(predicted.CaptureSnapshot());
                authorityStates.Add(authority.CaptureSnapshot());
            }

            var gate = new DivergenceGate();
            var corridorDelta = new Vector3(-10f, 0f, 0f) - new Vector3(10f, 0f, 0f);
            int snapUpgrades = 0;
            for (int i = 0; i < predictedStates.Count; i++)
            {
                var decision = Reconciler.Decide(Shift(authorityStates[i], corridorDelta), predictedStates[i], out _);
                if (gate.Feed(decision)) snapUpgrades++;
            }
            Assert.That(snapUpgrades, Is.EqualTo(0),
                "1 tick 跳跃相位差的垂直速度差必须在容差内，滞回不得升级硬校正");
        }

        [Test]
        public void Replay_AfterDivergence_RestoresSimStateAndConverges()
        {
            // 专项核心闭环：分叉（垂直速度反向 + 状态污染）→ 域检出 → 硬对位 + 重放未确认输入
            // → 模拟状态（速度/落地分支）恢复 + 位置收敛，后续同命令继续积分保持一致。
            SpawnGround();
            var (predicted, predictedRoot) = Spawn(new Vector3(-10f, 1.02f, 0f));
            var (authority, _) = Spawn(new Vector3(10f, 1.02f, 0f));

            var commands = new List<MovementCommand>();
            for (uint t = 1; t <= 60; t++) commands.Add(Cmd(t, 0f, 1f, t == 15));

            const uint acked = 30u;
            const uint divergeTick = 45u;
            var predictedSnapshots = new Dictionary<uint, MovementSnapshot>();
            var authoritySnapshots = new Dictionary<uint, MovementSnapshot>();
            foreach (var cmd in commands)
            {
                predicted.Simulate(cmd, FixedDelta);
                authority.Simulate(cmd, FixedDelta);
                predictedSnapshots[cmd.Tick] = predicted.CaptureSnapshot();
                authoritySnapshots[cmd.Tick] = authority.CaptureSnapshot();
            }

            // 注入分叉：预测端被污染（垂直速度反向 + 滞空状态），模拟客户端坏状态
            var cleanAt45 = predictedSnapshots[divergeTick];
            predicted.ApplyAuthoritativeSnapshot(OverrideState(cleanAt45, cleanAt45.Position, 4f, LocomotionState.Jump));

            // 权威同 tick 快照平移到预测走廊后再做时间对齐比较（走廊间距会直接落进 Snap 区间）
            var corridorDelta = new Vector3(-10f, 0f, 0f) - new Vector3(10f, 0f, 0f);
            var decision = Reconciler.Decide(Shift(authoritySnapshots[divergeTick], corridorDelta), predicted.CaptureSnapshot(), out _);
            Assert.That(decision.SoftDivergence, Is.True, "位置接近但垂直速度/状态分叉必须报域");

            // 硬对位 + 重放（Adapter HardSnapTo 语义）：对位 ack 快照 → 重放 ack+1..divergeTick
            predicted.ApplyAuthoritativeSnapshot(predictedSnapshots[acked]);
            for (uint tick = acked + 1; tick <= divergeTick; tick++)
                predicted.Simulate(commands[(int)tick - 1], FixedDelta);

            var recovered = predicted.CaptureSnapshot();
            var expected = Shift(authoritySnapshots[divergeTick], corridorDelta);
            Assert.That(recovered.VerticalVelocity, Is.EqualTo(expected.VerticalVelocity).Within(1e-2f),
                "重放后垂直速度恢复权威值");
            Assert.That(recovered.LocomotionState, Is.EqualTo(expected.LocomotionState),
                "落地/滞空分支恢复一致");
            Assert.That(Vector3.Distance(recovered.Position, expected.Position),
                Is.LessThan(MovementPredictionConfig.ReconcileSmoothMeters),
                "重放后位置回到平滑阈值内");
            Assert.That(predictedRoot.position.y, Is.LessThan(3f), "污染注入未造成失控");

            // 收敛后继续同命令积分：不再分叉
            var gate = new DivergenceGate();
            for (uint t = divergeTick + 1u; t <= 60u; t++)
            {
                var cmd = commands[(int)t - 1];
                predicted.Simulate(cmd, FixedDelta);
                authority.Simulate(cmd, FixedDelta);
                Assert.That(gate.Feed(Reconciler.Decide(Shift(authority.CaptureSnapshot(), corridorDelta), predicted.CaptureSnapshot(), out _)),
                    Is.False, $"tick {t}：收敛后不得再升级硬校正");
            }
        }

        [Test]
        public void Replay_AgainstWall_ConvergesWithAuthorityBlocking()
        {
            // 贴墙移动：CC 阻挡是两端共同的确定性约束——重放后在墙面前收敛到权威位置，
            // 不穿透、不残留法向偏差。
            SpawnGround();
            var (predicted, _) = Spawn(new Vector3(-10f, 1.02f, 0f));
            var (authority, _) = Spawn(new Vector3(10f, 1.02f, 0f));
            SpawnWall(new Vector3(-10f, 0.5f, 1.2f)); // 预测端走廊的墙
            SpawnWall(new Vector3(10f, 0.5f, 1.2f));  // 权威端走廊的墙（墙前 0.7m 接触）

            var commands = new List<MovementCommand>();
            for (uint t = 1; t <= 50; t++) commands.Add(Cmd(t, 0f, 1f)); // 持续向墙走

            const uint acked = 25u;
            var predictedSnapshots = new Dictionary<uint, MovementSnapshot>();
            var authoritySnapshots = new Dictionary<uint, MovementSnapshot>();
            foreach (var cmd in commands)
            {
                predicted.Simulate(cmd, FixedDelta);
                authority.Simulate(cmd, FixedDelta);
                predictedSnapshots[cmd.Tick] = predicted.CaptureSnapshot();
                authoritySnapshots[cmd.Tick] = authority.CaptureSnapshot();
            }

            // 注入预测端位置误差（+0.3m，Snap 区间）
            predicted.ApplyAuthoritativeSnapshot(OverrideState(
                predictedSnapshots[50u], predictedSnapshots[50u].Position + new Vector3(0.3f, 0f, 0f),
                predictedSnapshots[50u].VerticalVelocity, predictedSnapshots[50u].LocomotionState));

            predicted.ApplyAuthoritativeSnapshot(predictedSnapshots[acked]);
            for (uint tick = acked + 1; tick <= 50u; tick++)
                predicted.Simulate(commands[(int)tick - 1], FixedDelta);

            var recovered = predicted.CaptureSnapshot();
            var expected = Shift(authoritySnapshots[50u], new Vector3(-20f, 0f, 0f));
            Assert.That(Vector3.Distance(recovered.Position, expected.Position),
                Is.LessThan(MovementPredictionConfig.ReconcileSmoothMeters),
                "贴墙场景重放收敛到权威位置");
            Assert.That(recovered.Position.z, Is.LessThan(1.7f), "重放不得穿透墙面");
        }
    }
}
