using System;
using System.Collections.Generic;
using Game.Gameplay.Weapon;
using UnityEngine;

namespace Game.Presentation.HUD
{
    public enum OpticPresentationMode
    {
        Physical1x,
        MagnifiedOverlay
    }

    /// <summary>瞄具的表现配置。Sprite 可选；没有 Sprite 时 OpticAdsView 使用程序化分划。</summary>
    [Serializable]
    public sealed class OpticViewProfile
    {
        public string opticId;
        public OpticPresentationMode mode;
        public Sprite reticleSprite;
        public Color reticleColor = new Color(1f, .18f, .08f, 1f);
        [Min(4f)] public float reticleSize = 36f;
        [Range(.1f, .49f)] public float apertureRadius = .43f;
        [Range(0f, 1f)] public float vignetteAlpha = .98f;

        public bool IsMagnified => mode == OpticPresentationMode.MagnifiedOverlay;
    }

    /// <summary>
    /// 正式运行时瞄具表现目录。Resources 中存在资产时使用资产配置；缺失时按
    /// OpticAimTier 生成安全默认值，确保新配件不会因为漏填表现资产而黑屏。
    /// </summary>
    [CreateAssetMenu(fileName = "OpticViewCatalog", menuName = "UnityFps/Presentation/Optic View Catalog")]
    public sealed class OpticViewCatalog : ScriptableObject
    {
        [SerializeField] private List<OpticViewProfile> profiles = new();
        private Dictionary<string, OpticViewProfile> _index;
        private static OpticViewCatalog _runtime;

        public IReadOnlyList<OpticViewProfile> Profiles => profiles;

        public static OpticViewCatalog LoadOrDefault()
        {
            if (_runtime != null) return _runtime;
            _runtime = Resources.Load<OpticViewCatalog>("OpticViewCatalog");
            if (_runtime == null) _runtime = CreateInstance<OpticViewCatalog>();
            return _runtime;
        }

        public bool TryGet(string opticId, OpticAimTier tier, out OpticViewProfile profile)
        {
            EnsureIndex();
            if (!string.IsNullOrWhiteSpace(opticId) && _index.TryGetValue(opticId, out profile)) return true;
            if (tier == OpticAimTier.None) { profile = null; return false; }
            profile = new OpticViewProfile
            {
                opticId = opticId,
                mode = tier == OpticAimTier.LowZoom || tier == OpticAimTier.HighZoom
                    ? OpticPresentationMode.MagnifiedOverlay
                    : OpticPresentationMode.Physical1x,
                reticleSize = tier == OpticAimTier.HighZoom ? 54f : 34f,
                apertureRadius = tier == OpticAimTier.HighZoom ? .40f : .43f
            };
            return true;
        }

        private void EnsureIndex()
        {
            if (_index != null) return;
            _index = new Dictionary<string, OpticViewProfile>(StringComparer.Ordinal);
            if (profiles == null) return;
            foreach (var profile in profiles)
                if (profile != null && !string.IsNullOrWhiteSpace(profile.opticId)) _index[profile.opticId] = profile;
        }
    }
}
