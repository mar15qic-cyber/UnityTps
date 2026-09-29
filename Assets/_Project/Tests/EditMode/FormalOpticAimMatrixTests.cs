using System;
using System.Collections.Generic;
using System.IO;
using Game.Gameplay.Weapon;
using Game.Presentation.Camera;
using Game.Presentation.HUD;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// C2-A contract for the formal 13 weapon x 4 optic aim matrix.
    /// The rows are explicit combination data; the default optic rows remain
    /// as fallback data and must never mask a formal weapon row.
    /// Sniper family is intentionally excluded: builtin high-zoom only, no base
    /// optics (user decision 2026-09-21).
    /// </summary>
    public sealed class FormalOpticAimMatrixTests
    {
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";
        private const string AttachmentCatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string ViewCatalogPath = "Assets/_Project/Resources/OpticViewCatalog.asset";
        private const string Optic01 = "attach.lpfp.optic.01";
        private const string Optic02 = "attach.rifle.optic";
        private const string Optic03 = "attach.lpfp.optic.03";
        private const string Optic04 = "attach.lpfp.optic.02";
        private const string LockedWeapon = "weapon.rifle03";

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

        [Test]
        public void FormalRows_AreExactly52UniqueFiniteAndFullySolved()
        {
            var calibration = LoadCalibration();
            var formal = new Dictionary<string, OpticAimCalibrationRow>(StringComparer.Ordinal);
            foreach (var row in calibration.OpticAimRows)
            {
                if (row == null || !IsFormal(row.weaponItemId, row.opticItemId)) continue;
                var key = Key(row.weaponItemId, row.opticItemId);
                Assert.That(formal.ContainsKey(key), Is.False, "duplicate formal aim row: " + key);
                formal.Add(key, row);
                AssertFinite(row.eyePointLocal, key + " eye");
                AssertFinite(row.axisFrontPointLocal, key + " axis");
                AssertFinite(row.windowCenterLocal, key + " window center");
                Assert.That(OpticAimGeometry.CanUseFullAxisSolve(ToData(row)), Is.True, key + " axis");
                Assert.That(row.HasWindow, Is.True, key + " window");
                Assert.That(row.windowHalfWidthMeters, Is.GreaterThan(0f), key + " half width");
                Assert.That(row.windowHalfHeightMeters, Is.GreaterThan(0f), key + " half height");
                var expectedHeight = row.weaponItemId == LockedWeapon && row.opticItemId == Optic04
                    ? 0.075f
                    : IsLowZoom(row.opticItemId) ? 0.18f : 0.12f;
                Assert.That(row.targetViewportHeight, Is.EqualTo(expectedHeight)
                    .Within(1e-5f), key + " target viewport height");
            }

            Assert.That(formal, Has.Count.EqualTo(52));
            foreach (var weapon in WeaponIds)
            foreach (var optic in OpticIds)
                Assert.That(formal.ContainsKey(Key(weapon, optic)), Is.True, Key(weapon, optic));

            // 狙击族回归锁：基础瞄具行必须不存在（仅内置高倍镜，2026-09-21 用户拍板）。
            foreach (var row in calibration.OpticAimRows)
            {
                if (row == null || row.weaponItemId == null) continue;
                if (!row.weaponItemId.StartsWith("weapon.sniper", StringComparison.Ordinal)) continue;
                Assert.That(row.opticItemId, Does.StartWith("builtin."),
                    "sniper formal optic row must not exist: " + Key(row.weaponItemId, row.opticItemId));
            }
        }

        [Test]
        public void FormalRows_WindowSolveProjectsCenterAndTargetHeight()
        {
            var calibration = LoadCalibration();
            const float fov = 45f;
            const float aspect = 16f / 9f;
            var projection = new CameraProjection
            {
                Rotation = Quaternion.identity,
                FovDegrees = fov,
                Aspect = aspect,
                NearClip = 0.01f,
                FarClip = 1000f
            };

            foreach (var weapon in WeaponIds)
            foreach (var optic in OpticIds)
            {
                Assert.That(calibration.TryGetOpticAim(weapon, optic, out var data), Is.True,
                    Key(weapon, optic));
                var rootPosition = OpticAimGeometry.SolveWindowFramingLocalPosition(
                    data.WindowCenterLocal, Vector3.zero, data.WindowHalfHeightMeters, fov,
                    data.TargetViewportHeight);
                projection.Position = Vector3.zero;
                var center = projection.ProjectToViewport(rootPosition + data.WindowCenterLocal);
                var top = projection.ProjectToViewport(
                    rootPosition + data.WindowCenterLocal + Vector3.up * data.WindowHalfHeightMeters);
                var bottom = projection.ProjectToViewport(
                    rootPosition + data.WindowCenterLocal - Vector3.up * data.WindowHalfHeightMeters);
                var height = top.y - bottom.y;
                Assert.That(center.x, Is.EqualTo(0.5f).Within(0.01f), Key(weapon, optic) + " center x");
                Assert.That(center.y, Is.EqualTo(0.5f).Within(0.01f), Key(weapon, optic) + " center y");
                Assert.That(height, Is.EqualTo(data.TargetViewportHeight).Within(0.01f),
                    Key(weapon, optic) + " projected target height");
                Assert.That(center.z, Is.GreaterThan(0f), Key(weapon, optic) + " depth");
            }
        }

        [Test]
        public void FormalRows_DoNotChangeC1MountContract_AndScarLockRemainsExact()
        {
            var calibration = LoadCalibration();
            for (var wi = 0; wi < WeaponIds.Length; wi++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Project/Prefabs/Weapons/" + FpPrefabNames[wi] + ".prefab");
                Assert.That(prefab, Is.Not.Null, FpPrefabNames[wi]);
                var socket = prefab.transform.Find("Armature/weapon/Attach_Optic");
                Assert.That(socket, Is.Not.Null, FpPrefabNames[wi] + " Attach_Optic");
                var currentFrame = Quaternion.Inverse(prefab.transform.rotation) * socket.rotation;

                foreach (var optic in OpticIds)
                {
                    Assert.That(calibration.TryGet(WeaponIds[wi], optic,
                        out var position, out var rotation, out var authorFrame), Is.True,
                        Key(WeaponIds[wi], optic));
                    AssertFinite(position, Key(WeaponIds[wi], optic) + " mount position");
                    AssertFinite(rotation, Key(WeaponIds[wi], optic) + " mount rotation");
                    Assert.That(socket.GetComponent<AttachmentSocket>().GeometryVerified, Is.True);
                    Game.Gameplay.Tests.AttachmentMountMatrixTests.AssertMountContract(
                        WeaponIds[wi], optic, position, rotation, authorFrame);
                }
            }

            Assert.That(calibration.TryGetOpticAim(LockedWeapon, Optic04, out var scar), Is.True);
            // Aperture audit replaces the old housing/base center; mount/framing stay locked.
            var entry = Resources.Load<AttachmentAssetCatalog>("AttachmentAssetCatalog").Find(Optic04);
            var inverse = Quaternion.Inverse(entry.MountRotation);
            Assert.That(Vector3.Distance(inverse * (scar.EyePointLocal - entry.mountOffset),
                new Vector3(0, .01897f, -.07876f)), Is.LessThan(.00001f));
            Assert.That(Vector3.Distance(inverse * (scar.WindowCenterLocal - entry.mountOffset),
                new Vector3(0, .01897f, -.02876f)), Is.LessThan(.00001f));
            Assert.That(Vector3.Distance(scar.AxisFrontPointLocal, scar.WindowCenterLocal), Is.LessThan(.00001f));
            Assert.That(scar.WindowHalfWidthMeters, Is.EqualTo(0.013f).Within(1e-6f));
            Assert.That(scar.WindowHalfHeightMeters, Is.EqualTo(0.01062f).Within(1e-6f));
            Assert.That(scar.TargetViewportHeight, Is.EqualTo(0.075f).Within(1e-6f));
        }

        [Test]
        public void DefaultOpticRowsRemainFallbackOnlyAndExplicitRowsWin()
        {
            var calibration = LoadCalibration();
            var defaults = 0;
            var builtins = 0;
            foreach (var row in calibration.OpticAimRows)
            {
                if (row == null) continue;
                if (string.IsNullOrEmpty(row.weaponItemId)
                    && Array.IndexOf(OpticIds, row.opticItemId) >= 0) defaults++;
                if (row.opticItemId != null && row.opticItemId.StartsWith("builtin.weapon.", StringComparison.Ordinal))
                    builtins++;
            }
            Assert.That(defaults, Is.EqualTo(3), "the three legacy optic fallback rows remain");
            Assert.That(builtins, Is.EqualTo(3), "builtin sniper rows remain");

            var calibrationData = calibration.TryGetOpticAim("weapon.m4", Optic02, out var explicitData);
            var fallbackData = calibration.TryGetOpticAim("weapon.unknown", Optic02, out var fallback);
            Assert.That(calibrationData, Is.True);
            Assert.That(fallbackData, Is.True);
            Assert.That(explicitData.EyePointLocal, Is.Not.EqualTo(fallback.EyePointLocal));
            Assert.That(explicitData.HasAxisFront, Is.True);
            Assert.That(explicitData.HasWindow, Is.True);
        }

        [Test]
        public void FourOpticsAndBuiltinSnipersHaveConsistentIdentityFovMagnificationAndViewMode()
        {
            var attachments = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(AttachmentCatalogPath);
            var views = AssetDatabase.LoadAssetAtPath<OpticViewCatalog>(ViewCatalogPath);
            Assert.That(attachments, Is.Not.Null);
            Assert.That(views, Is.Not.Null);
            AssertOptic(attachments, views, Optic01, OpticAimTier.LowZoom, 18f, 3f,
                OpticPresentationMode.MagnifiedOverlay);
            AssertOptic(attachments, views, Optic03, OpticAimTier.LowZoom, 18f, 3f,
                OpticPresentationMode.MagnifiedOverlay);
            AssertOptic(attachments, views, Optic02, OpticAimTier.Holo, 0f, 0f,
                OpticPresentationMode.Physical1x);
            AssertOptic(attachments, views, Optic04, OpticAimTier.Holo, 0f, 0f,
                OpticPresentationMode.Physical1x);

            foreach (var builtin in new[] { "builtin.weapon.sniper01", "builtin.weapon.sniper02", "builtin.weapon.sniper03" })
            {
                var profile = FindView(views, builtin);
                Assert.That(profile, Is.Not.Null, builtin);
                Assert.That(profile.mode, Is.EqualTo(OpticPresentationMode.MagnifiedOverlay), builtin);
            }
        }

        [Test]
        public void ArenaSceneHasNoSerializedFirstPersonViewOrOpticGhost()
        {
            const string scenePath = "Assets/_Project/Scenes/Arena.unity";
            Assert.That(File.Exists(scenePath), Is.True, scenePath);
            var yaml = File.ReadAllText(scenePath);
            Assert.That(yaml, Does.Not.Contain("m_Name: FP_Rifle03_View"),
                "Arena must not serialize an FP view beside FPWeaponRig's runtime-owned views");
            Assert.That(yaml, Does.Not.Contain("m_Name: Att_attach.lpfp.optic.02"),
                "Arena must not serialize a ghost optic instance under a legacy FP view");
        }

        private static void AssertOptic(AttachmentAssetCatalog attachments, OpticViewCatalog views,
            string id, OpticAimTier tier, float fov, float magnification, OpticPresentationMode mode)
        {
            Assert.That(attachments.TryGet(id, out var entry), Is.True, id);
            Assert.That(entry.slot, Is.EqualTo(AttachmentSlotType.Optic), id);
            Assert.That(entry.aimTier, Is.EqualTo(tier), id);
            Assert.That(entry.adsFovOverride, Is.EqualTo(fov).Within(1e-5f), id + " FOV");
            Assert.That(entry.magnification, Is.EqualTo(magnification).Within(1e-5f), id + " magnification");
            var profile = FindView(views, id);
            Assert.That(profile, Is.Not.Null, id + " view profile");
            Assert.That(profile.mode, Is.EqualTo(mode), id + " mode");
        }

        private static OpticViewProfile FindView(OpticViewCatalog views, string id)
        {
            foreach (var profile in views.Profiles)
                if (profile != null && profile.opticId == id) return profile;
            return null;
        }

        private static AttachmentCalibration LoadCalibration()
        {
            var calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(CalibrationPath);
            Assert.That(calibration, Is.Not.Null, CalibrationPath);
            return calibration;
        }

        private static OpticAimData ToData(OpticAimCalibrationRow row)
        {
            return new OpticAimData
            {
                EyePointLocal = row.eyePointLocal,
                AxisFrontPointLocal = row.axisFrontPointLocal,
                WindowCenterLocal = row.windowCenterLocal,
                WindowHalfWidthMeters = row.windowHalfWidthMeters,
                WindowHalfHeightMeters = row.windowHalfHeightMeters,
                TargetViewportHeight = row.targetViewportHeight,
                HasAxisFront = row.HasAxisFront,
                HasWindow = row.HasWindow
            };
        }

        private static bool IsLowZoom(string opticId) => opticId == Optic01 || opticId == Optic03;

        private static bool IsFormal(string weaponId, string opticId)
            => Array.IndexOf(WeaponIds, weaponId) >= 0 && Array.IndexOf(OpticIds, opticId) >= 0;

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
        }

    }
}
