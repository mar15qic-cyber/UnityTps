using System.Collections.Generic;
using Game.Core;
using Game.Gameplay.Combat;
using Game.Gameplay.Health;
using Game.Gameplay.Settings;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Presentation.FX
{
    /// <summary>Local exposure: direct flash holds white; peripheral/back flashes recover sooner.</summary>
    [DefaultExecutionOrder(100)]
    public sealed class ThrowableScreenEffects : MonoBehaviour
    {
        private struct Exposure { public float Started, Duration, Strength; }
        private readonly List<Exposure> _exposures = new();
        private AudioSource _ring;
        private AudioClip _ringClip;
        private float _alpha;
        private bool _ducking;
        public float CurrentAlpha => _alpha;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Mount()
        {
            if (Application.isBatchMode || FindFirstObjectByType<ThrowableScreenEffects>() != null) return;
            var host = new GameObject("ThrowableScreenEffects");
            DontDestroyOnLoad(host);
            host.AddComponent<ThrowableScreenEffects>();
        }

        private void OnEnable()
        {
            ThrowableProjectile.OnEffectGlobal += HandleEffect;
            SceneManager.sceneLoaded += SceneLoaded;
        }
        private void OnDisable()
        {
            ThrowableProjectile.OnEffectGlobal -= HandleEffect;
            SceneManager.sceneLoaded -= SceneLoaded;
            ResetExposure();
        }
        private void OnDestroy() { if (_ringClip != null) Destroy(_ringClip); }
        private void SceneLoaded(Scene scene, LoadSceneMode mode) => ResetExposure();
        private void ResetExposure()
        {
            _exposures.Clear(); _alpha = 0f;
            if (_ring != null) _ring.Stop();
            if (_ducking) AudioListener.volume = SettingsRuntime.MasterVolume;
            _ducking = false;
        }

        private void HandleEffect(ThrowableType type, Vector3 center, ThrowableDefinition definition)
        {
            if (type != ThrowableType.Flash || definition == null) return;
            var camera = UnityEngine.Camera.main;
            if (camera == null || !camera.isActiveAndEnabled) return;
            Vector3 toward = center - camera.transform.position;
            float facing = toward.sqrMagnitude < .0001f ? 1f
                : Vector3.Dot(camera.transform.forward, toward.normalized);
            float intensity = CalculateFlashStrength(toward.magnitude, definition.Radius, facing, definition.FlashStrength);
            if (intensity <= 0f || IsOccluded(camera.transform.position, center)) return;
            // Each detonation keeps its own envelope: a weak follow-up cannot extend full white.
            _exposures.Add(new Exposure { Started = Time.unscaledTime, Strength = intensity,
                Duration = definition.EffectSeconds * Mathf.Lerp(.15f, 1f, intensity) });
            EnsureRinging();
        }

        public static float CalculateFlashStrength(float distance, float radius, float facingDot, float strength)
        {
            if (radius <= 0f || distance >= radius || strength <= 0f) return 0f;
            float range = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(radius * .35f, radius, Mathf.Max(0f, distance)));
            float angle = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-.15f, .85f, facingDot));
            float exposure = Mathf.Lerp(.12f, 1f, angle) * range * strength;
            return Mathf.Clamp01(exposure);
        }

        public static float EvaluateAlpha(float elapsed, float duration, float strength)
        {
            if (duration <= 0f || elapsed < 0f || elapsed >= duration) return 0f;
            float hold = duration * Mathf.Lerp(.08f, .45f, strength);
            return strength * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(hold, duration, elapsed)));
        }

        public static bool IsOccluded(Vector3 cameraPosition, Vector3 effectPosition)
        {
            Vector3 ray = effectPosition - cameraPosition;
            float distance = ray.magnitude;
            if (distance <= .15f) return false;
            int fpLayer = LayerMask.NameToLayer("FirstPersonView");
            int mask = fpLayer >= 0 ? ~(1 << fpLayer) : ~0;
            foreach (var hit in Physics.RaycastAll(cameraPosition, ray / distance,
                distance - .15f, mask, QueryTriggerInteraction.Ignore))
            {
                // Owner's capsule, held weapons and other players must not count as walls.
                if (hit.collider.GetComponentInParent<DamageableTarget>() != null
                    || hit.collider.GetComponentInParent<Game.Gameplay.Network.NetworkCombatAuthority>() != null
                    || hit.collider.GetComponentInParent<ThrowableProjectile>() != null) continue;
                return true;
            }
            return false;
        }

        private void LateUpdate()
        {
            _alpha = 0f;
            for (int i = _exposures.Count - 1; i >= 0; --i)
            {
                var exposure = _exposures[i];
                float age = Time.unscaledTime - exposure.Started;
                if (age >= exposure.Duration) { _exposures.RemoveAt(i); continue; }
                _alpha = Mathf.Max(_alpha, EvaluateAlpha(age, exposure.Duration, exposure.Strength));
            }
            if (_alpha > 0f)
            {
                _ducking = true;
                AudioListener.volume = SettingsRuntime.MasterVolume * Mathf.Lerp(1f, .18f, _alpha);
                if (_ring != null)
                {
                    _ring.volume = .08f * _alpha * SettingsRuntime.MasterVolume * AudioBus.SfxVolume;
                    if (!_ring.isPlaying) _ring.Play();
                }
            }
            else if (_ducking) ResetExposure();
        }

        private void EnsureRinging()
        {
            if (_ring != null) return;
            const int rate = 24000;
            var samples = new float[rate];
            for (int i = 0; i < rate; i++)
                samples[i] = Mathf.Sin(2f * Mathf.PI * 2200f * i / rate) * .7f
                    + Mathf.Sin(2f * Mathf.PI * 2800f * i / rate) * .3f;
            _ringClip = AudioClip.Create("FlashRinging", rate, 1, rate, false);
            _ringClip.SetData(samples, 0);
            _ring = gameObject.AddComponent<AudioSource>();
            _ring.clip = _ringClip; _ring.loop = true; _ring.playOnAwake = false;
            _ring.spatialBlend = 0f; _ring.ignoreListenerVolume = true;
        }

        private void OnGUI()
        {
            if (_alpha <= 0f) return;
            var old = GUI.color; int oldDepth = GUI.depth;
            GUI.depth = -1000;
            GUI.color = new Color(1f, 1f, 1f, _alpha);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old; GUI.depth = oldDepth;
        }
    }
}
