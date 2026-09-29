using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game.Account;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>Offline structure tests for restyled sub pages (Docs/20 Step 5). No backend calls made.</summary>
    public sealed class LobbySubPageTests
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
            PlayerPrefs.DeleteKey("unityfps.settings.music");
            PlayerPrefs.DeleteKey("unityfps.settings.sensitivity");
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
            });
            SetField(presenter, "session", session);
            catalogAsset = WeaponAssetCatalog.CreateRuntime();
            // D4-A.4（Gate A 复审 §2.3）：IsLpfpWeaponItem 契约要求 entry.definition 可解析——
            // CreateRuntime 兜底条目无定义引用，注入真实 Resources 目录中的同 id LPFP 定义，
            // 让商城卡片走真实过滤逻辑（而非删除断言）。
            var realCatalog = Resources.Load<WeaponAssetCatalog>("WeaponAssetCatalog");
            Assert.That(realCatalog, Is.Not.Null, "真实 WeaponAssetCatalog 必须在 Resources 下可加载");
            foreach (var entry in catalogAsset.Entries)
                if (entry != null && entry.definition == null && realCatalog.TryGet(entry.itemId, out var realEntry) && realEntry != null)
                    entry.definition = realEntry.definition;
            SetField(presenter, "weaponAssets", catalogAsset);
            return presenter;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static void Invoke(object target, string method, params object[] args)
        {
            var info = typeof(LobbyPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            info.Invoke(target, args);
        }

        private static List<string> AllTexts(Transform root)
        {
            var list = new List<string>();
            foreach (var t in root.GetComponentsInChildren<TMP_Text>(true))
                list.Add(t.text);
            return list;
        }

        [Test]
        public void Catalog_RendersOwnedAndLockedCards()
        {
            var presenter = CreatePresenter();
            SetField(presenter, "cachedCatalog", new ShopCatalogDto
            {
                coins = 500,
                level = 1,
                items = new[]
                {
                    // 2026-09-07 契约：商城页只显示 acquisitionSource=Shop 的行（初始武器进仓库不进商城）
                    new CatalogItemDto { itemId = "weapon.m4", itemType = "Weapon", slotType = "Primary", displayName = "M4", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Shop" },
                    new CatalogItemDto { itemId = "weapon.ak", itemType = "Weapon", slotType = "Primary", displayName = "AK", unlockLevel = 99, priceCoins = 1000, isOwned = false, isActive = true, isImplemented = true, acquisitionSource = "Shop" },
                },
            });
            SetField(presenter, "cachedInventory", new InventoryDto { coins = 500, items = Array.Empty<InventoryItemDto>() });

            Invoke(presenter, "RenderCatalog", true);
            var body = GetField<Transform>(presenter, "body");
            var ownedCard = body.Find("ShopPage/WeaponGrid/Viewport/Content/WeaponRow/WeaponCard_weapon.m4");
            var lockedCard = body.Find("ShopPage/WeaponGrid/Viewport/Content/WeaponRow/WeaponCard_weapon.ak");
            Assert.That(ownedCard, Is.Not.Null);
            Assert.That(lockedCard, Is.Not.Null);
            Assert.That(AllTexts(ownedCard), Has.Member("已拥有"));
            lockedCard.GetComponent<Button>().onClick.Invoke();
            var detail = body.Find("ShopPage/SelectionDetails");
            Assert.That(AllTexts(detail), Has.Member("等级 99 解锁"));
            Assert.That(detail.GetComponentsInChildren<Button>()[1].interactable, Is.False);

            // Filter pills exist for all five categories.
            var texts = AllTexts(body.Find("ShopPage"));
            foreach (var label in new[] { "步枪", "手枪", "霰弹枪", "冲锋枪", "狙击枪" })
                Assert.That(texts, Has.Member(label));
        }

        [Test]
        public void RetiredPagesCannotBeNavigatedByName()
        {
            Assert.That(Enum.GetNames(typeof(LobbyPage)), Does.Not.Contain("Upgrades"));
            Assert.That(Enum.GetNames(typeof(LobbyPage)), Does.Not.Contain("Mission"));
            Assert.That((int)LobbyPage.Settings, Is.EqualTo(10));
            Assert.That(typeof(IApiClient).GetMethod("UpdateUpgradesAsync"), Is.Null);
        }

        [Test]
        public void Settings_BuildsAudioKeybindGraphicsCards()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderSettings");
            var body = GetField<Transform>(presenter, "body");
            var page = body.Find("SettingsPage");
            Assert.That(page, Is.Not.Null);
            Assert.That(page.Find("AudioCard"), Is.Not.Null);
            Assert.That(page.Find("KeybindCard"), Is.Not.Null);
            Assert.That(page.Find("GraphicsCard"), Is.Not.Null);

            // 共享设置 Phase B：音量三层（Master/Music/SFX）+ 灵敏度 = 4 条滑杆
            var sliders = page.Find("AudioCard").GetComponentsInChildren<Slider>(true);
            Assert.That(sliders.Length, Is.EqualTo(4));
            var cardTexts = AllTexts(page.Find("AudioCard"));
            Assert.That(cardTexts, Has.Some.EqualTo("主音量"));
            Assert.That(cardTexts, Has.Some.EqualTo("音乐音量"));
            Assert.That(cardTexts, Has.Some.EqualTo("音效音量"));
            Assert.That(cardTexts, Has.Some.StartsWith("鼠标灵敏度"));
            var keyTexts = AllTexts(page.Find("KeybindCard"));
            Assert.That(keyTexts, Has.Member("前进"));
            Assert.That(keyTexts, Has.Member("换弹"));
            Assert.That(keyTexts, Has.Member("左探头"));
            Assert.That(keyTexts, Has.Member("右探头"));
            Assert.That(keyTexts, Has.Member("选择 / 切换投掷物"));
            Assert.That(keyTexts, Does.Not.Contain("快速切枪"));
            var gfxTexts = AllTexts(page.Find("GraphicsCard"));
            Assert.That(gfxTexts, Has.Member("分辨率"));
            Assert.That(gfxTexts, Has.Member("帧率上限"));
            Assert.That(gfxTexts, Has.Some.StartsWith("开镜方式："));
            Assert.That(gfxTexts, Has.Some.StartsWith("镜片准星："));
            Assert.That(gfxTexts, Has.Some.StartsWith("准星颜色："));
            // 应用 / 取消（回滚）操作行（Phase B 语义）
            var pageTexts = AllTexts(page);
            Assert.That(pageTexts, Has.Member("应用并保存"));
            Assert.That(pageTexts, Has.Member("取消（回滚）"));
            Assert.That(pageTexts, Has.Member("恢复默认"));
        }

        [Test]
        public void OfflineLaunchMethodIsRetired()
        {
            Assert.That(typeof(LobbyPresenter).GetMethod("StartGameplayAsync", BindingFlags.NonPublic | BindingFlags.Instance), Is.Null);
        }

        // ---------- 2026-09-16 需求1：仓库 CF 风（左列类型标签 + 大网格 + 仅已拥有） ----------

        private LobbyPresenter CreateArmoryPresenter()
        {
            var presenter = CreatePresenter();
            SetField(presenter, "cachedCatalog", new ShopCatalogDto
            {
                coins = 500,
                level = 1,
                items = new[]
                {
                    new CatalogItemDto { itemId = "weapon.m4", itemType = "Weapon", slotType = "Primary", displayName = "M4", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Initial" },
                    new CatalogItemDto { itemId = "weapon.ak", itemType = "Weapon", slotType = "Primary", displayName = "AK", priceCoins = 1000, isOwned = false, isActive = true, isImplemented = true, acquisitionSource = "Shop" },
                    new CatalogItemDto { itemId = "weapon.smg01", itemType = "Weapon", slotType = "Primary", displayName = "SMG-01", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Shop" },
                    new CatalogItemDto { itemId = "weapon.service_pistol", itemType = "Weapon", slotType = "Secondary", displayName = "Service Pistol", isOwned = true, isActive = true, isImplemented = true, acquisitionSource = "Initial" },
                },
            });
            SetField(presenter, "cachedInventory", new InventoryDto { coins = 500, items = Array.Empty<InventoryItemDto>() });
            return presenter;
        }

        [Test]
        public void Armory_ShowsOnlyOwnedWeapons_WithTypeTabsAndCardActions()
        {
            var presenter = CreateArmoryPresenter();
            Invoke(presenter, "RenderArmoryPage");
            var body = GetField<Transform>(presenter, "body");
            var page = body.Find("ArmoryPage");
            Assert.That(page, Is.Not.Null);

            Assert.That(FindDescendant(page, "WeaponCard_weapon.m4"), Is.Not.Null, "初始枪 M4 必须在仓库可见");
            Assert.That(FindDescendant(page, "WeaponCard_weapon.service_pistol"), Is.Not.Null, "初始枪 Service Pistol 必须在仓库可见");
            Assert.That(FindDescendant(page, "WeaponCard_weapon.smg01"), Is.Not.Null);
            Assert.That(FindDescendant(page, "WeaponCard_weapon.ak"), Is.Null, "未拥有武器不出现在仓库");

            foreach (var key in new[] { "All", "Rifle", "Smg", "Sniper", "Shotgun", "Pistol" })
                Assert.That(page.Find("Category_" + key), Is.Not.Null, $"缺少类型标签 {key}");

            var card = FindDescendant(page, "WeaponCard_weapon.m4");
            Assert.That(card.Find("WeaponIcon"), Is.Not.Null);
            var detail = page.Find("SelectionDetails");
            Assert.That(detail.GetComponentsInChildren<Button>().Any(b => b.name == "EquipSelectedWeapon"), Is.True);
            Assert.That(AllTexts(detail), Does.Contain("检视武器"));
            Assert.That(AllTexts(detail), Does.Contain("改装配件"));
        }

        [Test]
        public void Armory_TypeTabClick_FiltersToPistolOnly()
        {
            var presenter = CreateArmoryPresenter();
            Invoke(presenter, "RenderArmoryPage");
            var body = GetField<Transform>(presenter, "body");
            var pistolTab = body.Find("ArmoryPage/Category_Pistol")?.GetComponent<Button>();
            Assert.That(pistolTab, Is.Not.Null);
            pistolTab.onClick.Invoke();

            // EditMode 下 ClearBody 的 Destroy 延迟到帧末——取最后一次重建的 ArmoryPage
            Transform page = null;
            foreach (Transform child in body)
                if (child.name == "ArmoryPage") page = child;
            Assert.That(page, Is.Not.Null);
            Assert.That(FindDescendant(page, "WeaponCard_weapon.service_pistol"), Is.Not.Null);
            Assert.That(FindDescendant(page, "WeaponCard_weapon.m4"), Is.Null);
            Assert.That(FindDescendant(page, "WeaponCard_weapon.smg01"), Is.Null);
        }

        [Test]
        public void Armory_EquippedBadge_FollowsServerLoadout()
        {
            var presenter = CreateArmoryPresenter();
            var session = GetField<AccountSession>(presenter, "session");
            session.ApplyLoadout(new LoadoutDto { primaryWeaponId = "weapon.m4", secondaryWeaponId = "weapon.service_pistol" });
            Invoke(presenter, "RenderArmoryPage");
            var body = GetField<Transform>(presenter, "body");
            var page = body.Find("ArmoryPage");
            Assert.That(FindDescendant(page, "WeaponCard_weapon.m4")?.GetComponentsInChildren<TMP_Text>().FirstOrDefault(x => x.text == "已装备"), Is.Not.Null);
            Assert.That(FindDescendant(page, "WeaponCard_weapon.service_pistol")?.GetComponentsInChildren<TMP_Text>().FirstOrDefault(x => x.text == "已装备"), Is.Not.Null);
            Assert.That(FindDescendant(page, "WeaponCard_weapon.smg01")?.GetComponentsInChildren<TMP_Text>().FirstOrDefault(x => x.text == "已装备"), Is.Null);
        }

        private static Transform FindDescendant(Transform parent, string name) => parent.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name);

        private static T GetField<T>(object target, string name)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            return (T)field.GetValue(target);
        }
    }
}
