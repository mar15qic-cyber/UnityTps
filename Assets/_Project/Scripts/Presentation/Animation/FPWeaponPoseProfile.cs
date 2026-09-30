using System;
using System.Collections.Generic;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// First-person weapon pose contract. Every replacement mesh owns its own
    /// right-hand, trigger, support-hand and magazine interfaces; no weapon is
    /// expected to share another weapon's coordinates.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FPWeaponPoseProfile : MonoBehaviour
    {
        public enum ReloadHandPhase
        {
            Support,
            MagazineGrab,
            MagazineInsert
        }

        public enum ReloadContactPhase
        {
            None,
            ReachMagazine,
            CarryMagazine,
            AlignMagazine,
            ReachMagazineWell
        }

        [Serializable]
        public struct AnimationFamilyContactOffset
        {
            public FPAnimationReferenceFamily family;
            public bool configured;
            [Tooltip("Reference contact -> animated wrist position, expressed in contact local space.")]
            public Vector3 supportContactToWristLocalPosition;
            [Tooltip("Reference contact -> animated wrist rotation, expressed relative to contact.")]
            public Vector3 supportContactToWristLocalEulerAngles;

            public Quaternion SupportContactToWristRotation
                => Quaternion.Euler(supportContactToWristLocalEulerAngles);
        }

        [Header("Weapon interfaces")]
        [SerializeField] private Transform weaponRoot;
        [SerializeField] private Transform rightHand;
        [SerializeField] private Transform rightHandGrip;
        [SerializeField] private Transform leftSupportGrip;
        [SerializeField] private Transform trigger;
        [SerializeField] private Transform rearSight;
        [SerializeField] private Transform frontSight;
        [SerializeField] private Transform magazineWell;
        [SerializeField] private Transform magazineGrip;
        [Tooltip("Magazine-pivot pose at the start of the final straight insertion segment.")]
        [SerializeField] private Transform magazineInsertGuide;
        [Tooltip("Optional extracted pose for a weapon whose magazine must stay on one insertion axis while detached.")]
        [SerializeField] private Transform magazineExtracted;

        [Header("Anchored Dual Pose V2")]
        [Tooltip("V2 prefabs keep LPW_Gun directly under the animated weapon bone. Legacy prefabs may still create the migration pivot.")]
        [SerializeField] private LPWPoseCalibrationMode poseCalibrationMode;
        [SerializeField] private LPWSupportGripStyle supportGripStyle;
        [SerializeField] private LPWAdsFirePresentationMode adsFirePresentationMode = LPWAdsFirePresentationMode.ProceduralOnly;
        [SerializeField] private bool hasAdsGunPose;
        [SerializeField] private Vector3 adsGunLocalPosition;
        [SerializeField] private Vector3 adsGunLocalEulerAngles;
        [SerializeField] private bool hasElbowPoleHints;
        [SerializeField] private Vector3 leftElbowPoleHintCameraLocal = new(-0.35f, -0.80f, 0.10f);
        [SerializeField] private Vector3 rightElbowPoleHintCameraLocal = new(0.35f, -0.80f, 0.10f);

        [Header("Root calibration")]
        [SerializeField] private bool hasRootCalibration;
        [SerializeField] private Vector3 calibratedRootLocalPosition;
        [SerializeField] private Vector3 calibratedRootLocalEulerAngles;
        // When enabled, the prefab root is the source of truth in the editor.
        // The custom inspector mirrors manual edits into the calibration fields
        // so the same pose is restored when entering Play Mode.
        [SerializeField] private bool manualRootTransform;
        // A replacement mesh is calibrated against the animated palm, never
        // against its renderer bounds.  This is especially important for the
        // MAC-10 whose magazine/trigger are inside the grip.
        [SerializeField] private bool alignRootToRightHand = true;

        [Header("ADS viewmodel calibration")]
        [SerializeField] private bool hasAdsCalibration;
        [SerializeField] private Vector3 adsViewmodelLocalPosition;
        [SerializeField] private Vector3 adsViewmodelLocalEulerAngles;

        [Header("Reload phases")]
        [SerializeField] private bool hasMagazineCalibration;
        [SerializeField, Range(0f, 1f)] private float magazineOutNormalized = 0.18f;
        [SerializeField, Range(0f, 1f)] private float magazineInNormalized = 0.65f;
        [SerializeField, Range(0f, 1f)] private float emptyMagazineOutNormalized = 0.12f;
        [SerializeField, Range(0f, 1f)] private float emptyMagazineInNormalized = 0.45f;
        [SerializeField, Range(0f, 1f)] private float magazineGrabWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float magazineInsertWeight = 1f;
        [SerializeField] private Vector3 magazineHeldLocalPosition;
        [SerializeField] private Vector3 magazineHeldLocalEulerAngles;
        [Tooltip("When enabled, the detached magazine is authoritative and remains aligned to MagazineWell for the whole MagOut-MagIn interval. Keep disabled for the legacy animated-hand behavior.")]
        [SerializeField] private bool lockMagazineToWellAxis;
        [Tooltip("Insertion axis in WeaponRoot local space. For the AUG this is local +Y (vertical extraction/insertion).")]
        [SerializeField] private Vector3 magazineInsertionAxisLocal = Vector3.up;
        [SerializeField, Min(0f)] private float magazineExtractedDistance = .22f;
        [SerializeField, Min(0f)] private float magazineInsertStartDistance = .10f;

        [Header("Animation-family incremental reference")]
        [Tooltip("Auto follows the definition's selected LPFP animation family. Use an explicit value for attachment variants.")]
        [SerializeField] private FPAnimationReferenceFamily referenceFamily = FPAnimationReferenceFamily.Auto;
        [Tooltip("Contact-to-wrist offsets sampled from the LPFP family reference animation. These are not weapon/world coordinates.")]
        [SerializeField] private List<AnimationFamilyContactOffset> animationFamilyContactOffsets = new();
        [Tooltip("How far before/after the extraction contact the incremental reach correction is allowed to run.")]
        [SerializeField, Range(0f, .25f)] private float magazineReachWindow = .10f;
        [Tooltip("How far before/after the insertion contact the incremental reach correction is allowed to run.")]
        [SerializeField, Range(0f, .25f)] private float magazineInsertWindow = .08f;
        [Tooltip("Window immediately before straight insertion used to rotate and lift the magazine onto its guide axis.")]
        [SerializeField, Range(0f, .25f)] private float magazineAlignWindow;

        private float _activeMagazineOutNormalized;
        private float _activeMagazineInNormalized;
        private Transform _adsPivot;
        private bool _autoSightResolved;
        private bool _hasAutoSight;
        private Vector3 _autoRearSightLocal;
        private Vector3 _autoFrontSightLocal;

        public Transform WeaponRoot => weaponRoot;
        public Transform RightHand => rightHand;
        public Transform RightHandGrip => rightHandGrip;
        public Transform LeftSupportGrip => leftSupportGrip;
        public Transform Trigger => trigger;
        public Transform RearSight => rearSight;
        public Transform FrontSight => frontSight;
        public Transform AdsPivot => _adsPivot;
        public Transform MagazineWell => magazineWell;
        public Transform MagazineGrip => magazineGrip;
        public Transform MagazineInsertGuide => magazineInsertGuide;
        public Transform MagazineExtracted => magazineExtracted;
        public bool HasMagazineInsertGuide => magazineInsertGuide != null;
        public bool UsesStableMagazineCarry => lockMagazineToWellAxis && hasMagazineCalibration
            && magazineWell != null;
        public Vector3 MagazineInsertionAxisLocal => magazineInsertionAxisLocal;
        public float MagazineExtractedDistance => magazineExtractedDistance;
        public float MagazineInsertStartDistance => magazineInsertStartDistance;
        public LPWPoseCalibrationMode PoseCalibrationMode => poseCalibrationMode;
        public LPWSupportGripStyle SupportGripStyle => supportGripStyle;
        public LPWAdsFirePresentationMode AdsFirePresentationMode => adsFirePresentationMode;
        public bool IsAnchoredDualPoseV2 => poseCalibrationMode == LPWPoseCalibrationMode.AnchoredDualPoseV2;
        public bool HasAdsGunPose => hasAdsGunPose;
        public bool HasElbowPoleHints => hasElbowPoleHints;
        public Vector3 AdsGunLocalPosition => adsGunLocalPosition;
        public Quaternion AdsGunLocalRotation => Quaternion.Euler(adsGunLocalEulerAngles);
        public Vector3 AdsGunLocalEulerAngles => adsGunLocalEulerAngles;
        public Vector3 LeftElbowPoleHintCameraLocal => leftElbowPoleHintCameraLocal;
        public Vector3 RightElbowPoleHintCameraLocal => rightElbowPoleHintCameraLocal;
        public bool HasRootCalibration => hasRootCalibration;
        public bool ManualRootTransform => manualRootTransform;
        public Vector3 CalibratedRootLocalPosition => calibratedRootLocalPosition;
        public Vector3 CalibratedRootLocalEulerAngles => calibratedRootLocalEulerAngles;
        public bool HasAdsCalibration => hasAdsCalibration;
        public Vector3 AdsViewmodelLocalPosition => adsViewmodelLocalPosition;
        public Quaternion AdsViewmodelLocalRotation => Quaternion.Euler(adsViewmodelLocalEulerAngles);
        public Vector3 AdsViewmodelLocalEulerAngles => adsViewmodelLocalEulerAngles;
        public float RightHandGripError => rightHand == null || rightHandGrip == null
            ? float.PositiveInfinity
            : Vector3.Distance(rightHand.position, rightHandGrip.position);
        public float RightHandGripRotationError => rightHand == null || rightHandGrip == null
            ? float.PositiveInfinity
            : Quaternion.Angle(rightHand.rotation, rightHandGrip.rotation);
        public bool HasCompleteInterfaceLayout => rightHand != null && rightHandGrip != null
            && leftSupportGrip != null && trigger != null && magazineWell != null && magazineGrip != null;
        public bool HasMagazineCalibration => hasMagazineCalibration;
        public bool HasCompleteSightLayout => rearSight != null && frontSight != null
            && Vector3.Distance(rearSight.position, frontSight.position) > 0.001f;
        public bool HasCompleteAnchoredDualPoseV2 => IsAnchoredDualPoseV2
            && hasRootCalibration && hasAdsGunPose && hasElbowPoleHints
            && hasMagazineCalibration && HasCompleteInterfaceLayout && HasCompleteSightLayout;
        public float MagazineOutNormalized => _activeMagazineOutNormalized > 0f
            ? _activeMagazineOutNormalized : magazineOutNormalized;
        public float MagazineInNormalized => Mathf.Max(MagazineOutNormalized,
            _activeMagazineInNormalized > 0f ? _activeMagazineInNormalized : magazineInNormalized);
        public float EmptyMagazineOutNormalized => emptyMagazineOutNormalized;
        public float EmptyMagazineInNormalized => emptyMagazineInNormalized;
        public float MagazineGrabWeight => magazineGrabWeight;
        public float MagazineInsertWeight => magazineInsertWeight;
        public Vector3 MagazineHeldLocalPosition => magazineHeldLocalPosition;
        public Vector3 MagazineHeldLocalEulerAngles => magazineHeldLocalEulerAngles;
        public IReadOnlyList<AnimationFamilyContactOffset> AnimationFamilyContactOffsets
            => animationFamilyContactOffsets;
        public float MagazineReachWindow => magazineReachWindow;
        public float MagazineInsertWindow => magazineInsertWindow;
        public float MagazineAlignWindow => magazineAlignWindow;
        public FPAnimationReferenceFamily ReferenceFamily => referenceFamily;

        public void BeginReload(bool empty)
        {
            _activeMagazineOutNormalized = empty ? emptyMagazineOutNormalized : magazineOutNormalized;
            _activeMagazineInNormalized = empty ? emptyMagazineInNormalized : magazineInNormalized;
        }

        public void EndReload()
        {
            _activeMagazineOutNormalized = magazineOutNormalized;
            _activeMagazineInNormalized = magazineInNormalized;
        }

        private void Awake()
        {
            ResolveInterfaces();
            if (!HasCompleteAnchoredDualPoseV2)
                EnsureRuntimeAdsPivot();
            _activeMagazineOutNormalized = magazineOutNormalized;
            _activeMagazineInNormalized = magazineInNormalized;
            ApplyRootCalibration();
            if (GetComponent<FPRightHandIK>() == null)
                gameObject.AddComponent<FPRightHandIK>();
            if (!ValidateInterfaceLayout())
                Debug.LogWarning("[FPWeaponPoseProfile] Incomplete or misaligned weapon interfaces: " + name, this);
        }

        /// <summary>
        /// Returns the visible rear/front top-line used by ADS. Explicit authored markers win;
        /// legacy weapons derive a stable line once from their actual mesh vertices instead of
        /// pretending the LPW_Gun local X axis is the iron-sight axis.
        /// </summary>
        public bool TryGetSightLine(Transform fallbackRear, out Vector3 rearWorld, out Vector3 frontWorld)
        {
            if (HasCompleteSightLayout)
            {
                rearWorld = rearSight.position;
                frontWorld = frontSight.position;
                return true;
            }

            // Every production LPW prefab already has a calibrated SightReference and
            // Muzzle. The muzzle is below the sights, so connecting both points directly
            // would pitch the weapon upward. Use only its longitudinal X coordinate and
            // keep the rear sight's Y/Z plane: this is the weapon's parallel sight rail.
            Transform muzzle = FindDeep(weaponRoot, "Muzzle");
            if (weaponRoot != null && fallbackRear != null && muzzle != null)
            {
                Vector3 rearLocal = weaponRoot.InverseTransformPoint(fallbackRear.position);
                Vector3 muzzleLocal = weaponRoot.InverseTransformPoint(muzzle.position);
                Vector3 frontLocal = new(muzzleLocal.x, rearLocal.y, rearLocal.z);
                if (Vector3.Distance(rearLocal, frontLocal) > .01f)
                {
                    rearWorld = weaponRoot.TransformPoint(rearLocal);
                    frontWorld = weaponRoot.TransformPoint(frontLocal);
                    return true;
                }
            }

            if (!_autoSightResolved)
                ResolveAutoSightLine(fallbackRear);
            if (_hasAutoSight && weaponRoot != null)
            {
                rearWorld = weaponRoot.TransformPoint(_autoRearSightLocal);
                frontWorld = weaponRoot.TransformPoint(_autoFrontSightLocal);
                return true;
            }

            rearWorld = fallbackRear != null ? fallbackRear.position : Vector3.zero;
            frontWorld = rearWorld;
            return false;
        }

        /// <summary>
        /// Applies the authored root pose before any hand solver evaluates.
        /// This is intentionally a data operation, not a bounds-based guess.
        /// </summary>
        public void ApplyRootCalibration()
        {
            if (!hasRootCalibration || weaponRoot == null) return;
            // Manual mode makes the serialized Transform the source of truth in
            // both prefab mode and Play Mode. This lets a designer drag the
            // hidden-in-runtime weapon root in the editor without it snapping
            // back when the runtime adapters initialize.
            if (manualRootTransform) return;
            weaponRoot.localPosition = calibratedRootLocalPosition;
            weaponRoot.localRotation = Quaternion.Euler(calibratedRootLocalEulerAngles);

            // Consume the weapon-specific RightHandGrip at runtime.  The
            // authored local pose gets us into the right neighborhood; this
            // final palm delta prevents short/special meshes from floating
            // above the animated right hand.
            if (alignRootToRightHand && rightHand != null && rightHandGrip != null)
            {
                // Treat the authored RightHandGrip as a rigid palm anchor:
                // rotate first (around the weapon root), then translate after
                // the rotation has moved the marker. This also fixes the
                // MAC-10 trigger-hand orientation, not just its location.
                Quaternion deltaRotation = rightHand.rotation * Quaternion.Inverse(rightHandGrip.rotation);
                weaponRoot.rotation = deltaRotation * weaponRoot.rotation;
                weaponRoot.position += rightHand.position - rightHandGrip.position;
            }
        }

        /// <summary>
        /// Runtime/editor diagnostic for the complete interface contract. The
        /// trigger is deliberately checked too: it catches a root authored
        /// against bounds while the grip happens to look plausible.
        /// </summary>
        public bool ValidateInterfaceLayout(float maxGripError = 0.025f)
        {
            if (!HasCompleteInterfaceLayout) return false;
            if (RightHandGripError > maxGripError || RightHandGripRotationError > 12f) return false;
            return Vector3.Distance(trigger.position, rightHandGrip.position) < 0.35f;
        }

        public ReloadHandPhase GetReloadHandPhase(float normalizedProgress)
        {
            normalizedProgress = Mathf.Clamp01(normalizedProgress);
            if (normalizedProgress < MagazineOutNormalized) return ReloadHandPhase.Support;
            if (normalizedProgress < MagazineInNormalized) return ReloadHandPhase.MagazineGrab;
            return ReloadHandPhase.MagazineInsert;
        }

        /// <summary>
        /// Returns only the short contact windows. Carry is deliberately a
        /// separate phase so the LPFP animation owns the whole arm while the
        /// extracted magazine follows the hand.
        /// </summary>
        public ReloadContactPhase GetReloadContactPhase(float normalizedProgress)
        {
            normalizedProgress = Mathf.Clamp01(normalizedProgress);
            float outTime = MagazineOutNormalized;
            float inTime = MagazineInNormalized;
            // Stable-carry weapons are already handed off to the weapon-root
            // magazine driver at MagOut. Keep the left hand constrained for
            // the complete detached interval; there is deliberately no
            // Carry->Align rotation phase for this mode.
            if (UsesStableMagazineCarry && normalizedProgress >= outTime
                && normalizedProgress <= inTime)
                return ReloadContactPhase.ReachMagazineWell;
            if (normalizedProgress >= Mathf.Max(0f, outTime - magazineReachWindow)
                && normalizedProgress <= outTime)
                return ReloadContactPhase.ReachMagazine;
            if (normalizedProgress > outTime
                && normalizedProgress < GetMagazineAlignStart())
                return ReloadContactPhase.CarryMagazine;
            if (magazineInsertGuide != null && magazineAlignWindow > 0f
                && normalizedProgress >= GetMagazineAlignStart()
                && normalizedProgress < GetMagazineInsertStart())
                return ReloadContactPhase.AlignMagazine;
            if (normalizedProgress >= GetMagazineInsertStart()
                && normalizedProgress <= inTime)
                return ReloadContactPhase.ReachMagazineWell;
            return ReloadContactPhase.None;
        }

        public float GetMagazineAlignProgress(float normalizedProgress)
        {
            float start = GetMagazineAlignStart();
            float end = GetMagazineInsertStart();
            return end <= start ? 1f : Mathf.InverseLerp(start, end, normalizedProgress);
        }

        public float GetMagazineInsertProgress(float normalizedProgress)
        {
            float start = GetMagazineInsertStart();
            return MagazineInNormalized <= start
                ? 1f
                : Mathf.InverseLerp(start, MagazineInNormalized, normalizedProgress);
        }

        /// <summary>
        /// Resolves the actual wrist target for the support contact. The
        /// marker is a contact pose; the family offset preserves the authored
        /// LPFP wrist relationship instead of snapping the wrist to the marker.
        /// </summary>
        public bool TryGetSupportHandTarget(out FPContactPose target)
        {
            target = default;
            if (leftSupportGrip == null) return false;
            if (TryGetFamilyOffset(out var offset))
            {
                target = FPWeaponPoseMath.ComposeContactToWrist(
                    leftSupportGrip, offset.supportContactToWristLocalPosition,
                    offset.SupportContactToWristRotation);
                return true;
            }

            // Legacy LPW profiles authored only a weapon-specific support
            // marker and have no family contact offset.  The marker is already
            // the authored wrist target in that data shape; treating a missing
            // optional offset as "no target" silently disables Idle support IK.
            // Keep the fallback deterministic and let the solver follow the
            // existing marker without inventing a coordinate or changing AUG's
            // configured offset path.
            target = new FPContactPose(leftSupportGrip.position, leftSupportGrip.rotation);
            return true;
        }

        /// <summary>
        /// Resolves a wrist target from the weapon's magazine contact marker
        /// and the hand-local held-magazine pose. This is the key coordinate
        /// conversion for rear-magazine weapons such as the AUG.
        /// </summary>
        public bool TryGetReloadHandTarget(float normalizedProgress, out FPContactPose target)
        {
            target = default;
            if (!hasMagazineCalibration) return false;
            if (UsesStableMagazineCarry && normalizedProgress >= MagazineOutNormalized
                && normalizedProgress <= MagazineInNormalized
                && TryGetMagazinePose(normalizedProgress, out FPContactPose stableMagazine))
            {
                target = FPWeaponPoseMath.ResolveHandFromHeldMagazine(
                    stableMagazine.Position, stableMagazine.Rotation,
                    magazineHeldLocalPosition,
                    Quaternion.Euler(magazineHeldLocalEulerAngles));
                return true;
            }
            ReloadContactPhase phase = GetReloadContactPhase(normalizedProgress);
            Vector3 magazinePosition;
            Quaternion magazineRotation;
            if (phase == ReloadContactPhase.ReachMagazine && magazineGrip != null)
            {
                magazinePosition = magazineGrip.position;
                magazineRotation = magazineGrip.rotation;
            }
            else if (phase == ReloadContactPhase.AlignMagazine && magazineInsertGuide != null)
            {
                magazinePosition = magazineInsertGuide.position;
                magazineRotation = magazineInsertGuide.rotation;
            }
            else if (phase == ReloadContactPhase.ReachMagazineWell && magazineWell != null)
            {
                float t = magazineInsertGuide != null
                    ? Mathf.SmoothStep(0f, 1f, GetMagazineInsertProgress(normalizedProgress))
                    : 1f;
                magazinePosition = magazineInsertGuide != null
                    ? Vector3.Lerp(magazineInsertGuide.position, magazineWell.position, t)
                    : magazineWell.position;
                magazineRotation = magazineInsertGuide != null
                    ? Quaternion.Slerp(magazineInsertGuide.rotation, magazineWell.rotation, t)
                    : magazineWell.rotation;
            }
            else return false;

            target = FPWeaponPoseMath.ResolveHandFromHeldMagazine(
                magazinePosition, magazineRotation, magazineHeldLocalPosition,
                Quaternion.Euler(magazineHeldLocalEulerAngles));
            return true;
        }

        /// <summary>
        /// Evaluates the visible detached magazine pose for a weapon that opts
        /// into stable carry. All points are projected onto the authored
        /// insertion axis so a stale editor marker cannot reintroduce a
        /// diagonal/along-the-receiver slide. Rotation is never interpolated:
        /// it is the MagazineWell rotation for the complete detached interval.
        /// </summary>
        public bool TryGetMagazinePose(float normalizedProgress, out FPContactPose pose)
        {
            pose = default;
            if (!UsesStableMagazineCarry || magazineWell == null) return false;

            normalizedProgress = Mathf.Clamp01(normalizedProgress);
            float outTime = MagazineOutNormalized;
            float inTime = MagazineInNormalized;
            if (normalizedProgress < outTime || normalizedProgress > inTime) return false;

            Vector3 axis = ResolveMagazineInsertionAxis();
            Vector3 wellPosition = magazineWell.position;
            Vector3 guidePosition = GetAxisAlignedGuidePosition(wellPosition, axis);
            Vector3 extractedPosition = GetAxisAlignedExtractedPosition(
                wellPosition, guidePosition, axis);
            float insertStart = GetMagazineInsertStart();
            float extractEnd = Mathf.Min(insertStart,
                outTime + Mathf.Max(0f, magazineAlignWindow));
            bool hasExtractWindow = extractEnd > outTime;
            bool hasCarryWindow = insertStart > extractEnd;

            Vector3 magazinePosition;
            if (normalizedProgress <= outTime)
            {
                magazinePosition = wellPosition;
            }
            else if (normalizedProgress < extractEnd)
            {
                float t = Mathf.InverseLerp(outTime, extractEnd, normalizedProgress);
                // If there is no carry interval, merge extraction and alignment
                // so the zero-length boundary remains continuous.
                Vector3 extractTarget = hasCarryWindow ? extractedPosition : guidePosition;
                magazinePosition = Vector3.Lerp(wellPosition, extractTarget, t);
            }
            else if (normalizedProgress < insertStart)
            {
                float t = Mathf.InverseLerp(extractEnd, insertStart, normalizedProgress);
                // A zero-length extraction interval starts the carry at the well.
                Vector3 carryStart = hasExtractWindow ? extractedPosition : wellPosition;
                magazinePosition = Vector3.Lerp(carryStart, guidePosition, t);
            }
            else
            {
                float t = inTime <= insertStart
                    ? 1f
                    : Mathf.InverseLerp(insertStart, inTime, normalizedProgress);
                magazinePosition = Vector3.Lerp(guidePosition, wellPosition, t);
            }

            pose = new FPContactPose(magazinePosition, magazineWell.rotation);
            return true;
        }

        private Vector3 ResolveMagazineInsertionAxis()
        {
            Vector3 axis = weaponRoot != null
                ? weaponRoot.TransformDirection(magazineInsertionAxisLocal)
                : magazineWell != null ? magazineWell.up : Vector3.up;
            return axis.sqrMagnitude > .000001f ? axis.normalized : Vector3.up;
        }

        private Vector3 GetAxisAlignedGuidePosition(Vector3 wellPosition, Vector3 axis)
        {
            if (magazineInsertGuide == null)
                return wellPosition - axis * Mathf.Max(0f, magazineInsertStartDistance);

            Vector3 offset = Vector3.Project(magazineInsertGuide.position - wellPosition, axis);
            if (offset.sqrMagnitude < .000001f)
                offset = -axis * Mathf.Max(0f, magazineInsertStartDistance);
            return wellPosition + offset;
        }

        private Vector3 GetAxisAlignedExtractedPosition(
            Vector3 wellPosition, Vector3 guidePosition, Vector3 axis)
        {
            if (magazineExtracted != null)
            {
                Vector3 offset = Vector3.Project(magazineExtracted.position - wellPosition, axis);
                if (offset.sqrMagnitude > .000001f)
                    return wellPosition + offset;
            }

            Vector3 guideOffset = Vector3.Project(guidePosition - wellPosition, axis);
            float distance = Mathf.Max(
                Mathf.Abs(guideOffset.magnitude), magazineExtractedDistance);
            return wellPosition - axis * distance;
        }

        private float GetMagazineInsertStart()
            => Mathf.Max(MagazineOutNormalized, MagazineInNormalized - magazineInsertWindow);

        private float GetMagazineAlignStart()
            => Mathf.Max(MagazineOutNormalized, GetMagazineInsertStart() -
                (magazineInsertGuide != null ? magazineAlignWindow : 0f));

        public bool TryGetFamilyOffset(out AnimationFamilyContactOffset offset)
        {
            FPAnimationReferenceFamily family = GetResolvedReferenceFamily();
            for (int i = 0; i < animationFamilyContactOffsets.Count; i++)
            {
                AnimationFamilyContactOffset candidate = animationFamilyContactOffsets[i];
                if (candidate.family == family && candidate.configured)
                {
                    offset = candidate;
                    return true;
                }
            }
            offset = default;
            return false;
        }

        public Transform GetLeftHandTarget(bool reloading, float normalizedProgress)
        {
            if (!reloading) return leftSupportGrip;
            switch (GetReloadHandPhase(normalizedProgress))
            {
                case ReloadHandPhase.MagazineGrab:
                    return magazineGrip != null ? magazineGrip : leftSupportGrip;
                case ReloadHandPhase.MagazineInsert:
                    return magazineWell != null ? magazineWell : leftSupportGrip;
                default:
                    return leftSupportGrip;
            }
        }

        private void ResolveInterfaces()
        {
            weaponRoot ??= FindDeep(transform, "LPW_Gun");
            rightHand ??= FindDeep(transform, "hand_R");
            rightHandGrip ??= FindDeep(weaponRoot, "RightHandGrip");
            leftSupportGrip ??= FindDeep(weaponRoot, "LeftSupportGrip");
            trigger ??= FindDeep(weaponRoot, "Trigger");
            rearSight ??= FindDeep(weaponRoot, "RearSight");
            frontSight ??= FindDeep(weaponRoot, "FrontSight");
            magazineWell ??= FindDeep(weaponRoot, "MagazineWell");
            magazineGrip ??= FindDeep(weaponRoot, "MagazineGrip");
            magazineInsertGuide ??= FindDeep(weaponRoot, "MagazineInsertGuide");
            magazineExtracted ??= FindDeep(weaponRoot, "MagazineExtracted");
        }

        private FPAnimationReferenceFamily GetResolvedReferenceFamily()
        {
            if (referenceFamily != FPAnimationReferenceFamily.Auto)
                return referenceFamily;

            WeaponController controller = GetComponentInParent<WeaponController>();
            WeaponDefinition definition = controller != null ? controller.Definition : null;
            if (definition != null)
            {
                switch (definition.FirstPersonAnimationFamily)
                {
                    case FirstPersonAnimationFamily.Rifle01: return FPAnimationReferenceFamily.Rifle01;
                    case FirstPersonAnimationFamily.Rifle02: return FPAnimationReferenceFamily.Rifle02;
                    case FirstPersonAnimationFamily.Rifle03: return FPAnimationReferenceFamily.Rifle03;
                }

                string id = definition.WeaponId != null ? definition.WeaponId.ToLowerInvariant() : string.Empty;
                if (id.Contains("smg")) return FPAnimationReferenceFamily.Smg;
                if (id.Contains("pistol") || id.Contains("handgun")) return FPAnimationReferenceFamily.Pistol;
                if (id.Contains("shotgun")) return FPAnimationReferenceFamily.Shotgun;
                if (id.Contains("sniper")) return FPAnimationReferenceFamily.Sniper;
            }

            // Prefab Mode, edit-time validation and pooled views can resolve
            // before a WeaponController parent exists. A single authored
            // family entry is unambiguous and must remain usable there.
            FPAnimationReferenceFamily onlyConfigured = FPAnimationReferenceFamily.Auto;
            int configuredCount = 0;
            for (int i = 0; i < animationFamilyContactOffsets.Count; i++)
            {
                if (!animationFamilyContactOffsets[i].configured) continue;
                onlyConfigured = animationFamilyContactOffsets[i].family;
                configuredCount++;
            }
            if (configuredCount == 1) return onlyConfigured;
            return FPAnimationReferenceFamily.Native;
        }

        public Vector3 ResolveLeftElbowPolePosition(Transform camera)
        {
            return camera != null ? camera.TransformPoint(leftElbowPoleHintCameraLocal) : Vector3.zero;
        }

        public Vector3 ResolveRightElbowPolePosition(Transform camera)
        {
            return camera != null ? camera.TransformPoint(rightElbowPoleHintCameraLocal) : Vector3.zero;
        }

        private void EnsureRuntimeAdsPivot()
        {
            if (weaponRoot == null || weaponRoot.parent == null) return;
            if (weaponRoot.parent.name == "LPW_ADS_Pivot_Runtime")
            {
                _adsPivot = weaponRoot.parent;
                return;
            }

            Transform originalParent = weaponRoot.parent;
            int siblingIndex = weaponRoot.GetSiblingIndex();
            GameObject pivotObject = new("LPW_ADS_Pivot_Runtime")
            {
                hideFlags = HideFlags.DontSave
            };
            _adsPivot = pivotObject.transform;
            _adsPivot.SetParent(originalParent, false);
            _adsPivot.SetSiblingIndex(siblingIndex);
            _adsPivot.localPosition = Vector3.zero;
            _adsPivot.localRotation = Quaternion.identity;
            _adsPivot.localScale = Vector3.one;
            weaponRoot.SetParent(_adsPivot, false);
        }

        private void ResolveAutoSightLine(Transform fallbackRear)
        {
            _autoSightResolved = true;
            _hasAutoSight = false;
            if (weaponRoot == null) return;

            List<Vector3> vertices = new(1024);
            foreach (MeshFilter filter in weaponRoot.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                if (filter.sharedMesh.isReadable)
                    AppendVertices(vertices, filter.sharedMesh.vertices, filter.transform);
                else
                    AppendBoundsCorners(vertices, filter.sharedMesh.bounds, filter.transform);
            }
            foreach (SkinnedMeshRenderer renderer in weaponRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                if (renderer.sharedMesh.isReadable)
                    AppendVertices(vertices, renderer.sharedMesh.vertices, renderer.transform);
                else
                    AppendBoundsCorners(vertices, renderer.localBounds, renderer.transform);
            }
            if (vertices.Count < 2) return;

            Bounds bounds = new(vertices[0], Vector3.zero);
            for (int i = 1; i < vertices.Count; i++) bounds.Encapsulate(vertices[i]);
            if (bounds.size.x < .01f) return;

            Vector3 fallbackLocal = fallbackRear != null
                ? weaponRoot.InverseTransformPoint(fallbackRear.position)
                : new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            float rearX = Mathf.Clamp(fallbackLocal.x,
                bounds.min.x + bounds.size.x * .32f,
                bounds.max.x - bounds.size.x * .08f);
            float frontX = bounds.min.x + bounds.size.x * .12f;
            if (rearX - frontX < bounds.size.x * .2f)
                rearX = bounds.max.x - bounds.size.x * .22f;

            _autoRearSightLocal = SampleTopLine(vertices, bounds, rearX);
            _autoFrontSightLocal = SampleTopLine(vertices, bounds, frontX);
            _hasAutoSight = Vector3.Distance(_autoRearSightLocal, _autoFrontSightLocal) > .01f;
        }

        private void AppendVertices(List<Vector3> output, Vector3[] source, Transform sourceTransform)
        {
            for (int i = 0; i < source.Length; i++)
                output.Add(weaponRoot.InverseTransformPoint(sourceTransform.TransformPoint(source[i])));
        }

        private void AppendBoundsCorners(List<Vector3> output, Bounds bounds, Transform sourceTransform)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                Vector3 point = new(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z);
                output.Add(weaponRoot.InverseTransformPoint(sourceTransform.TransformPoint(point)));
            }
        }

        private static Vector3 SampleTopLine(List<Vector3> vertices, Bounds bounds, float targetX)
        {
            float xWindow = Mathf.Max(.008f, bounds.size.x * .065f);
            float zWindow = Mathf.Max(.008f, bounds.size.z * .32f);
            float maxY = float.NegativeInfinity;
            for (int pass = 0; pass < 2 && !float.IsFinite(maxY); pass++)
            {
                float passZ = pass == 0 ? zWindow : bounds.size.z;
                for (int i = 0; i < vertices.Count; i++)
                {
                    Vector3 point = vertices[i];
                    if (Mathf.Abs(point.x - targetX) <= xWindow
                        && Mathf.Abs(point.z - bounds.center.z) <= passZ)
                        maxY = Mathf.Max(maxY, point.y);
                }
            }

            if (!float.IsFinite(maxY))
                return new Vector3(targetX, bounds.max.y, bounds.center.z);

            float topBand = Mathf.Max(.002f, bounds.size.y * .025f);
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < vertices.Count; i++)
            {
                Vector3 point = vertices[i];
                if (Mathf.Abs(point.x - targetX) <= xWindow
                    && Mathf.Abs(point.z - bounds.center.z) <= zWindow
                    && point.y >= maxY - topBand)
                {
                    sum += point;
                    count++;
                }
            }
            return count > 0 ? sum / count : new Vector3(targetX, maxY, bounds.center.z);
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
