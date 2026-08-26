using System.Collections.Generic;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using UnityEngine;
using UnityEngine.Audio;

namespace Game.Presentation.HUD
{
    /// <summary>
    /// CP6 武器音频唯一播放者（Docs/13 检查点 6）：订阅 WeaponController/Arsenal/FPWeaponAnimator
    /// 事件 → AudioClip 播放。Gameplay 层零 AudioSource 引用；配置全部来自
    /// WeaponDefinition.AudioProfile（族共享）。
    /// 池：Fire 用 3 路轮换 OneShot（高速连射不截断）；音量/音高随机；本地 2D 混合
    /// （远端 3D 预留 anchor）。换弹分阶段经 OnAnimStage（版本号校验，中断不误触发）。
    /// 缺失 clip 静默（Editor Validator 报告，§9-11 拍板：不用近义 clip 假装完成）。
    /// </summary>
    public sealed class WeaponAudioView : MonoBehaviour
    {
        [SerializeField] private WeaponController controller;
        [SerializeField] private Arsenal arsenal;
        [Tooltip("各武器 FP 视图上的动画器（换弹分阶段事件源）；空=自动收集")]
        [SerializeField] private FPWeaponAnimator[] animators;

        [Header("Fire 轮换池")]
        [SerializeField, Min(1)] private int fireVoiceCount = 3;

        private readonly List<AudioSource> _fireVoices = new();
        private int _fireVoiceIndex;
        private WeaponAudioProfile _profile;

        private void Awake()
        {
            if (controller == null) controller = FindObjectOfType<WeaponController>();
            if (arsenal == null) arsenal = FindObjectOfType<Arsenal>();
            if (animators == null || animators.Length == 0)
                animators = FindObjectsOfType<FPWeaponAnimator>();
            BuildFireVoices();
        }

        private void OnEnable()
        {
            if (controller == null) return;
            controller.OnShotFired += HandleShot;
            controller.OnDryFire += HandleDryFire;
            controller.OnReloadStarted += HandleReloadStarted;
            controller.OnWeaponEquipped += HandleWeapon;
            if (arsenal != null)
            {
                arsenal.OnSwitchStarted += HandleSwitchStarted;
                arsenal.OnSwitchCompleted += HandleSwitchCompleted;
            }
            if (animators != null)
                foreach (var a in animators)
                    if (a != null) a.OnAnimStage += HandleAnimStage;
            RefreshProfile();
        }

        private void OnDisable()
        {
            if (controller == null) return;
            controller.OnShotFired -= HandleShot;
            controller.OnDryFire -= HandleDryFire;
            controller.OnReloadStarted -= HandleReloadStarted;
            controller.OnWeaponEquipped -= HandleWeapon;
            if (arsenal != null)
            {
                arsenal.OnSwitchStarted -= HandleSwitchStarted;
                arsenal.OnSwitchCompleted -= HandleSwitchCompleted;
            }
            if (animators != null)
                foreach (var a in animators)
                    if (a != null) a.OnAnimStage -= HandleAnimStage;
        }

        // ---------------- 事件 → 音频 ----------------

        private void HandleShot(WeaponShot shot) => PlayFire();

        private void HandleDryFire() => PlayEntry(_profile != null ? _profile.DryFire : default, "DryFire");

        private void HandleReloadStarted()
        {
            // 整段换弹音（分阶段经 HandleAnimStage；两轨并存时整段作底噪）
            if (_profile == null) return;
            bool empty = controller.Runtime != null && controller.Runtime.CurrentAmmo == 0;
            PlayEntry(empty ? _profile.ReloadOutOfAmmo : _profile.ReloadAmmoLeft, empty ? "ReloadEmpty" : "ReloadLeft");
        }

        private void HandleAnimStage(WeaponAnimEventType type, int version)
        {
            if (_profile == null) return;
            var stage = type switch
            {
                WeaponAnimEventType.MagOut => _profile.MagOut,
                WeaponAnimEventType.MagIn => _profile.MagIn,
                WeaponAnimEventType.BoltRack => _profile.BoltRack,
                _ => default,
            };
            if (stage.Clip != null) PlayClip(stage.Clip, 1f, 1f);
        }

        private void HandleSwitchStarted(Game.Gameplay.Weapon.WeaponDefinition oldWeapon, int _)
            => PlayEntry(_profile != null ? _profile.Holster : default, "Holster");

        private void HandleSwitchCompleted(Game.Gameplay.Weapon.WeaponDefinition newWeapon)
            => PlayEntry(_profile != null ? _profile.Draw : default, "Draw");

        private void HandleWeapon(Game.Gameplay.Weapon.WeaponDefinition _) => RefreshProfile();

        // ---------------- 播放 ----------------

        private void PlayFire()
        {
            if (_profile == null || _profile.FireVariants == null || _profile.FireVariants.Length == 0) return;
            var entry = _profile.FireVariants[Random.Range(0, _profile.FireVariants.Length)];
            var voice = _fireVoices[_fireVoiceIndex];
            _fireVoiceIndex = (_fireVoiceIndex + 1) % _fireVoices.Count;
            ApplyEntry(voice, entry);
            voice.Play();
        }

        private void PlayEntry(WeaponAudioProfile.ClipEntry entry, string debugName)
        {
            if (entry.Clip == null) return; // 缺失静默（Validator 报告）
            var src = GetOneShotSource();
            ApplyEntry(src, entry);
            src.Play();
        }

        private void PlayClip(AudioClip clip, float vol, float pitch)
        {
            var src = GetOneShotSource();
            src.clip = clip;
            src.volume = vol;
            src.pitch = pitch;
            src.Play();
        }

        private AudioSource GetOneShotSource()
        {
            // 非连射音复用 fire 池的下一路（避免与在播 Fire 同轨截断）
            var voice = _fireVoices[_fireVoiceIndex];
            _fireVoiceIndex = (_fireVoiceIndex + 1) % _fireVoices.Count;
            return voice;
        }

        private void ApplyEntry(AudioSource src, WeaponAudioProfile.ClipEntry entry)
        {
            src.clip = entry.Clip;
            src.volume = Random.Range(entry.VolumeRange.x, entry.VolumeRange.y);
            src.pitch = Random.Range(entry.PitchRange.x, entry.PitchRange.y);
        }

        private void RefreshProfile()
            => _profile = controller != null && controller.Definition != null
                ? controller.Definition.AudioProfile
                : null;

        private void BuildFireVoices()
        {
            for (int i = 0; i < fireVoiceCount; i++)
            {
                var go = new GameObject("AudioVoice_" + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = _profile != null ? _profile.SpatialBlend : 0f;
                src.outputAudioMixerGroup = _profile != null ? _profile.MixerGroup : null;
                _fireVoices.Add(src);
            }
        }
    }
}
