using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// F01（2026-09-19 审计）归因顺序回归：击杀归属必须在"伤害实际被结算"时登记（先于
    /// OnDied 回调），且只有实际接受的伤害登记。
    /// 反例背景：登记挂在 OnShotFired（结算完成后）——首发致死时注册表为空 → 击杀无归属
    /// （DeathOnly）；死亡处理清表后本次开火事件又把已死目标登记回去（污染下一生命归因）；
    /// 被拒绝的伤害（保护期/友军/死体）也登记，覆盖上一有效伤害者。
    /// 本套件按服务器路径直驱真实链路：CombatResolver → ApplyDamage(damageSource) →
    /// RegisterHit（先于 OnDied）→ 死亡 Consume 消费。
    /// </summary>
    public sealed class DamageAttributionOrderTests
    {
        private CombatResolver _resolver;
        private GameObject _shooterA;
        private GameObject _shooterB;
        private DamageableTarget _victim;
        private NetworkCombatAuthority _authorityA;
        private NetworkCombatAuthority _authorityB;

        [SetUp]
        public void SetUp()
        {
            MatchLifecycle.ClearAttributionForTests();
            MatchLifecycle.EligibilityProbeForTests = _ => true; // 无头直驱放行资格语义（助攻过滤生效）
            _resolver = new GameObject("resolver").AddComponent<CombatResolver>();

            _shooterA = new GameObject("shooterA");
            _authorityA = _shooterA.AddComponent<NetworkCombatAuthority>();
            _shooterB = new GameObject("shooterB");
            _authorityB = _shooterB.AddComponent<NetworkCombatAuthority>();

            var victimRoot = new GameObject("victim");
            victimRoot.AddComponent<NetworkCombatAuthority>(); // 受击体归属（IsInvincibleNow 闸生效面）
            var victimBody = new GameObject("victimBody");
            victimBody.transform.SetParent(victimRoot.transform);
            var box = victimBody.AddComponent<BoxCollider>();
            victimBody.transform.position = new Vector3(0f, 0f, 10f);
            _victim = victimBody.AddComponent<DamageableTarget>();
            _victim.ResetHealth(); // EditMode 下 AddComponent 不触发 Awake，显式初始化生命值
            Assert.IsTrue(_victim.IsAlive);
        }

        [TearDown]
        public void TearDown()
        {
            MatchLifecycle.EligibilityProbeForTests = null;
            MatchLifecycle.ClearAttributionForTests();
            Object.DestroyImmediate(_resolver.gameObject);
            Object.DestroyImmediate(_shooterA);
            Object.DestroyImmediate(_shooterB);
            Object.DestroyImmediate(_victim.transform.root.gameObject);
        }

        private HitscanResult Shoot(NetworkCombatAuthority source, int damage)
        {
            // 服务器路径形状：WeaponController.TryFire 把 shooter 的 authority 传入 resolver
            //（离线/客户端预测传 null，不进注册表）
            return _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, damage, ~0, _shooterA.transform, source);
        }

        [Test]
        public void FirstShotKill_KillerIsRegistered_BeforeDeathConsumes()
        {
            var result = Shoot(_authorityA, 100); // 满血一发致死

            Assert.IsTrue(result.Damaged);
            Assert.IsFalse(_victim.IsAlive);
            // 核心反例（旧实现此断言失败：注册表为空 → DeathOnly 无归属）
            Assert.IsTrue(MatchLifecycle.TryPeekKillerForTests(_victim, out var killer),
                "致死伤害必须在 OnDied 之前登记");
            Assert.AreEqual(_authorityA, killer);
            // 死亡处理取走后注册表不再残留（不再被迟到事件登记回去）
            Assert.AreEqual(_authorityA, MatchLifecycle.ConsumeKillerOf(_victim));
            Assert.IsFalse(MatchLifecycle.TryPeekKillerForTests(_victim, out _));
        }

        [Test]
        public void SecondShooterGetsKill_FirstShooterBecomesAssist()
        {
            Shoot(_authorityA, 30);  // A 先伤害（存活）
            Assert.IsTrue(_victim.IsAlive);
            Shoot(_authorityB, 100); // B 补刀

            Assert.IsTrue(MatchLifecycle.TryPeekKillerForTests(_victim, out var killer));
            Assert.AreEqual(_authorityB, killer, "击杀者=最后实际结算伤害的射手");
            var assists = MatchLifecycle.ConsumeAssistsOf(_victim, _authorityB);
            Assert.IsNotNull(assists);
            Assert.IsTrue(assists.Contains(_authorityA), "先伤害者进入助攻名单");
        }

        [Test]
        public void InvincibleReject_DoesNotOverwritePreviousKiller()
        {
            Shoot(_authorityA, 30); // A 有效伤害 → 注册表=A
            _victim.transform.root.GetComponent<NetworkCombatAuthority>()
                .SetInvincibleUntilTickForTests(1u); // 无头 tick=0 → 0<1 成立，保护窗生效
            Assert.IsTrue(_victim.transform.root.GetComponent<NetworkCombatAuthority>().IsInvincibleNow);

            var rejected = Shoot(_authorityB, 100); // B 射击被保护终闸拒绝（ApplyDamage 内部早退）

            // 注意：单段 ResolveHitscan（离线路径）的 Damaged 标志不反映 ApplyDamage 内部拒绝，
            // 服务器两段路径在 PassesRewindLifeGate 就已拒绝；此处断言真正的不变量：
            Assert.AreEqual(70, _victim.CurrentHealth, "保护期伤害不得结算（血量保持 A 伤害后）");
            Assert.IsTrue(MatchLifecycle.TryPeekKillerForTests(_victim, out var killer));
            Assert.AreEqual(_authorityA, killer, "被拒绝的伤害不得覆盖上一有效伤害者");
            Assert.AreEqual(1, MatchLifecycle.PeekAssistRecordCountForTests(_victim), "助攻登记同样不追加");
        }

        [Test]
        public void FriendlyBlock_NoRegistration()
        {
            _authorityA.SetTeamForTests("Red");
            _victim.transform.root.GetComponent<NetworkCombatAuthority>().SetTeamForTests("Red");

            var blocked = Shoot(_authorityA, 50);

            Assert.IsFalse(blocked.Damaged, "友军命中按阻挡（零伤害）");
            Assert.IsFalse(MatchLifecycle.TryPeekKillerForTests(_victim, out _), "友军伤害不登记");
        }

        [Test]
        public void ShotgunTwoTargets_EachAttributedIndependently()
        {
            var secondRoot = new GameObject("victim2");
            secondRoot.AddComponent<NetworkCombatAuthority>();
            var secondBody = new GameObject("victim2Body");
            secondBody.transform.SetParent(secondRoot.transform);
            secondBody.AddComponent<BoxCollider>();
            secondBody.transform.position = new Vector3(2f, 0f, 10f); // 射线 (0.2,0,1) 归一后 z=10 处 x≈1.96，落在盒内
            var victim2 = secondBody.AddComponent<DamageableTarget>();
            _victim.ResetHealth();
            victim2.ResetHealth();

            try
            {
                // 霰弹形状：WeaponController 逐 pellet 调 resolver（每 pellet 各自携带射手）
                Shoot(_authorityA, 100); // pellet1 → victim1 致死
                var hit2 = _resolver.ResolveHitscan(
                    Vector3.zero, new Vector3(0.2f, 0f, 1f).normalized, 50f, 100, ~0, _shooterA.transform, _authorityA);

                Assert.IsTrue(hit2.Damaged);
                Assert.IsFalse(victim2.IsAlive);
                Assert.IsTrue(MatchLifecycle.TryPeekKillerForTests(_victim, out var killer1));
                Assert.AreEqual(_authorityA, killer1, "目标1 归属不因第二个目标丢失");
                Assert.IsTrue(MatchLifecycle.TryPeekKillerForTests(victim2, out var killer2));
                Assert.AreEqual(_authorityA, killer2, "目标2 独立归属（聚合单结果旧实现会丢失其一）");
            }
            finally
            {
                Object.DestroyImmediate(secondRoot);
            }
        }

        [Test]
        public void DeadBody_RejectedDamage_DoesNotReRegister()
        {
            Shoot(_authorityA, 100); // 致死 + 死亡处理消费
            MatchLifecycle.ConsumeKillerOf(_victim); // 清表（HandleServerDied 的 Consume 形状）
            Assert.IsFalse(_victim.IsAlive);

            var late = Shoot(_authorityB, 50); // 迟到/补射命中尸体

            Assert.IsFalse(late.Damaged, "死体不结算");
            Assert.IsFalse(MatchLifecycle.TryPeekKillerForTests(_victim, out _),
                "死体伤害不得把已消费目标重新登记回注册表");
        }

        [Test]
        public void OfflinePath_NoSourcePassed_NoRegistration()
        {
            var result = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 30, ~0, _shooterA.transform, null);

            Assert.IsTrue(result.Damaged);
            Assert.IsTrue(_victim.IsAlive || MatchLifecycle.TryPeekKillerForTests(_victim, out _) == false,
                "离线路径（source=null）不进注册表");
            Assert.IsFalse(MatchLifecycle.TryPeekKillerForTests(_victim, out _));
        }
    }
}
