using Animancer;
using Game.Gameplay.Movement;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.Animation
{
    /// <summary>
    /// TP 表现的 locomotion 动画唯一写者（架构表A，Day3 Animancer 版）：
    /// Locomotor 状态 + 移动方向 → Layer0（全身）。
    /// Walk/Run 用 CartesianMixer 混 8 向 clip（参数 = 本地移动输入），Idle/Jump 离散播放。
    /// clip 集按武器族从 WeaponDefinition.ThirdPersonLocomotion 取，换武器时重建 mixer。
    /// 根运动经 RootMotionRelay 落地（Animator.applyRootMotion = true）。
    /// </summary>
    [RequireComponent(typeof(AnimancerComponent))]
    public class TPAnimDriver : MonoBehaviour
    {
        [SerializeField] private Locomotor locomotor;
        [SerializeField] private WeaponController controller;
        [SerializeField, Min(0f)] private float stateFadeSeconds = 0.15f;

        private const int LocomotionLayer = 0;

        private AnimancerComponent _animancer;
        private TpLocomotionSet _clips;

        private CartesianMixerState _walkMixer;
        private CartesianMixerState _runMixer;
        private LocomotionState _currentState = (LocomotionState)(-1);
        private bool _mixersValid;

        private void Awake()
        {
            _animancer = GetComponent<AnimancerComponent>();
            if (locomotor == null) locomotor = GetComponentInParent<Locomotor>();
            if (controller == null) controller = GetComponentInParent<WeaponController>();
            if (controller != null) controller.OnWeaponEquipped += HandleWeaponEquipped;
        }

        private void Start()
        {
            LoadClips();
            ApplyState(true);
        }

        private void OnDestroy()
        {
            if (controller != null) controller.OnWeaponEquipped -= HandleWeaponEquipped;
        }

        private void Update()
        {
            if (locomotor == null) return;
            if (locomotor.State != _currentState)
                ApplyState(false);
            UpdateMixerParameters();
        }

        private void ApplyState(bool immediate)
        {
            _currentState = locomotor.State;
            float fade = immediate ? 0f : stateFadeSeconds;
            if (!_mixersValid) return;

            switch (_currentState)
            {
                case LocomotionState.Idle:
                    if (_clips.Idle != null) _animancer.Layers[LocomotionLayer].Play(_clips.Idle, fade);
                    break;
                case LocomotionState.Jump:
                    if (_clips.JumpStart != null) _animancer.Layers[LocomotionLayer].Play(_clips.JumpStart, fade);
                    break;
                case LocomotionState.Air:
                    if (_clips.JumpLoop != null) _animancer.Layers[LocomotionLayer].Play(_clips.JumpLoop, fade);
                    break;
                case LocomotionState.Land:
                    if (_clips.JumpLand != null) _animancer.Layers[LocomotionLayer].Play(_clips.JumpLand, fade);
                    break;
                case LocomotionState.Sprint:
                    if (_runMixer != null) _animancer.Layers[LocomotionLayer].Play(_runMixer, fade);
                    break;
                default:
                    if (_walkMixer != null) _animancer.Layers[LocomotionLayer].Play(_walkMixer, fade);
                    break;
            }
        }

        private void UpdateMixerParameters()
        {
            if (locomotor == null) return;
            var move = locomotor.MoveInput;
            if (_walkMixer != null && _walkMixer.IsPlaying) _walkMixer.Parameter = move;
            if (_runMixer != null && _runMixer.IsPlaying) _runMixer.Parameter = move;
        }

        private void HandleWeaponEquipped(WeaponDefinition _)
        {
            LoadClips();
            ApplyState(false);
        }

        private void LoadClips()
        {
            if (controller == null || controller.Definition == null) return;
            _clips = controller.Definition.ThirdPersonLocomotion;
            _walkMixer = BuildDirectionalMixer(_clips.WalkForward, _clips.WalkForwardRight, _clips.WalkRight,
                _clips.WalkBackRight, _clips.WalkBackward, _clips.WalkBackLeft, _clips.WalkLeft, _clips.WalkForwardLeft);
            _runMixer = BuildDirectionalMixer(_clips.RunForward, _clips.RunForwardRight, _clips.RunRight,
                _clips.RunBackRight, _clips.RunBackward, _clips.RunBackLeft, _clips.RunLeft, _clips.RunForwardLeft);
            _mixersValid = _walkMixer != null || _runMixer != null || _clips.Idle != null;
        }

        /// <summary>8 向 clip → Cartesian mixer（参数空间：x=左右, y=前后；对角线阈值 0.707）。</summary>
        private CartesianMixerState BuildDirectionalMixer(
            AnimationClip forward, AnimationClip forwardRight, AnimationClip right, AnimationClip backRight,
            AnimationClip backward, AnimationClip backLeft, AnimationClip left, AnimationClip forwardLeft)
        {
            if (forward == null && right == null && backward == null && left == null) return null;

            var mixer = new CartesianMixerState();
            _animancer.Layers[LocomotionLayer].GetOrCreateState(mixer);
            const float d = 0.70710678f;
            if (forward != null) mixer.Add(forward, new Vector2(0f, 1f));
            if (forwardRight != null) mixer.Add(forwardRight, new Vector2(d, d));
            if (right != null) mixer.Add(right, new Vector2(1f, 0f));
            if (backRight != null) mixer.Add(backRight, new Vector2(d, -d));
            if (backward != null) mixer.Add(backward, new Vector2(0f, -1f));
            if (backLeft != null) mixer.Add(backLeft, new Vector2(-d, -d));
            if (left != null) mixer.Add(left, new Vector2(-1f, 0f));
            if (forwardLeft != null) mixer.Add(forwardLeft, new Vector2(-d, d));
            mixer.Parameter = Vector2.zero;
            return mixer;
        }
    }
}
