using System;
using System.Linq;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using Game.Presentation.Camera;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// Persistent authoring tool for schema-6 LPW dual-layer calibration. It writes only
    /// the selected formal FP prefab and the matching manifest row; scenes are never saved.
    /// </summary>
    public sealed class LPWDualLayerCalibrationWindow : EditorWindow
    {
        private const string ManifestPath = "Assets/_Project/ScriptableObjects/Weapons/LPW/LPWWeaponManifest.asset";
        private const string FpRoot = "Assets/_Project/Prefabs/Weapons/LPW/FP";
        private string _definitionId = "lpw.rifle.02";

        [MenuItem("Tools/LPW Production/Dual-Layer Calibration")]
        private static void OpenWindow() => GetWindow<LPWDualLayerCalibrationWindow>("LPW Calibration");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("LPW schema-6 dual-layer calibration", EditorStyles.boldLabel);
            _definitionId = EditorGUILayout.TextField("Definition Id", _definitionId);
            EditorGUILayout.HelpBox(
                "Idle/anchors write LPW_Gun data. ADS writes only FP_Weapon_Root pose data. "
                + "The active scene is never saved.", MessageType.Info);

            if (GUILayout.Button("Open Formal FP Prefab")) OpenFormalPrefab(_definitionId);

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Select Matching Runtime LPW_Gun"))
                {
                    FPWeaponPoseProfile profile = ResolveUniqueRuntimeProfile(_definitionId);
                    if (profile != null && profile.WeaponRoot != null)
                        Selection.activeGameObject = profile.WeaponRoot.gameObject;
                }
                if (GUILayout.Button("Save Idle Root + Explicit Interfaces"))
                    SaveRuntimeIdleAndInterfaces(_definitionId);
                if (GUILayout.Button("Solve + Save Full-ADS Viewmodel Pose"))
                    SolveAndSaveRuntimeAds(_definitionId);
            }
        }

        private static void OpenFormalPrefab(string definitionId)
        {
            LPWWeaponSpec spec = ResolveUniqueSpec(definitionId);
            if (spec == null) return;
            string path = FormalFpPath(spec);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new InvalidOperationException("Formal FP prefab missing: " + path);
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            AssetDatabase.OpenAsset(prefab);
        }

        private static void SaveRuntimeIdleAndInterfaces(string definitionId)
        {
            FPWeaponPoseProfile runtime = ResolveUniqueRuntimeProfile(definitionId);
            LPWWeaponSpec spec = ResolveUniqueSpec(definitionId);
            if (runtime == null || spec == null) return;
            if (runtime.WeaponRoot == null || runtime.RightHandGrip == null || runtime.Trigger == null
                || runtime.RearSight == null || runtime.FrontSight == null)
                throw new InvalidOperationException("Runtime profile is missing an explicit grip/trigger/rear/front interface.");

            string path = FormalFpPath(spec);
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                FPWeaponPoseProfile target = contents.GetComponent<FPWeaponPoseProfile>();
                if (target == null || target.WeaponRoot == null)
                    throw new InvalidOperationException("Formal FP prefab is missing FPWeaponPoseProfile/LPW_Gun: " + path);

                CopyTransform(runtime.WeaponRoot, target.WeaponRoot);
                CopyNamedInterface(runtime.RightHandGrip, target.WeaponRoot, "RightHandGrip");
                CopyNamedInterface(runtime.Trigger, target.WeaponRoot, "Trigger");
                CopyNamedInterface(runtime.RearSight, target.WeaponRoot, "RearSight");
                CopyNamedInterface(runtime.FrontSight, target.WeaponRoot, "FrontSight");

                SerializedObject profile = new(target);
                profile.FindProperty("hasRootCalibration").boolValue = true;
                profile.FindProperty("calibratedRootLocalPosition").vector3Value = target.WeaponRoot.localPosition;
                profile.FindProperty("calibratedRootLocalEulerAngles").vector3Value = target.WeaponRoot.localEulerAngles;
                profile.FindProperty("manualRootTransform").boolValue = false;
                profile.FindProperty("alignRootToRightHand").boolValue = false;
                profile.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }

            spec.poseCalibrationMode = LPWPoseCalibrationMode.DualLayerVerified;
            spec.hasGripCalibration = true;
            spec.fpRootPosition = runtime.WeaponRoot.localPosition;
            spec.fpRootEuler = runtime.WeaponRoot.localEulerAngles;
            spec.fpRightHandGripPosition = runtime.RightHandGrip.localPosition;
            spec.fpRightHandGripEuler = runtime.RightHandGrip.localEulerAngles;
            spec.fpTriggerPosition = runtime.Trigger.localPosition;
            spec.fpTriggerEuler = runtime.Trigger.localEulerAngles;
            spec.hasSightCalibration = true;
            spec.fpRearSightPosition = runtime.RearSight.localPosition;
            spec.fpRearSightEuler = runtime.RearSight.localEulerAngles;
            spec.fpFrontSightPosition = runtime.FrontSight.localPosition;
            spec.fpFrontSightEuler = runtime.FrontSight.localEulerAngles;
            MarkManifestDirty();
            Debug.Log("[LPWCalibration] Saved explicit idle/interfaces for " + definitionId + ".");
        }

        private static void SolveAndSaveRuntimeAds(string definitionId)
        {
            FPWeaponPoseProfile runtime = ResolveUniqueRuntimeProfile(definitionId);
            LPWWeaponSpec spec = ResolveUniqueSpec(definitionId);
            if (runtime == null || spec == null) return;
            PlayerAimState aim = runtime.GetComponentInParent<PlayerAimState>();
            if (aim == null || aim.Ads01 < .95f)
                throw new InvalidOperationException("ADS solve requires Ads01 >= 0.95.");
            if (!TrySolveAdsPose(runtime, out Vector3 localPosition, out Quaternion localRotation,
                    out float depth, out float axisAngle))
                throw new InvalidOperationException("ADS solve rejected invalid camera/sight geometry.");

            SerializedObject runtimeSo = new(runtime);
            runtimeSo.FindProperty("hasAdsCalibration").boolValue = true;
            runtimeSo.FindProperty("adsViewmodelLocalPosition").vector3Value = localPosition;
            runtimeSo.FindProperty("adsViewmodelLocalEulerAngles").vector3Value = localRotation.eulerAngles;
            runtimeSo.ApplyModifiedPropertiesWithoutUndo();

            string path = FormalFpPath(spec);
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                FPWeaponPoseProfile target = contents.GetComponent<FPWeaponPoseProfile>();
                if (target == null) throw new InvalidOperationException("Formal FP profile missing: " + path);
                SerializedObject targetSo = new(target);
                targetSo.FindProperty("hasAdsCalibration").boolValue = true;
                targetSo.FindProperty("adsViewmodelLocalPosition").vector3Value = localPosition;
                targetSo.FindProperty("adsViewmodelLocalEulerAngles").vector3Value = localRotation.eulerAngles;
                targetSo.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }

            spec.poseCalibrationMode = LPWPoseCalibrationMode.DualLayerVerified;
            spec.hasAdsCalibration = true;
            spec.fpAdsViewmodelPosition = localPosition;
            spec.fpAdsViewmodelEuler = localRotation.eulerAngles;
            MarkManifestDirty();
            Debug.Log($"[LPWCalibration] Saved ADS parent pose for {definitionId}: position={localPosition:F6}, "
                + $"euler={localRotation.eulerAngles:F3}, rearDepth={depth:F4}m, pre-solveAxis={axisAngle:F4}deg.");
        }

        public static bool TrySolveAdsPose(FPWeaponPoseProfile profile, out Vector3 localPosition,
            out Quaternion localRotation, out float rearDepth, out float preSolveAxisAngle)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            rearDepth = 0f;
            preSolveAxisAngle = 180f;
            if (profile == null || !profile.HasCompleteSightLayout) return false;

            FPWeaponMotion motion = profile.GetComponentInParent<FPWeaponMotion>();
            if (motion == null || motion.transform.parent == null) return false;
            UnityEngine.Camera camera = ResolveViewCamera(motion.transform);
            if (camera == null) return false;

            Vector3 direction = profile.FrontSight.position - profile.RearSight.position;
            if (direction.sqrMagnitude < 0.000001f) return false;
            direction.Normalize();
            rearDepth = Vector3.Dot(profile.RearSight.position - camera.transform.position, camera.transform.forward);
            if (rearDepth <= camera.nearClipPlane) return false;

            preSolveAxisAngle = Vector3.Angle(direction, camera.transform.forward);
            Quaternion shortestWorldDelta = Quaternion.FromToRotation(direction, camera.transform.forward);
            Quaternion targetWorldRotation = shortestWorldDelta * motion.transform.rotation;
            Transform parent = motion.transform.parent;
            localRotation = Quaternion.Inverse(parent.rotation) * targetWorldRotation;

            Vector3 rearInMotion = motion.transform.InverseTransformPoint(profile.RearSight.position);
            Vector3 desiredRearWorld = camera.transform.position + camera.transform.forward * rearDepth;
            Vector3 desiredRearParent = parent.InverseTransformPoint(desiredRearWorld);
            Vector3 rotatedRearParent = Matrix4x4.TRS(Vector3.zero, localRotation, motion.transform.localScale)
                .MultiplyPoint3x4(rearInMotion);
            localPosition = desiredRearParent - rotatedRearParent;
            return IsFinite(localPosition) && IsFinite(localRotation);
        }

        private static UnityEngine.Camera ResolveViewCamera(Transform motion)
        {
            int fpLayer = LayerMask.NameToLayer("FirstPersonView");
            UnityEngine.Camera[] cameras = motion.parent.GetComponentsInChildren<UnityEngine.Camera>(false);
            UnityEngine.Camera[] matches = cameras.Where(x => x != null && x.isActiveAndEnabled
                && x.gameObject != motion.parent.gameObject
                && (fpLayer < 0 || (x.cullingMask & (1 << fpLayer)) != 0)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private static FPWeaponPoseProfile ResolveUniqueRuntimeProfile(string definitionId)
        {
            FPWeaponPoseProfile[] matches = Resources.FindObjectsOfTypeAll<FPWeaponPoseProfile>()
                .Where(x => x != null && x.gameObject.scene.IsValid() && x.isActiveAndEnabled
                    && x.gameObject.activeInHierarchy)
                .Where(x =>
                {
                    WeaponController controller = x.GetComponentInParent<WeaponController>();
                    return controller != null && controller.Definition != null
                        && controller.Definition.WeaponId == definitionId;
                }).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected one active runtime profile for {definitionId}, found {matches.Length}.");
            return matches[0];
        }

        private static LPWWeaponSpec ResolveUniqueSpec(string definitionId)
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            if (manifest == null || manifest.SchemaVersion != 6)
                throw new InvalidOperationException("Schema-6 manifest missing: " + ManifestPath);
            LPWWeaponSpec[] matches = manifest.Weapons.Where(x => x.definitionId == definitionId).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected one manifest row for {definitionId}, found {matches.Length}.");
            return matches[0];
        }

        private static string FormalFpPath(LPWWeaponSpec spec)
        {
            string token = System.IO.Path.GetFileNameWithoutExtension(spec.sourcePrefabPath);
            return FpRoot + "/FP_" + token + "_View.prefab";
        }

        private static void CopyNamedInterface(Transform source, Transform targetRoot, string name)
        {
            Transform target = FindDeep(targetRoot, name);
            if (target == null) throw new InvalidOperationException("Formal FP interface missing: " + name);
            CopyTransform(source, target);
        }

        private static void CopyTransform(Transform source, Transform target)
        {
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
        }

        private static void MarkManifestDirty()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssetIfDirty(manifest);
            AssetDatabase.SaveAssets();
        }

        private static bool IsFinite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool IsFinite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
            && float.IsFinite(value.z) && float.IsFinite(value.w);

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
