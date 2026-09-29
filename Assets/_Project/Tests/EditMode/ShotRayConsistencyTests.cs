using System;
using System.Reflection;
using Game.Core;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.HUD;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public sealed class ShotRayConsistencyTests
    {
        [TestCase("Day2_ServicePistol", 0f)]
        [TestCase("Day2_ServicePistol", 1f)]
        [TestCase("Day3_Handgun04", 0f)]
        [TestCase("Day3_Handgun04", 1f)]
        public void RealPistol_PresentedRayAndServerSnapshot_HitSameWallPoint(string asset, float ads)
        {
            var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/" + asset + ".asset");
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(
                "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            var client = new GameObject("ShotClient");
            var server = new GameObject("ShotServer");
            var wall = new GameObject("ShotTestWall");
            try
            {
                var origin = new Vector3(5000, 1.62f, 5000);
                wall.transform.position = origin + Vector3.forward * 20;
                wall.AddComponent<BoxCollider>().size = new Vector3(30, 30, .1f);
                Physics.SyncTransforms();
                var a = CreateController(client, def, balance);
                var b = CreateController(server, def, balance);
                server.transform.rotation = Quaternion.Euler(12, -8, 0);
                b.RestoreRecoilCompensationDebt(new Vector2(5, 2));
                var context = new WeaponFireContext(ads, 0, false, true, false);
                WeaponShot predicted = default, authoritative = default;
                a.OnShotFired += shot => predicted = shot;
                b.OnShotFired += shot => authoritative = shot;
                for (uint id = 1; id <= 8; id++)
                {
                    int seed = ShotAimPolicy.SpreadSeed(id, 3);
                    Set(a, "_shotContextOverride", (WeaponFireContext?)context);
                    Set(a, "_shotSeedOverride", (int?)seed);
                    a.SetPresentedAim(origin, Vector3.forward);
                    Assert.That(a.TryFire(), Is.True);
                    Assert.That(b.TryFireWithServerSnapshot(origin, Vector3.forward, context, seed, default), Is.True);
                    Assert.That(predicted.Result.Hit && authoritative.Result.Hit, Is.True);
                    Assert.That(Vector3.Distance(predicted.Result.Point, authoritative.Result.Point), Is.LessThan(.0001f));
                    Assert.That(Vector3.Angle(authoritative.FiredDirection, Vector3.forward),
                        Is.LessThanOrEqualTo(authoritative.FinalSpreadDegrees + .002f));
                    a.Runtime.Tick(1); b.Runtime.Tick(1);
                }
            }
            finally { Object.DestroyImmediate(client); Object.DestroyImmediate(server); Object.DestroyImmediate(wall); }
        }

        [Test]
        public void WeaponFire_ClientAndServerUseSameCrosshairAboveCover()
        {
            var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/Day2_ServicePistol.asset");
            var balance = AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(
                "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset");
            var client = new GameObject("CameraCoverClient"); var server = new GameObject("CameraCoverServer");
            var crate = new GameObject("RecordedLowCrate"); var wall = new GameObject("DistantAimedWall");
            try
            {
                Vector3 basePosition = new Vector3(7000, 0, 7000);
                client.transform.position = server.transform.position = basePosition;
                crate.transform.position = basePosition + new Vector3(0, .75f, 1.2f);
                crate.AddComponent<BoxCollider>().size = new Vector3(2, 1.5f, .5f);
                wall.transform.position = basePosition + new Vector3(0, 1.62f, 20);
                wall.AddComponent<BoxCollider>().size = new Vector3(5, 3, .1f);
                Physics.SyncTransforms();
                var a = CreateController(client, def, balance); var b = CreateController(server, def, balance);
                var eye = Game.Gameplay.Player.LeanProfile.Eye(basePosition, Quaternion.identity, 0);
                var ctx = new WeaponFireContext(1, 0, false, true, false);
                Set(a, "_shotContextOverride", (WeaponFireContext?)ctx); Set(a, "_shotSeedOverride", (int?)123);
                a.SetPresentedAim(eye, Vector3.forward);
                WeaponShot predicted = default, authoritative = default;
                a.OnShotFired += shot => predicted = shot; b.OnShotFired += shot => authoritative = shot;
                Assert.That(a.TryFire(), Is.True);
                Assert.That(b.TryFireWithServerSnapshot(eye, Vector3.forward, ctx, 123, default), Is.True);
                Assert.That(a.LastFireEvidence.FinalCollider, Is.EqualTo("DistantAimedWall"));
                Assert.That(b.LastFireEvidence.FinalCollider, Is.EqualTo("DistantAimedWall"));
                Assert.That(Vector3.Distance(predicted.Result.Point, authoritative.Result.Point), Is.LessThan(.001f));
                Assert.That(b.LastFireEvidence.CameraOnly, Is.True);
                Assert.That(a.Runtime.CurrentAmmo, Is.EqualTo(b.Runtime.CurrentAmmo));
                b.Runtime.Tick(1);
                int ammo = b.Runtime.CurrentAmmo;
                var embedded = crate.transform.position;
                Assert.That(b.TryFireWithServerSnapshot(embedded, Vector3.forward, ctx, 124, default), Is.False);
                Assert.That(b.Runtime.CurrentAmmo, Is.EqualTo(ammo), "invalid eye is rejected before consuming a round");
            }
            finally
            {
                Object.DestroyImmediate(client); Object.DestroyImmediate(server);
                Object.DestroyImmediate(crate); Object.DestroyImmediate(wall);
            }
        }

        [Test]
        public void CameraOriginHistory_AllowsRenderedMovementButRejectsFloatingEyeAboveCover()
        {
            var go = new GameObject("EyeHistory");
            try
            {
                var weapon = go.AddComponent<WeaponController>();
                var adapter = go.AddComponent<PlayerNetworkAdapter>();
                var pivot = new GameObject("CameraPivot"); pivot.transform.SetParent(go.transform);
                pivot.transform.localPosition = Game.Gameplay.Player.LeanProfile.Eye(Vector3.zero, Quaternion.identity, 0);
                Set(weapon, "aimPivot", pivot.transform);
                var remember = typeof(PlayerNetworkAdapter).GetMethod("RememberServerAim",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Vector3 eyeAtTwo = default;
                for (uint tick = 1; tick <= 3; tick++)
                {
                    go.transform.position = new Vector3(0, tick * .1f, tick * .2f);
                    remember.Invoke(adapter, new object[] {
                        new Game.Gameplay.Movement.MovementCommand(Vector2.zero, false, false, 0, 0, tick, 4) });
                    if (tick == 2) eyeAtTwo = weapon.AimOrigin;
                }
                var eye = weapon.AimOrigin;
                Assert.That(adapter.IsCameraOriginConsistent(3, 4, (eyeAtTwo + eye) * .5f), Is.True);
                Assert.That(adapter.IsCameraOriginConsistent(3, 4, eye + Vector3.up * .5f), Is.False);
                Assert.That(adapter.IsCameraOriginConsistent(3, 4, eye + Vector3.right * .5f), Is.False);
                Assert.That(adapter.IsCameraOriginConsistent(3, 5, eye), Is.False);
                Assert.That(adapter.IsCameraOriginConsistent(4, 4, eye), Is.False);
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static WeaponController CreateController(GameObject go, WeaponDefinition def, DemoBalanceConfig balance)
        {
            var actions = go.AddComponent<ActionSystem>();
            var combat = go.AddComponent<CombatResolver>();
            var result = go.AddComponent<WeaponController>();
            Set(result, "actionSystem", actions); Set(result, "combatResolver", combat);
            Set(result, "processLocalInput", true); Set(result, "hitMask", (LayerMask)~0);
            result.Initialize(def, balance);
            return result;
        }

        [Test]
        public void Spread_AllSamplesInAdvertisedCone_AndNoDownwardBias()
        {
            var random = new System.Random(1024);
            Vector2 mean = default;
            for (int i = 0; i < 20000; i++)
            {
                var direction = WeaponController.ApplySpread(Vector3.forward, 4, random);
                var offset = new Vector2(direction.x, direction.y) / direction.z;
                Assert.That(offset.magnitude, Is.LessThanOrEqualTo(Mathf.Tan(4 * Mathf.Deg2Rad) + .000001f));
                mean += offset;
            }
            Assert.That((mean / 20000).magnitude, Is.LessThan(.001f));
        }

        [Test]
        public void CrosshairStyle_CannotShrinkOrClampAwayTrueCone()
        {
            var style = ScriptableObject.CreateInstance<CrosshairConfig>();
            try
            {
                style.GapScale = .25f; style.MaxGap = 10;
                float gap = CrosshairPresenter.CalculateGap(10, 40, 2160, style);
                Assert.That(gap, Is.EqualTo(Mathf.Tan(10 * Mathf.Deg2Rad) * 1080 / Mathf.Tan(20 * Mathf.Deg2Rad)).Within(.001f));
            }
            finally { Object.DestroyImmediate(style); }
        }

        [Test]
        public void AimPolicy_RejectsNonfiniteFarBehindAndInvalidAds()
        {
            var shot = new TimedFireRequest { AimDirection = Vector3.forward, Ads01 = 1 };
            Assert.That(ShotAimPolicy.Validate(shot, Vector3.zero, Vector3.forward), Is.True);
            shot.AimOrigin.x = float.NaN;
            Assert.That(ShotAimPolicy.Validate(shot, Vector3.zero, Vector3.forward), Is.False);
            shot.AimOrigin = Vector3.right;
            Assert.That(ShotAimPolicy.Validate(shot, Vector3.zero, Vector3.forward), Is.False);
            shot.AimOrigin = Vector3.zero; shot.AimDirection = Vector3.back;
            Assert.That(ShotAimPolicy.Validate(shot, Vector3.zero, Vector3.forward), Is.False);
            shot.AimDirection = Vector3.forward; shot.Ads01 = 2;
            Assert.That(ShotAimPolicy.Validate(shot, Vector3.zero, Vector3.forward), Is.False);
        }

        [Test]
        public void DisplayedOrigin_CannotCrossThinWallEvenInsideDistanceTolerance()
        {
            var host = new GameObject("AimOriginTest"); var wall = new GameObject("ThinWall");
            try
            {
                var combat = host.AddComponent<CombatResolver>();
                var origin = new Vector3(5500, 1, 5500);
                wall.transform.position = origin + Vector3.forward * .2f;
                wall.AddComponent<BoxCollider>().size = new Vector3(2, 2, .02f);
                Physics.SyncTransforms();
                Assert.That(combat.IsAimOriginUnobstructed(origin, origin + Vector3.forward * .4f, ~0, host.transform), Is.False);
                Assert.That(combat.IsAimOriginUnobstructed(origin, origin + Vector3.right * .1f, ~0, host.transform), Is.True);
            }
            finally { Object.DestroyImmediate(host); Object.DestroyImmediate(wall); }
        }

        [TestCase(0f, -1.24f)]
        [TestCase(35f, -40f)]
        [TestCase(0f, -124f)]
        public void AbsoluteViewPitch_AfterRejectedShots_DoesNotInheritServerPitchDrift(
            float serverDebt, float viewPitch)
        {
            var go = new GameObject("PitchResync");
            try
            {
                var weapon = go.AddComponent<WeaponController>();
                var adapter = go.AddComponent<PlayerNetworkAdapter>();
                var pivot = new GameObject("CameraPivot"); pivot.transform.SetParent(go.transform);
                Set(weapon, "aimPivot", pivot.transform);
                Set(adapter, "localOnlyRoot", pivot);
                Set(adapter, "_weaponController", weapon);
                Set(adapter, "_remotePitch", 88.9f); // measured failure in the recording's DS log
                weapon.RestoreRecoilCompensationDebt(new Vector2(serverDebt, 0));
                var input = new Game.Gameplay.Movement.MovementCommand(Vector2.zero, false, false, 0, 1)
                { HasViewPitch = true, ViewPitch = viewPitch };
                Assert.That(input.IsValidNetworkInput, Is.True);
                typeof(PlayerNetworkAdapter).GetMethod("ApplyRemoteAimInput", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(adapter, new object[] { input });
                Vector3 direction = PlayerNetworkAdapter.ResolveInputAimDirection(input, Quaternion.identity, weapon);
                var shot = new TimedFireRequest { AimDirection = Quaternion.Euler(viewPitch, 0, 0) * Vector3.forward };
                Assert.That(ShotAimPolicy.Validate(shot, Vector3.zero, direction), Is.True);
                // Use a perpendicular vector even when aiming vertically.
                shot.AimDirection = Vector3.Cross(direction, Vector3.right).normalized;
                Assert.That(ShotAimPolicy.Validate(shot, Vector3.zero, direction), Is.False);
                Assert.That(adapter.RemotePitch, Is.EqualTo(Mathf.Clamp(viewPitch + serverDebt, -89, 89)).Within(.001f));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(-124.01f)]
        [TestCase(89.01f)]
        public void AbsoluteViewPitch_RejectsMalformedNetworkInput(float pitch)
        {
            var input = new Game.Gameplay.Movement.MovementCommand(Vector2.zero, false, false, 0, 1)
                { HasViewPitch = true, ViewPitch = pitch };
            Assert.That(input.IsValidNetworkInput, Is.False);
        }

        [Test]
        public void WorldAndCharacterShots_ShareOnePresentedTimeAndCombinedBoundedBudget()
        {
            ObserverTimeline.Reset();
            try
            {
                ObserverTimeline.Observe(100, 30, 10);
                Assert.That(ObserverTimeline.Evaluate(10), Is.LessThan(ObserverTimeline.LatestTick));
                // At 150 ms total transport/processing age, adding a 100 ms observer delay
                // incorrectly rejects a shot at a static wall even on a valid input tick.
                Assert.That(ShotTimingPolicy.ValidDisplayTick(ObserverTimeline.LatestTick, 104, 30), Is.True);
                Assert.That(ShotTimingPolicy.ValidDisplayTick(ObserverTimeline.Evaluate(10), 104, 30), Is.True);
                Assert.That(ShotTimingPolicy.ValidDisplayTick(ObserverTimeline.Evaluate(10), 116, 30), Is.False);
            }
            finally { ObserverTimeline.Reset(); }
        }

        private static void Set(object obj, string field, object value)
            => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
    }
}
