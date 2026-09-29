using System.Reflection;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Phase 2 定向测试：LagComp 快照生命代际/无敌元数据 + 命中判定代际闸。
    /// 红测语义锁死三类判定：代际不符（TARGET_STALE，修复前"旧生命回溯伤害新生命"无任何拦截）、
    /// 快照无敌（TARGET_INVINCIBLE）、语境缺失诚实放行（无快照不拦截，维持既有即时判定语义）。
    /// 全部走 ServerLagCompensation 真实 Capture/TryGetRewindContext（无网络实例，EditMode 可驱）。
    /// </summary>
    public sealed class LagCompGenerationTests
    {
        private ServerLagCompensation _manager;
        private GameObject _victimRoot;
        private NetworkCombatAuthority _victimAuthority;
        private DamageableTarget _victimTarget;

        [SetUp]
        public void SetUp()
        {
            var managerGo = new GameObject("LagCompGenHarness");
            _manager = managerGo.AddComponent<ServerLagCompensation>();
            // EditMode 下 AddComponent 不触发 Awake（既有三坑纪律）：ServerLagCompensation.Instance
            // 由 Awake 赋值——代际闸读取的是静态 Instance，必须手动补一次 Awake 才非 null。
            typeof(ServerLagCompensation)
                .GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(_manager, null);

            _victimRoot = new GameObject("Victim");
            _victimRoot.transform.position = new Vector3(4900, 4900, 4900);
            _victimAuthority = _victimRoot.AddComponent<NetworkCombatAuthority>();
            _victimTarget = _victimRoot.AddComponent<DamageableTarget>();
            var hitboxGo = new GameObject("BodyHitbox");
            hitboxGo.transform.SetParent(_victimRoot.transform, false);
            var collider = hitboxGo.AddComponent<BoxCollider>();
            _manager.RegisterPlayer(_victimRoot.transform, new Collider[] { collider }, _victimAuthority);
        }

        [TearDown]
        public void TearDown()
        {
            ServerLagCompensation.Enabled = true;
            if (_victimRoot != null) Object.DestroyImmediate(_victimRoot);
            if (_manager != null) Object.DestroyImmediate(_manager.gameObject);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CameraRay_PreservesRewindLifeAndInvincibilityGates(bool invincible)
        {
            typeof(DamageableTarget).GetProperty("CurrentHealth").GetSetMethod(true)
                .Invoke(_victimTarget, new object[] { 100 });
            if (invincible) SetInvincibleUntil(100);
            _manager.Capture(1);
            if (!invincible) SetLifeGeneration(5);
            var shooter = new GameObject("CameraGateShooter");
            try
            {
                var resolver = shooter.AddComponent<CombatResolver>();
                Physics.SyncTransforms();
                var result = resolver.ResolveCameraHitscan(_victimRoot.transform.position + new Vector3(0, 0, -2), Vector3.forward,
                    10, 25, ~0, shooter.transform, new LagCompRewindContext(1, true));
                Assert.That(result.Target, Is.EqualTo(_victimTarget));
                Assert.That(result.Damaged, Is.False);
                Assert.That(_victimTarget.CurrentHealth, Is.EqualTo(100));
                Assert.That(resolver.LastFireEvidence.MissReason, Is.EqualTo(invincible
                    ? CombatResolver.MissTargetInvincible : CombatResolver.MissTargetStale));
            }
            finally { Object.DestroyImmediate(shooter); }
        }

        private void SetLifeGeneration(ulong generation)
        {
            typeof(NetworkCombatAuthority)
                .GetField("_lifeGeneration", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(_victimAuthority, generation);
        }

        private void SetInvincibleUntil(uint tick)
        {
            typeof(NetworkCombatAuthority)
                .GetField("_invincibleUntilTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_victimAuthority); // SyncVar 实例（weaver 可能改写访问性，三合一 flags 覆盖）
            var field = typeof(NetworkCombatAuthority)
                .GetField("_invincibleUntilTick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var syncVar = (FishNet.Object.Synchronizing.SyncVar<uint>)field.GetValue(_victimAuthority);
            syncVar.Value = tick;
        }

        // ---- 快照元数据 ----

        [Test]
        public void Capture_RecordsLifeGeneration()
        {
            SetLifeGeneration(3);
            _manager.Capture(1);

            Assert.That(_manager.TryGetRewindContext(_victimRoot.transform, 1, out ulong gen, out bool invincible), Is.True);
            Assert.That(gen, Is.EqualTo(3ul));
            Assert.That(invincible, Is.False);
        }

        [Test]
        public void Capture_RecordsInvincibilityWindow()
        {
            SetInvincibleUntil(50);
            _manager.Capture(1); // tick 1 < 截止 50 → 快照应携带无敌

            Assert.That(_manager.TryGetRewindContext(_victimRoot.transform, 1, out _, out bool invincible), Is.True);
            Assert.That(invincible, Is.True);
        }

        [Test]
        public void TryGetRewindContext_UnregisteredRoot_ReturnsFalse()
        {
            _manager.Capture(1);
            var stranger = new GameObject("Stranger");
            try
            {
                Assert.That(_manager.TryGetRewindContext(stranger.transform, 1, out _, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(stranger);
            }
        }

        // ---- 命中判定代际闸 ----

        [Test]
        public void RewindGate_OldGenerationHit_RejectedAsStale()
        {
            // tick 1：旧生命（代际 0）；之后重生（代际 5）——迟到的旧 tick 射击不得伤害新生命
            _manager.Capture(1);
            SetLifeGeneration(5);
            _manager.Capture(2);

            var context = new LagCompRewindContext(1, true);
            Assert.That(CombatResolver.PassesRewindLifeGate(_victimTarget, context, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo(CombatResolver.MissTargetStale));
        }

        [Test]
        public void RewindGate_CurrentGenerationHit_Passes()
        {
            _manager.Capture(1);
            SetLifeGeneration(5);
            _manager.Capture(2);

            var context = new LagCompRewindContext(2, true);
            Assert.That(CombatResolver.PassesRewindLifeGate(_victimTarget, context, out string reason), Is.True);
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void RewindGate_InvincibleSnapshot_RejectedAsProtected()
        {
            SetInvincibleUntil(50);
            _manager.Capture(1); // 同代际 + 射击时刻处于保护窗

            var context = new LagCompRewindContext(1, true);
            Assert.That(CombatResolver.PassesRewindLifeGate(_victimTarget, context, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo(CombatResolver.MissTargetInvincible));
        }

        [Test]
        public void RewindGate_NoRewindContext_Passes()
        {
            // 本地预测/离线（default 语境）：代际闸不生效
            _manager.Capture(1);
            SetLifeGeneration(5);

            Assert.That(CombatResolver.PassesRewindLifeGate(_victimTarget, default, out string reason), Is.True);
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void RewindGate_NetworkTargetSnapshotMissing_Rejects()
        {
            // 联网目标缺少历史时拒绝回溯伤害，不能按当前位姿假装历史命中
            _manager.UnregisterPlayer(_victimRoot.transform);
            _manager.Capture(1);

            var context = new LagCompRewindContext(1, true);
            Assert.That(CombatResolver.PassesRewindLifeGate(_victimTarget, context, out string reason), Is.False);
            Assert.That(reason, Is.Not.Null);
        }
    }
}
