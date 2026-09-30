using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Account;
using Game.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// CF 三背包 UI 结构（2026-09-30 Phase D，离线反射渲染——无后端调用）：
    /// ① 仓库页背包页签 1/2/3 + 装配条（主/副摘要 + 投掷物循环按钮）+ 网格收窄；
    /// ② 「已装备」徽标跟随所选背包（不再只看活动背包）；
    /// ③ 大厅装配卡：背包预览页签 + 投掷/配件摘要行，页签切换刷新对应背包内容。
    /// </summary>
    public sealed class BackpackUiStructureTests
    {
        private readonly List<GameObject> created = new();
        private WeaponAssetCatalog catalogAsset;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            created.Clear();
            if (catalogAsset != null) ScriptableObject.DestroyImmediate(catalogAsset);
        }

        private LobbyPresenter CreatePresenter()
        {
            var root = new GameObject("PresenterRoot");
            created.Add(root);
            var presenter = root.AddComponent<LobbyPresenter>();
            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(root.transform, false);
            SetField(presenter, "body", bodyGo.transform);
            var statusGo = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            statusGo.transform.SetParent(root.transform, false);
            SetField(presenter, "status", statusGo.GetComponent<TextMeshProUGUI>());

            var session = new AccountSession();
            session.Apply(new AuthSessionDto
            {
                token = "t",
                expiresAtUtc = DateTime.UtcNow.AddHours(1).ToString("o"),
                profile = new PlayerProfileDto { username = "Tester", level = 1, xp = 0, xpToNextLevel = 100, coins = 500 },
                loadout = Backpack(0, "weapon.m4", "weapon.service_pistol"),
                backpacks = BuildSet(),
            });
            SetField(presenter, "session", session);
            catalogAsset = WeaponAssetCatalog.CreateRuntime();
            var realCatalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            foreach (var entry in catalogAsset.Entries)
                if (entry != null && entry.definition == null && realCatalog.TryGet(entry.itemId, out var realEntry) && realEntry != null)
                    entry.definition = realEntry.definition;
            SetField(presenter, "weaponAssets", catalogAsset);
            SetField(presenter, "cachedCatalog", new ShopCatalogDto
            {
                coins = 500, level = 1,
                items = new[]
                {
                    new CatalogItemDto { itemId = "weapon.m4", itemType = "Weapon", slotType = "Primary", displayName = "M4", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Initial" },
                    new CatalogItemDto { itemId = "weapon.smg01", itemType = "Weapon", slotType = "Primary", displayName = "SMG-01", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Shop" },
                    new CatalogItemDto { itemId = "weapon.service_pistol", itemType = "Weapon", slotType = "Secondary", displayName = "Service Pistol", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Initial" },
                    new CatalogItemDto { itemId = "throwable.standard", itemType = "Throwable", slotType = "Throwable", displayName = "标准投掷包", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Initial" },
                },
            });
            SetField(presenter, "cachedInventory", new InventoryDto { coins = 500, items = Array.Empty<InventoryItemDto>() });
            return presenter;
        }

        private static LoadoutDto Backpack(int index, string primary, string secondary, string throwable = "throwable.standard")
            => new() { primaryWeaponId = primary, secondaryWeaponId = secondary, throwableId = throwable, version = 1 + index, backpackIndex = index };

        private static BackpackSetDto BuildSet() => new()
        {
            activeIndex = 0,
            backpacks = new[]
            {
                Backpack(0, "weapon.m4", "weapon.service_pistol"),
                Backpack(1, "weapon.smg01", "weapon.service_pistol", null),
                Backpack(2, "weapon.m4", "weapon.service_pistol"),
            },
        };

        private static void SetField(object target, string name, object value)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static object GetField(object target, string name)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            return field.GetValue(target);
        }

        private static void Invoke(object target, string method)
        {
            var info = typeof(LobbyPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            info.Invoke(target, Array.Empty<object>());
        }

        private static Transform FindDescendant(Transform parent, string name)
            => parent.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name);

        private static List<string> AllTexts(Transform root)
            => root.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text).ToList();

        // ---- 仓库页 ----

        [Test]
        public void Armory_RendersBackpackTabsAndAssemblyStrip()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderArmoryPage");
            var body = (Transform)GetField(presenter, "body");
            var page = body.Find("ArmoryPage");
            Assert.That(page, Is.Not.Null);

            Assert.That(page.Find("BackpackTab_1"), Is.Not.Null, "背包页签 1 缺失");
            Assert.That(page.Find("BackpackTab_2"), Is.Not.Null, "背包页签 2 缺失");
            Assert.That(page.Find("BackpackTab_3"), Is.Not.Null, "背包页签 3 缺失");
            Assert.That(FindDescendant(page, "ThrowableCycleButton"), Is.Null);
            for(int i=0;i<3;i++) Assert.That(FindDescendant(page,"ThrowableSlot"+i),Is.Not.Null);

            var stripTexts = AllTexts(page);
            // WeaponName 走 definition.DisplayName（weapon.m4 定义名=AK-47——历史模型互换命名，Docs/21）
            var realCatalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.That(realCatalog.TryResolveDefinition("weapon.m4", out var m4Definition) && m4Definition != null, Is.True);
            Assert.That(stripTexts.Any(t => t.Contains("主") && t.Contains(m4Definition.DisplayName)), Is.True,
                "装配条必须显示所选背包主武器");
            // 网格为装配条让位（0.835 上沿）
            var grid = FindDescendant(page, "WeaponGrid");
            Assert.That(grid, Is.Not.Null);
            var gridRect = (RectTransform)grid;
            Assert.That(gridRect.anchorMax.y, Is.EqualTo(0.725f).Within(0.001f), "仓库网格上沿应收窄到装配条之下");
        }

        [Test]
        public void Armory_BackpackTabClick_ScopesEquippedBadgeToSelectedBackpack()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderArmoryPage");
            var body = (Transform)GetField(presenter, "body");
            body.Find("ArmoryPage/BackpackTab_2")?.GetComponent<Button>().onClick.Invoke();

            // EditMode 下 ClearBody 的 Destroy 延迟到帧末——取最后一次重建的 ArmoryPage
            Transform page = null;
            foreach (Transform child in body)
                if (child.name == "ArmoryPage") page = child;
            Assert.That(page, Is.Not.Null);

            // 背包 2（index 1）= smg01：徽标跟随所选背包而非活动背包
            Assert.That(FindDescendant(page, "WeaponCard_weapon.smg01")?.GetComponentsInChildren<TMP_Text>()
                .FirstOrDefault(x => x.text == "已装备"), Is.Not.Null, "背包 2 装备的枪必须显示已装备");
            Assert.That(FindDescendant(page, "WeaponCard_weapon.m4")?.GetComponentsInChildren<TMP_Text>()
                .FirstOrDefault(x => x.text == "已装备"), Is.Null, "背包 1 的枪在背包 2 视角下不得显示已装备");
            var stripTexts = AllTexts(page);
            var realCatalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.That(realCatalog.TryResolveDefinition("weapon.smg01", out var smgDefinition) && smgDefinition != null, Is.True);
            Assert.That(stripTexts.Any(t => t.Contains(smgDefinition.DisplayName)), Is.True, "装配条摘要必须切到背包 2 主武器");
        }

        // ---- 大厅装配卡 ----

        [Test]
        public void Lobby_LoadoutCard_ShowsBackpackTabsAndAssemblyDetails()
        {
            var presenter = CreatePresenter();
            // RefreshLobbyLoadout 有 currentPage==Lobby 早退——离线直渲必须显式置位
            SetField(presenter, "currentPage", LobbyPage.Lobby);
            Invoke(presenter, "RenderTacticalLobby");
            var body = (Transform)GetField(presenter, "body");
            var card = body.Find("MainPage/LoadoutCard");
            if (card == null)
            {
                // 页面根命名随版本可能不同——按卡片名全树搜索
                card = FindDescendant(body, "LoadoutCard");
            }
            Assert.That(card, Is.Not.Null, "大厅装配卡缺失");

            Assert.That(FindDescendant(card, "LobbyBackpackTab_1"), Is.Not.Null);
            Assert.That(FindDescendant(card, "LobbyBackpackTab_2"), Is.Not.Null);
            Assert.That(FindDescendant(card, "LobbyBackpackTab_3"), Is.Not.Null);
            for(int i=0;i<3;i++) Assert.That(FindDescendant(card,"LobbyThrowableSlot"+i),Is.Not.Null);
            Assert.That(FindDescendant(card, "LoadoutAttachmentSummary"), Is.Not.Null, "配件摘要行缺失");

            var texts = AllTexts(card);
            var realCatalog2 = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.That(realCatalog2.TryResolveDefinition("weapon.m4", out var m4Def2) && m4Def2 != null, Is.True);
            Assert.That(texts.Any(t => t.Contains(m4Def2.DisplayName)), Is.True, "默认预览背包 1 主武器");

            // 页签切到背包 2（smg01/无投掷）——摘要必须跟随
            FindDescendant(card, "LobbyBackpackTab_2").GetComponent<Button>().onClick.Invoke();
            texts = AllTexts(card);
            Assert.That(realCatalog2.TryResolveDefinition("weapon.smg01", out var smgDef2) && smgDef2 != null, Is.True);
            Assert.That(texts.Any(t => t.Contains(smgDef2.DisplayName)), Is.True, "预览切背包 2 后主武器行必须更新");
            Assert.That(texts.Any(t => t.Contains("未装备")), Is.True, "背包 2 无投掷物必须显示「不带」");
        }
    }
}
