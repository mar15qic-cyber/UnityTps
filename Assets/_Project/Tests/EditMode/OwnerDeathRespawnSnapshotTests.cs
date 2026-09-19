using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 死亡/重生权威快照处理回归（2026-09-15 死亡重生票据）：
    /// PlayerNetworkAdapter.ApplyOwnerAuthoritativeState 的死亡边界与重生边界语义——
    /// 死亡后 Owner 不再上行输入，服务器快照的 LastClientTick 与死亡快照相同是常态，
    /// 因此死亡/重生边界必须绕过「重复 ACK 忽略」门，否则玩家会卡在死亡点（不重生 / 瞬移回死亡点）。
    /// 用真实 Locomotor + CharacterController 直接驱动快照入口（RPC 包装层由双端实机矩阵验证）。
    /// </summary>
    public sealed class OwnerDeathRespawnSnapshotTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<GameObject> _spawned = new();

        private (PlayerNetworkAdapter adapter, Locomotor locomotor, Transform root) Spawn(Vector3 position)
        {
            // EditMode 下 AddComponent/激活不触发普通 MonoBehaviour 的 Awake（_cc 解析依赖它）——
            // 按项目 EditMode 测试惯例反射直调 Awake；PlayerNetworkAdapter 的私有引用同样反射注入，
            // 只驱动快照决策链本身（不启动 FishNet 生命周期）。
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

        private static MovementSnapshot SnapshotAt(Vector3 position, LocomotionState state = LocomotionState.Idle)
        {
            return new MovementSnapshot
            {
                Position = position,
                Rotation = Quaternion.identity,
                HorizontalVelocity = Vector3.zero,
                VerticalVelocity = 0f,
                LocomotionState = state,
                GaitPhase = 0f,
            };
        }

        private static AuthoritativeMovementState State(uint serverTick, uint ack, bool dead, Vector3 position)
            => new AuthoritativeMovementState
            {
                ServerTick = serverTick,
                LastClientTick = ack,
                Dead = dead,
                Snapshot = SnapshotAt(position),
            };

        private static void Feed(PlayerNetworkAdapter adapter, in AuthoritativeMovementState state)
        {
            typeof(PlayerNetworkAdapter).GetMethod("ApplyOwnerAuthoritativeState", NonPublic)
                ?.Invoke(adapter, new object[] { state });
        }

        private static T Private<T>(object target, string field)
            => (T)target.GetType().GetField(field, NonPublic).GetValue(target);

        [SetUp]
        public void SetUp() => Physics.autoSyncTransforms = true;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        /// <summary>死亡快照无条件对位：死亡后 Owner 停发输入，服务器 LastProcessedTick 不再前进，
        /// 「相同 ACK 的重复死亡快照」是常态路径，绝不能被重复 ACK 门丢弃。</summary>
        [Test]
        public void DeathSnapshot_RepeatedSameAck_StillSnaps()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);

            Feed(adapter, State(serverTick: 100, ack: 7, dead: true, new Vector3(1f, 0f, 2f)));
            Assert.That(root.position.x, Is.EqualTo(1f).Within(1e-3f), "首次死亡快照必须对位权威姿态");
            Assert.That(root.position.z, Is.EqualTo(2f).Within(1e-3f));
            Assert.That(Private<bool>(adapter, "_ownerWasDead"), Is.True, "死亡边界已记录");

            // 相同 ACK 的第二张死亡快照（服务器继续推送）仍须对位——旧实现会被 IgnoreRepeat 丢弃
            Feed(adapter, State(serverTick: 101, ack: 7, dead: true, new Vector3(3f, 0f, 4f)));
            Assert.That(root.position.x, Is.EqualTo(3f).Within(1e-3f),
                "相同 ACK 的重复死亡快照不得被重复 ACK 门拦下");
            Assert.That(Private<bool>(adapter, "_ownerWasDead"), Is.True);

            Assert.That(Private<PredictionBuffer>(adapter, "_buffer").HasCommands, Is.False,
                "死亡期间预测历史必须为空");
        }

        /// <summary>重生边界：复活快照与死亡快照同 ACK（死亡期间无上行输入）也必须硬对位到出生点，
        /// 不得停留在死亡点（用户报告「死亡后不重生」的客户端侧症状）。</summary>
        [Test]
        public void RespawnSnapshot_SameAckAsDeath_SnapsToSpawnPoint_NotDeathPoint()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            var deathPoint = new Vector3(1f, 0f, 2f);
            var spawnPoint = new Vector3(-8f, 0f, 6f);

            Feed(adapter, State(serverTick: 100, ack: 9, dead: true, deathPoint));
            Assert.That(root.position.x, Is.EqualTo(deathPoint.x).Within(1e-3f));

            // 死亡期间 Owner 无输入 → 复活快照的 ACK 仍是 9（与死亡快照相同）
            Feed(adapter, State(serverTick: 190, ack: 9, dead: false, spawnPoint));
            Assert.That(Vector3.Distance(root.position, spawnPoint), Is.LessThan(1e-3f),
                "重生必须硬对位到出生点");
            Assert.That(Vector3.Distance(root.position, deathPoint), Is.GreaterThan(1f),
                "复活后不得停留在死亡点");
            Assert.That(Private<bool>(adapter, "_ownerWasDead"), Is.False, "重生边界已消费");
            Assert.That(Private<PredictionBuffer>(adapter, "_buffer").HasCommands, Is.False,
                "重生清空预测历史（避免旧输入重放）");
        }

        /// <summary>重生后回到正常校正链路：ACK 门重新武装——重复 ACK 忽略、ACK 前进对位，
        /// 不会因死亡/重生边界而永久处于「每帧硬对位」状态。</summary>
        [Test]
        public void AfterRespawn_GateRearmed_RepeatIgnored_AdvancedAckSnaps()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            Feed(adapter, State(100, 9, dead: true, new Vector3(1f, 0f, 0f)));
            Feed(adapter, State(190, 9, dead: false, new Vector3(-8f, 0f, 0f))); // 重生

            // 预测端本地再漂移（模拟预测继续跑）：重复 ACK 快照不得把它拉回
            var drifted = new Vector3(-8f, 0f, 1.5f);
            root.position = drifted;
            Feed(adapter, State(191, 9, dead: false, new Vector3(-8f, 0f, 9f)));
            Assert.That(root.position.z, Is.EqualTo(drifted.z).Within(1e-3f),
                "重生后的重复 ACK 快照必须被忽略（门已重新武装）");

            // ACK 前进：可配对历史为空 → 保守硬对位（既有语义）
            Feed(adapter, State(192, 12, dead: false, new Vector3(-7f, 0f, 0f)));
            Assert.That(root.position.x, Is.EqualTo(-7f).Within(1e-3f),
                "重生后 ACK 前进的权威快照照常对位");
        }

        /// <summary>边界回归：无死亡的首次权威快照（LastClientTick==0）一次性初始对位不被破坏。</summary>
        [Test]
        public void FirstSnapshotWithoutDeath_InitialSnap()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            Feed(adapter, State(50, 0, dead: false, new Vector3(2f, 0f, 3f)));
            Assert.That(root.position.x, Is.EqualTo(2f).Within(1e-3f));
            Assert.That(Private<bool>(adapter, "_ownerWasDead"), Is.False);
        }

        /// <summary>连续三轮死亡→重生：死亡标记与预测历史每轮都正确重建（用户要求「连续至少三轮」）。</summary>
        [Test]
        public void ThreeDeathRespawnCycles_AllRecover()
        {
            var (adapter, _, root) = Spawn(Vector3.zero);
            uint ack = 0;
            for (int round = 1; round <= 3; round++)
            {
                var deathPoint = new Vector3(round * 2f, 0f, 0f);
                var spawnPoint = new Vector3(-round * 2f, 0f, 0f);
                ack += 5;

                Feed(adapter, State((uint)(100 * round), ack, dead: true, deathPoint));
                Assert.That(root.position.x, Is.EqualTo(deathPoint.x).Within(1e-3f), $"第 {round} 轮死亡对位");

                Feed(adapter, State((uint)(100 * round + 45), ack, dead: false, spawnPoint));
                Assert.That(root.position.x, Is.EqualTo(spawnPoint.x).Within(1e-3f), $"第 {round} 轮重生对位");
                Assert.That(Private<bool>(adapter, "_ownerWasDead"), Is.False, $"第 {round} 轮重生边界已消费");
            }
        }

        /// <summary>移动/跳跃状态下死亡后重生：重生快照写回 Idle 步态与零水平/垂直速度
        /// （服务器 ServerRespawn 构造的快照语义），不带回死亡前的运动状态。</summary>
        [Test]
        public void RespawnSnapshot_ClearsMotionState()
        {
            var (adapter, locomotor, root) = Spawn(Vector3.zero);
            // 死亡前：预测端处于跳跃/空中运动状态
            locomotor.ApplyAuthoritativeSnapshot(new MovementSnapshot
            {
                Position = new Vector3(0f, 3f, 0f),
                Rotation = Quaternion.identity,
                HorizontalVelocity = new Vector3(3f, 0f, 3f),
                VerticalVelocity = 5f,
                LocomotionState = LocomotionState.Air,
                GaitPhase = 0.4f,
            });
            Feed(adapter, State(100, 4, dead: true, new Vector3(0f, 3f, 0f)));

            var respawn = new MovementSnapshot
            {
                Position = new Vector3(-8f, 0.1f, 0f),
                Rotation = Quaternion.identity,
                HorizontalVelocity = Vector3.zero,
                VerticalVelocity = 0f,
                LocomotionState = LocomotionState.Idle,
                GaitPhase = 0f,
            };
            Feed(adapter, new AuthoritativeMovementState
            {
                ServerTick = 150, LastClientTick = 4, Dead = false, Snapshot = respawn,
            });

            var applied = locomotor.CaptureSnapshot();
            Assert.That(applied.Position.x, Is.EqualTo(-8f).Within(1e-3f), "重生位置来自权威出生点");
            Assert.That(applied.HorizontalVelocity.magnitude, Is.LessThan(1e-3f), "重生清零水平速度");
            Assert.That(Mathf.Abs(applied.VerticalVelocity), Is.LessThan(1e-3f), "重生清零垂直速度");
            Assert.That(applied.LocomotionState, Is.EqualTo(LocomotionState.Idle), "重生恢复 Idle 步态");
            Assert.That(root.position.y, Is.LessThan(1f), "重生不得残留死亡点高度");
        }
    }
}
