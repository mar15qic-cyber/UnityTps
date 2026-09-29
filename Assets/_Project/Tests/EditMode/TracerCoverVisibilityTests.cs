using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Network;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public sealed class TracerCoverVisibilityTests
    {
        private static readonly Vector3 Base = new(8200, 0, 8200);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<GameObject> _objects = new();
        private readonly TracerVisibility _visibility = new();

        private GameObject Make(string name, Vector3 position)
        {
            var go = new GameObject(name); go.transform.position = position; _objects.Add(go); return go;
        }
        private GameObject Box(Vector3 position, Vector3 size)
        {
            var go = Make("Environment", position); go.AddComponent<BoxCollider>().size = size;
            Physics.SyncTransforms(); return go;
        }
        private static object Call(object obj, string method, params object[] args)
            => obj.GetType().GetMethod(method, Private).Invoke(obj, args);
        private static void Set(object obj, string field, object value)
            => obj.GetType().GetField(field, Private).SetValue(obj, value);
        private static T Get<T>(object obj, string field)
            => (T)obj.GetType().GetField(field, Private).GetValue(obj);
        private static LineRenderer[] Visible(Component view)
            => view.GetComponentsInChildren<LineRenderer>().Where(l => l.enabled).ToArray();
        private WeaponView Owner(Transform muzzle)
        {
            var view = Make("OwnerView", Base).AddComponent<WeaponView>();
            Set(view, "muzzle", muzzle); Set(view, "maxTracerLength", 100f);
            Call(view, "BuildEffects"); return view;
        }
        private RemoteShotFxView Observer()
        {
            var view = Make("ObserverView", Base).AddComponent<RemoteShotFxView>();
            Call(view, "BuildPool"); return view;
        }
        private static RemoteShotPresentation Shot(Vector3 end)
            => new() { FinalPoint = end, PelletCount = 1 };

        [TearDown]
        public void Cleanup()
        {
            foreach (var go in _objects)
            {
                if (go == null) continue;
                foreach (var view in go.GetComponents<WeaponView>())
                {
                    var material = Get<Material>(view, "_tracerMaterial");
                    Set(view, "_tracerMaterial", null); if (material != null) Object.DestroyImmediate(material);
                }
                foreach (var view in go.GetComponents<RemoteShotFxView>())
                {
                    var material = Get<Material>(view, "_material");
                    Set(view, "_material", null); if (material != null) Object.DestroyImmediate(material);
                }
                Object.DestroyImmediate(go);
            }
            _objects.Clear();
        }

        [TestCase(-1f)] [TestCase(0f)] [TestCase(1f)]
        public void CoverHidesCosmeticLine_ButEyeShotStillDamagesAndConsumesOneRound(float lean)
        {
            var root = Make("Shooter", Base);
            var controller = root.AddComponent<WeaponController>();
            Set(controller, "actionSystem", root.GetComponent<Game.Gameplay.Action.ActionSystem>());
            Set(controller, "combatResolver", root.GetComponent<CombatResolver>());
            var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>("Assets/_Project/ScriptableObjects/Weapons/Day2_ServicePistol.asset");
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>("Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            controller.Initialize(def, balance);
            Box(Base + new Vector3(0, .75f, 1.2f), new Vector3(2, 1.5f, .5f));
            var target = Box(Base + new Vector3(0, 1.62f, 15), new Vector3(3, .2f, .2f)).AddComponent<DamageableTarget>();
            typeof(DamageableTarget).GetProperty("CurrentHealth").GetSetMethod(true).Invoke(target, new object[] { 100 });
            var muzzle = Make("VisibleMuzzle", LeanProfile.Muzzle(Base, Quaternion.identity, lean)).transform;
            muzzle.SetParent(root.transform, true);
            var owner = Owner(muzzle); var observer = Observer();
            WeaponShot fired = default;
            controller.OnShotFired += shot => { fired = shot; Call(owner, "HandleLocalAuthoritativeShot", shot); };
            int before = controller.Runtime.CurrentAmmo;
            Assert.That(controller.TryFireWithServerSnapshot(LeanProfile.Eye(Base, Quaternion.identity, lean),
                Vector3.forward, new WeaponFireContext(1, 0, false, true, false), 123, default), Is.True);
            Call(owner, "LateUpdate");
            Call(observer, "DrawShot", muzzle, RemoteShotPresentation.FromShot(fired, 0));
            Assert.That(fired.Result.Damaged, Is.True);
            Assert.That(target.CurrentHealth, Is.EqualTo(100 - fired.Result.DamageAmount));
            Assert.That(controller.Runtime.CurrentAmmo, Is.EqualTo(before - 1));
            Assert.That(Visible(owner), Is.Empty); Assert.That(Visible(observer), Is.Empty);
            Assert.That(Get<Light>(owner, "_muzzleLight").enabled, Is.True, "hidden tracer must not cancel flash");
        }

        [Test]
        public void SurfaceEndpointAllowed_ButEarlierWallAndEmbeddedStartHidden()
        {
            Vector3 start = Base + Vector3.up;
            Box(start + Vector3.forward * 10.5f, Vector3.one);
            Assert.That(_visibility.CanShow(start, start + Vector3.forward * 10, null, ~0), Is.True);
            Assert.That(_visibility.CanShow(start, start + Vector3.forward * 11, null, ~0), Is.False);
            Assert.That(_visibility.CanShow(start + Vector3.forward * 10.5f, start, null, ~0), Is.False);
        }

        [TestCase("self")] [TestCase("trigger")] [TestCase("movement")] [TestCase("character")]
        public void NonEnvironmentDoesNotHideLine(string kind)
        {
            var root = Make("Shooter", Base);
            var obstacle = Box(Base + Vector3.forward * 2, Vector3.one);
            if (kind == "self") obstacle.transform.SetParent(root.transform, true);
            if (kind == "trigger") obstacle.GetComponent<Collider>().isTrigger = true;
            if (kind == "movement") HitVolumeTag.Assign(obstacle, HitVolumeRole.MovementBlocker);
            if (kind == "character") obstacle.AddComponent<PlayerNetworkAdapter>();
            Physics.SyncTransforms();
            Assert.That(_visibility.CanShow(Base, Base + Vector3.forward * 5, root.transform, ~0), Is.True);
        }

        [Test]
        public void DestructibleEnvironmentStillOccludes_AndHonorsMask()
        {
            var wall = Box(Base + Vector3.forward * 2, Vector3.one);
            wall.AddComponent<DamageableTarget>(); wall.layer = 2; Physics.SyncTransforms();
            Assert.That(_visibility.CanShow(Base, Base + Vector3.forward * 5, null, ~0), Is.False);
            Assert.That(_visibility.CanShow(Base, Base + Vector3.forward * 5, null, ~(1 << 2)), Is.True);
        }

        [TestCase(true)] [TestCase(false)]
        public void SaturatedQueriesHideConservatively(bool overlap)
        {
            var root = Make("Shooter", Base);
            for (int i = 0; i < (overlap ? 32 : 64); i++)
            {
                var box = Box(Base + Vector3.forward * (overlap ? 0 : 1 + i * .1f), Vector3.one * .05f);
                box.transform.SetParent(root.transform, true);
            }
            Physics.SyncTransforms();
            Assert.That(_visibility.CanShow(Base, Base + Vector3.forward * 10, root.transform, ~0), Is.False);
        }

        [Test]
        public void InvalidAndDegeneratePointsHidden()
        {
            Assert.That(_visibility.CanShow(Base, new Vector3(float.NaN, 0, 0), null, ~0), Is.False);
            Assert.That(_visibility.CanShow(new Vector3(float.PositiveInfinity, 0, 0), Base, null, ~0), Is.False);
            Assert.That(_visibility.CanShow(Base, Base, null, ~0), Is.False);
        }

        [TestCase(true)] [TestCase(false)]
        public void MovingMuzzleIntoCoverHidesUntilExpiry_EvenAfterMovingOut(bool fp)
        {
            var muzzle = Make("Muzzle", Base + Vector3.up * 1.62f).transform;
            Component view = fp ? Owner(muzzle) : Observer();
            var end = Base + new Vector3(0, 1.62f, 15);
            Box(Base + new Vector3(0, .75f, 1.2f), new Vector3(2, 1.5f, .5f));
            if (fp) Call(view, "SpawnTracer", muzzle.position, end, 2u);
            else Call(view, "DrawShot", muzzle, Shot(end));
            Assert.That(Visible(view).Length, Is.EqualTo(1));
            muzzle.position = Base + Vector3.up * 1.4f; Call(view, "LateUpdate");
            Assert.That(Visible(view), Is.Empty);
            muzzle.position = Base + Vector3.up * 1.62f; Call(view, "Update"); Call(view, "LateUpdate");
            Assert.That(Visible(view), Is.Empty, "old hidden tracer must never reappear");
        }

        [TestCase(true)] [TestCase(false)]
        public void MissingHiddenOrDestroyedMuzzleNeverFallsBackToEye(bool fp)
        {
            var muzzle = Make("Muzzle", Base).transform;
            Component view = fp ? Owner(muzzle) : Observer();
            var end = Base + Vector3.forward * 4;
            if (fp) Call(view, "SpawnTracer", Base, end, 3u);
            else Call(view, "DrawShot", muzzle, Shot(end));
            Assert.That(Visible(view).Length, Is.EqualTo(1));
            muzzle.gameObject.SetActive(false); Call(view, "LateUpdate");
            Assert.That(Visible(view), Is.Empty);
            Object.DestroyImmediate(muzzle.gameObject); Call(view, "LateUpdate");
            if (fp) Call(view, "SpawnTracer", Base, end, 4u);
            else Call(view, "DrawShot", null, Shot(end));
            Assert.That(Visible(view), Is.Empty);
        }

        [Test]
        public void ObserverPelletsAreIndependentlyHidden()
        {
            var muzzle = Make("Muzzle", Base + Vector3.up * 1.4f).transform;
            var view = Observer();
            Box(Base + new Vector3(0, .75f, 1.2f), new Vector3(2, 1.5f, .5f));
            var clear = Base + new Vector3(0, 6, 4);
            Call(view, "DrawShot", muzzle, new RemoteShotPresentation { PelletCount = 2,
                PelletPoints = new[] { Base + new Vector3(0, 1.62f, 15), clear } });
            Assert.That(Visible(view).Length, Is.EqualTo(1));
            Assert.That(Visible(view)[0].GetPosition(1), Is.EqualTo(clear));
        }

        [TestCase(true)] [TestCase(false)]
        public void OwnerConfirmationCorrectsEachPellet_BeforeOrAfterFirstDraw(bool beforeDraw)
        {
            var muzzle = Make("Muzzle", Base).transform; var view = Owner(muzzle);
            var pellets = new[] { new HitscanResult(false, false, Base + Vector3.forward * 4, Vector3.up, null),
                new HitscanResult(false, false, Base + new Vector3(1, 0, 4), Vector3.up, null) };
            // Register through the production prediction path, including the pellet-decal early return.
            Set(view, "_registry", new PredictedShotRegistry());
            var predicted = new WeaponShot(Base, Vector3.forward, Vector3.forward, pellets[0], 0, default, 0, 1, pellets);
            Call(view, "HandleOwnerPredictedShot", predicted, 7u);
            if (!beforeDraw) Call(view, "LateUpdate");
            var ends = new[] { Base + new Vector3(-1, 0, 4), Base + new Vector3(2, 0, 4) };
            Call(view, "HandleShotConfirmed", new RemoteShotPresentation { ShotRequestId = 7, PelletCount = 2,
                PelletPoints = ends }, true);
            Call(view, "LateUpdate");
            CollectionAssert.AreEquivalent(ends, Visible(view).Select(l => l.GetPosition(1)).ToArray());
        }

        [TestCase(true)] [TestCase(false)]
        public void RejectedOrExpiredPredictionsCannotReplay(bool reject)
        {
            var muzzle = Make("Muzzle", Base).transform; var view = Owner(muzzle);
            Set(view, "_registry", new PredictedShotRegistry());
            var result = new HitscanResult(false, false, Base + Vector3.forward * 4, Vector3.up, null);
            Call(view, "HandleOwnerPredictedShot", new WeaponShot(Base, Vector3.forward, result), 9u);
            Call(view, "LateUpdate");
            if (!reject)
            {
                foreach (var segment in Get<Array>(view, "_overlayTracers"))
                    segment.GetType().GetField("Timer").SetValue(segment, 0f);
                foreach (var line in Visible(view)) line.enabled = false;
            }
            Call(view, "HandleShotConfirmed", new RemoteShotPresentation { ShotRequestId = 9, PelletCount = 1,
                FinalPoint = Base + Vector3.forward * 3 }, !reject);
            Call(view, "LateUpdate"); Assert.That(Visible(view), Is.Empty);
        }

        [Test]
        public void OwnerQueriesProjectedWorldStart_NotOverlayGunPosition()
        {
            var world = Make("WorldCamera", Base + Vector3.up * 1.6f).AddComponent<UnityEngine.Camera>();
            var fp = Make("OverlayCamera", world.transform.position).AddComponent<UnityEngine.Camera>();
            world.fieldOfView = 60; fp.fieldOfView = 45; world.aspect = fp.aspect = 16f / 9;
            var muzzle = Make("Muzzle", world.transform.position + new Vector3(.3f, -.2f, .6f)).transform;
            var view = Owner(muzzle); Set(view, "_worldCamera", world); Set(view, "_fpCamera", fp);
            var a = Game.Presentation.Camera.CameraProjection.From(fp);
            var b = Game.Presentation.Camera.CameraProjection.From(world);
            Assert.That(Game.Presentation.Camera.CameraProjection.TryMatchAcrossCameras(a, b,
                muzzle.position, Mathf.Max(.02f, b.NearClip + .001f), b.FarClip * .9f, out var projected), Is.True);
            Assert.That(Vector3.Distance(projected, muzzle.position), Is.GreaterThan(.02f));
            Box(projected, Vector3.one * .01f);
            var end = world.transform.position + Vector3.forward * 20;
            Assert.That(_visibility.CanShow(muzzle.position, end, null, ~0), Is.True);
            Call(view, "SpawnTracer", muzzle.position, end, 12u);
            Assert.That(Visible(view), Is.Empty);
        }

        [Test]
        public void ConfirmedEndpointRechecksCover_AndDoesNotHideAnotherRequest()
        {
            var muzzle = Make("Muzzle", Base).transform; var view = Owner(muzzle);
            Set(view, "_registry", new PredictedShotRegistry());
            var result = new HitscanResult(false, false, Base + Vector3.forward * 4, Vector3.up, null);
            Call(view, "HandleOwnerPredictedShot", new WeaponShot(Base, Vector3.forward, result), 15u);
            Call(view, "HandleOwnerPredictedShot", new WeaponShot(Base, Vector3.forward, result), 16u);
            Call(view, "LateUpdate"); Assert.That(Visible(view).Length, Is.EqualTo(2));
            Box(Base + new Vector3(1, 0, 2), new Vector3(.2f, 1, .2f));
            Call(view, "HandleShotConfirmed", new RemoteShotPresentation { ShotRequestId = 15,
                PelletCount = 1, FinalPoint = Base + new Vector3(2, 0, 4) }, true);
            Assert.That(Visible(view).Length, Is.EqualTo(1));
            Assert.That(Visible(view)[0].GetPosition(1), Is.EqualTo(result.Point));
        }

        [Test]
        public void ObserverNeverSubmitsNonfiniteGeometryToRenderer()
        {
            var muzzle = Make("Muzzle", Base).transform; var view = Observer();
            Call(view, "DrawShot", muzzle, Shot(new Vector3(float.NaN, 0, 0)));
            Assert.That(Visible(view), Is.Empty);
        }

        [Test]
        public void RejectionBeforeFirstDrawClearsQueuedPellets()
        {
            var muzzle = Make("Muzzle", Base).transform; var view = Owner(muzzle);
            Set(view, "_registry", new PredictedShotRegistry());
            var result = new HitscanResult(false, false, Base + Vector3.forward * 4, Vector3.up, null);
            Call(view, "HandleOwnerPredictedShot", new WeaponShot(Base, Vector3.forward, result), 19u);
            Call(view, "HandleShotConfirmed", new RemoteShotPresentation { ShotRequestId = 19 }, false);
            Call(view, "LateUpdate"); Assert.That(Visible(view), Is.Empty);
        }
    }
}
