using Game.Gameplay.Action;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Keeps a replacement magazine rigidly installed in the replacement gun,
    /// then hands it to the animated left hand only for the extraction interval.
    /// This avoids using another weapon pack's mesh pivot as if it were the
    /// original skinned magazine bind pose.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class DetachableMagazineView : MonoBehaviour
    {
        [SerializeField] private Transform magazinePart;
        [SerializeField] private Transform installedParent;
        [SerializeField] private Transform leftHand;
        [SerializeField] private FPWeaponPoseProfile poseProfile;
        [SerializeField] private Vector3 heldLocalPosition;
        [SerializeField] private Vector3 heldLocalEulerAngles;
        [SerializeField, Range(0f, 1f)] private float ammoLeftMagOut = 0.18f;
        [SerializeField, Range(0f, 1f)] private float ammoLeftMagIn = 0.65f;
        [SerializeField, Range(0f, 1f)] private float emptyMagOut = 0.12f;
        [SerializeField, Range(0f, 1f)] private float emptyMagIn = 0.45f;

        private WeaponController _controller;
        private FPWeaponAnimator _weaponAnimator;
        // Keep the magazine under the original visual weapon model while it is
        // installed. The serialized installedParent used to point at LPW_Gun,
        // which made a Play Mode adjustment of the nested rifle leave the
        // magazine behind in world space.
        private Transform _sourceParent;
        private Vector3 _installedLocalPosition;
        private Quaternion _installedLocalRotation;
        private Vector3 _installedLocalScale;
        private float _magOut;
        private float _magIn;
        private bool _reloading;
        private bool _inHand;

        public Transform MagazinePart => magazinePart;
        public Transform InstalledParent => installedParent;
        public bool IsMagazineInHand => _inHand;

        private void Awake()
        {
            _controller = GetComponentInParent<WeaponController>();
            _weaponAnimator = GetComponent<FPWeaponAnimator>();
            if (_weaponAnimator == null) _weaponAnimator = GetComponentInParent<FPWeaponAnimator>();
            poseProfile ??= GetComponent<FPWeaponPoseProfile>();
            poseProfile?.ApplyRootCalibration();
            ResolveLeftHand();

            if (magazinePart == null) return;

            _sourceParent = magazinePart.parent;
            Transform resolvedInstalledParent = _sourceParent != null ? _sourceParent : installedParent;
            if (resolvedInstalledParent == null) return;

            // This assignment is runtime-only for prefab instances. It also
            // keeps RestoreInstalled and diagnostics consistent with the
            // parent that actually carries the visible rifle model.
            installedParent = resolvedInstalledParent;
            magazinePart.SetParent(resolvedInstalledParent, true);
            _installedLocalPosition = magazinePart.localPosition;
            _installedLocalRotation = magazinePart.localRotation;
            _installedLocalScale = magazinePart.localScale;

            foreach (var collider in magazinePart.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
        }

        private void OnEnable()
        {
            if (_controller == null) _controller = GetComponentInParent<WeaponController>();
            if (_controller == null) return;
            _controller.OnReloadStarted += HandleReloadStarted;
            _controller.OnReloadCompleted += HandleReloadCompleted;
            _controller.OnReloadInterrupted += HandleReloadInterrupted;
        }

        private void OnDisable()
        {
            if (_controller != null)
            {
                _controller.OnReloadStarted -= HandleReloadStarted;
                _controller.OnReloadCompleted -= HandleReloadCompleted;
                _controller.OnReloadInterrupted -= HandleReloadInterrupted;
            }
            RestoreInstalled();
        }

        private void Update()
        {
            // The Animancer reload state is the only presentation clock. The
            // ActionSystem duration remains gameplay authority, but deriving a
            // second clock here creates a parent-switch race when clip speed
            // is adapted or a reload is interrupted.
            if (!_reloading || _weaponAnimator == null
                || !_weaponAnimator.HasReloadAnimationClock) return;

            float normalized = _weaponAnimator.CurrentReloadNormalizedTime;
            if (!_inHand && normalized >= _magOut && normalized < _magIn)
                AttachToHand();
            else if (_inHand && normalized >= _magIn)
                RestoreInstalled();
        }

        private void HandleReloadStarted()
        {
            RestoreInstalled();
            if (_controller?.Runtime == null) return;

            bool empty = _controller.Runtime.CurrentAmmo == 0;
            poseProfile?.BeginReload(empty);
            _magOut = poseProfile != null
                ? poseProfile.MagazineOutNormalized
                : empty ? emptyMagOut : ammoLeftMagOut;
            _magIn = poseProfile != null
                ? poseProfile.MagazineInNormalized
                : Mathf.Max(_magOut, empty ? emptyMagIn : ammoLeftMagIn);
            _reloading = true;
        }

        private void HandleReloadCompleted()
        {
            _reloading = false;
            poseProfile?.EndReload();
            RestoreInstalled();
        }

        private void HandleReloadInterrupted(ActionInterruptReason _)
        {
            _reloading = false;
            poseProfile?.EndReload();
            RestoreInstalled();
        }

        private void AttachToHand()
        {
            if (magazinePart == null || leftHand == null) return;
            if (poseProfile != null && poseProfile.UsesStableMagazineCarry)
            {
                // The hand is a follower in this mode. Parenting the visible
                // magazine to hand_L would feed Rifle03's front-magazine wrist
                // rotation back into the AUG mesh before the IK pass runs.
                Transform motionParent = poseProfile.WeaponRoot != null
                    ? poseProfile.WeaponRoot
                    : _sourceParent != null ? _sourceParent : installedParent;
                if (motionParent == null) return;
                magazinePart.SetParent(motionParent, true);
                magazinePart.localScale = _installedLocalScale;
                _inHand = true;
                ApplyStableMagazinePose();
                return;
            }
            magazinePart.SetParent(leftHand, false);
            magazinePart.localPosition = poseProfile != null && poseProfile.MagazineGrip != null
                ? poseProfile.MagazineHeldLocalPosition
                : heldLocalPosition;
            magazinePart.localRotation = Quaternion.Euler(
                poseProfile != null && poseProfile.MagazineGrip != null
                    ? poseProfile.MagazineHeldLocalEulerAngles
                    : heldLocalEulerAngles);
            magazinePart.localScale = _installedLocalScale;
            _inHand = true;
        }

        private void LateUpdate()
        {
            if (!_reloading || !_inHand || poseProfile == null
                || !poseProfile.UsesStableMagazineCarry || _weaponAnimator == null
                || !_weaponAnimator.HasReloadAnimationClock)
                return;
            ApplyStableMagazinePose();
        }

        private void ApplyStableMagazinePose()
        {
            if (magazinePart == null || poseProfile == null || _weaponAnimator == null
                || !_weaponAnimator.HasReloadAnimationClock)
                return;
            if (poseProfile.TryGetMagazinePose(
                    _weaponAnimator.CurrentReloadNormalizedTime, out FPContactPose pose))
                magazinePart.SetPositionAndRotation(pose.Position, pose.Rotation);
        }

        private void RestoreInstalled()
        {
            Transform resolvedInstalledParent = _sourceParent != null ? _sourceParent : installedParent;
            if (magazinePart == null || resolvedInstalledParent == null) return;
            magazinePart.SetParent(resolvedInstalledParent, false);
            magazinePart.localPosition = _installedLocalPosition;
            magazinePart.localRotation = _installedLocalRotation;
            magazinePart.localScale = _installedLocalScale;
            _inHand = false;
        }

        private void ResolveLeftHand()
        {
            if (leftHand != null) return;

            var ownAnimator = GetComponent<Animator>();
            if (ownAnimator != null && ownAnimator.isHuman)
                leftHand = ownAnimator.GetBoneTransform(HumanBodyBones.LeftHand);
            if (leftHand == null)
                leftHand = FindDeep(transform, "hand_L");
            if (leftHand != null) return;

            foreach (var animator in GetComponentsInParent<Animator>(true))
            {
                if (!animator.isHuman) continue;
                leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                if (leftHand != null) break;
            }
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
