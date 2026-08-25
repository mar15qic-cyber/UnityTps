using Animancer;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// Day3 第一人称手臂动画唯一写者（Animancer 版）：WeaponController 事件 → clip 播放。
    /// clip 取自 WeaponDefinition.FirstPersonAnimations（数据驱动，无 AnimatorController）。
    /// 换弹完成仍由 ActionSystem 计时器决定，本类只做表现。
    /// </summary>
    [RequireComponent(typeof(AnimancerComponent))]
    public sealed class FPWeaponAnimator : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField, Min(0f)] private float fireFadeSeconds = 0.04f;
        [SerializeField, Min(0f)] private float actionFadeSeconds = 0.12f;

        private AnimancerComponent _animancer;
        private WeaponAnimationSet _clips;
        private bool _clipsReady;
        private bool _playedBeforeStart;

        private void Awake()
        {
            _animancer = GetComponent<AnimancerComponent>();
            _animancer.Animator.applyRootMotion = false;
            _animancer.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (controller == null) controller = GetComponentInParent<WeaponController>();
        }

        private void OnEnable()
        {
            if (controller == null) controller = GetComponentInParent<WeaponController>();
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

        /// <summary>换枪交换点：加载新武器 clip 集并播出枪动画。</summary>
        public void PlayDraw()
        {
            LoadClips();
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
            _playedBeforeStart = true;
            if (!_clipsReady) return;
            if (_clips.Holster != null)
                _animancer.Play(_clips.Holster, actionFadeSeconds, FadeMode.FromStart);
        }

        private void HandleShot(WeaponShot _) => PlayFire();

        private void PlayFire()
        {
            if (!_clipsReady || _clips.Fire == null) return;
            var state = _animancer.Play(_clips.Fire, fireFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = PlayIdle;
        }

        private void HandleDryFire()
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
            bool empty = controller.Runtime.CurrentAmmo == 0;
            AnimationClip clip = empty ? _clips.ReloadOutOfAmmo : _clips.ReloadAmmoLeft;
            if (clip == null) return;
            var state = _animancer.Play(clip, actionFadeSeconds, FadeMode.FromStart);
            // ActionSystem remains authoritative at Stat.ReloadTime. Fit the entire clip
            // into that window so the completion callback never cuts a long rifle reload.
            state.Speed = ReloadAnimationTiming.GetPlaybackSpeed(clip, controller.Stat.ReloadTime);
            state.Events(this).OnEnd = PlayIdle;
        }

        private void HandleReloadInterrupted(Game.Gameplay.Action.ActionInterruptReason _) => PlayIdle();

        /// <summary>计时器到点即换弹完成（真相）；clip 播放速度已适配该窗口。</summary>
        private void HandleReloadCompleted() => PlayIdle();

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
            _clips = controller.Definition.FirstPersonAnimations;
            _clipsReady = _clips.Idle != null || _clips.Fire != null;
        }
    }
}
