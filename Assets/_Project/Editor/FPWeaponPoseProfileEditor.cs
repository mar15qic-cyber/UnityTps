using UnityEditor;
using UnityEngine;

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
        private SerializedProperty _hasRootCalibration;
        private SerializedProperty _calibratedRootLocalPosition;
        private SerializedProperty _calibratedRootLocalEulerAngles;
        private SerializedProperty _manualRootTransform;
        private SerializedProperty _alignRootToRightHand;
        private SerializedProperty _magazineOut;
        private SerializedProperty _magazineIn;
        private SerializedProperty _emptyOut;
        private SerializedProperty _emptyIn;
        private SerializedProperty _magazineGrabWeight;
        private SerializedProperty _magazineInsertWeight;
        private SerializedProperty _magazineHeldLocalPosition;
        private SerializedProperty _magazineHeldLocalEulerAngles;

        private Game.Presentation.Animation.FPWeaponPoseProfile Profile
            => (Game.Presentation.Animation.FPWeaponPoseProfile)target;

        private void OnEnable()
        {
            _weaponRoot = serializedObject.FindProperty("weaponRoot");
            _rightHand = serializedObject.FindProperty("rightHand");
            _rightHandGrip = serializedObject.FindProperty("rightHandGrip");
            _leftSupportGrip = serializedObject.FindProperty("leftSupportGrip");
            _trigger = serializedObject.FindProperty("trigger");
            _magazineWell = serializedObject.FindProperty("magazineWell");
            _magazineGrip = serializedObject.FindProperty("magazineGrip");
            _hasRootCalibration = serializedObject.FindProperty("hasRootCalibration");
            _calibratedRootLocalPosition = serializedObject.FindProperty("calibratedRootLocalPosition");
            _calibratedRootLocalEulerAngles = serializedObject.FindProperty("calibratedRootLocalEulerAngles");
            _manualRootTransform = serializedObject.FindProperty("manualRootTransform");
            _alignRootToRightHand = serializedObject.FindProperty("alignRootToRightHand");
            _magazineOut = serializedObject.FindProperty("magazineOutNormalized");
            _magazineIn = serializedObject.FindProperty("magazineInNormalized");
            _emptyOut = serializedObject.FindProperty("emptyMagazineOutNormalized");
            _emptyIn = serializedObject.FindProperty("emptyMagazineInNormalized");
            _magazineGrabWeight = serializedObject.FindProperty("magazineGrabWeight");
            _magazineInsertWeight = serializedObject.FindProperty("magazineInsertWeight");
            _magazineHeldLocalPosition = serializedObject.FindProperty("magazineHeldLocalPosition");
            _magazineHeldLocalEulerAngles = serializedObject.FindProperty("magazineHeldLocalEulerAngles");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("Weapon Interfaces", EditorStyles.boldLabel);
            Draw(_weaponRoot);
            Draw(_rightHand);
            Draw(_rightHandGrip);
            Draw(_leftSupportGrip);
            Draw(_trigger);
            Draw(_magazineWell);
            Draw(_magazineGrip);

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
            Draw(_magazineOut);
            Draw(_magazineIn);
            Draw(_emptyOut);
            Draw(_emptyIn);
            Draw(_magazineGrabWeight);
            Draw(_magazineInsertWeight);
            Draw(_magazineHeldLocalPosition);
            Draw(_magazineHeldLocalEulerAngles);

            serializedObject.ApplyModifiedProperties();
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
            serializedObject.Update();
            if (_manualRootTransform == null || !_manualRootTransform.boolValue) return;
            Transform root = _weaponRoot.objectReferenceValue as Transform;
            if (root == null) return;

            Handles.color = new Color(0.25f, 0.8f, 1f, 0.9f);
            Handles.Label(root.position, "  FP weapon root (manual)");
            EditorGUI.BeginChangeCheck();
            Vector3 position = Handles.PositionHandle(root.position, root.rotation);
            Quaternion rotation = Handles.RotationHandle(root.rotation, root.position);
            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObjects(new Object[] { root, Profile }, "Move FP weapon root");
            root.position = position;
            root.rotation = rotation;
            CaptureRoot(root);
            SceneView.RepaintAll();
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
            const string path = "Assets/_Project/Prefabs/Weapons/LPWTest/FP_LPW_Rifle2_02_View.prefab";
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
    }
}
