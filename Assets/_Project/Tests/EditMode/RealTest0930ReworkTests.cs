using System.Collections.Generic;
using System.Reflection;
using Game.Gameplay.Network;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Gameplay.Tests
{
    public sealed class RealTest0930ReworkTests
    {
        public static IEnumerable<TestCaseData> AvailabilityCases()
        {
            foreach (bool team in new[] { false, true })
            foreach (bool zone in new[] { false, true })
            foreach (bool fired in new[] { false, true })
            foreach (bool left in new[] { false, true })
            foreach (bool busy in new[] { false, true })
                yield return new TestCaseData(team, zone, fired, left, busy);
        }

        [TestCaseSource(nameof(AvailabilityCases))]
        public void ReloadCannotRestoreLostBackpackEligibility(bool team, bool zone, bool fired, bool left, bool busy)
        {
            bool allowed = zone && (team ? !(fired && left) : !fired && !left);
            var actual = BackpackSwitchPolicy.EvaluateAvailability(team, zone, fired, left, busy);
            Assert.That(actual == BackpackSwitchPolicy.DenyReason.None || actual == BackpackSwitchPolicy.DenyReason.PendingShots,
                Is.EqualTo(allowed));
            if (allowed) Assert.That(actual, Is.EqualTo(busy ? BackpackSwitchPolicy.DenyReason.PendingShots : BackpackSwitchPolicy.DenyReason.None));
        }

        [TestCase("FP_Shotgun01_View.prefab")]
        [TestCase("LPW/FP/FP_Shotgun1_01_View.prefab")]
        [TestCase("LPW/FP/FP_Shotgun2_01_View.prefab")]
        [TestCase("LPW/FP/FP_Shotgun3_01_View.prefab")]
        [TestCase("LPW/FP/FP_Shotgun4_01_View.prefab")]
        [TestCase("LPW/FP/FP_Shotgun5_01_View.prefab")]
        public void ShotgunUsesUntouchedImportedMeshAndAuthoredCameraDepth(string file)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Weapons/" + file);
            var arms = System.Array.Find(root.GetComponentsInChildren<SkinnedMeshRenderer>(true), r => r.name == "arms");
            Assert.That(AssetDatabase.GetAssetPath(arms.sharedMesh), Is.EqualTo("Assets/Low Poly FPS Pack/Components/Meshes/Arms/Shotgun_01/arms_shotgun_01.fbx"));
            Assert.That(arms.sharedMesh.vertexCount, Is.EqualTo(2016));
            var profile = root.GetComponent<FPViewFramingProfile>();
            Assert.That(profile, Is.Not.Null);
            Assert.That(Vector3.Distance(profile.AuthoringCameraLocalPosition, root.transform.InverseTransformPoint(root.transform.Find("Armature/camera").position)), Is.LessThan(.00001));
            Assert.That(AssetDatabase.LoadAssetAtPath<Mesh>("Assets/_Project/Prefabs/Weapons/ShotgunSleeves.asset"), Is.Null);
        }

        [Test]
        public void SwitchingAwayRestoresOverlayPoseWithoutMovingWorldCamera()
        {
            var world = new GameObject("World", typeof(UnityEngine.Camera));
            var overlay = new GameObject("FP", typeof(UnityEngine.Camera));
            var view = new GameObject("Shotgun", typeof(FPViewFramingProfile));
            try
            {
                overlay.transform.SetParent(world.transform, false);
                var baseline = new Vector3(-.047f, .082f, -.299f);
                overlay.transform.localPosition = baseline;
                overlay.GetComponent<UnityEngine.Camera>().cullingMask = 1 << LayerMask.NameToLayer("FirstPersonView");
                world.transform.position = new Vector3(2, 3, 4);
                world.AddComponent<FPWeaponRig>();
                var framing = world.AddComponent<FPWeaponCameraFraming>();
                var apply = typeof(FPWeaponCameraFraming).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance);
                var authored = new Vector3(0, .0856f, -.1787f);
                view.GetComponent<FPViewFramingProfile>().Configure(authored);
                apply.Invoke(framing, new object[] { view });
                Assert.That(overlay.transform.localPosition, Is.EqualTo(authored));
                Assert.That(world.transform.position, Is.EqualTo(new Vector3(2, 3, 4)));
                apply.Invoke(framing, new object[] { null });
                Assert.That(overlay.transform.localPosition, Is.EqualTo(baseline));
            }
            finally { Object.DestroyImmediate(view); Object.DestroyImmediate(world); }
        }

        [Test]
        public void OverlayShadowIsolationDoesNotDisableWorldOrThirdPersonShadows()
        {
            var owner = new GameObject("FP");
            var fp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var tp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var rig = owner.AddComponent<FPWeaponRig>();
                rig.RegisterViewRenderer(fp.GetComponent<Renderer>());
                Assert.That(fp.GetComponent<Renderer>().shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
                Assert.That(tp.GetComponent<Renderer>().shadowCastingMode, Is.EqualTo(ShadowCastingMode.On));
                Assert.That(fp.GetComponent<Renderer>().enabled, Is.True);
            }
            finally { Object.DestroyImmediate(owner); Object.DestroyImmediate(fp); Object.DestroyImmediate(tp); }
        }

        [Test]
        public void TacticalShadowFilterPreservesWorldCastingAndRestoresCarrierOnReplacement()
        {
            var saved = new List<(Light source, Component data, int native, uint lighting, uint shadows, bool custom)>();
            foreach(var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                Component data = System.Array.Find(light.GetComponents<Component>(), c => c != null && c.GetType().Name == "UniversalAdditionalLightData");
                var type = data != null ? data.GetType() : null;
                saved.Add((light,data,light.renderingLayerMask,
                    data != null ? (uint)type.GetProperty("renderingLayers").GetValue(data) : 0,
                    data != null ? (uint)type.GetProperty("shadowRenderingLayers").GetValue(data) : 0,
                    data != null && (bool)type.GetProperty("customShadowLayers").GetValue(data)));
            }
            var weapon = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var node = new GameObject("Tactical"); node.transform.SetParent(weapon.transform);
            var sun = new GameObject("WorldLight",typeof(Light));
            try
            {
                var device = node.AddComponent<Game.Gameplay.Weapon.TacticalFlashlight>(); device.Setup(false);
                Game.Presentation.Weapon.TacticalFlashlightShadowFilter.Bind(device,weapon);
                var filter = node.GetComponent<Game.Presentation.Weapon.TacticalFlashlightShadowFilter>();
                var carrier = weapon.GetComponent<Renderer>();
                const uint layer = Game.Presentation.Weapon.TacticalFlashlightShadowFilter.CarrierWeaponLayer;
                Assert.That(carrier.renderingLayerMask & layer,Is.Not.Zero);
                Assert.That(carrier.shadowCastingMode,Is.EqualTo(ShadowCastingMode.On));
                Assert.That(sun.GetComponent<Light>().renderingLayerMask & (int)layer,Is.Not.Zero);
                Assert.That(device.GetComponentInChildren<Light>().renderingLayerMask & (int)layer,Is.Zero);
                var disable = filter.GetType().GetMethod("OnDisable",BindingFlags.NonPublic|BindingFlags.Instance);
                disable.Invoke(filter,null);
                Assert.That(carrier.renderingLayerMask,Is.EqualTo(1u));
                var replacement = new GameObject("Replacement"); replacement.transform.SetParent(weapon.transform);
                var next = replacement.AddComponent<Game.Gameplay.Weapon.TacticalFlashlight>(); next.Setup(false);
                Game.Presentation.Weapon.TacticalFlashlightShadowFilter.Bind(next,weapon);
                // A stale destruction callback must not undo the replacement device's mask.
                filter.GetType().GetMethod("OnDestroy",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(filter,null);
                Assert.That(carrier.renderingLayerMask & layer,Is.Not.Zero);
            }
            finally
            {
                Object.DestroyImmediate(weapon);Object.DestroyImmediate(sun);
                foreach(var entry in saved)
                {
                    if(entry.source==null)continue;
                    var current = System.Array.Find(entry.source.GetComponents<Component>(), c => c != null && c.GetType().Name == "UniversalAdditionalLightData");
                    if(entry.data==null){if(current!=null)Object.DestroyImmediate(current);}
                    else
                    {
                        var type=entry.data.GetType();
                        type.GetProperty("renderingLayers").SetValue(entry.data,entry.lighting);
                        type.GetProperty("shadowRenderingLayers").SetValue(entry.data,entry.shadows);
                        type.GetProperty("customShadowLayers").SetValue(entry.data,entry.custom);
                    }
                    entry.source.renderingLayerMask=entry.native;
                }
            }
        }
    }
}
