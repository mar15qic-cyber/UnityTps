using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// Day2 的本地数值适配器。Gameplay 只依赖 IBalanceConfig；Day10-11 接入 Lua 时可整体替换本资产。
    /// </summary>
    [CreateAssetMenu(menuName = "UnityFps/Balance/Demo Balance Config", fileName = "DemoBalanceConfig")]
    public sealed class DemoBalanceConfig : ScriptableObject, IBalanceConfig
    {
        [Serializable]
        public struct WeaponEntry
        {
            public string WeaponId;
            public WeaponStat Stat;
        }

        [SerializeField] private WeaponEntry[] weapons = Array.Empty<WeaponEntry>();
        private Dictionary<string, WeaponStat> _lookup;

        public WeaponStat GetWeaponStat(string weaponId)
        {
            EnsureLookup();
            if (!string.IsNullOrWhiteSpace(weaponId) && _lookup.TryGetValue(weaponId, out var stat))
                return Sanitize(stat);

            Debug.LogError($"[Balance] Missing weapon stat for '{weaponId}'.");
            return Sanitize(default);
        }

        public int GetFinalDamage(string weaponId, int upDamageLv)
        {
            var baseDamage = GetWeaponStat(weaponId).Damage;
            return Mathf.Max(1, Mathf.RoundToInt(baseDamage * (1f + Mathf.Max(0, upDamageLv) * 0.1f)));
        }

        public int GetXpForLevel(int level) => Mathf.Max(0, level) * 100;

        public int GetUpgradeCost(string statId, int currentLv) => 100 + Mathf.Max(0, currentLv) * 50;

        private void OnValidate() => _lookup = null;

        private void EnsureLookup()
        {
            if (_lookup != null) return;
            _lookup = new Dictionary<string, WeaponStat>(StringComparer.Ordinal);
            foreach (var entry in weapons)
            {
                if (!string.IsNullOrWhiteSpace(entry.WeaponId))
                    _lookup[entry.WeaponId] = entry.Stat;
            }
        }

        private static WeaponStat Sanitize(WeaponStat stat)
        {
            stat.Damage = Mathf.Max(1, stat.Damage);
            stat.Rpm = Mathf.Max(1, stat.Rpm);
            stat.MagSize = Mathf.Max(1, stat.MagSize);
            stat.ReserveAmmo = Mathf.Max(0, stat.ReserveAmmo);
            stat.ReloadTime = Mathf.Max(0.05f, stat.ReloadTime);
            stat.Spread = Mathf.Max(0f, stat.Spread);
            stat.MaxRange = Mathf.Max(1f, stat.MaxRange);
            stat.AdsFov = stat.AdsFov <= 0f ? 50f : stat.AdsFov;
            return stat;
        }
    }
}
