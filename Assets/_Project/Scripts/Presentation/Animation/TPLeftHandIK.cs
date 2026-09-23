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
    /// 单写者：只在动画求值后写左肩与三根左臂骨骼的 rotation（叠加式）。
    /// 帧驱动由 TPWeaponMeshSwapper 统一调用，使“武器实例/握点存在”和“IK 写入”处于
    /// 同一个生命周期；不能再依赖一个可能与网络生成顺序脱节的独立 LateUpdate。
    /// </summary>
    public sealed class TPLeftHandIK : MonoBehaviour
    {
        [SerializeField] private TPWeaponMeshSwapper swapper;
        [SerializeField] private WeaponController controller;
        [SerializeField, Range(0f, 1f)] private float weight = 1f;
        [SerializeField, Range(0f, 1f)] private float rotationWeight = 1f;
        [SerializeField, Min(0f)] private float blendSeconds = 0.15f;
        [SerializeField, Range(0f, 45f), Tooltip("仅当握点超出两段手臂长度时，允许锁骨向目标辅助的最大角度")]
        private float shoulderReachAssistDegrees = 30f;

        private Animator _animator;
        private Transform _shoulder;
        private Transform _upperArm;
        private Transform _lowerArm;
        private Transform _hand;
        private float _currentWeight;
        private bool _reloadSuppressed;
        /// <summary>死亡闸门来源（审计 2026-09-16 D2：IK 必须在死亡期间停止写骨骼）。</summary>
        private Game.Gameplay.Network.NetworkCombatAuthority _netAuthority;

        private void Awake()
        {
            ResolveReferences();
        }

        internal void Bind(TPWeaponMeshSwapper owner)
        {
            swapper = owner;
            ResolveReferences();
        }

        private void ResolveReferences()
        {
            _animator = GetComponentInChildren<Animator>(true);
            if (swapper == null) swapper = GetComponentInParent<TPWeaponMeshSwapper>() ?? GetComponent<TPWeaponMeshSwapper>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (_animator != null)
            {
                _shoulder = _animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
                _upperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _lowerArm = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _hand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            }
        }

        private void OnEnable()
        {
            ResolveReferences();
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

        /// <summary>
        /// 由 <see cref="TPWeaponMeshSwapper"/> 在 TP 动画和瞄准写入之后调用。
        /// 不保留 Unity 自动 LateUpdate，避免同帧双重解算。
        /// </summary>
        public void ApplyPreviewPose(Transform target, float poseWeight = 1f, Quaternion? wristRotation = null)
        {
            if (_animator == null) ResolveReferences();
            if (target == null || _upperArm == null || _lowerArm == null || _hand == null) return;
            var solveWeight = Mathf.Clamp01(poseWeight) * weight;
            if (solveWeight <= 0.0001f) return;
            // The lobby supplies a continuous animation-contact weight. Do not carry combat
            // blend state across the authored release gesture or pull the hand back to the gun.
            ApplyShoulderReachAssist(_shoulder, _upperArm, _lowerArm, _hand,
                target.position, solveWeight, shoulderReachAssistDegrees);
            TwoBoneIKSolver.Solve(_upperArm, _lowerArm, _hand, target.position,
                wristRotation ?? target.rotation, solveWeight, rotationWeight * solveWeight);
        }

        internal void ApplyPoseFrame()
        {
            // 2026-09-16 审计 D2 双保险：死亡期间 IK 不得继续解算已冻结的手臂（旧实现不在死亡停用名单里，
            // 会在 Animator 冻结后持续改写三根左臂骨骼），并清空混合权重以便复活后从 0 平滑接入。
            if (_netAuthority == null) _netAuthority = GetComponentInParent<Game.Gameplay.Network.NetworkCombatAuthority>();
            if (_netAuthority != null && _netAuthority.IsDead)
            {
                _currentWeight = 0f;
                return;
            }
            var target = swapper != null ? swapper.CurrentLeftHandTarget : null;
            // Selecting a grenade removes the gun and its grip immediately. A nonzero
            // blend-out weight cannot be solved against that destroyed transform.
            if (target == null) { _currentWeight = 0f; return; }
            float goal = target != null && !_reloadSuppressed ? weight : 0f;
            _currentWeight = !Application.isPlaying || blendSeconds <= 0f
                ? goal
                : Mathf.MoveTowards(_currentWeight, goal, Time.deltaTime / blendSeconds);
            if (_currentWeight <= 0.001f || _upperArm == null || _lowerArm == null || _hand == null) return;

            float rotation = weight <= 0.0001f
                ? 0f
                : rotationWeight * (_currentWeight / weight);
            ApplyShoulderReachAssist(
                _shoulder,
                _upperArm,
                _lowerArm,
                _hand,
                target.position,
                _currentWeight,
                shoulderReachAssistDegrees);
            TwoBoneIKSolver.Solve(
                _upperArm,
                _lowerArm,
                _hand,
                target.position,
                target.rotation,
                _currentWeight,
                rotation);
        }

        private static void ApplyShoulderReachAssist(
            Transform shoulder,
            Transform upperArm,
            Transform lowerArm,
            Transform hand,
            Vector3 targetPosition,
            float solveWeight,
            float maxDegrees)
        {
            if (shoulder == null || upperArm == null || lowerArm == null || hand == null
                || solveWeight <= 0.0001f || maxDegrees <= 0.0001f)
                return;

            float armReach = Vector3.Distance(upperArm.position, lowerArm.position)
                + Vector3.Distance(lowerArm.position, hand.position);
            var delta = ComputeShoulderReachAssistDelta(
                shoulder.position,
                upperArm.position,
                targetPosition,
                armReach,
                maxDegrees,
                solveWeight);
            shoulder.rotation = delta * shoulder.rotation;
        }

        /// <summary>
        /// Returns the smallest bounded clavicle rotation (along the direct-to-target arc)
        /// that makes a currently unreachable wrist target reachable by the two arm bones.
        /// Reachable handguard targets return identity, so their authored shoulder animation
        /// remains untouched. Pure math entry point kept public for deterministic EditMode tests.
        /// </summary>
        public static Quaternion ComputeShoulderReachAssistDelta(
            Vector3 clavicleOrigin,
            Vector3 upperArmOrigin,
            Vector3 targetPosition,
            float armReach,
            float maxDegrees,
            float weight)
        {
            weight = Mathf.Clamp01(weight);
            maxDegrees = Mathf.Max(0f, maxDegrees);
            var clavicle = upperArmOrigin - clavicleOrigin;
            if (weight <= 0.0001f || maxDegrees <= 0.0001f || armReach <= 0.0001f
                || clavicle.sqrMagnitude <= 0.000001f
                || Vector3.Distance(upperArmOrigin, targetPosition) <= armReach - 0.0001f)
                return Quaternion.identity;

            var toTarget = targetPosition - clavicleOrigin;
            if (toTarget.sqrMagnitude <= 0.000001f) return Quaternion.identity;
            var fullDelta = Quaternion.FromToRotation(clavicle, toTarget);
            float fullAngle = Quaternion.Angle(Quaternion.identity, fullDelta);
            if (fullAngle <= 0.0001f) return Quaternion.identity;

            float high = Mathf.Min(maxDegrees, fullAngle);
            float low = 0f;
            // Monotonic on this bounded direct-to-target arc. Eight iterations resolve
            // the assist angle below 0.2 degrees even at the 45-degree authoring limit.
            for (var i = 0; i < 8; i++)
            {
                float mid = (low + high) * 0.5f;
                var probeDelta = Quaternion.Slerp(Quaternion.identity, fullDelta, mid / fullAngle);
                var probeUpperArm = clavicleOrigin + probeDelta * clavicle;
                if (Vector3.Distance(probeUpperArm, targetPosition) <= armReach - 0.0001f)
                    high = mid;
                else
                    low = mid;
            }

            var bounded = Quaternion.Slerp(Quaternion.identity, fullDelta, high / fullAngle);
            return Quaternion.Slerp(Quaternion.identity, bounded, weight);
        }

        private void HandleReloadStarted() => _reloadSuppressed = true;
        private void HandleReloadEnded() => _reloadSuppressed = false;
        private void HandleReloadInterrupted(Game.Gameplay.Action.ActionInterruptReason _) => _reloadSuppressed = false;
    }
}
