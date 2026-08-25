using Animancer;
using Game.Gameplay.Action;
using Game.Gameplay.Movement;
using Game.Gameplay.Player;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP Animator 的唯一动画写者：Layer0 locomotion + Layer1 weapon action。
    /// 只把 PlayerStateView 与 Gameplay 事件翻译成 Animancer 播放，不修改 Gameplay 真相。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(AnimancerComponent))]
    public sealed class TPAnimDriver : MonoBehaviour
    {
        [SerializeField] private PlayerStateView stateView;
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
        private CartesianMixerState _walkMixer;
        private CartesianMixerState _runMixer;
        private LocomotionState _currentState = (LocomotionState)(-1);
        private Vector2 _visualMove;
        private Vector2 _visualMoveVelocity;
        private bool _mixersValid;
        private bool _actionClipsReady;

        private void Awake()
        {
            _animancer = GetComponent<AnimancerComponent>();
            _animancer.Animator.applyRootMotion = false;
            _animancer.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (stateView == null) stateView = GetComponentInParent<PlayerStateView>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();

            var actionLayer = _animancer.Layers[ActionLayer];
            if (upperBodyMask != null) _animancer.Layers.SetMask(ActionLayer, upperBodyMask);
            actionLayer.Weight = 0f;
            actionLayer.IsAdditive = false;
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

        private void Start()
        {
            LoadClips();
            ApplyState(true);
        }

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

        private void Update()
        {
            if (stateView == null) return;
            if (stateView.LocomotionState != _currentState) ApplyState(false);
            UpdateMixerParametersAndPhase();
        }

        private void ApplyState(bool immediate)
        {
            if (stateView == null) return;
            _currentState = stateView.LocomotionState;
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

        private void UpdateMixerParametersAndPhase()
        {
            Vector2 target = stateView.MoveInput;
            _visualMove = visualDirectionSmoothTime <= 0f
                ? target
                : Vector2.SmoothDamp(
                    _visualMove,
                    target,
                    ref _visualMoveVelocity,
                    visualDirectionSmoothTime,
                    Mathf.Infinity,
                    Time.deltaTime);

            float phase = stateView.GaitPhase;
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

        private void HandleWeaponEquipped(WeaponDefinition _)
        {
            LoadClips();
            FadeOutActionLayer();
            ApplyState(false);
        }

        private void LoadClips()
        {
            if (controller == null || controller.Definition == null) return;
            _locomotionClips = controller.Definition.ThirdPersonLocomotion;
            _actionClips = controller.Definition.ThirdPersonActions;

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
        }

        private CartesianMixerState BuildDirectionalMixer(
            AnimationClip forward, AnimationClip forwardRight, AnimationClip right, AnimationClip backRight,
            AnimationClip backward, AnimationClip backLeft, AnimationClip left, AnimationClip forwardLeft)
        {
            if (forward == null && right == null && backward == null && left == null) return null;

            var mixer = new CartesianMixerState();
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
            if (!_actionClipsReady || _actionClips.Fire == null) return;
            var state = _animancer.Layers[ActionLayer].Play(_actionClips.Fire, fireFadeSeconds, FadeMode.FromStart);
            state.Events(this).OnEnd = FadeOutActionLayer;
        }

        private void HandleDryFire() => FadeOutActionLayer();

        private void HandleReloadStarted()
        {
            if (!_actionClipsReady || controller?.Runtime == null) return;
            AnimationClip clip = controller.Runtime.CurrentAmmo == 0
                ? _actionClips.ReloadOutOfAmmo
                : _actionClips.ReloadAmmoLeft;
            if (clip == null) return;
            var state = _animancer.Layers[ActionLayer].Play(clip, actionFadeSeconds, FadeMode.FromStart);
            // ActionSystem remains authoritative at Stat.ReloadTime. Fit the entire clip
            // into that window so the completion callback never cuts a long rifle reload.
            state.Speed = ReloadAnimationTiming.GetPlaybackSpeed(clip, controller.Stat.ReloadTime);
            state.Events(this).OnEnd = FadeOutActionLayer;
        }

        private void HandleReloadInterrupted(ActionInterruptReason _) => FadeOutActionLayer();
        private void HandleReloadCompleted() => FadeOutActionLayer();

        private void FadeOutActionLayer()
        {
            _animancer.Layers[ActionLayer].StartFade(0f, layerFadeOutSeconds);
        }

    }
}
