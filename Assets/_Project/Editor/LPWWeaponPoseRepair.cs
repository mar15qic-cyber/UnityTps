using System;
using System.Linq;
using Game.Presentation.Animation;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Authors the reusable LPW adaptation contract onto the current Rifle/SMG
    /// spike prefabs. Running it again is safe and produces the same structure.
    /// </summary>
    public static class LPWWeaponPoseRepair
    {
        private sealed class Spec
        {
            public string PrefabPath;
            public string GunName;
            public string MagazineName;
            public Vector3 RootLocalPosition;
            public Vector3 RootLocalEulerAngles;
            public bool AlignRootToRightHand = true;
            public bool ManualRootTransform;
            public Vector3 RightHandGripPosition;
            public Vector3 RightHandGripEuler;
            public Vector3 LeftSupportGripPosition;
            public Vector3 LeftSupportGripEuler;
            public Vector3 TriggerPosition;
            public Vector3 TriggerEuler;
            public Vector3 MagazineWellPosition;
            public Vector3 MagazineWellEuler;
            public Vector3 MagazineGripPosition;
            public Vector3 MagazineGripEuler;
            public Vector3 LeftHandTargetPosition;
            public Vector3 LeftHandTargetEuler;
            public Vector3 HeldMagazinePosition;
            public Vector3 HeldMagazineEuler;
            public float AmmoLeftOut;
            public float AmmoLeftIn;
            public float EmptyOut;
            public float EmptyIn;
        }

        private static readonly Spec[] Specs =
        {
            new Spec
            {
                PrefabPath = "Assets/_Project/Prefabs/Weapons/LPWTest/FP_LPW_Rifle2_02_View.prefab",
                GunName = "LPW_Rifle2_02",
                MagazineName = "AssaultRifle2_02_2",
                // AUG/bullpup: restore the pre-audit calibration. The root is
                // intentionally editable in the prefab; the editor profile
                // captures any hand-authored transform for Play Mode.
                RootLocalPosition = new Vector3(0f, 0.05f, 0.23215571f),
                RootLocalEulerAngles = new Vector3(0f, 90f, 326.73f),
                AlignRootToRightHand = true,
                ManualRootTransform = true,
                RightHandGripPosition = new Vector3(0.28f, 0.045f, -0.025f),
                RightHandGripEuler = new Vector3(84.94f, 27.61f, 123.52f),
                LeftSupportGripPosition = new Vector3(-0.30f, 0.045f, -0.025f),
                LeftSupportGripEuler = new Vector3(350f, 325f, 302f),
                TriggerPosition = new Vector3(0.12f, -0.015f, -0.03f),
                TriggerEuler = new Vector3(90f, 0f, 0f),
                MagazineWellPosition = new Vector3(0.16f, -0.015f, 0f),
                MagazineWellEuler = new Vector3(90f, 0f, 0f),
                MagazineGripPosition = new Vector3(0.12f, -0.23f, 0.035f),
                MagazineGripEuler = new Vector3(346f, 267f, 296f),
                LeftHandTargetPosition = new Vector3(-0.02091f, 0.07454f, -0.03317f),
                LeftHandTargetEuler = new Vector3(349.673f, 234.949f, 302.306f),
                HeldMagazinePosition = new Vector3(-0.04188f, 0.00189f, 0.04764f),
                HeldMagazineEuler = new Vector3(345.924f, 267.449f, 295.761f),
                AmmoLeftOut = 0.18f,
                AmmoLeftIn = 0.65f,
                EmptyOut = 0.12f,
                EmptyIn = 0.45f,
            },
            new Spec
            {
                PrefabPath = "Assets/_Project/Prefabs/Weapons/LPWTest/FP_LPW_SMG1_01_View.prefab",
                GunName = "LPW_SMG1_01",
                MagazineName = "SMG1_01_2",
                // MAC-10: the source mesh is short and the magazine lives in
                // the grip, so its root must be moved back to the animated
                // right hand before any support-hand or reload solve.
                RootLocalPosition = new Vector3(0f, -0.13f, 0.01f),
                RootLocalEulerAngles = new Vector3(0f, 90f, 326.73f),
                RightHandGripPosition = new Vector3(0.045f, -0.025f, -0.03f),
                // Local rotation under the calibrated MAC-10 root; authored
                // from hand_R, so rigid anchor alignment does not side-roll
                // the short weapon while fixing its palm position.
                RightHandGripEuler = new Vector3(81.51f, 120.91f, 217.03f),
                LeftSupportGripPosition = new Vector3(-0.12f, 0.035f, -0.03f),
                LeftSupportGripEuler = new Vector3(275f, 125f, 79f),
                TriggerPosition = new Vector3(0.035f, -0.045f, -0.03f),
                TriggerEuler = new Vector3(82f, 211f, 217f),
                MagazineWellPosition = new Vector3(0f, -0.075f, 0f),
                MagazineWellEuler = new Vector3(90f, 180f, 0f),
                MagazineGripPosition = new Vector3(0f, -0.24f, 0.035f),
                MagazineGripEuler = new Vector3(275f, 125f, 79f),
                LeftHandTargetPosition = new Vector3(-0.00887f, 0.04222f, -0.01978f),
                LeftHandTargetEuler = new Vector3(274.995f, 35.286f, 78.703f),
                // The source SMG animation keeps the replacement pivot about 15 cm
                // from the wrist. Move the top-mounted pivot into the palm while
                // preserving the authored carry rotation.
                HeldMagazinePosition = new Vector3(-0.04f, 0.015f, 0.05f),
                HeldMagazineEuler = new Vector3(344.480f, 268.630f, 244.826f),
                AmmoLeftOut = 0.14f,
                AmmoLeftIn = 0.65f,
                EmptyOut = 0.08f,
                EmptyIn = 0.45f,
            },
        };

        [MenuItem("Tools/LPW/Repair Rifle + SMG Pose Adapters")]
        public static void RepairAll()
        {
            int repaired = 0;
            foreach (Spec spec in Specs)
                if (Repair(spec)) repaired++;

            AssetDatabase.SaveAssets();
            Debug.Log($"[LPWWeaponPoseRepair] Repaired {repaired}/{Specs.Length} FP weapon prefabs.");
        }

        private static bool Repair(Spec spec)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(spec.PrefabPath);
            if (root == null)
            {
                Debug.LogError("[LPWWeaponPoseRepair] Missing prefab: " + spec.PrefabPath);
                return false;
            }

            try
            {
                Transform gun = FindDeep(root.transform, spec.GunName);
                Transform wrapper = FindDeep(root.transform, "LPW_Gun");
                Transform magazine = FindDeep(root.transform, spec.MagazineName);
                Transform hand = FindDeep(root.transform, "hand_L");
                if (gun == null || wrapper == null || magazine == null || hand == null)
                {
                    Debug.LogError($"[LPWWeaponPoseRepair] Required node missing in {spec.PrefabPath}: "
                        + $"gun={gun != null}, wrapper={wrapper != null}, magazine={magazine != null}, hand={hand != null}");
                    return false;
                }

                // Freeze the installed magazine to the replacement gun. The source
                // `mag` bone is an animation carrier, not a compatible bind pose.
                magazine.SetParent(gun, true);

                // This root calibration is authored against hand_R, not against
                // renderer bounds. It is the step that fixes the MAC-10's whole
                // weapon floating above both hands.
                FPWeaponPoseProfile existingProfile = root.GetComponent<FPWeaponPoseProfile>();
                bool preserveManualRoot = existingProfile != null && existingProfile.ManualRootTransform;
                if (!preserveManualRoot)
                {
                    wrapper.localPosition = spec.RootLocalPosition;
                    wrapper.localRotation = Quaternion.Euler(spec.RootLocalEulerAngles);
                }

                foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                    UnityEngine.Object.DestroyImmediate(collider);

                int fpLayer = LayerMask.NameToLayer("FirstPersonView");
                foreach (Transform child in wrapper.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = fpLayer;

                Transform target = gun.Find("LeftHandTarget");
                if (target == null)
                {
                    var targetObject = new GameObject("LeftHandTarget");
                    target = targetObject.transform;
                    target.SetParent(gun, false);
                }
                target.localPosition = spec.LeftHandTargetPosition;
                target.localRotation = Quaternion.Euler(spec.LeftHandTargetEuler);
                target.localScale = Vector3.one;
                target.gameObject.layer = gun.gameObject.layer;

                Transform rightHandGrip = EnsureMarker(gun, "RightHandGrip",
                    spec.RightHandGripPosition, spec.RightHandGripEuler);
                Transform leftSupportGrip = EnsureMarker(gun, "LeftSupportGrip",
                    spec.LeftSupportGripPosition, spec.LeftSupportGripEuler);
                Transform trigger = EnsureMarker(gun, "Trigger",
                    spec.TriggerPosition, spec.TriggerEuler);
                Transform magazineWell = EnsureMarker(gun, "MagazineWell",
                    spec.MagazineWellPosition, spec.MagazineWellEuler);
                Transform magazineGrip = EnsureMarker(gun, "MagazineGrip",
                    spec.MagazineGripPosition, spec.MagazineGripEuler);
                // Keep the legacy target serialized for older scenes, while the
                // profile is the authoritative support/reload interface.
                target.localPosition = spec.LeftSupportGripPosition;
                target.localRotation = Quaternion.Euler(spec.LeftSupportGripEuler);

                var profile = existingProfile
                    ?? root.AddComponent<FPWeaponPoseProfile>();
                var profileObject = new SerializedObject(profile);
                Vector3 authoredRootLocalPosition = preserveManualRoot
                    ? wrapper.localPosition : spec.RootLocalPosition;
                Vector3 authoredRootLocalEulerAngles = preserveManualRoot
                    ? wrapper.localEulerAngles : spec.RootLocalEulerAngles;
                profileObject.FindProperty("weaponRoot").objectReferenceValue = wrapper;
                profileObject.FindProperty("rightHand").objectReferenceValue = hand == null
                    ? FindDeep(root.transform, "hand_R") : FindDeep(root.transform, "hand_R");
                profileObject.FindProperty("rightHandGrip").objectReferenceValue = rightHandGrip;
                profileObject.FindProperty("leftSupportGrip").objectReferenceValue = leftSupportGrip;
                profileObject.FindProperty("trigger").objectReferenceValue = trigger;
                profileObject.FindProperty("magazineWell").objectReferenceValue = magazineWell;
                profileObject.FindProperty("magazineGrip").objectReferenceValue = magazineGrip;
                profileObject.FindProperty("hasRootCalibration").boolValue = true;
                // Respect a designer's manual toggle when the repair tool is
                // rerun; the tool should never silently take control back.
                profileObject.FindProperty("manualRootTransform").boolValue =
                    preserveManualRoot || spec.ManualRootTransform;
                profileObject.FindProperty("alignRootToRightHand").boolValue = spec.AlignRootToRightHand;
                profileObject.FindProperty("calibratedRootLocalPosition").vector3Value = authoredRootLocalPosition;
                profileObject.FindProperty("calibratedRootLocalEulerAngles").vector3Value = authoredRootLocalEulerAngles;
                profileObject.FindProperty("magazineOutNormalized").floatValue = spec.AmmoLeftOut;
                profileObject.FindProperty("magazineInNormalized").floatValue = spec.AmmoLeftIn;
                profileObject.FindProperty("emptyMagazineOutNormalized").floatValue = spec.EmptyOut;
                profileObject.FindProperty("emptyMagazineInNormalized").floatValue = spec.EmptyIn;
                profileObject.FindProperty("magazineHeldLocalPosition").vector3Value = spec.HeldMagazinePosition;
                profileObject.FindProperty("magazineHeldLocalEulerAngles").vector3Value = spec.HeldMagazineEuler;
                profileObject.ApplyModifiedPropertiesWithoutUndo();
                // Bake the same rigid RightHandGrip correction that runtime
                // applies. This leaves the prefab already aligned for editor
                // validation while retaining the authored calibration data
                // for activation after animation/domain reload.
                profile.ApplyRootCalibration();

                var ik = root.GetComponent<FPLeftHandIK>() ?? root.AddComponent<FPLeftHandIK>();
                var ikObject = new SerializedObject(ik);
                ikObject.FindProperty("leftHandTarget").objectReferenceValue = target;
                ikObject.FindProperty("poseProfile").objectReferenceValue = profile;
                ikObject.FindProperty("upperArm").objectReferenceValue = FindDeep(root.transform, "arm_L");
                ikObject.FindProperty("lowerArm").objectReferenceValue = FindDeep(root.transform, "lower_arm_L");
                ikObject.FindProperty("hand").objectReferenceValue = hand;
                ikObject.FindProperty("positionWeight").floatValue = 1f;
                // Reload IK is position-only. Keeping rotationWeight at zero
                // avoids the visible wrist snap when the solver enters/exits.
                ikObject.FindProperty("rotationWeight").floatValue = 0f;
                ikObject.FindProperty("blendSeconds").floatValue = 0.08f;
                // Keep ordinary FP animation free of support-hand IK. The two
                // exceptional LPW prefabs opt into reload-only IK explicitly.
                ikObject.FindProperty("m_Enabled").boolValue = true;
                ikObject.FindProperty("reloadOnly").boolValue = true;
                // Begin a short pre-blend before the magazine leaves the gun.
                // The solver is still position-only, but this lets the hand
                // arrive at the extraction interface without a frame-edge snap.
                ikObject.FindProperty("reloadIkStartNormalized").floatValue = Mathf.Min(0.10f, spec.AmmoLeftOut);
                ikObject.FindProperty("reloadIkEndNormalized").floatValue = 0.96f;
                ikObject.FindProperty("reloadEnterBlendSeconds").floatValue = 0.12f;
                ikObject.FindProperty("reloadExitBlendSeconds").floatValue = 0.12f;
                ikObject.FindProperty("targetBlendSeconds").floatValue = 0.08f;
                ikObject.ApplyModifiedPropertiesWithoutUndo();

                var magazineView = root.GetComponent<DetachableMagazineView>()
                    ?? root.AddComponent<DetachableMagazineView>();
                var magObject = new SerializedObject(magazineView);
                magObject.FindProperty("magazinePart").objectReferenceValue = magazine;
                magObject.FindProperty("installedParent").objectReferenceValue = gun;
                magObject.FindProperty("leftHand").objectReferenceValue = hand;
                magObject.FindProperty("poseProfile").objectReferenceValue = profile;
                magObject.FindProperty("heldLocalPosition").vector3Value = spec.HeldMagazinePosition;
                magObject.FindProperty("heldLocalEulerAngles").vector3Value = spec.HeldMagazineEuler;
                magObject.FindProperty("ammoLeftMagOut").floatValue = spec.AmmoLeftOut;
                magObject.FindProperty("ammoLeftMagIn").floatValue = spec.AmmoLeftIn;
                magObject.FindProperty("emptyMagOut").floatValue = spec.EmptyOut;
                magObject.FindProperty("emptyMagIn").floatValue = spec.EmptyIn;
                magObject.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, spec.PrefabPath);
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static Transform FindDeep(Transform root, string targetName)
        {
            return root.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => string.Equals(t.name, targetName, StringComparison.Ordinal));
        }

        private static Transform EnsureMarker(Transform parent, string name, Vector3 position, Vector3 euler)
        {
            Transform marker = parent.Find(name);
            if (marker == null)
            {
                marker = new GameObject(name).transform;
                marker.SetParent(parent, false);
            }
            marker.localPosition = position;
            marker.localRotation = Quaternion.Euler(euler);
            marker.localScale = Vector3.one;
            marker.gameObject.layer = parent.gameObject.layer;
            return marker;
        }
    }
}
