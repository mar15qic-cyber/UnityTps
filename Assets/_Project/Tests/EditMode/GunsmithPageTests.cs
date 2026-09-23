using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Game.Account;
using Game.Gameplay.Weapon;
using Game.UI;

namespace Game.Gameplay.Tests
{
    /// <summary>
    /// 枪匠页离线结构测试（Docs/21 Phase F）：反射直调 BuildGunsmithPage（无 AppRoot/api），
    /// 断言槽位显隐（按兼容矩阵）、选项面板（拥有可点/未拥有禁用）、属性对比与保存/返回结构。
    /// </summary>
    public sealed class GunsmithPageTests
    {
        private const string DraftWeaponId = "weapon.stagea.gunsmith-draft";
        private const string DraftPrefix = "Game.GunsmithDraft.";
        private readonly List<GameObject> created = new();
        private WeaponAssetCatalog catalogAsset;

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            created.Clear();
            if (catalogAsset != null) ScriptableObject.DestroyImmediate(catalogAsset);
            PlayerPrefs.DeleteKey(DraftPrefix + DraftWeaponId);
            PlayerPrefs.Save();
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
            SetField(presenter, "weaponAssets", catalogAsset);
            return presenter;
        }

        private static void SetField(object target, string name, object value)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, $"field {name} missing");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            var field = typeof(LobbyPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            return (T)field.GetValue(target);
        }

        private static void Invoke(object target, string method)
        {
            var info = typeof(LobbyPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            info.Invoke(target, null);
        }

        private static T Invoke<T>(object target, string method, params object[] args)
        {
            var info = typeof(LobbyPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(info, Is.Not.Null, $"method {method} missing");
            return (T)info.Invoke(target, args);
        }

        private void SetGunsmithState(LobbyPresenter presenter, Dictionary<string, string> selections, string activeSlot = null)
        {
            var selectionsField = GetField<Dictionary<string, string>>(presenter, "gunsmithSelections");
            selectionsField.Clear();
            foreach (var kv in selections) selectionsField[kv.Key] = kv.Value;
            SetField(presenter, "gunsmithActiveSlot", activeSlot);
        }

        [Test]
        public void Gunsmith_RendersSlotsByCompatMatrix_AndHidesMissingSlots()
        {
            var presenter = CreatePresenter();
            SetField(presenter, "selectedWeapon", new CatalogItemDto
            {
                itemId = "weapon.m4", itemType = "Weapon", slotType = "Primary",
                displayName = "AKM", isOwned = true, isActive = true, isImplemented = true
            });
            // 该 fixture 没有有效 Optic 兼容行；Muzzle/Magazine 有，旧/缺失目录项应被过滤。
            SetField(presenter, "cachedCompatibility", new[]
            {
                new AttachmentCompatibilityDto { weaponId = "weapon.m4", attachmentId = "attach.lpfp.muffler.01", slotType = "Muzzle", isImplemented = true, calibrationKey = "socket-v1" },
                new AttachmentCompatibilityDto { weaponId = "weapon.m4", attachmentId = "attach.lpw.muffler.01_1", slotType = "Muzzle", isImplemented = true, calibrationKey = "socket-v1" },
                new AttachmentCompatibilityDto { weaponId = "weapon.m4", attachmentId = "attach.rifle.magazine", slotType = "Magazine", isImplemented = true, calibrationKey = "stat-only" },
                new AttachmentCompatibilityDto { weaponId = "weapon.m4", attachmentId = "attach.lpw.grip.01", slotType = "Underbarrel", isImplemented = false, calibrationKey = "pending" },
            });
            SetField(presenter, "cachedInventory", new InventoryDto
            {
                coins = 500,
                items = new[] { new InventoryItemDto { itemId = "attach.lpfp.muffler.01", quantity = 1, item = null } }
            });
            SetGunsmithState(presenter, new Dictionary<string, string> { { "Muzzle", "attach.lpfp.muffler.01" } });

            Invoke(presenter, "BuildGunsmithPage");
            var body = GetField<Transform>(presenter, "body");

            Assert.That(body.Find("GunsmithPage"), Is.Not.Null, "GunsmithPage 根应存在");
            Assert.That(body.Find("GunsmithPage/SlotRow_Muzzle"), Is.Not.Null, "Muzzle 槽行应存在");
            Assert.That(body.Find("GunsmithPage/SlotRow_Muzzle/SlotSelection_Muzzle"), Is.Not.Null);
            Assert.That(body.Find("GunsmithPage/SlotRow_Muzzle/SlotSelection_Muzzle").GetComponent<TMP_Text>().text,
                Does.Contain("经典消音器"), "已选配件应显示目录显示名");
            Assert.That(body.Find("GunsmithPage/SlotRow_Muzzle/SlotCheck_Muzzle"), Is.Not.Null, "已装槽应有 ✓ 角标");
            Assert.That(body.Find("GunsmithPage/SlotRow_Magazine"), Is.Not.Null, "Magazine 槽行应存在");
            Assert.That(body.Find("GunsmithPage/SlotRow_Underbarrel"), Is.Not.Null, "未放行槽行也应显示（诚实呈现'尚未适配'）");
            Assert.IsNull(body.Find("GunsmithPage/SlotRow_Optic"), "无导轨枪不得出现 Optic 槽行");
            Assert.That(body.Find("GunsmithPage/GunsmithStats"), Is.Not.Null, "属性条面板应存在");
            Assert.That(body.Find("GunsmithPage/GunsmithStats/Track_弹容量"), Is.Not.Null, "弹容量横条应存在");
            var magText = FindStatValueText(body, "弹容量");
            Assert.That(magText, Is.Not.Null);
            Assert.That(magText.text, Does.Contain("30"), "基础弹容 30 应显示");
        }

        [Test]
        public void Gunsmith_PickerShowsOwnedAndUnownedOptions()
        {
            var presenter = CreatePresenter();
            SetField(presenter, "selectedWeapon", new CatalogItemDto
            {
                itemId = "weapon.ak", itemType = "Weapon", slotType = "Primary",
                displayName = "M4A1", isOwned = true, isActive = true, isImplemented = true
            });
            SetField(presenter, "cachedCompatibility", new[]
            {
                new AttachmentCompatibilityDto { weaponId = "weapon.ak", attachmentId = "attach.lpfp.optic.01", slotType = "Optic", isImplemented = true, calibrationKey = "socket-v1" },
                new AttachmentCompatibilityDto { weaponId = "weapon.ak", attachmentId = "attach.rifle.optic", slotType = "Optic", isImplemented = true, calibrationKey = "socket-v1" },
                new AttachmentCompatibilityDto { weaponId = "weapon.ak", attachmentId = "attach.lpfp.optic.03", slotType = "Optic", isImplemented = true, calibrationKey = "socket-v1" },
                new AttachmentCompatibilityDto { weaponId = "weapon.ak", attachmentId = "attach.lpfp.optic.02", slotType = "Optic", isImplemented = true, calibrationKey = "socket-v1" },
                new AttachmentCompatibilityDto { weaponId = "weapon.ak", attachmentId = "attach.lpw.optic.01", slotType = "Optic", isImplemented = true, calibrationKey = "socket-v1" },
            });
            SetField(presenter, "cachedInventory", new InventoryDto
            {
                coins = 500,
                items = new[] { new InventoryItemDto { itemId = "attach.lpfp.optic.01", quantity = 1, item = null } }
            });
            SetGunsmithState(presenter, new Dictionary<string, string>(), activeSlot: "Optic");

            Invoke(presenter, "BuildGunsmithPage");
            var body = GetField<Transform>(presenter, "body");

            var drawer = body.Find("GunsmithPage/SlotDrawer_Optic");
            Assert.That(drawer, Is.Not.Null, "激活槽应展开侧边抽屉");
            // 滚动层级：drawer/OptionList/OptionContent/Option_x/OptionEquip_x|OptionBuy_x
            Assert.That(drawer.Find("OptionList/OptionContent/Option_attach.lpfp.optic.01/OptionEquip_attach.lpfp.optic.01"), Is.Not.Null, "Scope_01 已拥有件应有装备按钮");
            Assert.That(drawer.Find("OptionList/OptionContent/Option_attach.rifle.optic/OptionBuy_attach.rifle.optic"), Is.Not.Null, "Scope_02 未拥有件应有购买按钮");
            Assert.That(drawer.Find("OptionList/OptionContent/Option_attach.lpfp.optic.03/OptionBuy_attach.lpfp.optic.03"), Is.Not.Null, "Scope_03 未拥有件应有购买按钮");
            Assert.That(drawer.Find("OptionList/OptionContent/Option_attach.lpfp.optic.02/OptionBuy_attach.lpfp.optic.02"), Is.Not.Null, "Scope_04 未拥有件应有购买按钮");
            Assert.IsNull(drawer.Find("OptionList/OptionContent/Option_attach.lpfp.optic.01/OptionBuy_attach.lpfp.optic.01"), "已拥有件不应有购买按钮");
            Assert.IsNull(drawer.Find("OptionList/OptionContent/Option_attach.rifle.optic/OptionEquip_attach.rifle.optic"), "未拥有件不应有装备按钮");
            Assert.IsNull(drawer.Find("OptionList/OptionContent/Option_attach.lpw.optic.01"), "退役瞄具不得生成 UI 行");
            Assert.That(drawer.Find("Option_Clear"), Is.Not.Null, "应有卸下按钮");
            Assert.That(drawer.Find("Option_Close"), Is.Not.Null, "应有收起按钮");
            Assert.IsNull(body.Find("GunsmithPage/SlotRow_Muzzle"), "假数据无 Muzzle 兼容行则不渲染该槽（按矩阵显隐）");
        }

        [TestCase("weapon.ak", "M4A1")]
        [TestCase("weapon.rifle03", "SCAR")]
        public void Gunsmith_BuiltInGripRiflesRejectStaleServerUnderbarrelRows(string weaponId, string displayName)
        {
            var presenter = CreatePresenter();
            var authoredCatalog = UnityEditor.AssetDatabase.LoadAssetAtPath<WeaponAssetCatalog>(
                "Assets/_Project/Resources/WeaponAssetCatalog.asset");
            Assert.That(authoredCatalog, Is.Not.Null);
            SetField(presenter, "weaponAssets", authoredCatalog);
            SetField(presenter, "selectedWeapon", new CatalogItemDto
            {
                itemId = weaponId, itemType = "Weapon", slotType = "Primary",
                displayName = displayName, isOwned = true, isActive = true, isImplemented = true
            });
            SetField(presenter, "cachedCompatibility", new[]
            {
                // Simulates an old backend/database row that survives one deployment cycle.
                new AttachmentCompatibilityDto
                {
                    weaponId = weaponId, attachmentId = "attach.lpw.grip.01",
                    slotType = "Underbarrel", isImplemented = true, calibrationKey = "socket-v2"
                },
            });
            SetField(presenter, "cachedInventory", new InventoryDto { coins = 500, items = Array.Empty<InventoryItemDto>() });
            SetGunsmithState(presenter, new Dictionary<string, string>());

            Invoke(presenter, "BuildGunsmithPage");
            var body = GetField<Transform>(presenter, "body");
            Assert.That(body.Find("GunsmithPage/SlotRow_Underbarrel"), Is.Null,
                $"{displayName} 自带不可拆垂直握把，旧兼容行不得重新开放下挂握把");
        }

        [Test]
        public void Gunsmith_MagazineExtendsStatBar()
        {
            var presenter = CreatePresenter();
            SetField(presenter, "selectedWeapon", new CatalogItemDto
            {
                itemId = "weapon.m4", itemType = "Weapon", slotType = "Primary",
                displayName = "AKM", isOwned = true, isActive = true, isImplemented = true
            });
            SetField(presenter, "cachedCompatibility", new[]
            {
                new AttachmentCompatibilityDto { weaponId = "weapon.m4", attachmentId = "attach.rifle.magazine", slotType = "Magazine", isImplemented = true, calibrationKey = "stat-only" },
            });
            SetField(presenter, "cachedInventory", new InventoryDto
            {
                coins = 500,
                items = new[] { new InventoryItemDto { itemId = "attach.rifle.magazine", quantity = 1, item = null } }
            });
            SetGunsmithState(presenter, new Dictionary<string, string> { { "Magazine", "attach.rifle.magazine" } });

            Invoke(presenter, "BuildGunsmithPage");
            var body = GetField<Transform>(presenter, "body");
            // weapon.m4 基础弹容 30 + 步枪加长弹匣 +8 = 38（本地 AttachmentAssetCatalog 数值草案）
            var valueText = FindStatValueText(body, "弹容量");
            Assert.That(valueText, Is.Not.Null, "弹容量数值文本应存在");
            Assert.That(valueText.text, Does.Contain("30 → 38"), "加长弹匣应把弹容量 30 提到 38");
        }

        [Test]
        public void Gunsmith_InvalidCrossWeaponDraftKeepsValidServerSelectionAndClearsDraft()
        {
            var presenter = CreatePresenter();
            SetField(presenter, "selectedWeapon", new CatalogItemDto
            {
                itemId = DraftWeaponId, itemType = "Weapon", slotType = "Primary",
                displayName = "Draft filter fixture", isOwned = true, isActive = true, isImplemented = true
            });
            SetField(presenter, "cachedCompatibility", new[]
            {
                new AttachmentCompatibilityDto
                {
                    weaponId = DraftWeaponId, attachmentId = "attach.lpfp.optic.01", slotType = "Optic",
                    isImplemented = true, calibrationKey = "socket-v1"
                },
            });
            SetGunsmithState(presenter, new Dictionary<string, string>
            {
                { "Optic", "attach.lpfp.optic.01" },
            });
            GunsmithDraftStore.Save(DraftWeaponId, new Dictionary<string, string>
            {
                // 目录中存在，但当前武器没有这条兼容矩阵记录。
                { "Optic", "attach.rifle.optic" },
            });

            var draft = new Dictionary<string, string>();
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var applied = Invoke<bool>(presenter, "ApplyDraftIfValid", catalog, DraftWeaponId, draft);
            var selections = GetField<Dictionary<string, string>>(presenter, "gunsmithSelections");

            Assert.IsFalse(applied, "无当前武器兼容项的草稿不应覆盖服务器配装");
            Assert.That(selections["Optic"], Is.EqualTo("attach.lpfp.optic.01"));
            Assert.IsEmpty(draft);
            Assert.IsFalse(PlayerPrefs.HasKey(DraftPrefix + DraftWeaponId), "无效草稿应被清理");
        }

        [Test]
        public void Gunsmith_AllRetiredDraftKeepsValidServerSelectionAndIsRemoved()
        {
            var presenter = CreatePresenter();
            SetField(presenter, "selectedWeapon", new CatalogItemDto
            {
                itemId = DraftWeaponId, itemType = "Weapon", slotType = "Primary",
                displayName = "Retired draft fixture", isOwned = true, isActive = true, isImplemented = true
            });
            SetField(presenter, "cachedCompatibility", new[]
            {
                new AttachmentCompatibilityDto
                {
                    weaponId = DraftWeaponId, attachmentId = "attach.lpfp.optic.01", slotType = "Optic",
                    isImplemented = true, calibrationKey = "socket-v1"
                },
            });
            SetGunsmithState(presenter, new Dictionary<string, string>
            {
                { "Optic", "attach.lpfp.optic.01" },
            });
            PlayerPrefs.SetString(DraftPrefix + DraftWeaponId, "{\"Optic\":\"attach.pistol.optic\"}");
            PlayerPrefs.Save();

            var draft = new Dictionary<string, string>();
            var catalog = AttachmentAssetCatalog.LoadOrDefault();
            var applied = Invoke<bool>(presenter, "ApplyDraftIfValid", catalog, DraftWeaponId, draft);
            var selections = GetField<Dictionary<string, string>>(presenter, "gunsmithSelections");

            Assert.IsFalse(applied, "退役草稿不应覆盖服务器配装");
            Assert.That(selections["Optic"], Is.EqualTo("attach.lpfp.optic.01"));
            Assert.IsEmpty(draft);
            Assert.IsFalse(PlayerPrefs.HasKey(DraftPrefix + DraftWeaponId), "全退役草稿应被清理");
        }

        /// <summary>按属性名找数值文本（AddGunsmithStatBar 命名约定 StatValue_{label}）.</summary>
        private static TMP_Text FindStatValueText(Transform body, string statName)
        {
            var value = body.Find("GunsmithPage/GunsmithStats/StatValue_" + statName);
            return value != null ? value.GetComponent<TMP_Text>() : null;
        }
    }
}
