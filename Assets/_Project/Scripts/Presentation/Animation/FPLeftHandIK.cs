using Game.Gameplay.Action;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Data-driven first-person support-hand correction. Animation remains the
    /// source pose during ordinary handling; a reload-only profile can take
    /// over only while the animated magazine is being removed/carried/inserted.
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
        // Keep the component's historical full-time behaviour as the safe
        // code default; the two currently profiled LPW prefabs opt into the
        // reload-only mode explicitly in their prefab data.
        [SerializeField] private bool reloadOnly;
        [SerializeField, Range(0f, 1f)] private float reloadIkStartNormalized = 0.1f;
        [SerializeField, Range(0f, 1f)] private float reloadIkEndNormalized = 0.96f;
        [SerializeField, Min(0f)] private float reloadEnterBlendSeconds = 0.12f;
        [SerializeField, Min(0f)] private float reloadExitBlendSeconds = 0.12f;
        [SerializeField, Min(0f)] private float targetBlendSeconds = 0.08f;

        private WeaponController _controller;
        private float _currentWeight;
        private bool _reloadActive;
        private Transform _activeTarget;
        private Vector3 _smoothedTargetPosition;
        private Quaternion _smoothedTargetRotation = Quaternion.identity;
        private Vector3 _targetPositionVelocity;
        private bool _hasSmoothedTarget;
        private Transform _viewCamera;

        public Transform LeftHandTarget => leftHandTarget;
        public bool ReloadOnly => reloadOnly;
        public float ReloadIkStartNormalized => reloadIkStartNormalized;
        public float ReloadIkEndNormalized => reloadIkEndNormalized;

        private void Awake()
        {
            _controller = GetComponentInParent<WeaponController>();
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
            if (_controller == null) return;
            _controller.OnReloadStarted += HandleReloadStarted;
            _controller.OnReloadCompleted += HandleReloadEnded;
            _controller.OnReloadInterrupted += HandleReloadInterrupted;
            _reloadActive = _controller.Runtime != null
                && _controller.Runtime.State == WeaponRuntimeState.Reloading;
            _currentWeight = 0f;
            _activeTarget = null;
            _hasSmoothedTarget = false;
        }

        private void OnDisable()
        {
            if (_controller != null)
            {
                _controller.OnReloadStarted -= HandleReloadStarted;
                _controller.OnReloadCompleted -= HandleReloadEnded;
                _controller.OnReloadInterrupted -= HandleReloadInterrupted;
            }
            _currentWeight = 0f;
            _reloadActive = false;
            _activeTarget = null;
            _hasSmoothedTarget = false;
        }

        private void LateUpdate()
        {
            bool reloading = _reloadActive || (_controller?.Runtime?.State == WeaponRuntimeState.Reloading);
            float normalized = GetReloadProgress();
            float ads = aimState != null ? aimState.Ads01 : 0f;
            bool adsSupport = !reloading && ads > .0001f
                && poseProfile != null && poseProfile.LeftSupportGrip != null;
            bool ikWindow = adsSupport || IsIkWindow(reloading, normalized);
            Transform target = adsSupport
                ? poseProfile.LeftSupportGrip
                : ikWindow
                ? poseProfile != null
                    ? poseProfile.GetLeftHandTarget(reloading, normalized)
                    : leftHandTarget
                : null;

            // A profiled weapon has three explicit phase targets. The target
            // changes at the authored phase boundary, but the world-space
            // marker itself is smoothed so the hand never snaps from grip to
            // magazine well.
            float targetWeight = adsSupport ? positionWeight * ads : ikWindow ? positionWeight : 0f;
            if (reloading && poseProfile != null && target != null)
            {
                var phase = poseProfile.GetReloadHandPhase(normalized);
                targetWeight = phase == FPWeaponPoseProfile.ReloadHandPhase.MagazineGrab
                    ? positionWeight * poseProfile.MagazineGrabWeight
                    : phase == FPWeaponPoseProfile.ReloadHandPhase.MagazineInsert
                        ? positionWeight * poseProfile.MagazineInsertWeight
                        : positionWeight;
            }
            float goal = target != null ? targetWeight : 0f;
            float transitionSeconds = goal > _currentWeight
                ? (reloadOnly ? reloadEnterBlendSeconds : blendSeconds)
                : (reloadOnly ? reloadExitBlendSeconds : blendSeconds);
            _currentWeight = MoveWeightTowards(_currentWeight, goal, transitionSeconds);

            // Keep solving toward the last reload marker while the weight
            // fades out. Returning immediately when target becomes null would
            // drop the constraint in one frame even though the weight is
            // configured to blend out over reloadExitBlendSeconds.
            if (target != null)
            {
                // ADS target and weapon share the same moving viewmodel frame. World-space
                // damping here makes the hand chase yesterday's gun position whenever the
                // camera moves, which reads as hand sliding/jitter. Reload phase changes
                // still use smoothing because those targets intentionally jump.
                if (adsSupport)
                    SetTargetImmediate(target);
                else
                    SmoothTarget(target);
            }
            if (_currentWeight <= 0.0001f)
            {
                if (target == null)
                {
                    _activeTarget = null;
                    _hasSmoothedTarget = false;
                }
                return;
            }
            if (!_hasSmoothedTarget) return;

            float rotation = adsSupport || targetWeight <= 0.0001f
                ? 0f
                : rotationWeight * Mathf.Clamp01(_currentWeight / targetWeight);
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            Vector3? elbowPole = adsSupport && upperArm != null && _viewCamera != null
                ? upperArm.position - _viewCamera.right * .35f - _viewCamera.up * .80f
                    + _viewCamera.forward * .10f
                : null;
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
            if (!reloadOnly) return reloading || poseProfile != null || leftHandTarget != null;
            if (!reloading) return false;

            float start = Mathf.Clamp01(reloadIkStartNormalized);
            float end = Mathf.Max(start, Mathf.Clamp01(reloadIkEndNormalized));
            // The first constrained frame is the authored magazine-out phase,
            // not ordinary support-hand carry. BeginReload supplies the empty
            // magazine thresholds when necessary.
            if (poseProfile != null)
                start = Mathf.Max(start, poseProfile.MagazineOutNormalized);
            return normalized >= start && normalized <= end;
        }

        private static float MoveWeightTowards(float current, float goal, float seconds)
        {
            if (seconds <= 0f) return goal;
            return Mathf.MoveTowards(current, goal, Time.deltaTime / seconds);
        }

        private void SmoothTarget(Transform target)
        {
            if (!_hasSmoothedTarget)
            {
                _activeTarget = target;
                _smoothedTargetPosition = target.position;
                _smoothedTargetRotation = target.rotation;
                _targetPositionVelocity = Vector3.zero;
                _hasSmoothedTarget = true;
            }
            else if (_activeTarget != target)
            {
                // Preserve the previous marker as the start of the transition;
                // initializing to target.position here would teleport the IK
                // target at the magazine-grip → magazine-well boundary.
                _activeTarget = target;
                _targetPositionVelocity = Vector3.zero;
            }

            if (targetBlendSeconds <= 0f)
            {
                _smoothedTargetPosition = target.position;
                _smoothedTargetRotation = target.rotation;
                return;
            }

            _smoothedTargetPosition = Vector3.SmoothDamp(
                _smoothedTargetPosition,
                target.position,
                ref _targetPositionVelocity,
                targetBlendSeconds);
            float rotationAlpha = 1f - Mathf.Exp(-Time.deltaTime / targetBlendSeconds);
            _smoothedTargetRotation = Quaternion.Slerp(
                _smoothedTargetRotation,
                target.rotation,
                rotationAlpha);
        }

        private void SetTargetImmediate(Transform target)
        {
            _activeTarget = target;
            _smoothedTargetPosition = target.position;
            _smoothedTargetRotation = target.rotation;
            _targetPositionVelocity = Vector3.zero;
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
            if (poseProfile != null && _controller?.Runtime != null)
                poseProfile.BeginReload(_controller.Runtime.CurrentAmmo == 0);
        }

        private void HandleReloadEnded()
        {
            _reloadActive = false;
            poseProfile?.EndReload();
        }

        private void HandleReloadInterrupted(ActionInterruptReason _)
        {
            _reloadActive = false;
            poseProfile?.EndReload();
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
