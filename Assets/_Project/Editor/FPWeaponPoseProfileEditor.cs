using UnityEditor;
using UnityEngine;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;

namespace Game.EditorTools
{
    /// <summary>
    /// Editor-only controls for authoring a replacement weapon's root pose.
    /// Manual mode keeps the transform editable in prefab mode and mirrors
    /// changes into the runtime calibration consumed by FPWeaponPoseProfile.
    /// </summary>
    [CustomEditor(typeof(Game.Presentation.Animation.FPWeaponPoseProfile))]
    public sealed class FPWeaponPoseProfileEditor : UnityEditor.Editor
    {
        private SerializedProperty _weaponRoot;
        private SerializedProperty _rightHand;
        private SerializedProperty _rightHandGrip;
        private SerializedProperty _leftSupportGrip;
        private SerializedProperty _trigger;
        private SerializedProperty _magazineWell;
        private SerializedProperty _magazineGrip;
        private SerializedProperty _magazineInsertGuide;
        private SerializedProperty _magazineExtracted;
        private SerializedProperty _hasRootCalibration;
        private SerializedProperty _calibratedRootLocalPosition;
        private SerializedProperty _calibratedRootLocalEulerAngles;
        private SerializedProperty _manualRootTransform;
        private SerializedProperty _alignRootToRightHand;
        private SerializedProperty _magazineOut;
        private SerializedProperty _hasMagazineCalibration;
        private SerializedProperty _magazineIn;
        private SerializedProperty _emptyOut;
        private SerializedProperty _emptyIn;
        private SerializedProperty _magazineGrabWeight;
        private SerializedProperty _magazineInsertWeight;
        private SerializedProperty _magazineHeldLocalPosition;
        private SerializedProperty _magazineHeldLocalEulerAngles;
        private SerializedProperty _lockMagazineToWellAxis;
        private SerializedProperty _magazineInsertionAxisLocal;
        private SerializedProperty _magazineExtractedDistance;
        private SerializedProperty _magazineInsertStartDistance;
        private SerializedProperty _referenceFamily;
        private SerializedProperty _animationFamilyContactOffsets;
        private SerializedProperty _magazineReachWindow;
        private SerializedProperty _magazineInsertWindow;
        private SerializedProperty _magazineAlignWindow;
        private SerializedProperty _poseCalibrationMode;
        private SerializedProperty _supportGripStyle;
        private SerializedProperty _adsFirePresentationMode;
        private SerializedProperty _hasAdsGunPose;
        private SerializedProperty _adsGunLocalPosition;
        private SerializedProperty _adsGunLocalEulerAngles;
        private SerializedProperty _hasElbowPoleHints;
        private SerializedProperty _leftElbowPoleHintCameraLocal;
        private SerializedProperty _rightElbowPoleHintCameraLocal;

        private Game.Presentation.Animation.FPWeaponPoseProfile Profile
            => (Game.Presentation.Animation.FPWeaponPoseProfile)target;

