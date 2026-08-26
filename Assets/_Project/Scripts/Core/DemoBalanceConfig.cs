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
            SanitizeRecoil(ref stat.Recoil, stat.Spread);
            SanitizeAccuracy(ref stat.Accuracy, stat.Spread);
            SanitizeBallistic(ref stat.Ballistic);
            return stat;
        }

        /// <summary>CP3 迁移守卫（Docs/13 §6.1）：旧资产新字段反序列化为 0——乘法类兜底中性值、
        /// 弹簧兜底 CP2 legacy 值（9Hz/ζ0.75），再统一 Clamp。显式填值的资产不触发兜底。</summary>
        private static void SanitizeRecoil(ref RecoilProfileData r, float legacySpread)
        {
            if (r.PitchDeg <= 0f) r.PitchDeg = 1.1f;                 // CP2 legacy 每发垂直冲量
            if (r.YawDeg <= 0f) r.YawDeg = 0.3f;                     // CP2 legacy
            if (r.FirstShotMultiplier <= 0f) r.FirstShotMultiplier = 1f; // 中性
            if (r.Accumulation <= 0f) r.Accumulation = 1f;
            if (r.MaxAccumulation <= 0f) r.MaxAccumulation = 1f;
            if (r.RecoverySpeed <= 0f) r.RecoverySpeed = 6f;
            if (r.SpringFrequency <= 0f) r.SpringFrequency = 9f;     // CP2 legacy
            if (r.SpringDamping <= 0f) r.SpringDamping = 0.75f;      // CP2 legacy
            if (r.AdsRecoilMultiplier <= 0f) r.AdsRecoilMultiplier = 0.6f; // 决策默认
            // Clamp（Docs/13 §6.1 区间）
            r.PitchDeg = Mathf.Clamp(r.PitchDeg, 0.05f, 15f);
            r.YawDeg = Mathf.Clamp(r.YawDeg, 0f, 5f);
            r.FirstShotMultiplier = Mathf.Clamp(r.FirstShotMultiplier, 1f, 3f);
            r.Accumulation = Mathf.Clamp(r.Accumulation, 0.5f, 2f);
            r.MaxAccumulation = Mathf.Clamp(r.MaxAccumulation, 1f, 30f);
            r.RecoveryDelay = Mathf.Clamp(r.RecoveryDelay, 0f, 1f);
            r.RecoverySpeed = Mathf.Clamp(r.RecoverySpeed, 0.5f, 20f);
            r.SpringFrequency = Mathf.Clamp(r.SpringFrequency, 2f, 20f);
            r.SpringDamping = Mathf.Clamp(r.SpringDamping, 0.1f, 1f);
            r.ShakePositionAmplitude = Mathf.Clamp(r.ShakePositionAmplitude, 0f, 1f);
            r.ViewModelKickBack = Mathf.Clamp(r.ViewModelKickBack, 0f, 0.2f);
            r.ViewModelKickPitch = Mathf.Clamp(r.ViewModelKickPitch, 0f, 15f);
            r.AdsRecoilMultiplier = Mathf.Clamp(r.AdsRecoilMultiplier, 0.1f, 1f);
        }

        private static void SanitizeAccuracy(ref AccuracyProfileData a, float legacySpread)
        {
            // 兼容映射：BaseHipSpread 未填时继承旧平铺 Spread（Docs/13 §6.1 迁移期）
            if (a.BaseHipSpread <= 0f && legacySpread > 0f) a.BaseHipSpread = legacySpread;
            if (a.BaseHipSpread <= 0f) a.BaseHipSpread = 1f;         // 兜底中性
            if (a.BaseAdsSpread <= 0f) a.BaseAdsSpread = a.BaseHipSpread * 0.25f;
            if (a.BloomRecoverySpeed <= 0f) a.BloomRecoverySpeed = 5f;
            a.BaseHipSpread = Mathf.Clamp(a.BaseHipSpread, 0f, 10f);
            a.BaseAdsSpread = Mathf.Clamp(a.BaseAdsSpread, 0f, a.BaseHipSpread);
            a.MovementSpreadMax = Mathf.Clamp(a.MovementSpreadMax, 0f, 10f);
            a.SprintSpreadExtra = Mathf.Clamp(a.SprintSpreadExtra, 0f, 10f);
            a.ShotBloomPerShot = Mathf.Clamp(a.ShotBloomPerShot, 0f, 5f);
            a.MaxBloom = Mathf.Clamp(a.MaxBloom, 0f, 10f);
            if (a.MaxBloom < a.ShotBloomPerShot) a.MaxBloom = a.ShotBloomPerShot; // Bloom 至少容下一发
            a.BloomRecoveryDelay = Mathf.Clamp(a.BloomRecoveryDelay, 0f, 1f);
            a.BloomRecoverySpeed = Mathf.Clamp(a.BloomRecoverySpeed, 1f, 60f);
        }

        private static void SanitizeBallistic(ref BallisticProfileData b)
        {
            if (b.PelletCount <= 0) b.PelletCount = 1;
            b.PelletCount = Mathf.Clamp(b.PelletCount, 1, 16);
            b.PelletSpread = Mathf.Clamp(b.PelletSpread, 0f, 15f);
        }
    }
}
