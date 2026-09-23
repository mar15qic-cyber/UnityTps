using System;
using Animancer;
using Game.Gameplay.Action;
using Game.Gameplay.Combat;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>换弹/切枪动画的阶段（归一化时间点由 WeaponAudioProfile 提供数据驱动时机）。</summary>
    public enum WeaponAnimEventType
    {
        MagOut,   // 弹匣抽出
        MagIn,    // 弹匣插入
        BoltRack, // 拉栓/上膛
    }

    /// <summary>
    /// Day3 第一人称手臂动画唯一写者（Animancer 版）：WeaponController 事件 → clip 播放。
    /// CP6 扩展：换弹阶段事件（MagOut/MagIn/BoltRack，归一化时间由 AudioProfile 数据驱动）
    /// + actionVersion 版本号——动作被打断后旧版本回调被丢弃（Docs/13 §5.3-7），
    /// 换弹/切枪中断不会误触发分阶段音频。
    /// Docs/18 扩展：动画 ADS 轨道。开火事件延迟到 Update 由 FPAimAnimStateMachine 决策
    /// （腰射 Fire / ADS AimFire 分流），aim_in/aim_out 播速适配 PlayerAimState 过渡窗
    /// （与 ReloadAnimationTiming 同一"clip 适配权威计时窗"模式）。
    /// </summary>
    [RequireComponent(typeof(AnimancerComponent))]
    public sealed class FPWeaponAnimator : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private PlayerAimState aimState;   // 只读 Ads01 / AdsTransitionSeconds
        [SerializeField] private ActionSystem actionSystem; // 只读 IsBusy（换弹/切枪互斥）
        [SerializeField] private InputReader input;         // 只读 AimHeld（开镜意图）
        [SerializeField] private FPWeaponPoseProfile poseProfile;
        [SerializeField, Min(0f)] private float fireFadeSeconds = 0.04f;
        [SerializeField, Min(0f)] private float actionFadeSeconds = 0.12f;
        [SerializeField, Min(0f)] private float aimFadeSeconds = 0.08f;
        [SerializeField, Min(0f)] private float aimPoseFadeSeconds = 0.05f;

        // ProceduralOnly deliberately keeps the gun out of AimFire.  The
        // authored clip still contains useful trigger/support-arm feedback,
        // so it is evaluated on a masked layer which cannot write the
        // Armature root, camera, or weapon branch.
        private const int ArmFeedbackLayer = 1;

        /// <summary>动作版本号：每次 Reload/Switch 递增；阶段事件携带版本，回调校验失效即丢弃。</summary>
        public int CurrentActionVersion { get; private set; }

        /// <summary>换弹阶段事件（参数：类型、动作版本号）。订阅方校验版本 == CurrentActionVersion。</summary>
        public event Action<WeaponAnimEventType, int> OnAnimStage;
        public event Action OnProceduralFire;
        public int ProceduralFireCount { get; private set; }

        private AnimancerComponent _animancer;
        private ThrowableController _throwables;
        private AnimancerState _throwState;
        private GameObject _heldThrowable;
        private float _throwStartedAt;
        private bool _throwPlaying;
        private readonly System.Collections.Generic.List<Renderer> _throwHidden = new();
        private WeaponAnimationSet _clips;
        private bool _clipsReady;
        private bool _playedBeforeStart;
        private readonly FPAimAnimStateMachine _aimFsm = new();
        private bool _shotFiredThisFrame;
        private bool _dryFiredThisFrame;
        private bool _holsterRequestedThisFrame;
        private float _aimOutTimer;    // 收镜 clip 适配窗剩余（完成判定）
        private float _aimFireTimer;   // ADS 开火 clip 剩余时长
        private AnimancerState _reloadState;
        private float _segmentedReloadStartedAt;
        private bool _segmentedReload;
        private AnimancerState _drawState;
        private AnimancerState _holsterState;
        private float _drawIdleBlendRemaining;
        private AnimancerLayer _armFeedbackLayer;
        private AvatarMask _armFeedbackMask;

        /// <summary>
        /// The presentation clock for reload. Gameplay still decides when
        /// ammo is committed, but all visual reload consumers read this same
        /// Animancer state rather than independently integrating ActionSystem.
        /// </summary>
        public bool HasReloadAnimationClock => _reloadState != null;
        public float CurrentReloadNormalizedTime => _reloadState == null
            ? 0f
            : _segmentedReload
                ? Mathf.Clamp01((Time.time - _segmentedReloadStartedAt) / Mathf.Max(.01f, controller.Stat.ReloadTime))
                : Mathf.Clamp01(_reloadState.NormalizedTime);

        /// <summary>
        /// True while a draw/holster state or its explicit draw-to-idle fade is
        /// still the owner of the arm pose. This is intentionally independent
        /// of the longer gameplay switch timer.
        /// </summary>
        public bool IsWeaponTransitionAnimationActive
            => (_drawState != null && (_drawState.IsPlaying || _drawState.Weight > .001f))
                || (_holsterState != null && (_holsterState.IsPlaying || _holsterState.Weight > .001f))
                || _drawIdleBlendRemaining > 0f;

        private void Awake()
        {
            _animancer = GetComponent<AnimancerComponent>();
            _animancer.Animator.applyRootMotion = false;
            _animancer.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (_throwables == null) _throwables = GetComponentInParent<ThrowableController>();
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            if (actionSystem == null) actionSystem = GetComponentInParent<ActionSystem>();
            if (input == null) input = GetComponentInParent<InputReader>();
            if (poseProfile == null) poseProfile = GetComponent<FPWeaponPoseProfile>();

            ConfigureArmFeedbackLayer();
        }

        private void OnEnable()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (_throwables == null) _throwables = GetComponentInParent<ThrowableController>();
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            if (actionSystem == null) actionSystem = GetComponentInParent<ActionSystem>();
            if (input == null) input = GetComponentInParent<InputReader>();
            // 视图经 FPWeaponRig 池化复用（SetActive 切换）：标志位跨激活清零防串帧
            _shotFiredThisFrame = false;
            _dryFiredThisFrame = false;
            _holsterRequestedThisFrame = false;
            _aimOutTimer = 0f;
            _aimFireTimer = 0f;
            _reloadState = null;
            _drawState = null;
            _holsterState = null;
            _drawIdleBlendRemaining = 0f;
            StopArmFeedback(0f);
            if (controller == null) return;
            controller.OnShotFired += HandleShot;
            controller.OnDryFire += HandleDryFire;
            controller.OnReloadStarted += HandleReloadStarted;
            controller.OnReloadCompleted += HandleReloadCompleted;
            controller.OnReloadInterrupted += HandleReloadInterrupted;
            if (_throwables != null) { _throwables.OnLocalThrowStarted += HandleThrowStarted; _throwables.OnSelectionChanged += HandleThrowableSelection; }
        }

        private void Start()
        {
            LoadClips();
            // FPWeaponRig 可能在 Start 前已播出枪动画（激活即 PlayDraw），此时不再用 Idle 覆盖
            if (!_playedBeforeStart && _clipsReady && _clips.Idle != null)
                _animancer.Play(_clips.Idle);
            _playedBeforeStart = false;
        }

        private void OnDisable()
        {
            CurrentActionVersion++;
            if (controller != null)
            {
                controller.OnShotFired -= HandleShot;
                controller.OnDryFire -= HandleDryFire;
                controller.OnReloadStarted -= HandleReloadStarted;
                controller.OnReloadCompleted -= HandleReloadCompleted;
                controller.OnReloadInterrupted -= HandleReloadInterrupted;
            }
            if (_throwables != null) { _throwables.OnLocalThrowStarted -= HandleThrowStarted; _throwables.OnSelectionChanged -= HandleThrowableSelection; }
            ClearThrowablePresentation();
            _reloadState = null;
            _drawState = null;
            _holsterState = null;
            _drawIdleBlendRemaining = 0f;
            StopArmFeedback(0f);
        }

        /// <summary>ADS 决策帧（Docs/18 §4.2）：采样本帧事实 → 状态机决策 → 执行指令。
        /// 执行顺序依赖：WeaponController(-50) 开火事件先于本 Update（默认序）到达，
        /// PlayerAimState(-80)/ActionSystem(-100) 的本帧值已就绪。</summary>
        private void Update()
        {
            if (controller == null) return;
            if (_throwables != null && (_throwables.IsEquipped || _throwPlaying))
            {
                if (_throwPlaying && _heldThrowable != null && Time.time - _throwStartedAt >= _throwables.ReleaseDelaySeconds)
                    _heldThrowable.SetActive(false);
                if (!_throwables.IsEquipped && !_throwables.IsThrowing) HandleThrowableSelection();
                _shotFiredThisFrame = _dryFiredThisFrame = _holsterRequestedThisFrame = false;
                return;
            }
            float dt = Time.deltaTime;
            if (_aimOutTimer > 0f) _aimOutTimer = Mathf.Max(0f, _aimOutTimer - dt);
            if (_aimFireTimer > 0f) _aimFireTimer = Mathf.Max(0f, _aimFireTimer - dt);
            if (_drawIdleBlendRemaining > 0f)
                _drawIdleBlendRemaining = Mathf.Max(0f, _drawIdleBlendRemaining - dt);

            float ads01 = aimState != null ? aimState.Ads01 : 0f;
            var command = _aimFsm.Tick(new FPAimAnimInput(
                aimHeld: input != null && input.AimHeld,
                ads01: ads01,
                actionBusy: actionSystem != null && actionSystem.IsBusy,
                shotFired: _shotFiredThisFrame,
                dryFired: _dryFiredThisFrame,
                aimInFinished: ads01 >= 0.95f,          // clip 已适配过渡窗：ads01 到高位 ≈ clip 播完
                aimOutFinished: _aimOutTimer <= 0f,
                aimFireFinished: _aimFireTimer <= 0f,
                holsterRequested: _holsterRequestedThisFrame,
                proceduralAdsFire: _proceduralAdsFire && ads01 > 0f));
            Execute(command);

            // 腰射空仓：aim 轨道外走既有 DryFire 通道；aim 态无素材保持贴腮姿势（T9）
            if (_dryFiredThisFrame && !_aimFsm.IsOnAimTrack)
                PlayDryFire();

            _shotFiredThisFrame = false;
            _dryFiredThisFrame = false;
            _holsterRequestedThisFrame = false;
        }

        private void Execute(FPAimAnimCommand command)
        {
            switch (command)
            {
                case FPAimAnimCommand.PlayAimIn: ExecuteAimIn(); break;
                case FPAimAnimCommand.PlayAimOut: ExecuteAimOut(); break;
                case FPAimAnimCommand.PlayAimIdle: ExecuteAimIdle(); break;
                case FPAimAnimCommand.PlayAimFire: ExecuteAimFire(); break;
                case FPAimAnimCommand.ProceduralFire: ExecuteProceduralFire(); break;
                case FPAimAnimCommand.PlayHipFire: PlayFire(); break;
                case FPAimAnimCommand.Yield: break; // 换弹/切枪已由事件处理器接管主轨道
            }
        }

        /// <summary>开镜过渡：播速适配 ADS 过渡窗（clip.length → adsTransitionSeconds），
        /// 与 PlayerAimState.Ads01 斜坡同步完成——FOV 收敛与举枪动作同窗，无割裂。</summary>
        private void ExecuteAimIn()
        {
            StopArmFeedback(aimFadeSeconds);
            if (!_clipsReady || _clips.AimIn == null) return;
            var state = _animancer.Play(_clips.AimIn, aimFadeSeconds, FadeMode.FromStart);
            state.Speed = FitToAdsWindow(_clips.AimIn.length);
        }

        /// <summary>收镜过渡：适配过渡窗 + OnEnd 回 Idle（恢复手臂 idle 微动；
        /// 被 holster/fire 替换时 OnEnd 不触发，由替换者接管）。完成判定走 _aimOutTimer。</summary>
        private void ExecuteAimOut()
        {
            StopArmFeedback(aimFadeSeconds);
            if (!_clipsReady || _clips.AimOut == null) return;
            var state = _animancer.Play(_clips.AimOut, aimFadeSeconds, FadeMode.FromStart);
            state.Speed = FitToAdsWindow(_clips.AimOut.length);
            float window = aimState != null ? aimState.AdsTransitionSeconds : 0f;
            _aimOutTimer = window > 0f ? window : _clips.AimOut.length;
            state.Events(this).OnEnd = PlayIdle;
        }

        /// <summary>ADS 保持姿势：aim_fire_pose 全曲线常量（探针证实），
        /// 一次性播放后 Animancer 结束态定格即静态保持。AimIdle 缺失回退 aim_in 末帧定格。</summary>
        private void ExecuteAimIdle()
        {
            StopArmFeedback(aimPoseFadeSeconds);
            if (!_clipsReady || _clips.AimIn == null) return;
            if (_clips.AimIdle != null)
            {
                _animancer.Play(_clips.AimIdle, aimPoseFadeSeconds);
            }
            else
            {
                var state = _animancer.Play(_clips.AimIn, aimPoseFadeSeconds);
                state.Time = _clips.AimIn.length; // 跳到片尾
                state.Speed = 0f;                 // 定格
            }
        }

        /// <summary>ADS 开火：正常速播（反馈时长=素材时长）。缺素材回退保持姿势，
        /// 后坐反馈仍由 FPWeaponMotion 弹簧承担。</summary>
        private void ExecuteAimFire()
        {
            if (!_clipsReady) return;
            if (_clips.AimFire != null)
            {
                _animancer.Play(_clips.AimFire, fireFadeSeconds, FadeMode.FromStart);
                _aimFireTimer = _clips.AimFire.length;
            }
            else
            {
                ExecuteAimIdle();
                _aimFireTimer = 0.1f; // 防状态机立即回 Aim 产生的指令抖动
            }
        }

        private void ExecuteProceduralFire()
        {
            // Keep the current AimIdle/aim-in gun pose.  The authored AimFire
            // clip is replayed only on the arm branches, while LPWGunPoseDriver
            // and the Cinemachine recoil extension consume the same shot event
            // exactly once.  This restores the visible trigger/support-arm
            // feedback without allowing the clip to fight the gun pose.
            PlayArmFeedback();
            ProceduralFireCount++;
            OnProceduralFire?.Invoke();
        }

        private float FitToAdsWindow(float clipLength)
        {
            float window = aimState != null ? aimState.AdsTransitionSeconds : 0f;
            return window > 0f && clipLength > 0f ? clipLength / window : 1f;
        }

        /// <summary>换枪交换点：加载新武器 clip 集并播出枪动画。</summary>
        public void PlayDraw()
        {
            StopArmFeedback(actionFadeSeconds);
            LoadClips();
            _aimFsm.ResetToHip(); // 切枪后从干净腰射态进入
            _holsterState = null;
            _drawIdleBlendRemaining = 0f;
            _playedBeforeStart = true;
            if (!_clipsReady) return;
            if (_clips.Draw != null)
            {
                _drawState = _animancer.Play(_clips.Draw, actionFadeSeconds, FadeMode.FromStart);
                // Arsenal's DrawTime is the gameplay transition window. Fit the
                // authored clip to that same window so IK ownership changes on
                // the real animation end rather than at a second, unrelated timer.
                _drawState.Speed = ReloadAnimationTiming.GetPlaybackSpeed(
                    _clips.Draw, controller.Definition.DrawTime);
                _drawState.Events(this).OnEnd = HandleDrawEnded;
            }
            else if (_clips.Idle != null)
                _animancer.Play(_clips.Idle);
        }

        /// <summary>切枪开始：收旧枪动画。播完保持收枪末姿态直到交换点视图停用——
        /// 若 OnEnd 回 Idle，旧枪会在交换点前"放下又拿起"（交换点=(holsterTime+drawTime)*0.5，
        /// 长出枪武器如步枪 drawTime=1.37s 会把交换点拉后，空窗可达半秒）。
        /// 交换被打断时由 OnWeaponEquipped→PlayDraw 重播出枪兜底。</summary>
        public void PlayHolster()
        {
            StopArmFeedback(actionFadeSeconds);
            LoadClips();
            _aimFsm.ResetToHip(); // 消除同帧竞争：收枪后不得再发 aim 指令覆盖收枪 clip
            _drawState = null;
            _drawIdleBlendRemaining = 0f;
            _holsterRequestedThisFrame = true;
            _playedBeforeStart = true;
            if (!_clipsReady) return;
            if (_clips.Holster != null)
            {
                _holsterState = _animancer.Play(_clips.Holster, actionFadeSeconds, FadeMode.FromStart);
                _holsterState.Events(this).OnEnd = null;
            }
        }

        /// <summary>开火事件只记标志：决策延迟到 Update 由状态机分流
        /// （WeaponController(-50) 先于本组件 Update，同帧消费）。</summary>
        private void HandleShot(WeaponShot _) => _shotFiredThisFrame = true;

        private void PlayFire()
        {
            StopArmFeedback(fireFadeSeconds);
            if (!_clipsReady || _clips.Fire == null) return;
            var state = _animancer.Play(_clips.Fire, fireFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = PlayIdle;
        }

        private void HandleDryFire() => _dryFiredThisFrame = true;

        /// <summary>腰射空仓（aim 轨道外）：原有 DryFire 通道。</summary>
        private void PlayDryFire()
        {
            StopArmFeedback(fireFadeSeconds);
            if (!_clipsReady) return;
            if (_clips.DryFire != null)
            {
                var state = _animancer.Play(_clips.DryFire, fireFadeSeconds, FadeMode.FromStart);
                state.Events(this).OnEnd = PlayIdle;
            }
            else PlayIdle();
        }

        private void HandleReloadStarted()
        {
            StopArmFeedback(actionFadeSeconds);
            if (!_clipsReady || controller?.Runtime == null) return;
            bool wasOnAimTrack = _aimFsm.IsOnAimTrack;
            _aimFsm.ResetToHip(); // 换弹接管主轨道，aim 状态归零
            bool empty = controller.Runtime.CurrentAmmo == 0;
            AnimationClip clip = empty ? _clips.ReloadOutOfAmmo : _clips.ReloadAmmoLeft;
            _segmentedReload = false;
            if (_clips.ReloadOpen != null && _clips.ReloadInsert != null && _clips.ReloadClose != null)
            {
                _segmentedReload = true;
                _segmentedReloadStartedAt = Time.time;
                int shells = Mathf.Max(1, Mathf.Min(controller.Runtime.MagazineSize - controller.Runtime.CurrentAmmo,
                    controller.Runtime.ReserveAmmo));
                float length = _clips.ReloadOpen.length + shells * _clips.ReloadInsert.length + _clips.ReloadClose.length;
                PlayReloadStage(0, shells, length / Mathf.Max(.01f, controller.Stat.ReloadTime), ++CurrentActionVersion);
                return;
            }
            if (clip == null)
            {
                // 分段换弹枪（Shotgun01/Sniper01 无整段 reload clip）：aim 姿态滞留防护——
                // 无动作 clip 接管时显式回腰射基线，否则枪会保持贴腮姿势贯穿整个换弹计时窗
                if (wasOnAimTrack && _clips.Idle != null) PlayIdle();
                return;
            }
            var state = _animancer.Play(clip, actionFadeSeconds, FadeMode.FromStart);
            // ActionSystem remains authoritative at Stat.ReloadTime. Fit the entire clip
            // into that window so the completion callback never cuts a long rifle reload.
            state.Speed = ReloadAnimationTiming.GetPlaybackSpeed(clip, controller.Stat.ReloadTime);
            _reloadState = state;
            // Do not let Animancer end the state and switch to Idle ahead of
            // the gameplay callback. The state remains the single visual
            // clock until ActionSystem commits the reload.
            state.Events(this).OnEnd = null;

            // CP6 分阶段事件：归一化时间点由 AudioProfile 数据驱动（版本号防打断误触发）
            var profile = controller.Definition.AudioProfile;
            if (profile != null) RegisterStageEvents(state, profile, ++CurrentActionVersion);
        }

        /// <summary>注册归一化时间阶段事件（MagOut/MagIn/BoltRack）。版本不匹配的回调被丢弃。</summary>
        private void RegisterStageEvents(AnimancerState state, WeaponAudioProfile profile, int version)
        {
            var events = state.Events(this);
            if (profile.MagOut.Clip != null && profile.MagOut.NormalizedTime > 0f)
                events.Add(profile.MagOut.NormalizedTime, () => RaiseStage(WeaponAnimEventType.MagOut, version));
            if (profile.MagIn.Clip != null && profile.MagIn.NormalizedTime > 0f)
                events.Add(profile.MagIn.NormalizedTime, () => RaiseStage(WeaponAnimEventType.MagIn, version));
            if (profile.BoltRack.Clip != null && profile.BoltRack.NormalizedTime > 0f)
                events.Add(profile.BoltRack.NormalizedTime, () => RaiseStage(WeaponAnimEventType.BoltRack, version));
        }

        private void PlayReloadStage(int stage, int shells, float speed, int version)
        {
            if (version != CurrentActionVersion || controller == null || controller.Runtime == null) return;
            var clip = stage == 0 ? _clips.ReloadOpen : stage <= shells ? _clips.ReloadInsert : _clips.ReloadClose;
            _reloadState = _animancer.Play(clip, .04f, FadeMode.FromStart);
            _reloadState.Time = 0f;
            _reloadState.Speed = speed;
            if (stage == 0) RaiseStage(WeaponAnimEventType.MagOut, version);
            else if (stage <= shells) RaiseStage(WeaponAnimEventType.MagIn, version);
            else RaiseStage(WeaponAnimEventType.BoltRack, version);
            _reloadState.Events(this).OnEnd = stage <= shells
                ? () => PlayReloadStage(stage + 1, shells, speed, version)
                : null;
        }

        private void RaiseStage(WeaponAnimEventType type, int version)
        {
            if (version != CurrentActionVersion) return; // 动作已被打断/替换——旧回调丢弃
            OnAnimStage?.Invoke(type, version);
        }

        private void HandleReloadInterrupted(Game.Gameplay.Action.ActionInterruptReason _)
        {
            CurrentActionVersion++; // 使在途阶段回调全部失效
            _reloadState = null;
            StopArmFeedback(actionFadeSeconds);
            PlayIdle();
        }

        /// <summary>计时器到点即换弹完成（真相）；clip 播放速度已适配该窗口。</summary>
        private void HandleReloadCompleted()
        {
            CurrentActionVersion++; // 正常完成同样推进版本（OnEnd 已到，防御性失效）
            _reloadState = null;
            StopArmFeedback(actionFadeSeconds);
            PlayIdle();
        }

        /// <summary>腰射基线（切枪/动作收尾/复活重建共用）。</summary>
        private void PlayIdle()
        {
            StopArmFeedback(actionFadeSeconds);
            if (_clipsReady && _clips.Idle != null)
                _animancer.Play(_clips.Idle, actionFadeSeconds);
        }

        // ---- 2026-09-16 审计 §4：Owner FP 死亡/复活显式入口（表现层唯一所有者）----

        /// <summary>
        /// 死亡边界：停掉本帧行为、作废在途动作回调、把图**暂停**在当前姿态。
        /// 为什么不用 Disable 模拟冻结：FP 视图 prefab 的 AnimancerComponent `_ActionOnDisable=0`
        /// （DisableAction.Stop）会 Stop(+PauseGraph)，而 Stop 会重置状态选择——"停用即冻结死亡瞬间姿态"
        /// 的假设不成立；且重新启用恢复的是"图在跑"，不是"重新选中腰射 Idle"（旧实现只还原 enabled）。
        /// 暂停图则保持姿态且不丢状态，复活时由 ApplyRespawnState 明确重建。
        /// </summary>
        public void ApplyDeathState()
        {
            ClearTransientActionState();
            if (_animancer != null && _animancer.IsGraphInitialized)
                _animancer.Graph.PauseGraph();
        }

        /// <summary>
        /// 复活边界：清掉死亡前 Fire/Reload/Draw/Holster/ADS 的过期状态与回调，按**当前权威武器**
        /// 重新解析 clip 集并重建腰射 Idle，评估一次后再交还可见性（FPWeaponRig 负责显示）。
        /// 这是"死亡后必须重新按 ADS 才恢复枪位"的直接修复：旧实现只恢复 enabled，Animancer 图
        /// 停在死亡瞬间的 ADS/开火姿态，没有腰射 Idle 重建路径。
        /// </summary>
        public void ApplyRespawnState()
        {
            ClearTransientActionState();
            if (_animancer == null) return;
            if (_animancer.IsGraphInitialized) _animancer.Graph.UnpauseGraph();
            LoadClips(); // 死亡期间可能已换枪/换配件 → 按当前定义重建 clip 集与路由
            _playedBeforeStart = false;
            if (_clipsReady && _clips.Idle != null)
            {
                _animancer.Play(_clips.Idle, 0f);
                _animancer.Evaluate(0f);
            }
        }

        /// <summary>作废一切"死亡前已排定"的瞬时动作状态（flags/计时/OnEnd 回调/阶段事件版本）。</summary>
        private void ClearTransientActionState()
        {
            _shotFiredThisFrame = false;
            _dryFiredThisFrame = false;
            _holsterRequestedThisFrame = false;
            _aimOutTimer = 0f;
            _aimFireTimer = 0f;
            _drawIdleBlendRemaining = 0f;
            _reloadState = null;
            _drawState = null;
            _holsterState = null;
            CurrentActionVersion++;   // 在途 MagOut/MagIn/BoltRack 回调全部失效
            _aimFsm.ResetToHip();
            StopArmFeedback(0f);
        }

        private void HandleDrawEnded()
        {
            _drawState = null;
            _drawIdleBlendRemaining = actionFadeSeconds;
            PlayIdle();
        }

        private void LoadClips()
        {
            if (controller == null || controller.Definition == null) return;
            // WeaponDefinition resolves the rifle family here. This keeps the
            // existing Animancer consumer unchanged while ensuring a vertical-
            // grip rifle can select the authored rifle02/rifle03 set; SMG and
            // pistol definitions still return their native set.
            _clips = controller.Definition.FirstPersonAnimations;
            _clipsReady = _clips.Idle != null || _clips.Fire != null;
            _aimFsm.SetHasAimClips(_clips.HasAimClips);
            _proceduralAdsFire = RoutesToProceduralAdsFire(poseProfile);
            _aimFsm.SetProceduralAdsFire(_proceduralAdsFire);
        }

        private void HandleThrowableSelection()
        {
            ClearThrowablePresentation();
            _aimFsm.ResetToHip();
            if (_throwables == null || !_throwables.IsEquipped) { PlayIdle(); return; }
            if (!_clipsReady) LoadClips();
            StopArmFeedback(0f);
            if (_clips.ThrowGrenade == null) return;
            _throwState = _animancer.Play(_clips.ThrowGrenade, actionFadeSeconds, FadeMode.FromStart);
            _throwState.Time = _clips.ThrowGrenade.length * 0.35f;
            _throwState.Speed = 0f;
            var hand = transform.Find("Armature/arm_L/lower_arm_L/hand_L");
            if (hand != null && _throwables.SelectedDefinition != null)
            {
                _heldThrowable = Instantiate(_throwables.SelectedDefinition.ModelPrefab, hand, false);
                _heldThrowable.name = "HeldThrowable";
                _heldThrowable.transform.localPosition = HeldThrowablePosition(_heldThrowable);
                foreach (var t in _heldThrowable.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = gameObject.layer;
                foreach (var c in _heldThrowable.GetComponentsInChildren<Collider>()) c.enabled = false;
            }
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || r.name == "arms" || _heldThrowable != null && r.transform.IsChildOf(_heldThrowable.transform)) continue;
                r.enabled = false; _throwHidden.Add(r);
            }
        }

        internal static Vector3 HeldThrowablePosition(GameObject model)
        {
            // hand_L is the wrist, not the palm centre. Seat the body between
            // the curled fingers and thumb, outside the palm's negative-X face.
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool haveBounds = false;
            foreach (var mesh in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mesh.sharedMesh == null) continue;
                var b = mesh.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1,
                        (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = model.transform.InverseTransformPoint(mesh.transform.TransformPoint(corner));
                    if (!haveBounds) { bounds = new Bounds(p, Vector3.zero); haveBounds = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return new Vector3(-bounds.extents.x - .006f, .082f, .013f) - bounds.center;
        }

        private void ClearThrowablePresentation()
        {
            _throwPlaying = false; _throwState = null;
            if (_heldThrowable != null) { _heldThrowable.SetActive(false); Destroy(_heldThrowable); _heldThrowable = null; }
            foreach (var r in _throwHidden) if (r != null) r.enabled = true;
            _throwHidden.Clear();
        }

        private void HandleThrowStarted(ThrowableType _)
        {
            if (!_clipsReady) LoadClips();
            if (_clips.ThrowGrenade == null)
            {
                Debug.LogError($"[FPWeaponAnimator] grenade_throw clip missing for {controller?.Definition?.name}", this);
                return;
            }
            _throwPlaying = true;
            _throwStartedAt = Time.time;
            _aimFsm.ResetToHip();
            StopArmFeedback(actionFadeSeconds);
            var state = _animancer.Play(_clips.ThrowGrenade, actionFadeSeconds, FadeMode.FromStart);
            if (_throwables.ThrowActionSeconds <= 0f)
            {
                Debug.LogError("[FPWeaponAnimator] throw action duration missing", this);
                return;
            }
            // Selection already raised the grenade to this pose. Replaying the wind-up
            // from frame zero makes a click pull the hand back before it can release.
            state.Time = _clips.ThrowGrenade.length * .35f;
            state.Speed = _clips.ThrowGrenade.length * .65f / _throwables.ThrowActionSeconds;
            state.Events(this).OnEnd = () => { if (_throwables != null) _throwables.Unequip(); HandleThrowableSelection(); };
        }

        /// <summary>
        /// Creates the one runtime mask needed by ProceduralOnly LPW views.
        /// The LPW meshes keep the weapon under Armature/weapon while the two
        /// authored arm chains remain direct Armature children, so masking the
        /// chains is sufficient to exclude every gun/root transform and all
        /// authored sight/camera displacement.
        /// </summary>
        private void ConfigureArmFeedbackLayer()
        {
            if (_animancer == null) return;

            _armFeedbackLayer = _animancer.Layers[ArmFeedbackLayer];
            _armFeedbackLayer.IsAdditive = false;
            _armFeedbackLayer.Weight = 0f;

            _armFeedbackMask = new AvatarMask();
            _armFeedbackMask.hideFlags = HideFlags.HideAndDontSave;
            // A transform mask must contain the Animator root and the complete
            // hierarchy.  A sparse mask made only from arm_L/arm_R has no empty
            // root entry and Unity does not reliably bind its Generic curves.
            // Build the same shape as the imported LPFP mask assets, then keep
            // only the authored arm branches active.  The empty root path stays
            // active as the binding anchor; Armature, weapon, camera, magazine,
            // sight and mesh branches remain explicitly disabled.
            _armFeedbackMask.AddTransformPath(transform, true);
            bool hasArmPath = false;
            for (int i = 0; i < _armFeedbackMask.transformCount; i++)
            {
                string path = _armFeedbackMask.GetTransformPath(i);
                bool active = string.IsNullOrEmpty(path) || IsArmFeedbackPath(path);
                _armFeedbackMask.SetTransformActive(i, active);
                hasArmPath |= !string.IsNullOrEmpty(path) && active;
            }
            if (!hasArmPath)
            {
                Destroy(_armFeedbackMask);
                _armFeedbackMask = null;
                return;
            }
            _animancer.Layers.SetMask(ArmFeedbackLayer, _armFeedbackMask);
        }

        /// <summary>Replays AimFire on the arm-only layer for one shot.</summary>
        private void PlayArmFeedback()
        {
            if (!_proceduralAdsFire || !_clipsReady || _clips.AimFire == null
                || _armFeedbackLayer == null || _armFeedbackMask == null)
                return;

            _armFeedbackLayer.Weight = 1f;
            var state = _armFeedbackLayer.Play(_clips.AimFire, fireFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = HandleArmFeedbackEnded;
        }

        private void HandleArmFeedbackEnded() => StopArmFeedback(fireFadeSeconds);

        /// <summary>
        /// Removes the arm-only overlay before another owner (aim, reload,
        /// holster, or hip fire) takes the animation graph back.
        /// </summary>
        private void StopArmFeedback(float fadeSeconds = 0f)
        {
            if (_armFeedbackLayer == null) return;
            if (fadeSeconds <= 0f)
            {
                _armFeedbackLayer.Weight = 0f;
                return;
            }
            _armFeedbackLayer.StartFade(0f, fadeSeconds);
        }

        private static bool IsArmFeedbackPath(string path)
        {
            return path == "Armature/arm_L"
                || path.StartsWith("Armature/arm_L/", StringComparison.Ordinal)
                || path == "Armature/arm_R"
                || path.StartsWith("Armature/arm_R/", StringComparison.Ordinal);
        }

        /// <summary>ADS 开火路由（抖动修复核心）：资产模式 ProceduralOnly 本身决定路由，
        /// 不再要求 HasCompleteAnchoredDualPoseV2——Legacy 视图同样进入程序化开火。
        /// 否则 28 把 Legacy 枪每发以 FadeMode.FromStart 重启 AimFire，
        /// 与程序化弹簧在不同坐标空间和更新阶段争夺姿态（抖动/漂移根因之一）。
        /// 无 Profile 的原生 LPFP 武器保持 LegacyAimFire（作者动画即反馈）。</summary>
        public static bool RoutesToProceduralAdsFire(FPWeaponPoseProfile profile)
            => profile != null
                && profile.AdsFirePresentationMode == LPWAdsFirePresentationMode.ProceduralOnly;

        private bool _proceduralAdsFire;
    }
}
