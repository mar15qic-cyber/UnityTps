using Game.Gameplay.Action;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Data-driven first-person support-hand correction. LPFP remains the base
    /// animation, while the support hand is solved onto the weapon-specific
    /// support interface during ordinary handling and reload.
    /// </summary>
    [DefaultExecutionOrder(40)]
    public sealed class FPLeftHandIK : MonoBehaviour
    {
        [SerializeField] private Transform leftHandTarget;
        [SerializeField] private Transform upperArm;
        [SerializeField] private Transform lowerArm;
        [SerializeField] private Transform hand;
        [SerializeField] private FPWeaponPoseProfile poseProfile;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField, Range(0f, 1f)] private float positionWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float rotationWeight = 0.35f;
        [SerializeField, Min(0f)] private float blendSeconds = 0.08f;
        // Normal handling keeps the support-hand constraint active. Reload
        // handling is phase-gated below so the authored LPFP clip still owns
        // the approach and return portions of the animation.
        [SerializeField] private bool reloadOnly;
        [SerializeField, Range(0f, 1f)] private float reloadIkStartNormalized = 0.1f;
        [SerializeField, Range(0f, 1f)] private float reloadIkEndNormalized = 0.96f;
        [SerializeField, Min(0f)] private float reloadEnterBlendSeconds = 0.12f;
        [SerializeField, Min(0f)] private float reloadExitBlendSeconds = 0.12f;
        [SerializeField, Min(0f)] private float targetBlendSeconds = 0.08f;
        [SerializeField, Range(0f, 1f)] private float reloadReturnStartWeight = 0.35f;
        [Tooltip("Maximum positional correction while the authored reload animation is handling the magazine.")]
        [SerializeField, Range(0f, 1f)] private float reloadPositionWeight = 0.7f;
        [Tooltip("Maximum rotational correction while the authored reload animation is handling the magazine.")]
        [SerializeField, Range(0f, 1f)] private float reloadRotationWeight = 0.12f;

        private WeaponController _controller;
        private ActionSystem _actionSystem;
        private FPWeaponAnimator _weaponAnimator;
        private float _currentWeight;
        private bool _reloadActive;
        private Transform _activeTarget;
        private Vector3 _smoothedTargetPosition;
        private Quaternion _smoothedTargetRotation = Quaternion.identity;
        private Vector3 _smoothedTargetLocalPosition;
        private Quaternion _smoothedTargetLocalRotation = Quaternion.identity;
        private Vector3 _targetLocalPositionVelocity;
        private Transform _targetFrame;
        private bool _hasSmoothedTarget;
        private Transform _viewCamera;
        private float _supportReturnElapsed = -1f;
        private bool _hasMagazineAlignStart;
        private FPContactPose _magazineAlignStart;

        public Transform LeftHandTarget => leftHandTarget;
        public bool ReloadOnly => reloadOnly;
        public float ReloadIkStartNormalized => reloadIkStartNormalized;
        public float ReloadIkEndNormalized => reloadIkEndNormalized;

        private void Awake()
        {
            _controller = GetComponentInParent<WeaponController>();
            _actionSystem = GetComponentInParent<ActionSystem>();
            _weaponAnimator = GetComponent<FPWeaponAnimator>();
            if (_weaponAnimator == null) _weaponAnimator = GetComponentInParent<FPWeaponAnimator>();
            poseProfile ??= GetComponent<FPWeaponPoseProfile>();
            aimState ??= GetComponentInParent<PlayerAimState>();
            upperArm ??= FindDeep(transform, "arm_L");
            lowerArm ??= FindDeep(transform, "lower_arm_L");
            hand ??= FindDeep(transform, "hand_L");
            _viewCamera = ResolveViewCamera();
        }

        private void OnEnable()
        {
            if (_controller == null) _controller = GetComponentInParent<WeaponController>();
            if (_actionSystem == null) _actionSystem = GetComponentInParent<ActionSystem>();
            if (_weaponAnimator == null) _weaponAnimator = GetComponent<FPWeaponAnimator>();
            if (_weaponAnimator == null) _weaponAnimator = GetComponentInParent<FPWeaponAnimator>();
            if (_controller == null) return;
            _controller.OnReloadStarted += HandleReloadStarted;
            _controller.OnReloadCompleted += HandleReloadEnded;
            _controller.OnReloadInterrupted += HandleReloadInterrupted;
            _reloadActive = _controller.Runtime != null
                && _controller.Runtime.State == WeaponRuntimeState.Reloading;
            ClearConstraintState();
        }

        private void OnDisable()
        {
            if (_controller != null)
            {
                _controller.OnReloadStarted -= HandleReloadStarted;
                _controller.OnReloadCompleted -= HandleReloadEnded;
                _controller.OnReloadInterrupted -= HandleReloadInterrupted;
            }
            _reloadActive = false;
            ClearConstraintState();
        }

        private void LateUpdate()
        {
            // Holster/draw clips already contain the complete authored arm
            // motion. Keeping this constraint alive during SwitchWeapon
            // makes the old hand target pull the new clip across the body and
            // is the source of the visible elbow flip.
            if (_weaponAnimator != null && _weaponAnimator.IsWeaponTransitionAnimationActive)
            {
                ClearConstraintState();
                return;
            }
            if (_weaponAnimator == null && _actionSystem != null
                && _actionSystem.CurrentAction == PlayerActionType.SwitchWeapon)
            {
                ClearConstraintState();
                return;
            }

            bool reloadIntent = _reloadActive || (_controller?.Runtime?.State == WeaponRuntimeState.Reloading);
            bool hasReloadClock = _weaponAnimator == null || _weaponAnimator.HasReloadAnimationClock;
            if (reloadIntent && !hasReloadClock)
            {
                ClearConstraintState();
                return;
            }
            bool reloading = reloadIntent;
            float normalized = _weaponAnimator != null && _weaponAnimator.HasReloadAnimationClock
                ? _weaponAnimator.CurrentReloadNormalizedTime
                : GetReloadProgress();
            float ads = aimState != null ? aimState.Ads01 : 0f;
            Transform supportTarget = poseProfile != null && poseProfile.LeftSupportGrip != null
                ? poseProfile.LeftSupportGrip
                : leftHandTarget;
            FPContactPose resolvedTarget = default;
            Transform target = null;
            bool hasTarget = false;
            FPWeaponPoseProfile.ReloadContactPhase reloadPhase = FPWeaponPoseProfile.ReloadContactPhase.None;
            if (reloading)
            {
                reloadPhase = poseProfile != null
                    ? poseProfile.GetReloadContactPhase(normalized)
                    : FPWeaponPoseProfile.ReloadContactPhase.None;
                if (reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.AlignMagazine
                    && (poseProfile == null || !poseProfile.UsesStableMagazineCarry)
                    && poseProfile != null && poseProfile.MagazineInsertGuide != null && hand != null)
                {
                    if (!_hasMagazineAlignStart)
                    {
                        _magazineAlignStart = FPWeaponPoseMath.ComposeHeldMagazine(
                            hand.position, hand.rotation,
                            poseProfile.MagazineHeldLocalPosition,
                            Quaternion.Euler(poseProfile.MagazineHeldLocalEulerAngles));
                        _hasMagazineAlignStart = true;
                    }

                    float t = Mathf.SmoothStep(0f, 1f,
                        poseProfile.GetMagazineAlignProgress(normalized));
                    Vector3 magazinePosition = Vector3.Lerp(
                        _magazineAlignStart.Position,
                        poseProfile.MagazineInsertGuide.position, t);
                    Quaternion magazineRotation = Quaternion.Slerp(
                        _magazineAlignStart.Rotation,
                        poseProfile.MagazineInsertGuide.rotation, t);
                    resolvedTarget = FPWeaponPoseMath.ResolveHandFromHeldMagazine(
                        magazinePosition, magazineRotation,
                        poseProfile.MagazineHeldLocalPosition,
                        Quaternion.Euler(poseProfile.MagazineHeldLocalEulerAngles));
                    hasTarget = true;
                }
                else
                {
                    hasTarget = poseProfile != null
                        && poseProfile.TryGetReloadHandTarget(normalized, out resolvedTarget);
                    if (reloadPhase != FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell)
                        _hasMagazineAlignStart = false;
                }

                target = reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.ReachMagazine
                    ? poseProfile?.MagazineGrip
                    : reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.AlignMagazine
                        ? poseProfile?.MagazineInsertGuide
                        : reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell
                            ? poseProfile?.MagazineWell
                            : null;
            }
            else
            {
                _hasMagazineAlignStart = false;
                hasTarget = poseProfile != null
                    ? poseProfile.TryGetSupportHandTarget(out resolvedTarget)
                    : supportTarget != null;
                if (poseProfile == null && supportTarget != null)
                    resolvedTarget = new FPContactPose(supportTarget.position, supportTarget.rotation);
                target = supportTarget;
            }
            bool supportIk = !reloading && !reloadOnly && hasTarget;
            bool supportReturn = !reloading && _supportReturnElapsed >= 0f && hasTarget;
            bool adsSupport = supportIk && ads > .0001f;
            bool ikWindow = reloading ? IsIkWindow(reloading, normalized) : supportIk || supportReturn;

            // Normal handling is a rigid support-hand constraint whenever the
            // family offset has been authored. The target is already a wrist
            // pose composed from contact + reference offset, so the same
            // bounded correction remains valid in hip, ADS, move, run and jump.
            // Carry/reload phases are the only intentional exceptions below.
            float targetWeight = supportIk ? positionWeight : ikWindow ? positionWeight : 0f;
            if (supportReturn)
            {
                float returnWindow = Mathf.Max(reloadExitBlendSeconds, 0.0001f);
                float returnT = Mathf.Clamp01(_supportReturnElapsed / returnWindow);
                targetWeight = positionWeight * Mathf.SmoothStep(
                    reloadReturnStartWeight, 1f, returnT);
                _supportReturnElapsed += Time.deltaTime;
                if (_supportReturnElapsed >= returnWindow)
                    _supportReturnElapsed = -1f;
            }
            if (reloading && poseProfile != null && target != null)
            {
                targetWeight = !ikWindow
                    ? 0f
                    : reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.ReachMagazine
                        ? positionWeight * reloadPositionWeight * poseProfile.MagazineGrabWeight
                        : reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.AlignMagazine
                            || reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell
                            ? positionWeight * poseProfile.MagazineInsertWeight
                            : 0f;
            }
            float goal = hasTarget && target != null ? targetWeight : 0f;
            bool guidedMagazine = reloading && poseProfile != null
                && poseProfile.HasMagazineInsertGuide
                && (reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.AlignMagazine
                    || reloadPhase == FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell);
            float transitionSeconds = reloading
                ? (goal > _currentWeight ? reloadEnterBlendSeconds : reloadExitBlendSeconds)
                : (goal > _currentWeight ? blendSeconds : blendSeconds);
            _currentWeight = guidedMagazine
                ? goal
                : MoveWeightTowards(_currentWeight, goal, transitionSeconds);

            // Keep solving toward the last reload marker while the weight
            // fades out. Returning immediately when target becomes null would
            // drop the constraint in one frame even though the weight is
            // configured to blend out over reloadExitBlendSeconds.
            if (hasTarget && target != null && ikWindow)
            {
                // The contact-to-wrist target is smoothed in the weapon-local
                // frame. Phase changes therefore preserve the authored LPFP
                // trajectory while only applying a bounded incremental reach.
                if (guidedMagazine || (!reloading && !supportReturn))
                    SetTargetImmediate(target, resolvedTarget.Position, resolvedTarget.Rotation);
                else
                    SmoothTarget(target, resolvedTarget.Position, resolvedTarget.Rotation);
            }
            if (_currentWeight <= 0.0001f)
            {
                _activeTarget = null;
                _hasSmoothedTarget = false;
                _targetFrame = null;
                return;
            }
            if (!_hasSmoothedTarget) return;

            float rotation = adsSupport
                ? poseProfile != null && poseProfile.HasCompleteAnchoredDualPoseV2
                    ? Mathf.Clamp01(_currentWeight / Mathf.Max(targetWeight, 0.0001f))
                    : 0f
                : reloading
                    ? targetWeight <= 0.0001f
                        ? 0f
                        : (guidedMagazine ? 1f : reloadRotationWeight)
                            * Mathf.Clamp01(_currentWeight / targetWeight)
                : targetWeight <= 0.0001f
                    ? 0f
                    : rotationWeight * Mathf.Clamp01(_currentWeight / targetWeight);
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            Vector3? elbowPole = null;
            if ((adsSupport || (reloading && ikWindow && !guidedMagazine))
                && upperArm != null && _viewCamera != null)
            {
                elbowPole = poseProfile != null && poseProfile.HasCompleteAnchoredDualPoseV2
                    ? poseProfile.ResolveLeftElbowPolePosition(_viewCamera)
                    : upperArm.position - _viewCamera.right * .35f - _viewCamera.up * .80f
                        + _viewCamera.forward * .10f;
            }
            TwoBoneIKSolver.Solve(
                upperArm,
                lowerArm,
                hand,
                _smoothedTargetPosition,
                _smoothedTargetRotation,
                _currentWeight,
                rotation,
                elbowPole);
        }

        private Transform ResolveViewCamera()
        {
            PlayerAimState owner = aimState != null ? aimState : GetComponentInParent<PlayerAimState>();
            if (owner == null) return null;
            foreach (UnityEngine.Camera candidate in owner.GetComponentsInChildren<UnityEngine.Camera>(true))
                if (candidate != null && candidate.name == "FP View Camera") return candidate.transform;
            return null;
        }

        private bool IsIkWindow(bool reloading, float normalized)
        {
            if (!reloading) return !reloadOnly && (poseProfile != null || leftHandTarget != null);
            if (poseProfile == null) return false;
            FPWeaponPoseProfile.ReloadContactPhase phase = poseProfile.GetReloadContactPhase(normalized);
            return phase == FPWeaponPoseProfile.ReloadContactPhase.ReachMagazine
                || phase == FPWeaponPoseProfile.ReloadContactPhase.AlignMagazine
                || phase == FPWeaponPoseProfile.ReloadContactPhase.ReachMagazineWell;
        }

        private void ClearConstraintState()
        {
            _currentWeight = 0f;
            _activeTarget = null;
            _hasSmoothedTarget = false;
            _targetFrame = null;
            _supportReturnElapsed = -1f;
            _hasMagazineAlignStart = false;
        }

        private static float MoveWeightTowards(float current, float goal, float seconds)
        {
            if (seconds <= 0f) return goal;
            return Mathf.MoveTowards(current, goal, Time.deltaTime / seconds);
        }

        private void SmoothTarget(Transform target, Vector3 targetPosition, Quaternion targetRotation)
        {
            Transform frame = poseProfile != null && poseProfile.WeaponRoot != null
                ? poseProfile.WeaponRoot
                : target.parent;
            if (frame == null)
            {
                SetTargetImmediate(target, targetPosition, targetRotation);
                return;
            }

            Vector3 targetLocalPosition = frame.InverseTransformPoint(targetPosition);
            Quaternion targetLocalRotation = Quaternion.Inverse(frame.rotation) * targetRotation;
            if (!_hasSmoothedTarget)
            {
                _activeTarget = target;
                _targetFrame = frame;
                _smoothedTargetLocalPosition = targetLocalPosition;
                _smoothedTargetLocalRotation = targetLocalRotation;
                _targetLocalPositionVelocity = Vector3.zero;
                _hasSmoothedTarget = true;
            }
            else if (_activeTarget != target || _targetFrame != frame)
            {
                // Preserve the previous local pose as the start of the reload
                // phase transition; never teleport to a new marker at the
                // magazine-grip -> magazine-well boundary.
                _activeTarget = target;
                _targetFrame = frame;
                _targetLocalPositionVelocity = Vector3.zero;
            }

            if (targetBlendSeconds <= 0f)
            {
                _smoothedTargetLocalPosition = targetLocalPosition;
                _smoothedTargetLocalRotation = targetLocalRotation;
                _smoothedTargetPosition = frame.TransformPoint(_smoothedTargetLocalPosition);
                _smoothedTargetRotation = frame.rotation * _smoothedTargetLocalRotation;
                return;
            }

            _smoothedTargetLocalPosition = Vector3.SmoothDamp(
                _smoothedTargetLocalPosition,
                targetLocalPosition,
                ref _targetLocalPositionVelocity,
                targetBlendSeconds);
            float rotationAlpha = 1f - Mathf.Exp(-Time.deltaTime / targetBlendSeconds);
            _smoothedTargetLocalRotation = Quaternion.Slerp(
                _smoothedTargetLocalRotation,
                targetLocalRotation,
                rotationAlpha);
            _smoothedTargetPosition = frame.TransformPoint(_smoothedTargetLocalPosition);
            _smoothedTargetRotation = frame.rotation * _smoothedTargetLocalRotation;
        }

        private void SetTargetImmediate(Transform target, Vector3 targetPosition, Quaternion targetRotation)
        {
            _activeTarget = target;
            _smoothedTargetPosition = targetPosition;
            _smoothedTargetRotation = targetRotation;
            _targetFrame = poseProfile != null && poseProfile.WeaponRoot != null
                ? poseProfile.WeaponRoot
                : target.parent;
            _smoothedTargetLocalPosition = _targetFrame != null
                ? _targetFrame.InverseTransformPoint(targetPosition)
                : targetPosition;
            _smoothedTargetLocalRotation = _targetFrame != null
                ? Quaternion.Inverse(_targetFrame.rotation) * targetRotation
                : targetRotation;
            _targetLocalPositionVelocity = Vector3.zero;
            _hasSmoothedTarget = true;
        }

        private float GetReloadProgress()
        {
            if (_controller?.Runtime == null || _controller.Stat.ReloadTime <= 0f)
                return 0f;
            return 1f - Mathf.Clamp01(
                _controller.Runtime.ReloadRemaining / _controller.Stat.ReloadTime);
        }

        private void HandleReloadStarted()
        {
            _reloadActive = true;
            _supportReturnElapsed = -1f;
            if (poseProfile != null && _controller?.Runtime != null)
                poseProfile.BeginReload(_controller.Runtime.CurrentAmmo == 0);
        }

        private void HandleReloadEnded()
        {
            _reloadActive = false;
            poseProfile?.EndReload();
            BeginSupportReturn();
        }

        private void HandleReloadInterrupted(ActionInterruptReason _)
        {
            _reloadActive = false;
            poseProfile?.EndReload();
            BeginSupportReturn();
        }

        private void BeginSupportReturn()
        {
            if (reloadOnly || poseProfile == null || poseProfile.LeftSupportGrip == null)
                return;
            _supportReturnElapsed = 0f;
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
