using System;
using UnityEngine;

namespace Game.Gameplay.Settings
{
    /// <summary>Direct references to LPFP source textures; no recoloring or copied bitmap assets.</summary>
    public sealed class NativeScopeReticleCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public OpticReticleStyle style;
            public OpticReticleColor color;
            public Texture2D texture;
            // UV of the aiming feature, not the bounding-box center (arrow apex / three-post dot).
            public Vector2 aimUv = new(.5f, .5f);
        }
        [SerializeField] private Entry[] entries = Array.Empty<Entry>();
        [SerializeField] private Material additiveMaterial;
        [SerializeField] private Texture2D sniperTexture;
        [SerializeField] private Material sniperMaterial;
        public Texture2D SniperTexture => sniperTexture;
        public Material SniperMaterial => sniperMaterial;
        public Entry[] Entries => entries;
        public Material AdditiveMaterial => additiveMaterial;
        public static NativeScopeReticleCatalog Load() => Resources.Load<NativeScopeReticleCatalog>("NativeScopeReticleCatalog");
        public Entry Find(OpticReticleStyle style, OpticReticleColor color)
        {
            style = SettingsModel.NormalizeReticleStyle(style);
            color = SettingsModel.NormalizeReticleColor(style, color);
            foreach (var entry in entries) if (entry.style == style && entry.color == color) return entry;
            return null;
        }
    }
}
