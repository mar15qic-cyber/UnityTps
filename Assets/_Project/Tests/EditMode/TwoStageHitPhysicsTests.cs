using System.Collections.Generic;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// R10 审计修复的两段命中【物理级】回归（真实碰撞体 + Physics 射线，非纯枚举）：
    /// ① 伸墙：身体→枪口被近墙阻断 → 身体段遮挡命中（眼前墙），绝不伤害墙后相机可见目标；
    /// ② 门框/墙角：枪口路径被门框遮挡 → 改判枪口路径命中；
    /// ③ 无遮挡 → 相机候选正常伤害；
    /// ④ 霰弹多弹丸每发独立两段判定（命中点/伤害单次）；
    /// ⑤ 回溯恢复：LagComp rewind 把 hitbox 移到历史位姿供判定、EndRewind 精确恢复。
    /// EditMode 要点：AddComponent 不跑 Awake（DamageableTarget 反射初始化血量）；变换后 Physics.SyncTransforms。
    /// </summary>
    public sealed class TwoStageHitPhysicsTests
    {
        private GameObject _shooterRoot;
        private CombatResolver _resolver;
        private readonly List<Object> _temp = new();

        [SetUp]
        public void SetUp()
        {
            _shooterRoot = new GameObject("Shooter_TwoStage");
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

        private GameObject Box(string name, Vector3 pos, Vector3 size, Transform parent = null)
        {
            var go = new GameObject(name);
            _temp.Add(go);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var col = go.AddComponent<BoxCollider>();
            col.size = size;
            Physics.SyncTransforms();
            return go;
        }

        private DamageableTarget MakeTarget(GameObject go)
        {
            var target = go.AddComponent<DamageableTarget>();
            // EditMode AddComponent 不跑 Awake：反射初始化血量使 IsAlive=true
            typeof(DamageableTarget).GetProperty("CurrentHealth")!
                .GetSetMethod(true)!.Invoke(target, new object[] { 100 });
            return target;
        }

        private static (Vector3 muzzle, Vector3 body) AnchorsOf(Transform root)
        {
            var muzzle = TwoStageHitResolver.LogicalMuzzle(root.position, root.forward, root.up);
            var body = TwoStageHitResolver.BodyAnchor(root.position);
            return (muzzle, body);
        }

        [Test]
        public void MuzzlePokingIntoWall_BlocksShot_EvenWhenCameraSeesTargetBehindWall()
        {
            // 场景：玩家贴墙站，枪口（根前 0.6m）伸进 z=0.5m 处的薄墙；墙后 8m 处敌人（相机可见）。
            // 旧实现：身体→枪口受阻被"豁免"→ 相机候选伤害墙后目标（伸墙射击成立，错误）。
            // 新实现：身体段遮挡命中（打在眼前墙面），零伤害。
            _shooterRoot.transform.position = Vector3.zero;
            var wall = Box("Wall", new Vector3(0f, 1f, 0.5f), new Vector3(4f, 3f, 0.1f));
            var targetGo = Box("Enemy", new Vector3(0f, 1f, 8f), new Vector3(1f, 2f, 0.5f));
            var target = MakeTarget(targetGo);
            var (muzzle, body) = AnchorsOf(_shooterRoot.transform);

            var r = _resolver.ResolveHitscanTwoStage(
                new Vector3(0f, 1.6f, 0f), Vector3.forward, 50f, 25, ~0, _shooterRoot.transform, muzzle, body);

            Assert.That(r.Hit, Is.True, "射击应命中可信侧遮挡（眼前墙）");
            Assert.That(r.Damaged, Is.False, "伸墙时墙后相机可见目标不得受伤（R10 语义）");
            Assert.That(r.Target, Is.Null, "命中的是墙（无 DamageableTarget）");
            Assert.That(r.Point.z, Is.EqualTo(0.45f).Within(0.02f), "命中点=墙面朝射手一侧（墙心 0.5 厚 0.1 → 近面 0.45）");
            Assert.That(target.CurrentHealth, Is.EqualTo(100), "目标满血");
        }

        [Test]
        public void DoorFrameOccludingMuzzlePath_ReroutesToMuzzleHit()
        {
            // 门框：枪口正前 1.5m 竖柱挡住枪口→候选路径；相机（1.7m 高）视线从柱顶越过后命中敌人。
            // 相机候选 = 敌人；枪口路径被柱挡 → 改判枪口路径命中（柱）。
            _shooterRoot.transform.position = Vector3.zero;
            var frame = Box("DoorFrame", new Vector3(0f, 1f, 1.5f), new Vector3(0.2f, 2f, 0.2f));
            var targetGo = Box("Enemy", new Vector3(0f, 1f, 9f), new Vector3(1f, 2f, 0.5f));
            MakeTarget(targetGo);
            var (muzzle, body) = AnchorsOf(_shooterRoot.transform);

            var r = _resolver.ResolveHitscanTwoStage(
                new Vector3(0f, 1.6f, 0f), Vector3.forward, 50f, 25, ~0, _shooterRoot.transform, muzzle, body);

            Assert.That(r.Hit, Is.True);
            Assert.That(r.Damaged, Is.False, "枪口路径被门框挡住：打在门框上");
            Assert.That(r.Point.z, Is.EqualTo(1.4f).Within(0.05f), "命中点=门框近面");
        }

        [Test]
        public void CornerShot_MuzzleClearPath_DamagesCameraCandidate()
        {
            // 墙角场景：墙偏在枪口路径之外（x=1.2 侧柱），枪口→候选路径干净 → 相机候选伤害成立
            _shooterRoot.transform.position = new Vector3(-1f, 0f, 0f);
            Box("CornerWall", new Vector3(1.2f, 1f, 2f), new Vector3(0.2f, 2f, 0.2f));
            var targetGo = Box("Enemy", new Vector3(-1f, 1f, 8f), new Vector3(1f, 2f, 0.5f));
            var target = MakeTarget(targetGo);
            var (muzzle, body) = AnchorsOf(_shooterRoot.transform);

            var r = _resolver.ResolveHitscanTwoStage(
                new Vector3(-1f, 1.6f, 0f), Vector3.forward, 50f, 25, ~0, _shooterRoot.transform, muzzle, body);

            Assert.That(r.Damaged, Is.True, "无遮挡走廊射击应命中相机候选");
            Assert.That(r.Target, Is.SameAs(target));
            Assert.That(target.CurrentHealth, Is.EqualTo(75));
        }

        [Test]
        public void Pellets_EachResolvedIndependently_WithSingleDamageApplication()
        {
            // 霰弹：两发方向一发穿门洞（命中敌人）、一发被门柱挡（命中柱）——每发独立两段，无重复伤害
            _shooterRoot.transform.position = Vector3.zero;
            Box("DoorFrameLeft", new Vector3(-0.6f, 1f, 1.5f), new Vector3(0.2f, 2f, 0.2f));
            var targetGo = Box("Enemy", new Vector3(0.4f, 1f, 8f), new Vector3(0.6f, 2f, 0.5f));
            var target = MakeTarget(targetGo);
            var (muzzle, body) = AnchorsOf(_shooterRoot.transform);

            var blocked = _resolver.ResolveHitscanTwoStage(
                new Vector3(0f, 1.6f, 0f), new Vector3(-0.4f, 0f, 1f).normalized, 50f, 25, ~0,
                _shooterRoot.transform, muzzle, body);
            var clean = _resolver.ResolveHitscanTwoStage(
                new Vector3(0f, 1.6f, 0f), new Vector3(0.05f, 0f, 1f).normalized, 50f, 25, ~0,
                _shooterRoot.transform, muzzle, body);

            Assert.That(blocked.Damaged, Is.False, "偏 door 柱方向的弹丸应被门框挡住");
            Assert.That(clean.Damaged, Is.True, "穿门洞的弹丸应命中敌人");
            Assert.That(target.CurrentHealth, Is.EqualTo(75), "两段链路每发只应用一次伤害（25 伤 ×1）");
        }

        [Test]
        public void LagCompensationRewind_MovesHitboxToHistory_AndRestoresExactly()
        {
            // 回溯恢复：tick=10 记录位置 A → 移到 B 记 tick=20 → rewind(10) 命中 A 位姿 → EndRewind 回 B
            var go = new GameObject("LagCompHost");
            _temp.Add(go);
            var comp = go.AddComponent<ServerLagCompensation>();
            ServerLagCompensation.Enabled = true;

            var root = new GameObject("RewindPlayer");
            _temp.Add(root);
            var hitboxGo = new GameObject("BodyHitbox");
            _temp.Add(hitboxGo);
            hitboxGo.transform.SetParent(root.transform, false);
            hitboxGo.transform.position = new Vector3(0f, 1f, 5f);
            var collider = hitboxGo.AddComponent<BoxCollider>();
            collider.size = new Vector3(1f, 2f, 0.5f);
            comp.RegisterPlayer(root.transform, new[] { collider });

            Physics.SyncTransforms();
            comp.Capture(10); // 历史位姿：z=5
            hitboxGo.transform.position = new Vector3(0f, 1f, 30f);
            Physics.SyncTransforms();
            comp.Capture(20); // 当前位姿：z=30

            Assert.That(comp.TryBeginRewind(10), Is.True, "窗口内历史可回滚");
            try
            {
                Physics.SyncTransforms();
                var hitAtHistory = Physics.Raycast(
                    new Ray(new Vector3(0f, 1f, 0f), Vector3.forward), out var historyHit, 50f, ~0,
                    QueryTriggerInteraction.Ignore);
                Assert.That(hitAtHistory, Is.True, "回滚后 hitbox 回到历史位姿 z=5，应被命中");
                Assert.That(historyHit.point.z, Is.EqualTo(4.75f).Within(0.05f));
            }
            finally
            {
                comp.EndRewind(); // 回溯恢复：判定后必须精确回到回滚前位姿
            }

            Physics.SyncTransforms();
            Assert.That(hitboxGo.transform.position.z, Is.EqualTo(30f).Within(1e-4f), "EndRewind 恢复当前位姿");
            var hitNow = Physics.Raycast(
                new Ray(new Vector3(0f, 1f, 0f), Vector3.forward), out _, 10f, ~0,
                QueryTriggerInteraction.Ignore);
            Assert.That(hitNow, Is.False, "恢复后 hitbox 已离开近距射线（回到 z=30）");
        }
    }
}
