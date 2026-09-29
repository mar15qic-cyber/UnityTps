using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using NUnit.Framework;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class SystemAuditFixTests
    {
        private static readonly Vector3 Origin = new(1234, 1456, 1678);
        private readonly List<Object> _objects = new();
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        private GameObject Make(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            go.transform.SetParent(parent, false);
            return go;
        }

        private BoxCollider Box(string name, float distance, Transform parent = null,
            HitBodyRegion region = HitBodyRegion.Torso)
        {
            var go = Make(name, parent);
            go.transform.position = Origin + Vector3.forward * (distance + .005f);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(.5f, .5f, .01f);
            if (parent != null) HitVolumeTag.Assign(go, HitVolumeRole.DamageSurface, region);
            return box;
        }

        [TearDown]
        public void Cleanup()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            Physics.SyncTransforms();
        }

        private static IEnumerable<RaycastHit[]> Permutations(RaycastHit[] hits)
        {
            if (hits.Length == 1) { yield return hits; yield break; }
            for (int i = 0; i < hits.Length; i++)
                foreach (var rest in Permutations(hits.Where((_, j) => j != i).ToArray()))
                    yield return new[] { hits[i] }.Concat(rest).ToArray();
        }

        [Test]
        public void EveryCandidateOrder_WallTwoCentimetresBeforeHeadAlwaysWins()
        {
            var victim = Make("Victim"); victim.AddComponent<DamageableTarget>();
            var wall = Box("Wall", 1f);
            Box("Head", 1.02f, victim.transform, HitBodyRegion.Head);
            Physics.SyncTransforms();
            var hits = Physics.RaycastAll(Origin, Vector3.forward, 2f, ~0, QueryTriggerInteraction.Collide);
            Assert.That(hits.Length, Is.EqualTo(2));
            foreach (var order in Permutations(hits))
                Assert.That(order[CombatResolver.SelectClosestDamageHit(order, order.Length, null, out _, out _)].collider,
                    Is.SameAs(wall));
        }

        [Test]
        public void EveryCandidateOrder_RegionToleranceCannotChainBeyondNearestSurface()
        {
            var victim = Make("Victim"); victim.AddComponent<DamageableTarget>();
            Box("Torso", 1f, victim.transform);
            var arm = Box("Arm", 1.025f, victim.transform, HitBodyRegion.Arm);
            Box("Head", 1.05f, victim.transform, HitBodyRegion.Head);
            Physics.SyncTransforms();
            var hits = Physics.RaycastAll(Origin, Vector3.forward, 2f, ~0, QueryTriggerInteraction.Collide);
            Assert.That(hits.Length, Is.EqualTo(3));
            foreach (var order in Permutations(hits))
                Assert.That(order[CombatResolver.SelectClosestDamageHit(order, order.Length, null, out _, out _)].collider,
                    Is.SameAs(arm));
        }

        [Test]
        public void SaturatedRayBuffer_FindsWallAmongMoreThanThirtyTwoIgnoredSelfVolumes()
        {
            var shooter = Make("Shooter"); var resolver = shooter.AddComponent<CombatResolver>();
            for (int i = 0; i < 45; i++) Box("Self" + i, 1f + i * .04f, shooter.transform);
            var wall = Box("NearestWorld", 4f);
            Physics.SyncTransforms();
            var result = resolver.ProbeDisplayedShot(Origin, Vector3.forward, 10f, ~0, shooter.transform);
            Assert.That(result.Hit, Is.True);
            Assert.That(result.ColliderName, Is.EqualTo(wall.name));
            Assert.That(result.SelfHitsSkipped, Is.EqualTo(45));
        }

        [Test]
        public void DeathDisablesAllDamageVolumes_RespawnRestoresOriginalEnabledStates()
        {
            var root = Make("Player"); var authority = root.AddComponent<NetworkCombatAuthority>();
            var model = Make("TP_Model", root.transform); var target = model.AddComponent<DamageableTarget>();
            typeof(NetworkCombatAuthority).GetField("_target", Private).SetValue(authority, target);
            var volumes = new List<Collider>();
            for (int i = 0; i < 11; i++) volumes.Add(Box("Damage" + i, 1f, model.transform));
            volumes[10].enabled = false;
            var movement = root.AddComponent<CharacterController>();
            HitVolumeTag.Assign(root, HitVolumeRole.MovementBlocker);
            typeof(NetworkCombatAuthority).GetMethod("ApplyDeathVisual", Private).Invoke(authority, null);
            Assert.That(volumes.All(c => !c.enabled), Is.True);
            Assert.That(movement.enabled, Is.True);
            typeof(NetworkCombatAuthority).GetMethod("ResetDeathVisual", Private).Invoke(authority, null);
            Assert.That(volumes.Take(10).All(c => c.enabled), Is.True);
            Assert.That(volumes[10].enabled, Is.False);
        }

        [Test]
        public void BlastIgnoresVictimRootController_ButRetainsRealWallOcclusion()
        {
            var root = Make("Victim"); root.transform.position = Origin + Vector3.forward * 2f;
            root.AddComponent<NetworkCombatAuthority>(); root.AddComponent<CharacterController>();
            var target = Make("TP_Model", root.transform); target.AddComponent<DamageableTarget>();
            Physics.SyncTransforms();
            Assert.That(ThrowableProjectile.IsBlastBlockedByWorld(Origin, root.transform.position), Is.False);
            Box("WorldWall", 1f); Physics.SyncTransforms();
            Assert.That(ThrowableProjectile.IsBlastBlockedByWorld(Origin, root.transform.position), Is.True);
        }

        [Test]
        public void ClearingPendingViewEffects_RemovesUnconfirmedButPreservesConfirmedImpact()
        {
            var pending = Make("PendingDecal"); var confirmed = Make("ConfirmedDecal");
            var registry = new PredictedShotRegistry();
            registry.Register(1, Vector3.zero, Vector3.up, true, 0, pending);
            registry.Register(2, Vector3.zero, Vector3.up, true, 0, confirmed);
            registry.ConsumePending(2, 0);
            registry.Clear(destroyPending: true);
            Assert.That(pending == null, Is.True);
            Assert.That(confirmed != null, Is.True);
        }

        [Test]
        public void LaserOriginSupportsMeshWithDiscardedCpuVertices()
        {
            var root = Make("Laser"); var filter = root.AddComponent<MeshFilter>();
            var mesh = new Mesh(); _objects.Add(mesh);
            mesh.vertices = new[] { new Vector3(-1, -1, -1), new Vector3(1, 1, 1), Vector3.zero };
            mesh.triangles = new[] { 0, 1, 2 }; mesh.RecalculateBounds(); mesh.UploadMeshData(true);
            filter.sharedMesh = mesh;
            var laser = root.AddComponent<LaserSightBeam>();
            var origin = (Vector3)typeof(LaserSightBeam).GetMethod("ResolveOrigin", Private).Invoke(laser, null);
            Assert.That(float.IsFinite(origin.x) && float.IsFinite(origin.y) && float.IsFinite(origin.z), Is.True);
            Assert.That(origin.sqrMagnitude, Is.GreaterThan(.5f), "use the front face, not the uninitialized device origin");
        }
    }
}
