using Game.Core;
using Game.Gameplay.Combat;
using Game.Gameplay.Network;
using Game.Gameplay.Weapon;
using Game.Presentation.Animation;
using UnityEngine;

namespace Game.Presentation.Audio
{
    /// <summary>权威开火广播只给观察者播远端枪声；Owner 的 2D 枪声仍由 WeaponAudioView 负责。</summary>
    public sealed class RemoteGunAudioView : MonoBehaviour
    {
        private CombatAudioConfig _config;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Mount()
        {
            if (Application.isBatchMode || FindFirstObjectByType<RemoteGunAudioView>() != null) return;
            var host = new GameObject("RemoteGunAudioHost");
            DontDestroyOnLoad(host);
            host.AddComponent<RemoteGunAudioView>();
        }

        private void OnEnable()
        {
            _config = Resources.Load<CombatAudioConfig>("CombatAudioConfig");
            if (_config == null || !_config.IsValid)
            { Debug.LogError("[RemoteGunAudioView] CombatAudioConfig missing or invalid", this); enabled = false; return; }
            NetworkCombatAuthority.OnRemoteShotGlobal += HandleShot;
        }

        private void OnDisable() => NetworkCombatAuthority.OnRemoteShotGlobal -= HandleShot;

        private void HandleShot(NetworkCombatAuthority shooter, RemoteShotPresentation shot)
        {
            if (shooter == null || shooter.IsOwnerPlayer || Application.isBatchMode) return;
            var arsenal = shooter.GetComponent<Arsenal>();
            WeaponDefinition definition = null;
            var current = shooter.GetComponent<WeaponController>()?.Definition;
            if (current != null && current.WeaponId == shot.WeaponId) definition = current;
            if (arsenal != null)
                foreach (var entry in arsenal.Slots)
                    if (entry != null && entry.WeaponId == shot.WeaponId) { definition = entry; break; }
            if (definition == null)
            {
                Debug.LogError($"[RemoteGunAudioView] weapon '{shot.WeaponId}' missing from shooter arsenal", shooter);
                return;
            }
            var variants = Game.Presentation.HUD.WeaponAudioView.SelectFireVariants(
                definition.AudioProfile, shot.IsSuppressed);
            if (variants == null || variants.Length == 0) return;
            var entryClip = variants[Random.Range(0, variants.Length)];
            if (entryClip.Clip == null) return;
            var muzzle = shooter.GetComponentInChildren<TPWeaponMeshSwapper>(true)?.CurrentMuzzle;
            if (muzzle == null) return;
            var host = new GameObject("RemoteGunShot");
            host.transform.position = muzzle.position;
            var source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = _config.RemoteGunMaxDistance;
            source.volume = AudioBus.ComputeVolume(_config.RemoteGunVolume
                * Random.Range(entryClip.VolumeRange.x, entryClip.VolumeRange.y), AudioBus.Category.Sfx);
            source.pitch = Random.Range(entryClip.PitchRange.x, entryClip.PitchRange.y);
            source.clip = entryClip.Clip;
            source.Play();
            Destroy(host, entryClip.Clip.length / Mathf.Max(0.1f, source.pitch) + 0.1f);
        }
    }
}
