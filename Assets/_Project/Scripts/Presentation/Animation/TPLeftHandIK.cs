using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Day4.2 TP 左手持枪 IK（程序化 TwoBoneIK 预览版）：
    /// 把 upper_arm_L→lower_arm_L→hand_L 解算到当前武器 LeftHandTarget 挂点，
    /// 解决「动画左手扶弹匣，而枪型带垂直握把/护木」的贴合问题。
    ///
    /// 数据驱动：LeftHandTarget 烘焙在每把 TP_Weapon prefab 上（grip 上端/护木/弹匣），
    /// 未来枪械改装（加装/拆除握把）只需挪挂点，零代码。肘部弯向保持动画原姿态（不设 pole），
    /// 视觉自然；换枪时权重平滑过渡。
    ///
    /// 单写者：只在动画求值后写三根左臂骨骼的 localRotation/localRotation（叠加式）。
    /// 必须晚于 TPAimDriver（aim 移动肩位后再解算手臂）。
    /// </summary>
    [DefaultExecutionOrder(35)]
    public sealed class TPLeftHandIK : MonoBehaviour
    {
        [SerializeField] private TPWeaponMeshSwapper swapper;
        [SerializeField] private WeaponController controller;
        [SerializeField, Range(0f, 1f)] private float weight = 1f;
        [SerializeField, Range(0f, 1f)] private float rotationWeight = 1f;
        [SerializeField, Min(0f)] private float blendSeconds = 0.15f;

        private Animator _animator;
        private Transform _upperArm;
        private Transform _lowerArm;
        private Transform _hand;
        private float _currentWeight;
        private bool _reloadSuppressed;

        private void Awake()
        {
            _animator = GetComponentInChildren<Animator>(true);
            if (swapper == null) swapper = GetComponentInParent<TPWeaponMeshSwapper>() ?? GetComponent<TPWeaponMeshSwapper>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (_animator != null)
            {
                _upperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _lowerArm = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _hand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            }
        }

        private void OnEnable()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (controller == null) return;
            controller.OnReloadStarted += HandleReloadStarted;
            controller.OnReloadCompleted += HandleReloadEnded;
            controller.OnReloadInterrupted += HandleReloadInterrupted;
            _reloadSuppressed = controller.Runtime != null
                && controller.Runtime.State == WeaponRuntimeState.Reloading;
        }

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnReloadStarted -= HandleReloadStarted;
            controller.OnReloadCompleted -= HandleReloadEnded;
            controller.OnReloadInterrupted -= HandleReloadInterrupted;
        }

        private void LateUpdate()
        {
            var target = swapper != null ? swapper.CurrentLeftHandTarget : null;
            float goal = target != null && !_reloadSuppressed ? weight : 0f;
            _currentWeight = blendSeconds <= 0f
                ? goal
                : Mathf.MoveTowards(_currentWeight, goal, Time.deltaTime / blendSeconds);
            if (_currentWeight <= 0.001f || _upperArm == null || _lowerArm == null || _hand == null) return;

            float rotation = weight <= 0.0001f
                ? 0f
                : rotationWeight * (_currentWeight / weight);
            TwoBoneIKSolver.Solve(
                _upperArm,
                _lowerArm,
                _hand,
                target.position,
                target.rotation,
                _currentWeight,
                rotation);
        }

        private void HandleReloadStarted() => _reloadSuppressed = true;
        private void HandleReloadEnded() => _reloadSuppressed = false;
        private void HandleReloadInterrupted(Game.Gameplay.Action.ActionInterruptReason _) => _reloadSuppressed = false;
    }
}
