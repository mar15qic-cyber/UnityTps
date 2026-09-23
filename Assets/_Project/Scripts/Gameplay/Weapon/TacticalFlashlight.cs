using Game.Gameplay.Network;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>World illumination follows the physical accessory; previews and hidden owner TP copies stay dark.</summary>
    public sealed class TacticalFlashlight : MonoBehaviour
    {
        private Light _light;
        private bool _firstPerson;
        private IWeaponPresentationGate _gate;
        private NetworkCombatAuthority _player;

        public void Setup(bool firstPerson)
        {
            _firstPerson = firstPerson;
            _gate = GetComponentInParent<IWeaponPresentationGate>();
            _player = GetComponentInParent<NetworkCombatAuthority>();
            if (_light != null) return;
            var node = new GameObject("TacticalLightSource");
            node.transform.SetParent(transform, false);
            node.layer = 0;
            _light = node.AddComponent<Light>();
            _light.type = LightType.Spot;
            _light.range = 28f;
            _light.intensity = 8f;
            _light.spotAngle = 48f;
            _light.innerSpotAngle = 24f;
            _light.color = new Color(1f, 0.96f, 0.86f);
            _light.shadows = LightShadows.Soft;
            _light.shadowBias = 0.015f;
            _light.shadowNormalBias = 0.02f;
            _light.cullingMask = ~((1 << 5) | (1 << 8) | (1 << 9) | (1 << 31));
            _light.enabled = false;
        }

        private void LateUpdate()
        {
            if (_light == null) return;
            _light.gameObject.layer = 0; // FP/TP recursive layer assignment must not cull this world light.
            bool visible = _firstPerson ? _gate != null && _gate.IsWeaponViewVisible
                : _player != null && !_player.IsOwnerPlayer && !_player.IsDead;
            var throwables = _player != null ? _player.GetComponent<Game.Gameplay.Combat.ThrowableController>() : null;
            _light.enabled = visible && (throwables == null || !throwables.IsEquipped);
            if (!_light.enabled) return;
            var axis = LaserSightBeam.DeviceAxis(transform);
            _light.transform.rotation = Quaternion.LookRotation(axis, transform.up);
            // Advance to the emitting end of the housing, so its own mesh cannot occlude the cone.
            Bounds bounds = default;
            bool any = false;
            foreach (var mesh in GetComponentsInChildren<MeshRenderer>())
            { if (!any) { bounds = mesh.bounds; any = true; } else bounds.Encapsulate(mesh.bounds); }
            float tip = any ? Vector3.Dot(new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)), bounds.extents) : 0f;
            _light.transform.position = (any ? bounds.center : transform.position) + axis * (tip + 0.015f);
        }

        private void OnDisable() { if (_light != null) _light.enabled = false; }
    }
}
