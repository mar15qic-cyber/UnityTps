using System;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Gameplay.Movement
{
    public enum LocomotionState { Idle, Walk, Sprint, Jump, Air, Land }

    /// <summary>
    /// 实体移动唯一写者。离线模式按渲染帧模拟；未来 FishNet 服务器/预测端调用同一个 Simulate。
    /// 地面位移来自确定性 RootMotionProfile，不依赖运行时 Animator.deltaPosition。
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(CharacterController))]
    public sealed class Locomotor : MonoBehaviour
    {
        [Header("模拟权威")]
        [SerializeField] private MovementSimulationMode simulationMode = MovementSimulationMode.OfflineLocal;
        [SerializeField] private RootMotionProfile rootMotionProfile;

        [Header("地面响应")]
        [SerializeField, Min(0f)] private float groundAcceleration = 32f;
        [SerializeField, Min(0f)] private float groundDeceleration = 48f;
        [SerializeField, Range(0f, 1f)] private float airControl = 0.35f;

        [Header("跳跃与重力")]
        [SerializeField] private float jumpHeight = 1.1f;
        [SerializeField] private float gravity = -22f;
        [SerializeField] private float coyoteTime = 0.12f;
        [SerializeField] private float landDuration = 0.12f;

                [Header("根运动旋转")]
        [SerializeField, Range(0f, 1f)] private float rootMotionYawWeight = 0f;
        [SerializeField, Min(0f)] private float maxRootMotionYawStep = 0.35f;

[Header("视角")]
        [SerializeField, Range(0.01f, 1f)] private float yawSensitivity = 0.1f;

        public LocomotionState State { get; private set; } = LocomotionState.Idle;
        public float HorizontalSpeed => _horizontalVelocity.magnitude;
        public Vector2 MoveInput => _lastCommand.Move;
        public float GaitPhase => _gaitPhase;
        public MovementSimulationMode SimulationMode => simulationMode;
        public string ProfileVersionHash => rootMotionProfile != null ? rootMotionProfile.VersionHash : string.Empty;
        public event Action<LocomotionState> OnStateChanged;

        private CharacterController _cc;
        private InputReader _input;
        private WeaponController _weaponController;
        private MovementCommand _lastCommand;
        private Vector3 _horizontalVelocity;
        private float _groundSpeed;
        private float _verticalVelocity;
        private float _coyoteTimer;
        private float _landTimer;
        private float _gaitPhase;
        private bool _sprintIntent;
        private bool _jumpConsumedThisStep;
        private uint _offlineTick;

        private const float DefaultWalkSpeed = 1.58f;
        private const float DefaultSprintSpeed = 3.44f;

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _input = GetComponentInParent<InputReader>();
            _weaponController = GetComponentInParent<WeaponController>();
            ResolveProfileFromWeapon(_weaponController != null ? _weaponController.Definition : null);
        }

        private void OnEnable()
        {
            if (_weaponController == null) _weaponController = GetComponentInParent<WeaponController>();
            if (_weaponController != null) _weaponController.OnWeaponEquipped += ResolveProfileFromWeapon;
        }

        private void Start()
        {
            ResolveProfileFromWeapon(_weaponController != null ? _weaponController.Definition : null);
        }

        private void OnDisable()
        {
            if (_weaponController != null) _weaponController.OnWeaponEquipped -= ResolveProfileFromWeapon;
        }

        private void Update()
        {
            if (simulationMode != MovementSimulationMode.OfflineLocal || _input == null) return;

            var command = new MovementCommand(
                _input.Move,
                _input.Sprint,
                _input.JumpQueued,
                _input.LookDelta.x * yawSensitivity,
                ++_offlineTick);

            Simulate(command, Time.deltaTime);
            if (_jumpConsumedThisStep) _input.ConsumeJump();
        }

        /// <summary>离线、预测客户端与服务器共同使用的唯一移动模拟入口。</summary>
        public void Simulate(in MovementCommand command, float deltaTime)
        {
            if (_cc == null || simulationMode == MovementSimulationMode.RemoteProxy || deltaTime <= 0f) return;

            _jumpConsumedThisStep = false;
            _lastCommand = command;
            _lastCommand.Move = Vector2.ClampMagnitude(command.Move, 1f);
            transform.Rotate(0f, command.YawDelta, 0f);

            bool groundedBeforeMove = _cc.isGrounded;
            if (groundedBeforeMove)
            {
                _coyoteTimer = coyoteTime;
                if (_verticalVelocity < -2f) _verticalVelocity = -2f;
            }
            else
            {
                _coyoteTimer -= deltaTime;
            }

            if (command.Jump && _coyoteTimer > 0f)
            {
                _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
                _coyoteTimer = 0f;
                _jumpConsumedThisStep = true;
                groundedBeforeMove = false;
            }

            Vector3 horizontalDelta = groundedBeforeMove
                ? SimulateGround(command, deltaTime)
                : SimulateAir(command, deltaTime);

            _verticalVelocity += gravity * deltaTime;
            _cc.Move(horizontalDelta + Vector3.up * (_verticalVelocity * deltaTime));
            UpdateState(deltaTime);
        }

        public MovementSnapshot CaptureSnapshot()
        {
            return new MovementSnapshot
            {
                Tick = _lastCommand.Tick,
                Position = transform.position,
                Rotation = transform.rotation,
                HorizontalVelocity = _horizontalVelocity,
                VerticalVelocity = _verticalVelocity,
                LocomotionState = State,
                GaitPhase = _gaitPhase
            };
        }

        /// <summary>未来 NetworkAdapter 提交服务器快照的入口；网络层不得直接写 Transform。</summary>
        public void ApplyAuthoritativeSnapshot(in MovementSnapshot snapshot)
        {
            if (_cc == null) return;
            bool wasEnabled = _cc.enabled;
            _cc.enabled = false;
            transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
            _cc.enabled = wasEnabled;

            _horizontalVelocity = snapshot.HorizontalVelocity;
            _groundSpeed = _horizontalVelocity.magnitude;
            _verticalVelocity = snapshot.VerticalVelocity;
            _gaitPhase = Mathf.Repeat(snapshot.GaitPhase, 1f);
            _lastCommand.Tick = snapshot.Tick;
            SetState(snapshot.LocomotionState);
        }

        public void SetSimulationMode(MovementSimulationMode mode) => simulationMode = mode;

        public void SetRootMotionProfile(RootMotionProfile profile) => rootMotionProfile = profile;

        private Vector3 SimulateGround(in MovementCommand command, float deltaTime)
        {
            Vector2 move = command.Move;
            if (move.sqrMagnitude < 0.0001f)
            {
                _groundSpeed = 0f;
                _horizontalVelocity = Vector3.zero;
                _sprintIntent = false;
                return Vector3.zero;
            }

            _sprintIntent = command.Sprint && move.y > 0.5f;
            RootMotionGait gait = _sprintIntent ? RootMotionGait.Sprint : RootMotionGait.Walk;
            float canonicalSpeed = gait == RootMotionGait.Sprint
                ? rootMotionProfile != null ? rootMotionProfile.SprintSpeed : DefaultSprintSpeed
                : rootMotionProfile != null ? rootMotionProfile.WalkSpeed : DefaultWalkSpeed;

            float response = canonicalSpeed >= _groundSpeed ? groundAcceleration : groundDeceleration;
            _groundSpeed = Mathf.MoveTowards(_groundSpeed, canonicalSpeed, response * deltaTime);

            Vector2 localRootDelta;
            float rootYaw;
            if (rootMotionProfile != null && rootMotionProfile.IsValid)
            {
                localRootDelta = rootMotionProfile.EvaluateDelta(
                    gait, move, _gaitPhase, deltaTime, out _gaitPhase, out rootYaw);
                localRootDelta *= canonicalSpeed > 0f ? _groundSpeed / canonicalSpeed : 0f;
            }
            else
            {
                _gaitPhase = Mathf.Repeat(_gaitPhase + deltaTime / (gait == RootMotionGait.Sprint ? 0.6666667f : 0.9333334f), 1f);
                localRootDelta = move.normalized * (_groundSpeed * deltaTime);
                rootYaw = 0f;
            }

            // FPS 身体朝向由输入 YawDelta 权威驱动。RootQ 仍保留在 Profile 中，
            // 但步态周期的往复扭转默认不写入 Player 根节点，避免第一人称相机继承眩晕感。
            // 后续第三人称表现可通过 rootMotionYawWeight 逐步启用，并受单帧上限保护。
            if (rootMotionYawWeight > 0f && Mathf.Abs(rootYaw) > 0.0001f)
            {
                float yawStep = Mathf.Clamp(
                    rootYaw * rootMotionYawWeight,
                    -maxRootMotionYawStep,
                    maxRootMotionYawStep);
                transform.Rotate(0f, yawStep, 0f);
            }
            Vector3 worldDelta = transform.TransformDirection(new Vector3(localRootDelta.x, 0f, localRootDelta.y));
            _horizontalVelocity = worldDelta / deltaTime;
            return worldDelta;
        }

        private Vector3 SimulateAir(in MovementCommand command, float deltaTime)
        {
            Vector3 wishDirection = transform.TransformDirection(new Vector3(command.Move.x, 0f, command.Move.y));
            if (wishDirection.sqrMagnitude > 1f) wishDirection.Normalize();
            float targetSpeed = _sprintIntent ? DefaultSprintSpeed : DefaultWalkSpeed;
            _horizontalVelocity = Vector3.MoveTowards(
                _horizontalVelocity,
                wishDirection * targetSpeed,
                groundAcceleration * airControl * deltaTime);
            return _horizontalVelocity * deltaTime;
        }

        private void UpdateState(float deltaTime)
        {
            LocomotionState next;
            if (!_cc.isGrounded)
            {
                _landTimer = landDuration;
                next = _verticalVelocity > 0.1f ? LocomotionState.Jump : LocomotionState.Air;
            }
            else if (_landTimer > 0f)
            {
                _landTimer -= deltaTime;
                next = LocomotionState.Land;
            }
            else if (_lastCommand.Move.sqrMagnitude < 0.01f)
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

            SetState(next);
        }

        private void SetState(LocomotionState next)
        {
            if (next == State) return;
            State = next;
            OnStateChanged?.Invoke(State);
        }

        private void ResolveProfileFromWeapon(WeaponDefinition definition)
        {
            if (definition != null && definition.ThirdPersonRootMotionProfile != null)
                rootMotionProfile = definition.ThirdPersonRootMotionProfile;
        }
    }
}
