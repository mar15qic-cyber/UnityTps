using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 生产装配链集成回归（2026-09-18 复核 R4）：上一轮的物理用例用手工打标的 BoxCollider 代表
    /// 移动阻挡体，只证明"标签过滤规则"成立，**没有**证明真实玩家初始化会把真实 CharacterController
    /// 标成 MovementBlocker、并把 BodyHitbox 留成可命中受击体。本文件补的是这条装配→判定的链：
    /// 直接调用 <see cref="PlayerNetworkAdapter"/> 的运行时装配入口，再用真实射线验证三件事：
    /// ① 生产生成的 BodyHitbox 确实可被命中并归属（trigger + 同层 + 父链）；
    /// ② 只穿过生产打标的真实 CharacterController 时零伤害，射线继续命中后墙；
    /// ③ 装配是幂等的（重复 OnStartNetwork 式调用不产生第二套受击体/角色标记）。
    ///
    /// EditMode 已知限制：CharacterController 需真正 Move 过才进射线命中集，故 CC 的"被跳过"
    /// 通过【标记正确性 + 后墙仍能命中且玩家零伤害】两路共同证明，不单靠物理命中。
    /// </summary>
    public sealed class PlayerHitVolumeWiringTests
    {
        private const BindingFlags NonPublic = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<Object> _temp = new();
        private GameObject _shooter;
        private CombatResolver _resolver;

        [SetUp]
        public void SetUp()
        {
            Physics.autoSyncTransforms = true;
            _shooter = new GameObject("Shooter_Wiring");
            _temp.Add(_shooter);
            _resolver = _shooter.AddComponent<CombatResolver>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _temp)
                if (o != null) Object.DestroyImmediate(o);
            _temp.Clear();
        }

        /// <summary>用生产入口搭一个玩家：根 CharacterController + TP_Model(DamageableTarget)，
        /// 然后调用 PlayerNetworkAdapter 的受击体/角色装配方法。</summary>
        private (GameObject root, DamageableTarget target, PlayerNetworkAdapter adapter) BuildWiredPlayer(
            string name, Vector3 position)
        {
            var root = new GameObject(name);
            _temp.Add(root);
            root.transform.position = position;
            var cc = root.AddComponent<CharacterController>();
            // 刻意比受击体"胖"：实机症状的前提就是根 CC 外壳与 BodyHitbox 不共形
            // （方案 §1.3：同一射线两段命中距离实测差 0.04–0.29m）。
            // CC: center 0.9 + height 2.6 → 覆盖 -0.4..2.2；hitbox → 覆盖 0..1.8。
            cc.height = 2.6f;
            cc.radius = 0.5f;
            cc.center = new Vector3(0f, 0.9f, 0f);

            var model = new GameObject("TP_Model");
            _temp.Add(model);
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = new Vector3(0f, 0f, 0.341f); // 正式 prefab 的作者偏移
            var target = model.AddComponent<DamageableTarget>();
            typeof(DamageableTarget).GetProperty("CurrentHealth")!
                .GetSetMethod(true)!.Invoke(target, new object[] { 100 });

            var adapter = root.AddComponent<PlayerNetworkAdapter>();
            Invoke(adapter, "EnsureBodyHitbox"); // 生产装配入口（运行时建 hitbox + 登记角色）
            Physics.SyncTransforms();
            return (root, target, adapter);
        }

        private static void Invoke(object target, string method)
        {
            var m = target.GetType().GetMethod(method, NonPublic);
            Assert.That(m, Is.Not.Null, $"{method} 必须存在（运行时装配入口）");
            m.Invoke(target, null);
        }

        [Test]
        public void ProductionWiring_MarksRealControllerAndKeepsHitboxQueryable()
        {
            var (root, _, _) = BuildWiredPlayer("Victim", new Vector3(0f, 0f, 6f));

            var cc = root.GetComponent<CharacterController>();
            Assert.That(CombatResolver.IsMovementBlocker(cc), Is.True,
                "真实根 CharacterController 必须被生产装配标为移动阻挡体");

            var hitbox = root.transform.Find("TP_Model/BodyHitbox");
            Assert.That(hitbox, Is.Not.Null, "装配入口应创建 BodyHitbox");
            var capsule = hitbox.GetComponent<CapsuleCollider>();
            Assert.That(capsule, Is.Not.Null);
            Assert.That(capsule.isTrigger, Is.True, "受击体保持 trigger（不挡移动）");
            Assert.That(capsule.enabled, Is.True);
            Assert.That(hitbox.gameObject.layer, Is.EqualTo(root.layer), "受击体与玩家根同层（hitMask 可命中）");
            var tag = hitbox.GetComponent<HitVolumeTag>();
            Assert.That(tag, Is.Not.Null);
            Assert.That(tag.Role, Is.EqualTo(HitVolumeRole.DamageSurface), "受击体登记为可归属受击面");
        }

        [Test]
        public void ProductionWiring_IsIdempotent_AndDoesNotStackVolumes()
        {
            var (root, _, adapter) = BuildWiredPlayer("Victim", new Vector3(0f, 0f, 6f));
            int hitboxesBefore = CountByName(root.transform, "BodyHitbox");
            int tagsBefore = CountComponent<HitVolumeTag>(root);

            Invoke(adapter, "EnsureBodyHitbox"); // 复用/重生成边界再来一次
            Physics.SyncTransforms();

            Assert.That(CountByName(root.transform, "BodyHitbox"), Is.EqualTo(hitboxesBefore),
                "重复装配不得再建一套受击体（否则一发会命中两个体）");
            Assert.That(CountComponent<HitVolumeTag>(root), Is.EqualTo(tagsBefore),
                "角色标记按 GO 复用，不得叠加");
        }

        [Test]
        public void ProductionHitbox_AttributesDamageOnce_AndAbsorbsTheRay()
        {
            var (_, target, _) = BuildWiredPlayer("Victim", new Vector3(0f, 0f, 6f));
            var wall = new GameObject("Wall");
            _temp.Add(wall);
            wall.transform.position = new Vector3(0f, 0f, 12f);
            wall.AddComponent<BoxCollider>().size = new Vector3(6f, 6f, 1f); // 表面 z=11.5
            Physics.SyncTransforms();

            var r = _resolver.ResolveHitscan(new Vector3(0f, 1.2f, 0f), Vector3.forward, 50f, 25, ~0,
                _shooter.transform);

            Assert.That(r.Damaged, Is.True, "生产生成的 BodyHitbox 必须真能被命中");
            Assert.That(r.Target, Is.SameAs(target));
            Assert.That(target.CurrentHealth, Is.EqualTo(75), "一发只结算一次");
            Assert.That(r.Point.z, Is.LessThan(11f), "受击体吸收子弹：不得穿到后墙");
        }

        [Test]
        public void OnlyThroughProductionController_PlayerTakesZeroDamage_WallBehindStillHit()
        {
            // "模型外空处受击"端到端回归：避开 BodyHitbox（瞄准其上方），射线只可能碰到玩家移动外壳
            var (_, target, _) = BuildWiredPlayer("Victim", new Vector3(0f, 0f, 6f));
            var wall = new GameObject("Wall");
            _temp.Add(wall);
            wall.transform.position = new Vector3(0f, 0f, 12f);
            wall.AddComponent<BoxCollider>().size = new Vector3(6f, 6f, 1f);
            Physics.SyncTransforms();

            // 生产几何：根 CC 外壳覆盖 -0.4..2.2m，BodyHitbox 覆盖 0..1.8m。
            // 2.0m 落在"只有移动外壳、没有任何受击面"的空处——正是用户看到"打模型后方空处也掉血
            // + 空气弹孔"的那条射线。修复后：零伤害、射线继续、落点在墙。
            var r = _resolver.ResolveHitscan(new Vector3(0f, 2.0f, 0f), Vector3.forward, 50f, 25, ~0,
                _shooter.transform);

            Assert.That(target.CurrentHealth, Is.EqualTo(100), "未命中受击体不得掉血");
            Assert.That(r.Target, Is.Null);
            Assert.That(r.Hit, Is.True, "射线不得被玩家外壳截断");
            Assert.That(r.Point.z, Is.EqualTo(11.5f).Within(0.05f), "落点归后墙");
            // 不在这里断言 LastSegmentMovementSkipped：EditMode 下 CharacterController 从不进入
            // 射线命中集（需 PlayMode 真实 Move），本例的物理命中集合里根本没有 CC。
            // "外壳被跳过而非被选中后置空"由两处共同证明：本文件
            // ProductionWiring_MarksRealControllerAndKeepsHitboxQueryable（真实 CC 被生产装配标成
            // MovementBlocker 且谓词识别）+ PlayerHitVolumeFilterTests（等体积标记体的计数与落点）。
        }

        private static int CountByName(Transform root, string name)
        {
            int count = 0;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i].name == name) count++;
            return count;
        }

        private static int CountComponent<T>(GameObject go) where T : Component
            => go.GetComponentsInChildren<T>(true).Length;
    }
}
