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

        [Header("Weapon interfaces")]
        [SerializeField] private Transform weaponRoot;
        [SerializeField] private Transform rightHand;
        [SerializeField] private Transform rightHandGrip;
        [SerializeField] private Transform leftSupportGrip;
        [SerializeField] private Transform trigger;
        [SerializeField] private Transform magazineWell;
        [SerializeField] private Transform magazineGrip;

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

        [Header("Reload phases")]
        [SerializeField, Range(0f, 1f)] private float magazineOutNormalized = 0.18f;
        [SerializeField, Range(0f, 1f)] private float magazineInNormalized = 0.65f;
        [SerializeField, Range(0f, 1f)] private float emptyMagazineOutNormalized = 0.12f;
        [SerializeField, Range(0f, 1f)] private float emptyMagazineInNormalized = 0.45f;
        [SerializeField, Range(0f, 1f)] private float magazineGrabWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float magazineInsertWeight = 1f;
        [SerializeField] private Vector3 magazineHeldLocalPosition;
        [SerializeField] private Vector3 magazineHeldLocalEulerAngles;

        private float _activeMagazineOutNormalized;
        private float _activeMagazineInNormalized;

        public Transform WeaponRoot => weaponRoot;
        public Transform RightHand => rightHand;
        public Transform RightHandGrip => rightHandGrip;
        public Transform LeftSupportGrip => leftSupportGrip;
        public Transform Trigger => trigger;
        public Transform MagazineWell => magazineWell;
        public Transform MagazineGrip => magazineGrip;
        public bool HasRootCalibration => hasRootCalibration;
        public bool ManualRootTransform => manualRootTransform;
        public Vector3 CalibratedRootLocalPosition => calibratedRootLocalPosition;
        public Vector3 CalibratedRootLocalEulerAngles => calibratedRootLocalEulerAngles;
        public float RightHandGripError => rightHand == null || rightHandGrip == null
            ? float.PositiveInfinity
            : Vector3.Distance(rightHand.position, rightHandGrip.position);
        public float RightHandGripRotationError => rightHand == null || rightHandGrip == null
            ? float.PositiveInfinity
            : Quaternion.Angle(rightHand.rotation, rightHandGrip.rotation);
        public bool HasCompleteInterfaceLayout => rightHand != null && rightHandGrip != null
            && leftSupportGrip != null && trigger != null && magazineWell != null && magazineGrip != null;
        public float MagazineOutNormalized => _activeMagazineOutNormalized > 0f
            ? _activeMagazineOutNormalized : magazineOutNormalized;
        public float MagazineInNormalized => Mathf.Max(MagazineOutNormalized,
            _activeMagazineInNormalized > 0f ? _activeMagazineInNormalized : magazineInNormalized);
        public float MagazineGrabWeight => magazineGrabWeight;
        public float MagazineInsertWeight => magazineInsertWeight;
        public Vector3 MagazineHeldLocalPosition => magazineHeldLocalPosition;
        public Vector3 MagazineHeldLocalEulerAngles => magazineHeldLocalEulerAngles;

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
            _activeMagazineOutNormalized = magazineOutNormalized;
            _activeMagazineInNormalized = magazineInNormalized;
            ApplyRootCalibration();
            if (!ValidateInterfaceLayout())
                Debug.LogWarning("[FPWeaponPoseProfile] Incomplete or misaligned weapon interfaces: " + name, this);
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
            magazineWell ??= FindDeep(weaponRoot, "MagazineWell");
            magazineGrip ??= FindDeep(weaponRoot, "MagazineGrip");
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
