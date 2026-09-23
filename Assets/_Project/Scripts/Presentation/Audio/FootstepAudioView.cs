using FishNet;
using Game.Core;
using Game.Gameplay.Combat;
using Game.Gameplay.Movement;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using UnityEngine;

namespace Game.Presentation.Audio
{
    /// <summary>本地 2D、远端 3D 步态循环，直接消费已同步的移动状态。</summary>
    public sealed class FootstepAudioView : MonoBehaviour
    {
        private CombatAudioConfig _config;
        private NetworkCombatAuthority _combat;
        private NetworkLocomotionState _gait;
        private AudioSource _source;
        private AudioSource _throwSource;
        private ThrowableController _throwables;
        private bool _local;

        private void Awake()
        {
            if (Application.isBatchMode) { enabled = false; return; }
            _config = Resources.Load<CombatAudioConfig>("CombatAudioConfig");
            if (_config == null || !_config.IsValid)
            { Debug.LogError("[FootstepAudioView] CombatAudioConfig missing or invalid", this); enabled = false; return; }
            _combat = GetComponent<NetworkCombatAuthority>();
            _throwables = GetComponent<ThrowableController>();
            _gait = GetComponent<NetworkLocomotionState>();
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.minDistance = 1.5f;
            _source.maxDistance = _config.FootstepMaxDistance;
            _throwSource = gameObject.AddComponent<AudioSource>();
            _throwSource.playOnAwake = false;
            _throwSource.rolloffMode = AudioRolloffMode.Linear;
            _throwSource.minDistance = 1.5f;
            _throwSource.maxDistance = _config.FootstepMaxDistance;
        }

        private void OnEnable()
        {
            if (_throwables == null) _throwables = GetComponent<ThrowableController>();
            if (_throwables != null) _throwables.OnLocalThrowStarted += PlayThrow;
        }

        private void OnDisable()
        {
            if (_throwables != null) _throwables.OnLocalThrowStarted -= PlayThrow;
        }

        private void PlayThrow(ThrowableType type)
        {
            var catalog = Resources.Load<ThrowableCatalog>("ThrowableCatalog");
            var clip = catalog?.Get(type)?.ThrowClip;
            if (_throwSource != null && clip != null)
                _throwSource.PlayOneShot(clip, AudioBus.ComputeVolume(0.8f, AudioBus.Category.Sfx));
        }

        private void Update()
        {
            if (_source == null || _gait == null) return;
            _local = _combat == null || _combat.IsOwnerPlayer;
            _source.spatialBlend = _local ? 0f : 1f;
            _throwSource.spatialBlend = _source.spatialBlend;
            var state = _gait.State;
            bool moving = state == LocomotionState.Walk || state == LocomotionState.Sprint;
            bool allowed = moving && _gait.HorizontalSpeed >= _config.WalkMinSpeed
                && (_combat == null || !_combat.IsDead) && !MatchLifecycle.InputFrozen
                && MatchLifecycle.Phase != MatchPhase.Ended;
            if (!allowed) { if (_source.isPlaying) _source.Stop(); return; }
            // 静步对其他玩家无脚步声；本地也不播放循环，保留投掷音效所在的 AudioSource。
            if (state == LocomotionState.Walk)
            {
                if (_source.isPlaying) _source.Stop();
                return;
            }
            bool sprint = state == LocomotionState.Sprint && _gait.HorizontalSpeed >= _config.SprintMinSpeed;
            var clip = sprint ? _config.RunningLoop : _config.WalkingLoop;
            if (_source.clip != clip)
            {
                _source.Stop();
                _source.clip = clip;
            }
            _source.volume = AudioBus.ComputeVolume(_local ? _config.LocalFootstepVolume
                : _config.RemoteFootstepVolume, AudioBus.Category.Sfx);
            if (!_source.isPlaying && clip != null) _source.Play();
        }
    }

}