        private void OnEnable()
        {
            if (targets == null || targets.Length == 0 || targets[0] == null)
                return;
            _weaponRoot = serializedObject.FindProperty("weaponRoot");
            _rightHand = serializedObject.FindProperty("rightHand");
            _rightHandGrip = serializedObject.FindProperty("rightHandGrip");
            _leftSupportGrip = serializedObject.FindProperty("leftSupportGrip");
            _trigger = serializedObject.FindProperty("trigger");
            _magazineWell = serializedObject.FindProperty("magazineWell");
            _magazineGrip = serializedObject.FindProperty("magazineGrip");
            _magazineInsertGuide = serializedObject.FindProperty("magazineInsertGuide");
            _magazineExtracted = serializedObject.FindProperty("magazineExtracted");
            _hasRootCalibration = serializedObject.FindProperty("hasRootCalibration");
            _calibratedRootLocalPosition = serializedObject.FindProperty("calibratedRootLocalPosition");
            _calibratedRootLocalEulerAngles = serializedObject.FindProperty("calibratedRootLocalEulerAngles");
            _manualRootTransform = serializedObject.FindProperty("manualRootTransform");
            _alignRootToRightHand = serializedObject.FindProperty("alignRootToRightHand");
            _magazineOut = serializedObject.FindProperty("magazineOutNormalized");
            _hasMagazineCalibration = serializedObject.FindProperty("hasMagazineCalibration");
            _magazineIn = serializedObject.FindProperty("magazineInNormalized");
            _emptyOut = serializedObject.FindProperty("emptyMagazineOutNormalized");
            _emptyIn = serializedObject.FindProperty("emptyMagazineInNormalized");
            _magazineGrabWeight = serializedObject.FindProperty("magazineGrabWeight");
            _magazineInsertWeight = serializedObject.FindProperty("magazineInsertWeight");
            _magazineHeldLocalPosition = serializedObject.FindProperty("magazineHeldLocalPosition");
            _magazineHeldLocalEulerAngles = serializedObject.FindProperty("magazineHeldLocalEulerAngles");
            _lockMagazineToWellAxis = serializedObject.FindProperty("lockMagazineToWellAxis");
            _magazineInsertionAxisLocal = serializedObject.FindProperty("magazineInsertionAxisLocal");
            _magazineExtractedDistance = serializedObject.FindProperty("magazineExtractedDistance");
            _magazineInsertStartDistance = serializedObject.FindProperty("magazineInsertStartDistance");
            _referenceFamily = serializedObject.FindProperty("referenceFamily");
            _animationFamilyContactOffsets = serializedObject.FindProperty("animationFamilyContactOffsets");
            _magazineReachWindow = serializedObject.FindProperty("magazineReachWindow");
            _magazineInsertWindow = serializedObject.FindProperty("magazineInsertWindow");
            _magazineAlignWindow = serializedObject.FindProperty("magazineAlignWindow");
            _poseCalibrationMode = serializedObject.FindProperty("poseCalibrationMode");
            _supportGripStyle = serializedObject.FindProperty("supportGripStyle");
            _adsFirePresentationMode = serializedObject.FindProperty("adsFirePresentationMode");
            _hasAdsGunPose = serializedObject.FindProperty("hasAdsGunPose");
            _adsGunLocalPosition = serializedObject.FindProperty("adsGunLocalPosition");
            _adsGunLocalEulerAngles = serializedObject.FindProperty("adsGunLocalEulerAngles");
            _hasElbowPoleHints = serializedObject.FindProperty("hasElbowPoleHints");
            _leftElbowPoleHintCameraLocal = serializedObject.FindProperty("leftElbowPoleHintCameraLocal");
            _rightElbowPoleHintCameraLocal = serializedObject.FindProperty("rightElbowPoleHintCameraLocal");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawPlayModeCaptureControls();

            EditorGUILayout.LabelField("Weapon Interfaces", EditorStyles.boldLabel);
            Draw(_weaponRoot);
            Draw(_rightHand);
            Draw(_rightHandGrip);
            Draw(_leftSupportGrip);
            Draw(_trigger);
            Draw(_magazineWell);
            Draw(_magazineGrip);
            Draw(_magazineInsertGuide);
            Draw(_magazineExtracted);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("AnchoredDualPoseV2", EditorStyles.boldLabel);
            Draw(_poseCalibrationMode);
            Draw(_supportGripStyle);
            Draw(_adsFirePresentationMode);
            Draw(_hasAdsGunPose);
            Draw(_adsGunLocalPosition);
            Draw(_adsGunLocalEulerAngles);
            Draw(_hasElbowPoleHints);
            Draw(_leftElbowPoleHintCameraLocal);
            Draw(_rightElbowPoleHintCameraLocal);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Root Calibration", EditorStyles.boldLabel);
            Draw(_hasRootCalibration);
            Draw(_manualRootTransform);
            Draw(_calibratedRootLocalPosition);
            Draw(_calibratedRootLocalEulerAngles);
            Draw(_alignRootToRightHand);

            DrawManualRootControls();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reload Phases", EditorStyles.boldLabel);
            Draw(_hasMagazineCalibration);
            Draw(_magazineOut);
            Draw(_magazineIn);
            Draw(_emptyOut);
            Draw(_emptyIn);
            Draw(_magazineGrabWeight);
            Draw(_magazineInsertWeight);
            Draw(_magazineHeldLocalPosition);
            Draw(_magazineHeldLocalEulerAngles);
            Draw(_lockMagazineToWellAxis);
            if (_lockMagazineToWellAxis != null && _lockMagazineToWellAxis.boolValue)
            {
                Draw(_magazineInsertionAxisLocal);
                Draw(_magazineExtractedDistance);
                Draw(_magazineInsertStartDistance);
                EditorGUILayout.HelpBox(
                    "稳定弹匣模式会在 MagOut 到 MagIn 全程锁定 MagazineWell 朝向，并将弹匣轨迹投影到插拔轴。",
                    MessageType.Info);
            }
            Draw(_referenceFamily);
            Draw(_animationFamilyContactOffsets);
            Draw(_magazineReachWindow);
            Draw(_magazineInsertWindow);
            Draw(_magazineAlignWindow);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawPlayModeCaptureControls()
        {
            if (!EditorApplication.isPlaying) return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Play Mode Pose Capture", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "可在运行时（建议按住 RMB 后暂停）调整 LPW_Gun。Capture 会把当前局部位置/旋转写回源 FP Prefab，并启用 Manual Root Transform。",
                MessageType.Info);

            Transform root = Profile.WeaponRoot;
            using (new EditorGUI.DisabledScope(root == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Select Runtime LPW_Gun"))
                    Selection.activeGameObject = root.gameObject;
                if (GUILayout.Button("Capture Runtime Pose To Prefab"))
                    CaptureRuntimePoseToSourcePrefab(Profile);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void Draw(SerializedProperty property)
        {
            if (property != null) EditorGUILayout.PropertyField(property, true);
        }

        private void DrawManualRootControls()
        {
            Transform root = _weaponRoot != null
                ? _weaponRoot.objectReferenceValue as Transform
                : null;
            if (!_manualRootTransform.boolValue) return;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Manual Root Editing", EditorStyles.boldLabel);
            if (root == null)
            {
                EditorGUILayout.HelpBox("Assign Weapon Root before editing its transform.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.HelpBox(
                "Adjust the root below or use the Scene view handles. Changes are captured for Play Mode automatically.",
                MessageType.None);

            EditorGUI.BeginChangeCheck();
            Vector3 position = EditorGUILayout.Vector3Field("Root local position", root.localPosition);
            Vector3 eulerAngles = EditorGUILayout.Vector3Field("Root local rotation", root.localEulerAngles);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObjects(new Object[] { root, Profile }, "Adjust FP weapon root");
                root.localPosition = position;
                root.localEulerAngles = eulerAngles;
                CaptureRoot(root);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Capture Current"))
                CaptureRoot(root);
            if (GUILayout.Button("Apply Saved"))
                ApplySavedRoot(root);
            if (GUILayout.Button("Select Root"))
                Selection.activeGameObject = root.gameObject;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void OnSceneGUI()
        {
            if (target == null || serializedObject == null || serializedObject.targetObject == null)
                return;
            serializedObject.Update();
            Transform root = _weaponRoot.objectReferenceValue as Transform;
            if (root == null) return;

            bool changed = false;
            if (_manualRootTransform != null && _manualRootTransform.boolValue)
            {
                Handles.color = new Color(0.25f, 0.8f, 1f, 0.9f);
                Handles.Label(root.position, "  FP weapon root (manual)");
                EditorGUI.BeginChangeCheck();
                Vector3 position = Handles.PositionHandle(root.position, root.rotation);
                Quaternion rotation = Handles.RotationHandle(root.rotation, root.position);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObjects(new Object[] { root, Profile }, "Move FP weapon root");
                    root.position = position;
                    root.rotation = rotation;
                    CaptureRoot(root);
                    changed = true;
                }
            }

            changed |= DrawInterfaceHandle(Profile.RightHandGrip, new Color(1f, .35f, .2f), "RightHandGrip");
            changed |= DrawInterfaceHandle(Profile.LeftSupportGrip, new Color(.2f, 1f, .35f), "LeftSupportGrip");
            changed |= DrawInterfaceHandle(Profile.Trigger, new Color(1f, .8f, .2f), "Trigger");
            changed |= DrawInterfaceHandle(Profile.MagazineWell, new Color(.9f, .35f, 1f), "MagazineWell");
            changed |= DrawInterfaceHandle(Profile.MagazineGrip, new Color(.35f, .9f, 1f), "MagazineGrip");
            changed |= DrawInterfaceHandle(Profile.MagazineInsertGuide, new Color(1f, .55f, .1f), "MagazineInsertGuide");
            changed |= DrawInterfaceHandle(Profile.RearSight, Color.cyan, "RearSight");
            changed |= DrawInterfaceHandle(Profile.FrontSight, Color.magenta, "FrontSight");
            if (changed) SceneView.RepaintAll();
        }

        private bool DrawInterfaceHandle(Transform marker, Color color, string label)
        {
            if (marker == null) return false;
            Handles.color = color;
            Handles.Label(marker.position, "  " + label);
            EditorGUI.BeginChangeCheck();
            Vector3 position = Handles.PositionHandle(marker.position, marker.rotation);
            Quaternion rotation = Handles.RotationHandle(marker.rotation, marker.position);
            if (!EditorGUI.EndChangeCheck()) return false;

            Undo.RecordObject(marker, "Edit " + label);
            marker.position = position;
            marker.rotation = rotation;
            EditorUtility.SetDirty(marker);
            PrefabUtility.RecordPrefabInstancePropertyModifications(marker);
            SceneView.RepaintAll();
            return true;
        }

        private void CaptureRoot(Transform root)
        {
            serializedObject.Update();
            _calibratedRootLocalPosition.vector3Value = root.localPosition;
            _calibratedRootLocalEulerAngles.vector3Value = root.localEulerAngles;
            _hasRootCalibration.boolValue = true;
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(Profile);
            PrefabUtility.RecordPrefabInstancePropertyModifications(root);
        }

        private void ApplySavedRoot(Transform root)
        {
            Undo.RecordObject(root, "Apply saved FP weapon root");
            root.localPosition = _calibratedRootLocalPosition.vector3Value;
            root.localEulerAngles = _calibratedRootLocalEulerAngles.vector3Value;
            EditorUtility.SetDirty(root);
            PrefabUtility.RecordPrefabInstancePropertyModifications(root);
        }

        [MenuItem("Tools/LPW/Open AUG FP Pose Prefab")]
        private static void OpenAugPosePrefab()
        {
            OpenPosePrefab("Assets/_Project/Prefabs/Weapons/LPWTest/FP_LPW_Rifle2_02_View.prefab");
        }

        [MenuItem("Tools/LPW/Open MAC FP Pose Prefab")]
        private static void OpenMacPosePrefab()
        {
            OpenPosePrefab("Assets/_Project/Prefabs/Weapons/LPWTest/FP_LPW_SMG1_01_View.prefab");
        }

        [MenuItem("Tools/LPW/Pose Capture/Select Active Runtime LPW Gun", true)]
        private static bool CanSelectActiveRuntimeWeaponRoot()
            => EditorApplication.isPlaying && FindActiveRuntimeProfile() != null;

        [MenuItem("Tools/LPW/Pose Capture/Select Active Runtime LPW Gun")]
        private static void SelectActiveRuntimeWeaponRoot()
        {
            FPWeaponPoseProfile profile = FindActiveRuntimeProfile();
            if (profile == null || profile.WeaponRoot == null)
            {
                Debug.LogWarning("[FPWeaponPoseProfileEditor] No active runtime LPW weapon root found.");
                return;
            }

            Selection.activeGameObject = profile.WeaponRoot.gameObject;
            EditorGUIUtility.PingObject(profile.WeaponRoot.gameObject);
        }

        [MenuItem("Tools/LPW/Pose Capture/Capture Active Runtime Pose To FP Prefab", true)]
        private static bool CanCaptureActiveRuntimePose()
            => EditorApplication.isPlaying && FindActiveRuntimeProfile() != null;

        [MenuItem("Tools/LPW/Pose Capture/Capture Active Runtime Pose To FP Prefab")]
        private static void CaptureActiveRuntimePose()
        {
            FPWeaponPoseProfile profile = FindActiveRuntimeProfile();
            if (profile == null)
            {
                Debug.LogWarning("[FPWeaponPoseProfileEditor] No active runtime LPW pose profile found.");
                return;
            }

            CaptureRuntimePoseToSourcePrefab(profile);
        }

        private static void OpenPosePrefab(string path)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogError("[FPWeaponPoseProfileEditor] Missing prefab: " + path);
                return;
            }

            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            AssetDatabase.OpenAsset(prefab);
        }

        private static FPWeaponPoseProfile FindActiveRuntimeProfile()
        {
            foreach (FPWeaponPoseProfile profile in Resources.FindObjectsOfTypeAll<FPWeaponPoseProfile>())
            {
                if (profile == null || !profile.gameObject.scene.IsValid()) continue;
                if (profile.isActiveAndEnabled && profile.gameObject.activeInHierarchy)
                    return profile;
            }
            return null;
        }

        private static void CaptureRuntimePoseToSourcePrefab(FPWeaponPoseProfile runtimeProfile)
        {
            Transform runtimeRoot = runtimeProfile != null ? runtimeProfile.WeaponRoot : null;
            if (runtimeRoot == null)
            {
                Debug.LogError("[FPWeaponPoseProfileEditor] Runtime profile has no WeaponRoot.");
                return;
            }

            WeaponController controller = runtimeProfile.GetComponentInParent<WeaponController>();
            WeaponDefinition definition = controller != null ? controller.Definition : null;
            GameObject sourcePrefab = definition != null ? definition.FirstPersonViewPrefab : null;
            string prefabPath = sourcePrefab != null ? AssetDatabase.GetAssetPath(sourcePrefab) : string.Empty;
            if (string.IsNullOrEmpty(prefabPath))
            {
                prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(runtimeProfile.gameObject);
            }
            if (string.IsNullOrEmpty(prefabPath))
            {
                Debug.LogError("[FPWeaponPoseProfileEditor] Cannot resolve the source FP prefab for " + runtimeProfile.name);
                return;
            }

            Vector3 localPosition = runtimeRoot.localPosition;
            Vector3 localEulerAngles = runtimeRoot.localEulerAngles;
            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                FPWeaponPoseProfile prefabProfile = contents.GetComponent<FPWeaponPoseProfile>();
                Transform prefabRoot = prefabProfile != null ? prefabProfile.WeaponRoot : FindDeep(contents.transform, "LPW_Gun");
                if (prefabProfile == null || prefabRoot == null)
                    throw new System.InvalidOperationException("FP prefab is missing FPWeaponPoseProfile or LPW_Gun: " + prefabPath);

                prefabRoot.localPosition = localPosition;
                prefabRoot.localRotation = Quaternion.Euler(localEulerAngles);

                SerializedObject profileObject = new(prefabProfile);
                profileObject.FindProperty("hasRootCalibration").boolValue = true;
                profileObject.FindProperty("manualRootTransform").boolValue = true;
                profileObject.FindProperty("calibratedRootLocalPosition").vector3Value = localPosition;
                profileObject.FindProperty("calibratedRootLocalEulerAngles").vector3Value = localEulerAngles;
                profileObject.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            Debug.Log($"[FPWeaponPoseProfileEditor] Captured runtime LPW pose to {prefabPath}: "
                + $"position={localPosition:F5}, euler={localEulerAngles:F3}. Re-enter Play Mode to verify.");
            EditorGUIUtility.PingObject(sourcePrefab);
        }

        private static Transform FindDeep(Transform root, string targetName)
        {
            if (root == null) return null;
            if (root.name == targetName) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), targetName);
                if (found != null) return found;
            }
            return null;
        }
    }
}
