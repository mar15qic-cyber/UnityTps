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
        public bool HasCompleteSightLayout => rearSight != null && frontSight != null
            && Vector3.Distance(rearSight.position, frontSight.position) > 0.001f;
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
