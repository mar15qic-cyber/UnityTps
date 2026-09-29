using NUnit.Framework;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    public sealed class DragunovAlignmentTests
    {
        [Test]
        public void FirstPersonSightFollowsWeaponAndTracerBeginsAtBareBarrelTip()
        {
            const string path = "Assets/_Project/Prefabs/Weapons/FP_Sniper03_View.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var bone = root.transform.Find("Armature/weapon");
                var sight = bone?.Find("SightReference");
                var muzzle = bone?.Find("Muzzle");
                var socket = bone?.Find("Attach_Optic");
                Assert.That(sight, Is.Not.Null, "ADS reference must follow the animated weapon bone");
                Assert.That(muzzle, Is.Not.Null);
                Assert.That(socket, Is.Not.Null);
                var sightRoot = root.transform.InverseTransformPoint(sight.position);
                Assert.That(Mathf.Abs(sightRoot.x), Is.LessThan(.01f));
                Assert.That(sightRoot.y, Is.InRange(.06f, .13f),
                    "ADS reference must cross the physical scope, rather than the old 0.65 m point");
                var calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(
                    "Assets/_Project/Resources/AttachmentCalibration.asset");
                Assert.That(calibration.TryGetOpticAim("weapon.sniper03", "builtin.weapon.sniper03", out var aim),
                    Is.True);
                var eyeRoot = root.transform.InverseTransformPoint(socket.TransformPoint(aim.EyePointLocal));
                Assert.That(Mathf.Abs(eyeRoot.x - sightRoot.x), Is.LessThan(.015f));
                Assert.That(Mathf.Abs(eyeRoot.y - sightRoot.y), Is.LessThan(.015f),
                    "Built-in optic calibration must share the visible scope axis");
                var silencer = System.Array.Find(root.GetComponentsInChildren<SkinnedMeshRenderer>(true),
                    renderer => renderer.name == "sniper_03" || renderer.name == "sniper_rifle_03");
                Assert.That(silencer, Is.Not.Null);
                var mesh = new Mesh();
                try
                {
                    silencer.BakeMesh(mesh);
                    float front = float.NegativeInfinity;
                    foreach (var vertex in mesh.vertices)
                        front = Mathf.Max(front, root.transform.InverseTransformPoint(
                            silencer.transform.TransformPoint(vertex)).z);
                    var view = root.GetComponent<Game.Presentation.Weapon.WeaponView>();
                    var start = (Vector3)typeof(Game.Presentation.Weapon.WeaponView).GetMethod("ResolveTracerStart",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, System.Type.EmptyTypes, null).Invoke(view, null);
                    float tracerZ = root.transform.InverseTransformPoint(start).z;
                    Assert.That(Mathf.Abs(tracerZ - front), Is.LessThan(.015f),
                        "First person muzzle marker must sit on the visible outlet");
                }
                finally { Object.DestroyImmediate(mesh); }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        [Test]
        public void ThirdPersonMuzzleIsAlreadyAtVisibleBarrelTip()
        {
            const string path = "Assets/_Project/Prefabs/Weapons/TP_Weapon_Sniper_03.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var muzzle = root.transform.Find("Muzzle");
                var body = root.GetComponentInChildren<MeshRenderer>(true);
                Assert.That(muzzle, Is.Not.Null);
                Assert.That(body, Is.Not.Null);
                float tracerZ = root.transform.InverseTransformPoint(muzzle.position).z;
                var filter = body.GetComponent<MeshFilter>();
                float front = float.NegativeInfinity;
                foreach (var vertex in filter.sharedMesh.vertices)
                    front = Mathf.Max(front, root.transform.InverseTransformPoint(filter.transform.TransformPoint(vertex)).z);
                Assert.That(Mathf.Abs(tracerZ - front), Is.LessThan(.015f));
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
