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
        /// <summary>土狼时间配置值（秒）：外部构造快照（重生）需要它，否则计时被写成 0 → 下一 tick 分支分叉。</summary>
        public float CoyoteSeconds => coyoteTime;
        /// <summary>落地计时配置值（秒）。</summary>
        public float LandSeconds => landDuration;
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
        /// <summary>上一次模拟步采样到的落地状态（审计 2026-09-16 M3：快照携带，不再用枚举反推）。</summary>
        private bool _groundedSampled;
        /// <summary>
        /// 重放首步的接地来源（审计 2026-09-16 §3.2-1）：ApplyAuthoritativeSnapshot 把 CC 瞬移到
        /// 权威位姿（期间禁用再启用），此刻 `_cc.isGrounded` 尚未重新求解，直接用它会拿错误的分支
        /// （SimulateGround vs SimulateAir）→ 重放第一步就分叉。因此由快照显式给一次接地覆盖：
        /// **只用一步**，后续步一律以实际接触（CC.isGrounded）更新（坡沿/跳跃语义不变）。
        /// </summary>
        private bool _hasGroundedOverride;
        private bool _groundedOverride;

        /// <summary>最近一次 Simulate 的完整物理/状态证据（诊断环形采样用；只读）。</summary>
        public LocomotorStepDebug LastStepDebug { get; private set; }
        /// <summary>最近一次权威快照恢复的证据（诊断用；只读）。</summary>
        public LocomotorRestoreDebug LastRestoreDebug { get; private set; }

        /// <summary>
        /// 基础俯仰提供者（可选；由网络适配器注入）：客户端=相机节点当前俯仰，服务器=权威远端俯仰。
        /// 未注入（离线/无相机）时为 0。审计 2026-09-16 §6.2：快照必须携带基础俯仰，
        /// 否则两端 pitch 各自漂移且位置/yaw 纠偏不会修正它。
        /// </summary>
        public Func<float> PitchProvider { get; set; }

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
            Vector3 rootBefore = transform.position;
            // Yaw 后坐由同一 WeaponRecoilState 提供；鼠标反向输入先消费债务，
            // 剩余部分才真正旋转玩家身体，保证水平压枪与相机/射线同源。
            float yawDelta = command.YawDelta;
            if (_weaponController != null)
                yawDelta = _weaponController.ConsumeRecoilCompensation(new Vector2(0f, yawDelta)).y;
            transform.Rotate(0f, yawDelta, 0f);

            // 审计 2026-09-16 §3.2-1：接地分支必须用"定义明确的来源"——重放首步取权威快照的
            // grounded（刚瞬移过，CC 尚未重解算），其余步一律取 CC 实际接触。
            bool groundedFromSnapshot = _hasGroundedOverride;
            bool groundedBeforeMove = _hasGroundedOverride ? _groundedOverride : _cc.isGrounded;
            _hasGroundedOverride = false;
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
            CollisionFlags collisionFlags = _cc.Move(horizontalDelta + Vector3.up * (_verticalVelocity * deltaTime));
            // 2026-09-16 审计 M3：采样本步落地状态（快照携带；物理分支仍由 CC 决定，不反推）
            _groundedSampled = _cc.isGrounded;
            UpdateState(deltaTime);

            // 审计 2026-09-16 §3.3：把本步的输入/物理分支/状态完整留证（供两端按 tick 对齐）。
            LastStepDebug = new LocomotorStepDebug
            {
                Tick = command.Tick,
                MoveInput = _lastCommand.Move,
                Sprint = command.Sprint,
                Jump = command.Jump,
                RootBefore = rootBefore,
                RootAfterMove = transform.position,
                GroundedBranch = groundedBeforeMove,
                GroundedFromSnapshot = groundedFromSnapshot,
                GroundedAfterMove = _groundedSampled,
                CollisionFlags = collisionFlags,
                HorizontalVelocity = _horizontalVelocity,
                VerticalVelocity = _verticalVelocity,
                GroundSpeed = _groundSpeed,
                CoyoteTimer = _coyoteTimer,
                LandTimer = _landTimer,
                GaitPhase = _gaitPhase,
                State = State,
                SprintIntent = _sprintIntent,
                ProfileHash = ProfileVersionHash,
                RecoilDebt = _weaponController != null
                    ? _weaponController.RecoilCompensationDebt
                    : Vector2.zero,
            };
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
                GaitPhase = _gaitPhase,
                // 2026-09-16 审计 M3：确定性重放所需状态（原实现靠速度模长/枚举推断 → 重放不收敛）
                Grounded = _groundedSampled,
                GroundSpeed = _groundSpeed,
                CoyoteTimer = _coyoteTimer,
                LandTimer = _landTimer,
                SprintIntent = _sprintIntent,
                RecoilDebt = _weaponController != null
                    ? _weaponController.RecoilCompensationDebt
                    : Vector2.zero,
                Pitch = PitchProvider != null ? PitchProvider() : 0f,
            };
        }

        /// <summary>未来 NetworkAdapter 提交服务器快照的入口；网络层不得直接写 Transform。</summary>
        public void ApplyAuthoritativeSnapshot(in MovementSnapshot snapshot)
        {
            if (_cc == null) return;
            bool wasEnabled = _cc.enabled;
            bool groundedBefore = _groundedSampled;
            _cc.enabled = false;
            transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
            _cc.enabled = wasEnabled;
            // 审计 2026-09-16 §3.2-1：CC 刚被瞬移，isGrounded 尚未重新解算 → 下一步的分支输入
            // 必须由快照显式给一次（只用一步，后续步回到实际接触）。
            _hasGroundedOverride = true;
            _groundedOverride = snapshot.Grounded;

            LastRestoreDebug = new LocomotorRestoreDebug
            {
                CcWasEnabled = wasEnabled,
                CcEnabledAfter = _cc.enabled,
                GroundedBefore = groundedBefore,
                GroundedSupplied = snapshot.Grounded,
                CcGroundedAfter = _cc.isGrounded,
                AppliedPosition = snapshot.Position,
            };

            _horizontalVelocity = snapshot.HorizontalVelocity;
            _verticalVelocity = snapshot.VerticalVelocity;
            _gaitPhase = Mathf.Repeat(snapshot.GaitPhase, 1f);
            _lastCommand.Tick = snapshot.Tick;
            SetState(snapshot.LocomotionState);

            // 2026-09-16 审计 M3：快照携带的模拟状态**精确恢复**。原实现用"速度模长 + 枚举"推断：
            // ① _groundSpeed = |HorizontalVelocity| 在 RootMotion 相位下恒不等（该相位下瞬时速度与
            //    步态相位相关）；② 空中 sprintIntent 推断不出（注释曾承认"由位置比较兜底"）；
            // ③ coyote/land 计时被重置成满值。三者都会让硬校正后的下一 tick 继续分叉 → 来回纠偏。
            _groundSpeed = snapshot.GroundSpeed;
            _coyoteTimer = snapshot.CoyoteTimer;
            _landTimer = snapshot.LandTimer;
            _sprintIntent = snapshot.SprintIntent;
            _groundedSampled = snapshot.Grounded;
            // 武器后坐补偿债务也必须回到权威值：Simulate 每步消费它，否则重放会二次消费/少消费
            // （审计 §M3"输入消费与视觉/武器副作用分离"）。
            if (_weaponController != null)
                _weaponController.RestoreRecoilCompensationDebt(snapshot.RecoilDebt);
        }

        /// <summary>Owner 小误差平滑收敛的位移入口（2026-09-10 审计 §3）：与 ApplyAuthoritativeSnapshot
        /// 同一写入模式（CC 短暂禁用后直写 Transform）。替代调用方直接 transform.position += ——
        /// 避免与 CharacterController.Move 的碰撞解算在同一帧内互相拉扯（双方贴近站位时放大可见抖动）。
        /// 小步长不经 CC.Move：CC.Move 对亚厘米位移会被皮肤宽度吞掉，导致校正永久滞留。</summary>
        public void ApplySmoothCorrection(Vector3 offset)
        {
            if (_cc == null || offset.sqrMagnitude < 1e-12f) return;
            bool wasEnabled = _cc.enabled;
            _cc.enabled = false;
            transform.position += offset;
            _cc.enabled = wasEnabled;
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

    /// <summary>
    /// 单个模拟步的物理/状态证据（诊断只读；审计 2026-09-16 §3.3）。
    /// 与 MovementStepSample 的区别：这是"Locomotor 视角的原始事实"，不含网络/纠偏包装。
    /// </summary>
    public struct LocomotorStepDebug
    {
        public uint Tick;
        public Vector2 MoveInput;
        public bool Sprint;
        public bool Jump;
        public Vector3 RootBefore;
        /// <summary>CC.Move 之后（不含平滑校正）。</summary>
        public Vector3 RootAfterMove;
        /// <summary>本步分支输入使用的 grounded（重放首步来自快照）。</summary>
        public bool GroundedBranch;
        /// <summary>本步分支输入是否来自权威快照（重放首步）。</summary>
        public bool GroundedFromSnapshot;
        public bool GroundedAfterMove;
        public CollisionFlags CollisionFlags;
        public Vector3 HorizontalVelocity;
        public float VerticalVelocity;
        public float GroundSpeed;
        public float CoyoteTimer;
        public float LandTimer;
        public float GaitPhase;
        public LocomotionState State;
        public bool SprintIntent;
        public string ProfileHash;
        public Vector2 RecoilDebt;
    }

    /// <summary>
    /// 权威快照恢复的证据（诊断只读）：记录 CC 禁启前/后的接地与瞬移结果——
    /// 用于回答"重放首步的分支输入到底来自哪里"（审计 2026-09-16 §3.2-1）。
    /// </summary>
    public struct LocomotorRestoreDebug
    {
        public bool CcWasEnabled;
        public bool CcEnabledAfter;
        public bool GroundedBefore;
        public bool GroundedSupplied;
        /// <summary>瞬移+重新启用后 CC 自己报的接地（未重新解算，仅作对照）。</summary>
        public bool CcGroundedAfter;
        public Vector3 AppliedPosition;
    }
}
