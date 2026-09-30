using System.Collections.Generic;
using Game.Gameplay.Weapon;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Game.Presentation.Weapon
{
    /// <summary>Carrier guns retain world shadows, but cannot stencil their own tactical light.</summary>
    public sealed class TacticalFlashlightShadowFilter : MonoBehaviour
    {
        // Project rendering layer 1 is unused by authored content (default content uses bit 0).
        public const uint CarrierWeaponLayer = 1u << 1;
        private readonly Dictionary<Renderer, uint> _original = new();
        private UniversalAdditionalLightData _light;
        private uint _lighting, _shadows;
        private bool _customShadows, _bound, _applied;

        public static void Bind(TacticalFlashlight device, GameObject weapon)
        {
            if (device == null || weapon == null) return;
            var filter = device.GetComponent<TacticalFlashlightShadowFilter>()
                ?? device.gameObject.AddComponent<TacticalFlashlightShadowFilter>();
            filter.Configure(weapon);
        }

        private void Configure(GameObject weapon)
        {
            Restore();
            _original.Clear();
            var source = GetComponentInChildren<Light>(true);
            if (source == null) return;
            _light = source.GetComponent<UniversalAdditionalLightData>()
                ?? source.gameObject.AddComponent<UniversalAdditionalLightData>();
            _lighting = _light.renderingLayers;
            _shadows = _light.shadowRenderingLayers;
            _customShadows = _light.customShadowLayers;
            foreach (var renderer in weapon.GetComponentsInChildren<Renderer>(true))
                _original[renderer] = renderer.renderingLayerMask;
            _bound = true;
            Apply();
        }

        private void OnEnable() { if (_bound) Apply(); }
        private void OnDisable() => Restore();
        private void OnDestroy() => Restore();

        private void Apply()
        {
            if (_light == null) return;
            foreach (var pair in _original)
                if (pair.Key != null) pair.Key.renderingLayerMask = (pair.Value & ~1u) | CarrierWeaponLayer;
            // Ordinary lights still illuminate/cast the carrier gun. Tactical lights use
            // a distinct shadow mask, retaining visible walls, obstacles and character bodies.
            foreach (var source in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (source.GetComponentInParent<TacticalFlashlight>() != null) continue;
                var data = source.GetComponent<UniversalAdditionalLightData>()
                    ?? source.gameObject.AddComponent<UniversalAdditionalLightData>();
                if ((data.renderingLayers & 1u) != 0) data.renderingLayers |= CarrierWeaponLayer;
                if (data.customShadowLayers && (data.shadowRenderingLayers & 1u) != 0)
                    data.shadowRenderingLayers |= CarrierWeaponLayer;
            }
            _light.customShadowLayers = true;
            _light.renderingLayers = _lighting | CarrierWeaponLayer;
            _light.shadowRenderingLayers = (_customShadows ? _shadows : _lighting) & ~CarrierWeaponLayer;
            _applied = true;
        }

        private void Restore()
        {
            if (!_applied) return;
            _applied = false;
            foreach (var pair in _original)
                if (pair.Key != null) pair.Key.renderingLayerMask = pair.Value;
            if (!_bound || _light == null) return;
            _light.renderingLayers = _lighting;
            _light.shadowRenderingLayers = _shadows;
            _light.customShadowLayers = _customShadows;
        }
    }
}
