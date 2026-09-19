using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Account;
using Game.Gameplay.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
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
                // 2026-09-16 需求1：仓库与商城分流——商城沿用目录卡片网格，仓库走 CF 风专属页
                if (shop) RenderCatalog(true);
                else RenderArmoryPage();
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

            // 商城只列 Shop 行；仓库列全集（含 Initial 初始武器——2026-09-07 修复：
            // 目录端点曾按 Shop 过滤导致 AK/M4 不在仓库、无法进入枪匠页改装）
            var lpfpItems = (cachedCatalog?.items ?? Array.Empty<CatalogItemDto>()).Where(IsLpfpWeaponItem);
            var items = (shop ? lpfpItems.Where(x => x.acquisitionSource == "Shop") : lpfpItems)
                .Where(x => GetCategory(x.itemId) == catalogFilter).ToArray();
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

        /// <summary>Only explicit LPFP catalog rows are visible in the mainline armory/shop.</summary>
        private bool IsLpfpWeaponItem(CatalogItemDto item)
        {
            return item != null
                && string.Equals(item.itemType, "Weapon", StringComparison.OrdinalIgnoreCase)
                && weaponAssets != null
                && weaponAssets.TryGet(item.itemId, out var asset)
                && asset != null
                && asset.IsLpfp
                && asset.definition != null;
        }

        // ---------- Armory（2026-09-16 需求1：CF 风——左列类型标签 + 中央大网格，仅已拥有武器） ----------

        /// <summary>仓库类型标签（含"全部"；顺序即左列从上到下）。</summary>
        private static readonly (string Key, string Label)[] ArmoryTabs =
        {
            ("All", "全部"), ("Rifle", "步枪"), ("Smg", "冲锋枪"),
            ("Sniper", "狙击枪"), ("Shotgun", "霰弹枪"), ("Pistol", "手枪"),
        };
        private string armoryFilter = "All";
        private int armoryPage;
        private const int ArmoryPageSize = 12; // 4 列 × 3 行

        private void RenderArmoryPage()
        {
            ClearBody();
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("ArmoryPage");
            var page = root.transform;
            StyledText(page, "仓库", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.90f), new Vector2(0.4f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";
            UIComponents.Badge("CoinsBadge", page, $"COINS {cachedCatalog?.coins ?? cachedInventory?.coins ?? 0:N0}", UITheme.AccentPrimary,
                new Vector2(0.72f, 0.905f), new Vector2(0.97f, 0.975f));

            // 左列类型标签（CF 仓库侧栏；本地过滤，不重拉 API）
            var tabY = 0.80f;
            foreach (var (key, label) in ArmoryTabs)
            {
                var captured = key;
                var tab = UIComponents.NavPill("ArmoryTab_" + key, page, label,
                    new Vector2(0.02f, tabY - 0.055f), new Vector2(0.145f, tabY + 0.01f));
                UIComponents.SetNavPillSelected(tab, armoryFilter == key);
                tab.onClick.AddListener(() => { armoryFilter = captured; armoryPage = 0; RenderArmoryPage(); });
                tabY -= 0.075f;
            }

            // 数据：仅已拥有的 LPFP 武器（含初始三枪——目录契约 2026-09-07 已保证 Initial 行在仓库可见）
            var owned = (cachedCatalog?.items ?? Array.Empty<CatalogItemDto>())
                .Where(x => x.isOwned && IsLpfpWeaponItem(x))
                .Where(x => armoryFilter == "All" || GetCategory(x.itemId) == armoryFilter)
                .ToArray();

            if (owned.Length == 0)
            {
                StyledText(page, armoryFilter == "All" ? "暂无可展示武器" : "该分类下暂无已拥有武器",
                    UITheme.FontCardTitle, UITheme.TextMuted, new Vector2(0.16f, 0.45f), new Vector2(0.98f, 0.58f));
                PlayEnter(root.gameObject);
                return;
            }

            var pages = Mathf.Max(1, Mathf.CeilToInt(owned.Length / (float)ArmoryPageSize));
            armoryPage = Mathf.Clamp(armoryPage, 0, pages - 1);
            var pageItems = owned.Skip(armoryPage * ArmoryPageSize).Take(ArmoryPageSize).ToArray();
            for (var i = 0; i < pageItems.Length; i++) CreateArmoryCard(page, pageItems[i], i);

            if (pages > 1)
            {
                var prev = StyledButton(page, "上一页", UIComponents.ButtonKind.Secondary,
                    new Vector2(0.38f, 0.015f), new Vector2(0.47f, 0.075f), () => { armoryPage--; RenderArmoryPage(); });
                prev.interactable = armoryPage > 0;
                StyledText(page, $"{armoryPage + 1}/{pages}", UITheme.FontCaption, UITheme.TextMuted,
                    new Vector2(0.48f, 0.02f), new Vector2(0.54f, 0.07f));
                var next = StyledButton(page, "下一页", UIComponents.ButtonKind.Secondary,
                    new Vector2(0.55f, 0.015f), new Vector2(0.64f, 0.075f), () => { armoryPage++; RenderArmoryPage(); });
                next.interactable = armoryPage < pages - 1;
            }

            PlayEnter(root.gameObject);
            // 装备角标需要服务器配装；未加载时补拉一次（失败静默）
            if (session.Loadout == null && api != null && pageCts != null) _ = EnsureLoadoutCachedAsync(pageCts.Token);
        }

        /// <summary>仓库大网格卡片（4 列；点击「配件改装」进枪匠，未适配配件的武器落详情页）。</summary>
        private void CreateArmoryCard(Transform parent, CatalogItemDto item, int index)
        {
            const int columns = 4;
            const float cellW = 0.19375f, cellH = 0.26f, gapX = 0.015f, gapY = 0.02f;
            var col = index % columns;
            var row = index / columns;
            var x0 = 0.16f + col * (cellW + gapX);
            var x1 = x0 + cellW;
            var y1 = 0.86f - row * (cellH + gapY);
            var y0 = y1 - cellH;

            var card = StyledPanel("WeaponCard_" + item.itemId, parent, UITheme.CardSurface, new Vector2(x0, y0), new Vector2(x1, y1));
            var stats = weaponAssets.FindStats(item.itemId);
            weaponAssets.TryGet(item.itemId, out var asset);

            StyledText(card.transform, item.displayName, UITheme.FontBody, UITheme.TextPrimary,
                new Vector2(0.05f, 0.86f), new Vector2(0.95f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold).name = "CardName";
            UIComponents.Badge("TypeBadge", card.transform, CategoryLabel(GetCategory(item.itemId)), UITheme.AccentInfo,
                new Vector2(0.05f, 0.70f), new Vector2(0.45f, 0.84f));

            var loadout = session.Loadout;
            if (loadout != null && (item.itemId == loadout.primaryWeaponId || item.itemId == loadout.secondaryWeaponId))
                UIComponents.Badge("EquippedBadge", card.transform, "已装备", UITheme.AccentSecondary,
                    new Vector2(0.50f, 0.70f), new Vector2(0.95f, 0.84f));

            StyledText(card.transform, $"伤害 {stats.damage:0}   射速 {stats.roundsPerMinute:0}\n弹容 {stats.magazineSize:0}   后坐 {stats.recoil:0.##}",
                UITheme.FontCaption, UITheme.TextMuted, new Vector2(0.05f, 0.34f), new Vector2(0.95f, 0.68f), TextAlignmentOptions.Left).name = "CardStats";

            var supportsAttachments = asset != null && asset.supportsVerifiedAttachments;
            var gunsmith = UIComponents.Button("Gunsmith_" + item.itemId, card.transform,
                supportsAttachments ? "配件改装" : "查看详情", UIComponents.ButtonKind.Primary,
                new Vector2(0.05f, 0.06f), new Vector2(0.63f, 0.28f));
            gunsmith.onClick.AddListener(() => OpenGunsmithFromArmory(item));
            var details = UIComponents.Button("Details_" + item.itemId, card.transform, "详情", UIComponents.ButtonKind.Info,
                new Vector2(0.67f, 0.06f), new Vector2(0.95f, 0.28f));
            details.onClick.AddListener(() => { selectedWeapon = item; detailsFromShop = false; Navigate(LobbyPage.WeaponDetails); });
        }

        /// <summary>仓库点击枪械的统一入口（用户拍板 2026-09-16：进该枪枪匠页；未适配配件→详情页）。</summary>
        private void OpenGunsmithFromArmory(CatalogItemDto item)
        {
            selectedWeapon = item;
            detailsFromShop = false;
            weaponAssets.TryGet(item.itemId, out var asset);
            if (asset != null && asset.supportsVerifiedAttachments)
            {
                gunsmithSelections.Clear();
                gunsmithActiveSlot = null;
                _ = RenderAttachmentsAsync();
            }
            else
            {
                status.text = "该武器暂未适配配件，已打开详情页";
                Navigate(LobbyPage.WeaponDetails);
            }
        }

        private static string CategoryLabel(string key) => key switch
        {
            "Rifle" => "步枪",
            "Pistol" => "手枪",
            "Shotgun" => "霰弹枪",
            "Smg" => "冲锋枪",
            "Sniper" => "狙击枪",
            _ => "全部",
        };
        // ---------- Weapon details (keeps 3D preview + radar chart) ----------

        private void RenderWeaponDetails()
        {
            if (!IsLpfpWeaponItem(selectedWeapon)) { selectedWeapon = null; Navigate(LobbyPage.Armory); return; }
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
            if (!selectedWeapon.isOwned && selectedWeapon.acquisitionSource == "Shop")
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
                        () => { gunsmithSelections.Clear(); gunsmithActiveSlot = null; _ = RenderAttachmentsAsync(); });
                else
                    StyledText(page, "配件：尚未适配", UITheme.FontCaption, UITheme.TextMuted,
                        new Vector2(0.78f, 0.20f), new Vector2(0.97f, 0.29f));
            }
            StyledButton(page, detailsFromShop ? "返回商城" : "返回仓库", UIComponents.ButtonKind.Secondary,
                new Vector2(0.78f, 0.07f), new Vector2(0.97f, 0.16f), () => Navigate(detailsFromShop ? LobbyPage.Shop : LobbyPage.Armory));
            PlayEnter(root.gameObject);
        }

        // ---------- Gunsmith（军事硬核风 v2：中央大预览 + 环绕槽位 + 侧边抽屉直购，Docs/21 Phase F） ----------

        private static readonly string[] GunsmithSlots = { "Optic", "Muzzle", "Magazine", "Tactical", "Underbarrel" };
        private static readonly string[] GunsmithSlotNames = { "瞄具", "枪口", "弹匣", "战术", "下挂" };
        private static readonly bool[] GunsmithSlotOnLeft = { true, true, true, false, false };
        private readonly Dictionary<string, string> gunsmithSelections = new();
        private string gunsmithActiveSlot;
        private long gunsmithLoadoutVersion;
        private WeaponPreviewController gunsmithPreview;

        private async Task RenderAttachmentsAsync()
        {
            if (!IsLpfpWeaponItem(selectedWeapon))
            {
                selectedWeapon = null;
                Navigate(LobbyPage.Armory);
                return;
            }            var token = pageCts.Token;
            var compatibility = await api.GetAttachmentCompatibilityAsync(token);
            var inventory = await api.GetInventoryAsync(token);
            var loadout = await api.GetLoadoutAttachmentsAsync(token);
            if (!compatibility.Success || !inventory.Success || !loadout.Success) { status.text = "配件数据加载失败，请重试"; return; }
            cachedCompatibility = compatibility.Data ?? Array.Empty<AttachmentCompatibilityDto>();
            cachedInventory = inventory.Data;
            gunsmithLoadoutVersion = loadout.Data.version;
            // 恢复屏幕状态：服务器配装为底，本地草稿覆盖（草稿 = 最近一次离开页面时的所见）
            if (!gunsmithSelections.Any())
            {
                gunsmithSelections.Clear();
                var slotKey = selectedWeapon.slotType == "Secondary" ? "Secondary" : "Primary";
                foreach (var row in loadout.Data.attachments ?? Array.Empty<LoadoutAttachmentDto>())
                    if (row.weaponSlot == slotKey) gunsmithSelections[row.attachmentSlot] = row.attachmentItemId;
                if (Game.Gameplay.Weapon.GunsmithDraftStore.TryLoad(selectedWeapon.itemId, gunsmithSelections))
                    status.text = "已恢复上次改装草稿（点「保存配置」写入服务器）";
            }
            else
            {
                // 同一武器反复进出：草稿始终优先（最新屏幕状态）
                Game.Gameplay.Weapon.GunsmithDraftStore.TryLoad(selectedWeapon.itemId, gunsmithSelections);
            }
            BuildGunsmithPage();
        }

        /// <summary>构建枪匠页（同步、离线可测：全部读缓存字段，不碰 api）.</summary>
        private void BuildGunsmithPage()
        {
            ClearBody();
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("GunsmithPage");
            var page = root.transform;

            weaponAssets.TryGet(selectedWeapon.itemId, out var asset);
            var baseStats = weaponAssets.FindStats(selectedWeapon.itemId);
            var owned = new HashSet<string>((cachedInventory.items ?? Array.Empty<InventoryItemDto>())
                .Where(x => x.quantity > 0).Select(x => x.itemId), StringComparer.Ordinal);
            var attachmentCatalog = Game.Gameplay.Weapon.AttachmentAssetCatalog.LoadOrDefault();
            string DisplayNameOf(string id) => attachmentCatalog.TryGet(id, out var e) ? e.displayName : id;
            long PriceOf(string id) => cachedCatalog?.items?.FirstOrDefault(x => x.itemId == id)?.priceCoins ?? 0;

            // —— 顶部：返回（左上箭头）+ 标题
            UIComponents.Button("GunsmithBack", page, "← 返回", UIComponents.ButtonKind.Secondary,
                new Vector2(0.02f, 0.905f), new Vector2(0.115f, 0.975f))
                .onClick.AddListener(() => { gunsmithSelections.Clear(); gunsmithActiveSlot = null; Navigate(detailsFromShop ? LobbyPage.Shop : LobbyPage.Armory); });
            StyledText(page, "枪械工坊 · " + selectedWeapon.displayName, UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.13f, 0.90f), new Vector2(0.68f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";

            // —— 中央 3D 大预览（核心区，不被遮挡；抽屉只覆盖侧列）
            var previewFrame = StyledPanel("WeaponPreviewFrame", page, new Color(UITheme.BackgroundPanel.r, UITheme.BackgroundPanel.g, UITheme.BackgroundPanel.b, 0.55f),
                new Vector2(0.245f, 0.155f), new Vector2(0.755f, 0.885f));
            previewFrame.GetComponent<Image>().raycastTarget = false;
            var previewObject = new GameObject("WeaponPreview3D", typeof(RectTransform), typeof(RawImage), typeof(WeaponPreviewController));
            previewObject.transform.SetParent(previewFrame.transform, false);
            UIComponents.Place(previewObject.GetComponent<RectTransform>(), Vector2.zero, Vector2.one);
            var preview = previewObject.GetComponent<WeaponPreviewController>();
            preview.Initialize(weaponAssets.FindPreviewPrefab(selectedWeapon.itemId));
            gunsmithPreview = preview;

            // —— 属性横条（左下：底槽 + 白基础段 + 绿/红增量段 + 数值）
            var statsPanel = StyledPanel("GunsmithStats", page, new Color(UITheme.CardSurface.r, UITheme.CardSurface.g, UITheme.CardSurface.b, 0.9f),
                new Vector2(0.02f, 0.025f), new Vector2(0.42f, 0.295f));
            StyledText(statsPanel.transform, "改装属性", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.04f, 0.82f), new Vector2(0.96f, 0.97f), TextAlignmentOptions.Left, FontStyles.Bold);
            var statBars = new (Image baseFill, Image deltaFill, TMP_Text value)[4];
            string[] statNames = { "伤害", "射速", "弹容量", "后坐力" };
            for (int i = 0; i < 4; i++)
                statBars[i] = AddGunsmithStatBar(statsPanel.transform, statNames[i], 0.60f - i * 0.155f);

            // —— 环绕槽位（左列 3 / 右列 2，点行内 + 号开抽屉）
            var weaponCompat = cachedCompatibility.Where(x => x.weaponId == selectedWeapon.itemId).ToArray();
            float[] leftY = { 0.70f, 0.52f, 0.34f };      // Optic / Muzzle / Magazine
            float[] rightY = { 0.70f, 0.52f };            // Tactical / Underbarrel
            int leftIndex = 0, rightIndex = 0;
            for (var i = 0; i < GunsmithSlots.Length; i++)
            {
                var slot = GunsmithSlots[i];
                var slotName = GunsmithSlotNames[i];
                var rows = weaponCompat.Where(x => x.slotType == slot).ToArray();
                if (rows.Length == 0) continue;                        // 无此槽位（按挂点能力显隐）
                var onLeft = GunsmithSlotOnLeft[i];
                var y = onLeft ? leftY[leftIndex++] : rightY[rightIndex++];
                var row = StyledPanel("SlotRow_" + slot, page,
                    gunsmithActiveSlot == slot ? UITheme.AccentPrimary : new Color(UITheme.BackgroundPanel.r, UITheme.BackgroundPanel.g, UITheme.BackgroundPanel.b, 0.9f),
                    onLeft ? new Vector2(0.02f, y) : new Vector2(0.78f, y),
                    onLeft ? new Vector2(0.225f, y + 0.135f) : new Vector2(0.985f, y + 0.135f));
                StyledText(row.transform, slotName, UITheme.FontBody, UITheme.TextPrimary,
                    new Vector2(0.06f, 0.52f), new Vector2(0.55f, 0.92f), TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
                var hasSelection = gunsmithSelections.TryGetValue(slot, out var picked);
                var selectionText = hasSelection ? DisplayNameOf(picked)
                    : rows.Any(x => x.isImplemented) ? "未安装" : "尚未适配";
                var selectionUi = StyledText(row.transform, selectionText,
                    UITheme.FontCaption, hasSelection ? UITheme.AccentPrimary : UITheme.TextMuted,
                    new Vector2(0.06f, 0.08f), new Vector2(0.82f, 0.48f), TextAlignmentOptions.MidlineLeft);
                selectionUi.name = "SlotSelection_" + slot;
                if (hasSelection)
                    UIComponents.Badge("SlotCheck_" + slot, row.transform, "?", UITheme.AccentPrimary,
                        new Vector2(0.84f, 0.42f), new Vector2(0.97f, 0.95f));
                if (rows.Any(x => x.isImplemented))
                    UIComponents.Button("SlotEdit_" + slot, row.transform, "+", UIComponents.ButtonKind.Secondary,
                        new Vector2(0.80f, 0.06f), new Vector2(0.99f, 0.46f))
                        .onClick.AddListener(() => { gunsmithActiveSlot = gunsmithActiveSlot == slot ? null : slot; BuildGunsmithPage(); });
                else
                    StyledText(row.transform, "—", UITheme.FontCaption, UITheme.TextMuted,
                        new Vector2(0.82f, 0.06f), new Vector2(0.99f, 0.46f), TextAlignmentOptions.Center);
            }

            // —— 侧边抽屉（滚动列表 + 固定页脚；未拥有件直接购买；2026-09-02 返工）——
            if (gunsmithActiveSlot != null && weaponCompat.Any(x => x.slotType == gunsmithActiveSlot && x.isImplemented))
            {
                var slotIdx = Array.IndexOf(GunsmithSlots, gunsmithActiveSlot);
                var onLeft = GunsmithSlotOnLeft[slotIdx];
                var drawer = StyledPanel("SlotDrawer_" + gunsmithActiveSlot, page, new Color(UITheme.CardSurface.r, UITheme.CardSurface.g, UITheme.CardSurface.b, 0.96f),
                    onLeft ? new Vector2(0.02f, 0.30f) : new Vector2(0.665f, 0.30f),
                    onLeft ? new Vector2(0.335f, 0.89f) : new Vector2(0.985f, 0.89f));
                StyledText(drawer.transform, GunsmithSlotNames[slotIdx] + " · 选择配件", UITheme.FontCardTitle, UITheme.TextPrimary,
                    new Vector2(0.05f, 0.905f), new Vector2(0.95f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold);

                // 滚动区：viewport(RectMask2D) + content(行) + ScrollRect + 滚轮桥接
                var viewport = new GameObject("OptionList", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
                viewport.transform.SetParent(drawer.transform, false);
                var vpRect = viewport.GetComponent<RectTransform>();
                vpRect.anchorMin = new Vector2(0.03f, 0.10f);
                vpRect.anchorMax = new Vector2(0.97f, 0.89f);
                vpRect.offsetMin = Vector2.zero;
                vpRect.offsetMax = Vector2.zero;
                viewport.GetComponent<Image>().color = Color.clear;
                viewport.GetComponent<Image>().raycastTarget = true;   // 捕获拖拽/滚轮

                var options = weaponCompat.Where(x => x.slotType == gunsmithActiveSlot && x.isImplemented).ToArray();
                var content = new GameObject("OptionContent", typeof(RectTransform));
                content.transform.SetParent(viewport.transform, false);
                var contentRect = content.GetComponent<RectTransform>();
                contentRect.anchorMin = new Vector2(0f, 1f);
                contentRect.anchorMax = new Vector2(1f, 1f);
                contentRect.pivot = new Vector2(0.5f, 1f);
                contentRect.sizeDelta = new Vector2(0f, options.Length * 120f);   // 每行 120px，行多则滚动
                contentRect.anchoredPosition = Vector2.zero;

                for (var j = 0; j < options.Length; j++)
                {
                    var option = options[j];
                    var isOwned = owned.Contains(option.attachmentId);
                    var isPicked = gunsmithSelections.TryGetValue(gunsmithActiveSlot, out var cur) && cur == option.attachmentId;
                    // 行锚定在 content 顶部（归一化高度 = 1/行数）
                    var rowNormMin = 1f - (j + 1) / (float)options.Length;
                    var rowNormMax = 1f - j / (float)options.Length;
                    var itemRow = new GameObject("Option_" + option.attachmentId, typeof(RectTransform), typeof(Image));
                    itemRow.transform.SetParent(content.transform, false);
                    var rowRect = itemRow.GetComponent<RectTransform>();
                    rowRect.anchorMin = new Vector2(0f, rowNormMin);
                    rowRect.anchorMax = new Vector2(1f, rowNormMax);
                    rowRect.offsetMin = new Vector2(4f, 3f);
                    rowRect.offsetMax = new Vector2(-4f, -3f);
                    var rowImage = itemRow.GetComponent<Image>();
                    rowImage.sprite = UISprites.RoundedRect(UITheme.RadiusButton);
                    rowImage.type = Image.Type.Sliced;
                    rowImage.color = isPicked ? new Color(UITheme.AccentPrimary.r, UITheme.AccentPrimary.g, UITheme.AccentPrimary.b, 0.30f) : UITheme.BackgroundPanel;

                    StyledText(itemRow.transform, (isPicked ? "● " : "") + DisplayNameOf(option.attachmentId), UITheme.FontBody,
                        isPicked ? UITheme.AccentPrimary : UITheme.TextPrimary,
                        new Vector2(0.03f, 0.48f), new Vector2(0.70f, 0.96f), TextAlignmentOptions.MidlineLeft, isPicked ? FontStyles.Bold : FontStyles.Normal);
                    var deltaText = GunsmithModifierSummary(attachmentCatalog, option.attachmentId);
                    if (!string.IsNullOrEmpty(deltaText))
                        StyledText(itemRow.transform, deltaText, UITheme.FontCaption, UITheme.TextMuted,
                            new Vector2(0.03f, 0.04f), new Vector2(0.70f, 0.46f), TextAlignmentOptions.MidlineLeft);
                    if (isOwned)
                        UIComponents.Button("OptionEquip_" + option.attachmentId, itemRow.transform, isPicked ? "已装备" : "装备",
                            UIComponents.ButtonKind.Primary, new Vector2(0.72f, 0.08f), new Vector2(0.99f, 0.92f))
                            .onClick.AddListener(() =>
                            {
                                gunsmithSelections[gunsmithActiveSlot] = option.attachmentId;
                                Game.Gameplay.Weapon.GunsmithDraftStore.Save(selectedWeapon.itemId, gunsmithSelections);
                                gunsmithActiveSlot = null;
                                BuildGunsmithPage();
                            });
                    else
                        UIComponents.Button("OptionBuy_" + option.attachmentId, itemRow.transform, PriceOf(option.attachmentId).ToString("N0") + " 购买",
                            UIComponents.ButtonKind.Info, new Vector2(0.72f, 0.08f), new Vector2(0.99f, 0.92f))
                            .onClick.AddListener(() => _ = PurchaseAndEquipAttachmentAsync(option.attachmentId));
                }

                var scroll = drawer.AddComponent<ScrollRect>();
                scroll.viewport = vpRect;
                scroll.content = contentRect;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.scrollSensitivity = 20f;
                var wheel = drawer.AddComponent<MouseWheelScroll>();
                wheel.target = scroll;

                // 固定页脚（不再与列表重叠）
                var clear = UIComponents.Button("Option_Clear", drawer.transform, "卸下此槽", UIComponents.ButtonKind.Danger,
                    new Vector2(0.03f, 0.02f), new Vector2(0.30f, 0.09f));
                clear.onClick.AddListener(() =>
                {
                    gunsmithSelections.Remove(gunsmithActiveSlot);
                    Game.Gameplay.Weapon.GunsmithDraftStore.Save(selectedWeapon.itemId, gunsmithSelections);
                    gunsmithActiveSlot = null;
                    BuildGunsmithPage();
                });
                var close = UIComponents.Button("Option_Close", drawer.transform, "收起", UIComponents.ButtonKind.Secondary,
                    new Vector2(0.72f, 0.02f), new Vector2(0.97f, 0.09f));
                close.onClick.AddListener(() => { gunsmithActiveSlot = null; BuildGunsmithPage(); });
            }

            // —— 实时预览换装 + 属性条刷新（草稿每变一次调用）
            void RefreshPreview()
            {
                var entries = new List<Game.Gameplay.Weapon.AttachmentAssetEntry>();
                foreach (var kv in gunsmithSelections)
                    if (attachmentCatalog.TryGet(kv.Value, out var entry)) entries.Add(entry);
                preview.ApplyPreviewAttachments(attachmentCatalog, selectedWeapon.itemId, entries);
                RefreshStatCompare();
            }
            void RefreshStatCompare()
            {
                var mag = baseStats.magazineSize;
                var recoilScale = 1f;
                var rpmScale = 1f;
                foreach (var kv in gunsmithSelections)
                {
                    if (!attachmentCatalog.TryGet(kv.Value, out var entry)) continue;
                    foreach (var m in entry.modifiers)
                    {
                        if (m.stat == Game.Core.WeaponStatId.MagazineSize && m.op == Game.Core.ModifierOperation.Add) mag += m.value;
                        if (m.stat == Game.Core.WeaponStatId.MagazineSize && m.op == Game.Core.ModifierOperation.Multiply) mag *= m.value;
                        if (m.stat == Game.Core.WeaponStatId.VerticalRecoil && m.op == Game.Core.ModifierOperation.Multiply) recoilScale *= m.value;
                        if (m.stat == Game.Core.WeaponStatId.Spread && m.op == Game.Core.ModifierOperation.Multiply) recoilScale *= 1f; // 散布并入后坐显示（简化）
                        if (m.stat == Game.Core.WeaponStatId.Spread && m.op == Game.Core.ModifierOperation.Multiply) rpmScale *= 1f;
                    }
                }
                SetGunsmithStatBar(statBars[0], baseStats.damage, baseStats.damage, 120f, false);
                SetGunsmithStatBar(statBars[1], baseStats.roundsPerMinute, baseStats.roundsPerMinute * rpmScale, 1000f, false);
                SetGunsmithStatBar(statBars[2], baseStats.magazineSize, mag, 60f, false);
                SetGunsmithStatBar(statBars[3], baseStats.recoil, baseStats.recoil * recoilScale, 4f, true);
            }

            // —— 底部居中：保存（一个入口：装配写服务器 + 配件位置写校准资产）/ 重置
            UIComponents.Button("GunsmithSave", page, "保 存", UIComponents.ButtonKind.Primary,
                new Vector2(0.42f, 0.025f), new Vector2(0.58f, 0.115f))
                .onClick.AddListener(() => _ = SaveGunsmithAsync());
            UIComponents.Button("GunsmithReset", page, "重 置", UIComponents.ButtonKind.Secondary,
                new Vector2(0.59f, 0.025f), new Vector2(0.72f, 0.115f))
                .onClick.AddListener(() =>
                {
                    gunsmithSelections.Clear();
                    Game.Gameplay.Weapon.GunsmithDraftStore.Clear(selectedWeapon.itemId);
                    gunsmithActiveSlot = null;
                    status.text = "已重置为服务器配装";
                    BuildGunsmithPage();
                });

            StyledText(page, "调位后拖动配件可微调 · 点「保 存」一并保存装配与配件位置", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.74f, 0.04f), new Vector2(0.985f, 0.105f), TextAlignmentOptions.MidlineRight);

            RefreshPreview();
            PlayEnter(root.gameObject);
        }

        /// <summary>枪匠属性横条：底槽 + 白基础段 + 绿/红增量段 + 数值文本，返回可刷新句柄.</summary>
        private static (Image baseFill, Image deltaFill, TMP_Text value) AddGunsmithStatBar(Transform parent, string label, float yTop)
        {
            UITypography.Text("StatLabel_" + label, parent, label, UITheme.FontBody, UITheme.TextPrimary,
                new Vector2(0.05f, yTop - 0.14f), new Vector2(0.20f, yTop - 0.005f), TextAlignmentOptions.MidlineLeft);
            var track = new GameObject("Track_" + label, typeof(RectTransform), typeof(Image));
            track.transform.SetParent(parent, false);
            UIComponents.Place(track.GetComponent<RectTransform>(), new Vector2(0.21f, yTop - 0.125f), new Vector2(0.72f, yTop - 0.02f));
            track.GetComponent<Image>().color = UITheme.BorderDark;

            var baseFill = new GameObject("Base_" + label, typeof(RectTransform), typeof(Image));
            baseFill.transform.SetParent(track.transform, false);
            var value = UITypography.Text("StatValue_" + label, parent, "0", UITheme.FontBody, UITheme.TextPrimary,
                new Vector2(0.74f, yTop - 0.14f), new Vector2(0.97f, yTop - 0.005f), TextAlignmentOptions.MidlineRight);

            var deltaFill = new GameObject("Delta_" + label, typeof(RectTransform), typeof(Image));
            deltaFill.transform.SetParent(track.transform, false);
            return (baseFill.GetComponent<Image>(), deltaFill.GetComponent<Image>(), value);
        }

        /// <summary>写属性条：基础段 fill + 增量段（绿/红）+ 数值文本；fill 用锚点拉伸（offset 归零，杜绝比例失控）.</summary>
        private static void SetGunsmithStatBar((Image baseFill, Image deltaFill, TMP_Text value) bar, float from, float to, float max, bool lowerIsBetter)
        {
            float Norm(float v) => Mathf.Clamp01(v / Mathf.Max(1f, max));
            var lo = Mathf.Min(Norm(from), Norm(to));
            var hi = Mathf.Max(Norm(from), Norm(to));
            var improved = lowerIsBetter ? to <= from : to >= from;

            var baseRect = bar.baseFill.rectTransform;
            baseRect.anchorMin = Vector2.zero;
            baseRect.anchorMax = new Vector2(hi, 1f);
            baseRect.offsetMin = Vector2.zero;
            baseRect.offsetMax = Vector2.zero;
            bar.baseFill.color = new Color(0.62f, 0.68f, 0.78f, 0.9f);

            var deltaRect = bar.deltaFill.rectTransform;
            deltaRect.anchorMin = new Vector2(lo, 0f);
            deltaRect.anchorMax = new Vector2(hi, 1f);
            deltaRect.offsetMin = Vector2.zero;
            deltaRect.offsetMax = Vector2.zero;
            bar.deltaFill.color = Mathf.Approximately(to, from) ? Color.clear
                : improved ? UITheme.AccentSecondary : UITheme.AccentDanger;

            var delta = to - from;
            bar.value.text = Mathf.Approximately(delta, 0f)
                ? from.ToString("0")
                : from.ToString("0") + " → " + to.ToString("0") + "  " + (delta > 0 ? "+" : "") + delta.ToString("0.#");
        }

        /// <summary>配件增减小结（抽屉行第二行文字：弹容量+8 / 后坐-8% 等）.</summary>
        private static string GunsmithModifierSummary(Game.Gameplay.Weapon.AttachmentAssetCatalog catalog, string attachmentId)
        {
            if (catalog == null || !catalog.TryGet(attachmentId, out var entry) || entry.modifiers == null) return string.Empty;
            var parts = new List<string>();
            foreach (var m in entry.modifiers)
            {
                switch (m.stat)
                {
                    case Game.Core.WeaponStatId.MagazineSize when m.op == Game.Core.ModifierOperation.Add:
                        parts.Add($"弹容量{(m.value >= 0 ? "+" : "")}{m.value:0}"); break;
                    case Game.Core.WeaponStatId.MagazineSize when m.op == Game.Core.ModifierOperation.Multiply:
                        parts.Add($"弹容量×{m.value:0.##}"); break;
                    case Game.Core.WeaponStatId.VerticalRecoil when m.op == Game.Core.ModifierOperation.Multiply:
                        parts.Add($"后坐{(1f - m.value) * 100f:+0;-0}%"); break;
                    case Game.Core.WeaponStatId.Spread when m.op == Game.Core.ModifierOperation.Multiply:
                        parts.Add($"散布{(1f - m.value) * 100f:+0;-0}%"); break;
                }
            }
            return string.Join(" · ", parts);
        }

        /// <summary>抽屉直购并装备（未拥有件一步到位：购买→库存刷新→选中→重绘）.</summary>
        private async Task PurchaseAndEquipAttachmentAsync(string attachmentId)
        {
            var slot = gunsmithActiveSlot;
            status.text = "购买配件中…";
            var result = await api.PurchaseAsync(new PurchaseRequest { itemId = attachmentId, quantity = 1, idempotencyKey = Guid.NewGuid().ToString("N") }, pageCts.Token);
            if (!result.Success)
            {
                status.text = ApiErrorMessages.ToUserMessage(result);
                return;
            }
            if (session.Profile != null)
            {
                session.Profile.coins = result.Data.coins;
                session.ApplyProfile(session.Profile);
            }
            var inventory = await api.GetInventoryAsync(pageCts.Token);
            if (inventory.Success) cachedInventory = inventory.Data;
            gunsmithSelections[slot] = attachmentId;
            Game.Gameplay.Weapon.GunsmithDraftStore.Save(selectedWeapon.itemId, gunsmithSelections);
            gunsmithActiveSlot = null;
            status.text = "已购买并装备（保存后写入配装）";
            BuildGunsmithPage();
        }

        /// <summary>局部重绘枪匠页（选择变更/槽切换）——保留草稿，数据用缓存不重拉.</summary>
        private void ReRenderGunsmith() => BuildGunsmithPage();

        private async Task SaveGunsmithAsync()
        {
            var selections = GunsmithSlots.Where(gunsmithSelections.ContainsKey)
                .Select(slot => new AttachmentSelectionRequest { attachmentSlot = slot, attachmentItemId = gunsmithSelections[slot] })
                .ToList();
#if UNITY_EDITOR
            SaveAttachmentCalibration();     // 位置校准与装配同一个入口（Scene 微调即被捕获）；方法体读编辑器预览实例，仅编辑器可编译
#endif
            await SaveAttachmentsAsync(gunsmithLoadoutVersion, selections);
            // 保存成功后刷新版本号（SaveAttachmentsAsync 只在成功路径更新状态文本，这里补拉取）
            var loadout = await api.GetLoadoutAttachmentsAsync(pageCts.Token);
            if (loadout.Success) gunsmithLoadoutVersion = loadout.Data.version;
        }

#if UNITY_EDITOR
        /// <summary>
        /// 保存配件贴合校准：读取预览实例中每个已装配件的当前局部位姿，
        /// 与目录 mountOffset/mountEuler 的差值写入 AttachmentCalibration 资产 —— 永久生效：
        /// 枪匠页重建预览、局内 FP/TP 视图全部自动应用。由「保 存」按钮统一调用。
        /// </summary>
        private void SaveAttachmentCalibration()
        {
            var view = gunsmithPreview != null && gunsmithPreview.ModelInstance != null
                ? gunsmithPreview.ModelInstance.GetComponent<Game.Gameplay.Weapon.WeaponAttachmentView>()
                : null;
            if (view == null || view.Spawned.Count == 0) return;
            var attachmentCatalog = Game.Gameplay.Weapon.AttachmentAssetCatalog.LoadOrDefault();
            var calibration = attachmentCatalog.Calibration;
            if (calibration == null) return;

            foreach (var att in view.Spawned)
            {
                if (att == null) continue;
                var attachmentId = att.name.StartsWith("Att_") ? att.name.Substring(4) : att.name;
                if (!attachmentCatalog.TryGet(attachmentId, out var entry)) continue;
                var posOffset = att.transform.localPosition - entry.mountOffset;
                // ApplyAttachments left-multiplies: local = Mount * D, so the inverse must PRE-multiply
                // (quaternions are non-commutative; post-multiply silently skews any mountEuler != 0).
                var rotDelta = Quaternion.Inverse(entry.MountRotation) * att.transform.localRotation;
                // 作者帧（2026-09-05 激光错位修正）：保存时挂点相对视图根的朝向——
                // FP/TP/预览挂点局部朝向不同，应用侧据此把 delta 换算到当前挂点帧
                var authorFrame = Quaternion.Inverse(view.transform.rotation) * att.transform.parent.rotation;
                calibration.Set(selectedWeapon.itemId, attachmentId, posOffset, rotDelta.eulerAngles, authorFrame);
            }
            UnityEditor.EditorUtility.SetDirty(calibration);
            UnityEditor.AssetDatabase.SaveAssets();
        }
#endif

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
            var minus = UIComponents.Button("Minus", row.transform, "?", UIComponents.ButtonKind.Danger,
                new Vector2(0.74f, 0.12f), new Vector2(0.85f, 0.88f));
            minus.onClick.AddListener(() => { current = Mathf.Max(0, current - 1); Refresh(); });
            var plus = UIComponents.Button("Plus", row.transform, "+", UIComponents.ButtonKind.Primary,
                new Vector2(0.88f, 0.12f), new Vector2(0.99f, 0.88f));
            plus.onClick.AddListener(() => { current = Mathf.Min(5, current + 1); Refresh(); });
            return () => current;
        }

        // ---------- Settings (音量 / 键位 / 画质) ----------
        // 共享设置 Phase B 重构：数据与应用逻辑全部走 Gameplay.Settings 共享服务
        // （SettingsDraft 草稿 + SettingsRuntime 实时层 + KeybindRules 冲突规则），
        // 本页只负责构建与绑定——大厅与 Arena 游戏菜单共用同一套持久值与回滚语义。

        private SettingsDraft _settingsDraft;
        private SettingsKeyMap.Binding _pendingRebindBinding;
        private Key _pendingRebindCandidate;
        private SettingsKeyMap.Action? _pendingRebindConflict;
        private readonly System.Collections.Generic.Dictionary<SettingsKeyMap.Action, Button> _keyButtons = new();

        private SettingsDraft DraftOrNull => _settingsDraft ??= SettingsDraft.CaptureFromCurrent();

        private void RenderSettings()
        {
            _settingsDraft = SettingsDraft.CaptureFromCurrent(); // 进入设置页 = 草稿捕获点（取消回滚基准）
            _pendingRebindBinding = null;
            _pendingRebindConflict = null;
            _keyButtons.Clear();
            SetBackground(UIArt.KeyBackgroundLobby);
            var root = PageRoot("SettingsPage");
            var page = root.transform;
            StyledText(page, "设置", UITheme.FontPageTitle, UITheme.TextPrimary,
                new Vector2(0.02f, 0.90f), new Vector2(0.6f, 0.99f), TextAlignmentOptions.Left, FontStyles.Bold).name = "PageTitle";

            RenderAudioCard(page, new Vector2(0.02f, 0.16f), new Vector2(0.335f, 0.86f));
            RenderKeybindCard(page, new Vector2(0.345f, 0.16f), new Vector2(0.665f, 0.86f));
            RenderGraphicsCard(page, new Vector2(0.675f, 0.16f), new Vector2(0.99f, 0.86f));

            StyledButton(page, "应用并保存", UIComponents.ButtonKind.Primary,
                new Vector2(0.02f, 0.02f), new Vector2(0.20f, 0.12f), () =>
                {
                    _settingsDraft.ApplyAndPersist(); // 应用才写 PlayerPrefs 并统一 ApplyAll
                    _settingsDraft = null;
                    status.text = "设置已保存并应用";
                    RenderSettings();
                });
            StyledButton(page, "取消（回滚）", UIComponents.ButtonKind.Secondary,
                new Vector2(0.21f, 0.02f), new Vector2(0.37f, 0.12f), () =>
                {
                    _settingsDraft.RestoreLive(); // 回滚即时预览（含音量/灵敏度/键位）
                    _settingsDraft = null;
                    status.text = "已恢复进入设置页前的状态";
                    RenderSettings();
                });
            StyledButton(page, "恢复默认", UIComponents.ButtonKind.Info,
                new Vector2(0.38f, 0.02f), new Vector2(0.52f, 0.12f), () =>
                {
                    _settingsDraft.ResetToDefaults();
                    _settingsDraft.PreviewAllLive(); // 即时预览出厂值（应用前不落盘）
                    RenderSettings();
                });
            PlayEnter(root.gameObject);
        }

        private void RenderAudioCard(Transform page, Vector2 min, Vector2 max)
        {
            var card = StyledPanel("AudioCard", page, UITheme.CardSurface, min, max);
            StyledText(card.transform, "音量与灵敏度", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.90f), new Vector2(0.92f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            var draft = DraftOrNull;

            var master = UIComponents.SliderRow("MasterSlider", card.transform, new Vector2(0.08f, 0.68f), new Vector2(0.92f, 0.78f), draft.MasterVolume);
            master.onValueChanged.AddListener(v => { draft.MasterVolume = v; SettingsRuntime.SetLive(SensitivityTarget.Master, v); });
            StyledText(card.transform, "主音量", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.08f, 0.78f), new Vector2(0.92f, 0.84f), TextAlignmentOptions.Left);

            var music = UIComponents.SliderRow("MusicSlider", card.transform, new Vector2(0.08f, 0.48f), new Vector2(0.92f, 0.58f), draft.MusicVolume);
            music.onValueChanged.AddListener(v => { draft.MusicVolume = v; SettingsRuntime.SetLive(SensitivityTarget.Music, v); });
            StyledText(card.transform, "音乐音量", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.08f, 0.58f), new Vector2(0.92f, 0.64f), TextAlignmentOptions.Left);

            var sfx = UIComponents.SliderRow("SfxSlider", card.transform, new Vector2(0.08f, 0.28f), new Vector2(0.92f, 0.38f), draft.SfxVolume);
            sfx.onValueChanged.AddListener(v => { draft.SfxVolume = v; SettingsRuntime.SetLive(SensitivityTarget.Sfx, v); });
            StyledText(card.transform, "音效音量", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.08f, 0.38f), new Vector2(0.92f, 0.44f), TextAlignmentOptions.Left);

            var sens = UIComponents.SliderRow("SensitivitySlider", card.transform, new Vector2(0.08f, 0.08f), new Vector2(0.92f, 0.18f),
                Mathf.InverseLerp(0.1f, 5f, draft.Sensitivity));
            sens.onValueChanged.AddListener(v => { draft.Sensitivity = Mathf.Lerp(0.1f, 5f, v); SettingsRuntime.SetLive(SensitivityTarget.Sensitivity, draft.Sensitivity); });
            StyledText(card.transform, "鼠标灵敏度（拖动即时预览）", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.08f, 0.18f), new Vector2(0.92f, 0.24f), TextAlignmentOptions.Left);
        }

        private void RenderKeybindCard(Transform page, Vector2 min, Vector2 max)
        {
            var card = StyledPanel("KeybindCard", page, UITheme.CardSurface, min, max);
            StyledText(card.transform, "键位", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.90f), new Vector2(0.92f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(card.transform, "点击「重设」后按新键；Esc 取消；Esc 为系统菜单键不可绑定", UITheme.FontCaption, UITheme.TextMuted,
                new Vector2(0.08f, 0.855f), new Vector2(0.95f, 0.90f), TextAlignmentOptions.Left);

            // 可滚动键位列表（行高固定像素；绑定项增多时滚动，不再溢出卡片）
            var viewport = new GameObject("KeyList", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
            viewport.transform.SetParent(card.transform, false);
            var vpRect = viewport.GetComponent<RectTransform>();
            vpRect.anchorMin = new Vector2(0.04f, 0.04f);
            vpRect.anchorMax = new Vector2(0.97f, 0.845f);
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = Vector2.zero;
            viewport.GetComponent<Image>().color = Color.clear;
            viewport.GetComponent<Image>().raycastTarget = true;

            var bindings = SettingsKeyMap.Bindings;
            const float rowPixels = 58f;
            var content = new GameObject("KeyContent", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(0f, bindings.Length * rowPixels);
            contentRect.anchoredPosition = Vector2.zero;

            for (var i = 0; i < bindings.Length; i++)
            {
                var b = bindings[i];
                var rowNormMin = 1f - (float)(i + 1) / bindings.Length;
                var rowNormMax = 1f - (float)i / bindings.Length;
                var row = new GameObject("KeyRow_" + b.action, typeof(RectTransform), typeof(Image));
                row.transform.SetParent(content.transform, false);
                var rowRect = row.GetComponent<RectTransform>();
                rowRect.anchorMin = new Vector2(0f, rowNormMin);
                rowRect.anchorMax = new Vector2(1f, rowNormMax);
                rowRect.offsetMin = new Vector2(2f, 2f);
                rowRect.offsetMax = new Vector2(-2f, -2f);
                row.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.03f);

                StyledText(row.transform, b.label, UITheme.FontCaption + 1, UITheme.TextPrimary,
                    new Vector2(0.04f, 0f), new Vector2(0.44f, 1f), TextAlignmentOptions.MidlineLeft);
                var captured = b;
                var keyBtn = UIComponents.Button("Key_" + b.action, row.transform,
                    SettingsKeyMap.DisplayName(SettingsKeyMap.Get(b.action)),
                    UIComponents.ButtonKind.Secondary, new Vector2(0.46f, 0.10f), new Vector2(0.74f, 0.90f));
                keyBtn.onClick.AddListener(() => StartRebind(captured, keyBtn));
                _keyButtons[captured.action] = keyBtn;
                UIComponents.Button("Reset_" + b.action, row.transform, "默认", UIComponents.ButtonKind.Info,
                    new Vector2(0.77f, 0.10f), new Vector2(0.96f, 0.90f))
                    .onClick.AddListener(() =>
                    {
                        DraftOrNull.PreviewKey(captured.action, captured.defaultKey); // 单项恢复默认（草稿预览）
                        RefreshKeybindButton(keyBtn, captured);
                        status.text = captured.label + " 已恢复默认（应用后保存）";
                    });
            }

            var scroll = card.AddComponent<ScrollRect>();
            scroll.viewport = vpRect;
            scroll.content = contentRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 20f;
            var wheel = card.AddComponent<MouseWheelScroll>();
            wheel.target = scroll;
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
            _pendingRebindBinding = binding;
            StartCoroutine(RebindRoutine(binding, keyBtn));
        }

        private System.Collections.IEnumerator RebindRoutine(SettingsKeyMap.Binding binding, Button keyBtn)
        {
            // 等一帧避免把触发本次重绑的按键也算进去
            yield return null;
            while (_pendingRebindBinding == binding)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) yield break;
                if (kb.escapeKey.wasPressedThisFrame)
                {
                    _pendingRebindBinding = null;
                    RefreshKeybindButton(keyBtn, binding);
                    status.text = "已取消重设";
                    yield break;
                }
                foreach (UnityEngine.InputSystem.Key key in System.Enum.GetValues(typeof(UnityEngine.InputSystem.Key)))
                {
                    if (key == UnityEngine.InputSystem.Key.None || key == UnityEngine.InputSystem.Key.Escape) continue; // Esc 保留为系统菜单键
                    var control = kb[key];
                    if (control == null || !control.wasPressedThisFrame) continue;

                    var outcome = KeybindRules.Evaluate(binding.action, key, out var conflicted);
                    if (outcome == RebindOutcome.Conflict)
                    {
                        _pendingRebindCandidate = key;
                        _pendingRebindConflict = conflicted;
                        ShowKeybindConflictDialog(binding, conflicted.Value);
                        yield break; // 弹窗按钮接管后续（交换/取消）
                    }
                    if (outcome == RebindOutcome.Available)
                    {
                        DraftOrNull.PreviewKey(binding.action, key); // 草稿即时预览；应用才落盘
                        RefreshKeybindButton(keyBtn, binding);
                        status.text = "「" + binding.label + "」已设为 " + SettingsKeyMap.DisplayName(key) + "（应用后保存）";
                    }
                    else
                    {
                        status.text = "该键为系统保留键，不能绑定";
                    }
                    _pendingRebindBinding = null;
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>重复绑定冲突弹窗（禁止静默冲突）：交换或取消。</summary>
        private void ShowKeybindConflictDialog(SettingsKeyMap.Binding binding, SettingsKeyMap.Action conflicted)
        {
            var conflictedBinding = SettingsKeyMap.Find(conflicted);
            var dialog = StyledPanel("ConflictDialog", body, UITheme.CardSurface, new Vector2(0.32f, 0.36f), new Vector2(0.68f, 0.64f));
            StyledText(dialog.transform, "键位冲突", UITheme.FontCardTitle + 2, UITheme.AccentDanger,
                new Vector2(0.08f, 0.72f), new Vector2(0.92f, 0.90f), TextAlignmentOptions.Left, FontStyles.Bold);
            StyledText(dialog.transform,
                "「" + SettingsKeyMap.DisplayName(_pendingRebindCandidate) + "」已被「" +
                (conflictedBinding != null ? conflictedBinding.label : conflicted.ToString()) + "」占用。\n选择「交换键位」互换两键，或取消本次重绑。",
                UITheme.FontBody, UITheme.TextMuted, new Vector2(0.08f, 0.34f), new Vector2(0.92f, 0.68f), TextAlignmentOptions.Left);
            StyledButton(dialog.transform, "交换键位", UIComponents.ButtonKind.Primary,
                new Vector2(0.08f, 0.08f), new Vector2(0.47f, 0.26f), () =>
                {
                    var original = SettingsKeyMap.Get(binding.action);
                    var draft = DraftOrNull;
                    KeybindRules.ApplyWithSwap(binding.action, _pendingRebindCandidate, persist: false);
                    draft.Keys[binding.action] = _pendingRebindCandidate;
                    draft.Keys[conflicted] = original;
                    _pendingRebindConflict = null;
                    RefreshAllKeybindButtons();
                    status.text = "已交换键位（应用后保存）";
                });
            StyledButton(dialog.transform, "取消", UIComponents.ButtonKind.Secondary,
                new Vector2(0.53f, 0.08f), new Vector2(0.92f, 0.26f), () =>
                {
                    _pendingRebindConflict = null;
                    RefreshAllKeybindButtons();
                    status.text = "已取消重设";
                });
        }

        private void RefreshAllKeybindButtons()
        {
            foreach (var kv in _keyButtons)
                RefreshKeybindButton(kv.Value, SettingsKeyMap.Find(kv.Key));
            _pendingRebindBinding = null;
        }

        private void RenderGraphicsCard(Transform page, Vector2 min, Vector2 max)
        {
            var card = StyledPanel("GraphicsCard", page, UITheme.CardSurface, min, max);
            StyledText(card.transform, "画质", UITheme.FontCardTitle, UITheme.TextPrimary,
                new Vector2(0.08f, 0.90f), new Vector2(0.92f, 0.98f), TextAlignmentOptions.Left, FontStyles.Bold);
            var draft = DraftOrNull;

            // 分辨率（应用后才应用；编辑器只保存不切分辨率——SettingsModel.ApplyResolution 内建判定）
            StyledText(card.transform, "分辨率", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.74f), new Vector2(0.92f, 0.82f), TextAlignmentOptions.Left);
            UIComponents.Stepper("ResolutionStepper", card.transform, new Vector2(0.08f, 0.62f), new Vector2(0.92f, 0.72f),
                out var resPrev, out var resNext, out var resLabel);
            var resolutions = SettingsModel.SupportedResolutions;
            var resIndex = System.Math.Max(0, System.Array.IndexOf(resolutions, draft.Resolution));
            void RefreshRes()
            {
                draft.Resolution = resolutions[resIndex];
                resLabel.text = SettingsModel.FormatResolution(resolutions[resIndex]);
            }
            resPrev.onClick.AddListener(() => { resIndex = (resIndex - 1 + resolutions.Length) % resolutions.Length; RefreshRes(); });
            resNext.onClick.AddListener(() => { resIndex = (resIndex + 1) % resolutions.Length; RefreshRes(); });
            RefreshRes();

            // 全屏开关
            var fsBtn = UIComponents.Button("FullscreenToggle", card.transform,
                draft.Fullscreen ? "全屏：开" : "全屏：关", UIComponents.ButtonKind.Info,
                new Vector2(0.08f, 0.50f), new Vector2(0.92f, 0.60f));
            fsBtn.onClick.AddListener(() =>
            {
                draft.Fullscreen = !draft.Fullscreen;
                var l = fsBtn.GetComponentInChildren<TMP_Text>();
                if (l != null) l.text = draft.Fullscreen ? "全屏：开" : "全屏：关";
            });

            // 帧率上限（应用后经 ApplyAll 生效）
            StyledText(card.transform, "帧率上限", UITheme.FontBody, UITheme.TextMuted,
                new Vector2(0.08f, 0.40f), new Vector2(0.92f, 0.48f), TextAlignmentOptions.Left);
            UIComponents.Stepper("FrameCapStepper", card.transform, new Vector2(0.08f, 0.28f), new Vector2(0.92f, 0.38f),
                out var capPrev, out var capNext, out var capLabel);
            var caps = SettingsModel.FrameCapOptions;
            var capIndex = System.Math.Max(0, System.Array.IndexOf(caps, draft.FrameCap));
            void RefreshCap()
            {
                draft.FrameCap = caps[capIndex];
                capLabel.text = SettingsModel.FormatFrameCap(caps[capIndex]);
            }
            capPrev.onClick.AddListener(() => { capIndex = (capIndex - 1 + caps.Length) % caps.Length; RefreshCap(); });
            capNext.onClick.AddListener(() => { capIndex = (capIndex + 1) % caps.Length; RefreshCap(); });
            RefreshCap();

            // 开镜方式：长按（按住右键）/ 切换（点按右键开收镜）
            var adsBtn = UIComponents.Button("AdsModeToggle", card.transform,
                "开镜方式：" + (draft.AdsToggleMode ? "切换" : "长按"), UIComponents.ButtonKind.Info,
                new Vector2(0.08f, 0.14f), new Vector2(0.92f, 0.24f));
            adsBtn.onClick.AddListener(() =>
            {
                draft.AdsToggleMode = !draft.AdsToggleMode;
                AdsInputMode.Toggle = draft.AdsToggleMode; // 即时生效；取消时 RestoreLive 回写捕获值
                var l = adsBtn.GetComponentInChildren<TMP_Text>();
                if (l != null) l.text = "开镜方式：" + (draft.AdsToggleMode ? "切换" : "长按");
                status.text = "开镜方式已设为「" + (draft.AdsToggleMode ? "切换" : "长按") + "」（应用后保存）";
            });
        }
    }
}
