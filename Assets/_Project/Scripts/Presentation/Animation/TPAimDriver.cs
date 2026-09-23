using Game.Gameplay.Network;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Day4.2 TP 瞄准解算预览版（正式 IK 前的过渡实现）：
    /// 把第一人称相机俯仰（及可选 yaw 残差）按权重分配到 spine→chest→neck→head，
    /// 在 Animator 求值后的姿态上做世界空间叠加，实现「TP 模型随准心抬头低头」。
    ///
    /// 为什么不用 FinalIK AimIK：FinalIK 无 asmdef（编入 Assembly-CSharp），
    /// Game.Presentation 程序集边界引用不到；Day7 联网版再决定 FinalIK 的程序集
    /// 集成方式（或沿用本实现——它无依赖、可控、与 Animancer 无冲突）。
    ///
    /// 单写者：只写 spine/chest/neck/head 四根骨骼的 localRotation（叠加，不改动画状态）。
    /// 未来远端玩家：aim 源换网络同步的瞄准方向即可。
    /// </summary>
    [DefaultExecutionOrder(30)]
    public sealed class TPAimDriver : MonoBehaviour
    {
        [Header("瞄准源")]
        [SerializeField, Min(1f)] private float aimDistance = 20f;

        [Header("Pitch 分配（上身各骨占比，总和≈1）")]
        [SerializeField, Range(0f, 1f)] private float spineWeight = 0.30f;
        [SerializeField, Range(0f, 1f)] private float chestWeight = 0.40f;
        [SerializeField, Range(0f, 1f)] private float neckWeight = 0.15f;
        [SerializeField, Range(0f, 1f)] private float headWeight = 0.15f;
        [SerializeField, Range(0f, 90f)] private float maxPitchDegrees = 60f;
        [SerializeField, Min(0f)] private float pitchSmoothSeconds = 0.08f;

        [Header("Yaw 残差（本地玩家为 0：Locomotor 已直接驱动根 Yaw；预留给远端表现）")]
        [SerializeField, Range(0f, 1f)] private float yawWeight = 0f;
        [SerializeField, Range(0f, 90f)] private float maxYawDegrees = 45f;

        private Animator _animator;
        private Transform _spine;
        private Transform _chest;
        private Transform _neck;
        private Transform _head;
        private UnityEngine.Camera _camera;
        private NetworkCombatAuthority _netAuthority;
        private PlayerNetworkAdapter _networkAdapter;
        private Game.Gameplay.Movement.Locomotor _locomotor;
        private float _pitch;
        private float _pitchVelocity;
        private readonly Quaternion[] _baseRotations = new Quaternion[4];
        private readonly Quaternion[] _appliedRotations = new Quaternion[4];
        private readonly bool[] _poseApplied = new bool[4];

        private void Awake()
        {
            _networkAdapter = GetComponentInParent<PlayerNetworkAdapter>();
            _locomotor = GetComponentInParent<Game.Gameplay.Movement.Locomotor>();
            _animator = GetComponentInChildren<Animator>(true);
            if (_animator != null)
            {
                _spine = _animator.GetBoneTransform(HumanBodyBones.Spine);
                _chest = _animator.GetBoneTransform(HumanBodyBones.Chest);
                _neck = _animator.GetBoneTransform(HumanBodyBones.Neck);
                _head = _animator.GetBoneTransform(HumanBodyBones.Head);
            }
        }

        private void LateUpdate()
        {
            if (_animator == null || _spine == null) return;

            // 2026-09-16 审计 D2 双保险：死亡期间**不得**再向骨骼叠加（死亡生命周期已统一停用本组件，
            // 这里是防漏闸——旧实现漏停 TPAim/IK 时，Animator 冻结后同一 pitch 会逐帧叠加到同一骨骼上
            // 造成持续扭曲，并让"死亡入口求一次的贴地包围盒"失效）。
            if (_netAuthority == null) _netAuthority = GetComponentInParent<NetworkCombatAuthority>();
            if (_netAuthority != null && _netAuthority.IsDead)
            {
                ResetAccumulatedPose(); // 复活从已知姿态重新收敛，不携带死亡前的俯仰累计
                return;
            }

            // 远端玩家（Docs/23 P0-5 G2b）：瞄准源 = NetworkCombatAuthority.AimDirectionWorld
            // （服务器同步俯仰后的 aimPivot 前向）；本地/离线玩家路径保持现状（相机中心射线）。
            Vector3 aimDir;
            if (_netAuthority != null && !_netAuthority.IsOwnerPlayer)
            {
                aimDir = _netAuthority.AimDirectionWorld;
                if (aimDir.sqrMagnitude < 0.0001f) return;
            }
            else
            {
                if (_camera == null) _camera = UnityEngine.Camera.main;
                if (_camera == null) return;
                // 目标瞄准方向（相机中心射线）
                aimDir = (_camera.transform.position + _camera.transform.forward * aimDistance
                    - (_chest != null ? _chest.position : transform.position)).normalized;
            }

            // pitch：瞄准方向相对水平面的仰角（本地模型 yaw 已与相机一致，只需 pitch）
            float targetPitch = Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) * Mathf.Rad2Deg;
            targetPitch = Mathf.Clamp(targetPitch, -maxPitchDegrees, maxPitchDegrees);
            _pitch = pitchSmoothSeconds <= 0f
                ? targetPitch
                : Mathf.SmoothDamp(_pitch, targetPitch, ref _pitchVelocity, pitchSmoothSeconds, Mathf.Infinity, Time.deltaTime);

            // yaw 残差（默认权重 0）
            Vector3 flatAim = new Vector3(aimDir.x, 0f, aimDir.z).normalized;
            Vector3 flatFwd = new Vector3(transform.root.forward.x, 0f, transform.root.forward.z).normalized;
            float yawDelta = 0f;
            if (yawWeight > 0f && flatAim.sqrMagnitude > 0.01f && flatFwd.sqrMagnitude > 0.01f)
            {
                float sign = Vector3.Cross(flatFwd, flatAim).y >= 0f ? 1f : -1f;
                yawDelta = Mathf.Clamp(
                    Vector3.Angle(flatFwd, flatAim) * sign * yawWeight,
                    -maxYawDegrees, maxYawDegrees);
            }

            // 世界空间叠加轴（模型右轴/上轴）
            Vector3 right = transform.root.right;
            Vector3 up = transform.root.up;
            float lean = _networkAdapter != null ? _networkAdapter.VisualLeanAmount
                : _locomotor != null ? _locomotor.Lean.Amount : 0f;
            float roll = lean * Game.Gameplay.Player.LeanProfile.MaxBodyRollDegrees;
            Vector3 forward = transform.root.forward;
            ApplyAim(_spine, 0, spineWeight, right, up, forward, _pitch, yawDelta, roll * .6f);
            if (_chest != null) ApplyAim(_chest, 1, chestWeight, right, up, forward, _pitch, yawDelta, roll * .4f);
            if (_neck != null) ApplyAim(_neck, 2, neckWeight, right, up, forward, _pitch, yawDelta, -lean * 4f);
            if (_head != null) ApplyAim(_head, 3, headWeight, right, up, forward, _pitch, yawDelta, -lean * 4f);
        }

        /// <summary>清空俯仰平滑累计（死亡/复活边界，审计 D2）：复活后从当前瞄准重新收敛，
        /// 不把死亡前的平滑速度/角度带进新姿态。</summary>
        public void ResetAccumulatedPose()
        {
            _pitch = 0f;
            _pitchVelocity = 0f;
            for (int i = 0; i < _poseApplied.Length; i++) _poseApplied[i] = false;
        }

        /// <summary>在动画姿态之上叠加份额旋转（世界空间 delta → 该骨骼局部）。</summary>
        private void ApplyAim(Transform bone, int index, float share, Vector3 right, Vector3 up, Vector3 forward,
            float pitch, float yaw, float roll)
        {
            // When Animancer has not evaluated a new pose this frame (for example after
            // a clip ends), remove our own previous delta before applying the new one.
            // Otherwise a stationary lean accumulates once per rendered frame.
            if (_poseApplied[index] && Quaternion.Angle(bone.localRotation, _appliedRotations[index]) < .01f)
                bone.localRotation = _baseRotations[index];
            _baseRotations[index] = bone.localRotation;
            if (share <= 0f && Mathf.Abs(roll) <= .001f)
            {
                _poseApplied[index] = false;
                return;
            }
            // Unity 旋转约定：绕角色右轴正角 = 低头（与 FPMouseLook「抬头为负欧拉 X」一致，
            // 已用 AnimationMode 探针实测：+30° 绕 root.right 使 head.forward.y 下降 0.46）。
            // pitch 以「抬头为正」，故叠加时取负。
            Quaternion delta = Quaternion.AngleAxis(-pitch * share, right)
                             * Quaternion.AngleAxis(yaw * share, up)
                             * Quaternion.AngleAxis(-roll, forward);
            Quaternion parentWorld = bone.parent != null ? bone.parent.rotation : Quaternion.identity;
            Quaternion worldRot = parentWorld * bone.localRotation;
            bone.localRotation = Quaternion.Inverse(parentWorld) * (delta * worldRot);
            _appliedRotations[index] = bone.localRotation;
            _poseApplied[index] = true;
        }
    }
}
