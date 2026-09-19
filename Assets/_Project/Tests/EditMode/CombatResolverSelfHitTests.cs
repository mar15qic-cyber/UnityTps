using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// Day4 实机审计 §1：命中查询对自身碰撞体的处理语义。
    /// 核心回归：旧版首命中为自身时返回自身命中点（Point 贴脸）→ 拖尾竖直/向下短线。
    /// 现语义：跳过 ignoreRoot 全部碰撞体取最近非自身命中；无非自身命中 Point=origin+dir*maxRange。
    /// EditMode 要点：AddComponent 不跑 Awake（DamageableTarget 血量需反射初始化）；
    /// 创建/移动碰撞体后须 Physics.SyncTransforms()。
    /// </summary>
    public sealed class CombatResolverSelfHitTests
    {
        private GameObject _player;
        private CombatResolver _resolver;
        private readonly System.Collections.Generic.List<Object> _temp = new();

        [SetUp]
        public void SetUp()
        {
            _player = new GameObject("Player_Self");
            _temp.Add(_player);
            _resolver = _player.AddComponent<CombatResolver>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _temp)
                if (o != null) Object.DestroyImmediate(o);
            _temp.Clear();
        }

        private GameObject Box(string name, Vector3 pos, float size, Transform parent = null)
        {
            var go = new GameObject(name);
            _temp.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var col = go.AddComponent<BoxCollider>();
            col.size = Vector3.one * size;
            Physics.SyncTransforms();
            return go;
        }

        [Test]
        public void SelfColliderFirst_Skipped_WallBehindStillHits()
        {
            // 自身碰撞体在 z=0.75 处（射线首位），墙在 z=10：必须跳过自身命中墙
            Box("SelfBody", new Vector3(0f, 0f, 1f), 0.5f, _player.transform);
            Box("Wall", new Vector3(0f, 0f, 10f), 4f);

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 10, ~0, _player.transform);

            Assert.That(r.Hit, Is.True, "自身碰撞体之后的墙应被命中");
            Assert.That(r.Damaged, Is.False);
            Assert.That(r.Point.z, Is.EqualTo(8f).Within(0.05f), "命中点应是墙表面（z=8），不是自身命中点");
            Assert.That(r.SelfHitsSkipped, Is.EqualTo(1), "应跳过 1 个自身碰撞体");
        }

        [Test]
        public void OnlySelfCollider_MissPointIsMaxRange_NotSelfHitPoint()
        {
            // 只有自身碰撞体：旧版返回 hitInfo.point（贴脸）——回归锚点
            Box("SelfBody", new Vector3(0f, 0f, 1f), 0.5f, _player.transform);

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 40f, 10, ~0, _player.transform);

            Assert.That(r.Hit, Is.False);
            Assert.That(r.Point, Is.EqualTo(Vector3.forward * 40f), "无命中时 Point 必须是 origin+dir*maxRange 远点");
            Assert.That(r.Point.z, Is.Not.EqualTo(0.75f).Within(0.05f), "绝不能返回自身命中点");
            Assert.That(r.SelfHitsSkipped, Is.EqualTo(1));
        }

        [Test]
        public void NoColliders_MissWithFarPoint()
        {
            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 30f, 10, ~0, _player.transform);

            Assert.That(r.Hit, Is.False);
            Assert.That(r.Point, Is.EqualTo(Vector3.forward * 30f));
            Assert.That(r.SelfHitsSkipped, Is.EqualTo(0));
        }

        [Test]
        public void NearWallBeatsFarWall_NearestNonSelfSelected()
        {
            // RaycastNonAlloc 结果无序：两墙面 z=3/z=8，必须取最近（z=3 面）
            Box("FarWall", new Vector3(0f, 0f, 10f), 4f);
            Box("NearWall", new Vector3(0f, 0f, 5f), 4f);

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 10, ~0, _player.transform);

            Assert.That(r.Hit, Is.True);
            Assert.That(r.Point.z, Is.EqualTo(3f).Within(0.05f), "应命中最近的非自身碰撞体（面 z=3）");
        }

        [Test]
        public void TargetBehindSelfCollider_GetsDamaged()
        {
            Box("SelfBody", new Vector3(0f, 0f, 1f), 0.5f, _player.transform);
            var targetGo = Box("Target", new Vector3(0f, 0f, 8f), 2f);
            var target = targetGo.AddComponent<DamageableTarget>();
            // EditMode AddComponent 不跑 Awake：反射初始化血量使 IsAlive=true
            typeof(DamageableTarget).GetProperty("CurrentHealth")!
                .GetSetMethod(true)!.Invoke(target, new object[] { 100 });

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 30, ~0, _player.transform);

            Assert.That(r.Damaged, Is.True, "自身碰撞体不应遮挡其后目标");
            Assert.That(r.Target, Is.SameAs(target));
            Assert.That(r.Point.z, Is.EqualTo(7f).Within(0.05f));
            Assert.That(r.SelfHitsSkipped, Is.EqualTo(1));
            Assert.That(target.CurrentHealth, Is.EqualTo(70), "伤害应穿透自身碰撞体应用到目标");
        }

        [Test]
        public void IgnoreRootNull_SelfLikeColliderStillHits()
        {
            // ignoreRoot=null：不跳过任何碰撞体（防御语义）
            Box("SomeBody", new Vector3(0f, 0f, 2f), 1f);

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 10, ~0, null);

            Assert.That(r.Hit, Is.True, "ignoreRoot=null 时普通碰撞体正常命中");
            Assert.That(r.SelfHitsSkipped, Is.EqualTo(0));
        }

        [Test]
        public void MultipleSelfColliders_AllSkipped()
        {
            // 玩家 root 下多个碰撞体（胶囊+武器等）全部在射线轴上，全部跳过
            Box("SelfBody", new Vector3(0f, 0f, 1f), 0.5f, _player.transform);
            Box("SelfWeapon", new Vector3(0f, 0f, 1.6f), 0.3f, _player.transform);
            Box("Wall", new Vector3(0f, 0f, 12f), 4f);

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 10, ~0, _player.transform);

            Assert.That(r.Hit, Is.True);
            Assert.That(r.Point.z, Is.EqualTo(10f).Within(0.05f));
            Assert.That(r.SelfHitsSkipped, Is.EqualTo(2), "两个自身碰撞体都应被跳过");
        }

        // ---- 审计 2026-09-15 §5.1：根 CC（无 DamageableTarget）最近命中 → 同根 BodyHitbox 归属回退 ----

        /// <summary>构建与运行时玩家同构的受害者：root 实体碰撞体（模拟 CharacterController）+
        /// TP_Model(DamageableTarget)/BodyHitbox 子碰撞体（同体积共心）。</summary>
        private DamageableTarget BuildVictim(string name, Vector3 rootPos)
        {
            var victim = Box(name, rootPos, 1f); // root 碰撞体表面 z=rootPos.z-0.5
            var model = new GameObject("TP_Model");
            _temp.Add(model);
            model.transform.SetParent(victim.transform, false);
            var target = model.AddComponent<DamageableTarget>();
            typeof(DamageableTarget).GetProperty("CurrentHealth")!
                .GetSetMethod(true)!.Invoke(target, new object[] { 100 });
            var hitboxGo = new GameObject("BodyHitbox");
            _temp.Add(hitboxGo);
            hitboxGo.transform.SetParent(model.transform, false);
            var hitbox = hitboxGo.AddComponent<BoxCollider>();
            hitbox.size = new Vector3(0.94f, 0.94f, 0.94f); // 与 root 碰撞体近似重合（表面差 0.03m，容差内）
            hitbox.center = Vector3.zero;
            Physics.SyncTransforms();
            return target;
        }

        [Test]
        public void VictimRootColliderNearest_AttributesToSameRootHitbox()
        {
            // 最近命中 = 受害者 root 碰撞体（父链无 DamageableTarget）→ 旧版 Hit=true/Target=null 零伤害；
            // 修复后必须经同根回退归属到 BodyHitbox 的 DamageableTarget
            var victim = BuildVictim("Victim", new Vector3(0f, 0f, 2f));

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 30, ~0, _player.transform);

            Assert.That(r.Hit, Is.True);
            Assert.That(r.Target, Is.SameAs(victim), "最近命中是根碰撞体时应回退归属同根 hitbox");
            Assert.That(r.Damaged, Is.True, "修复前该场景恒零伤害（无伤链路根因）");
            Assert.That(victim.CurrentHealth, Is.EqualTo(70));
        }

        [Test]
        public void WallBeforeVictim_BlocksAttribution()
        {
            // 墙在受害者之前：最近命中=墙（不同根）→ 绝不归属墙后的受害者（阻挡语义不变）
            BuildVictim("Victim", new Vector3(0f, 0f, 2f));
            Box("Wall", new Vector3(0f, 0f, 1f), 1f); // 表面 z=0.5，先于受害者表面 z=1.5

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 30, ~0, _player.transform);

            Assert.That(r.Hit, Is.True);
            Assert.That(r.Target, Is.Null, "墙体命中不得被归属到不同根的受害者");
            Assert.That(r.Point.z, Is.EqualTo(0.5f).Within(0.05f), "命中点应是墙表面");
        }

        [Test]
        public void SecondVictimHitboxNeverMisattributed()
        {
            // 最近命中=A 受害者 root，B 受害者 hitbox 更远（不同根）→ 只归属 A
            var victimA = BuildVictim("VictimA", new Vector3(0f, 0f, 2f));
            BuildVictim("VictimB", new Vector3(0f, 0f, 5f));

            var r = _resolver.ResolveHitscan(
                Vector3.zero, Vector3.forward, 50f, 30, ~0, _player.transform);

            Assert.That(r.Target, Is.SameAs(victimA), "同根回退只认最近命中所属的受害者根");
        }
    }
}
