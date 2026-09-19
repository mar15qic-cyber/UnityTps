using System.Collections.Generic;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 命中归属回归（2026-09-16 建立，2026-09-18 审计 §5 按新语义更新）。
    ///
    /// 本文件原本为"祖先→子树猜 DamageableTarget"兜底归属而写：射线命中玩家根碰撞体
    /// （实机 = 根 CharacterController，DamageableTarget 挂在子节点 TP_Model 上父链搜不到）时，
    /// 按"射线打中了这个玩家对象"归属并沿用根碰撞体的命中点。
    ///
    /// 该规则已被**移除**：它同时是"模型外空处受击 + 空气弹孔"的规则缺口——根 CC 外壳与可见
    /// 蒙皮身体不是同一套几何（实测表面差 0.04–0.29m），只要最近命中落在外壳上，
    /// 玩家就会在根本没被打到的位置掉血。现在的规则是：
    /// ① 显式标记为移动阻挡体的碰撞体在选择"最近有效几何命中"阶段整体跳过（不吃伤害、不挡枪）；
    /// ② 玩家受击必须命中显式受击体（BodyHitbox，父链可归属）；
    /// ③ 同根 5cm 共面回退保留，只用于"复合目标给精确落点"，不承担按对象猜归属。
    ///
    /// EditMode 要点：AddComponent 不跑 Awake（DamageableTarget 血量需反射初始化）；改变换后
    /// Physics.SyncTransforms()。真实 CharacterController 在 EditMode 不被射线命中（需真正 Move 过），
    /// 故这里用等参数碰撞体 + 显式标记代表移动阻挡体；CC 的识别规则见 PlayerHitVolumeFilterTests。
    /// </summary>
    public sealed class HitAttributionFallbackTests
    {
        private GameObject _shooterRoot;
        private CombatResolver _resolver;
        private readonly List<Object> _temp = new();

        [SetUp]
        public void SetUp()
        {
            Physics.autoSyncTransforms = true;
            _shooterRoot = new GameObject("Shooter_Attribution");
            _temp.Add(_shooterRoot);
            _resolver = _shooterRoot.AddComponent<CombatResolver>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _temp)
                if (o != null) Object.DestroyImmediate(o);
            _temp.Clear();
        }

        /// <summary>玩家形对象：根身体碰撞体 + 子节点模型（挂 DamageableTarget）+ 可选 BodyHitbox。</summary>
        private (GameObject root, DamageableTarget target) MakePlayerBody(
            string name, Vector3 position, bool withDamageableTarget = true, bool withBodyHitbox = false)
        {
            var root = new GameObject(name);
            _temp.Add(root);
            root.transform.position = position;
            var body = root.AddComponent<CapsuleCollider>();
            body.center = new Vector3(0f, 0.9f, 0f);
            body.radius = 0.35f;
            body.height = 1.8f;
            // 与运行时装配一致：玩家根身体碰撞体 = 只挡移动（2026-09-18 §5）
            HitVolumeTag.Assign(root, HitVolumeRole.MovementBlocker);

            var model = new GameObject("TP_Model");
            _temp.Add(model);
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = new Vector3(0f, 0f, 0.34f); // 与正式 prefab 同形

            DamageableTarget target = null;
            if (withDamageableTarget)
            {
                target = model.AddComponent<DamageableTarget>();
                // EditMode AddComponent 不跑 Awake：反射初始化血量使 IsAlive=true
                typeof(DamageableTarget).GetProperty("CurrentHealth")!
                    .GetSetMethod(true)!.Invoke(target, new object[] { 100 });
            }

            if (withBodyHitbox)
            {
                var hitbox = new GameObject("BodyHitbox");
                _temp.Add(hitbox);
                hitbox.transform.SetParent(model.transform, false);
                hitbox.transform.localPosition = model.transform.InverseTransformPoint(root.transform.position);
                var capsule = hitbox.AddComponent<CapsuleCollider>();
                capsule.isTrigger = true;
                capsule.center = new Vector3(0f, 0.9f, 0f);
                capsule.radius = 0.35f;
                capsule.height = 1.8f;
                capsule.direction = 1;
                HitVolumeTag.Assign(hitbox, HitVolumeRole.DamageSurface);
            }

            Physics.SyncTransforms();
            return (root, target);
        }

        private HitscanResult Shoot(Vector3 origin, Vector3 dir, int damage = 25)
            => _resolver.ResolveHitscan(origin, dir, 50f, damage, ~0, _shooterRoot.transform);

        [Test]
        public void MovementVolumeOnlyHit_DoesNotDamage_AndRayContinuesBehind()
        {
            // 新语义（替换旧的"命中根碰撞体也要造成伤害"断言）：射线穿过玩家移动外壳、但没有任何
            // 显式受击体被命中 = 零伤害，且射线不被不可见外壳截断，后方的墙照常命中并落在墙表面。
            // 旧规则正是"模型外空处掉血 + 空气弹孔"的来源。
            var (_, target) = MakePlayerBody("Victim", new Vector3(0f, 0f, 6f)); // 无 BodyHitbox
            _shooterRoot.transform.position = Vector3.zero;
            var wall = new GameObject("Wall");
            _temp.Add(wall);
            wall.transform.position = new Vector3(0f, 0f, 12f);
            wall.AddComponent<BoxCollider>().size = new Vector3(6f, 6f, 1f); // 表面 z=11.5
            Physics.SyncTransforms();

            var r = Shoot(new Vector3(0f, 1.2f, 0f), Vector3.forward); // 胸高：确实穿过玩家外壳

            Assert.That(target.CurrentHealth, Is.EqualTo(100), "只穿过移动阻挡体不得掉血");
            Assert.That(r.Damaged, Is.False);
            Assert.That(r.Target, Is.Null);
            Assert.That(r.Hit, Is.True, "射线不得被外壳截断：后墙必须命中");
            Assert.That(r.Point.z, Is.EqualTo(11.5f).Within(0.05f), "落点归后墙表面");
        }

        [Test]
        public void ExplicitHitboxHit_AttributesViaParentChain_AndStopsTheRay()
        {
            // 正常形态（生产装配）：命中 BodyHitbox 子碰撞体 → 父链归属，射线在此截断，不掉穿到后墙
            var (_, target) = MakePlayerBody("VictimHitbox", new Vector3(0f, 0f, 6f), withBodyHitbox: true);
            _shooterRoot.transform.position = Vector3.zero;
            var wall = new GameObject("Wall");
            _temp.Add(wall);
            wall.transform.position = new Vector3(0f, 0f, 12f);
            wall.AddComponent<BoxCollider>().size = new Vector3(6f, 6f, 1f);
            Physics.SyncTransforms();

            var r = Shoot(new Vector3(0f, 1.2f, 0f), Vector3.forward);

            Assert.That(r.Damaged, Is.True);
            Assert.That(r.Target, Is.SameAs(target));
            Assert.That(target.CurrentHealth, Is.EqualTo(75));
            Assert.That(r.Point.z, Is.LessThan(11f), "受击体必须吸收子弹：落点不得穿到后墙");
        }

        [Test]
        public void NonDamageableRoot_IsNeverAttributedToAnotherRootsTarget()
        {
            // 不变量：最近命中是"无 DamageableTarget 的普通对象"时，绝不跨根归属到它身后的玩家
            var (_, farTarget) = MakePlayerBody("EnemyBehind", new Vector3(0f, 0f, 9f), withBodyHitbox: true);
            var deco = new GameObject("DecoRoot");
            _temp.Add(deco);
            deco.transform.position = new Vector3(0f, 0f, 3f);
            var decoBody = deco.AddComponent<CapsuleCollider>();
            decoBody.center = new Vector3(0f, 0.9f, 0f);
            decoBody.radius = 0.35f;
            decoBody.height = 1.8f;
            Physics.SyncTransforms();
            _shooterRoot.transform.position = Vector3.zero;

            var r = Shoot(new Vector3(0f, 1.2f, 0f), Vector3.forward);

            Assert.That(r.Hit, Is.True, "命中最前面的对象");
            Assert.That(r.Damaged, Is.False, "无目标对象只吸收子弹，不得跨根归属");
            Assert.That(r.Target, Is.Null);
            Assert.That(farTarget.CurrentHealth, Is.EqualTo(100), "身后玩家不得受伤");
        }

        [Test]
        public void ShooterOwnRoot_NeverSelfDamages()
        {
            // 射手自身 = ignoreRoot：自身根碰撞体 + 自身 DamageableTarget 必须被整体跳过（不自伤）
            _shooterRoot.transform.position = Vector3.zero;
            var selfBody = _shooterRoot.AddComponent<CapsuleCollider>();
            selfBody.center = new Vector3(0f, 0.9f, 0f);
            selfBody.radius = 0.35f;
            selfBody.height = 1.8f;
            var selfModel = new GameObject("TP_Model");
            _temp.Add(selfModel);
            selfModel.transform.SetParent(_shooterRoot.transform, false);
            var selfTarget = selfModel.AddComponent<DamageableTarget>();
            typeof(DamageableTarget).GetProperty("CurrentHealth")!
                .GetSetMethod(true)!.Invoke(selfTarget, new object[] { 100 });

            var (_, victimTarget) = MakePlayerBody("EnemyFront", new Vector3(0f, 0f, 6f), withBodyHitbox: true);
            Physics.SyncTransforms();

            var r = Shoot(new Vector3(0f, 1.2f, 0f), Vector3.forward);

            Assert.That(r.Damaged, Is.True);
            Assert.That(r.Target, Is.SameAs(victimTarget), "应命中前方敌人");
            Assert.That(selfTarget.CurrentHealth, Is.EqualTo(100), "射手自身不得被自己的射线伤害");
        }

        [Test]
        public void PlayerWithoutExplicitHitbox_IsNotDamageable_AndIsNotShootThroughTarget()
        {
            // 资产缺口（TP_Model/BodyHitbox 缺失 → 没有显式受击体）时的诚实降级：
            // 不得退回到"按对象猜归属"，同时移动阻挡体仍然吸收子弹 → 落点在玩家身上但零伤害。
            var (_, target) = MakePlayerBody("VictimNoHitbox", new Vector3(0f, 0f, 6f));
            _shooterRoot.transform.position = Vector3.zero;

            var r = Shoot(new Vector3(0f, 1.2f, 0f), Vector3.forward);

            Assert.That(target.CurrentHealth, Is.EqualTo(100), "没有显式受击体就不造成玩家伤害");
            Assert.That(r.Target, Is.Null);
            Assert.That(r.Hit, Is.False,
                "后方无对象时整发判 miss：移动阻挡体不产生命中点（旧实现会在玩家外壳上留空气弹孔）");
        }
    }
}
