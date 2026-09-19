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
    /// Persistent authoring tool for schema-7 LPW dual-layer calibration. It writes only
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
            EditorGUILayout.LabelField("LPW schema-7 AnchoredDualPoseV2 calibration", EditorStyles.boldLabel);
            _definitionId = EditorGUILayout.TextField("Definition Id", _definitionId);
            EditorGUILayout.HelpBox(
                "Idle/anchors write LPW_Gun, the visible weapon model, and explicit interface data. "
                + "ADS writes only FP_Weapon_Root pose data. "
                + "The active scene is never saved.", MessageType.Info);

            if (GUILayout.Button("Open Formal FP Prefab")) OpenFormalPrefab(_definitionId);
            if (GUILayout.Button("Open Matching Original FP Reference")) OpenReferencePrefab(_definitionId);
            if (GUILayout.Button("Use Current Equipped Runtime LPW + Open Formal Prefab"))
                SelectCurrentEquippedRuntimeLpw();

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Select Matching Runtime LPW_Gun"))
                {
                    FPWeaponPoseProfile profile = ResolveUniqueRuntimeProfile(_definitionId);
                    if (profile != null && profile.WeaponRoot != null)
                        Selection.activeGameObject = profile.WeaponRoot.gameObject;
                }
                if (GUILayout.Button("Create/Select Manual Sight Markers"))
                    CreateRuntimeSightMarkers(_definitionId);
                if (GUILayout.Button("Save Idle Root + Explicit Interfaces"))
                    SaveRuntimeIdleAndInterfaces(_definitionId);
                if (GUILayout.Button("Solve + Save Full-ADS Viewmodel Pose"))
                    SolveAndSaveRuntimeAds(_definitionId);
            }
        }

        private void SelectCurrentEquippedRuntimeLpw()
        {
            if (!EditorApplication.isPlaying)
            {
                ShowRuntimeResolutionError("This action requires Play Mode so the currently equipped runtime weapon can be resolved.");
                return;
            }

            Arsenal[] arsenals = Resources.FindObjectsOfTypeAll<Arsenal>()
                .Where(x => x != null && x.isActiveAndEnabled && x.gameObject.activeInHierarchy
                    && x.gameObject.scene.IsValid() && x.ActiveWeapon != null)
                .ToArray();
            if (arsenals.Length == 0)
            {
                ShowRuntimeResolutionError("No active Arsenal with an equipped weapon was found.");
                return;
            }
            if (arsenals.Length != 1)
            {
                ShowRuntimeResolutionError($"The equipped runtime weapon is ambiguous: found {arsenals.Length} active Arsenals.");
                return;
            }

            Arsenal arsenal = arsenals[0];
            WeaponDefinition weapon = arsenal.ActiveWeapon;
            WeaponController controller = arsenal.GetComponentInParent<WeaponController>();
            if (weapon == null || controller == null || !controller.IsInitialized)
            {
                ShowRuntimeResolutionError("The active Arsenal has no initialized WeaponController/WeaponDefinition pair.");
                return;
            }
            if (controller.Definition != weapon && (controller.Definition == null
                || controller.Definition.WeaponId != weapon.WeaponId))
            {
                ShowRuntimeResolutionError("The Arsenal and WeaponController disagree during weapon equip; no prefab was selected.");
                return;
            }

            FPWeaponPoseProfile[] profiles = Resources.FindObjectsOfTypeAll<FPWeaponPoseProfile>()
                .Where(x => x != null && x.isActiveAndEnabled && x.gameObject.activeInHierarchy
                    && x.gameObject.scene.IsValid() && x.WeaponRoot != null
                    && x.GetComponentInParent<WeaponController>() == controller)
                .ToArray();
            if (profiles.Length != 1)
            {
                ShowRuntimeResolutionError($"Expected one active FPWeaponPoseProfile for {weapon.WeaponId}, found {profiles.Length}; no prefab was selected.");
                return;
            }

            LPWWeaponSpec[] specs = ResolveManifestRows(weapon.WeaponId);
            if (specs.Length == 0)
            {
                ShowRuntimeResolutionError($"The equipped weapon '{weapon.WeaponId}' is not an LPW manifest entry; no prefab was selected.");
                return;
            }
            if (specs.Length != 1)
            {
                ShowRuntimeResolutionError($"The LPW manifest has {specs.Length} matches for '{weapon.WeaponId}'; no prefab was selected.");
                return;
            }

            string path = FormalFpPath(specs[0]);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                ShowRuntimeResolutionError($"The formal LPW FP prefab is missing for '{weapon.WeaponId}': {path}");
                return;
            }

            _definitionId = weapon.WeaponId;
            Repaint();
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            AssetDatabase.OpenAsset(prefab);
        }

        private static LPWWeaponSpec[] ResolveManifestRows(string definitionId)
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            if (manifest == null || manifest.Weapons == null) return Array.Empty<LPWWeaponSpec>();
            return manifest.Weapons.Where(x => x != null && x.definitionId == definitionId).ToArray();
        }

        private static void ShowRuntimeResolutionError(string message)
            => EditorUtility.DisplayDialog("LPW Calibration", message, "OK");

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

        private static void OpenReferencePrefab(string definitionId)
        {
            LPWWeaponSpec spec = ResolveUniqueSpec(definitionId);
            if (spec == null) return;
            WeaponDefinition reference = FindDefinition(spec.animationDefinitionId);
            GameObject prefab = reference != null ? reference.FirstPersonViewPrefab : null;
            if (prefab == null)
                throw new InvalidOperationException("Reference FP prefab missing for " + spec.animationDefinitionId);
            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            AssetDatabase.OpenAsset(prefab);
        }

        private static void SaveRuntimeIdleAndInterfaces(string definitionId)
        {
            FPWeaponPoseProfile runtime = ResolveUniqueRuntimeProfile(definitionId);
            LPWWeaponSpec spec = ResolveUniqueSpec(definitionId);
            if (runtime == null || spec == null) return;
            if (runtime.WeaponRoot == null || runtime.RightHandGrip == null
                || runtime.LeftSupportGrip == null || runtime.Trigger == null
                || runtime.RearSight == null || runtime.FrontSight == null)
                throw new InvalidOperationException("Runtime profile is missing an explicit grip/trigger/rear/front interface.");
            Transform runtimeModel = FindVisibleModel(runtime.WeaponRoot);
            if (runtimeModel == null)
                throw new InvalidOperationException("Runtime LPW_Gun has no visible weapon model to save.");

            string path = FormalFpPath(spec);
            // Saving Idle/interfaces rewrites the geometry used by the ADS solve.  An
            // absolute ADS pose is therefore never reusable after this operation;
            // force the documented workflow to solve ADS again instead of allowing
            // a stale pose to survive a grip/sight/root edit.
            bool preserveAnchoredDualPose = false;
            bool saveMagazine = false;
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                FPWeaponPoseProfile target = contents.GetComponent<FPWeaponPoseProfile>();
                if (target == null || target.WeaponRoot == null)
                    throw new InvalidOperationException("Formal FP prefab is missing FPWeaponPoseProfile/LPW_Gun: " + path);

                CopyTransform(runtime.WeaponRoot, target.WeaponRoot);
                Transform targetModel = FindVisibleModel(target.WeaponRoot);
                if (targetModel == null)
                    throw new InvalidOperationException("Formal FP prefab has no visible weapon model to save: " + path);
                CopyTransform(runtimeModel, targetModel);
                CopyNamedInterface(runtime.RightHandGrip, target.WeaponRoot, "RightHandGrip");
                CopyNamedInterface(runtime.LeftSupportGrip, target.WeaponRoot, "LeftSupportGrip");
                CopyNamedInterface(runtime.Trigger, target.WeaponRoot, "Trigger");
                CopyNamedInterface(runtime.RearSight, target.WeaponRoot, "RearSight");
                CopyNamedInterface(runtime.FrontSight, target.WeaponRoot, "FrontSight");
                saveMagazine = runtime.HasMagazineCalibration
                    && runtime.MagazineWell != null && runtime.MagazineGrip != null;
                bool preserveExistingMagazine = target.HasMagazineCalibration
                    && FindDeep(target.WeaponRoot, "MagazineWell") != null
                    && FindDeep(target.WeaponRoot, "MagazineGrip") != null;
                if (saveMagazine)
                {
                    CopyNamedInterface(runtime.MagazineWell, target.WeaponRoot, "MagazineWell");
                    CopyNamedInterface(runtime.MagazineGrip, target.WeaponRoot, "MagazineGrip");
                    if (runtime.MagazineInsertGuide != null)
                        CopyNamedInterface(runtime.MagazineInsertGuide, target.WeaponRoot, "MagazineInsertGuide");
                    if (runtime.MagazineExtracted != null)
                        CopyNamedInterface(runtime.MagazineExtracted, target.WeaponRoot, "MagazineExtracted");
                }
                Transform targetRearSight = FindDeep(target.WeaponRoot, "RearSight");
                Transform targetFrontSight = FindDeep(target.WeaponRoot, "FrontSight");
                Transform targetMagazineWell = FindDeep(target.WeaponRoot, "MagazineWell");
                Transform targetMagazineGrip = FindDeep(target.WeaponRoot, "MagazineGrip");
                Transform targetMagazineInsertGuide = FindDeep(target.WeaponRoot, "MagazineInsertGuide");
                Transform targetMagazineExtracted = FindDeep(target.WeaponRoot, "MagazineExtracted");

                Game.Presentation.Weapon.WeaponView targetView = contents.GetComponent<Game.Presentation.Weapon.WeaponView>();
                if (targetView != null)
                {
                    SerializedObject view = new(targetView);
                    view.FindProperty("sightReference").objectReferenceValue = targetRearSight;
                    view.FindProperty("alignAdsToSightAxis").boolValue = true;
                    view.ApplyModifiedPropertiesWithoutUndo();
                }

                SerializedObject profile = new(target);
                profile.FindProperty("hasRootCalibration").boolValue = true;
                profile.FindProperty("calibratedRootLocalPosition").vector3Value = target.WeaponRoot.localPosition;
                profile.FindProperty("calibratedRootLocalEulerAngles").vector3Value = target.WeaponRoot.localEulerAngles;
                profile.FindProperty("manualRootTransform").boolValue = false;
                profile.FindProperty("alignRootToRightHand").boolValue = false;
                profile.FindProperty("poseCalibrationMode").enumValueIndex =
                    (int)(preserveAnchoredDualPose
                        ? LPWPoseCalibrationMode.AnchoredDualPoseV2
                        : LPWPoseCalibrationMode.LegacyUnverified);
                profile.FindProperty("hasAdsCalibration").boolValue = preserveAnchoredDualPose;
                profile.FindProperty("hasAdsGunPose").boolValue = preserveAnchoredDualPose;
                if (!preserveAnchoredDualPose)
                {
                    profile.FindProperty("adsGunLocalPosition").vector3Value = Vector3.zero;
                    profile.FindProperty("adsGunLocalEulerAngles").vector3Value = Vector3.zero;
                }
                profile.FindProperty("supportGripStyle").enumValueIndex = (int)spec.supportGripStyle;
                profile.FindProperty("adsFirePresentationMode").enumValueIndex =
                    (int)LPWAdsFirePresentationMode.ProceduralOnly;
                profile.FindProperty("hasElbowPoleHints").boolValue = true;
                profile.FindProperty("leftElbowPoleHintCameraLocal").vector3Value =
                    runtime.LeftElbowPoleHintCameraLocal;
                profile.FindProperty("rightElbowPoleHintCameraLocal").vector3Value =
                    runtime.RightElbowPoleHintCameraLocal;
                profile.FindProperty("rearSight").objectReferenceValue = targetRearSight;
                profile.FindProperty("frontSight").objectReferenceValue = targetFrontSight;
                profile.FindProperty("magazineWell").objectReferenceValue = targetMagazineWell;
                profile.FindProperty("magazineGrip").objectReferenceValue = targetMagazineGrip;
                profile.FindProperty("magazineInsertGuide").objectReferenceValue = targetMagazineInsertGuide;
                profile.FindProperty("magazineExtracted").objectReferenceValue = targetMagazineExtracted;
                profile.FindProperty("hasMagazineCalibration").boolValue = saveMagazine || preserveExistingMagazine;
                if (saveMagazine)
                {
                    profile.FindProperty("magazineOutNormalized").floatValue = runtime.MagazineOutNormalized;
                    profile.FindProperty("magazineInNormalized").floatValue = runtime.MagazineInNormalized;
                    profile.FindProperty("emptyMagazineOutNormalized").floatValue = runtime.EmptyMagazineOutNormalized;
                    profile.FindProperty("emptyMagazineInNormalized").floatValue = runtime.EmptyMagazineInNormalized;
                    profile.FindProperty("magazineHeldLocalPosition").vector3Value = runtime.MagazineHeldLocalPosition;
                    profile.FindProperty("magazineHeldLocalEulerAngles").vector3Value = runtime.MagazineHeldLocalEulerAngles;
                    profile.FindProperty("lockMagazineToWellAxis").boolValue = runtime.UsesStableMagazineCarry;
                    profile.FindProperty("magazineInsertionAxisLocal").vector3Value = runtime.MagazineInsertionAxisLocal;
                    profile.FindProperty("magazineExtractedDistance").floatValue = runtime.MagazineExtractedDistance;
                    profile.FindProperty("magazineInsertStartDistance").floatValue = runtime.MagazineInsertStartDistance;
                }
                profile.ApplyModifiedPropertiesWithoutUndo();
                CopyFamilyContactOffsets(runtime, target);

                if (saveMagazine)
                {
                    DetachableMagazineView targetMagazine = contents.GetComponent<DetachableMagazineView>();
                    if (targetMagazine != null)
                    {
                        SerializedObject magazine = new(targetMagazine);
                        magazine.FindProperty("heldLocalPosition").vector3Value = runtime.MagazineHeldLocalPosition;
                        magazine.FindProperty("heldLocalEulerAngles").vector3Value = runtime.MagazineHeldLocalEulerAngles;
                        magazine.FindProperty("ammoLeftMagOut").floatValue = runtime.MagazineOutNormalized;
                        magazine.FindProperty("ammoLeftMagIn").floatValue = runtime.MagazineInNormalized;
                        magazine.FindProperty("emptyMagOut").floatValue = runtime.EmptyMagazineOutNormalized;
                        magazine.FindProperty("emptyMagIn").floatValue = runtime.EmptyMagazineInNormalized;
                        magazine.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }

            spec.poseCalibrationMode = preserveAnchoredDualPose
                ? LPWPoseCalibrationMode.AnchoredDualPoseV2
                : LPWPoseCalibrationMode.LegacyUnverified;
            spec.hasAdsCalibration = preserveAnchoredDualPose;
            if (!preserveAnchoredDualPose)
            {
                spec.fpAdsGunPosition = Vector3.zero;
                spec.fpAdsGunEuler = Vector3.zero;
            }
            spec.hasGripCalibration = true;
            spec.fpRootPosition = runtime.WeaponRoot.localPosition;
            spec.fpRootEuler = runtime.WeaponRoot.localEulerAngles;
            spec.hasModelCalibration = true;
            spec.fpModelPosition = runtimeModel.localPosition;
            spec.fpModelEuler = runtimeModel.localEulerAngles;
            spec.fpModelScale = runtimeModel.localScale;
            spec.fpRightHandGripPosition = runtime.RightHandGrip.localPosition;
            spec.fpRightHandGripEuler = runtime.RightHandGrip.localEulerAngles;
            spec.fpLeftSupportGripPosition = runtime.LeftSupportGrip.localPosition;
            spec.fpLeftSupportGripEuler = runtime.LeftSupportGrip.localEulerAngles;
            spec.fpTriggerPosition = runtime.Trigger.localPosition;
            spec.fpTriggerEuler = runtime.Trigger.localEulerAngles;
            spec.hasSightCalibration = true;
            spec.fpRearSightPosition = runtime.RearSight.localPosition;
            spec.fpRearSightEuler = runtime.RearSight.localEulerAngles;
            spec.fpFrontSightPosition = runtime.FrontSight.localPosition;
            spec.fpFrontSightEuler = runtime.FrontSight.localEulerAngles;
            if (saveMagazine)
            {
                spec.hasMagazineCalibration = true;
                spec.magazinePartName = runtime.GetComponent<DetachableMagazineView>()?.MagazinePart?.name ?? spec.magazinePartName;
                spec.fpMagazineWellPosition = runtime.MagazineWell.localPosition;
                spec.fpMagazineWellEuler = runtime.MagazineWell.localEulerAngles;
                spec.fpMagazineGripPosition = runtime.MagazineGrip.localPosition;
                spec.fpMagazineGripEuler = runtime.MagazineGrip.localEulerAngles;
                spec.hasMagazineInsertGuide = runtime.MagazineInsertGuide != null;
                if (runtime.MagazineInsertGuide != null)
                {
                spec.fpMagazineInsertGuidePosition = runtime.MagazineInsertGuide.localPosition;
                    spec.fpMagazineInsertGuideEuler = runtime.MagazineInsertGuide.localEulerAngles;
                }
                spec.lockMagazineToWellAxis = runtime.UsesStableMagazineCarry;
                spec.fpMagazineInsertionAxisLocal = runtime.MagazineInsertionAxisLocal;
                spec.magazineExtractedDistance = runtime.MagazineExtractedDistance;
                spec.magazineInsertStartDistance = runtime.MagazineInsertStartDistance;
                spec.fpMagazineExtractedPosition = runtime.MagazineExtracted != null
                    ? runtime.MagazineExtracted.localPosition : Vector3.zero;
                spec.fpMagazineExtractedEuler = runtime.MagazineExtracted != null
                    ? runtime.MagazineExtracted.localEulerAngles : Vector3.zero;
                spec.magazineOutNormalized = runtime.MagazineOutNormalized;
                spec.magazineInNormalized = runtime.MagazineInNormalized;
                spec.emptyMagazineOutNormalized = runtime.EmptyMagazineOutNormalized;
                spec.emptyMagazineInNormalized = runtime.EmptyMagazineInNormalized;
                spec.magazineHeldLocalPosition = runtime.MagazineHeldLocalPosition;
                spec.magazineHeldLocalEuler = runtime.MagazineHeldLocalEulerAngles;
            }
            spec.magazineReachWindow = runtime.MagazineReachWindow;
            spec.magazineInsertWindow = runtime.MagazineInsertWindow;
            spec.magazineAlignWindow = runtime.MagazineAlignWindow;
            spec.fpAnimationFamilyContactOffsets = runtime.AnimationFamilyContactOffsets
                .Select(x => new LPWAnimationFamilyContactOffset
                {
                    family = x.family,
                    configured = x.configured,
                    supportContactToWristLocalPosition = x.supportContactToWristLocalPosition,
                    supportContactToWristLocalEulerAngles = x.supportContactToWristLocalEulerAngles
                }).ToList();
            spec.hasElbowPoleHints = true;
            spec.fpLeftElbowPoleHintCameraLocal = runtime.LeftElbowPoleHintCameraLocal;
            spec.fpRightElbowPoleHintCameraLocal = runtime.RightElbowPoleHintCameraLocal;
            spec.adsFirePresentationMode = LPWAdsFirePresentationMode.ProceduralOnly;
            MarkManifestDirty();
            Debug.Log("[LPWCalibration] Saved explicit idle/interfaces and visible model transform for "
                + definitionId + ": modelLocalPosition=" + runtimeModel.localPosition.ToString("F6")
                + ", modelLocalEuler=" + runtimeModel.localEulerAngles.ToString("F3") + ".");
        }

        private static void SolveAndSaveRuntimeAds(string definitionId)
        {
            FPWeaponPoseProfile runtime = ResolveUniqueRuntimeProfile(definitionId);
            LPWWeaponSpec spec = ResolveUniqueSpec(definitionId);
            if (runtime == null || spec == null) return;
            PlayerAimState aim = runtime.GetComponentInParent<PlayerAimState>();
            if (aim == null || aim.Ads01 < .95f)
                throw new InvalidOperationException("ADS solve requires Ads01 >= 0.95.");
            if (!TrySolveAdsGunPose(runtime, out Vector3 localPosition, out Quaternion localRotation,
                    out float depth, out float axisAngle, out float sharedForwardDepth))
                throw new InvalidOperationException("ADS solve rejected invalid camera/sight geometry.");

            UnityEngine.Camera solveCamera = ResolveViewCamera(runtime.transform);
            FPWeaponMotion viewmodelMotion = runtime.GetComponentInParent<FPWeaponMotion>();
            // The solver returns an absolute camera-forward correction measured
            // from a neutral shared-root depth.  Do not accumulate it onto the
            // previous profile value: the runtime root already contains that
            // value while the solve is running (the old .23m AUG value made the
            // renderer measurement include the correction twice).
            Vector3 sharedPosition = runtime.AdsViewmodelLocalPosition;
            if (solveCamera != null && viewmodelMotion != null)
            {
                Vector3 sharedDelta = viewmodelMotion.transform.InverseTransformVector(
                    solveCamera.transform.forward * sharedForwardDepth);
                sharedPosition.z = sharedDelta.z;
            }

            SerializedObject runtimeSo = new(runtime);
            runtimeSo.FindProperty("hasAdsCalibration").boolValue = true;
            runtimeSo.FindProperty("poseCalibrationMode").enumValueIndex =
                (int)LPWPoseCalibrationMode.AnchoredDualPoseV2;
            runtimeSo.FindProperty("adsFirePresentationMode").enumValueIndex =
                (int)LPWAdsFirePresentationMode.ProceduralOnly;
            runtimeSo.FindProperty("hasAdsGunPose").boolValue = true;
            runtimeSo.FindProperty("adsGunLocalPosition").vector3Value = localPosition;
            runtimeSo.FindProperty("adsGunLocalEulerAngles").vector3Value = localRotation.eulerAngles;
            runtimeSo.FindProperty("adsViewmodelLocalPosition").vector3Value = sharedPosition;
            runtimeSo.ApplyModifiedPropertiesWithoutUndo();

            string path = FormalFpPath(spec);
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                FPWeaponPoseProfile target = contents.GetComponent<FPWeaponPoseProfile>();
                if (target == null) throw new InvalidOperationException("Formal FP profile missing: " + path);
                SerializedObject targetSo = new(target);
                targetSo.FindProperty("hasAdsCalibration").boolValue = true;
                targetSo.FindProperty("poseCalibrationMode").enumValueIndex =
                    (int)LPWPoseCalibrationMode.AnchoredDualPoseV2;
                targetSo.FindProperty("adsFirePresentationMode").enumValueIndex =
                    (int)LPWAdsFirePresentationMode.ProceduralOnly;
                targetSo.FindProperty("hasAdsGunPose").boolValue = true;
                targetSo.FindProperty("adsGunLocalPosition").vector3Value = localPosition;
                targetSo.FindProperty("adsGunLocalEulerAngles").vector3Value = localRotation.eulerAngles;
                targetSo.FindProperty("adsViewmodelLocalPosition").vector3Value = sharedPosition;
                targetSo.ApplyModifiedPropertiesWithoutUndo();
                Game.Presentation.Weapon.WeaponView targetView = contents.GetComponent<Game.Presentation.Weapon.WeaponView>();
                if (targetView != null)
                {
                    SerializedObject view = new(targetView);
                    view.FindProperty("sightReference").objectReferenceValue = FindDeep(target.WeaponRoot, "RearSight");
                    view.FindProperty("alignAdsToSightAxis").boolValue = true;
                    view.ApplyModifiedPropertiesWithoutUndo();
                }
                EnsureV2Components(contents, target, spec);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }

            spec.hasAdsCalibration = true;
            spec.poseCalibrationMode = LPWPoseCalibrationMode.AnchoredDualPoseV2;
            spec.adsFirePresentationMode = LPWAdsFirePresentationMode.ProceduralOnly;
            spec.fpAdsGunPosition = localPosition;
            spec.fpAdsGunEuler = localRotation.eulerAngles;
            spec.fpAdsViewmodelPosition = sharedPosition;
            MarkManifestDirty();
            Debug.Log($"[LPWCalibration] Saved ADS parent pose for {definitionId}: position={localPosition:F6}, "
                + $"euler={localRotation.eulerAngles:F3}, rearDepth={depth:F4}m, "
                + $"sharedForwardDepth={sharedForwardDepth:F4}m, pre-solveAxis={axisAngle:F4}deg.");
        }

        private static void CreateRuntimeSightMarkers(string definitionId)
        {
            FPWeaponPoseProfile profile = ResolveUniqueRuntimeProfile(definitionId);
            if (profile == null || profile.WeaponRoot == null) return;
            Game.Presentation.Weapon.WeaponView view = profile.GetComponent<Game.Presentation.Weapon.WeaponView>();
            Transform source = view != null ? view.SightReference : null;
            Transform rear = profile.RearSight;
            Transform front = profile.FrontSight;

            if (rear == null)
                rear = CreateRuntimeMarker(profile.WeaponRoot, "RearSight", source);
            if (front == null)
                front = CreateRuntimeMarker(profile.WeaponRoot, "FrontSight", rear);

            SerializedObject profileSo = new(profile);
            profileSo.FindProperty("rearSight").objectReferenceValue = rear;
            profileSo.FindProperty("frontSight").objectReferenceValue = front;
            profileSo.ApplyModifiedPropertiesWithoutUndo();
            view?.OverrideSightReferenceForCalibration(rear);

            Selection.activeGameObject = profile.gameObject;
            EditorGUIUtility.PingObject(profile.gameObject);
            Debug.Log("[LPWCalibration] Created runtime-only RearSight/FrontSight markers for " + definitionId
                + ". FrontSight starts at RearSight; move both against the physical iron sights before saving.");
        }

        private static Transform CreateRuntimeMarker(Transform parent, string name, Transform source)
        {
            GameObject markerObject = new(name);
            Transform marker = markerObject.transform;
            marker.SetParent(parent, false);
            if (source != null)
            {
                marker.localPosition = parent.InverseTransformPoint(source.position);
                marker.localRotation = Quaternion.Inverse(parent.rotation) * source.rotation;
            }
            marker.localScale = Vector3.one;
            marker.gameObject.layer = parent.gameObject.layer;
            return marker;
        }

        private static Transform FindVisibleModel(Transform weaponRoot)
        {
            if (weaponRoot == null) return null;
            for (int i = 0; i < weaponRoot.childCount; i++)
            {
                Transform child = weaponRoot.GetChild(i);
                if (child.GetComponentInChildren<Renderer>(true) != null)
                    return child;
            }
            return null;
        }

        public static bool TrySolveAdsGunPose(FPWeaponPoseProfile profile, out Vector3 localPosition,
            out Quaternion localRotation, out float rearDepth, out float preSolveAxisAngle)
            => TrySolveAdsGunPose(profile, out localPosition, out localRotation,
                out rearDepth, out preSolveAxisAngle, out _);

        public static bool TrySolveAdsGunPose(FPWeaponPoseProfile profile, out Vector3 localPosition,
            out Quaternion localRotation, out float rearDepth, out float preSolveAxisAngle,
            out float sharedForwardDepth)
        {
            localPosition = Vector3.zero;
            localRotation = Quaternion.identity;
            rearDepth = 0f;
            preSolveAxisAngle = 180f;
            sharedForwardDepth = 0f;
            if (profile == null || !profile.HasCompleteSightLayout) return false;

            Transform weaponRoot = profile.WeaponRoot;
            if (weaponRoot == null || weaponRoot.parent == null) return false;
            UnityEngine.Camera camera = ResolveViewCamera(profile.transform);
            if (camera == null) return false;

            Vector3 direction = profile.FrontSight.position - profile.RearSight.position;
            if (direction.sqrMagnitude < 0.000001f) return false;
            direction.Normalize();
            rearDepth = Vector3.Dot(profile.RearSight.position - camera.transform.position, camera.transform.forward);
            // The current marker can itself be near/behind the clip plane while
            // calibrating a long receiver. The post-solve renderer guard below is
            // responsible for moving the complete gun to a valid camera depth.
            if (!float.IsFinite(rearDepth)) return false;

            preSolveAxisAngle = Vector3.Angle(direction, camera.transform.forward);
            Transform parent = weaponRoot.parent;
            if (parent == null) return false;
            Quaternion hipWorldRotation = weaponRoot.rotation;

            // FPRightHandIK follows the currently saved gun pose during ADS. If
            // that solved wrist is fed back into this calibration, the next gun
            // pose simply re-anchors to the previous result (the old depth-guard
            // closure). Temporarily suspend the optional solver and force the
            // Animator to evaluate its authored pose, then use that snapshot for
            // the hand/grip solve. The solver/component state is restored before
            // returning; this is an editor-only measurement operation.
            Vector3 authoredHandPosition = profile.RightHand != null
                ? profile.RightHand.position : Vector3.zero;
            Quaternion authoredHandRotation = profile.RightHand != null
                ? profile.RightHand.rotation : hipWorldRotation;
            bool hasAuthoredHand = TryCaptureAuthoredRightHand(
                profile, ref authoredHandPosition, ref authoredHandRotation);

            // The sight axis is the hard rotational constraint.  It leaves one
            // degree of freedom (roll around camera-forward); use that freedom to
            // keep the authored trigger hand orientation instead of letting a
            // sight-only frame twist the wrist.  Sampling the one-dimensional
            // domain is deterministic and avoids Euler/quaternion sign pitfalls.
            Quaternion gripLocalRotation = profile.RightHandGrip != null
                ? Quaternion.Inverse(hipWorldRotation) * profile.RightHandGrip.rotation
                : Quaternion.identity;
            Quaternion solvedWorldRotation = FPWeaponPoseMath.SolveSightAxisWithGripRoll(
                direction,
                camera.transform.forward,
                hipWorldRotation,
                gripLocalRotation,
                hasAuthoredHand ? authoredHandRotation : hipWorldRotation,
                hasAuthoredHand && profile.RightHandGrip != null,
                out _);

            localRotation = Quaternion.Inverse(parent.rotation) * solvedWorldRotation;

            if (profile.RightHand != null && profile.RightHandGrip != null)
            {
                // Translation is anchored by the animated palm.  Sight centering
                // remains a separate camera-plane correction on FP_Weapon_Root;
                // putting both constraints into this gun layer would drag the
                // arms root in camera-forward depth and recreate the old failure.
                Vector3 gripInWeapon = weaponRoot.InverseTransformPoint(
                    profile.RightHandGrip.position);
                Vector3 handInParent = parent.InverseTransformPoint(authoredHandPosition);
                localPosition = handInParent - localRotation * gripInWeapon;
            }
            else
            {
                // Legacy calibration fallback: no palm anchor is available, so
                // preserve the original sight-depth solve.  This branch is not
                // used by AnchoredDualPoseV2 production rows.
                Vector3 rearInWeapon = weaponRoot.InverseTransformPoint(profile.RearSight.position);
                Vector3 desiredRearWorld = camera.transform.position + camera.transform.forward * rearDepth;
                Vector3 desiredRearParent = parent.InverseTransformPoint(desiredRearWorld);
                Vector3 rotatedRearParent = localRotation * rearInWeapon;
                localPosition = desiredRearParent - rotatedRearParent;
            }

            // The palm anchor establishes the authored hand relationship, but
            // it does not guarantee that the chosen visible rear sight lands on
            // the camera center after a marker is moved to the actual iron-sight
            // rail. Apply the remaining two camera-plane degrees of freedom to
            // the complete gun layer. This preserves the solved sight axis and
            // roll while letting the V2 arm solver chase the resulting grip.
            Vector3 measureOriginalPosition = weaponRoot.localPosition;
            Quaternion measureOriginalRotation = weaponRoot.localRotation;
            try
            {
                weaponRoot.localPosition = localPosition;
                weaponRoot.localRotation = localRotation;
                Vector3 candidateRear = profile.RearSight.position;
                float candidateRearDepth = Vector3.Dot(
                    candidateRear - camera.transform.position, camera.transform.forward);
                Vector3 desiredRear = camera.transform.position
                    + camera.transform.forward * candidateRearDepth;
                Vector3 planeDelta = Vector3.ProjectOnPlane(
                    desiredRear - candidateRear, camera.transform.forward);
                localPosition += parent.InverseTransformVector(planeDelta);
            }
            finally
            {
                weaponRoot.localPosition = measureOriginalPosition;
                weaponRoot.localRotation = measureOriginalRotation;
            }

            // Long stocks and rear-magazine bodies are intentionally allowed to sit
            // behind the camera in the authored LPFP composition. Measuring the
            // complete renderer AABB here used to pull every rifle/shotgun/sniper
            // 0.3-0.6m toward the camera, exposing the stock and displacing both
            // arms. Only the sight/receiver envelope is a near-clip concern.
            Vector3 originalPosition = weaponRoot.localPosition;
            Quaternion originalRotation = weaponRoot.localRotation;
            float receiverMinDepth = float.PositiveInfinity;
            try
            {
                weaponRoot.localPosition = localPosition;
                weaponRoot.localRotation = localRotation;
                receiverMinDepth = MeasureVisibleReceiverMinDepth(profile, weaponRoot, camera);
            }
            finally
            {
                weaponRoot.localPosition = originalPosition;
                weaponRoot.localRotation = originalRotation;
            }

            // Keep the visible receiver a small distance in front of near clip. The
            // result is a positive distance along camera.forward; the caller converts
            // that vector into the shared-root local frame (usually local -Z for the
            // FP camera). This sign is important: writing +Z moves the gun toward a
            // camera whose forward is -Z and recreates the old black-screen failure.
            float requiredDepth = Mathf.Max(camera.nearClipPlane + .02f, .04f);
            if (!float.IsFinite(receiverMinDepth)) return false;
            // Shared depth is a composition correction, not a weapon framing
            // control.  Cap it at 8cm so a questionable imported receiver bound
            // can never drag the common arms root into a new authored pose.  If a
            // future weapon needs more than this, its stable legacy fallback (or
            // manual authoring) is preferable to silently changing arm coverage.
            sharedForwardDepth = Mathf.Clamp(requiredDepth - receiverMinDepth, 0f, .08f);
            return IsFinite(localPosition) && IsFinite(localRotation);
        }

        private static float MeasureVisibleReceiverMinDepth(
            FPWeaponPoseProfile profile, Transform weaponRoot, UnityEngine.Camera camera)
        {
            if (profile == null || weaponRoot == null || camera == null
                || profile.RearSight == null || profile.FrontSight == null)
                return float.PositiveInfinity;

            Vector3 rear = profile.RearSight.position;
            Vector3 front = profile.FrontSight.position;
            Vector3 sightAxis = front - rear;
            float sightLength = sightAxis.magnitude;
            if (!IsFinite(rear) || !IsFinite(front) || !IsFinite(sightLength)
                || sightLength < .001f)
                return float.PositiveInfinity;
            sightAxis /= sightLength;

            // The authored visible envelope is the receiver around the sights plus
            // the front sight/barrel interface. Stock/butt geometry behind the rear
            // sight is not part of this near-clip contract and may be cropped.
            const float rearAllowance = .08f;
            const float frontAllowance = .15f;
            float minLongitudinal = -rearAllowance;
            float maxLongitudinal = sightLength + frontAllowance;
            float minDepth = float.PositiveInfinity;
            Renderer[] renderers = weaponRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                Bounds bounds = renderer.bounds;
                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;
                float rendererMinLongitudinal = float.PositiveInfinity;
                float rendererMaxLongitudinal = float.NegativeInfinity;
                for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                    float longitudinal = Vector3.Dot(corner - rear, sightAxis);
                    if (!IsFinite(longitudinal)) continue;
                    rendererMinLongitudinal = Mathf.Min(rendererMinLongitudinal, longitudinal);
                    rendererMaxLongitudinal = Mathf.Max(rendererMaxLongitudinal, longitudinal);
                }

                // A single imported LPW renderer often contains the whole weapon
                // (stock + receiver + barrel). Its AABB spans both sides of the
                // sights, so its nearest corner is not a meaningful visible
                // receiver depth. Keep narrow, independently bounded parts and
                // let the sight markers cover the marker-only case below.
                float rendererSpan = rendererMaxLongitudinal - rendererMinLongitudinal;
                float maxReceiverSpan = sightLength * 1.25f + .10f;
                if (!IsFinite(rendererSpan) || rendererSpan > maxReceiverSpan)
                    continue;
                for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                    float longitudinal = Vector3.Dot(corner - rear, sightAxis);
                    if (!IsFinite(longitudinal)
                        || longitudinal < minLongitudinal || longitudinal > maxLongitudinal)
                        continue;
                    float depth = Vector3.Dot(corner - camera.transform.position,
                        camera.transform.forward);
                    if (IsFinite(depth) && depth < minDepth) minDepth = depth;
                }
            }

            // A marker-only fallback remains deterministic for tiny/marker-only
            // meshes and never invents a zero vector sight line.
            if (!float.IsFinite(minDepth))
            {
                float rearDepth = Vector3.Dot(rear - camera.transform.position,
                    camera.transform.forward);
                float frontDepth = Vector3.Dot(front - camera.transform.position,
                    camera.transform.forward);
                minDepth = Mathf.Min(rearDepth, frontDepth);
            }
            return minDepth;
        }

        private static float MeasureRendererMinDepthAtNeutralSharedDepth(
            FPWeaponPoseProfile profile, Transform weaponRoot, UnityEngine.Camera camera)
        {
            // FPWeaponMotion writes the serialized AdsViewmodelLocalPosition every
            // frame. Measure from the authored shared-root baseline so a prior
            // solve cannot be fed back into the depth guard. This is intentionally
            // limited to the selected V2 weapon's measurement; legacy views keep
            // their established path.
            FPWeaponMotion viewmodelMotion = profile != null
                ? profile.GetComponentInParent<FPWeaponMotion>() : null;
            Transform sharedRoot = viewmodelMotion != null ? viewmodelMotion.transform : null;
            if (sharedRoot == null || sharedRoot == weaponRoot)
                return MeasureRendererMinDepth(weaponRoot, camera);

            Vector3 originalPosition = sharedRoot.localPosition;
            try
            {
                Vector3 neutralPosition = originalPosition;
                neutralPosition.z = 0f;
                sharedRoot.localPosition = neutralPosition;
                return MeasureRendererMinDepth(weaponRoot, camera);
            }
            finally
            {
                sharedRoot.localPosition = originalPosition;
            }
        }

        private static bool TryCaptureAuthoredRightHand(FPWeaponPoseProfile profile,
            ref Vector3 handPosition, ref Quaternion handRotation)
        {
            if (profile == null || profile.RightHand == null) return false;

            FPRightHandIK rightIk = profile.GetComponent<FPRightHandIK>();
            System.Reflection.FieldInfo solveField = typeof(FPRightHandIK).GetField(
                "solveRightHand", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic);
            bool wasEnabled = rightIk != null && rightIk.enabled;
            bool wasSolving = rightIk != null && solveField != null
                && (bool)solveField.GetValue(rightIk);
            Animator[] animators = profile.GetComponentsInChildren<Animator>(true);
            bool[] animatorStates = new bool[animators.Length];
            try
            {
                if (rightIk != null)
                {
                    rightIk.enabled = false;
                    if (solveField != null) solveField.SetValue(rightIk, false);
                }

                for (int i = 0; i < animators.Length; i++)
                {
                    animatorStates[i] = animators[i] != null && animators[i].enabled;
                    if (animators[i] != null && !animators[i].enabled)
                        animators[i].enabled = true;
                    animators[i]?.Update(0f);
                }

                handPosition = profile.RightHand.position;
                handRotation = profile.RightHand.rotation;
                return IsFinite(handPosition) && IsFinite(handRotation);
            }
            finally
            {
                for (int i = 0; i < animators.Length; i++)
                    if (animators[i] != null) animators[i].enabled = animatorStates[i];
                if (rightIk != null)
                {
                    if (solveField != null) solveField.SetValue(rightIk, wasSolving);
                    rightIk.enabled = wasEnabled;
                }
            }
        }

        private static float MeasureRendererMinDepth(Transform weaponRoot,
            UnityEngine.Camera camera)
        {
            float minDepth = float.PositiveInfinity;
            Renderer[] renderers = weaponRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                Bounds bounds = renderer.bounds;
                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;
                for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
                    float depth = Vector3.Dot(corner - camera.transform.position,
                        camera.transform.forward);
                    if (depth < minDepth) minDepth = depth;
                }
            }
            return minDepth;
        }

        public static bool TrySolveAdsPose(FPWeaponPoseProfile profile, out Vector3 localPosition,
            out Quaternion localRotation, out float rearDepth, out float preSolveAxisAngle)
            => TrySolveAdsGunPose(profile, out localPosition, out localRotation,
                out rearDepth, out preSolveAxisAngle);

        private static UnityEngine.Camera ResolveViewCamera(Transform owner)
        {
            int fpLayer = LayerMask.NameToLayer("FirstPersonView");
            PlayerAimState aim = owner != null ? owner.GetComponentInParent<PlayerAimState>() : null;
            if (aim == null) return null;
            UnityEngine.Camera[] cameras = aim.GetComponentsInChildren<UnityEngine.Camera>(true);
            UnityEngine.Camera[] matches = cameras.Where(x => x != null && x.isActiveAndEnabled
                && x.name == "FP View Camera"
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
            if (manifest == null || manifest.SchemaVersion != 7)
                throw new InvalidOperationException("Schema-7 manifest missing: " + ManifestPath);
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
            if (target == null)
            {
                target = new GameObject(name).transform;
                target.SetParent(targetRoot, false);
                target.gameObject.layer = targetRoot.gameObject.layer;
            }
            CopyTransform(source, target);
        }

        private static void CopyTransform(Transform source, Transform target)
        {
            target.localPosition = source.localPosition;
            target.localRotation = source.localRotation;
            target.localScale = source.localScale;
        }

        private static void CopyFamilyContactOffsets(FPWeaponPoseProfile source, FPWeaponPoseProfile target)
        {
            SerializedObject sourceObject = new(source);
            SerializedObject targetObject = new(target);
            SerializedProperty sourceOffsets = sourceObject.FindProperty("animationFamilyContactOffsets");
            SerializedProperty targetOffsets = targetObject.FindProperty("animationFamilyContactOffsets");
            if (sourceOffsets == null || targetOffsets == null) return;

            targetObject.FindProperty("referenceFamily").enumValueIndex =
                sourceObject.FindProperty("referenceFamily").enumValueIndex;
            targetObject.FindProperty("magazineReachWindow").floatValue = source.MagazineReachWindow;
            targetObject.FindProperty("magazineInsertWindow").floatValue = source.MagazineInsertWindow;
            targetObject.FindProperty("magazineAlignWindow").floatValue = source.MagazineAlignWindow;
            targetObject.FindProperty("lockMagazineToWellAxis").boolValue = source.UsesStableMagazineCarry;
            targetObject.FindProperty("magazineInsertionAxisLocal").vector3Value = source.MagazineInsertionAxisLocal;
            targetObject.FindProperty("magazineExtractedDistance").floatValue = source.MagazineExtractedDistance;
            targetObject.FindProperty("magazineInsertStartDistance").floatValue = source.MagazineInsertStartDistance;

            targetOffsets.arraySize = sourceOffsets.arraySize;
            for (int i = 0; i < sourceOffsets.arraySize; i++)
            {
                SerializedProperty sourceElement = sourceOffsets.GetArrayElementAtIndex(i);
                SerializedProperty targetElement = targetOffsets.GetArrayElementAtIndex(i);
                targetElement.FindPropertyRelative("family").enumValueIndex =
                    sourceElement.FindPropertyRelative("family").enumValueIndex;
                targetElement.FindPropertyRelative("configured").boolValue =
                    sourceElement.FindPropertyRelative("configured").boolValue;
                targetElement.FindPropertyRelative("supportContactToWristLocalPosition").vector3Value =
                    sourceElement.FindPropertyRelative("supportContactToWristLocalPosition").vector3Value;
                targetElement.FindPropertyRelative("supportContactToWristLocalEulerAngles").vector3Value =
                    sourceElement.FindPropertyRelative("supportContactToWristLocalEulerAngles").vector3Value;
            }
            targetObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void MarkManifestDirty()
        {
            LPWWeaponManifest manifest = AssetDatabase.LoadAssetAtPath<LPWWeaponManifest>(ManifestPath);
            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssetIfDirty(manifest);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureV2Components(GameObject root, FPWeaponPoseProfile profile, LPWWeaponSpec spec)
        {
            if (root == null || profile == null) return;
            FPRightHandIK rightIk = root.GetComponent<FPRightHandIK>();
            if (rightIk == null) rightIk = root.AddComponent<FPRightHandIK>();
            SerializedObject rightSo = new(rightIk);
            rightSo.FindProperty("poseProfile").objectReferenceValue = profile;
            rightSo.FindProperty("upperArm").objectReferenceValue = FindDeep(root.transform, "arm_R");
            rightSo.FindProperty("lowerArm").objectReferenceValue = FindDeep(root.transform, "lower_arm_R");
            rightSo.FindProperty("hand").objectReferenceValue = FindDeep(root.transform, "hand_R");
            rightSo.FindProperty("positionWeight").floatValue = 1f;
            rightSo.FindProperty("rotationWeight").floatValue = 1f;
            // V2 ADS may intentionally move the complete gun layer forward after
            // the sight solve. The right arm must chase the resulting grip marker.
            rightSo.FindProperty("solveRightHand").boolValue = true;
            rightSo.ApplyModifiedPropertiesWithoutUndo();

            LPWGunPoseDriver driver = root.GetComponent<LPWGunPoseDriver>();
            if (driver == null) driver = root.AddComponent<LPWGunPoseDriver>();
            SerializedObject driverSo = new(driver);
            driverSo.FindProperty("poseProfile").objectReferenceValue = profile;
            driverSo.ApplyModifiedPropertiesWithoutUndo();

            // LPW two-handed weapons use the support-hand solver during normal
            // handling.  Pistols intentionally retain their authored/reload-only
            // behavior; classification comes from the manifest category, never
            // from a prefab or GameObject name.
            if (spec != null && spec.category != WeaponCatalogCategory.Pistol
                && profile.LeftSupportGrip != null)
            {
                FPLeftHandIK leftIk = root.GetComponent<FPLeftHandIK>();
                if (leftIk == null) leftIk = root.AddComponent<FPLeftHandIK>();
                SerializedObject leftSo = new(leftIk);
                leftSo.FindProperty("leftHandTarget").objectReferenceValue = profile.LeftSupportGrip;
                leftSo.FindProperty("upperArm").objectReferenceValue = FindDeep(root.transform, "arm_L");
                leftSo.FindProperty("lowerArm").objectReferenceValue = FindDeep(root.transform, "lower_arm_L");
                leftSo.FindProperty("hand").objectReferenceValue = FindDeep(root.transform, "hand_L");
                leftSo.FindProperty("poseProfile").objectReferenceValue = profile;
                leftSo.FindProperty("reloadOnly").boolValue = false;
                leftSo.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static bool HasReusableAnchoredDualPose(FPWeaponPoseProfile profile, LPWWeaponSpec spec)
        {
            return profile != null
                && spec != null
                && spec.hasAdsCalibration
                && profile.HasRootCalibration
                && profile.HasAdsGunPose
                && profile.HasAdsCalibration
                && profile.HasElbowPoleHints
                && profile.HasMagazineCalibration
                && profile.HasCompleteInterfaceLayout
                && profile.HasCompleteSightLayout;
        }

        private static WeaponDefinition FindDefinition(string id)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:WeaponDefinition"))
            {
                WeaponDefinition definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (definition != null && definition.WeaponId == id) return definition;
            }
            return null;
        }

        private static bool IsFinite(float value) => float.IsFinite(value);
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
