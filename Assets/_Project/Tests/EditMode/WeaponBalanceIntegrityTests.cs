using System.Collections.Generic;
using System.Linq;
using Game.Core;
using Game.Gameplay.Weapon;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 正式武器数值与动画完整性（Docs/23 实机复盘 2026-09-05）：
    /// ① 每个可从 Lobby 配装进入 Arena 的正式 LPFP 武器，必须满足
    ///    Catalog itemId → WeaponDefinition → WeaponId → DemoBalanceConfig 唯一条目；
    /// ② 缺 Balance 条目必须可被显式检出（TryGetWeaponStat=false），不允许静默 1/0；
    /// ③ 本轮新增武器的 Draw 动画必须绑定 take_out_weapon@*（SMG04 曾错绑 run@smg_04）。
    /// </summary>
    public sealed class WeaponBalanceIntegrityTests
    {
        private const string BalanceAssetPath = "Assets/_Project/ScriptableObjects/Weapons/Day2_DemoBalance.asset";
        private const string CatalogAssetPath = "Assets/_Project/ScriptableObjects/Account/WeaponAssetCatalog.asset";

        /// <summary>本轮新增武器（实机暴露 1/0 弹药问题的一批）对应的正式定义资产。</summary>
        private static readonly string[] NewWeaponDefinitionPaths =
        {
            "Assets/_Project/ScriptableObjects/Weapons/Day3_Handgun03.asset",
            "Assets/_Project/ScriptableObjects/Weapons/Day3_Handgun04.asset",
            "Assets/_Project/ScriptableObjects/Weapons/Day3_SMG03.asset",
            "Assets/_Project/ScriptableObjects/Weapons/Day3_SMG04.asset",
            "Assets/_Project/ScriptableObjects/Weapons/Day3_SMG05.asset",
            "Assets/_Project/ScriptableObjects/Weapons/Day3_Sniper03.asset",
        };

        private static DemoBalanceConfig LoadBalance()
            => AssetDatabase.LoadAssetAtPath<DemoBalanceConfig>(BalanceAssetPath);

        private static WeaponAssetCatalog LoadCatalog()
            => AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(CatalogAssetPath)
               ?? WeaponAssetCatalog.CreateRuntime();

        // ---- ① 正式 LPFP 武器 ↔ Balance 唯一条目 ----

        [Test]
        public void EveryLpfpLoadoutWeapon_HasUniqueBalanceEntry()
        {
            var balance = LoadBalance();
            Assert.That(balance, Is.Not.Null, $"Balance 资产缺失：{BalanceAssetPath}");
            var catalog = LoadCatalog();
            Assert.That(catalog, Is.Not.Null, "WeaponAssetCatalog 无法加载（资产与 CreateRuntime 均失败）");

            var missing = new List<string>();
            var checkedIds = new List<string>();
            foreach (var entry in catalog.Entries)
            {
                if (entry == null || !entry.IsLpfp) continue;
                if (!catalog.TryResolveDefinition(entry.itemId, out var definition) || definition == null)
                {
                    missing.Add($"{entry.itemId} → 定义解析失败");
                    continue;
                }
                checkedIds.Add(definition.WeaponId);
                if (!balance.TryGetWeaponStat(definition.WeaponId, out _))
                    missing.Add($"{entry.itemId} → {definition.WeaponId} 无 Balance 条目（将降级 1/0）");
            }

            Assert.That(missing, Is.Empty,
                "正式可装备武器必须全部具备 Balance 条目：" + string.Join("; ", missing));

            // 唯一性：Balance 表内同 WeaponId 只允许一条（重复会被字典覆盖掩盖配置错误）
            var weaponsField = typeof(DemoBalanceConfig).GetField("weapons",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var entries = (DemoBalanceConfig.WeaponEntry[])weaponsField.GetValue(balance);
            var duplicated = entries
                .GroupBy(e => e.WeaponId, System.StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            Assert.That(duplicated, Is.Empty, "Balance 存在重复 WeaponId：" + string.Join(",", duplicated));
            Assert.That(checkedIds, Is.Not.Empty, "目录中未检出任何 LPFP 可装备武器——测试环境资产异常");
        }

        // ---- ② 缺项显式检出，不静默 1/0 ----

        [Test]
        public void MissingBalanceEntry_TryGet_ReturnsFalse_WithoutFakeValues()
        {
            var balance = LoadBalance();
            Assert.That(balance, Is.Not.Null);

            Assert.That(balance.TryGetWeaponStat("weapon.does_not_exist", out var stat), Is.False);
            Assert.That(stat.MagSize, Is.EqualTo(0), "TryGet 缺项不得返回被 Sanitize 夹成 1 的伪合法弹匣");
        }

        // ---- ③ 本轮新增武器 Draw 动画语义（SMG04 错绑 run@smg_04 回归） ----

        [Test]
        public void NewWeapons_DrawClip_MustBeTakeOutWeapon()
        {
            var failures = new List<string>();
            foreach (var path in NewWeaponDefinitionPaths)
            {
                var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(path);
                if (definition == null)
                {
                    failures.Add($"{path} 定义资产缺失");
                    continue;
                }
                var draw = definition.FirstPersonAnimations.Draw;
                if (draw == null)
                {
                    failures.Add($"{definition.name} Draw 未绑定");
                    continue;
                }
                if (!draw.name.StartsWith("take_out_weapon@", System.StringComparison.Ordinal))
                    failures.Add($"{definition.name} Draw 错绑为 {draw.name}（必须 take_out_weapon@*）");
            }
            Assert.That(failures, Is.Empty, string.Join("; ", failures));
        }

        [Test]
        public void Smg04_Draw_IsExactly_TakeOutWeapon()
        {
            var definition = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(
                "Assets/_Project/ScriptableObjects/Weapons/Day3_SMG04.asset");
            Assert.That(definition, Is.Not.Null, "Day3_SMG04 定义缺失");
            var draw = definition.FirstPersonAnimations.Draw;
            Assert.That(draw, Is.Not.Null, "SMG04 Draw 未绑定");
            Assert.That(draw.name, Is.EqualTo("take_out_weapon@smg_04"),
                "SMG04 首次装备曾播放 run@smg_04（fileID 7400062 误绑），必须为 take_out_weapon@smg_04");
        }

        // ---- ④ 全目录 Draw 语义扫描（若绑定则必须为出枪语义） ----

        [Test]
        public void AllLpfpDefinitions_BoundDrawClip_HasTakeOutSemantics()
        {
            var catalog = LoadCatalog();
            var failures = new List<string>();
            foreach (var entry in catalog.Entries)
            {
                if (entry == null || !entry.IsLpfp) continue;
                if (!catalog.TryResolveDefinition(entry.itemId, out var definition) || definition == null) continue;
                var draw = definition.FirstPersonAnimations.Draw;
                if (draw == null) continue; // 未绑定 Draw 属资产配置完整性问题，由 ③ 与正式链路另行把关
                if (!draw.name.StartsWith("take_out_weapon@", System.StringComparison.Ordinal))
                    failures.Add($"{definition.name} Draw={draw.name}");
            }
            Assert.That(failures, Is.Empty, "Draw 绑定含非出枪语义 clip：" + string.Join("; ", failures));
        }
    }
}
