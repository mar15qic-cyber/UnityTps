using System.Reflection;
using FishNet.Object.Synchronizing;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase 1（重生调度服务器权威化）定向测试：tick 换算纯函数、到点消费单次语义、
    /// 出生保护窗口边界。EditMode 无网络实例——NetworkCombatAuthority 走 NetworkObject 空路径
    ///（ServerRespawn 守卫早退），只验证调度与窗口语义本身；端到端表现属实机验收。
    /// </summary>
    public sealed class RespawnTickTests
    {
        private NetworkCombatAuthority _authority;

        [SetUp]
        public void SetUp()
        {
            var go = new GameObject("RespawnTickTests_Authority");
            _authority = go.AddComponent<NetworkCombatAuthority>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_authority != null) Object.DestroyImmediate(_authority.gameObject);
        }

        // ---- MatchRules tick 换算纯函数 ----

        [Test]
        public void SecondsToTicks_RespawnDelay_At30Hz()
        {
            Assert.That(MatchRules.SecondsToTicks(MatchRules.RespawnDelaySeconds, 30), Is.EqualTo(90u));
        }

        [Test]
        public void SecondsToTicks_SpawnProtection_At30Hz()
        {
            Assert.That(MatchRules.SecondsToTicks(MatchRules.SpawnProtectionSeconds, 30), Is.EqualTo(75u));
        }

        [Test]
        public void SecondsToTicks_NonPositiveSeconds_ReturnsZero()
        {
            Assert.That(MatchRules.SecondsToTicks(0f, 30), Is.EqualTo(0u));
            Assert.That(MatchRules.SecondsToTicks(-1f, 30), Is.EqualTo(0u));
        }

        [Test]
        public void SecondsToTicks_InvalidTickRate_FallsBackTo30()
        {
            Assert.That(MatchRules.SecondsToTicks(1f, 0), Is.EqualTo(30u));
            Assert.That(MatchRules.SecondsToTicks(1f, -5), Is.EqualTo(30u));
        }

        [Test]
        public void SecondsToTicks_CeilsPartialTicks()
        {
            Assert.That(MatchRules.SecondsToTicks(0.05f, 30), Is.EqualTo(2u)); // ceil(1.5)=2
            Assert.That(MatchRules.SecondsToTicks(1f, 30), Is.EqualTo(30u));
        }

        [Test]
        public void TicksToSeconds_RoundTrips()
        {
            Assert.That(MatchRules.TicksToSeconds(90u, 30), Is.EqualTo(3f).Within(1e-4f));
            Assert.That(MatchRules.TicksToSeconds(0u, 30), Is.EqualTo(0f));
            Assert.That(MatchRules.TicksToSeconds(30u, 0), Is.EqualTo(1f).Within(1e-4f)); // 回退 30Hz
        }

        // ---- SyncVar 访问器（weaver 可能改写访问性：Public|NonPublic 三合一反射） ----

        private SyncVar<uint> GetSyncVar(string fieldName)
        {
            var field = typeof(NetworkCombatAuthority).GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"SyncVar 字段缺失：{fieldName}");
            return (SyncVar<uint>)field.GetValue(_authority);
        }

        [Test]
        public void Accessors_Default_ToZero()
        {
            Assert.That(_authority.RespawnAtTick, Is.EqualTo(0u));
            Assert.That(_authority.InvincibleUntilTick, Is.EqualTo(0u));
            Assert.That(_authority.LifeGeneration, Is.EqualTo(0ul));
        }

        // ---- 到点消费：单次语义（internal 薄壳直驱） ----

        [Test]
        public void TryConsumeRespawnDue_NotQueued_ReturnsFalse()
        {
            Assert.That(_authority.TryConsumeRespawnDue(1000u), Is.False);
            Assert.That(_authority.RespawnAtTick, Is.EqualTo(0u));
        }

        [Test]
        public void TryConsumeRespawnDue_BeforeDeadline_ReturnsFalse()
        {
            GetSyncVar("_respawnAtTick").Value = 90u;
            Assert.That(_authority.TryConsumeRespawnDue(89u), Is.False);
            Assert.That(_authority.RespawnAtTick, Is.EqualTo(90u));
        }

        [Test]
        public void TryConsumeRespawnDue_AtDeadline_ConsumesOnce()
        {
            GetSyncVar("_respawnAtTick").Value = 90u;
            Assert.That(_authority.TryConsumeRespawnDue(90u), Is.True);
            Assert.That(_authority.RespawnAtTick, Is.EqualTo(0u));
            // 已消费：后续 tick 不再触发（ServerRespawn 早退无副作用；此断言锁"单次"语义）
            Assert.That(_authority.TryConsumeRespawnDue(200u), Is.False);
        }

        // ---- 出生保护窗口语义 ----

        [Test]
        public void IsInvincibleAt_ZeroDeadline_NeverProtected()
        {
            Assert.That(_authority.IsInvincibleAt(0u), Is.False);
            Assert.That(_authority.IsInvincibleAt(uint.MaxValue), Is.False);
        }

        [Test]
        public void IsInvincibleAt_WindowBoundary_IsHalfOpen()
        {
            GetSyncVar("_invincibleUntilTick").Value = 75u;
            Assert.That(_authority.IsInvincibleAt(0u), Is.True);
            Assert.That(_authority.IsInvincibleAt(74u), Is.True);
            Assert.That(_authority.IsInvincibleAt(75u), Is.False); // 截止 tick 当刻已出保护
        }
    }
}
