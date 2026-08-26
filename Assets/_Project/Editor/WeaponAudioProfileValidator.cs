using System.Text;
using Game.Gameplay.Weapon;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// CP6 音频配置校验（Docs/13 §9-11 拍板：缺失 clip 留空+报告，不用近义 clip 假装完成）。
    /// 菜单：Tools/Weapon Audio Profile Validator——报告 5 份族 Profile 的空槽清单。
    /// </summary>
    public static class WeaponAudioProfileValidator
    {
        [MenuItem("Tools/Weapon Audio Profile Validator")]
        public static void Run()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== Weapon Audio Profile Validator（族共享 ×5）====");
            int missingTotal = 0;

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
            }

            sb.AppendLine($"==== 核心缺失合计: {missingTotal}（Jam/ShellEject/Pump/SniperBolt/Switch 属已知缺失策略）====");
            Debug.Log(sb.ToString());
        }

        private static bool IsEmpty(WeaponAudioProfile.ClipEntry[] variants)
            => variants == null || variants.Length == 0 || variants[0].Clip == null;
    }
}
