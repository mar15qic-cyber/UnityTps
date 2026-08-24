using Animancer;
using Game.Gameplay.Action;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP 武器动作动画唯一写者（Day3 核心：FP/TP 事件同步）。
    /// 订阅与 FPWeaponAnimator 完全相同的 WeaponController 事件流，翻译成
    /// Layer1（上半身 AvatarMask）上的 TP clip 播放——跑动中开枪/换弹时腿部
    /// locomotion 不受影响。真相同源：换弹完成由 ActionSystem 计时器决定。
    /// Day7 联网时远端实例的事件源换成服务器广播，本类接口不变。
    /// </summary>
    [RequireComponent(typeof(AnimancerComponent))]
    public sealed class TPWeaponAnimDriver : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private AvatarMask upperBodyMask;
        [SerializeField, Min(0f)] private float fireFadeSeconds = 0.05f;
        [SerializeField, Min(0f)] private float actionFadeSeconds = 0.15f;
        [SerializeField, Min(0f)] private float layerFadeOutSeconds = 0.2f;

        private const int ActionLayer = 1;

        private AnimancerComponent _animancer;
        private TpActionSet _clips;
        private bool _clipsReady;

        private void Awake()
        {
            _animancer = GetComponent<AnimancerComponent>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            var layer = _animancer.Layers[ActionLayer];
            if (upperBodyMask != null)
                _animancer.Layers.SetMask(ActionLayer, upperBodyMask);
            layer.Weight = 0f;
            layer.IsAdditive = false;
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
            controller.OnWeaponEquipped += HandleWeaponEquipped;
        }

        private void Start() => LoadClips();

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnShotFired -= HandleShot;
            controller.OnDryFire -= HandleDryFire;
            controller.OnReloadStarted -= HandleReloadStarted;
            controller.OnReloadCompleted -= HandleReloadCompleted;
            controller.OnReloadInterrupted -= HandleReloadInterrupted;
            controller.OnWeaponEquipped -= HandleWeaponEquipped;
        }

        private void HandleShot(WeaponShot _)
        {
            if (!_clipsReady || _clips.Fire == null) return;
            var state = _animancer.Layers[ActionLayer].Play(_clips.Fire, fireFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = FadeOutActionLayer;
        }

        private void HandleDryFire() => FadeOutActionLayer();

        private void HandleReloadStarted()
        {
            if (!_clipsReady || controller?.Runtime == null) return;
            bool empty = controller.Runtime.CurrentAmmo == 0;
            AnimationClip clip = empty ? _clips.ReloadOutOfAmmo : _clips.ReloadAmmoLeft;
            if (clip == null) return;
            var state = _animancer.Layers[ActionLayer].Play(clip, actionFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = FadeOutActionLayer;
        }

        private void HandleReloadInterrupted(ActionInterruptReason _) => FadeOutActionLayer();

        /// <summary>计时器到点即换弹完成（真相），动画未播完也强制切回（架构 §6.5）。</summary>
        private void HandleReloadCompleted() => FadeOutActionLayer();

        private void HandleWeaponEquipped(WeaponDefinition _) => FadeOutActionLayer();

        private void FadeOutActionLayer()
        {
            _animancer.Layers[ActionLayer].StartFade(0f, layerFadeOutSeconds);
        }

        private void LoadClips()
        {
            if (controller == null || controller.Definition == null) return;
            _clips = controller.Definition.ThirdPersonActions;
            _clipsReady = _clips.Fire != null || _clips.ReloadAmmoLeft != null;
        }
    }
}
