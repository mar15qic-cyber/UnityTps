using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Docs/20 Step 5: restyled sub pages (Mission / Armory / Shop / WeaponDetails / Attachments /
    /// Upgrades / Settings). Business logic (purchase/equip/attachment save) stays in the core
    /// partial untouched; these methods only rebuild the views.
    /// </summary>
    public sealed partial class LobbyPresenter
    {
        // ---------- Mission ----------

        private void RenderMission()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("MissionPage");
            var page = root.transform;
            StyledText(page, "任务 / 地图", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.88f), new Vector2(0.6f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";

            var card = StyledPanel("MapCard", page, UITheme.CardSurface, new Vector2(0.02f, 0.28f), new Vector2(0.60f, 0.78f));
            StyledText(card.transform, "村庄 · 训练行动", UITheme.FontCardTitle + 4, UITheme.TextPrimary,
                new Vector2(0.07f, 0.72f), new Vector2(0.93f, 0.90f), TextAlignmentOptions.Left, FontStyles.Bold).name = "MapTitle";
            StyledText(card.transform, "当前可用：本地 Gameplay\n服务器结算：通过 ClientMatchId 幂等提交 XP 与金币\n在线匹配：尚未接入", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.07f, 0.30f), new Vector2(0.93f, 0.68f), TextAlignmentOptions.Left);
            UIComponents.Badge("ModeBadge", card.transform, "LOCAL", UITheme.AccentInfo,
                new Vector2(0.07f, 0.10f), new Vector2(0.28f, 0.22f));

            StyledButton(page, "开始本地任务", UIComponents.ButtonKind.Primary,
                new Vector2(0.66f, 0.52f), new Vector2(0.95f, 0.68f), StartGameplay);
            StyledButton(page, "返回大厅", UIComponents.ButtonKind.Secondary,
                new Vector2(0.66f, 0.36f), new Vector2(0.95f, 0.49f), () => Navigate(LobbyPage.Lobby));
            PlayEnter(root.gameObject);
        }

        // ---------- Catalog (Armory / Shop) ----------

        private void LoadCatalogAndRender(bool shop, CancellationToken token) => _ = LoadCatalogAsync(shop, token);

        private async Task LoadCatalogAsync(bool shop, CancellationToken token)
        {
            var root = PageRoot("CatalogLoading");
            StyledText(root.transform, shop ? "加载商城目录…" : "加载仓库目录…", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.62f));
            try
            {
                var catalogTask = api.GetShopCatalogAsync(token);
                var inventoryTask = api.GetInventoryAsync(token);
                await Task.WhenAll(catalogTask, inventoryTask);
                var catalogResult = await catalogTask;
                var inventoryResult = await inventoryTask;
                if (token.IsCancellationRequested || currentPage != (shop ? LobbyPage.Shop : LobbyPage.Armory)) return;
                if (!catalogResult.Success) { HandleCatalogFailure(shop, catalogResult); return; }
                if (!inventoryResult.Success) { HandleCatalogFailure(shop, inventoryResult); return; }
                cachedCatalog = catalogResult.Data;
                cachedInventory = inventoryResult.Data;
                RenderCatalog(shop);
            }
            catch (OperationCanceledException)
            {
                // Page navigation intentionally cancels the previous request.
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested || currentPage != (shop ? LobbyPage.Shop : LobbyPage.Armory)) return;
                apiAvailable = false;
                SetNavigationVisible(false);
                Debug.LogException(ex);
                RenderError("商城/仓库请求未完成，请检查 API 是否正在运行后重试", () => Navigate(shop ? LobbyPage.Shop : LobbyPage.Armory));
            }
        }

        private void HandleCatalogFailure<T>(bool shop, ApiResult<T> failed)
        {
            if (failed.Code == "AUTH_UNAUTHORIZED")
            {
                Navigate(LobbyPage.SessionExpired);
                return;
            }

            if (ApiClientErrorCodes.IsTransportFailure(failed.Code))
            {
                apiAvailable = false;
                SetNavigationVisible(false);
            }

            RenderError(ApiErrorMessages.ToUserMessage(failed), () => Navigate(shop ? LobbyPage.Shop : LobbyPage.Armory));
        }

        private void RenderCatalog(bool shop)
        {
            ClearBody();
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot(shop ? "ShopPage" : "ArmoryPage");
            var page = root.transform;
            StyledText(page, shop ? "商城" : "仓库", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.90f), new Vector2(0.4f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";
            UIComponents.Badge("CoinsBadge", page, $"COINS {cachedCatalog?.coins ?? cachedInventory?.coins ?? 0:N0}", UITheme.AccentPrimary,
                new Vector2(0.72f, 0.905f), new Vector2(0.97f, 0.975f));

            var filters = new[] { "Rifle", "Pistol", "Shotgun", "Smg", "Sniper" };
            for (var i = 0; i < filters.Length; i++)
            {
                var key = filters[i];
                var label = key == "Rifle" ? "步枪" : key == "Pistol" ? "手枪" : key == "Shotgun" ? "霰弹枪" : key == "Smg" ? "冲锋枪" : "狙击枪";
                var x0 = 0.02f + i * 0.135f;
                var selected = catalogFilter == key;
                var filterButton = StyledButton(page, label, selected ? UIComponents.ButtonKind.Primary : UIComponents.ButtonKind.Secondary,
                    new Vector2(x0, 0.83f), new Vector2(x0 + 0.12f, 0.885f), () => { catalogFilter = key; RenderCatalog(shop); });
                filterButton.GetComponentInChildren<TMP_Text>().fontSize = UITheme.FontCaption;
            }

            var items = (cachedCatalog?.items ?? Array.Empty<CatalogItemDto>()).Where(x => x != null && x.itemType == "Weapon" && GetCategory(x.itemId) == catalogFilter).ToArray();
            if (items.Length == 0)
            {
                StyledText(page, "暂无目录数据", UITheme.FontCardTitle, UITheme.TextMuted,
                    new Vector2(0.1f, 0.45f), new Vector2(0.9f, 0.58f));
                StyledButton(page, "重试", UIComponents.ButtonKind.Info,
                    new Vector2(0.4f, 0.32f), new Vector2(0.6f, 0.41f), () => Navigate(shop ? LobbyPage.Shop : LobbyPage.Armory));
                PlayEnter(root.gameObject);
                return;
            }
            for (var i = 0; i < items.Length; i++) CreateWeaponCard(page, items[i], shop, i, items.Length);
            PlayEnter(root.gameObject);
        }

        private void CreateWeaponCard(Transform parent, CatalogItemDto item, bool shop, int index, int count)
        {
            const int columns = 3;
            var rows = Mathf.CeilToInt(count / (float)columns);
            var col = index % columns;
            var row = index / columns;
            var x0 = 0.02f + col * 0.325f;
            var x1 = x0 + 0.305f;
            var y1 = 0.79f - row * (0.72f / Mathf.Max(1, rows));
            var y0 = y1 - 0.20f;

            var card = StyledPanel("WeaponCard_" + item.itemId, parent,
                item.isOwned ? UITheme.CardSurface : UITheme.BackgroundPanel, new Vector2(x0, y0), new Vector2(x1, y1));
            var stats = weaponAssets.FindStats(item.itemId);
            var level = cachedCatalog?.level ?? session.Profile?.level ?? 0;
            var coins = cachedCatalog?.coins ?? cachedInventory?.coins ?? 0;

            StyledText(card.transform, item.displayName, UITheme.FontBody, item.isOwned ? UITheme.TextPrimary : UITheme.TextMuted,
                new Vector2(0.05f, 0.80f), new Vector2(0.95f, 0.95f), TextAlignmentOptions.Left, FontStyles.Bold).name = "CardName";

            var stateText = item.isOwned ? "已拥有" : item.unlockLevel > level
                ? $"等级 {item.unlockLevel} 解锁" : item.priceCoins > coins
                ? $"金币不足 · {item.priceCoins:N0}" : $"{item.priceCoins:N0} COINS";
            var stateColor = item.isOwned ? UITheme.AccentSecondary : item.unlockLevel > level || item.priceCoins > coins ? UITheme.TextMuted : UITheme.AccentPrimary;
            UIComponents.Badge("State", card.transform, stateText, stateColor,
                new Vector2(0.05f, 0.62f), new Vector2(0.60f, 0.77f));
            StyledText(card.transform, $"伤害 {stats.damage:0}   射速 {stats.roundsPerMinute:0}\n弹容 {stats.magazineSize:0}   后坐 {stats.recoil:0.##}",
                UITheme.FontCaption, UITheme.TextMuted, new Vector2(0.05f, 0.30f), new Vector2(0.95f, 0.60f), TextAlignmentOptions.Left).name = "CardStats";

            var previewButton = UIComponents.Button("Preview_" + item.itemId, card.transform, "预览", UIComponents.ButtonKind.Info,
                new Vector2(0.05f, 0.06f), new Vector2(shop && !item.isOwned ? 0.47f : 0.95f, 0.24f));
            previewButton.onClick.AddListener(() =>
            {
                selectedWeapon = item;
                detailsFromShop = shop;
                Navigate(LobbyPage.WeaponDetails);
            });
            if (shop && !item.isOwned)
            {
                var buyButton = UIComponents.Button("Buy_" + item.itemId, card.transform, "购买", UIComponents.ButtonKind.Primary,
                    new Vector2(0.52f, 0.06f), new Vector2(0.95f, 0.24f));
                buyButton.onClick.AddListener(() => _ = PurchaseAsync(item));
                buyButton.interactable = item.isActive && item.isImplemented && item.unlockLevel <= level && item.priceCoins <= coins;
            }
        }

        private static string GetCategory(string itemId)
        {
            if (itemId.Contains("pistol") || itemId.Contains("handgun")) return "Pistol";
            if (itemId.Contains("shotgun")) return "Shotgun";
            if (itemId.Contains("smg")) return "Smg";
            if (itemId.Contains("sniper")) return "Sniper";
            return "Rifle";
        }

        // ---------- Weapon details (keeps 3D preview + radar chart) ----------

        private void RenderWeaponDetails()
        {
            if (selectedWeapon == null) { Navigate(LobbyPage.Armory); return; }
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("WeaponDetailsPage");
            var page = root.transform;
            weaponAssets.TryGet(selectedWeapon.itemId, out var asset);
            var stats = weaponAssets.FindStats(selectedWeapon.itemId);

            StyledText(page, selectedWeapon.displayName, UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.90f), new Vector2(0.55f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";
            UIComponents.Badge("OwnState", page, selectedWeapon.isOwned ? "OWNED" : "LOCKED",
                selectedWeapon.isOwned ? UITheme.AccentSecondary : UITheme.AccentDanger,
                new Vector2(0.02f, 0.845f), new Vector2(0.14f, 0.895f));
            StyledText(page, selectedWeapon.isOwned ? "已拥有 · 可装备" : $"价格  {selectedWeapon.priceCoins:N0} COINS",
                UITheme.FontBody + 2, selectedWeapon.isOwned ? UITheme.AccentSecondary : UITheme.AccentPrimary,
                new Vector2(0.62f, 0.90f), new Vector2(0.97f, 0.97f), TextAlignmentOptions.Right, FontStyles.Bold);

            var previewFrame = StyledPanel("WeaponPreviewFrame", page, UITheme.BackgroundPanel, new Vector2(0.02f, 0.14f), new Vector2(0.52f, 0.80f));
            previewFrame.GetComponent<Image>().raycastTarget = false;
            var previewObject = new GameObject("WeaponPreview3D", typeof(RectTransform), typeof(RawImage), typeof(WeaponPreviewController));
            previewObject.transform.SetParent(previewFrame.transform, false);
            UIComponents.Place(previewObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.one);
            previewObject.GetComponent<WeaponPreviewController>().Initialize(weaponAssets.FindPreviewPrefab(selectedWeapon.itemId));
            StyledText(previewFrame.transform, "拖拽旋转  ·  滚轮缩放", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.06f, 0.02f), new Vector2(0.94f, 0.09f));

            var chartPanel = StyledPanel("WeaponStatsChart", page, UITheme.CardSurface, new Vector2(0.56f, 0.32f), new Vector2(0.97f, 0.80f));
            StyledText(chartPanel.transform, "战斗属性", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.86f), new Vector2(0.92f, 0.97f), TextAlignmentOptions.Center, FontStyles.Bold);
            var chartObject = new GameObject("WeaponRadarChart", typeof(RectTransform), typeof(CanvasRenderer), typeof(WeaponRadarChart));
            chartObject.transform.SetParent(chartPanel.transform, false);
            UIComponents.Place(chartObject.GetComponent<RectTransform>(), new Vector2(0.16f, 0.13f), new Vector2(0.84f, 0.80f));
            chartObject.GetComponent<WeaponRadarChart>().SetStats(stats);
            StyledText(chartPanel.transform, $"伤害  {stats.damage:0}", UITheme.FontCaption, UITheme.TextPrimary,
                new Vector2(0.35f, 0.70f), new Vector2(0.65f, 0.82f));
            StyledText(chartPanel.transform, $"射速  {stats.roundsPerMinute:0} RPM", UITheme.FontCaption, UITheme.TextPrimary,
                new Vector2(0.60f, 0.43f), new Vector2(0.98f, 0.55f), TextAlignmentOptions.Right);
            StyledText(chartPanel.transform, $"弹容量  {stats.magazineSize:0}", UITheme.FontCaption, UITheme.TextPrimary,
                new Vector2(0.32f, 0.06f), new Vector2(0.68f, 0.18f));
            StyledText(chartPanel.transform, $"后坐力  {stats.recoil:0.##}", UITheme.FontCaption, UITheme.TextPrimary,
                new Vector2(0.02f, 0.43f), new Vector2(0.40f, 0.55f), TextAlignmentOptions.Left);

            StyledText(page, "业务 ID  " + selectedWeapon.itemId + "\n资源键  " + (asset?.assetKey ?? selectedWeapon.assetKey),
                UITheme.FontCaption, UITheme.TextMuted, new Vector2(0.02f, 0.05f), new Vector2(0.40f, 0.13f), TextAlignmentOptions.Left);

            StyledButton(page, "重置视角", UIComponents.ButtonKind.Info, new Vector2(0.56f, 0.20f), new Vector2(0.75f, 0.29f), () =>
            {
                var controller = previewObject.GetComponent<WeaponPreviewController>();
                controller.Initialize(weaponAssets.FindPreviewPrefab(selectedWeapon.itemId));
            });
            if (!selectedWeapon.isOwned)
            {
                StyledButton(page, "购买", UIComponents.ButtonKind.Primary, new Vector2(0.78f, 0.20f), new Vector2(0.97f, 0.29f),
                    () => _ = PurchaseAsync(selectedWeapon));
            }
            else
            {
                StyledButton(page, selectedWeapon.slotType == "Secondary" ? "装备为副武器" : "装备为主武器", UIComponents.ButtonKind.Primary,
                    new Vector2(0.56f, 0.07f), new Vector2(0.75f, 0.16f), () => _ = EquipWeaponAsync(selectedWeapon));
                if (asset != null && asset.supportsVerifiedAttachments)
                    StyledButton(page, "打开配件装配", UIComponents.ButtonKind.Secondary, new Vector2(0.78f, 0.20f), new Vector2(0.97f, 0.29f),
                        () => _ = RenderAttachmentsAsync());
                else
                    StyledText(page, "配件：尚未适配", UITheme.FontCaption, UITheme.TextMuted,
                        new Vector2(0.78f, 0.20f), new Vector2(0.97f, 0.29f));
            }
            StyledButton(page, detailsFromShop ? "返回商城" : "返回仓库", UIComponents.ButtonKind.Secondary,
                new Vector2(0.78f, 0.07f), new Vector2(0.97f, 0.16f), () => Navigate(detailsFromShop ? LobbyPage.Shop : LobbyPage.Armory));
            PlayEnter(root.gameObject);
        }

        private async Task RenderAttachmentsAsync()
        {
            var token = pageCts.Token;
            var compatibility = await api.GetAttachmentCompatibilityAsync(token);
            var inventory = await api.GetInventoryAsync(token);
            var loadout = await api.GetLoadoutAttachmentsAsync(token);
            if (!compatibility.Success || !inventory.Success || !loadout.Success) { status.text = "配件数据加载失败，请重试"; return; }
            cachedCompatibility = compatibility.Data ?? Array.Empty<AttachmentCompatibilityDto>();
            cachedInventory = inventory.Data;
            ClearBody();
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("AttachmentPage");
            var page = root.transform;
            StyledText(page, "配件装配 · " + selectedWeapon.displayName, UITheme.FontCardTitle + 4, UITheme.TextPrimary,
                new Vector2(0.02f, 0.88f), new Vector2(0.8f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";

            var owned = new HashSet<string>((cachedInventory.items ?? Array.Empty<InventoryItemDto>()).Where(x => x.quantity > 0).Select(x => x.itemId), StringComparer.Ordinal);
            var selections = new List<AttachmentSelectionRequest>();
            var slots = new[] { "Optic", "Muzzle", "Magazine" };
            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                var compat = cachedCompatibility.Where(x => x.weaponId == selectedWeapon.itemId && x.slotType == slot).ToArray();
                var row = StyledPanel("AttachmentRow_" + slot, page, UITheme.CardSurface,
                    new Vector2(0.05f, 0.62f - i * 0.17f), new Vector2(0.95f, 0.75f - i * 0.17f));
                StyledText(row.transform, slot, UITheme.FontBody + 2, UITheme.TextPrimary,
                    new Vector2(0.03f, 0.2f), new Vector2(0.22f, 0.8f), TextAlignmentOptions.Left, FontStyles.Bold);
                var implemented = compat.Where(x => x.isImplemented && owned.Contains(x.attachmentId)).ToArray();
                if (implemented.Length == 0)
                {
                    StyledText(row.transform, compat.Any(x => x.isImplemented) ? "未拥有可用配件" : "尚未适配", UITheme.FontBody, UITheme.TextMuted,
                        new Vector2(0.26f, 0.2f), new Vector2(0.78f, 0.8f), TextAlignmentOptions.Left);
                    continue;
                }
                var chosen = implemented[0];
                selections.Add(new AttachmentSelectionRequest { attachmentSlot = slot, attachmentItemId = chosen.attachmentId });
                StyledText(row.transform, chosen.attachmentId + " · " + chosen.calibrationKey, UITheme.FontCaption + 1, UITheme.AccentSecondary,
                    new Vector2(0.26f, 0.2f), new Vector2(0.92f, 0.8f), TextAlignmentOptions.Left);
            }
            StyledText(page, "仅显示服务端兼容且玩家已拥有的真实组合；未拥有或未校准组合不会被伪造。", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.05f, 0.08f), new Vector2(0.65f, 0.17f), TextAlignmentOptions.Left);
            StyledButton(page, "保存真实装配", UIComponents.ButtonKind.Primary, new Vector2(0.70f, 0.11f), new Vector2(0.95f, 0.24f),
                () => _ = SaveAttachmentsAsync(loadout.Data.version, selections));
            PlayEnter(root.gameObject);
        }

        // ---------- Upgrades ----------

        private void RenderUpgrades()
        {
            if (!session.IsAuthenticated) { Navigate(LobbyPage.Login); return; }
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("UpgradesPage");
            var page = root.transform;
            var profile = session.Profile;
            StyledText(page, "能力升级", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.88f), new Vector2(0.6f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";
            StyledText(page, "升级数据由服务器保存；本地只负责编辑待提交值。", UITheme.FontCaption + 2, UITheme.TextMuted,
                new Vector2(0.02f, 0.83f), new Vector2(0.8f, 0.88f), TextAlignmentOptions.Left);

            var up = profile?.upgrades ?? new UpgradeLevelsDto();
            var damage = AddUpgradeRow(page, "伤害", up.upDamage, 0.62f);
            var ammo = AddUpgradeRow(page, "弹容量", up.upAmmoCap, 0.46f);
            var health = AddUpgradeRow(page, "最大生命", up.upMaxHealth, 0.30f);
            StyledButton(page, "提交升级", UIComponents.ButtonKind.Primary, new Vector2(0.70f, 0.30f), new Vector2(0.95f, 0.44f), async () =>
            {
                var result = await api.UpdateUpgradesAsync(new UpgradeRequest { upDamage = damage(), upAmmoCap = ammo(), upMaxHealth = health() }, pageCts.Token);
                if (result.Success) { session.ApplyProfile(result.Data); status.text = "升级已同步"; RenderUpgrades(); }
                else status.text = ApiErrorMessages.ToUserMessage(result);
            });
            PlayEnter(root.gameObject);
        }

        private Func<int> AddUpgradeRow(Transform parent, string label, int value, float y)
        {
            var row = StyledPanel("Upgrade_" + label, parent, UITheme.CardSurface, new Vector2(0.05f, y), new Vector2(0.62f, y + 0.13f));
            var valueText = StyledText(row.transform, label + "  " + value + "/5", UITheme.FontBody + 2, UITheme.TextPrimary,
                new Vector2(0.05f, 0.2f), new Vector2(0.68f, 0.8f), TextAlignmentOptions.Left, FontStyles.Bold);
            var current = value;
            void Refresh() => valueText.text = label + "  " + current + "/5";
            var minus = UIComponents.Button("Minus", row.transform, "−", UIComponents.ButtonKind.Danger,
                new Vector2(0.74f, 0.12f), new Vector2(0.85f, 0.88f));
            minus.onClick.AddListener(() => { current = Mathf.Max(0, current - 1); Refresh(); });
            var plus = UIComponents.Button("Plus", row.transform, "+", UIComponents.ButtonKind.Primary,
                new Vector2(0.88f, 0.12f), new Vector2(0.99f, 0.88f));
            plus.onClick.AddListener(() => { current = Mathf.Min(5, current + 1); Refresh(); });
            return () => current;
        }

        // ---------- Settings (音量 / 键位 / 画质) ----------

        private void RenderSettings()
        {
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("SettingsPage");
            var page = root.transform;
            StyledText(page, "设置", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.90f), new Vector2(0.6f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";

            RenderAudioCard(page, new Vector2(0.02f, 0.16f), new Vector2(0.335f, 0.86f));
            RenderKeybindCard(page, new Vector2(0.345f, 0.16f), new Vector2(0.665f, 0.86f));
            RenderGraphicsCard(page, new Vector2(0.675f, 0.16f), new Vector2(0.99f, 0.86f));

            StyledButton(page, "保存并应用", UIComponents.ButtonKind.Primary,
                new Vector2(0.02f, 0.02f), new Vector2(0.30f, 0.12f), () =>
                {
                    SettingsModel.Save();
                    SettingsModel.ApplyAll();
                    status.text = "设置已保存并应用";
                });
            StyledButton(page, "恢复默认", UIComponents.ButtonKind.Secondary,
                new Vector2(0.32f, 0.02f), new Vector2(0.50f, 0.12f), () => { ResetSettingsToDefaults(); RenderSettings(); status.text = "已恢复默认设置"; });
            PlayEnter(root.gameObject);
        }

        private void RenderAudioCard(Transform page, Vector2 min, Vector2 max)
        {
            var card = StyledPanel("AudioCard", page, UITheme.CardSurface, min, max);
            StyledText(card.transform, "音量", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.88f), new Vector2(0.92f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);

            // 全局音量
            StyledText(card.transform, "全局音量", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.74f), new Vector2(0.92f, 0.82f), TextAlignmentOptions.Left);
            var master = UIComponents.SliderRow("MasterSlider", card.transform, new Vector2(0.08f, 0.62f), new Vector2(0.92f, 0.72f), SettingsModel.MasterVolume);
            master.onValueChanged.AddListener(v =>
            {
                SettingsModel.MasterVolume = v;
                SettingsModel.ApplyMasterVolume();
            });

            // 音乐音量
            StyledText(card.transform, "音乐音量", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.50f), new Vector2(0.92f, 0.58f), TextAlignmentOptions.Left);
            var music = UIComponents.SliderRow("MusicSlider", card.transform, new Vector2(0.08f, 0.38f), new Vector2(0.92f, 0.48f), SettingsModel.MusicVolume);
            music.onValueChanged.AddListener(v => SettingsModel.MusicVolume = v);

            // 鼠标灵敏度
            StyledText(card.transform, "鼠标灵敏度", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.26f), new Vector2(0.92f, 0.34f), TextAlignmentOptions.Left);
            var sens = UIComponents.SliderRow("SensitivitySlider", card.transform, new Vector2(0.08f, 0.14f), new Vector2(0.92f, 0.24f),
                Mathf.InverseLerp(0.1f, 5f, SettingsModel.Sensitivity));
            sens.onValueChanged.AddListener(v => SettingsModel.Sensitivity = Mathf.Lerp(0.1f, 5f, v));

            // 开镜方式：长按（按住右键）/ 切换（点按右键开收镜）
            var adsBtn = UIComponents.Button("AdsModeToggle", card.transform,
                "开镜方式：" + AdsInputMode.DisplayName(), UIComponents.ButtonKind.Info,
                new Vector2(0.08f, 0.02f), new Vector2(0.92f, 0.12f));
            adsBtn.onClick.AddListener(() =>
            {
                AdsInputMode.Toggle = !AdsInputMode.Toggle;
                SettingsModel.Save();
                var l = adsBtn.GetComponentInChildren<TMP_Text>();
                if (l != null) l.text = "开镜方式：" + AdsInputMode.DisplayName();
                status.text = "开镜方式已设为「" + AdsInputMode.DisplayName() + "」，进入对战后生效";
            });
        }

        private void RenderKeybindCard(Transform page, Vector2 min, Vector2 max)
        {
            var card = StyledPanel("KeybindCard", page, UITheme.CardSurface, min, max);
            StyledText(card.transform, "键位", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.88f), new Vector2(0.92f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(card.transform, "点击「重设」后按新键；Esc 取消", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.08f, 0.82f), new Vector2(0.92f, 0.88f), TextAlignmentOptions.Left);

            var bindings = SettingsKeyMap.Bindings;
            var startY = 0.74f;
            var rowH = 0.075f;
            for (var i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                var y1 = startY - i * rowH;
                var y0 = y1 - rowH + 0.012f;
                StyledText(card.transform, b.label, UITheme.FontCaption + 1, UITheme.TextPrimary,
                    new Vector2(0.08f, y0 + 0.008f), new Vector2(0.42f, y1), TextAlignmentOptions.Left);
                var keyBtn = UIComponents.Button("Key_" + b.action, card.transform, SettingsKeyMap.DisplayName(SettingsKeyMap.Get(b.action)),
                    UIComponents.ButtonKind.Secondary, new Vector2(0.44f, y0), new Vector2(0.74f, y1));
                var captured = b;
                keyBtn.onClick.AddListener(() => StartRebind(captured, keyBtn));
                var resetBtn = UIComponents.Button("Reset_" + b.action, card.transform, "默认", UIComponents.ButtonKind.Info,
                    new Vector2(0.77f, y0), new Vector2(0.94f, y1));
                resetBtn.onClick.AddListener(() =>
                {
                    SettingsKeyMap.Reset(captured.action);
                    RefreshKeybindButton(keyBtn, captured);
                    status.text = captured.label + " 已恢复默认";
                });
            }
        }

        private void RefreshKeybindButton(Button keyBtn, SettingsKeyMap.Binding binding)
        {
            if (keyBtn == null) return;
            var label = keyBtn.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = SettingsKeyMap.DisplayName(SettingsKeyMap.Get(binding.action));
        }

        private void StartRebind(SettingsKeyMap.Binding binding, Button keyBtn)
        {
            var label = keyBtn != null ? keyBtn.GetComponentInChildren<TMP_Text>() : null;
            if (label != null) label.text = "按任意键…";
            status.text = "重设「" + binding.label + "」：按新键，Esc 取消";
            StartCoroutine(RebindRoutine(binding, keyBtn));
        }

        private System.Collections.IEnumerator RebindRoutine(SettingsKeyMap.Binding binding, Button keyBtn)
        {
            // 等一帧避免把触发本次重绑的按键也算进去
            yield return null;
            while (true)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) yield break;
                if (kb.escapeKey.wasPressedThisFrame)
                {
                    RefreshKeybindButton(keyBtn, binding);
                    status.text = "已取消重设";
                    yield break;
                }
                foreach (UnityEngine.InputSystem.Key key in System.Enum.GetValues(typeof(UnityEngine.InputSystem.Key)))
                {
                    if (key == UnityEngine.InputSystem.Key.None || key == UnityEngine.InputSystem.Key.Escape) continue;
                    var control = kb[key];
                    if (control != null && control.wasPressedThisFrame)
                    {
                        SettingsKeyMap.Set(binding.action, key);
                        RefreshKeybindButton(keyBtn, binding);
                        status.text = "「" + binding.label + "」已设为 " + SettingsKeyMap.DisplayName(key);
                        yield break;
                    }
                }
                yield return null;
            }
        }

        private void RenderGraphicsCard(Transform page, Vector2 min, Vector2 max)
        {
            var card = StyledPanel("GraphicsCard", page, UITheme.CardSurface, min, max);
            StyledText(card.transform, "画质", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.88f), new Vector2(0.92f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);

            // 分辨率
            StyledText(card.transform, "分辨率", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.74f), new Vector2(0.92f, 0.82f), TextAlignmentOptions.Left);
            UIComponents.Stepper("ResolutionStepper", card.transform, new Vector2(0.08f, 0.62f), new Vector2(0.92f, 0.72f),
                out var resPrev, out var resNext, out var resLabel);
            var resolutions = SettingsModel.SupportedResolutions;
            var resIndex = System.Array.IndexOf(resolutions, SettingsModel.Resolution);
            if (resIndex < 0) resIndex = 2; // 1920x1080 default
            void RefreshRes()
            {
                resLabel.text = SettingsModel.FormatResolution(resolutions[resIndex]);
                SettingsModel.Resolution = resolutions[resIndex];
            }
            resPrev.onClick.AddListener(() => { resIndex = (resIndex - 1 + resolutions.Length) % resolutions.Length; RefreshRes(); });
            resNext.onClick.AddListener(() => { resIndex = (resIndex + 1) % resolutions.Length; RefreshRes(); });
            RefreshRes();

            // 全屏开关
            var fsBtn = UIComponents.Button("FullscreenToggle", card.transform,
                SettingsModel.Fullscreen ? "全屏：开" : "全屏：关", UIComponents.ButtonKind.Info,
                new Vector2(0.08f, 0.52f), new Vector2(0.92f, 0.60f));
            fsBtn.onClick.AddListener(() =>
            {
                SettingsModel.Fullscreen = !SettingsModel.Fullscreen;
                var l = fsBtn.GetComponentInChildren<TMP_Text>();
                if (l != null) l.text = SettingsModel.Fullscreen ? "全屏：开" : "全屏：关";
            });

            // 锁帧
            StyledText(card.transform, "帧率上限", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.40f), new Vector2(0.92f, 0.48f), TextAlignmentOptions.Left);
            UIComponents.Stepper("FrameCapStepper", card.transform, new Vector2(0.08f, 0.28f), new Vector2(0.92f, 0.38f),
                out var capPrev, out var capNext, out var capLabel);
            var caps = SettingsModel.FrameCapOptions;
            var capIndex = System.Array.IndexOf(caps, SettingsModel.FrameCap);
            if (capIndex < 0) capIndex = 2; // 60 default
            void RefreshCap()
            {
                capLabel.text = SettingsModel.FormatFrameCap(caps[capIndex]);
                SettingsModel.FrameCap = caps[capIndex];
                SettingsModel.ApplyFrameCap();
            }
            capPrev.onClick.AddListener(() => { capIndex = (capIndex - 1 + caps.Length) % caps.Length; RefreshCap(); });
            capNext.onClick.AddListener(() => { capIndex = (capIndex + 1) % caps.Length; RefreshCap(); });
            RefreshCap();
        }

        private void ResetSettingsToDefaults()
        {
            SettingsModel.MasterVolume = 1f;
            SettingsModel.MusicVolume = 1f;
            SettingsModel.Sensitivity = 1f;
            SettingsModel.Resolution = (1920, 1080);
            SettingsModel.Fullscreen = true;
            SettingsModel.FrameCap = 60;
            AdsInputMode.Reset();
            foreach (var b in SettingsKeyMap.Bindings) SettingsKeyMap.Reset(b.action);
            SettingsModel.Save();
            SettingsModel.ApplyAll();
        }
    }
}
