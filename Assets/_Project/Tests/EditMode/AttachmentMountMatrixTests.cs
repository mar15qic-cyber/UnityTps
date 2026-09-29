using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// C1 mount-only contract for the formal 13 FP weapons x 4 optic assets.
    /// The geometry check measures the socket design plane; the locked SCAR row
    /// is verified against its existing runtime evidence because its legacy
    /// positionOffset is authored relative to a different design reference.
    /// Sniper family excluded: builtin high-zoom only (user decision 2026-09-21).
    /// </summary>
    public sealed class AttachmentMountMatrixTests
    {
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";
        private const string CatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string Optic01 = "attach.lpfp.optic.01";
        private const string Optic02 = "attach.rifle.optic";
        private const string Optic03 = "attach.lpfp.optic.03";
        private const string Optic04 = "attach.lpfp.optic.02";
        private const string LockedWeapon = "weapon.rifle03";
        private const string LockedOptic = Optic04;
        private const string M4Weapon = "weapon.ak";
        private const string M4Scope02 = Optic02;

        private static readonly string[] WeaponIds =
        {
            "weapon.m4", "weapon.ak", "weapon.rifle03", "weapon.service_pistol",
            "weapon.handgun02", "weapon.handgun03", "weapon.handgun04", "weapon.smg01",
            "weapon.smg02", "weapon.smg03", "weapon.smg04", "weapon.smg05",
            "weapon.shotgun01"
        };

        private static readonly string[] FpPrefabNames =
        {
            "FP_Rifle_View", "FP_Rifle02_View", "FP_Rifle03_View", "FP_ServicePistol_View",
            "FP_Handgun02_View", "FP_Handgun03_View", "FP_Handgun04_View", "FP_SMG01_View",
            "FP_SMG02_View", "FP_SMG03_View", "FP_SMG04_View", "FP_SMG05_View",
            "FP_Shotgun01_View"
        };

        private static readonly string[] OpticIds = { Optic01, Optic02, Optic03, Optic04 };
        private static readonly string[] NativeRifleFpPaths =
        {
            "Assets/_Project/Prefabs/Weapons/FP_Rifle_View.prefab",
            "Assets/_Project/Prefabs/Weapons/FP_Rifle02_View.prefab",
            "Assets/_Project/Prefabs/Weapons/FP_Rifle03_View.prefab"
        };
        private static readonly string[] NativeRifleTpPaths =
        {
            "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_01.prefab",
            "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_02.prefab",
            "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_03.prefab"
        };

        private GameObject fpInstance;
        private GameObject previewControllerObject;

        internal static void AssertMountContract(string weapon, string optic, Vector3 position, Vector3 rotation, Quaternion frame)
        {
            // September 23 centering calibration: only the two asymmetric M4 clamps move laterally.
            var expected = weapon == "weapon.m4" && (optic == Optic01 || optic == Optic03)
                ? new Vector3(0, 0, -.00179714f) : Vector3.zero;
            Assert.That(Vector3.Distance(position, expected), Is.LessThan(1e-7f), weapon + " " + optic);
            Assert.That(rotation, Is.EqualTo(Vector3.zero));
            if (weapon == "weapon.m4" && optic == Optic02)
                Assert.That(Quaternion.Angle(frame, Quaternion.Euler(0, 90, 0)), Is.LessThan(.001f));
            else Assert.That(frame, Is.EqualTo(default(Quaternion)));
        }

        [TearDown]
        public void TearDown()
        {
            if (fpInstance != null) UnityEngine.Object.DestroyImmediate(fpInstance);
            if (previewControllerObject != null) UnityEngine.Object.DestroyImmediate(previewControllerObject);
        }

        [Test]
        public void FormalMountRows_Are52UniqueFiniteAndUseStaticFpSocketFrames()
        {
            var calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(CalibrationPath);
            Assert.That(calibration, Is.Not.Null, CalibrationPath);

            var targetKeys = new HashSet<string>(StringComparer.Ordinal);
            var targetCount = 0;
            foreach (var row in calibration.Rows)
            {
                if (row == null || !IsTarget(row.weaponItemId, row.attachmentItemId)) continue;
                targetCount++;
                Assert.That(targetKeys.Add(Key(row.weaponItemId, row.attachmentItemId)), Is.True,
                    "duplicate mount row: " + Key(row.weaponItemId, row.attachmentItemId));
                AssertFinite(row.positionOffset, "positionOffset " + Key(row.weaponItemId, row.attachmentItemId));
                AssertFinite(row.rotationEulerOffset, "rotationEulerOffset " + Key(row.weaponItemId, row.attachmentItemId));
                AssertMountContract(row.weaponItemId, row.attachmentItemId, row.positionOffset,
                    row.rotationEulerOffset, row.AuthorRotation);
            }

            Assert.That(targetCount, Is.EqualTo(52));
            Assert.That(targetKeys, Has.Count.EqualTo(52));
        }

        [Test]
        public void NativeRifleOpticSockets_AreReceiverAnchoredInsteadOfAveragingFrontAndRearSights()
        {
            foreach (var path in NativeRifleFpPaths) AssertReceiverAnchor(path);
            foreach (var path in NativeRifleTpPaths) AssertReceiverAnchor(path);
        }

        [Test]
        public void GeometrySocketRows_HaveFiniteMountAndActualClampContact()
        {
            var calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(CalibrationPath);
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(CatalogPath);
            foreach (var weapon in WeaponIds)
            {
                foreach (var optic in OpticIds)
                {
                    Assert.That(calibration.TryGet(weapon, optic, out var position, out var rotation, out var frame), Is.True);
                    AssertMountContract(weapon, optic, position, rotation, frame);
                    AssertFinite(catalog.Find(optic).mountOffset, optic);
                }
                new NativeAttachmentMountTests().AllFourOpticsSeatTheirClampOnAnActualSurfaceInBothViews(weapon);
            }
        }

        [Test]
        public void RuntimeAndGunsmithPreview_UseTheSameMountComposition()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(CatalogPath);
            var fpPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/FP_Rifle03_View.prefab");
            var tpPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Project/Prefabs/Weapons/TP_Weapon_AssaultRifle_03.prefab");
            Assert.That(catalog, Is.Not.Null);
            Assert.That(fpPrefab, Is.Not.Null);
            Assert.That(tpPrefab, Is.Not.Null);
            Assert.That(catalog.TryGet(LockedOptic, out var entry), Is.True);

            fpInstance = (GameObject)PrefabUtility.InstantiatePrefab(fpPrefab);
            var fpView = fpInstance.AddComponent<WeaponAttachmentView>();
            fpView.ApplyAttachments(catalog, LockedWeapon, new[] { entry }, laserBeamEnabled: false);
            var fpSpawn = fpView.FindSpawned(LockedOptic);
            Assert.That(fpSpawn, Is.Not.Null, "runtime optic clone");
            AssertComposedPosition(fpInstance.transform, fpView.GetSocketTransform(AttachmentSlotType.Optic),
                fpSpawn, entry, catalog.Calibration, LockedWeapon, LockedOptic);

            previewControllerObject = new GameObject("AttachmentMountMatrixPreview", typeof(RectTransform),
                typeof(RawImage), typeof(WeaponPreviewController));
            var controller = previewControllerObject.GetComponent<WeaponPreviewController>();
            controller.Initialize(tpPrefab);
            controller.ApplyPreviewAttachments(catalog, LockedWeapon, new[] { entry });
            var previewView = controller.ModelInstance.GetComponent<WeaponAttachmentView>();
            var previewSpawn = previewView.FindSpawned(LockedOptic);
            Assert.That(previewSpawn, Is.Not.Null, "preview optic clone");
            AssertComposedPosition(controller.ModelInstance.transform,
                previewView.GetSocketTransform(AttachmentSlotType.Optic), previewSpawn, entry,
                catalog.Calibration, LockedWeapon, LockedOptic);
        }

        private static void AssertComposedPosition(Transform viewRoot, Transform socket, Transform spawned,
            AttachmentAssetEntry entry, AttachmentCalibration calibration, string weaponId, string opticId)
        {
            Assert.That(socket, Is.Not.Null);
            Assert.That(calibration.TryGet(weaponId, opticId,
                out var positionOffset, out var rotationOffset, out var authorFrame), Is.True);
            var currentFrame = Quaternion.Inverse(viewRoot.rotation) * socket.rotation;
            var converted = Quaternion.Inverse(currentFrame) * authorFrame * positionOffset;
            Assert.That(Vector3.Distance(spawned.localPosition, entry.mountOffset + converted), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(spawned.localRotation,
                entry.MountRotation * Quaternion.Euler(rotationOffset)), Is.LessThan(0.02f));
        }

        private static Vector3 ComputeBoundsAfterMount(AttachmentAssetEntry entry, Vector3 delta,
            out Bounds socketPlaneBounds)
        {
            var root = entry.prefab.transform;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers, Is.Not.Empty, entry.itemId + " renderer");

            var rawMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var rawMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            foreach (var renderer in renderers)
            {
                var local = renderer.localBounds;
                var extents = local.extents;
                for (var ix = -1; ix <= 1; ix += 2)
                for (var iy = -1; iy <= 1; iy += 2)
                for (var iz = -1; iz <= 1; iz += 2)
                {
                    var corner = local.center + Vector3.Scale(extents, new Vector3(ix, iy, iz));
                    var opticLocal = root.InverseTransformPoint(renderer.transform.TransformPoint(corner));
                    var rotated = entry.MountRotation * opticLocal;
                    rawMin = Vector3.Min(rawMin, rotated);
                    rawMax = Vector3.Max(rawMax, rotated);
                    var mounted = rotated + entry.mountOffset + delta;
                    min = Vector3.Min(min, mounted);
                    max = Vector3.Max(max, mounted);
                }
            }

            socketPlaneBounds = new Bounds((min + max) * 0.5f, max - min);
            return new Vector3(-(rawMin.x + rawMax.x) * 0.5f, -rawMin.y, -(rawMin.z + rawMax.z) * 0.5f);
        }

        private static void AssertReceiverAnchor(string prefabPath)
        {
            var instance = PrefabUtility.LoadPrefabContents(prefabPath);
            var baked = new Mesh();
            try
            {
                var socket = Array.Find(instance.GetComponentsInChildren<AttachmentSocket>(true),
                    s => s.Slot == AttachmentSlotType.Optic);
                Assert.That(socket.GeometryVerified, Is.True);
                var parent = socket.transform.parent;
                var forward = socket.transform.localRotation * Vector3.left;
                Renderer body;
                Mesh mesh;
                if (prefabPath.Contains("/FP_"))
                {
                    var skin = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                        .Where(r => !r.name.Contains("arms") && !r.name.Contains("knife"))
                        .OrderByDescending(r => r.sharedMesh.vertexCount).First();
                    skin.BakeMesh(baked); body = skin; mesh = baked;
                }
                else
                {
                    var mf = instance.GetComponentsInChildren<MeshFilter>(true).OrderByDescending(m => m.sharedMesh.vertexCount).First();
                    body = mf.GetComponent<Renderer>(); mesh = mf.sharedMesh;
                }
                var samples = new List<float>();
                foreach (var p in mesh.vertices)
                    samples.Add(Vector3.Dot(parent.InverseTransformPoint(body.transform.TransformPoint(p)), forward));
                samples.Sort();
                var length = samples[samples.Count - 1] - samples[0];
                var seat = Vector3.Dot(socket.transform.localPosition, forward);
                Assert.That(seat, Is.InRange(samples[0] + length * .2f, samples[0] + length * .5f),
                    "Optic must sit on the receiver, not front sight/handguard or buttstock.");
            }
            finally { UnityEngine.Object.DestroyImmediate(baked); PrefabUtility.UnloadPrefabContents(instance); }
        }

        private static bool IsTarget(string weaponId, string opticId)
        {
            var weapon = Array.IndexOf(WeaponIds, weaponId) >= 0;
            var optic = Array.IndexOf(OpticIds, opticId) >= 0;
            return weapon && optic;
        }

        private static string Key(string weaponId, string opticId) => weaponId + "|" + opticId;

        private static void AssertFinite(Vector3 value, string message)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x), Is.False, message);
            Assert.That(float.IsNaN(value.y) || float.IsInfinity(value.y), Is.False, message);
            Assert.That(float.IsNaN(value.z) || float.IsInfinity(value.z), Is.False, message);
        }

        private static void AssertFinite(Quaternion value, string message)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x), Is.False, message);
            Assert.That(float.IsNaN(value.y) || float.IsInfinity(value.y), Is.False, message);
            Assert.That(float.IsNaN(value.z) || float.IsInfinity(value.z), Is.False, message);
            Assert.That(float.IsNaN(value.w) || float.IsInfinity(value.w), Is.False, message);
            var norm = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            Assert.That(norm, Is.GreaterThan(0.99f), message);
        }
    }
}
