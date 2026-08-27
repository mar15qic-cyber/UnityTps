using System.Collections.Generic;
using System.Text;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// CP6 音频配置校验（Docs/13 §9-11 拍板：缺失 clip 留空+报告，不用近义 clip 假装完成）。
    /// 菜单：Tools/Weapon Audio Profile Validator——报告 5 份族 Profile 的空槽清单。
    /// Day4 实机审计 §5 扩展：① FireVariants 跨族同 GUID 复用检测（同一 shoot.wav 换 Profile
    /// 名不构成差异化）；② WeaponDefinition 引用的 Profile 与实际消费侧对账（空/未引用槽）。
    /// </summary>
    public static class WeaponAudioProfileValidator
    {
        [MenuItem("Tools/Weapon Audio Profile Validator")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== Weapon Audio Profile Validator（族共享 ×5）====");
            int missingTotal = 0;
            var fireGuidOwners = new Dictionary<string, List<string>>();

            foreach (var guid in AssetDatabase.FindAssets("t:WeaponAudioProfile", new[] { "Assets/_Project" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var p = AssetDatabase.LoadAssetAtPath<WeaponAudioProfile>(path);
                if (p == null) continue;

                var missing = new StringBuilder();
                int count = 0;
                if (IsEmpty(p.FireVariants)) { missing.Append("Fire "); count++; }
                if (p.DryFire.Clip == null) { missing.Append("DryFire "); count++; }
                if (p.ReloadAmmoLeft.Clip == null) { missing.Append("ReloadLeft "); count++; }
                if (p.ReloadOutOfAmmo.Clip == null) { missing.Append("ReloadEmpty "); count++; }
                if (p.MagOut.Clip == null && p.MagIn.Clip == null && p.BoltRack.Clip == null) { missing.Append("(无分阶段) "); }
                if (p.Draw.Clip == null) { missing.Append("Draw "); count++; }
                if (p.Holster.Clip == null) { missing.Append("Holster "); count++; }
                if (p.WeaponSwitch.Clip == null) { missing.Append("Switch "); count++; }
                if (p.ShellEject.Clip == null) { missing.Append("ShellEject "); count++; }
                if (p.Pump.Clip == null) { missing.Append("Pump "); count++; }
                if (p.SniperBolt.Clip == null) { missing.Append("SniperBolt "); count++; }
                if (p.Jam.Clip == null) { missing.Append("Jam(预留) "); }

                missingTotal += count;
                sb.AppendLine($"[{p.name}] 缺失槽位: {(count > 0 ? missing.ToString() : "（核心槽全齐）")}");

                // Fire GUID 复用检测（审计 §5：同 GUID 跨族 = 全部枪声相同）
                if (p.FireVariants != null)
                    foreach (var v in p.FireVariants)
                    {
                        if (v.Clip == null) continue;
                        string assetPath = AssetDatabase.GetAssetPath(v.Clip);
                        if (!fireGuidOwners.TryGetValue(assetPath, out var owners))
                            fireGuidOwners[assetPath] = owners = new List<string>();
                        if (!owners.Contains(p.name)) owners.Add(p.name);
                    }
            }

            sb.AppendLine();
            sb.AppendLine("---- Fire 素材复用（跨族同 clip = 枪声无差异，审计 §5）----");
            bool anyReuse = false;
            foreach (var kv in fireGuidOwners)
            {
                if (kv.Value.Count > 1)
                {
                    anyReuse = true;
                    sb.AppendLine($"[复用×{kv.Value.Count}] {kv.Key} ← {string.Join(", ", kv.Value)}");
                }
            }
            if (!anyReuse) sb.AppendLine("（无跨族复用）");

            // WeaponDefinition → Profile 对账（空 Profile / 未被任何武器引用的 Profile）
            sb.AppendLine();
            sb.AppendLine("---- WeaponDefinition 消费对账 ----");
            var referenced = new HashSet<Object>();
            foreach (var wGuid in AssetDatabase.FindAssets("t:WeaponDefinition", new[] { "Assets/_Project" }))
            {
                var wPath = AssetDatabase.GUIDToAssetPath(wGuid);
                var w = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(wPath);
                if (w == null) continue;
                if (w.AudioProfile == null)
                    sb.AppendLine($"[空Profile] {w.name} ({w.WeaponId})——该武器完全静音");
                else
                    referenced.Add(w.AudioProfile);
            }
            foreach (var guid in AssetDatabase.FindAssets("t:WeaponAudioProfile", new[] { "Assets/_Project" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var p = AssetDatabase.LoadAssetAtPath<WeaponAudioProfile>(path);
                if (p != null && !referenced.Contains(p))
                    sb.AppendLine($"[未消费] {p.name}——无任何 WeaponDefinition 引用");
            }

            sb.AppendLine();
            sb.AppendLine($"==== 核心缺失合计: {missingTotal}（Jam/ShellEject/Pump/SniperBolt/Switch 属已知缺失策略）====");
            if (anyReuse)
                sb.AppendLine("⚠ 存在跨族 Fire 复用：这些武器开火声完全相同（占位需显式标注，最终验收前必须替换为不同素材）");
            Debug.Log(sb.ToString());
        }

        private static bool IsEmpty(WeaponAudioProfile.ClipEntry[] variants)
            => variants == null || variants.Length == 0 || variants[0].Clip == null;
    }
}
