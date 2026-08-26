using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Gameplay.Weapon
{
    /// <summary>
    /// 数值解析只读结果（Docs/13 §6.2 v3）。合成顺序固定：base → (+ΣAdd) → (×ΠMultiply)
    /// → 数值防护 → Clamp。倍率字段独立保留（消费端按通道组合，见组合访问器）。
    /// CP3 仅建立管线与测试；WeaponController 在 CP4 接线消费。
    /// </summary>
    public readonly struct ResolvedWeaponStats
    {
        public readonly WeaponStat Stat;

        // 各 StatId 合成后的最终值（含 Clamp）
        public readonly float VerticalRecoilDeg;    // (PitchDeg+Σadd)×Πmul，Clamp [0,15]
        public readonly float HorizontalRecoilDeg;  // (YawDeg+Σadd)×Πmul，Clamp [0,5]
        public readonly float AimRecoilScale;       // (1+Σadd)×Πmul，Clamp [0,10]
        public readonly float ViewModelKickScale;   // 同上
        public readonly float RecoilRecoveryScale;  // 同上
        public readonly float PatternScale;         // 同上
        public readonly float FirstShotScale;       // 同上
        public readonly float AdsRecoilScale;       // 同上
        public readonly float SpreadScale;          // 同上

        public ResolvedWeaponStats(WeaponStat stat,
            float verticalRecoilDeg, float horizontalRecoilDeg,
            float aimRecoilScale, float viewModelKickScale, float recoilRecoveryScale,
            float patternScale, float firstShotScale, float adsRecoilScale, float spreadScale)
        {
            Stat = stat;
            VerticalRecoilDeg = verticalRecoilDeg;
            HorizontalRecoilDeg = horizontalRecoilDeg;
            AimRecoilScale = aimRecoilScale;
            ViewModelKickScale = viewModelKickScale;
            RecoilRecoveryScale = recoilRecoveryScale;
            PatternScale = patternScale;
            FirstShotScale = firstShotScale;
            AdsRecoilScale = adsRecoilScale;
            SpreadScale = spreadScale;
        }

        // ---- 组合访问器（CP4 消费端语义；Docs/13 §6.2 情境倍率另在消费时叠加）----

        /// <summary>有效每发垂直后坐（AimRecoil 同源作用弹道与相机）。</summary>
        public float EffectiveRecoilPitchDeg => VerticalRecoilDeg * AimRecoilScale;
        /// <summary>有效每发水平后坐半径。</summary>
        public float EffectiveRecoilYawDeg => HorizontalRecoilDeg * AimRecoilScale;
        /// <summary>有效首枪倍率（Stat.Recoil.FirstShotMultiplier × FirstShotScale）。</summary>
        public float EffectiveFirstShotMultiplier => Stat.Recoil.FirstShotMultiplier * FirstShotScale;
        /// <summary>有效 ADS 后坐总倍率（Stat.Recoil.AdsRecoilMultiplier × AdsRecoilScale）。</summary>
        public float EffectiveAdsRecoilMultiplier => Stat.Recoil.AdsRecoilMultiplier * AdsRecoilScale;
        /// <summary>有效 Viewmodel 后移。</summary>
        public float EffectiveViewModelBack => Stat.Recoil.ViewModelKickBack * ViewModelKickScale;
        /// <summary>有效 Viewmodel 上抬。</summary>
        public float EffectiveViewModelPitchDeg => Stat.Recoil.ViewModelKickPitch * ViewModelKickScale;
        /// <summary>有效 Burst 恢复速度（发/秒）。</summary>
        public float EffectiveRecoverySpeed => Stat.Recoil.RecoverySpeed * RecoilRecoveryScale;
    }

    /// <summary>
    /// 数值解析器（Docs/13 §6.2 v3）：统一收集 → 稳定排序 → Add 求和 → Multiply 连乘 →
    /// 数值防护 → Clamp。唯一调用方 WeaponController（重算时机：Initialize/EquipDefinition、
    /// Modifier 集合变更）。纯函数、无状态、可测。
    /// </summary>
    public static class WeaponStatResolver
    {
        private const float MaxScale = 10f; // 倍率型合成上限（0 合法=完全消除；负值防护步 clamp 0）

        public static ResolvedWeaponStats Resolve(WeaponStat baseStat, IReadOnlyList<IWeaponStatModifierSource> sources)
        {
            var adds = new AddSums();
            var muls = new MulProducts();

            // ①/② 收集并稳定排序：(Priority, SourceId, 提交序=列表序)
            if (sources != null)
            {
                var ordered = new List<IWeaponStatModifierSource>(sources);
                ordered.Sort((a, b) =>
                {
                    int byPriority = a.Priority.CompareTo(b.Priority);
                    if (byPriority != 0) return byPriority;
                    return string.CompareOrdinal(a.SourceId, b.SourceId);
                });

                foreach (var source in ordered)
                {
                    var mods = source.GetModifiers();
                    if (mods == null) continue;
                    foreach (var mod in mods)
                    {
                        // ④ 数值防护（Docs/13 §6.2）：NaN/±Inf 拒绝该修饰符；乘法负值 clamp 0（0 合法）
                        float v = mod.Value;
                        if (float.IsNaN(v) || float.IsInfinity(v))
                        {
                            Debug.LogWarning($"[StatResolver] 非法数值修饰符被拒绝: {source.SourceId} {mod.Stat} {mod.Op} {v}");
                            continue;
                        }
                        if (mod.Op == ModifierOperation.Add) adds.Accumulate(mod.Stat, v);
                        else muls.Accumulate(mod.Stat, Mathf.Max(v, 0f));
                    }
                }
            }

            // ③/⑤ 合成 + Clamp：value = (base + Σadd) × Πmul → clamp
            return new ResolvedWeaponStats(
                baseStat,
                Compose(baseStat.Recoil.PitchDeg, adds, muls, WeaponStatId.VerticalRecoil, 0f, 15f),
                Compose(baseStat.Recoil.YawDeg, adds, muls, WeaponStatId.HorizontalRecoil, 0f, 5f),
                ComposeScale(adds, muls, WeaponStatId.AimRecoil),
                ComposeScale(adds, muls, WeaponStatId.ViewModelKick),
                ComposeScale(adds, muls, WeaponStatId.RecoilRecovery),
                ComposeScale(adds, muls, WeaponStatId.RecoilPatternScale),
                ComposeScale(adds, muls, WeaponStatId.FirstShotRecoil),
                ComposeScale(adds, muls, WeaponStatId.AdsRecoil),
                ComposeScale(adds, muls, WeaponStatId.Spread));
        }

        private static float Compose(float baseValue, AddSums adds, MulProducts muls, WeaponStatId id, float min, float max)
            => Mathf.Clamp((baseValue + adds.Get(id)) * muls.Get(id), min, max);

        private static float ComposeScale(AddSums adds, MulProducts muls, WeaponStatId id)
            => Mathf.Clamp((1f + adds.Get(id)) * muls.Get(id), 0f, MaxScale);

        /// <summary>加法累积（缺省 0=恒等）。</summary>
        private sealed class AddSums
        {
            private readonly Dictionary<WeaponStatId, float> _map = new();
            public void Accumulate(WeaponStatId id, float v) => _map[id] = (_map.TryGetValue(id, out var cur) ? cur : 0f) + v;
            public float Get(WeaponStatId id) => _map.TryGetValue(id, out var v) ? v : 0f;
        }

        /// <summary>乘法累积（缺省 1=恒等；与 AddSums 分开正是为缺省值不同）。</summary>
        private sealed class MulProducts
        {
            private readonly Dictionary<WeaponStatId, float> _map = new();
            public void Accumulate(WeaponStatId id, float v) => _map[id] = (_map.TryGetValue(id, out var cur) ? cur : 1f) * v;
            public float Get(WeaponStatId id) => _map.TryGetValue(id, out var v) ? v : 1f;
        }
    }
}
