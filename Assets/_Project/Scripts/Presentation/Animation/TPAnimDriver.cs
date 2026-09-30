using Animancer;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Animation;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP Animator 的唯一动画写者：Layer0 locomotion + Layer1 weapon action。
    /// 只把移动状态源与 Gameplay 事件翻译成 Animancer 播放，不修改 Gameplay 真相。
    /// 状态源（审计 2026-09-16 D1 修复）：远端化身启用 RemotePlayerStateView → 读 NetworkLocomotionState
    /// 的同步值；否则读本地 PlayerStateView（真实 Locomotor）。显式类型选择，不再依赖反射注入。
    /// 死亡/复活生命周期（审计 2026-09-18 §4）：实现 <see cref="IThirdPersonPoseLifecycle"/>，
    /// 由 Gameplay 的死亡表现统一驱动"冻结 / 复活重建"，不再依赖组件停用副作用与状态变化巧合。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(AnimancerComponent))]
    public sealed class TPAnimDriver : MonoBehaviour, IThirdPersonPoseLifecycle
    {
        [SerializeField] private PlayerStateView stateView;              // 本地/离线/服务器权威（真实 Locomotor）
        [SerializeField] private RemotePlayerStateView remoteStateView;  // 远端化身替身（同步状态）
        [SerializeField] private WeaponController controller;
        [SerializeField, Min(0f)] private float stateFadeSeconds = 0.06f;
        [SerializeField, Min(0f)] private float visualDirectionSmoothTime = 0.04f;

        [Header("Layer1 武器动作")]
        [SerializeField] private AvatarMask upperBodyMask;
        [SerializeField, Min(0f)] private float fireFadeSeconds = 0.05f;
        [SerializeField, Min(0f)] private float actionFadeSeconds = 0.15f;
        [SerializeField, Min(0f)] private float layerFadeOutSeconds = 0.2f;

        private const int LocomotionLayer = 0;
        private const int ActionLayer = 1;

        private AnimancerComponent _animancer;
        private TpLocomotionSet _locomotionClips;
        private TpActionSet _actionClips;
        private TpThrowClips _throwClips;
        private TpThrowAnimationCatalog _throwCatalog;
        private ThrowableController _throwables;
        private TPWeaponMeshSwapper _weaponSwapper;
        private GameObject _heldThrowable;
        private string _heldThrowableItemId;
        private uint _throwPoseSequence;
        private ThrowablePosePhase _throwPosePhase;
        private CartesianMixerState _walkMixer;
        private CartesianMixerState _runMixer;
        private LocomotionState _currentState = (LocomotionState)(-1);
        private Vector2 _visualMove;
        private Vector2 _visualMoveVelocity;
        private bool _mixersValid;
        private bool _actionClipsReady;
        private bool _serverTickDriven;

        private void Awake()
        {
            _throwables = GetComponentInParent<ThrowableController>();
            _weaponSwapper = GetComponent<TPWeaponMeshSwapper>();
            _throwCatalog = Resources.Load<TpThrowAnimationCatalog>("TpThrowAnimationCatalog");
            _animancer = GetComponent<AnimancerComponent>();
            _animancer.Animator.applyRootMotion = false;
            _animancer.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (stateView == null) stateView = GetComponentInParent<PlayerStateView>();
            if (remoteStateView == null) remoteStateView = GetComponentInChildren<RemotePlayerStateView>(true);
            ResolveStateSource();
            if (controller == null) controller = GetComponentInParent<WeaponController>();

            var actionLayer = _animancer.Layers[ActionLayer];
            if (upperBodyMask != null) _animancer.Layers.SetMask(ActionLayer, upperBodyMask);
            actionLayer.Weight = 0f;
            actionLayer.IsAdditive = false;
        }

        /// <summary>
        /// 状态源解析（审计 2026-09-16 D1）：远端替身"存在且启用" → 用它（其底层 NetworkLocomotionState
        /// 的 getter 已按 本地/远端/离线 三分支给出正确值）；否则用本地 PlayerStateView。
        /// 池化复用（Owner↔远端切换）时 OnEnable 会重新解析。
        /// </summary>
        private void ResolveStateSource()
        {
            if (remoteStateView == null) remoteStateView = GetComponentInChildren<RemotePlayerStateView>(true);
            if (stateView == null) stateView = GetComponentInParent<PlayerStateView>();
            _remoteSource = remoteStateView != null && remoteStateView.isActiveAndEnabled ? remoteStateView : null;
            _localSource = stateView;
        }

        private ITpLocomotionSource _remoteSource;
        private PlayerStateView _localSource;

        /// <summary>当前是否使用远端状态源（诊断/测试：旧实现的反射接线恒 false，故这一位必须可断言）。</summary>
        public bool UsingRemoteSource => _remoteSource != null;
        /// <summary>当前状态源给出的移动状态（诊断/测试）。</summary>
        public LocomotionState SourceLocomotionState => _remoteSource != null
            ? _remoteSource.LocomotionState
            : _localSource != null ? _localSource.LocomotionState : LocomotionState.Idle;
        /// <summary>当前状态源给出的步态相位（诊断/测试）。</summary>
        public float SourceGaitPhase => _remoteSource != null
            ? _remoteSource.GaitPhase
            : _localSource != null ? _localSource.GaitPhase : 0f;
        /// <summary>当前状态源给出的移动输入（诊断/测试）。</summary>
        public Vector2 SourceMoveInput => _remoteSource != null
            ? _remoteSource.MoveInput
            : _localSource != null ? _localSource.MoveInput : Vector2.zero;

        private void OnEnable()
        {
            ResolveStateSource(); // 池化复用（Owner↔远端）时重新解析状态源
            // 重新启用后 Animancer 图可能已被 DisableAction.Reset 停掉（OnEnable 只 UnpauseGraph，
            // 不会重新选中状态）——必须请求一次强制重播，不能等"状态发生变化"这个巧合。
            _needsReapply = true;
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (controller == null) return;
            controller.OnShotFired += HandleShot;
            controller.OnDryFire += HandleDryFire;
            controller.OnReloadStarted += HandleReloadStarted;
            controller.OnReloadCompleted += HandleReloadCompleted;
            controller.OnReloadInterrupted += HandleReloadInterrupted;
            controller.OnWeaponEquipped += HandleWeaponEquipped;
        }

        private void Start()
        {
            if (_poseFrozenForDeath) return;
            LoadClips();
            ApplyState(true);
        }

        private void OnDisable()
        {
            _serverTickDriven = false;
            ClearThrowableView();
            if (controller == null) return;
            controller.OnShotFired -= HandleShot;
            controller.OnDryFire -= HandleDryFire;
            controller.OnReloadStarted -= HandleReloadStarted;
            controller.OnReloadCompleted -= HandleReloadCompleted;
            controller.OnReloadInterrupted -= HandleReloadInterrupted;
            controller.OnWeaponEquipped -= HandleWeaponEquipped;
        }

        private void Update()
        {
            if (_serverTickDriven) return;
            UpdatePose(Time.deltaTime);
        }

        // Called once per authoritative tick, before pitch and hitbox capture. Pausing
        // automatic evaluation prevents render FPS from advancing the server graph twice.
        public void EvaluateServerAnimationTick(float deltaTime)
        {
            if (!isActiveAndEnabled || _animancer == null) return;
            _serverTickDriven = true;
            _remoteSource = null;
            _animancer.Graph.PauseGraph();
            if (!_clipsLoaded) LoadClips();
            UpdatePose(deltaTime);
            _animancer.Evaluate(deltaTime);
        }

        private void UpdatePose(float deltaTime)
        {
            if (_poseFrozenForDeath)
            {
                if (_deathState != null && _deathState.Time >= _deathState.Length)
                { _deathState.Time = _deathState.Length; _deathState.Speed = 0; }
                return;
            } // // 死亡期间姿态由 Gameplay 冻结：本驱动器不得写图
            if (_remoteSource == null && _localSource == null) return;
            // _needsReapply：复活/池化重启用后状态值可能仍是 Idle（与缓存相同），旧实现在这里
            // 什么都不做 → 图保持停止、模型停在 Rebind 骨架姿态（实机"重生陷地"）。
            if (_needsReapply || SourceLocomotionState != _currentState)
            {
                _needsReapply = false;
                ApplyState(false);
            }
            UpdateThrowableView();
            UpdateMixerParametersAndPhase(deltaTime);
        }

        private void ApplyState(bool immediate)
        {
            if (_poseFrozenForDeath) return;
            if (_remoteSource == null && _localSource == null) return;
            var previousState = _currentState;
            _currentState = SourceLocomotionState;
            // 2026-09-17 方案B：进入走/跑态时把本地积分相位对齐到最新同步值（状态切换是
            // 唯一需要瞬时对齐的时刻；稳态纠偏交给 CorrectToward，速率一致时锚定不可见）
            if (_remoteSource != null
                && _currentState != previousState
                && (_currentState == LocomotionState.Walk || _currentState == LocomotionState.Sprint))
                _remotePhase = SourceGaitPhase;
            float fade = immediate ? 0f : stateFadeSeconds;
            if (!_mixersValid) return;

            switch (_currentState)
            {
                case LocomotionState.Idle:
                    if (_locomotionClips.Idle != null) _animancer.Layers[LocomotionLayer].Play(_locomotionClips.Idle, fade);
                    break;
                case LocomotionState.Jump:
                    if (_locomotionClips.JumpStart != null) _animancer.Layers[LocomotionLayer].Play(_locomotionClips.JumpStart, fade);
                    break;
                case LocomotionState.Air:
                    if (_locomotionClips.JumpLoop != null) _animancer.Layers[LocomotionLayer].Play(_locomotionClips.JumpLoop, fade);
                    break;
                case LocomotionState.Land:
                    if (_locomotionClips.JumpLand != null) _animancer.Layers[LocomotionLayer].Play(_locomotionClips.JumpLand, fade);
                    break;
                case LocomotionState.Sprint:
                    if (_runMixer != null) _animancer.Layers[LocomotionLayer].Play(_runMixer, fade);
                    break;
                default:
                    if (_walkMixer != null) _animancer.Layers[LocomotionLayer].Play(_walkMixer, fade);
                    break;
            }
        }

        // ---- 2026-09-17 方案B：远端步态相位本地连续积分（实机"翻小人书"修复）----
        // 远端相位以渲染帧率本地推进（服务器同周期常量），10Hz SyncVar 只做低频锚定；
        // 此前每帧把 10Hz 同步相位覆写进 Speed=0 的混合器 → 腿部动画 10Hz 跳变。
        private const float RemotePhaseSnapThreshold = 0.25f;
        private const float RemotePhasePullPerSecond = 4f;
        private float _remotePhase;

        /// <summary>远端当前本地积分相位（诊断/测试）。</summary>
        public float RemoteIntegratedPhase => _remotePhase;

        private void UpdateMixerParametersAndPhase(float deltaTime)
        {
            Vector2 target = SourceMoveInput;
            _visualMove = visualDirectionSmoothTime <= 0f
                ? target
                : Vector2.SmoothDamp(
                    _visualMove,
                    target,
                    ref _visualMoveVelocity,
                    visualDirectionSmoothTime,
                    Mathf.Infinity,
                    deltaTime);

            float phase;
            if (_remoteSource != null)
            {
                if (_remoteSource.HasPresentedPose)
                {
                    // Position, jump state and gait now share one rendered server tick.
                    // Extrapolating the gait independently makes feet slide between
                    // snapshots and visibly depart from the rewound server hitboxes.
                    _remotePhase = SourceGaitPhase;
                }
                else
                {
                    bool sprint = SourceLocomotionState == LocomotionState.Sprint;
                    _remotePhase = RemoteGaitPhase.Advance(_remotePhase, deltaTime, ResolveCycleSeconds(sprint));
                    _remotePhase = RemoteGaitPhase.CorrectToward(
                        _remotePhase, SourceGaitPhase, RemotePhaseSnapThreshold, RemotePhasePullPerSecond, deltaTime);
                }
                phase = _remotePhase;
            }
            else
            {
                phase = SourceGaitPhase;
            }
            ApplyPhaseToMixers(phase);
        }

        private void ApplyPhaseToMixers(float phase)
        {
            if (_walkMixer != null && _walkMixer.IsPlaying)
            {
                _walkMixer.Parameter = _visualMove;
                _walkMixer.NormalizedTime = phase;
            }
            if (_runMixer != null && _runMixer.IsPlaying)
            {
                _runMixer.Parameter = _visualMove;
                _runMixer.NormalizedTime = phase;
            }
        }

        /// <summary>步态周期：优先当前武器 RootMotionProfile（与服务器 Locomotor 同源），缺失回退常量。</summary>
        private float ResolveCycleSeconds(bool sprint)
        {
            var profile = controller != null && controller.Definition != null
                ? controller.Definition.ThirdPersonRootMotionProfile
                : null;
            if (profile != null)
                return sprint ? profile.SprintCycleDuration : profile.WalkCycleDuration;
            return sprint ? RemoteGaitPhase.FallbackSprintCycleSeconds : RemoteGaitPhase.FallbackWalkCycleSeconds;
        }

        private void HandleWeaponEquipped(WeaponDefinition _)
        {
            if (_poseFrozenForDeath) return;
            LoadClips();
            FadeOutActionLayer();
            ApplyState(false);
        }

        /// <summary>clip 集来源定义（复用判定）。TpLocomotionSet 是 struct，不能用 null 判"已装载"，
        /// 故另置 _clipsLoaded。</summary>
        private WeaponDefinition _clipSource;
        private bool _clipsLoaded;

        private void LoadClips()
        {
            if (controller == null || controller.Definition == null) return;
            // R2（2026-09-18 复核）：同一武器的 clip 集**复用**，不再每次复活重建。
            // 旧实现在每次 RecoverPoseAfterRespawn 里都 new CartesianMixerState 并注册进 Layer0，
            // 而"权重归零/Stop"都不把节点从图上摘掉 → 每复活一次净增一套 walk/run mixer，
            // 直到图销毁才释放（AnimancerLayer.GetOrCreateState 对新的无父状态直接 SetParent，
            // 不按资源去重）。换武器确需重建时，先有序销毁旧 mixer 的子状态。
            if (_clipsLoaded && ReferenceEquals(_clipSource, controller.Definition)) return;
            DestroyLocomotionMixers();
            _clipSource = controller.Definition;
            _locomotionClips = controller.Definition.ThirdPersonLocomotion;
            _actionClips = controller.Definition.ThirdPersonActions;
            _throwClips = _throwCatalog != null ? _throwCatalog.For(controller.Definition) : default;

            _walkMixer = BuildDirectionalMixer(
                _locomotionClips.WalkForward, _locomotionClips.WalkForwardRight, _locomotionClips.WalkRight,
                _locomotionClips.WalkBackRight, _locomotionClips.WalkBackward, _locomotionClips.WalkBackLeft,
                _locomotionClips.WalkLeft, _locomotionClips.WalkForwardLeft);
            _runMixer = BuildDirectionalMixer(
                _locomotionClips.RunForward, _locomotionClips.RunForwardRight, _locomotionClips.RunRight,
                _locomotionClips.RunBackRight, _locomotionClips.RunBackward, _locomotionClips.RunBackLeft,
                _locomotionClips.RunLeft, _locomotionClips.RunForwardLeft);

            _mixersValid = _walkMixer != null || _runMixer != null || _locomotionClips.Idle != null;
            _actionClipsReady = _actionClips.Fire != null || _actionClips.ReloadAmmoLeft != null;
            _clipsLoaded = true;
        }

        /// <summary>销毁本驱动器自己建立的 locomotion mixer 节点（AnimancerState.Destroy 连带摘除其子状态）。
        /// 只摘图上的节点，不销毁共享的 AnimationClip 资源。</summary>
        private void DestroyLocomotionMixers()
        {
            if (_walkMixer != null) _walkMixer.Destroy();
            if (_runMixer != null) _runMixer.Destroy();
            _walkMixer = null;
            _runMixer = null;
            _mixersValid = false;
        }

        private CartesianMixerState BuildDirectionalMixer(
            AnimationClip forward, AnimationClip forwardRight, AnimationClip right, AnimationClip backRight,
            AnimationClip backward, AnimationClip backLeft, AnimationClip left, AnimationClip forwardLeft)
        {
            if (forward == null && right == null && backward == null && left == null) return null;

            var mixer = new CartesianMixerState();
            LocomotionMixerCreations++; // R2 回归断言点：同一武器反复复活时本计数必须不变
            _animancer.Layers[LocomotionLayer].GetOrCreateState(mixer);
            const float diagonal = 0.70710678f;
            if (forward != null) mixer.Add(forward, new Vector2(0f, 1f));
            if (forwardRight != null) mixer.Add(forwardRight, new Vector2(diagonal, diagonal));
            if (right != null) mixer.Add(right, new Vector2(1f, 0f));
            if (backRight != null) mixer.Add(backRight, new Vector2(diagonal, -diagonal));
            if (backward != null) mixer.Add(backward, new Vector2(0f, -1f));
            if (backLeft != null) mixer.Add(backLeft, new Vector2(-diagonal, -diagonal));
            if (left != null) mixer.Add(left, new Vector2(-1f, 0f));
            if (forwardLeft != null) mixer.Add(forwardLeft, new Vector2(-diagonal, diagonal));
            mixer.Parameter = Vector2.zero;
            mixer.Speed = 0f;
            return mixer;
        }

        private void HandleShot(WeaponShot _)
        {
            if (_poseFrozenForDeath || !_actionClipsReady || _actionClips.Fire == null) return;
            var state = _animancer.Layers[ActionLayer].Play(_actionClips.Fire, fireFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = FadeOutActionLayer;
        }

        private void HandleDryFire() => FadeOutActionLayer();

        private void HandleReloadStarted()
        {
            if (_poseFrozenForDeath || !_actionClipsReady || controller?.Runtime == null) return;
            AnimationClip clip = controller.Runtime.CurrentAmmo == 0
                ? _actionClips.ReloadOutOfAmmo
                : _actionClips.ReloadAmmoLeft;
            if (clip == null) return;
            var state = _animancer.Layers[ActionLayer].Play(clip, actionFadeSeconds, FadeMode.FromStart);
            // ActionSystem remains authoritative at Stat.ReloadTime. Fit the entire clip
            // into that window so the completion callback never cuts a long rifle reload.
            state.Speed = ReloadAnimationTiming.GetPlaybackSpeed(clip, controller.UsesShellReload ? controller.Runtime.ReloadRemaining : controller.Stat.ReloadTime);
            state.Events(this).OnEnd = FadeOutActionLayer;
        }

        private void HandleReloadInterrupted(ActionInterruptReason _) => FadeOutActionLayer();
        private void HandleReloadCompleted() => FadeOutActionLayer();

        private void FadeOutActionLayer()
        {
            if (_poseFrozenForDeath) return;
            _animancer.Layers[ActionLayer].StartFade(0f, layerFadeOutSeconds);
        }

        private void UpdateThrowableView()
        {
            if (_throwables == null) return;
            var pose = _throwables.Presentation;
            var authority = GetComponentInParent<NetworkCombatAuthority>();
            if (authority != null && pose.LifeEpoch != authority.LifeEpochForPresentation)
            {
                ClearThrowableView();
                return;
            }
            if (pose.Sequence == _throwPoseSequence && pose.Phase == _throwPosePhase) return;
            _throwPoseSequence = pose.Sequence;
            _throwPosePhase = pose.Phase;
            if (pose.Phase == ThrowablePosePhase.None)
            {
                ClearThrowableView();
                return;
            }
            if (pose.Phase == ThrowablePosePhase.Canceled)
            {
                HideHeldThrowable();
                var state = PlayThrowClip(_throwClips.Cancel);
                if (state != null) state.Events(this).OnEnd = () =>
                {
                    if (_throwPosePhase == ThrowablePosePhase.Canceled)
                        _weaponSwapper?.SetThrowableHidden(false);
                };
                else _weaponSwapper?.SetThrowableHidden(false);
                return;
            }
            _weaponSwapper?.SetThrowableHidden(true);
            if (pose.Phase == ThrowablePosePhase.Released)
            {
                HideHeldThrowable();
                var state = PlayThrowClip(_throwClips.Release);
                if (state != null) state.Time = Mathf.Clamp(ElapsedPoseSeconds(pose), 0f, state.Length);
                return;
            }
            EnsureHeldThrowable(string.IsNullOrEmpty(pose.ItemId) ? ThrowableSlots.DefaultId(pose.Type) : pose.ItemId);
            if (pose.Phase == ThrowablePosePhase.Selected) PlayThrowHold();
            else if (pose.Phase == ThrowablePosePhase.Started)
            {
                var state = PlayThrowClip(_throwClips.Start);
                if (state != null)
                {
                    float releaseDelay = Mathf.Max(.01f, _throwables.ReleaseDelaySeconds);
                    float elapsed = ElapsedPoseSeconds(pose);
                    state.Time = Mathf.Clamp(elapsed / releaseDelay * state.Length, 0f, state.Length);
                    state.Speed = state.Length / releaseDelay;
                    state.Events(this).OnEnd = () =>
                    {
                        if (_throwPosePhase == ThrowablePosePhase.Started) PlayThrowHold();
                    };
                }
            }
        }

        private static float ElapsedPoseSeconds(ThrowablePoseState pose)
        {
            double displayedTick = ObserverTimeline.Ready ? ObserverTimeline.PresentedTick
                : FishNet.InstanceFinder.TimeManager != null ? FishNet.InstanceFinder.TimeManager.Tick : pose.StartTick;
            int rate = FishNet.InstanceFinder.TimeManager != null
                ? (int)FishNet.InstanceFinder.TimeManager.TickRate : 30;
            return pose.StartTick > 0 && displayedTick >= pose.StartTick
                ? (float)((displayedTick - pose.StartTick) / Mathf.Max(1, rate)) : 0f;
        }

        private AnimancerState PlayThrowClip(AnimationClip clip)
        {
            if (clip == null || _poseFrozenForDeath || _animancer == null) return null;
            return _animancer.Layers[ActionLayer].Play(clip, actionFadeSeconds, FadeMode.FromStart);
        }

        private void PlayThrowHold()
        {
            var state = PlayThrowClip(_throwClips.Pose);
            if (state != null) { state.Time = state.Length * .5f; state.Speed = 0f; }
        }

        private void EnsureHeldThrowable(string itemId)
        {
            if (_heldThrowable != null && _heldThrowableItemId == itemId)
            {
                _heldThrowable.SetActive(true);
                return;
            }
            if (_heldThrowable != null) Destroy(_heldThrowable);
            _heldThrowable = null;
            var definition = Resources.Load<ThrowableCatalog>("ThrowableCatalog")?.Get(itemId);
            var hand = _animancer != null ? _animancer.Animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            if (hand == null || definition?.ModelPrefab == null) return;
            _heldThrowable = Instantiate(definition.ModelPrefab, hand, false);
            _heldThrowableItemId = itemId;
            _heldThrowable.name = "TP_HeldThrowable";
            _heldThrowable.transform.localPosition = new Vector3(0f, .035f, 0f);
            foreach (var item in _heldThrowable.GetComponentsInChildren<Transform>(true)) item.gameObject.layer = hand.gameObject.layer;
            foreach (var collider in _heldThrowable.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in _heldThrowable.GetComponentsInChildren<Rigidbody>(true))
            { body.isKinematic = true; body.detectCollisions = false; }
        }

        private void HideHeldThrowable()
        {
            if (_heldThrowable != null) _heldThrowable.SetActive(false);
        }

        private void ClearThrowableView()
        {
            _weaponSwapper?.SetThrowableHidden(false);
            if (_heldThrowable != null) Destroy(_heldThrowable);
            _heldThrowable = null;
            _throwPosePhase = ThrowablePosePhase.None;
        }

        // ---- 2026-09-18 审计 §4：TP 死亡/复活生命周期（IThirdPersonPoseLifecycle 实现）----

        /// <summary>死亡冻结闸：为真时 Update 不得写图，复活后必须显式重建。</summary>
        private bool _poseFrozenForDeath;
        /// <summary>需要一次无条件重播（复活 / 组件重新启用后图可能已被 Reset 停掉）。</summary>
        private bool _needsReapply;

        /// <summary>当前是否处于死亡冻结（诊断/测试：不得只靠"状态没变"推断姿态是否被写）。</summary>
        public bool PoseFrozenForDeath => _poseFrozenForDeath;
        /// <summary>缓存的 locomotion 状态（诊断/测试：复活必须把它清成无效值）。</summary>
        internal LocomotionState CachedLocomotionState => _currentState;
        /// <summary>Layer0 是否**真的在播**有效 locomotion（复核 R3 修正）。
        /// 旧实现只看 ChildCount&gt;0——未播放的 mixer、已停止的 clip 同样计数，会给出假阳性，
        /// 不能用来验收"复活后站桩立即恢复有效待机姿态"。现在要求：图已初始化 + Layer0 权重&gt;0
        /// + 确有在播的当前状态（Idle 单 clip 或 walk/run mixer）。</summary>
        public bool LocomotionPlaying
        {
            get
            {
                if (_animancer == null || !_animancer.IsGraphInitialized) return false;
                var layer = _animancer.Layers[LocomotionLayer];
                if (layer.Weight <= 0.001f) return false;
                if (_walkMixer != null && _walkMixer.IsPlaying) return true;
                if (_runMixer != null && _runMixer.IsPlaying) return true;
                return _locomotionClips.Idle != null && layer.IsPlayingClip(_locomotionClips.Idle);
            }
        }

        /// <summary>Layer0 当前子状态数（图增长断言用；未初始化图返回 0）。</summary>
        public int LocomotionLayerStateCount =>
            _animancer != null && _animancer.IsGraphInitialized ? _animancer.Layers[LocomotionLayer].ChildCount : 0;

        /// <summary>本驱动器建立过多少次方向混合器（复核 R2 回归：同一武器反复复活必须恒定，
        /// 只有换武器/换 clip 集才允许增长）。</summary>
        internal int LocomotionMixerCreations { get; private set; }

        /// <summary>
        /// 死亡冻结：**暂停图**而不是停用组件。prefab 里 AnimancerComponent 的
        /// `_ActionOnDisable=3`（DisableAction.Reset）在停用时执行 Graph.Stop + Animator.Rebind +
        /// PauseGraph，而 OnEnable 只 UnpauseGraph——图已被 Stop，复活后没有任何状态在播，
        /// 模型停在 Rebind 出来的骨架姿态上（实机"重生陷地"）。同 FPWeaponAnimator 的结论：
        /// 停用模拟不了冻结，暂停图才既保持姿态又不丢状态。
        /// </summary>
        private AnimancerState _deathState;
        public bool DeathPoseComplete => _deathState == null || _deathState.Time >= _deathState.Length;
        public void FreezePoseForDeath() => PlayDeathPose(0f);

        public void PlayDeathPose(float elapsedSeconds)
        {
            if (_poseFrozenForDeath) return;
            ClearThrowableView();
            _poseFrozenForDeath = true;
            if (_animancer == null) return;
            _animancer.Animator.applyRootMotion = false;
            _animancer.Layers[ActionLayer].Weight = 0f;
            var clip = Resources.Load<AnimationClip>("UI/TPDeath");
            if (clip == null)
            {
                Debug.LogError("Missing UI/TPDeath death animation", this);
                if (_animancer.IsGraphInitialized) _animancer.Graph.PauseGraph();
                return;
            }
            if (!_serverTickDriven) _animancer.Graph.UnpauseGraph();
            _deathState = _animancer.Layers[LocomotionLayer].Play(clip, 0.08f);
            _deathState.Time = Mathf.Clamp(elapsedSeconds, 0, clip.length);
            _deathState.Speed = elapsedSeconds >= clip.length ? 0 : 1;
            _animancer.Evaluate(0f);
        }

        /// <summary>
        /// 复活重建：解冻 → 清缓存（状态/方向平滑/步态相位/动作层）→ 按当前武器重解析 clip 集 →
        /// 无条件重播当前有效 locomotion（复活基线 Idle）→ 求值一帧。
        /// 只在"权威死亡状态真正切回存活"的边界调用一次（NCA 幂等闸保证），不得每帧执行。
        /// </summary>
        public void RecoverPoseAfterRespawn()
        {
            ClearThrowableView();
            bool wasFrozen = _poseFrozenForDeath;
            _poseFrozenForDeath = false;
            _deathState = null;
            ResetTransientAnimationState();
            if (_animancer == null) return;
            if (wasFrozen && !_serverTickDriven && _animancer.IsGraphInitialized) _animancer.Graph.UnpauseGraph();
            LoadClips(); // 死亡期间可能已换枪 → 按当前权威武器重建 clip 集
            ApplyState(true); // immediate：不做淡入，站桩也立刻有有效姿态
            if (_animancer.IsGraphInitialized) _animancer.Evaluate(0f); // 就地求值一帧
        }

        /// <summary>清掉一切"死亡前/停用前"的缓存与在途平滑，使重播不被状态缓存挡住（审计 §4.2）。</summary>
        private void ResetTransientAnimationState()
        {
            _currentState = (LocomotionState)(-1); // 无效值：下一帧必走 ApplyState
            _visualMove = Vector2.zero;
            _visualMoveVelocity = Vector2.zero;
            _remotePhase = 0f;
            _needsReapply = true;
            if (_animancer != null && _animancer.IsGraphInitialized)
                _animancer.Layers[ActionLayer].Weight = 0f;
        }

    }
}
