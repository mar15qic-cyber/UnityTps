using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Weapon;
using Game.UI;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    public static class PreviewVisualContentRepair
    {
        private const string Examples = "Assets/Low Poly FPS Pack/Prefabs/Example_Prefabs/Arms";
        private const string Suppressor = "Assets/_Project/Prefabs/Attachments/Silencer_Visual.prefab";

        [MenuItem("Tools/Review/Repair Preview Visual Content")]
        public static void Run()
        {
            var frames = new Dictionary<string, Pose>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Examples }))
            {
                var original = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                var camera = original.GetComponentsInChildren<Camera>(true).FirstOrDefault(c => c.name == "Gun Camera");
                if (camera == null) continue;
                foreach (var rig in original.GetComponentsInChildren<Animator>(true))
                    if (rig.avatar != null)
                        frames[AssetDatabase.GetAssetPath(rig.avatar)] = new Pose(
                            camera.transform.InverseTransformPoint(rig.transform.position),
                            Quaternion.Inverse(camera.transform.rotation) * rig.transform.rotation);
            }
            int count = 0;
            var weapons = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            var native = new HashSet<WeaponDefinition>(weapons.Entries.Where(e => e.IsLpfp).Select(e => e.definition));
            var report = new List<string> { "weapon,tipX,tipY,tipZ,correctionMm" };
            // Throwing has one arm-only choreography, independent of the equipped gun.
            // Pistol clips recover into a lowered pistol grip and cannot serve as grenade idle.
            var commonThrow = weapons.Entries.First(e => e.itemId == "weapon.m4").definition.FirstPersonAnimations.ThrowGrenade;
            foreach (var definition in weapons.Entries.Where(e => e.definition != null)
                         .Select(e => e.definition).Distinct())
            {
                string path = AssetDatabase.GetAssetPath(definition.FirstPersonViewPrefab);
                if (string.IsNullOrEmpty(path)) continue;
                var view = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var animator = view.GetComponent<FPWeaponAnimator>();
                    var clip = commonThrow;
                    if (animator != null && clip != null)
                    {
                        if (!frames.TryGetValue(AssetDatabase.GetAssetPath(clip), out var frame))
                            throw new InvalidOperationException("Missing authored throw camera: " + definition.name);
                        var so = new SerializedObject(animator);
                        so.FindProperty("throwablePresentationClip").objectReferenceValue =
                            AssetDatabase.LoadAssetAtPath<AnimationClip>(FirstPersonThrowClipBuilder.ClipPath) ?? clip;
                        so.FindProperty("throwViewLocalPosition").vector3Value = frame.position;
                        so.FindProperty("throwViewLocalEuler").vector3Value = frame.rotation.eulerAngles;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                    var weapon = view.GetComponent<WeaponView>();
                    if (weapon != null && weapon.Muzzle != null)
                    {
                        var so = new SerializedObject(weapon);
                        var tip = so.FindProperty("barrelTipMuzzleLocal").vector3Value;
                        if (!so.FindProperty("hasCalibratedBarrelTip").boolValue)
                            tip = Vector3.back * so.FindProperty("tracerMuzzleInsetMeters").floatValue;
                        if (native.Contains(definition))
                        {
                            Vector3 previous = tip;
                            var pose = view.GetComponentsInChildren<Transform>(true).Select(t =>
                                (node: t, position: t.localPosition, rotation: t.localRotation, scale: t.localScale)).ToArray();
                            definition.FirstPersonAnimations.Idle.SampleAnimation(view, 0f);
                            var renderer = view.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                                .Single(r => System.Text.RegularExpressions.Regex.IsMatch(r.name,
                                    "^(assault_rifle|smg|handgun|sniper|sniper_rifle|shotgun)_\\d+$"));
                            var baked = new Mesh();
                            Transform boreBone = null;
                            Vector3 muzzlePosition = default;
                            Quaternion muzzleRotation = Quaternion.identity;
                            try
                            {
                                renderer.BakeMesh(baked);
                                var points = baked.vertices.Select(v => weapon.Muzzle.InverseTransformPoint(
                                    renderer.transform.TransformPoint(v))).ToArray();
                                tip = BoreCenter(definition.CatalogItemId, points);
                                float front = points.Max(p => p.z);
                                float depth = SectionDepth(definition.CatalogItemId);
                                var indices = Enumerable.Range(0, points.Length).Where(i =>
                                    Mathf.Abs(points[i].z - (front - depth)) <= .0003f).ToArray();
                                var weights = renderer.sharedMesh.boneWeights;
                                int boneIndex = weights[indices[0]].boneIndex0;
                                if (indices.Any(i => weights[i].boneIndex0 != boneIndex || weights[i].weight0 < .999f))
                                    throw new InvalidOperationException("Bore has no rigid owner bone: " + definition.CatalogItemId);
                                boreBone = renderer.bones[boneIndex];
                                muzzlePosition = boreBone.InverseTransformPoint(weapon.Muzzle.position);
                                muzzleRotation = Quaternion.Inverse(boreBone.rotation) * weapon.Muzzle.rotation;
                            }
                            finally
                            {
                                UnityEngine.Object.DestroyImmediate(baked);
                                foreach (var p in pose)
                                { p.node.localPosition = p.position; p.node.localRotation = p.rotation; p.node.localScale = p.scale; }
                            }
                            // Sniper01/02 were attached to lid_2 even though their bore is
                            // weighted to weapon/barrel. Bind in the sampled reference pose,
                            // then restore the prefab pose before persisting the new parent.
                            weapon.Muzzle.SetParent(boreBone, false);
                            weapon.Muzzle.localPosition = muzzlePosition;
                            weapon.Muzzle.localRotation = muzzleRotation;
                            so.FindProperty("barrelTipMuzzleLocal").vector3Value = tip;
                            so.FindProperty("hasCalibratedBarrelTip").boolValue = true;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            report.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "{0},{1:F7},{2:F7},{3:F7},{4:F4}", definition.CatalogItemId,
                                tip.x, tip.y, tip.z, Vector3.Distance(previous, tip) * 1000f));
                        }
                        var exit = weapon.Muzzle.Find("MuzzleExit");
                        if (exit == null) { exit = new GameObject("MuzzleExit").transform; exit.SetParent(weapon.Muzzle, false); }
                        exit.localPosition = tip; exit.localRotation = Quaternion.identity;
                        exit.gameObject.layer = weapon.Muzzle.gameObject.layer;
                        if (native.Contains(definition))
                        {
                            var socket = view.GetComponentsInChildren<AttachmentSocket>(true).SingleOrDefault(s => s.Slot == AttachmentSlotType.Muzzle);
                            if (socket != null) socket.transform.position = exit.position;
                        }
                    }
                    PrefabUtility.SaveAsPrefabAsset(view, path); count++;
                }
                finally { PrefabUtility.UnloadPrefabContents(view); }
                if (native.Contains(definition)) RepairThirdPerson(definition);
            }

            var catalog = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog");
            var entries = catalog.Entries.Where(e => e.isSuppressor && e.prefab != null).ToArray();
            if (entries.Length == 0) throw new InvalidOperationException("Suppressor content is missing");
            Directory.CreateDirectory(Path.GetDirectoryName(Suppressor));
            var device = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(entries[0].prefab));
            try
            {
                var points = device.GetComponentsInChildren<MeshFilter>(true).SelectMany(f =>
                    f.sharedMesh.vertices.Select(v => device.transform.InverseTransformPoint(f.transform.TransformPoint(v)))).ToArray();
                float front = points.Min(p => p.z); // This authored model's outlet faces -Z.
                var face = points.Where(p => p.z <= front + .001f).ToArray();
                var exit = device.transform.Find("MuzzleExit");
                if (exit == null) { exit = new GameObject("MuzzleExit").transform; exit.SetParent(device.transform, false); }
                exit.localPosition = new Vector3((face.Min(p => p.x) + face.Max(p => p.x)) * .5f,
                    (face.Min(p => p.y) + face.Max(p => p.y)) * .5f, front);
                exit.localRotation = Quaternion.Euler(0f, 180f, 0f);
                var prefab = PrefabUtility.SaveAsPrefabAsset(device, Suppressor);
                foreach (var entry in entries) { entry.prefab = prefab; entry.prefabPath = Suppressor; }
                EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssetIfDirty(catalog);
            }
            finally { PrefabUtility.UnloadPrefabContents(device); }
            Debug.Log("[PreviewVisualRepair] authored frames/exits saved: " + count);
            Directory.CreateDirectory("Temp/VisualFix0928");
            File.WriteAllLines("Temp/VisualFix0928/muzzle-calibration.csv", report);
        }

        private static Vector3 BoreCenter(string id, Vector3[] points)
        {
            float front = points.Max(p => p.z);
            // These two crowns are slanted. Use their complete rear circular section
            // for the bore axis. All other native barrels have a planar front ring.
            // A 0.25 mm slice excludes Shotgun01's raised front sight at 1.4 mm.
            float depth = SectionDepth(id);
            var ring = points.Where(p => Mathf.Abs(p.z - (front - depth)) <= .0003f).ToArray();
            if (ring.Length < 12) throw new InvalidOperationException("Barrel section changed: " + id);
            var min = new Vector3(ring.Min(p => p.x), ring.Min(p => p.y), front);
            var max = new Vector3(ring.Max(p => p.x), ring.Max(p => p.y), front);
            var size = max - min;
            if (size.x < .004f || size.y < .004f || Mathf.Abs(size.x - size.y) > .001f)
                throw new InvalidOperationException("Expected a complete circular bore section: " + id);
            return (min + max) * .5f;
        }

        private static float SectionDepth(string id)
            => id == "weapon.m4" ? .0257f : id == "weapon.smg04" ? .02745f : 0f;

        private static void RepairThirdPerson(WeaponDefinition definition)
        {
            string path = AssetDatabase.GetAssetPath(definition.ThirdPersonViewPrefab);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var muzzle = root.transform.Find("Muzzle");
                var body = root.GetComponentsInChildren<MeshFilter>(true).OrderByDescending(f => f.sharedMesh.vertexCount).First();
                var points = body.sharedMesh.vertices.Select(p => muzzle.InverseTransformPoint(body.transform.TransformPoint(p))).ToArray();
                var exit = muzzle.Find("MuzzleExit");
                if (exit == null) { exit = new GameObject("MuzzleExit").transform; exit.SetParent(muzzle, false); }
                exit.localPosition = BoreCenter(definition.CatalogItemId, points);
                exit.localRotation = Quaternion.identity;
                var socket = root.GetComponentsInChildren<AttachmentSocket>(true).SingleOrDefault(s => s.Slot == AttachmentSlotType.Muzzle);
                if (socket != null) socket.transform.position = exit.position;
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
