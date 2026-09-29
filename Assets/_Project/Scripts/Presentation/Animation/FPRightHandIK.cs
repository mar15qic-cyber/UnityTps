using Game.Gameplay.Action;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Optional diagnostic/fallback solver for exceptional weapons. The normal LPW
    /// path keeps the trigger hand fully animation-authored and aligns the replacement
    /// mesh to that hand instead of solving the arm at runtime.
    /// </summary>
    [DefaultExecutionOrder(45)]
    [DisallowMultipleComponent]
    public sealed class FPRightHandIK : MonoBehaviour
    {
        [SerializeField] private FPWeaponPoseProfile poseProfile;
        [SerializeField] private PlayerAimState aimState;
        [SerializeField] private Transform upperArm;
        [SerializeField] private Transform lowerArm;
        [SerializeField] private Transform hand;
        [SerializeField, Range(0f, 1f)] private float positionWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float rotationWeight = .45f;
        [SerializeField, Min(0f)] private float blendSeconds = .1f;
        [Tooltip("Leave disabled for LPW weapons. The LPFP animation owns the trigger hand.")]
        [SerializeField] private bool solveRightHand;

        private ActionSystem _actionSystem;
        private float _weight;
        private Transform _viewCamera;

        private void Awake()
        {
            poseProfile ??= GetComponent<FPWeaponPoseProfile>();
            _actionSystem = GetComponentInParent<ActionSystem>();
            aimState ??= GetComponentInParent<PlayerAimState>();
            upperArm ??= FindDeep(transform, "arm_R");
            lowerArm ??= FindDeep(transform, "lower_arm_R");
            hand ??= FindDeep(transform, "hand_R");
            _viewCamera = ResolveViewCamera();
        }

        private void OnEnable()
        {
            if (_actionSystem == null) _actionSystem = GetComponentInParent<ActionSystem>();
            _weight = 0f;
        }

        private void LateUpdate()
        {
            var animator = GetComponent<FPWeaponAnimator>();
            if (animator != null && animator.IsThrowablePresentationActive)
            { _weight = 0f; return; }
            if (!solveRightHand)
            {
                _weight = 0f;
                return;
            }

            // The authored holster/draw clips own both arms during a weapon
            // switch. A fallback right-hand solver must not re-introduce the
            // same elbow flip that the optional LPW path is avoiding.
            if (_actionSystem != null
                && _actionSystem.CurrentAction == PlayerActionType.SwitchWeapon)
            {
                _weight = 0f;
                return;
            }

            Transform target = poseProfile != null ? poseProfile.RightHandGrip : null;
            float ads = aimState != null ? aimState.Ads01 : 0f;
            float goal = target != null ? positionWeight * ads : 0f;
            _weight = blendSeconds <= 0f
                ? goal
                : Mathf.MoveTowards(_weight, goal, Time.deltaTime / blendSeconds);
            if (_weight <= .0001f || target == null) return;

            // AnchoredDualPoseV2 solves the gun from the animated hand first.
            // If a reference family has a small residual wrist twist, keep that
            // correction explicitly bounded by rotationWeight instead of
            // snapping the whole authored arm pose onto the grip.
            float rotation = poseProfile != null && poseProfile.HasCompleteAnchoredDualPoseV2
                ? rotationWeight * Mathf.Clamp01(_weight / Mathf.Max(goal, 0.0001f))
                : positionWeight <= .0001f
                    ? 0f
                    : rotationWeight * Mathf.Clamp01(_weight / positionWeight);
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            Vector3? elbowPole = null;
            if (upperArm != null && _viewCamera != null)
            {
                elbowPole = poseProfile != null && poseProfile.HasCompleteAnchoredDualPoseV2
                    ? poseProfile.ResolveRightElbowPolePosition(_viewCamera)
                    : upperArm.position + _viewCamera.right * .35f - _viewCamera.up * .80f
                        + _viewCamera.forward * .10f;
            }
            TwoBoneIKSolver.Solve(
                upperArm,
                lowerArm,
                hand,
                target.position,
                target.rotation,
                _weight,
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
