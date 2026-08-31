using System;
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
                profile = new PlayerProfileDto { username = "Tester", level = 1, xp = 0, xpToNextLevel = 100, skillPoints = 0, coins = 500, upgrades = new UpgradeLevelsDto { upDamage = 2 } },
            });
            SetField(presenter, "session", session);
            catalogAsset = WeaponAssetCatalog.CreateRuntime();
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
                    new CatalogItemDto { itemId = "weapon.m4", itemType = "Weapon", slotType = "Primary", displayName = "M4", isOwned = true, isActive = true, isImplemented = true },
                    new CatalogItemDto { itemId = "weapon.ak", itemType = "Weapon", slotType = "Primary", displayName = "AK", unlockLevel = 99, priceCoins = 1000, isOwned = false, isActive = true, isImplemented = true },
                },
            });
            SetField(presenter, "cachedInventory", new InventoryDto { coins = 500, items = Array.Empty<InventoryItemDto>() });

            Invoke(presenter, "RenderCatalog", true);
            var body = GetField<Transform>(presenter, "body");
            var ownedCard = body.Find("ShopPage/WeaponCard_weapon.m4");
            var lockedCard = body.Find("ShopPage/WeaponCard_weapon.ak");
            Assert.That(ownedCard, Is.Not.Null);
            Assert.That(lockedCard, Is.Not.Null);
            Assert.That(AllTexts(ownedCard), Has.Member("已拥有"));
            Assert.That(AllTexts(lockedCard), Has.Member("等级 99 解锁"));

            // Locked card: buy button (on Face child of Buy_*) present but disabled.
            var buyFace = lockedCard.Find("Buy_weapon.ak/Face");
            Assert.That(buyFace, Is.Not.Null, "Buy button face should exist");
            var buy = buyFace.GetComponent<Button>();
            Assert.That(buy, Is.Not.Null);
            Assert.That(buy.interactable, Is.False);

            // Filter pills exist for all five categories.
            var texts = AllTexts(body.Find("ShopPage"));
            foreach (var label in new[] { "步枪", "手枪", "霰弹枪", "冲锋枪", "狙击枪" })
                Assert.That(texts, Has.Member(label));
        }

        [Test]
        public void Upgrades_PlusButton_RefreshesPendingValue()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderUpgrades");
            var body = GetField<Transform>(presenter, "body");
            var row = body.Find("UpgradesPage/Upgrade_伤害");
            Assert.That(row, Is.Not.Null);
            var plusFace = row.Find("Plus/Face");
            Assert.That(plusFace, Is.Not.Null, "upgrade + button face should exist");
            plusFace.GetComponent<Button>().onClick.Invoke();
            Assert.That(AllTexts(row), Has.Member("伤害  3/5"));
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

            var sliders = page.Find("AudioCard").GetComponentsInChildren<Slider>(true);
            Assert.That(sliders.Length, Is.EqualTo(3));
            var adsToggle = page.Find("AudioCard").Find("AdsModeToggle");
            Assert.That(adsToggle, Is.Not.Null, "设置页应有开镜方式切换按钮");
            Assert.That(AllTexts(page.Find("AudioCard")), Has.Some.StartsWith("开镜方式："));
            var keyTexts = AllTexts(page.Find("KeybindCard"));
            Assert.That(keyTexts, Has.Member("前进"));
            Assert.That(keyTexts, Has.Member("换弹"));
            var gfxTexts = AllTexts(page.Find("GraphicsCard"));
            Assert.That(gfxTexts, Has.Member("分辨率"));
            Assert.That(gfxTexts, Has.Member("帧率上限"));
        }

        [Test]
        public void Mission_BuildsCardAndCtas()
        {
            var presenter = CreatePresenter();
            Invoke(presenter, "RenderMission");
            var body = GetField<Transform>(presenter, "body");
            var page = body.Find("MissionPage");
            Assert.That(page, Is.Not.Null);
            Assert.That(page.Find("MapCard"), Is.Not.Null);
            var texts = AllTexts(page);
            Assert.That(texts, Has.Member("开始本地任务"));
            Assert.That(texts, Has.Member("返回大厅"));
        }

        private static T GetField<T>(object target, string name)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            return (T)field.GetValue(target);
        }
    }
}
