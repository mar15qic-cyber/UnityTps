using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Gameplay.Weapon;
using Game.Presentation.Weapon;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Gameplay.Tests
{
    public sealed class WeaponGeometryRegressionTests
    {
        [TestCase("Day2_ServicePistol")]
        [TestCase("Day3_Handgun02")]
        [TestCase("Day3_Handgun03")]
        [TestCase("Day3_Handgun04")]
        public void PistolMuzzle_FollowsActualBoreThroughoutIdleAimAndFire(string name)
        {
            var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/" + name + ".asset");
            var root = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(def.FirstPersonViewPrefab));
            var mesh = new Mesh();
            try
            {
                var muzzle = root.GetComponent<WeaponView>().Muzzle;
                Assert.That(muzzle.parent.name, Is.EqualTo("weapon"), "bore is weighted to weapon, not the moving slider");
                var renderer = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .First(r => r.name.StartsWith("handgun_") && !r.name.Contains("iron"));
                var clips = def.FirstPersonAnimations;
                clips.AimIdle.SampleAnimation(root, 0);
                renderer.BakeMesh(mesh);
                var points = mesh.vertices.Select(v => root.transform.InverseTransformPoint(renderer.transform.TransformPoint(v))).ToArray();
                float front = points.Max(v => v.z);
                var ring = Enumerable.Range(0, points.Length).Where(i => points[i].z > front - .0002f).ToArray();
                Assert.That(ring.Length, Is.GreaterThan(8));
                foreach (var clip in new[] { clips.Idle, clips.AimIdle, clips.Fire, clips.AimFire })
                foreach (float fraction in new[] { 0f, .2f, .5f, .8f })
                {
                    Assert.That(clip, Is.Not.Null);
                    clip.SampleAnimation(root, clip.length * fraction);
                    renderer.BakeMesh(mesh);
                    var vertices = mesh.vertices;
                    // The circular bore vertices are symmetric; bounds center in the gun frame
                    // remains the same under every animation (unlike world-axis bounds).
                    var gun = muzzle.parent;
                    var bounds = new Bounds(gun.InverseTransformPoint(renderer.transform.TransformPoint(vertices[ring[0]])), Vector3.zero);
                    foreach (int i in ring) bounds.Encapsulate(gun.InverseTransformPoint(renderer.transform.TransformPoint(vertices[i])));
                    Assert.That(Vector3.Distance(gun.TransformPoint(bounds.center), muzzle.position),
                        Is.LessThan(.0015f), name + " " + clip.name + " @" + fraction);
                }
            }
            finally { Object.DestroyImmediate(mesh); PrefabUtility.UnloadPrefabContents(root); }
        }

        [TestCase("weapon.m4", "attach.rifle.optic")]
        [TestCase("weapon.ak", "attach.rifle.optic")]
        [TestCase("weapon.rifle03", "attach.lpfp.optic.02")]
        [TestCase("weapon.service_pistol", "attach.rifle.optic")]
        [TestCase("weapon.handgun04", "attach.lpfp.optic.02")]
        public void BasicOptic_WindowIsOpenAperture_NotOpaqueMount(string weapon, string optic)
        {
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var entry = catalog.Find(optic);
            Assert.That(catalog.Calibration.TryGetOpticAim(weapon, optic, out var aim), Is.True);
            var row = catalog.Calibration.Rows.First(r => r.weaponItemId == weapon && r.attachmentItemId == optic);
            var prefabName = weapon == "weapon.m4" ? "FP_Rifle_View" : weapon == "weapon.ak" ? "FP_Rifle02_View"
                : weapon == "weapon.rifle03" ? "FP_Rifle03_View" : weapon == "weapon.service_pistol" ? "FP_ServicePistol_View" : "FP_Handgun04_View";
            var fp = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/Weapons/" + prefabName + ".prefab");
            var socket = fp.transform.Find("Armature/weapon/Attach_Optic");
            var current = Quaternion.Inverse(fp.transform.rotation) * socket.rotation;
            var delta = row.HasAuthorFrame ? Quaternion.Inverse(current) * row.AuthorRotation * row.positionOffset : row.positionOffset;
            var center = Quaternion.Inverse(entry.MountRotation) * (aim.WindowCenterLocal - entry.mountOffset - delta);
            var mesh = entry.prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
            var vertices = mesh.vertices; var triangles = mesh.triangles;
            Assert.That(center.y, Is.GreaterThan(.015f));
            Assert.That(mesh.bounds.Contains(center), Is.True);
            // A +Z sightline through the designated center must pass through the mesh hole.
            for (int i = 0; i < triangles.Length; i += 3)
                Assert.That(InsideTriangleXY(center, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]),
                    Is.False, "window center lies over the opaque housing triangle " + i);
        }

        private static bool InsideTriangleXY(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            float denom = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(denom) < 1e-10f) return false;
            float u = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / denom;
            float v = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / denom;
            return u > .0001f && v > .0001f && u + v < .9999f;
        }

        [Test]
        public void ActualOwnerTracer_OneLineFollowsMovedMuzzle_AndProjectsToWallHit()
        {
            var root = new GameObject("TracerWorldCamera");
            WeaponView view = null;
            try
            {
                int layer = LayerMask.NameToLayer("FirstPersonView");
                var world = root.AddComponent<UnityEngine.Camera>();
                world.cullingMask = ~(1 << layer); world.fieldOfView = 60; world.aspect = 16f / 9;
                var fpGo = new GameObject("FP camera"); fpGo.transform.SetParent(root.transform, false);
                var fp = fpGo.AddComponent<UnityEngine.Camera>();
                fp.cullingMask = 1 << layer; fp.fieldOfView = 45; fp.aspect = world.aspect;
                fp.transform.localPosition = new Vector3(-.047f, .082f, -.299f);
                var gun = new GameObject("Gun"); gun.transform.SetParent(root.transform, false);
                gun.transform.localPosition = new Vector3(.1f, -.15f, .4f);
                view = gun.AddComponent<WeaponView>();
                Set(view, "muzzle", gun.transform);
                Invoke(view, "BuildEffects");
                var hit = new Vector3(0, 0, 20);
                Invoke(view, "SpawnTracer", gun.transform.position, hit, 1u);
                for (int frame = 0; frame < 4; frame++)
                {
                    root.transform.position += new Vector3(.01f, .1f, .12f);
                    root.transform.rotation = Quaternion.Euler(-frame * .7f, frame, 0);
                    Invoke(view, "LateUpdate");
                    var lines = gun.GetComponentsInChildren<LineRenderer>().Where(l => l.enabled).ToArray();
                    Assert.That(lines.Length, Is.EqualTo(1), "no second FP/world segment or ACK replay");
                    Assert.That(Vector3.Distance(lines[0].GetPosition(1), hit), Is.LessThan(.00001f));
                    Assert.That(lines[0].gameObject.layer, Is.Zero, "scope RT and world occlusion must retain the tracer");
                    var a = world.WorldToViewportPoint(lines[0].GetPosition(0));
                    var b = fp.WorldToViewportPoint(gun.transform.position);
                    Assert.That(Vector2.Distance(new Vector2(a.x, a.y), new Vector2(b.x, b.y)), Is.LessThan(.00001f));
                }
            }
            finally
            {
                if (view != null)
                {
                    var field = typeof(WeaponView).GetField("_tracerMaterial", BindingFlags.NonPublic | BindingFlags.Instance);
                    var material = field.GetValue(view) as Material;
                    field.SetValue(view, null);
                    if (material != null) Object.DestroyImmediate(material);
                }
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ObserverTracer_UsesCurrentTpMuzzle_NotNetworkEyeOrigin()
        {
            var host = new GameObject("ObserverFxTest");
            var barrel = new GameObject("TPBarrel");
            RemoteShotFxView view = null;
            try
            {
                view = host.AddComponent<RemoteShotFxView>();
                Invoke(view, "BuildPool");
                barrel.transform.position = new Vector3(2, 1.4f, 3);
                var result = new RemoteShotPresentation { Origin = new Vector3(2, 1.8f, 2),
                    FinalPoint = new Vector3(2, 2, 50), PelletCount = 1 };
                Invoke(view, "DrawShot", barrel.transform, result);
                barrel.transform.position += new Vector3(0, .2f, .3f);
                Invoke(view, "LateUpdate");
                var line = host.GetComponentsInChildren<LineRenderer>().Single(l => l.enabled);
                Assert.That(line.GetPosition(0), Is.EqualTo(barrel.transform.position));
                Assert.That(line.GetPosition(0), Is.Not.EqualTo(result.Origin));
                Assert.That(line.GetPosition(1), Is.EqualTo(result.FinalPoint));
            }
            finally
            {
                if (view != null)
                {
                    var field = typeof(RemoteShotFxView).GetField("_material", BindingFlags.NonPublic | BindingFlags.Instance);
                    var material = field.GetValue(view) as Material;
                    field.SetValue(view, null);
                    if (material != null) Object.DestroyImmediate(material);
                }
                Object.DestroyImmediate(host); Object.DestroyImmediate(barrel);
            }
        }

        [Test]
        public void OwnerTracerUsesInstalledMuzzleDeviceAperture()
        {
            var root = new GameObject("MuzzleDeviceTest");
            WeaponView view = null;
            try
            {
                var marker = new GameObject("Muzzle").transform;
                marker.SetParent(root.transform);
                marker.localPosition = new Vector3(0, 0, .3f);
                var socket = new GameObject("Attach_Muzzle");
                socket.transform.SetParent(root.transform);
                socket.transform.localPosition = new Vector3(0, 0, .28f);
                var component = socket.AddComponent<AttachmentSocket>();
                Set(component, "slot", AttachmentSlotType.Muzzle);
                var device = GameObject.CreatePrimitive(PrimitiveType.Cube);
                device.name = "Att_test_suppressor";
                device.transform.SetParent(socket.transform);
                device.transform.localPosition = new Vector3(0, 0, .13f);
                device.transform.localScale = new Vector3(.08f, .08f, .3f);
                root.AddComponent<WeaponAttachmentView>();
                view = root.AddComponent<WeaponView>();
                Set(view, "muzzle", marker);
                var method = typeof(WeaponView).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(m => m.Name == "ResolveTracerStart" && m.GetParameters().Length == 1);
                var start = (Vector3)method.Invoke(view, new object[] { Vector3.zero });
                Assert.That(start.z, Is.GreaterThan(.52f), "visible suppressor tip extends past the original muzzle marker");
                Object.DestroyImmediate(device);
                start = (Vector3)method.Invoke(view, new object[] { Vector3.zero });
                Assert.That(start.z, Is.EqualTo(marker.position.z).Within(.001f));
            }
            finally
            {
                if (view != null)
                {
                    var field = typeof(WeaponView).GetField("_tracerMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
                    var material = field.GetValue(view) as Material;
                    field.SetValue(view, null);
                    if (material != null) Object.DestroyImmediate(material);
                }
                Object.DestroyImmediate(root);
            }
        }

        private static void Set(object obj, string field, object value)
            => obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);
        private static void Invoke(object obj, string method, params object[] args)
            => obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(obj, args);
    }
}
