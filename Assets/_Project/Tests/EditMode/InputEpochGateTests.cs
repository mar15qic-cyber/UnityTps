using System.Reflection;
using FishNet.Object;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// F14（2026-09-19 审计）输入生命代际闸回归：死亡前在途的输入批次在冻结门开启（复活）
    /// 之后才到达时，必须按代际拒收并清队——不得作为新生命输入消费。
    /// 反例背景：冻结门（D3）只拦"收到时仍冻结"的批次；冻结期结束后的迟到旧批次会穿过
    /// 该门，tick 落在未来窗口时被当作新生命输入。协议 v5→v6：MovementCommand.LifeEpoch。
    /// </summary>
    public sealed class InputEpochGateTests
    {
        private static readonly BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly System.Collections.Generic.List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private (PlayerNetworkAdapter adapter, NetworkCombatAuthority combat, ServerInputQueue queue)
            SpawnServerSide()
        {
            var go = new GameObject("EpochSim_" + _spawned.Count);
            _spawned.Add(go);
            go.AddComponent<CharacterController>();
            var combat = go.AddComponent<NetworkCombatAuthority>();
            var locomotor = go.AddComponent<Locomotor>();
            locomotor.SetSimulationMode(MovementSimulationMode.ServerAuthority);
            typeof(Locomotor).GetMethod("Awake", NonPublic)?.Invoke(locomotor, null);
            var adapter = go.AddComponent<PlayerNetworkAdapter>();
            typeof(PlayerNetworkAdapter).GetField("_locomotor", NonPublic)?.SetValue(adapter, locomotor);
            typeof(PlayerNetworkAdapter).GetField("_combatAuthority", NonPublic)?.SetValue(adapter, combat);
            var queue = (ServerInputQueue)typeof(PlayerNetworkAdapter).GetField("_serverQueue", Any).GetValue(adapter);
            var enqueue = typeof(PlayerNetworkAdapter).GetMethod("EnqueueServerInputBatch", Any);
            return (adapter, combat, queue);
        }

        private static MovementCommand Cmd(uint tick, uint epoch) =>
            new(new Vector2(0f, 1f), false, false, 0f, 0f, tick, epoch);

        [Test]
        public void StaleEpochBatch_AfterRespawn_DroppedAndQueueCleared()
        {
            var (adapter, combat, queue) = SpawnServerSide();
            var enqueue = typeof(PlayerNetworkAdapter).GetMethod("EnqueueServerInputBatch", Any);

            // 新生命（代际 1）：新代际批次正常入队
            combat.BumpLifeGenerationForTests();
            Assert.That(combat.CurrentLifeEpoch, Is.EqualTo(1u));
            enqueue.Invoke(adapter, new object[] { new[] { Cmd(10, 1), Cmd(11, 1) } });
            Assert.That(queue.Count, Is.EqualTo(2), "当前代际批次正常入队");

            // 死亡前在途批次（代际 0）在复活后到达：整批拒收 + 清队（F14 核心反例）
            enqueue.Invoke(adapter, new object[] { new[] { Cmd(4, 0), Cmd(5, 0) } });
            Assert.That(queue.Count, Is.EqualTo(0), "旧代际批次到达必须清空队列（新基线）");
            Assert.That(queue.DroppedStaleEpoch, Is.EqualTo(2), "拒收计数留痕");

            // 新代际后续批次照常入队（客户端按 SendUnacked 重发自愈）
            enqueue.Invoke(adapter, new object[] { new[] { Cmd(12, 1) } });
            Assert.That(queue.Count, Is.EqualTo(1));
        }

        [Test]
        public void FirstLife_EpochZeroBatch_Accepted_BackwardCompatible()
        {
            var (adapter, combat, queue) = SpawnServerSide();
            var enqueue = typeof(PlayerNetworkAdapter).GetMethod("EnqueueServerInputBatch", Any);
            Assert.That(combat.CurrentLifeEpoch, Is.EqualTo(0u), "从未重生的会话代际为 0");

            enqueue.Invoke(adapter, new object[] { new[] { Cmd(1, 0), Cmd(2, 0) } });
            Assert.That(queue.Count, Is.EqualTo(2), "首生会话（旧客户端/既有测试形状）恒通过");
            Assert.That(queue.DroppedStaleEpoch, Is.EqualTo(0));
        }

        [Test]
        public void MixedEpochWithinBatch_WholeBatchRejected()
        {
            var (adapter, combat, queue) = SpawnServerSide();
            var enqueue = typeof(PlayerNetworkAdapter).GetMethod("EnqueueServerInputBatch", Any);

            combat.BumpLifeGenerationForTests();
            enqueue.Invoke(adapter, new object[] { new[] { Cmd(10, 1), Cmd(11, 0), Cmd(12, 1) } });
            Assert.That(queue.Count, Is.EqualTo(0), "批内混杂代际整批拒绝（防旧代际夹带）");
            Assert.That(queue.DroppedStaleEpoch, Is.EqualTo(3));
        }
    }
}
