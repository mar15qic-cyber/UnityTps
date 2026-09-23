using System;
using System.Collections.Generic;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Builds the explicit 16 x 4 optic aim rows from the formal FP socket and
    /// the optic-local window metadata below. The four meshes are single
    /// renderer assets without an aperture submesh or material marker, so the
    /// window metadata is intentionally explicit, named, and reviewable rather
    /// than pretending that an opaque body mesh identifies its glass.
    /// </summary>
    public static class FormalOpticAimCalibrationBuilder
    {
        private const string CatalogPath = "Assets/_Project/Resources/AttachmentAssetCatalog.asset";
        private const string CalibrationPath = "Assets/_Project/Resources/AttachmentCalibration.asset";
        private const string LockedWeapon = "weapon.rifle03";
        private const string LockedOptic = "attach.lpfp.optic.02";

        // Sniper family excluded: builtin high-zoom only, no base optics (user decision 2026-09-21).
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

        // Removal sweeps the 16 native ids (incl. retired sniper rows) so a re-run can
        // never resurrect sniper x base-optic combinations.
        private static readonly string[] RemovalWeaponIds =
        {
            "weapon.m4", "weapon.ak", "weapon.rifle03", "weapon.service_pistol",
            "weapon.handgun02", "weapon.handgun03", "weapon.handgun04", "weapon.smg01",
            "weapon.smg02", "weapon.smg03", "weapon.smg04", "weapon.smg05",
            "weapon.shotgun01", "weapon.sniper01", "weapon.sniper02", "weapon.sniper03"
        };

        private sealed class WindowMetadata
        {
            public string opticId;
            public string prefabPath;
            public Vector3 eyePrefabLocal;
            public Vector3 axisFrontPrefabLocal;
            public Vector3 windowCenterPrefabLocal;
            public float halfWidth;
            public float halfHeight;
            public float targetViewportHeight;
            public string evidence;
        }

        // The point pairs are measured in each Scope prefab's local mesh frame.
        // mountEuler=(0,-90,0) maps prefab +Z to socket -X. These four assets are
        // single opaque renderers without a glass submesh. Basic optics use measured
        // open-aperture rings, NOT whole-body bounds (which include the mounting base).
        // Magnified optics retain their existing framing metadata in this repair.
        private static readonly WindowMetadata[] WindowData =
        {
            new WindowMetadata
            {
                opticId = "attach.lpfp.optic.01",
                prefabPath = "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_01.prefab",
                eyePrefabLocal = new Vector3(0f, 0.0500f, -0.1400f),
                axisFrontPrefabLocal = new Vector3(0f, 0.0500f, -0.0900f),
                windowCenterPrefabLocal = new Vector3(0f, 0.003227f, -0.1050f),
                halfWidth = 0.0120f,
                halfHeight = 0.032976f,
                targetViewportHeight = 0.18f,
                evidence = "Scope_01 prefab mesh-bound center/vertical extent audit; low-zoom 3x"
            },
            new WindowMetadata
            {
                opticId = "attach.rifle.optic",
                prefabPath = "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_02.prefab",
                eyePrefabLocal = new Vector3(0f, 0.02353f, -0.02189f),
                axisFrontPrefabLocal = new Vector3(0f, 0.02353f, 0.02811f),
                windowCenterPrefabLocal = new Vector3(0f, 0.02353f, 0.02811f),
                halfWidth = 0.0188f,
                halfHeight = 0.01159f,
                targetViewportHeight = 0.12f,
                evidence = "Scope_02 open aperture: centerline y=.01194..03512, rear ring z=.02811 (not body bounds)"
            },
            new WindowMetadata
            {
                opticId = "attach.lpfp.optic.03",
                prefabPath = "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_03.prefab",
                eyePrefabLocal = new Vector3(0f, 0.0500f, -0.1400f),
                axisFrontPrefabLocal = new Vector3(0f, 0.0500f, -0.0900f),
                windowCenterPrefabLocal = new Vector3(-0.000182f, 0.010669f, -0.1035f),
                halfWidth = 0.0122f,
                halfHeight = 0.030183f,
                targetViewportHeight = 0.18f,
                evidence = "Scope_03 prefab mesh-bound center/vertical extent audit; low-zoom 3x"
            },
            new WindowMetadata
            {
                opticId = "attach.lpfp.optic.02",
                prefabPath = "Assets/Low Poly FPS Pack/Prefabs/Models_Only/Attachments/Scope_04.prefab",
                eyePrefabLocal = new Vector3(0f, 0.01897f, -0.07876f),
                axisFrontPrefabLocal = new Vector3(0f, 0.01897f, -0.02876f),
                windowCenterPrefabLocal = new Vector3(0f, 0.01897f, -0.02876f),
                halfWidth = 0.0130f,
                halfHeight = 0.01062f,
                targetViewportHeight = 0.12f,
                evidence = "Scope_04 main open aperture: y=.00835..02959, rear ring z=-.02876; excludes upper notch"
            }
        };

        [MenuItem("Tools/Attachments/Rebuild Formal 16x4 Optic Aim Rows")]
        public static void BuildFormalRows()
            => BuildRows(false);

        // Repair only basic optics; never overwrite unrelated/custom/magnified calibration.
        public static void RepairBasicOpticRows() => BuildRows(true);

        private static void BuildRows(bool basicOnly)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<AttachmentAssetCatalog>(CatalogPath);
            var calibration = AssetDatabase.LoadAssetAtPath<AttachmentCalibration>(CalibrationPath);
            if (catalog == null || calibration == null)
            {
                Debug.LogError("[FormalOpticAimBuilder] AttachmentAssetCatalog or AttachmentCalibration missing.");
                return;
            }

            if (!basicOnly) RemoveFormalRows(calibration);
            var written = 0;
            for (var wi = 0; wi < WeaponIds.Length; wi++)
            {
                var fp = AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Project/Prefabs/Weapons/" + FpPrefabNames[wi] + ".prefab");
                var socket = fp != null ? fp.transform.Find("Armature/weapon/Attach_Optic") : null;
                if (fp == null || socket == null)
                {
                    Debug.LogError("[FormalOpticAimBuilder] Missing FP optic socket: " + WeaponIds[wi]);
                    return;
                }

                var currentFrame = Quaternion.Inverse(fp.transform.rotation) * socket.rotation;
                for (var oi = 0; oi < WindowData.Length; oi++)
                {
                    var metadata = WindowData[oi];
                    if (basicOnly && metadata.opticId != "attach.rifle.optic"
                        && metadata.opticId != "attach.lpfp.optic.02") continue;
                    var entry = catalog.Find(metadata.opticId);
                    if (entry == null || entry.prefab == null)
                    {
                        Debug.LogError("[FormalOpticAimBuilder] Missing optic catalog entry: " + metadata.opticId);
                        return;
                    }

                    var mount = FindMountRow(calibration, WeaponIds[wi], metadata.opticId);
                    if (mount == null)
                    {
                        Debug.LogError("[FormalOpticAimBuilder] Missing mount row: "
                            + WeaponIds[wi] + "|" + metadata.opticId);
                        return;
                    }

                    var delta = mount.positionOffset;
                    if (mount.HasAuthorFrame && !socket.GetComponent<AttachmentSocket>().GeometryVerified)
                        delta = Quaternion.Inverse(currentFrame) * mount.AuthorRotation * delta;

                    var data = new OpticAimData
                    {
                        EyePointLocal = MountPoint(entry, metadata.eyePrefabLocal, delta),
                        AxisFrontPointLocal = MountPoint(entry, metadata.axisFrontPrefabLocal, delta),
                        WindowCenterLocal = MountPoint(entry, metadata.windowCenterPrefabLocal, delta),
                        WindowHalfWidthMeters = metadata.halfWidth,
                        WindowHalfHeightMeters = metadata.halfHeight,
                        TargetViewportHeight = metadata.targetViewportHeight,
                        HasAxisFront = true,
                        HasWindow = true
                    };

                    if (WeaponIds[wi] == LockedWeapon && metadata.opticId == LockedOptic)
                        ApplyLockedScarFraming(ref data);

                    // Correct window/axis geometry without overwriting per-gun framing choices.
                    if (basicOnly && calibration.TryGetOpticAim(WeaponIds[wi],metadata.opticId,out var previous)
                        && previous.TargetViewportHeight > .01f)
                        data.TargetViewportHeight = previous.TargetViewportHeight;

                    calibration.SetOpticAim(WeaponIds[wi], metadata.opticId, data);
                    written++;
                }
            }

            EditorUtility.SetDirty(calibration);
            AssetDatabase.SaveAssetIfDirty(calibration);
            Debug.Log("[FormalOpticAimBuilder] Wrote " + written
                + " explicit opticAimRows from formal FP sockets and optic metadata.");
        }

        private static Vector3 MountPoint(AttachmentAssetEntry entry, Vector3 prefabPoint, Vector3 delta)
            => entry.MountRotation * prefabPoint + entry.mountOffset + delta;

        private static void ApplyLockedScarFraming(ref OpticAimData data)
        {
            // The native-rifle socket now owns longitudinal placement at the receiver.
            // Do not preserve the old +86.4 mm per-optic compensation in optical points;
            // MountPoint has already produced the correct Scope_04 geometry from the
            // canonical mount row.  Only the user-approved compact M4-like framing is
            // special for this combination.
            // Scope_04 外壳显著大于 M4 的全息镜；0.12 会把 SCAR 枪身推到镜头前。
            // 0.075 来自同分辨率实机视频中两把枪的外框占屏比，使观感与 M4 1x ADS 一致。
            data.TargetViewportHeight = 0.075f;
            data.HasAxisFront = true;
            data.HasWindow = true;
        }

        private static void RemoveFormalRows(AttachmentCalibration calibration)
        {
            var so = new SerializedObject(calibration);
            var rows = so.FindProperty("opticAimRows");
            for (var i = rows.arraySize - 1; i >= 0; i--)
            {
                var row = rows.GetArrayElementAtIndex(i);
                var weapon = row.FindPropertyRelative("weaponItemId").stringValue;
                var optic = row.FindPropertyRelative("opticItemId").stringValue;
                if (Array.IndexOf(RemovalWeaponIds, weapon) < 0 || Array.IndexOf(GetOpticIds(), optic) < 0) continue;
                rows.DeleteArrayElementAtIndex(i);
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            ((ISerializationCallbackReceiver)calibration).OnAfterDeserialize();
        }

        private static string[] GetOpticIds()
        {
            var ids = new string[WindowData.Length];
            for (var i = 0; i < WindowData.Length; i++) ids[i] = WindowData[i].opticId;
            return ids;
        }

        private static AttachmentCalibrationRow FindMountRow(AttachmentCalibration calibration,
            string weaponId, string opticId)
        {
            foreach (var row in calibration.Rows)
                if (row != null && row.weaponItemId == weaponId && row.attachmentItemId == opticId)
                    return row;
            return null;
        }

    }
}
