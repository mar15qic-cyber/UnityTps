using System.Collections.Generic;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 受击体 / 移动阻挡体职责分离回归（2026-09-18 审计 §5、§7 用例 3–5）。
    ///
    /// 修复前的规则缺口：射线命中玩家根 CharacterController 时，CombatResolver 会"在该对象子树里
    /// 猜一个 DamageableTarget"来归属伤害，并沿用 CC 的命中点 —— 于是**打模型后方的空处也掉血**，
    /// 还在空气里生成实体弹孔（CC 外壳与可见身体不是同一套几何，实测表面差 0.04–0.29m）。
    ///
    /// 现规则：显式标记为 MovementBlocker 的碰撞体在"选择最近有效几何命中"阶段就被整体跳过——
    /// 它既不吃伤害，也不替身后的目标吞子弹。跳过必须是"不进候选集"而不是"选中后置空 Target"，
    /// 后者仍会挡枪，因此本文件同时断言落点落在受击体/后方墙上（LastSegmentMovementSkipped 证明
    /// 它确实被跳过）。
    ///
    /// EditMode 限制（旧审计已实测）：CharacterController 必须真正 Move 过才进入射线命中集，
    /// 故物理用例用等价的"被标记碰撞体"代表移动阻挡体，真实 CharacterController 的识别规则
    /// 由本文件最后的谓词用例覆盖（不依赖物理命中）。改变换后须 Physics.SyncTransforms()。
    /// </summary>
    public sealed class PlayerHitVolumeFilterTests
    {
        private GameObject _shooterRoot;
        private CombatResolver _resolver;
        private readonly List<Object> _temp = new();

        [SetUp]
        public void SetUp()
        {
            Physics.autoSyncTransforms = true;
            _shooterRoot = new GameObject("Shooter_HitVolume");
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

        private GameObject Box(string name, Vector3 center, Vector3 size, Transform parent = null)
        {
            var go = new GameObject(name);
            _temp.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = center;
            var col = go.AddComponent<BoxCollider>();
            col.size = size;
            Physics.SyncTransforms();
            return go;
        }

        /// <summary>与运行时玩家同构的受害者：根移动阻挡体（标 MovementBlocker）+
        /// TP_Model(DamageableTarget)/BodyHitbox（标 DamageSurface）。根碰撞体刻意比受击体**更大更靠前**
        /// （表面 z=1.0 vs 受击体 z=1.55），旧规则会把伤害归属到 z=1.0 的空处命中点。</summary>
        private DamageableTarget BuildVictim(string name, float rootZ, bool withHitbox, bool tagRoot)
        {
            var root = Box(name, new Vector3(0f, 0f, rootZ), new Vector3(2f, 2f, 2f));
            if (tagRoot)
                HitVolumeTag.Assign(root, HitVolumeRole.MovementBlocker);

            var model = new GameObject("TP_Model");
            _temp.Add(model);
            model.transform.SetParent(root.transform, false);
            var target = model.AddComponent<DamageableTarget>();
            typeof(DamageableTarget).GetProperty("CurrentHealth")!
                .GetSetMethod(true)!.Invoke(target, new object[] { 100 });

            if (withHitbox)
            {
                var hitbox = Box("BodyHitbox", new Vector3(0f, 0f, rootZ), new Vector3(0.9f, 1.6f, 0.9f),
                    model.transform);
                hitbox.GetComponent<Collider>().isTrigger = true; // 与生产一致：受击体不吃移动阻挡
                HitVolumeTag.Assign(hitbox, HitVolumeRole.DamageSurface);
            }
            Physics.SyncTransforms();
            return target;
        }

        private HitscanResult Shoot(Vector3 origin, Vector3 dir, int damage = 30)
            => _resolver.ResolveHitscan(origin, dir, 50f, damage, ~0, _shooterRoot.transform);

        [Test]
        public void MovementBlockerNearest_FinalPointIsOnDamageSurface_AndDamagesOnce()
        {
            var victim = BuildVictim("Victim", 2f, withHitbox: true, tagRoot: true);

            var r = Shoot(Vector3.zero, Vector3.forward);

            Assert.That(r.Damaged, Is.True, "同一射线真实穿过受击体：必须正常造成伤害");
            Assert.That(r.Target, Is.SameAs(victim));
            Assert.That(victim.CurrentHealth, Is.EqualTo(70), "一发只有一次伤害");
            Assert.That(r.Point.z, Is.EqualTo(1.55f).Within(0.02f),
                "落点必须属于受击体表面，不是移动阻挡体外壳（z=1.0）——旧规则给的是外壳点");
            Assert.That(_resolver.LastSegmentMovementSkipped, Is.EqualTo(1),
                "移动阻挡体是被【跳过】的，不是被选中后置空（置空仍会挡枪）");
        }

        [Test]
        public void RayThroughMovementBlockerOnly_NoDamage_AndRayContinuesBehind()
        {
            // "模型外空处受击"的回归锚点：射线只穿过玩家移动外壳（未穿过任何受击体）
            var victim = BuildVictim("Victim", 2f, withHitbox: false, tagRoot: true);
            Box("Wall", new Vector3(0f, 0f, 10f), new Vector3(4f, 4f, 4f));

            var r = Shoot(Vector3.zero, Vector3.forward);

            Assert.That(victim.CurrentHealth, Is.EqualTo(100), "只穿过移动外壳不得掉血");
            Assert.That(r.Target, Is.Null);
            Assert.That(r.Damaged, Is.False);
            Assert.That(r.Hit, Is.True, "射线不得被不可见外壳截断：后方墙必须被命中");
            Assert.That(r.Point.z, Is.EqualTo(8f).Within(0.05f), "落点归后方墙（模型外的实弹孔来源）");
        }

        [Test]
        public void RayThroughMovementBlocker_NothingBehind_IsMiss()
        {
            BuildVictim("Victim", 2f, withHitbox: false, tagRoot: true);

            var r = Shoot(Vector3.zero, Vector3.forward);

            Assert.That(r.Hit, Is.False, "后方无对象时必须是 miss（旧行为：命中外壳 = Hit=true）");
            Assert.That(r.Point.z, Is.EqualTo(50f).Within(0.01f), "miss 落点为 origin+dir*maxRange 远点");
        }

        [Test]
        public void WallBeforeMovementBlocker_KeepsBlockingSemantics()
        {
            BuildVictim("Victim", 2f, withHitbox: true, tagRoot: true);
            Box("Wall", new Vector3(0f, 0f, 0.5f), new Vector3(4f, 4f, 1f)); // 表面 z=0

            var r = Shoot(new Vector3(0f, 0f, -2f), Vector3.forward, 30);

            Assert.That(r.Target, Is.Null, "墙在前：不得穿透墙体归属到玩家");
            Assert.That(r.Damaged, Is.False);
        }

        [Test]
        public void UnrelatedCharacterController_IsNotTreatedAsMovementBlocker()
        {
            // §5 红线：不跳过"无关控制器"。识别只认显式标记，不认组件类型/名字。
            var crate = new GameObject("Prop_WithController");
            _temp.Add(crate);
            var controller = crate.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;

            Assert.That(CombatResolver.IsMovementBlocker(controller), Is.False,
                "未标记的 CharacterController 判定行为不变");

            HitVolumeTag.Assign(crate, HitVolumeRole.MovementBlocker);
            Assert.That(CombatResolver.IsMovementBlocker(controller), Is.True,
                "玩家根控制器由运行时显式标记后排除");

            var plain = new GameObject("Prop_Plain");
            _temp.Add(plain);
            Assert.That(CombatResolver.IsMovementBlocker(plain.AddComponent<BoxCollider>()), Is.False);
            Assert.That(CombatResolver.IsMovementBlocker(null), Is.False, "空引用安全");
        }

        [Test]
        public void TaggedDamageSurface_StillAttributesViaParentChain()
        {
            // 生产形态完整装配（根=MovementBlocker + BodyHitbox=DamageSurface）：父链归属不回退
            var victim = BuildVictim("Victim", 3f, withHitbox: true, tagRoot: true);

            var r = Shoot(new Vector3(0f, 0f, 0f), Vector3.forward);

            Assert.That(r.Target, Is.SameAs(victim));
            Assert.That(r.Damaged, Is.True);
        }
    }
}
