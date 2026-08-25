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
        [SerializeField, Range(0f, 1f)] private float weight = 1f;
        [SerializeField, Min(0f)] private float blendSeconds = 0.15f;

        private Animator _animator;
        private Transform _upperArm;
        private Transform _lowerArm;
        private Transform _hand;
        private float _currentWeight;

        private void Awake()
        {
            _animator = GetComponentInChildren<Animator>(true);
            if (swapper == null) swapper = GetComponentInParent<TPWeaponMeshSwapper>() ?? GetComponent<TPWeaponMeshSwapper>();
            if (_animator != null)
            {
                _upperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _lowerArm = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _hand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
            }
        }

        private void LateUpdate()
        {
            var target = swapper != null ? swapper.CurrentLeftHandTarget : null;
            float goal = target != null ? weight : 0f;
            _currentWeight = blendSeconds <= 0f
                ? goal
                : Mathf.MoveTowards(_currentWeight, goal, Time.deltaTime / blendSeconds);
            if (_currentWeight <= 0.001f || _upperArm == null || _lowerArm == null || _hand == null) return;

            Vector3 goalPos = Vector3.Lerp(_hand.position, target.position, _currentWeight);
            Quaternion goalRot = Quaternion.Slerp(_hand.rotation, target.rotation, _currentWeight);

            // Step1：整臂指向目标（upperArm 世界旋转补 shoulder→wrist 与 shoulder→goal 的差）
            Vector3 shoulderWrist = _hand.position - _upperArm.position;
            Vector3 shoulderGoal = goalPos - _upperArm.position;
            if (shoulderWrist.sqrMagnitude > 0.000001f && shoulderGoal.sqrMagnitude > 0.000001f)
                ApplyWorldDelta(_upperArm, Quaternion.FromToRotation(shoulderWrist.normalized, shoulderGoal.normalized));

            // Step2：小臂精确把腕送到目标（lowerArm 世界旋转补 elbow→wrist 与 elbow→goal 的差）
            Vector3 elbowWrist = _hand.position - _lowerArm.position;
            Vector3 elbowGoal = goalPos - _lowerArm.position;
            if (elbowWrist.sqrMagnitude > 0.000001f && elbowGoal.sqrMagnitude > 0.000001f)
                ApplyWorldDelta(_lowerArm, Quaternion.FromToRotation(elbowWrist.normalized, elbowGoal.normalized));

            // Step3：手部旋转对齐握把姿态
            Quaternion parentWorld = _hand.parent != null ? _hand.parent.rotation : Quaternion.identity;
            Quaternion worldRot = parentWorld * _hand.localRotation;
            _hand.localRotation = Quaternion.Inverse(parentWorld)
                * Quaternion.Slerp(worldRot, goalRot, _currentWeight);
        }

        private static void ApplyWorldDelta(Transform bone, Quaternion delta)
        {
            Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            Quaternion worldRot = parentWorld * bone.localRotation;
            bone.localRotation = Quaternion.Inverse(parentWorld) * (delta * worldRot);
        }
    }
}
