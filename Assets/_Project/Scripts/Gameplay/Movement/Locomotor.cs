using System;
using Game.Gameplay.Player;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    public enum LocomotionState { Idle, Walk, Sprint, Jump, Air, Land }

    /// <summary>
    /// 移动唯一写者（架构表A，2026-08-24 根运动修订）：
    /// 地面 XZ 位移 = TP clip 根运动增量（RootMotionRelay 转发，本组件消费，唯一落点 CC.Move）；
    /// 垂直 = 物理（跳跃手感独立可调）；空中 = 动量 + airControl 操控。
    /// 状态判定 = 输入意图（根运动下若按实测速度判定，会与 clip 选择互锁死循环）。
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class Locomotor : MonoBehaviour
    {
        [Header("根运动（地面位移来源）")]
        [SerializeField] private bool useRootMotion = true;
        [SerializeField] private float rootMotionScale = 1f;

        [Header("速度（速度驱动回退/空中动量基准）")]
        [SerializeField] private float walkSpeed = 4.5f;
        [SerializeField] private float sprintSpeed = 7.5f;
        [SerializeField] private float groundAcceleration = 30f;
        [SerializeField, Range(0f, 1f)] private float airControl = 0.35f;

        [Header("跳跃与重力")]
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private float gravity = -22f;
        [SerializeField] private float coyoteTime = 0.12f;
        [SerializeField] private float landDuration = 0.12f;

        [Header("视角（Yaw 写身体，Pitch 由表现层相机持有）")]
        [SerializeField, Range(0.01f, 1f)] private float yawSensitivity = 0.1f;

        public LocomotionState State { get; private set; } = LocomotionState.Idle;
        public float HorizontalSpeed => new Vector3(_velocity.x, 0f, _velocity.z).magnitude;
        /// <summary>本地移动输入（AnimDriver 选 8 向 clip 用；Day3 收敛进 PlayerStateView）。</summary>
        public Vector2 MoveInput => _input != null ? _input.Move : Vector2.zero;
        public event Action<LocomotionState> OnStateChanged;

        private CharacterController _cc;
        private InputReader _input;
        private Vector3 _velocity;
        private float _verticalVelocity;
        private float _coyoteTimer;
        private float _landTimer;
        private bool _sprintIntent;
        // 根运动增量：动画帧产生（OnAnimatorMove），物理帧消费
        private Vector3 _pendingRootPos;
        private Quaternion _pendingRootRot = Quaternion.identity;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _input = GetComponentInParent<InputReader>();
        }

        private void Update()
        {
            if (_input == null) return;
            // Yaw：鼠标水平增量驱动身体朝向；相机只读跟随（Pitch 在表现层 FPMouseLook）
            transform.Rotate(0f, _input.LookDelta.x * yawSensitivity, 0f);
        }

        /// <summary>RootMotionRelay（OnAnimatorMove 内）调用：根运动增量入队。旁路 CC 的位移一律拒绝。</summary>
        public void OnAnimatorRootMotion(Vector3 deltaPosition, Quaternion deltaRotation)
        {
            _pendingRootPos += deltaPosition * rootMotionScale;
            _pendingRootRot = deltaRotation * _pendingRootRot;
        }

        private void FixedUpdate()
        {
            if (_input == null) return;
            float dt = Time.fixedDeltaTime;

            // 消费本物理帧累积的根运动增量
            Vector3 rootDelta = _pendingRootPos;
            _pendingRootPos = Vector3.zero;
            Quaternion rootRot = _pendingRootRot;
            _pendingRootRot = Quaternion.identity;

            bool sprinting = _input.Sprint && _input.Move.y > 0.5f; // 仅前进可冲刺（FPS 惯例）
            _sprintIntent = sprinting && _input.Move.sqrMagnitude > 0.01f;
            Vector3 wishDir = transform.TransformDirection(new Vector3(_input.Move.x, 0f, _input.Move.y));
            if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

            Vector3 horizontalMove;
            if (useRootMotion && _cc.isGrounded && rootDelta.sqrMagnitude > 1e-10f)
            {
                // 地面：XZ 由根运动决定（压平 Y——垂直属物理域）。
                // _velocity 记实测 clip 速度：跳跃瞬间的动量来源 + 状态/调试显示。
                Vector3 flat = new Vector3(rootDelta.x, 0f, rootDelta.z);
                horizontalMove = flat;
                _velocity = flat / dt;
            }
            else
            {
                // 空中（动量+airControl 操控）或无根运动（回退速度驱动；静止输入向 0 收敛）
                float targetSpeed = sprinting ? sprintSpeed : walkSpeed;
                float control = _cc.isGrounded ? 1f : airControl;
                _velocity = Vector3.MoveTowards(_velocity, wishDir * targetSpeed, groundAcceleration * control * dt);
                horizontalMove = _velocity * dt;
            }

            // 根运动旋转增量（locomotion clip≈0；为闪避/死亡踉跄等带转向 clip 预留通道）
            if (rootRot != Quaternion.identity)
                transform.rotation *= rootRot;

            // 跳跃（土狼时间+输入缓冲）与重力
            if (_cc.isGrounded)
            {
                _coyoteTimer = coyoteTime;
                if (_verticalVelocity < -2f) _verticalVelocity = -2f; // 贴地防抖
            }
            else
            {
                _coyoteTimer -= dt;
            }

            if (_input.JumpQueued && _coyoteTimer > 0f)
            {
                _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
                _coyoteTimer = 0f;
                _input.ConsumeJump();
            }
            _verticalVelocity += gravity * dt;

            _cc.Move(horizontalMove + Vector3.up * _verticalVelocity * dt);

            UpdateState();
        }

        private void UpdateState()
        {
            LocomotionState next;
            if (!_cc.isGrounded)
            {
                _landTimer = landDuration; // 空中持续刷新，落地后保持 Land 态一小段
                next = _verticalVelocity > 0.1f ? LocomotionState.Jump : LocomotionState.Air;
            }
            else if (_landTimer > 0f)
            {
                _landTimer -= Time.fixedDeltaTime;
                next = LocomotionState.Land;
            }
            else if (_input.Move.sqrMagnitude < 0.01f)
            {
                next = LocomotionState.Idle;
            }
            else if (_sprintIntent)
            {
                next = LocomotionState.Sprint;
            }
            else
            {
                next = LocomotionState.Walk;
            }

            if (next != State)
            {
                State = next;
                OnStateChanged?.Invoke(State);
            }
        }
    }
}
