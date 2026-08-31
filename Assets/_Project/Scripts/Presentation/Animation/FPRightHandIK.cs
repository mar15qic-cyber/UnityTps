using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Keeps the trigger hand attached to the active weapon while the runtime ADS pivot
    /// performs weapon-only sight alignment. Hip fire remains fully animation-authored.
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

        private float _weight;
        private Transform _viewCamera;

        private void Awake()
        {
            poseProfile ??= GetComponent<FPWeaponPoseProfile>();
            aimState ??= GetComponentInParent<PlayerAimState>();
            upperArm ??= FindDeep(transform, "arm_R");
            lowerArm ??= FindDeep(transform, "lower_arm_R");
            hand ??= FindDeep(transform, "hand_R");
            _viewCamera = ResolveViewCamera();
        }

        private void OnEnable() => _weight = 0f;

        private void LateUpdate()
        {
            Transform target = poseProfile != null ? poseProfile.RightHandGrip : null;
            float ads = aimState != null ? aimState.Ads01 : 0f;
            float goal = target != null ? positionWeight * ads : 0f;
            _weight = blendSeconds <= 0f
                ? goal
                : Mathf.MoveTowards(_weight, goal, Time.deltaTime / blendSeconds);
            if (_weight <= .0001f || target == null) return;

            float rotation = positionWeight <= .0001f
                ? 0f
                : rotationWeight * Mathf.Clamp01(_weight / positionWeight);
            if (_viewCamera == null) _viewCamera = ResolveViewCamera();
            Vector3? elbowPole = upperArm != null && _viewCamera != null
                ? upperArm.position + _viewCamera.right * .35f - _viewCamera.up * .80f
                    + _viewCamera.forward * .10f
                : null;
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
