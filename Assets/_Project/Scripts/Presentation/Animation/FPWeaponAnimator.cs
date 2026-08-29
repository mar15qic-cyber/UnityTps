using System;
using Animancer;
using Game.Gameplay.Action;
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
        [SerializeField, Min(0f)] private float fireFadeSeconds = 0.04f;
        [SerializeField, Min(0f)] private float actionFadeSeconds = 0.12f;
        [SerializeField, Min(0f)] private float aimFadeSeconds = 0.08f;
        [SerializeField, Min(0f)] private float aimPoseFadeSeconds = 0.05f;

        /// <summary>动作版本号：每次 Reload/Switch 递增；阶段事件携带版本，回调校验失效即丢弃。</summary>
        public int CurrentActionVersion { get; private set; }

        /// <summary>换弹阶段事件（参数：类型、动作版本号）。订阅方校验版本 == CurrentActionVersion。</summary>
        public event Action<WeaponAnimEventType, int> OnAnimStage;

        private AnimancerComponent _animancer;
        private WeaponAnimationSet _clips;
        private bool _clipsReady;
        private bool _playedBeforeStart;
        private readonly FPAimAnimStateMachine _aimFsm = new();
        private bool _shotFiredThisFrame;
        private bool _dryFiredThisFrame;
        private bool _holsterRequestedThisFrame;
        private float _aimOutTimer;    // 收镜 clip 适配窗剩余（完成判定）
        private float _aimFireTimer;   // ADS 开火 clip 剩余时长

        private void Awake()
        {
            _animancer = GetComponent<AnimancerComponent>();
            _animancer.Animator.applyRootMotion = false;
            _animancer.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            if (actionSystem == null) actionSystem = GetComponentInParent<ActionSystem>();
            if (input == null) input = GetComponentInParent<InputReader>();
        }

        private void OnEnable()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (aimState == null) aimState = GetComponentInParent<PlayerAimState>();
            if (actionSystem == null) actionSystem = GetComponentInParent<ActionSystem>();
            if (input == null) input = GetComponentInParent<InputReader>();
            // 视图经 FPWeaponRig 池化复用（SetActive 切换）：标志位跨激活清零防串帧
            _shotFiredThisFrame = false;
            _dryFiredThisFrame = false;
            _holsterRequestedThisFrame = false;
            _aimOutTimer = 0f;
            _aimFireTimer = 0f;
            if (controller == null) return;
            controller.OnShotFired += HandleShot;
            controller.OnDryFire += HandleDryFire;
            controller.OnReloadStarted += HandleReloadStarted;
            controller.OnReloadCompleted += HandleReloadCompleted;
            controller.OnReloadInterrupted += HandleReloadInterrupted;
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
            if (controller == null) return;
            controller.OnShotFired -= HandleShot;
            controller.OnDryFire -= HandleDryFire;
            controller.OnReloadStarted -= HandleReloadStarted;
            controller.OnReloadCompleted -= HandleReloadCompleted;
            controller.OnReloadInterrupted -= HandleReloadInterrupted;
        }

        /// <summary>ADS 决策帧（Docs/18 §4.2）：采样本帧事实 → 状态机决策 → 执行指令。
        /// 执行顺序依赖：WeaponController(-50) 开火事件先于本 Update（默认序）到达，
        /// PlayerAimState(-80)/ActionSystem(-100) 的本帧值已就绪。</summary>
        private void Update()
        {
            if (controller == null) return;
            float dt = Time.deltaTime;
            if (_aimOutTimer > 0f) _aimOutTimer = Mathf.Max(0f, _aimOutTimer - dt);
            if (_aimFireTimer > 0f) _aimFireTimer = Mathf.Max(0f, _aimFireTimer - dt);

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
                holsterRequested: _holsterRequestedThisFrame));
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
                case FPAimAnimCommand.PlayHipFire: PlayFire(); break;
                case FPAimAnimCommand.Yield: break; // 换弹/切枪已由事件处理器接管主轨道
            }
        }

        /// <summary>开镜过渡：播速适配 ADS 过渡窗（clip.length → adsTransitionSeconds），
        /// 与 PlayerAimState.Ads01 斜坡同步完成——FOV 收敛与举枪动作同窗，无割裂。</summary>
        private void ExecuteAimIn()
        {
            if (!_clipsReady || _clips.AimIn == null) return;
            var state = _animancer.Play(_clips.AimIn, aimFadeSeconds, FadeMode.FromStart);
            state.Speed = FitToAdsWindow(_clips.AimIn.length);
        }

        /// <summary>收镜过渡：适配过渡窗 + OnEnd 回 Idle（恢复手臂 idle 微动；
        /// 被 holster/fire 替换时 OnEnd 不触发，由替换者接管）。完成判定走 _aimOutTimer。</summary>
        private void ExecuteAimOut()
        {
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

        private float FitToAdsWindow(float clipLength)
        {
            float window = aimState != null ? aimState.AdsTransitionSeconds : 0f;
            return window > 0f && clipLength > 0f ? clipLength / window : 1f;
        }

        /// <summary>换枪交换点：加载新武器 clip 集并播出枪动画。</summary>
        public void PlayDraw()
        {
            LoadClips();
            _aimFsm.ResetToHip(); // 切枪后从干净腰射态进入
            _playedBeforeStart = true;
            if (!_clipsReady) return;
            if (_clips.Draw != null)
                PlayAction(_clips.Draw);
            else if (_clips.Idle != null)
                _animancer.Play(_clips.Idle);
        }

        /// <summary>切枪开始：收旧枪动画。播完保持收枪末姿态直到交换点视图停用——
        /// 若 OnEnd 回 Idle，旧枪会在交换点前"放下又拿起"（交换点=(holsterTime+drawTime)*0.5，
        /// 长出枪武器如步枪 drawTime=1.37s 会把交换点拉后，空窗可达半秒）。
        /// 交换被打断时由 OnWeaponEquipped→PlayDraw 重播出枪兜底。</summary>
        public void PlayHolster()
        {
            LoadClips();
            _aimFsm.ResetToHip(); // 消除同帧竞争：收枪后不得再发 aim 指令覆盖收枪 clip
            _holsterRequestedThisFrame = true;
            _playedBeforeStart = true;
            if (!_clipsReady) return;
            if (_clips.Holster != null)
                _animancer.Play(_clips.Holster, actionFadeSeconds, FadeMode.FromStart);
        }

        /// <summary>开火事件只记标志：决策延迟到 Update 由状态机分流
        /// （WeaponController(-50) 先于本组件 Update，同帧消费）。</summary>
        private void HandleShot(WeaponShot _) => _shotFiredThisFrame = true;

        private void PlayFire()
        {
            if (!_clipsReady || _clips.Fire == null) return;
            var state = _animancer.Play(_clips.Fire, fireFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = PlayIdle;
        }

        private void HandleDryFire() => _dryFiredThisFrame = true;

        /// <summary>腰射空仓（aim 轨道外）：原有 DryFire 通道。</summary>
        private void PlayDryFire()
        {
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
            if (!_clipsReady || controller?.Runtime == null) return;
            bool wasOnAimTrack = _aimFsm.IsOnAimTrack;
            _aimFsm.ResetToHip(); // 换弹接管主轨道，aim 状态归零
            bool empty = controller.Runtime.CurrentAmmo == 0;
            AnimationClip clip = empty ? _clips.ReloadOutOfAmmo : _clips.ReloadAmmoLeft;
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
            state.Events(this).OnEnd = PlayIdle;

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

        private void RaiseStage(WeaponAnimEventType type, int version)
        {
            if (version != CurrentActionVersion) return; // 动作已被打断/替换——旧回调丢弃
            OnAnimStage?.Invoke(type, version);
        }

        private void HandleReloadInterrupted(Game.Gameplay.Action.ActionInterruptReason _)
        {
            CurrentActionVersion++; // 使在途阶段回调全部失效
            PlayIdle();
        }

        /// <summary>计时器到点即换弹完成（真相）；clip 播放速度已适配该窗口。</summary>
        private void HandleReloadCompleted()
        {
            CurrentActionVersion++; // 正常完成同样推进版本（OnEnd 已到，防御性失效）
            PlayIdle();
        }

        private void PlayIdle()
        {
            if (_clipsReady && _clips.Idle != null)
                _animancer.Play(_clips.Idle, actionFadeSeconds);
        }

        private void PlayAction(AnimationClip clip)
        {
            var state = _animancer.Play(clip, actionFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = PlayIdle;
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
        }
    }
}
